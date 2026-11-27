using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml;
using System.Xml.Linq;

namespace SeaReader.Native;

public sealed partial class EpubDocument
{
    private const double MaximumFixedDimension = 32768;
    private static readonly Regex ViewportMember = new(
        @"(?:^|[\s,;])(?<name>width|height)\s*=\s*(?<value>[^\s,;]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ViewportNumber = new(@"\A[+]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal FixedEpubLayout? FixedLayout { get; }

    private static FixedEpubLayout? TryReadFixedLayout(string archivePath, ZipArchive archive, XElement? metadata,
        XElement[] references, Dictionary<string, ManifestItem> items, Dictionary<string, string> labels,
        bool rightToLeft, CancellationToken cancellationToken)
    {
        var defaultFixed = ReadRenderingProperty(metadata, "rendition:layout") == "pre-paginated";
        var defaultSpread = ParseSpread(ReadRenderingProperty(metadata, "rendition:spread")) ?? FixedEpubSpreadPolicy.Auto;
        var defaultOrientation = ParseOrientation(ReadRenderingProperty(metadata, "rendition:orientation")) ?? FixedEpubOrientation.Auto;
        var candidates = new List<(int SpineIndex, XElement Reference, ManifestItem Item, ZipArchiveEntry Entry, string[] Properties)>();
        for (int index = 0; index < references.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = references[index];
            if (((string?)reference.Attribute("linear"))?.Trim() == "no") continue;
            var properties = ((string?)reference.Attribute("properties"))?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
            var fixedOverride = properties.FirstOrDefault(property => property is "rendition:layout-pre-paginated" or "rendition:layout-reflowable");
            var isFixed = fixedOverride == null ? defaultFixed : fixedOverride == "rendition:layout-pre-paginated";
            if (!isFixed) return null;
            if ((string?)reference.Attribute("idref") is not { } id || !items.TryGetValue(id, out var item) ||
                item.MediaType is not ("application/xhtml+xml" or "text/html" or "image/svg+xml") ||
                archive.GetEntry(item.Path) is not { } entry) return null;
            candidates.Add((index, reference, item, entry, properties));
        }
        if (candidates.Count == 0) return null;

        var pages = new FixedEpubPage[candidates.Count];
        Size? previousSize = null;
        for (int index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var spread = ReadOverride(candidate.Properties, "rendition:spread-", ParseSpread) ?? defaultSpread;
            var orientation = ReadOverride(candidate.Properties, "rendition:orientation-", ParseOrientation) ?? defaultOrientation;
            var side = FixedEpubPageSide.Auto;
            foreach (var property in candidate.Properties)
            {
                side = property switch
                {
                    "page-spread-left" or "rendition:page-spread-left" => FixedEpubPageSide.Left,
                    "page-spread-right" or "rendition:page-spread-right" => FixedEpubPageSide.Right,
                    "rendition:page-spread-center" => FixedEpubPageSide.Center,
                    _ => FixedEpubPageSide.Auto
                };
                if (side != FixedEpubPageSide.Auto) break;
            }

            var fallback = previousSize ?? (orientation == FixedEpubOrientation.Landscape ? new Size(900, 600) : new Size(600, 900));
            var (width, height) = ReadPageDimensions(candidate.Entry, candidate.Item.MediaType, cancellationToken);
            var size = new Size(width ?? fallback.Width, height ?? fallback.Height);
            previousSize = size;
            var title = labels.GetValueOrDefault(candidate.Item.Path) ?? $"第 {index + 1} 页";
            pages[index] = new FixedEpubPage(candidate.Item.Path, title, size, side, spread, orientation,
                candidate.SpineIndex, (string?)candidate.Reference.Attribute("id"));
        }

        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FixedEpubLayout.SupportedAssetMediaType(item.MediaType) is { } mediaType &&
                archive.GetEntry(item.Path) is { } entry && entry.Length <= FixedEpubLayout.AssetLimit(mediaType))
                assets.TryAdd(item.Path, mediaType);
        }
        return new FixedEpubLayout(archivePath, pages, rightToLeft, assets);
    }

    private static string? ReadRenderingProperty(XElement? metadata, string property) => metadata?.Elements()
        .FirstOrDefault(element => element.Name.LocalName == "meta" && element.Attribute("refines") == null &&
            ((string?)element.Attribute("property"))?.Trim() == property)?.Value.Trim();

    private static T? ReadOverride<T>(string[] properties, string prefix, Func<string?, T?> parse) where T : struct
    {
        foreach (var property in properties)
        {
            if (property.StartsWith(prefix, StringComparison.Ordinal) && parse(property[prefix.Length..]) is T value)
                return value;
        }
        return null;
    }

    private static FixedEpubSpreadPolicy? ParseSpread(string? value) => value switch
    {
        "auto" => FixedEpubSpreadPolicy.Auto,
        "none" => FixedEpubSpreadPolicy.None,
        "landscape" => FixedEpubSpreadPolicy.Landscape,
        "both" or "portrait" => FixedEpubSpreadPolicy.Both,
        _ => null
    };

    private static FixedEpubOrientation? ParseOrientation(string? value) => value switch
    {
        "auto" => FixedEpubOrientation.Auto,
        "landscape" => FixedEpubOrientation.Landscape,
        "portrait" => FixedEpubOrientation.Portrait,
        _ => null
    };

    private static (double? Width, double? Height) ReadPageDimensions(ZipArchiveEntry entry, string mediaType,
        CancellationToken cancellationToken)
    {
        if (entry.Length > ChapterLimit) return default;
        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, XmlSettings(ChapterLimit));
            if (mediaType == "image/svg+xml") return ReadSvgDimensions(reader);

            int headDepth = -1;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == headDepth) break;
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.Depth == 1 && reader.LocalName.Equals("head", StringComparison.OrdinalIgnoreCase))
                {
                    if (reader.IsEmptyElement) break;
                    headDepth = reader.Depth;
                    continue;
                }
                if (reader.Depth == 1 && reader.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase)) break;
                if (headDepth >= 0 && reader.Depth == headDepth + 1 &&
                    reader.LocalName.Equals("meta", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(reader.GetAttribute("name")?.Trim(), "viewport", StringComparison.OrdinalIgnoreCase))
                    return ReadViewportDimensions(reader.GetAttribute("content") ?? "");
            }
        }
        catch (Exception error) when (error is XmlException or IOException or InvalidDataException) { }
        return default;
    }

    private static (double? Width, double? Height) ReadSvgDimensions(XmlReader reader)
    {
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "svg") return default;
        var viewBox = reader.GetAttribute("viewBox")?.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        if (viewBox is not { Length: 4 } || !FiniteNumber(viewBox[0], out _) || !FiniteNumber(viewBox[1], out _)) return default;
        return (ValidFixedDimension(viewBox[2]), ValidFixedDimension(viewBox[3]));
    }

    private static (double? Width, double? Height) ReadViewportDimensions(string content)
    {
        double? width = null;
        double? height = null;
        bool widthSeen = false;
        bool heightSeen = false;
        foreach (Match match in ViewportMember.Matches(content))
        {
            var isWidth = match.Groups["name"].Value.Equals("width", StringComparison.OrdinalIgnoreCase);
            if (isWidth ? widthSeen : heightSeen) continue;
            var number = ViewportNumber.Match(match.Groups["value"].Value);
            var dimension = number.Success ? ValidFixedDimension(number.Value) : null;
            if (isWidth) { widthSeen = true; width = dimension; }
            else { heightSeen = true; height = dimension; }
            if (widthSeen && heightSeen) break;
        }
        return (width, height);
    }

    private static double? ValidFixedDimension(string value) =>
        FiniteNumber(value, out var dimension) && dimension is > 0 and <= MaximumFixedDimension ? dimension : null;

    private static bool FiniteNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
}
