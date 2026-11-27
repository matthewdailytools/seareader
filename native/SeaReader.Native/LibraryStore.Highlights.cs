namespace SeaReader.Native;

public enum AnnotationHighlight
{
    None = 0,
    Amber = 1,
    Mint = 2,
    Lavender = 3,
    Underline = 4
}

public sealed partial class LibraryStore
{
    private static void InitializeHighlightTable(LibraryDatabase db) => db.ExecuteScript("""
        CREATE TABLE IF NOT EXISTS native_annotation_highlights (
            annotation_id INTEGER PRIMARY KEY REFERENCES annotations(id) ON DELETE CASCADE,
            style INTEGER NOT NULL CHECK (style BETWEEN 1 AND 4));
        """);

    internal Task SaveAnnotationHighlightAsync(string id, string position, string quotation, AnnotationHighlight highlight)
    {
        if (highlight is <= AnnotationHighlight.None or > AnnotationHighlight.Underline)
            throw new ArgumentOutOfRangeException(nameof(highlight));

        return Query(db =>
        {
            db.ExecuteScript("BEGIN IMMEDIATE;");
            db.Execute("""
                INSERT INTO annotations(book_id, position, quotation, note, updated_at) VALUES (?, ?, ?, '', ?)
                ON CONFLICT(book_id, position) DO UPDATE SET
                    quotation=CASE WHEN quotation='' THEN excluded.quotation ELSE quotation END,
                    updated_at=excluded.updated_at
                """, id, position, quotation, Now());
            db.Execute("""
                INSERT INTO native_annotation_highlights(annotation_id, style)
                SELECT id, ? FROM annotations WHERE book_id=? AND position=?
                ON CONFLICT(annotation_id) DO UPDATE SET style=excluded.style
                """, (long)highlight, id, position);
            db.ExecuteScript("COMMIT;");
        });
    }

    internal Task RemoveAnnotationHighlightAsync(string id, long annotation) => Query(db =>
    {
        db.ExecuteScript("BEGIN IMMEDIATE;");
        var note = db.Read("""
            SELECT note FROM annotations
            INNER JOIN native_annotation_highlights ON native_annotation_highlights.annotation_id=annotations.id
            WHERE book_id=? AND annotations.id=?
            """, row => row.Text(0), id, annotation).SingleOrDefault();
        if (note != null)
        {
            if (string.IsNullOrWhiteSpace(note))
                db.Execute("DELETE FROM annotations WHERE book_id=? AND id=?", id, annotation);
            else
            {
                db.Execute("DELETE FROM native_annotation_highlights WHERE annotation_id=?", annotation);
                db.Execute("UPDATE annotations SET updated_at=? WHERE book_id=? AND id=?", Now(), id, annotation);
            }
        }
        db.ExecuteScript("COMMIT;");
    });

    internal Task RemoveAnnotationNoteAsync(string id, long annotation) => Query(db =>
    {
        db.ExecuteScript("BEGIN IMMEDIATE;");
        db.Execute("""
            DELETE FROM annotations WHERE book_id=? AND id=?
            AND NOT EXISTS (SELECT 1 FROM native_annotation_highlights WHERE annotation_id=annotations.id)
            """, id, annotation);
        db.Execute("UPDATE annotations SET note='', updated_at=? WHERE book_id=? AND id=? AND note<>''",
            Now(), id, annotation);
        db.ExecuteScript("COMMIT;");
    });
}
