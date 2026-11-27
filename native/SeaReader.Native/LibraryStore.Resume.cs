namespace SeaReader.Native;

internal readonly record struct BookResumePosition(string? Position, int? Percent);

public sealed partial class LibraryStore
{
    private static void InitializeResumeTable(LibraryDatabase db) => db.ExecuteScript("""
        CREATE TABLE IF NOT EXISTS native_book_resume_progress (
            book_id TEXT PRIMARY KEY NOT NULL REFERENCES books(id) ON DELETE CASCADE,
            position TEXT NOT NULL, percent INTEGER NOT NULL CHECK (typeof(percent)='integer' AND percent BETWEEN 0 AND 100));
        """);

    public Task SavePositionAsync(string id, string position) => SavePositionAsync(id, position, percent: null);

    internal Task SavePositionAsync(string id, string position, int? percent)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        return Query(db =>
        {
            db.ExecuteScript("BEGIN IMMEDIATE;");
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

            if (percent is int progress)
                db.Execute("""
                    INSERT INTO native_book_resume_progress(book_id, position, percent) VALUES (?, ?, ?)
                    ON CONFLICT(book_id) DO UPDATE SET position=excluded.position, percent=excluded.percent
                    """, id, position, (long)progress);
            else
                db.Execute("DELETE FROM native_book_resume_progress WHERE book_id=? AND position<>?", id, position);
            db.ExecuteScript("COMMIT;");
        });
    }

    internal Task<IReadOnlyDictionary<string, BookResumePosition>> GetResumePositionsAsync(IReadOnlyList<string> ids)
    {
        var requested = ids.Distinct(StringComparer.Ordinal).ToArray();
        var positions = new Dictionary<string, BookResumePosition>(requested.Length, StringComparer.Ordinal);
        if (requested.Length == 0) return Task.FromResult<IReadOnlyDictionary<string, BookResumePosition>>(positions);

        return Query<IReadOnlyDictionary<string, BookResumePosition>>(db =>
        {
            // Keep a single read snapshot and stay within SQLite's variable limit for larger requests.
            db.ExecuteScript("BEGIN;");
            foreach (var batch in requested.Chunk(500))
            {
                var parameters = string.Join(", ", Enumerable.Repeat("?", batch.Length));
                var entries = db.Read($"""
                    WITH locations AS (
                        SELECT books.id, COALESCE(native_epub_positions.position, reading_positions.position) AS position
                        FROM books
                        LEFT JOIN native_epub_positions ON native_epub_positions.book_id=books.id
                        LEFT JOIN reading_positions ON reading_positions.book_id=books.id
                        WHERE books.id IN ({parameters})
                    )
                    SELECT locations.id, locations.position, progress.percent, locations.position IS NOT NULL
                    FROM locations
                    LEFT JOIN native_book_resume_progress AS progress
                        ON progress.book_id=locations.id AND progress.position=locations.position
                    """, row => (Id: row.Text(0), Resume: new BookResumePosition(row.Integer(3) != 0 ? row.Text(1) : null,
                        (int?)row.NullableInteger(2))), batch);
                foreach (var entry in entries) positions.Add(entry.Id, entry.Resume);
            }
            db.ExecuteScript("COMMIT;");
            return positions;
        });
    }
}
