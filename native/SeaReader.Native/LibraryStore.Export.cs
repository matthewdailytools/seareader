namespace SeaReader.Native;

internal sealed record LibraryReadingData(
    string Schema,
    int Version,
    DateTimeOffset ExportedAt,
    IReadOnlyList<ReadingDataBook> Books,
    IReadOnlyList<ReadingDataPosition> ReadingPositions,
    IReadOnlyList<ReadingDataPosition> NativeEpubPositions,
    IReadOnlyList<ReadingDataBookmark> Bookmarks,
    IReadOnlyList<ReadingDataAnnotation> Annotations,
    IReadOnlyList<ReadingDataRecognizedPage> RecognizedPages,
    IReadOnlyList<LibraryCollection> Collections,
    IReadOnlyList<ReadingDataCollectionMembership> CollectionMemberships,
    IReadOnlyList<ReadingDataManualOrder> ManualOrder,
    ReadingDataGoals ReadingGoals,
    IReadOnlyDictionary<string, string> Preferences);

// ImportedAt, OpenedAt, CreatedAt and UpdatedAt are Unix timestamps in milliseconds.
internal sealed record ReadingDataBook(string Id, string OriginalTitle, string Title, string Author, string Format,
    long ImportedAt, long? OpenedAt, string ReadingState, bool ContinueHidden);
internal sealed record ReadingDataPosition(string BookId, string Position);
internal sealed record ReadingDataBookmark(long Id, string BookId, string Position, long CreatedAt);
internal sealed record ReadingDataAnnotation(long Id, string BookId, string Position, string Quotation, string Note,
    long UpdatedAt, string Highlight);
internal sealed record ReadingDataRecognizedPage(string BookId, long Page, string Text);
internal sealed record ReadingDataCollectionMembership(long CollectionId, string BookId);
internal sealed record ReadingDataManualOrder(string Shelf, string BookId, long Rank, long? CollectionId);
internal sealed record ReadingDataGoalDay(long DayNumber, long Milliseconds, long TargetMinutes);
internal sealed record ReadingDataGoalFinish(string BookId, long DayNumber);
// DayNumber uses DateOnly.DayNumber, beginning at zero on 0001-01-01.
internal sealed record ReadingDataGoals(IReadOnlyList<ReadingDataGoalDay> Days, IReadOnlyList<ReadingDataGoalFinish> Finishes);

public sealed partial class LibraryStore
{
    internal Task<LibraryReadingData> GetReadingDataExportAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<LibraryReadingData>(token);

        return Query(db =>
        {
            token.ThrowIfCancellationRequested();
            db.ExecuteScript("BEGIN;");
            var books = ReadRows("""
                SELECT books.id, books.title, COALESCE(NULLIF(metadata.title, ''), books.title),
                       COALESCE(metadata.author, ''), books.format, books.imported_at, activity.opened_at,
                       COALESCE(reading.state, 0), hidden.book_id IS NOT NULL
                FROM books
                LEFT JOIN native_library_book_metadata AS metadata ON metadata.book_id=books.id AND metadata.cache_version=?
                LEFT JOIN book_activity AS activity ON activity.book_id=books.id
                LEFT JOIN native_library_reading_states AS reading ON reading.book_id=books.id
                LEFT JOIN native_library_continue_hidden AS hidden ON hidden.book_id=books.id
                ORDER BY books.id
                """, row => new ReadingDataBook(row.Text(0), row.Text(1), row.Text(2), row.Text(3), row.Text(4),
                row.Integer(5), row.NullableInteger(6), ReadingStateName(row.Integer(7)), row.Integer(8) != 0), BookMetadataVersion);

            // The original table still holds TXT/PDF positions and may also contain a legacy EPUB CFI.
            var positions = ReadRows("SELECT book_id, position FROM reading_positions ORDER BY book_id",
                row => new ReadingDataPosition(row.Text(0), row.Text(1)));
            var epubPositions = ReadRows("SELECT book_id, position FROM native_epub_positions ORDER BY book_id",
                row => new ReadingDataPosition(row.Text(0), row.Text(1)));
            var bookmarks = ReadRows("SELECT id, book_id, position, created_at FROM bookmarks ORDER BY book_id, id",
                row => new ReadingDataBookmark(row.Integer(0), row.Text(1), row.Text(2), row.Integer(3)));
            var annotations = ReadRows("""
                SELECT annotations.id, annotations.book_id, position, quotation, note, updated_at, COALESCE(highlights.style, 0)
                FROM annotations
                LEFT JOIN native_annotation_highlights AS highlights ON highlights.annotation_id=annotations.id
                ORDER BY annotations.book_id, annotations.id
                """, row => new ReadingDataAnnotation(row.Integer(0), row.Text(1), row.Text(2), row.Text(3), row.Text(4),
                row.Integer(5), HighlightName(row.Integer(6))));
            var recognizedPages = ReadRows("SELECT book_id, page, text FROM recognized_pages ORDER BY book_id, page",
                row => new ReadingDataRecognizedPage(row.Text(0), row.Integer(1), row.Text(2)));
            var collections = ReadRows("SELECT id, name FROM native_library_collections ORDER BY id",
                row => new LibraryCollection(row.Integer(0), row.Text(1)));
            var memberships = ReadRows("""
                SELECT collection_id, book_id FROM native_library_collection_books ORDER BY collection_id, book_id
                """, row => new ReadingDataCollectionMembership(row.Integer(0), row.Text(1)));
            var manualOrder = ReadRows("""
                SELECT shelf, book_id, rank, collection_id FROM native_library_manual_order ORDER BY shelf, rank, book_id
                """, row => new ReadingDataManualOrder(row.Text(0), row.Text(1), row.Integer(2), row.NullableInteger(3)));
            var goals = new ReadingDataGoals(
                ReadRows("""
                    SELECT day_number, milliseconds, target_minutes FROM native_reading_goal_days ORDER BY day_number
                    """, row => new ReadingDataGoalDay(row.Integer(0), row.Integer(1), row.Integer(2))),
                ReadRows("SELECT book_id, day_number FROM native_reading_goal_finishes ORDER BY day_number, book_id",
                    row => new ReadingDataGoalFinish(row.Text(0), row.Integer(1))));
            var preferences = ReadRows("""
                SELECT key, value FROM application_preferences
                WHERE key IN ('reading-appearance', 'reading-page-layout', 'reading-goals', 'library-display-mode',
                              'library-sort-order', 'txt-reading-mode', 'epub-reading-mode', 'pdf-reading-mode',
                              'fb2-reading-mode', 'html-reading-mode', 'docx-reading-mode')
                ORDER BY key
                """, row => (Key: row.Text(0), Value: row.Text(1)))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

            token.ThrowIfCancellationRequested();
            db.ExecuteScript("COMMIT;");
            return new LibraryReadingData("seareader.windows.reading-data", 1, DateTimeOffset.UtcNow, books,
                positions, epubPositions, bookmarks, annotations, recognizedPages, collections, memberships,
                manualOrder, goals, preferences);

            List<T> ReadRows<T>(string sql, Func<LibraryRow, T> map, params object[] arguments)
            {
                token.ThrowIfCancellationRequested();
                return db.Read(sql, row =>
                {
                    token.ThrowIfCancellationRequested();
                    return map(row);
                }, arguments);
            }
        });
    }

    private static string ReadingStateName(long state) => (LibraryReadingState)state switch
    {
        LibraryReadingState.WantToRead => "wantToRead",
        LibraryReadingState.Finished => "finished",
        _ => "none"
    };

    private static string HighlightName(long highlight) => (AnnotationHighlight)highlight switch
    {
        AnnotationHighlight.Amber => "amber",
        AnnotationHighlight.Mint => "mint",
        AnnotationHighlight.Lavender => "lavender",
        AnnotationHighlight.Underline => "underline",
        _ => "none"
    };
}
