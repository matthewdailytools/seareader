using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace SeaReader.Native;

internal sealed record EpubLibraryMetadata(string Title, string Author, byte[]? Cover);

public sealed partial class EpubDocument
{
    private const int CoverByteLimit = 8 * 1024 * 1024;
    private const int CoverDimensionLimit = 8192;
    private const int CoverPixelLimit = 24 * 1024 * 1024;
    private static readonly XNamespace DublinCore = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    internal static Task<EpubLibraryMetadata> ReadLibraryMetadataAsync(string path, CancellationToken ct = default,
        bool includeCover = true) =>
        Task.Run(() => ReadLibraryMetadata(path, ct, includeCover), ct);

    private static EpubLibraryMetadata ReadLibraryMetadata(string path, CancellationToken ct, bool includeCover)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var (packagePath, package) = ReadPackage(archive);
            var manifest = FindChild(package.Root, "manifest")
                ?? throw new InvalidDataException("EPUB 缺少书籍清单。");
            if (package.Root?.Name.LocalName != "package")
                throw new InvalidDataException("EPUB 书籍描述文件格式无效。");

            var metadata = FindChild(package.Root, "metadata");
            var (title, author) = ReadPublicationTitleAndAuthor(metadata);
            ct.ThrowIfCancellationRequested();
            var cover = includeCover
                ? ReadLibraryCover(archive, packagePath, package.Root, metadata, ReadManifest(manifest, packagePath, ct), ct)
                : null;
            return new EpubLibraryMetadata(title, author, cover);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("EPUB 书籍描述文件格式无效。", ex);
        }
    }

    private static (string Title, string Author) ReadPublicationTitleAndAuthor(XElement? metadata)
    {
        var title = metadata?.Elements(DublinCore + "title")
            .Select(element => NormalizeMetadataText(element.Value, 512))
            .FirstOrDefault(value => value.Length > 0) ?? "";
        var creators = metadata?.Elements(DublinCore + "creator")
            .Select(element => NormalizeMetadataText(element.Value, 128))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(4);
        return (title, creators is null ? "" : string.Join(", ", creators));
    }

    private static byte[]? ReadLibraryCover(ZipArchive archive, string packagePath, XElement package, XElement? metadata,
        Dictionary<string, ManifestItem> items, CancellationToken ct)
    {
        var images = ReadCoverCandidates(archive, packagePath, package, metadata, items, ct);
        foreach (var item in images.DistinctBy(item => item.Path).Take(8))
        {
            ct.ThrowIfCancellationRequested();
            if (TryReadCoverImage(archive, item, ct) is { } cover) return cover;
        }
        return null;
    }

    private static IEnumerable<ManifestItem> ReadCoverCandidates(ZipArchive archive, string packagePath, XElement package,
        XElement? metadata, Dictionary<string, ManifestItem> items, CancellationToken ct)
    {
        // EPUB 3 declaration takes priority over EPUB 2 compatibility metadata.
        foreach (var image in items.Values.Where(item => HasToken(item.Properties, "cover-image") && IsRasterImage(item)))
            yield return image;

        foreach (var meta in metadata?.Elements().Where(element => element.Name.LocalName == "meta" &&
                     string.Equals((string?)element.Attribute("name"), "cover", StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            var id = ((string?)meta.Attribute("content"))?.Trim();
            if (id is not null && items.TryGetValue(id, out var image) && IsRasterImage(image))
                yield return image;
        }

        // Some EPUB 2 books name a cover page in the guide instead of including cover metadata.
        var guide = FindChild(package, "guide");
        var reference = guide?.Elements().FirstOrDefault(element => element.Name.LocalName == "reference" &&
            HasToken((string?)element.Attribute("type"), "cover"));
        var coverPath = ResolvePath(packagePath, (string?)reference?.Attribute("href"));
        var entry = items.Values.FirstOrDefault(item => item.Path == coverPath);
        if (entry is null) yield break;
        if (IsRasterImage(entry))
        {
            yield return entry;
            yield break;
        }
        if (entry.MediaType is not ("application/xhtml+xml" or "text/html") ||
            TryReadMetadata(archive, entry.Path) is not { } coverPage)
            yield break;

        var rasterPaths = items.Values.Where(IsRasterImage)
            .GroupBy(item => item.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var element in coverPage.Descendants())
        {
            ct.ThrowIfCancellationRequested();
            var href = element.Name.LocalName == "img" ? (string?)element.Attribute("src")
                : element.Name == Svg + "image" ? (string?)element.Attribute(XLink + "href") ?? (string?)element.Attribute("href")
                : null;
            if (ResolvePath(entry.Path, href) is { } path && rasterPaths.TryGetValue(path, out var image))
                yield return image;
        }
    }

    private static bool IsRasterImage(ManifestItem item) =>
        item.MediaType is "image/png" or "image/jpeg" or "image/gif";

    private static byte[]? TryReadCoverImage(ZipArchive archive, ManifestItem item, CancellationToken ct)
    {
        var entry = archive.GetEntry(item.Path);
        if (entry is null || entry.Length <= 0 || entry.Length > CoverByteLimit) return null;

        try
        {
            using var stream = entry.Open();
            var bytes = new byte[(int)entry.Length];
            for (int offset = 0; offset < bytes.Length;)
            {
                ct.ThrowIfCancellationRequested();
                int count = stream.Read(bytes.AsSpan(offset, Math.Min(64 * 1024, bytes.Length - offset)));
                if (count == 0) return null;
                offset += count;
            }

            // Confirm both the actual payload format and dimensions before handing a cover to a decoder.
            if (stream.ReadByte() != -1 || !IsBoundedRaster(bytes, item.MediaType)) return null;
            return bytes;
        }
        catch (InvalidDataException) { return null; }
    }

    private static bool IsBoundedRaster(ReadOnlySpan<byte> bytes, string mediaType)
    {
        if (mediaType == "image/png" && bytes.Length >= 33 &&
            bytes.StartsWith<byte>([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a]) &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && bytes[12..16].SequenceEqual("IHDR"u8))
        {
            return IsValidCoverSize(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]),
                BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]));
        }
        if (mediaType == "image/gif" && bytes.Length >= 13 &&
            (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)))
        {
            return IsValidCoverSize(BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
        }
        if (mediaType != "image/jpeg" || bytes.Length < 12 || !bytes.StartsWith<byte>([0xff, 0xd8, 0xff]))
            return false;

        for (int offset = 2; offset < bytes.Length - 3;)
        {
            if (bytes[offset++] != 0xff) return false;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length - 2) return false;
            int marker = bytes[offset++];
            int size = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (size < 2 || size > bytes.Length - offset || marker is 0xd9 or 0xda) return false;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                return size >= 8 && IsValidCoverSize(BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]));
            }
            offset += size;
        }
        return false;
    }

    private static bool IsValidCoverSize(uint width, uint height) =>
        width is > 0 and <= CoverDimensionLimit && height is > 0 and <= CoverDimensionLimit &&
        width * height <= CoverPixelLimit;

    private static string NormalizeMetadataText(string value, int limit)
    {
        var normalized = new StringBuilder(Math.Min(value.Length, limit));
        bool space = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                space = normalized.Length > 0;
                continue;
            }
            if (space && normalized.Length < limit) normalized.Append(' ');
            if (normalized.Length == limit) break;
            normalized.Append(character);
            space = false;
        }
        if (normalized.Length > 0 && char.IsHighSurrogate(normalized[^1])) normalized.Length--;
        return normalized.ToString().TrimEnd();
    }
}
