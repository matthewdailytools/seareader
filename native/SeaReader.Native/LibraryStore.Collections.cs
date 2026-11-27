using System.Text;

namespace SeaReader.Native;

internal enum LibraryReadingState
{
    None = 0,
    WantToRead = 1,
    Finished = 2
}

internal sealed record LibraryCollection(long Id, string Name);

internal sealed record LibraryOrganization(
    IReadOnlyList<LibraryCollection> Collections,
    IReadOnlyDictionary<long, IReadOnlySet<string>> Members,
    IReadOnlyDictionary<string, LibraryReadingState> ReadingStates);

public sealed partial class LibraryStore
{
    internal const int MaxCollectionNameLength = 80;

    private static void InitializeOrganizationTables(LibraryDatabase db) => db.ExecuteScript("""
        CREATE TABLE IF NOT EXISTS native_library_collections (
            id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE);
        CREATE TABLE IF NOT EXISTS native_library_collection_books (
            collection_id INTEGER NOT NULL REFERENCES native_library_collections(id) ON DELETE CASCADE,
            book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
            PRIMARY KEY (collection_id, book_id));
        CREATE INDEX IF NOT EXISTS native_library_collection_books_by_book ON native_library_collection_books(book_id);
        CREATE TABLE IF NOT EXISTS native_library_reading_states (
            book_id TEXT PRIMARY KEY NOT NULL REFERENCES books(id) ON DELETE CASCADE,
            state INTEGER NOT NULL CHECK (state IN (1, 2)));
        """);

    internal Task<LibraryOrganization> GetLibraryOrganizationAsync() => Query(db =>
    {
        db.ExecuteScript("BEGIN;");
        var collections = db.Read("SELECT id, name FROM native_library_collections ORDER BY name_key, id",
            row => new LibraryCollection(row.Integer(0), row.Text(1)));
        var members = collections.ToDictionary(collection => collection.Id, _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var (collectionId, bookId) in db.Read("SELECT collection_id, book_id FROM native_library_collection_books",
                     row => (row.Integer(0), row.Text(1))))
            members[collectionId].Add(bookId);
        var states = db.Read("SELECT book_id, state FROM native_library_reading_states",
            row => (BookId: row.Text(0), State: (LibraryReadingState)row.Integer(1)))
            .ToDictionary(item => item.BookId, item => item.State, StringComparer.Ordinal);
        db.ExecuteScript("COMMIT;");
        return new LibraryOrganization(collections, members.ToDictionary(item => item.Key, item => (IReadOnlySet<string>)item.Value), states);
    });

    internal Task<long> CreateCollectionAsync(string name)
    {
        var (display, key) = NormalizeCollectionName(name);
        return Query(db =>
        {
            db.Execute("""
                INSERT INTO native_library_collections(name, name_key) VALUES (?, ?)
                ON CONFLICT(name_key) DO NOTHING
                """, display, key);
            var id = db.Read("SELECT last_insert_rowid() WHERE changes()=1", row => row.Integer(0)).SingleOrDefault();
            if (id == 0) throw new InvalidOperationException("此收藏名称已存在。");
            return id;
        });
    }

    internal Task RenameCollectionAsync(long id, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        var (display, key) = NormalizeCollectionName(name);
        return Query(db =>
        {
            db.ExecuteScript("BEGIN IMMEDIATE;");
            EnsureCollectionExists(db, id);
            db.Execute("UPDATE OR IGNORE native_library_collections SET name=?, name_key=? WHERE id=?", display, key, id);
            if (db.Read("SELECT changes()", row => row.Integer(0)).Single() == 0)
                throw new InvalidOperationException("此收藏名称已存在。");
            db.ExecuteScript("COMMIT;");
        });
    }

    internal Task DeleteCollectionAsync(long id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return Query(db => db.Execute("DELETE FROM native_library_collections WHERE id=?", id));
    }

    internal Task SetCollectionMembershipAsync(long collectionId, string bookId, bool included)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bookId);
        return Query(db =>
        {
            if (!included)
            {
                db.Execute("DELETE FROM native_library_collection_books WHERE collection_id=? AND book_id=?", collectionId, bookId);
                return;
            }

            db.ExecuteScript("BEGIN IMMEDIATE;");
            EnsureCollectionExists(db, collectionId);
            EnsureBookExists(db, bookId);
            db.Execute("""
                INSERT INTO native_library_collection_books(collection_id, book_id) VALUES (?, ?)
                ON CONFLICT(collection_id, book_id) DO NOTHING
                """, collectionId, bookId);
            db.ExecuteScript("COMMIT;");
        });
    }

    internal Task SetReadingStateAsync(string bookId, LibraryReadingState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookId);
        if (state is not (LibraryReadingState.None or LibraryReadingState.WantToRead or LibraryReadingState.Finished))
            throw new ArgumentOutOfRangeException(nameof(state));
        var today = DateOnly.FromDateTime(DateTime.Now);
        return Query(db =>
        {
            db.ExecuteScript("BEGIN IMMEDIATE;");
            if (state == LibraryReadingState.None)
            {
                RecordReadingGoalFinish(db, bookId, state, today);
                db.Execute("DELETE FROM native_library_reading_states WHERE book_id=?", bookId);
                db.ExecuteScript("COMMIT;");
                return;
            }

            EnsureBookExists(db, bookId);
            RecordReadingGoalFinish(db, bookId, state, today);
            db.Execute("""
                INSERT INTO native_library_reading_states(book_id, state) VALUES (?, ?)
                ON CONFLICT(book_id) DO UPDATE SET state=excluded.state
                """, bookId, (long)state);
            db.ExecuteScript("COMMIT;");
        });
    }

    private static void EnsureCollectionExists(LibraryDatabase db, long id)
    {
        if (db.Read("SELECT 1 FROM native_library_collections WHERE id=?", row => row.Integer(0), id).Count == 0)
            throw new InvalidOperationException("找不到此收藏。");
    }

    private static void EnsureBookExists(LibraryDatabase db, string id)
    {
        if (db.Read("SELECT 1 FROM books WHERE id=?", row => row.Integer(0), id).Count == 0)
            throw new InvalidOperationException("找不到此书。");
    }

    private static (string Display, string Key) NormalizeCollectionName(string name)
    {
        var display = name?.Trim() ?? "";
        if (display.Length == 0 || display.Length > MaxCollectionNameLength || display.Any(char.IsControl))
            throw new ArgumentException($"收藏名称需为 1 至 {MaxCollectionNameLength} 个字符的单行文字。", nameof(name));
        var key = display.Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();
        return (display, key);
    }
}
