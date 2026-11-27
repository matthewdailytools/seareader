using System.Globalization;

namespace SeaReader.Native;

public sealed partial class LibraryStore
{
    private const string CollectionShelfPrefix = "collection:";

    private static void InitializeManualOrderTable(LibraryDatabase db) => db.ExecuteScript("""
        CREATE TABLE IF NOT EXISTS native_library_manual_order (
            shelf TEXT NOT NULL,
            book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
            rank INTEGER NOT NULL CHECK (rank>=0),
            collection_id INTEGER,
            PRIMARY KEY (shelf, book_id),
            UNIQUE (shelf, rank),
            FOREIGN KEY (collection_id, book_id)
                REFERENCES native_library_collection_books(collection_id, book_id) ON DELETE CASCADE,
            CHECK ((collection_id IS NULL AND shelf NOT LIKE 'collection:%')
                OR (collection_id IS NOT NULL AND shelf='collection:' || collection_id)));
        CREATE INDEX IF NOT EXISTS native_library_manual_order_by_book ON native_library_manual_order(book_id);
        """);

    internal Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetLibraryManualOrdersAsync() =>
        Query<IReadOnlyDictionary<string, IReadOnlyList<string>>>(db => db.Read("""
            SELECT manual.shelf, manual.book_id FROM native_library_manual_order AS manual
            INNER JOIN books ON books.id=manual.book_id
            LEFT JOIN native_library_reading_states AS reading ON reading.book_id=manual.book_id
            WHERE manual.collection_id IS NOT NULL OR manual.shelf='all'
                OR (manual.shelf='want-to-read' AND reading.state=1)
                OR (manual.shelf='finished' AND reading.state=2)
                OR (manual.shelf NOT IN ('all', 'want-to-read', 'finished') AND books.format=manual.shelf COLLATE NOCASE)
            ORDER BY manual.shelf, manual.rank
            """, row => (Shelf: row.Text(0), BookId: row.Text(1)))
            .GroupBy(item => item.Shelf, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(item => item.BookId).ToArray(),
                StringComparer.Ordinal));

    internal Task<bool> MoveLibraryBookAsync(string shelf, string bookId, string anchorId, bool after)
    {
        var scope = ParseManualShelf(shelf);
        if (scope == null || string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(anchorId) || bookId == anchorId)
            return Task.FromResult(false);

        var (key, collectionId) = scope.Value;
        return Query(db =>
        {
            db.ExecuteScript("BEGIN IMMEDIATE;");
            var eligible = ReadBooksInManualShelf(db, key, collectionId);
            var unranked = eligible.ToHashSet(StringComparer.Ordinal);
            var order = db.Read("SELECT book_id FROM native_library_manual_order WHERE shelf=? ORDER BY rank",
                row => row.Text(0), key).Where(unranked.Remove).ToList();
            order.AddRange(eligible.Where(unranked.Contains));
            int from = order.IndexOf(bookId);
            int anchor = order.IndexOf(anchorId);
            int target = anchor + (after ? 1 : 0);
            if (from < target) target--;
            bool moved = from >= 0 && anchor >= 0 && from != target;
            if (moved)
            {
                order.RemoveAt(from);
                order.Insert(target, bookId);
                db.Execute("DELETE FROM native_library_manual_order WHERE shelf=?", key);
                for (int rank = 0; rank < order.Count; rank++)
                    db.Execute("""
                        INSERT INTO native_library_manual_order(shelf, book_id, rank, collection_id)
                        VALUES (?, ?, ?, NULLIF(?, 0))
                        """, key, order[rank], (long)rank, collectionId);
            }
            db.ExecuteScript("COMMIT;");
            return moved;
        });
    }

    private static (string Key, long CollectionId)? ParseManualShelf(string shelf)
    {
        if (string.IsNullOrEmpty(shelf)) return null;
        shelf = shelf.ToLowerInvariant();
        if (shelf.StartsWith(CollectionShelfPrefix, StringComparison.Ordinal))
        {
            if (!long.TryParse(shelf.AsSpan(CollectionShelfPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                    out long collectionId) || collectionId <= 0) return null;
            return (CollectionShelfPrefix + collectionId.ToString(CultureInfo.InvariantCulture), collectionId);
        }
        if (shelf is "all" or "want-to-read" or "finished" or "txt" or "pdf" || BookFormats.IsChapterBook(shelf))
            return (shelf, 0);
        return null;
    }

    private static List<string> ReadBooksInManualShelf(LibraryDatabase db, string key, long collectionId)
    {
        if (collectionId > 0)
            return db.Read("""
                SELECT books.id FROM books
                INNER JOIN native_library_collection_books AS members ON members.book_id=books.id
                WHERE members.collection_id=? ORDER BY books.imported_at, books.id
                """, row => row.Text(0), collectionId);

        if (key == "all")
            return db.Read("SELECT id FROM books ORDER BY imported_at, id", row => row.Text(0));

        if (key is "want-to-read" or "finished")
        {
            var state = key == "want-to-read" ? LibraryReadingState.WantToRead : LibraryReadingState.Finished;
            return db.Read("""
                SELECT books.id FROM books INNER JOIN native_library_reading_states AS reading ON reading.book_id=books.id
                WHERE reading.state=? ORDER BY books.imported_at, books.id
                """, row => row.Text(0), (long)state);
        }

        return db.Read("SELECT id FROM books WHERE format=? COLLATE NOCASE ORDER BY imported_at, id",
            row => row.Text(0), key);
    }
}
