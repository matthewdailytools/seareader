using System.Globalization;
using System.IO;

namespace SeaReader.Native;

internal sealed record ReadingDataRestorePreview(int MatchedBooks, int MissingBooks);
internal sealed record ReadingDataRestoreResult(int MatchedBooks, int MissingBooks, int AddedRecords);

public sealed partial class LibraryStore
{
    private sealed record RestoreBook(string Format, bool WasOpened);

    internal Task<ReadingDataRestorePreview> PreviewReadingDataRestoreAsync(LibraryReadingData data, CancellationToken token) =>
        Query(db =>
        {
            var matched = MatchRestoreBooks(db, data.Books, token);
            return new ReadingDataRestorePreview(matched.Count, data.Books.Count - matched.Count);
        });

    internal Task<ReadingDataRestoreResult> RestoreReadingDataAsync(LibraryReadingData data, CancellationToken token) => Query(db =>
    {
        token.ThrowIfCancellationRequested();
        db.ExecuteScript("BEGIN IMMEDIATE;");
        try
        {
            var matched = MatchRestoreBooks(db, data.Books, token);
            RestoreBookData(db, data, matched, token);
            RestoreLibraryOrganization(db, data, matched, token);
            RestoreGoalsAndPreferences(db, data, matched, token);
            var added = checked((int)db.Read("SELECT total_changes()", row => row.Integer(0)).Single());
            token.ThrowIfCancellationRequested();
            db.ExecuteScript("COMMIT;");
            return new ReadingDataRestoreResult(matched.Count, data.Books.Count - matched.Count, added);
        }
        catch
        {
            // SQLite can already have rolled back on a database error. Preserve that original error.
            try { db.ExecuteScript("ROLLBACK;"); }
            catch (InvalidOperationException) { }
            throw;
        }
    });

    private Dictionary<string, RestoreBook> MatchRestoreBooks(LibraryDatabase db, IReadOnlyList<ReadingDataBook> books,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var available = db.Read("""
            SELECT books.id, books.format, activity.book_id IS NOT NULL
            FROM books LEFT JOIN book_activity AS activity ON activity.book_id=books.id
            """, row =>
            {
                token.ThrowIfCancellationRequested();
                return (Id: row.Text(0), Book: new RestoreBook(row.Text(1), row.Integer(2) != 0));
            }).ToDictionary(row => row.Id, row => row.Book, StringComparer.Ordinal);
        var matched = new Dictionary<string, RestoreBook>(StringComparer.Ordinal);
        foreach (var book in books)
        {
            token.ThrowIfCancellationRequested();
            if (available.TryGetValue(book.Id, out var local) && local.Format.Equals(book.Format, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(BookPath(book.Id))) matched.TryAdd(book.Id, local);
        }
        return matched;
    }

    private static void RestoreBookData(LibraryDatabase db, LibraryReadingData data, Dictionary<string, RestoreBook> matched,
        CancellationToken token)
    {
        var existingPositions = db.Read("SELECT book_id FROM reading_positions UNION SELECT book_id FROM native_epub_positions", row =>
        {
            token.ThrowIfCancellationRequested();
            return row.Text(0);
        }).ToHashSet(StringComparer.Ordinal);
        foreach (var book in data.Books)
        {
            token.ThrowIfCancellationRequested();
            if (!matched.TryGetValue(book.Id, out var local)) continue;
            if (book.OpenedAt is { } opened && !local.WasOpened)
                db.Execute("INSERT INTO book_activity(book_id, opened_at) VALUES (?, ?) ON CONFLICT(book_id) DO NOTHING", book.Id, opened);
            if (book.ContinueHidden && !local.WasOpened)
                db.Execute("INSERT INTO native_library_continue_hidden(book_id) VALUES (?) ON CONFLICT(book_id) DO NOTHING", book.Id);

            var state = book.ReadingState switch
            {
                "wantToRead" => LibraryReadingState.WantToRead,
                "finished" => LibraryReadingState.Finished,
                _ => LibraryReadingState.None
            };
            // An opened book without a status row is already in progress in this library.
            if (state != LibraryReadingState.None && !local.WasOpened)
                db.Execute("""
                    INSERT INTO native_library_reading_states(book_id, state) VALUES (?, ?) ON CONFLICT(book_id) DO NOTHING
                    """, book.Id, (long)state);
            if (book.Title != book.OriginalTitle || !string.IsNullOrWhiteSpace(book.Author))
                db.Execute("""
                    INSERT INTO native_library_book_metadata(book_id, cache_version, title, author, has_cover, cover_checked)
                    VALUES (?, ?, ?, ?, 0, 0) ON CONFLICT(book_id) DO NOTHING
                    """, book.Id, BookMetadataVersion, LimitMetadata(book.Title), LimitMetadata(book.Author));
        }

        // Either existing position protects the local resume point. On a fresh EPUB restore both forms.
        foreach (var position in data.ReadingPositions)
        {
            token.ThrowIfCancellationRequested();
            if (matched.ContainsKey(position.BookId) && !existingPositions.Contains(position.BookId))
                db.Execute("INSERT INTO reading_positions(book_id, position) VALUES (?, ?) ON CONFLICT(book_id) DO NOTHING",
                    position.BookId, position.Position);
        }
        foreach (var position in data.NativeEpubPositions)
        {
            token.ThrowIfCancellationRequested();
            if (matched.TryGetValue(position.BookId, out var local) && local.Format.Equals("epub", StringComparison.OrdinalIgnoreCase) &&
                !existingPositions.Contains(position.BookId))
                db.Execute("INSERT INTO native_epub_positions(book_id, position) VALUES (?, ?) ON CONFLICT(book_id) DO NOTHING",
                    position.BookId, position.Position);
        }

        foreach (var bookmark in data.Bookmarks)
        {
            token.ThrowIfCancellationRequested();
            if (matched.ContainsKey(bookmark.BookId))
                db.Execute("""
                    INSERT INTO bookmarks(book_id, position, created_at) VALUES (?, ?, ?) ON CONFLICT(book_id, position) DO NOTHING
                    """, bookmark.BookId, bookmark.Position, bookmark.CreatedAt);
        }
        foreach (var annotation in data.Annotations)
        {
            token.ThrowIfCancellationRequested();
            if (!matched.ContainsKey(annotation.BookId)) continue;
            db.Execute("""
                INSERT INTO annotations(book_id, position, quotation, note, updated_at) VALUES (?, ?, ?, ?, ?)
                ON CONFLICT(book_id, position) DO NOTHING
                """, annotation.BookId, annotation.Position, annotation.Quotation, annotation.Note, annotation.UpdatedAt);
            var highlight = annotation.Highlight switch
            {
                "amber" => AnnotationHighlight.Amber,
                "mint" => AnnotationHighlight.Mint,
                "lavender" => AnnotationHighlight.Lavender,
                "underline" => AnnotationHighlight.Underline,
                _ => AnnotationHighlight.None
            };
            if (highlight != AnnotationHighlight.None)
                db.Execute("""
                    INSERT INTO native_annotation_highlights(annotation_id, style)
                    SELECT last_insert_rowid(), ? WHERE changes()=1
                    """, (long)highlight);
        }
        foreach (var page in data.RecognizedPages)
        {
            token.ThrowIfCancellationRequested();
            if (matched.TryGetValue(page.BookId, out var local) && local.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase))
                db.Execute("""
                    INSERT INTO recognized_pages(book_id, page, text) VALUES (?, ?, ?) ON CONFLICT(book_id, page) DO NOTHING
                    """, page.BookId, page.Page, page.Text);
        }
    }

    private static void RestoreLibraryOrganization(LibraryDatabase db, LibraryReadingData data,
        Dictionary<string, RestoreBook> matched, CancellationToken token)
    {
        var localCollections = db.Read("SELECT name_key, id FROM native_library_collections", row =>
        {
            token.ThrowIfCancellationRequested();
            return (Key: row.Text(0), Id: row.Integer(1));
        }).ToDictionary(row => row.Key, row => row.Id, StringComparer.Ordinal);
        var collectionMap = new Dictionary<long, long>();
        foreach (var collection in data.Collections)
        {
            token.ThrowIfCancellationRequested();
            var (name, key) = NormalizeCollectionName(collection.Name);
            if (!localCollections.TryGetValue(key, out var localId))
            {
                db.Execute("INSERT INTO native_library_collections(name, name_key) VALUES (?, ?)", name, key);
                localId = db.Read("SELECT last_insert_rowid()", row => row.Integer(0)).Single();
                localCollections.Add(key, localId);
            }
            collectionMap.Add(collection.Id, localId);
        }
        foreach (var membership in data.CollectionMemberships)
        {
            token.ThrowIfCancellationRequested();
            if (matched.ContainsKey(membership.BookId) && collectionMap.TryGetValue(membership.CollectionId, out var collectionId))
                db.Execute("""
                    INSERT INTO native_library_collection_books(collection_id, book_id) VALUES (?, ?)
                    ON CONFLICT(collection_id, book_id) DO NOTHING
                    """, collectionId, membership.BookId);
        }

        foreach (var shelf in data.ManualOrder.GroupBy(entry => entry.Shelf, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (ParseManualShelf(shelf.Key) is not { } scope) continue;
            var (key, collectionId) = scope;
            if (collectionId > 0)
            {
                if (!collectionMap.TryGetValue(collectionId, out collectionId)) continue;
                key = CollectionShelfPrefix + collectionId.ToString(CultureInfo.InvariantCulture);
            }

            var eligible = ReadBooksInManualShelf(db, key, collectionId).Where(matched.ContainsKey).ToHashSet(StringComparer.Ordinal);
            var localOrder = db.Read("SELECT book_id, rank FROM native_library_manual_order WHERE shelf=?", row =>
            {
                token.ThrowIfCancellationRequested();
                return (BookId: row.Text(0), Rank: row.Integer(1));
            }, key);
            eligible.ExceptWith(localOrder.Select(entry => entry.BookId));
            var rank = localOrder.Count == 0 ? -1 : localOrder.Max(entry => entry.Rank);
            foreach (var entry in shelf.OrderBy(entry => entry.Rank).ThenBy(entry => entry.BookId, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (!eligible.Remove(entry.BookId)) continue;
                rank = checked(rank + 1);
                db.Execute("""
                    INSERT INTO native_library_manual_order(shelf, book_id, rank, collection_id)
                    VALUES (?, ?, ?, NULLIF(?, 0))
                    """, key, entry.BookId, rank, collectionId);
            }
        }
    }

    private static void RestoreGoalsAndPreferences(LibraryDatabase db, LibraryReadingData data,
        Dictionary<string, RestoreBook> matched, CancellationToken token)
    {
        foreach (var day in data.ReadingGoals.Days)
        {
            token.ThrowIfCancellationRequested();
            db.Execute("""
                INSERT INTO native_reading_goal_days(day_number, milliseconds, target_minutes) VALUES (?, ?, ?)
                ON CONFLICT(day_number) DO NOTHING
                """, day.DayNumber, day.Milliseconds, day.TargetMinutes);
        }
        foreach (var finish in data.ReadingGoals.Finishes)
        {
            token.ThrowIfCancellationRequested();
            if (matched.ContainsKey(finish.BookId))
                db.Execute("""
                    INSERT INTO native_reading_goal_finishes(book_id, day_number)
                    SELECT ?, ? WHERE EXISTS (SELECT 1 FROM native_library_reading_states WHERE book_id=? AND state=2)
                    ON CONFLICT(book_id) DO NOTHING
                    """, finish.BookId, finish.DayNumber, finish.BookId);
        }
        foreach (var (key, value) in data.Preferences)
        {
            token.ThrowIfCancellationRequested();
            if (LibraryReadingDataImport.IsSupportedPreference(key))
                db.Execute("INSERT INTO application_preferences(key, value) VALUES (?, ?) ON CONFLICT(key) DO NOTHING", key, value);
        }
    }
}
