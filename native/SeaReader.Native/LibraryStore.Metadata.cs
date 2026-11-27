using System.IO;

namespace SeaReader.Native;

internal sealed record LibraryBookMetadata(string Title, string Author, bool HasCover, bool CoverChecked = true);

public sealed partial class LibraryStore
{
    private const long BookMetadataVersion = 1;
    private const int MaximumCoverSize = 512 * 1024;
    private const int MaximumMetadataLength = 512;
    private string CoverCacheDirectory => Path.Combine(DataDirectory, "cover-cache");

    private static void InitializeMetadataTable(LibraryDatabase db) => db.ExecuteScript("""
        CREATE TABLE IF NOT EXISTS native_library_book_metadata (
            book_id TEXT PRIMARY KEY NOT NULL REFERENCES books(id) ON DELETE CASCADE,
            cache_version INTEGER NOT NULL,
            title TEXT NOT NULL,
            author TEXT NOT NULL,
            has_cover INTEGER NOT NULL CHECK (has_cover IN (0, 1)),
            cover_checked INTEGER NOT NULL CHECK (cover_checked IN (0, 1) AND has_cover <= cover_checked));
        """);

    internal string SourceBookPath(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return BookPath(id);
    }

    internal string CoverCachePath(string id) => Path.Combine(CoverCacheDirectory,
        $"{Path.GetFileName(SourceBookPath(id))}.v{BookMetadataVersion}.jpg");

    internal Task<IReadOnlyDictionary<string, LibraryBookMetadata>> GetBookMetadataAsync() =>
        Query<IReadOnlyDictionary<string, LibraryBookMetadata>>(db =>
            db.Read("SELECT book_id, title, author, has_cover, cover_checked FROM native_library_book_metadata WHERE cache_version=?",
                row => (Id: row.Text(0), Metadata: new LibraryBookMetadata(row.Text(1), row.Text(2), row.Integer(3) == 1,
                    row.Integer(4) == 1)), BookMetadataVersion)
            .ToDictionary(item => item.Id, item => item.Metadata.HasCover && !HasCachedCover(CoverCachePath(item.Id))
                ? item.Metadata with { HasCover = false, CoverChecked = false } : item.Metadata, StringComparer.Ordinal));

    internal async Task SaveBookMetadataAsync(string id, string title, string author, byte[]? thumbnail,
        CancellationToken ct = default, bool coverChecked = true, bool discardInvalidCover = false)
    {
        ct.ThrowIfCancellationRequested();
        var cover = CoverCachePath(id);
        if (!coverChecked && thumbnail != null)
            throw new ArgumentException("只更新书目信息时不应提供封面。", nameof(thumbnail));
        if (thumbnail != null && !IsThumbnail(thumbnail))
            throw new ArgumentException("封面缓存需要不超过 512 KiB 的 JPEG 缩略图。", nameof(thumbnail));
        title = LimitMetadata(title);
        author = LimitMetadata(author);
        string? pending = null;
        try
        {
            if (thumbnail != null)
            {
                Directory.CreateDirectory(CoverCacheDirectory);
                pending = $"{cover}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllBytesAsync(pending, thumbnail, ct).ConfigureAwait(false);
            }

            await Query(db =>
            {
                ct.ThrowIfCancellationRequested();
                db.ExecuteScript("BEGIN IMMEDIATE;");
                EnsureBookExists(db, id);
                ct.ThrowIfCancellationRequested();

                var checkedCover = coverChecked;
                var hasCover = pending != null;
                if (pending != null) File.Move(pending, cover, overwrite: true);
                else
                {
                    var prior = db.Read("""
                        SELECT has_cover, cover_checked FROM native_library_book_metadata WHERE book_id=? AND cache_version=?
                        """, row => (HasCover: row.Integer(0) == 1, CoverChecked: row.Integer(1) == 1), id, BookMetadataVersion)
                        .SingleOrDefault();
                    checkedCover |= prior.CoverChecked;
                    hasCover = !discardInvalidCover && prior.HasCover && (!coverChecked || HasCachedCover(cover));
                }

                db.Execute("""
                    INSERT INTO native_library_book_metadata(book_id, cache_version, title, author, has_cover, cover_checked)
                    VALUES (?, ?, ?, ?, ?, ?)
                    ON CONFLICT(book_id) DO UPDATE SET cache_version=excluded.cache_version,
                        title=CASE WHEN excluded.title='' AND cache_version=excluded.cache_version THEN title ELSE excluded.title END,
                        author=CASE WHEN excluded.author='' AND cache_version=excluded.cache_version THEN author ELSE excluded.author END,
                        has_cover=excluded.has_cover, cover_checked=excluded.cover_checked
                    """, id, BookMetadataVersion, title, author, hasCover ? 1L : 0L, checkedCover ? 1L : 0L);
                db.ExecuteScript("COMMIT;");
            }).ConfigureAwait(false);
        }
        finally
        {
            if (pending != null) File.Delete(pending);
        }
    }

    private static bool HasCachedCover(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length is >= 4 and <= MaximumCoverSize;
    }

    private static bool IsThumbnail(ReadOnlySpan<byte> image) =>
        image.Length is >= 4 and <= MaximumCoverSize && image[0] == 0xff && image[1] == 0xd8 &&
        image[^2] == 0xff && image[^1] == 0xd9;

    private static string LimitMetadata(string value)
    {
        value = value.Trim().Replace('\0', ' ');
        if (value.Length <= MaximumMetadataLength) return value;
        var length = MaximumMetadataLength;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
}
