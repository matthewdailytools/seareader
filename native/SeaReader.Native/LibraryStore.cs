using System.Buffers;
using System.IO;
using System.Security.Cryptography;

namespace SeaReader.Native;

public sealed record BookEntry(string Id, string Title, string Format, long? OpenedAt);
public sealed record BookmarkEntry(long Id, string Position, long CreatedAt);
public sealed record AnnotationEntry(long Id, string Position, string Quotation, string Note, long UpdatedAt);

public sealed class LibraryStore(string directory)
{
    internal string DataDirectory => directory;
    private string DatabasePath => Path.Combine(directory, "library.sqlite");
    private string BookDirectory => Path.Combine(directory, "books");

    public Task InitializeAsync() => Task.Run(() =>
    {
        Directory.CreateDirectory(BookDirectory);
        using var database = new LibraryDatabase(DatabasePath);
        database.ExecuteScript("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS books (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, format TEXT NOT NULL, imported_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS book_activity (
                book_id TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE, opened_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS reading_positions (
                book_id TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE, position TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS native_epub_positions (
                book_id TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE, position TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS application_preferences (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS bookmarks (
                id INTEGER PRIMARY KEY, book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                position TEXT NOT NULL, created_at INTEGER NOT NULL, UNIQUE (book_id, position));
            CREATE TABLE IF NOT EXISTS annotations (
                id INTEGER PRIMARY KEY, book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                position TEXT NOT NULL, quotation TEXT NOT NULL, note TEXT NOT NULL,
                updated_at INTEGER NOT NULL, UNIQUE (book_id, position));
            CREATE TABLE IF NOT EXISTS recognized_pages (
                book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                page INTEGER NOT NULL, text TEXT NOT NULL, PRIMARY KEY (book_id, page));
            """);
    });

    public Task<IReadOnlyList<BookEntry>> ListAsync() => Query<IReadOnlyList<BookEntry>>(db =>
        db.Read("""
            SELECT id, title, format, opened_at FROM books
            LEFT JOIN book_activity ON book_activity.book_id=books.id
            ORDER BY opened_at DESC, imported_at DESC, id
            """, Book));

    public async Task<BookEntry> ImportAsync(string path, CancellationToken ct = default)
    {
        string temporaryPath = Path.Combine(BookDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var (id, format) = await CopyAndHashAsync(path, temporaryPath, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            string finalPath = BookPath(id);
            try { File.Move(temporaryPath, finalPath); }
            catch (IOException) when (File.Exists(finalPath)) { }

            return await Query(db =>
            {
                db.Execute("""
                    INSERT INTO books(id, title, format, imported_at) VALUES (?, ?, ?, ?)
                    ON CONFLICT(id) DO UPDATE SET format=excluded.format WHERE books.format<>excluded.format
                    """, id, Path.GetFileNameWithoutExtension(path), format, Now());
                return db.Read("""
                    SELECT id, title, format, opened_at FROM books
                    LEFT JOIN book_activity ON book_activity.book_id=books.id WHERE id=?
                    """, Book, id).Single();
            }).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public Task<string> OpenAsync(string id) => Query(db =>
    {
        string path = BookPath(id);
        if (!File.Exists(path) || db.Read("SELECT id FROM books WHERE id=?", row => row.Text(0), id).Count == 0)
            throw new FileNotFoundException("无法找到该书的本地文件。", path);
        db.Execute("""
            INSERT INTO book_activity(book_id, opened_at) VALUES (?, ?)
            ON CONFLICT(book_id) DO UPDATE SET opened_at=excluded.opened_at
            """, id, Now());
        return path;
    });

    public Task<string?> GetPositionAsync(string id) => Query(db =>
        db.Read("""
            SELECT position FROM native_epub_positions WHERE book_id=?
            UNION ALL SELECT position FROM reading_positions WHERE book_id=?
            LIMIT 1
            """, row => row.Text(0), id, id).FirstOrDefault());

    public Task SavePositionAsync(string id, string position) => Query(db =>
    {
        bool epub = db.Read("SELECT format FROM books WHERE id=?", row => row.Text(0), id).Single() == "epub";
        if (epub)
            db.Execute("""
                INSERT INTO native_epub_positions(book_id, position) VALUES (?, ?)
                ON CONFLICT(book_id) DO UPDATE SET position=excluded.position
                """, id, position);
        else
            db.Execute("""
                INSERT INTO reading_positions(book_id, position) VALUES (?, ?)
                ON CONFLICT(book_id) DO UPDATE SET position=excluded.position
                """, id, position);
    });

    public Task<IReadOnlyList<BookmarkEntry>> ListBookmarksAsync(string id) => Query<IReadOnlyList<BookmarkEntry>>(db =>
        db.Read("SELECT id, position, created_at FROM bookmarks WHERE book_id=? ORDER BY created_at DESC, id DESC",
            row => new BookmarkEntry(row.Integer(0), row.Text(1), row.Integer(2)), id));

    public Task AddBookmarkAsync(string id, string position) => Query(db =>
        db.Execute("INSERT OR IGNORE INTO bookmarks(book_id, position, created_at) VALUES (?, ?, ?)", id, position, Now()));

    public Task RemoveBookmarkAsync(string id, long bookmark) => Query(db =>
        db.Execute("DELETE FROM bookmarks WHERE book_id=? AND id=?", id, bookmark));

    public Task<IReadOnlyList<AnnotationEntry>> ListAnnotationsAsync(string id) => Query<IReadOnlyList<AnnotationEntry>>(db =>
        db.Read("""
            SELECT id, position, quotation, note, updated_at FROM annotations
            WHERE book_id=? ORDER BY updated_at DESC, id DESC
            """, row => new AnnotationEntry(row.Integer(0), row.Text(1), row.Text(2), row.Text(3), row.Integer(4)), id));

    public Task SaveAnnotationAsync(string id, string position, string quotation, string note) => Query(db =>
        db.Execute("""
            INSERT INTO annotations(book_id, position, quotation, note, updated_at) VALUES (?, ?, ?, ?, ?)
            ON CONFLICT(book_id, position) DO UPDATE SET
                quotation=excluded.quotation, note=excluded.note, updated_at=excluded.updated_at
            """, id, position, quotation, note, Now()));

    public Task RemoveAnnotationAsync(string id, long annotation) => Query(db =>
        db.Execute("DELETE FROM annotations WHERE book_id=? AND id=?", id, annotation));

    public Task<string?> GetRecognizedPageAsync(string id, int page) => Query(db =>
        db.Read("SELECT text FROM recognized_pages WHERE book_id=? AND page=?",
            row => row.Text(0), id, (long)page).FirstOrDefault());

    public Task<IReadOnlyDictionary<int, string>> ListRecognizedPagesAsync(string id) =>
        Query<IReadOnlyDictionary<int, string>>(db =>
            db.Read("SELECT page, text FROM recognized_pages WHERE book_id=? ORDER BY page",
                row => (Page: (int)row.Integer(0), Text: row.Text(1)), id)
              .ToDictionary(item => item.Page, item => item.Text));

    public Task SaveRecognizedPageAsync(string id, int page, string text)
    {
        if (page < 1 || text.Length > 250000)
            throw new ArgumentException("页码或识别文本无效。");
        return Query(db =>
        {
            if (string.IsNullOrWhiteSpace(text))
                db.Execute("DELETE FROM recognized_pages WHERE book_id=? AND page=?", id, (long)page);
            else
                db.Execute("""
                    INSERT INTO recognized_pages(book_id, page, text) VALUES (?, ?, ?)
                    ON CONFLICT(book_id,page) DO UPDATE SET text=excluded.text
                    """, id, (long)page, text);
        });
    }

    public Task<string?> GetPreferenceAsync(string name) => Query(db =>
        db.Read("SELECT value FROM application_preferences WHERE key=?", row => row.Text(0), name).FirstOrDefault());

    public Task SavePreferenceAsync(string name, string? value) => Query(db =>
    {
        if (value is null) db.Execute("DELETE FROM application_preferences WHERE key=?", name);
        else db.Execute("""
            INSERT INTO application_preferences(key,value) VALUES (?, ?)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """, name, value);
    });

    private Task<T> Query<T>(Func<LibraryDatabase, T> operation) => Task.Run(() =>
    {
        using var database = new LibraryDatabase(DatabasePath);
        return operation(database);
    });

    private Task Query(Action<LibraryDatabase> operation) => Query(db => { operation(db); return true; });
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static BookEntry Book(LibraryRow row) => new(row.Text(0), row.Text(1), row.Text(2), row.NullableInteger(3));

    private string BookPath(string id)
    {
        if (id.Length != 64 || id.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("无效的书籍 ID。");
        return Path.Combine(BookDirectory, id);
    }

    private static async Task<(string Id, string Format)> CopyAndHashAsync(string source, string target, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        string format = await BookFormats.DetectAsync(input, ct).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            return (Convert.ToHexStringLower(hash.GetHashAndReset()), format);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
