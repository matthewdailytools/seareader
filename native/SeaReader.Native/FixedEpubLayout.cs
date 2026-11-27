using System.IO;
using System.IO.Compression;
using System.Windows;

namespace SeaReader.Native;

internal enum FixedEpubPageSide { Auto, Left, Right, Center }
internal enum FixedEpubSpreadPolicy { Auto, None, Landscape, Both }
internal enum FixedEpubOrientation { Auto, Portrait, Landscape }

internal sealed record FixedEpubPage(string Path, string Title, Size Size, FixedEpubPageSide Side,
    FixedEpubSpreadPolicy SpreadPolicy, FixedEpubOrientation Orientation, int SpineIndex, string? SpineId);

internal readonly record struct FixedEpubSpread(int? Left, int? Right, int? Center);

internal sealed record FixedEpubAsset(string MediaType, byte[] Bytes);

internal sealed class FixedEpubLayout
{
    private readonly string _archivePath;
    private readonly Dictionary<string, string> _assets;
    private readonly FixedEpubSpread[] _portraitSpreads;
    private readonly FixedEpubSpread[] _landscapeSpreads;
    private readonly SemaphoreSlim _assetReads = new(4);

    internal FixedEpubLayout(string archivePath, FixedEpubPage[] pages, bool rightToLeft,
        Dictionary<string, string> assets)
    {
        _archivePath = archivePath;
        _assets = assets;
        Pages = Array.AsReadOnly(pages);
        RightToLeft = rightToLeft;
        _portraitSpreads = PlanSpreads(false);
        _landscapeSpreads = PlanSpreads(true);
    }

    internal IReadOnlyList<FixedEpubPage> Pages { get; }
    internal bool RightToLeft { get; }

    internal FixedEpubSpread GetSpread(int active, bool allowSpread, bool landscape)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(active);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(active, Pages.Count);
        if (!allowSpread) return new FixedEpubSpread(null, null, active);
        return (landscape ? _landscapeSpreads : _portraitSpreads)[active];
    }

    private FixedEpubSpread[] PlanSpreads(bool landscape)
    {
        var spreads = new FixedEpubSpread[Pages.Count];
        var leading = RightToLeft ? FixedEpubPageSide.Right : FixedEpubPageSide.Left;
        var trailing = RightToLeft ? FixedEpubPageSide.Left : FixedEpubPageSide.Right;
        int? left = null;
        int? right = null;
        for (int index = 0; index < Pages.Count; index++)
        {
            var page = Pages[index];
            if (page.Side == FixedEpubPageSide.Center || page.SpreadPolicy == FixedEpubSpreadPolicy.None ||
                (page.SpreadPolicy == FixedEpubSpreadPolicy.Landscape && !landscape))
            {
                Flush();
                spreads[index] = new FixedEpubSpread(null, null, index);
                continue;
            }

            var side = page.Side;
            if (side == FixedEpubPageSide.Auto)
                // The first page sits opposite the normal leading side so it can serve as the cover.
                side = index == 0 || left.HasValue || right.HasValue ? trailing : leading;

            if (side == leading) Flush();
            if (side == FixedEpubPageSide.Left) left = index;
            else right = index;
            if (side == trailing) Flush();
        }
        Flush();
        return spreads;

        void Flush()
        {
            var spread = new FixedEpubSpread(left, right, null);
            if (left is int leftIndex) spreads[leftIndex] = spread;
            if (right is int rightIndex) spreads[rightIndex] = spread;
            left = right = null;
        }
    }

    internal async Task<FixedEpubAsset?> ReadAssetAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_assets.TryGetValue(normalizedPath, out var mediaType)) return null;

        await _assetReads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                try
                {
                    using var archive = ZipFile.OpenRead(_archivePath);
                    var entry = archive.GetEntry(normalizedPath);
                    if (entry == null || entry.Length > AssetLimit(mediaType)) return null;
                    var bytes = GC.AllocateUninitializedArray<byte>((int)entry.Length);
                    using var stream = entry.Open();
                    await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new FixedEpubAsset(mediaType, bytes);
                }
                catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _assetReads.Release(); }
    }

    internal static string? SupportedAssetMediaType(string mediaType) => mediaType switch
    {
        "application/xhtml+xml" or "text/html" or "text/css" or "image/svg+xml" or "image/jpeg" or "image/png" or
        "image/gif" or "image/webp" or "font/ttf" or "font/otf" or "font/woff" or "font/woff2" => mediaType,
        "application/x-font-ttf" => "font/ttf",
        "application/x-font-otf" or "application/vnd.ms-opentype" => "font/otf",
        "application/font-woff" => "font/woff",
        _ => null
    };

    internal static int AssetLimit(string mediaType) => mediaType switch
    {
        "text/css" => 1024 * 1024,
        "font/ttf" or "font/otf" or "font/woff" or "font/woff2" => 8 * 1024 * 1024,
        _ => 16 * 1024 * 1024
    };
}
