using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;

namespace SeaReader.Native;

internal sealed record EpubIllustration(int Index, int Offset, string AlternativeText, BitmapSource Bitmap, string? DetailPath = null);

public sealed partial class EpubDocument
{
    private const int MaximumDetailEdge = 3200;
    private static readonly SemaphoreSlim DetailDecode = new(1);

    internal async Task<BitmapSource?> ReadIllustrationDetailAsync(EpubIllustration illustration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (illustration.DetailPath is not { } path || !_rasterManifestItems.TryGetValue(path, out var item)) return null;

        // Closing a window cannot interrupt the native decoder mid-call. Keep later
        // detail requests from accumulating native decodes while cancellation settles.
        await DetailDecode.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => ReadIllustrationDetail(item, illustration.Bitmap, ct), ct).ConfigureAwait(false); }
        finally { DetailDecode.Release(); }
    }

    private BitmapSource? ReadIllustrationDetail(ManifestItem item, BitmapSource preview, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var archive = ZipFile.OpenRead(_path);
            if (TryReadCoverImage(archive, item, ct) is not { } bytes ||
                LibraryThumbnail.DecodeEpubIllustration(bytes, MaximumDetailEdge, ct, out _) is not { } detail) return null;
            return detail.PixelWidth >= preview.PixelWidth && detail.PixelHeight >= preview.PixelHeight &&
                (detail.PixelWidth > preview.PixelWidth || detail.PixelHeight > preview.PixelHeight) ? detail : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    private sealed class IllustrationCollector(string chapterPath, IReadOnlyDictionary<string, ManifestItem> rasterItems)
    {
        private const int MaximumCandidates = 64;
        private const int MaximumIllustrations = 32;
        private const int MaximumImageEdge = 1280;
        private const int ChapterEncodedBytes = 24 * 1024 * 1024;
        private const int ChapterDecodedPixels = 12 * 1024 * 1024;
        private readonly List<Candidate> candidates = [];
        private int nextIndex;

        private readonly record struct Candidate(int Index, int Offset, string AlternativeText, ManifestItem Item);

        internal EpubIllustration[] Images { get; private set; } = [];

        internal void Add(string? href, int offset, string? alternative)
        {
            int index = nextIndex++;
            if (candidates.Count == MaximumCandidates || href is not { Length: > 0 and <= 4096 } ||
                ResolvePath(chapterPath, href.Trim()) is not { } path || !rasterItems.TryGetValue(path, out var item)) return;

            candidates.Add(new Candidate(index, offset, NormalizeMetadataText(alternative ?? "", 512), item));
        }

        internal void ReadImages(ZipArchive archive, int length, CancellationToken ct)
        {
            if (candidates.Count == 0) return;
            var result = new List<EpubIllustration>();
            var decoded = new Dictionary<string, (BitmapSource? Bitmap, bool HasDetail)>(StringComparer.Ordinal);
            int encodedBytes = 0;
            int decodedPixels = 0;
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!decoded.TryGetValue(candidate.Item.Path, out var image))
                {
                    image = ReadImage(candidate.Item);
                    decoded.Add(candidate.Item.Path, image);
                }
                if (image.Bitmap is not { } bitmap) continue;
                result.Add(new EpubIllustration(candidate.Index, Math.Min(candidate.Offset, length), candidate.AlternativeText,
                    bitmap, image.HasDetail ? candidate.Item.Path : null));
                if (result.Count == MaximumIllustrations) break;
            }
            Images = result.ToArray();

            (BitmapSource? Bitmap, bool HasDetail) ReadImage(ManifestItem item)
            {
                var entry = archive.GetEntry(item.Path);
                if (entry == null || entry.Length <= 0 || entry.Length > CoverByteLimit ||
                    entry.Length > ChapterEncodedBytes - encodedBytes || decodedPixels == ChapterDecodedPixels) return default;

                encodedBytes += (int)entry.Length;
                try
                {
                    // The cover pipeline already verifies manifest media type and input bounds.
                    if (TryReadCoverImage(archive, item, ct) is not { } bytes ||
                        LibraryThumbnail.DecodeEpubIllustration(bytes, MaximumImageEdge, ct, out var hasDetail) is not { } bitmap ||
                        bitmap.PixelWidth * bitmap.PixelHeight > ChapterDecodedPixels - decodedPixels) return default;
                    decodedPixels += bitmap.PixelWidth * bitmap.PixelHeight;
                    return (bitmap, hasDetail);
                }
                catch (IOException) { return default; }
            }
        }

        internal void Clear()
        {
            candidates.Clear();
            nextIndex = 0;
            Images = [];
        }
    }
}
