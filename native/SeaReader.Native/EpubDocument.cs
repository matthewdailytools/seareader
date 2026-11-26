using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace SeaReader.Native;

public sealed record EpubChapter(string Title, string Href);

public sealed class EpubDocument : IChapterBook
{
    private const long MetadataLimit = 4 * 1024 * 1024;
    private const long ChapterLimit = 16 * 1024 * 1024;
    private static readonly Regex NamedEntity = new(
        @"&[a-zA-Z][a-zA-Z0-9]{1,31};",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> IgnoredElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "head", "script", "style", "noscript", "template", "iframe", "object"
    };
    private static readonly HashSet<string> ParagraphElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "h1", "h2", "h3", "h4", "h5", "h6", "p", "li", "blockquote", "figure", "figcaption", "pre", "tr"
    };

    private readonly string _path;
    private readonly int[] _spineIndexes;

    private EpubDocument(string path, EpubChapter[] chapters, int[] spineIndexes)
    {
        _path = path;
        Chapters = Array.AsReadOnly(chapters);
        _spineIndexes = spineIndexes;
    }

    public IReadOnlyList<EpubChapter> Chapters { get; }

    public static Task<EpubDocument> LoadAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Load(path, ct), ct);

    public Task<string> ReadChapterAsync(int index, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Chapters.Count);
        return Task.Run(() => ReadChapter(Chapters[index], ct), ct);
    }

    public int GetChapterForPosition(string? position)
    {
        if (string.IsNullOrWhiteSpace(position) || position.Length > 4096) return 0;
        if (position[0] == '{' || position.StartsWith("native-epub:", StringComparison.Ordinal))
            return BookFormats.GetChapterForPosition(position, Chapters.Count);

        // epub.js stores the spine index as the second even step in the CFI.
        if (!position.StartsWith("epubcfi(/", StringComparison.Ordinal)) return 0;
        var spineStep = position.AsSpan(9);
        int slash = spineStep.IndexOf('/');
        if (slash < 0) return 0;
        spineStep = spineStep[(slash + 1)..];
        int digits = 0;
        while (digits < spineStep.Length && char.IsAsciiDigit(spineStep[digits])) digits++;
        if (!int.TryParse(spineStep[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var step) ||
            step < 2 || step % 2 != 0)
            return 0;

        int chapterIndex = Array.BinarySearch(_spineIndexes, (step / 2) - 1);
        return chapterIndex >= 0 ? chapterIndex : Math.Min(~chapterIndex, Chapters.Count - 1);
    }

    private static EpubDocument Load(string path, CancellationToken ct)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var container = ReadMetadata(archive, "META-INF/container.xml");
            var packagePath = container.Descendants()
                .Where(element => element.Name.LocalName == "rootfile")
                .Select(element => ResolvePath("", (string?)element.Attribute("full-path")))
                .FirstOrDefault(entry => entry is not null && archive.GetEntry(entry) is not null);
            if (packagePath is null) throw new InvalidDataException("EPUB 缺少有效的书籍描述文件。");

            var package = ReadMetadata(archive, packagePath);
            var manifest = FindChild(package.Root, "manifest");
            var spine = FindChild(package.Root, "spine");
            if (manifest is null || spine is null) throw new InvalidDataException("EPUB 缺少书籍目录。");

            var items = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
            foreach (var item in manifest.Elements().Where(element => element.Name.LocalName == "item"))
            {
                ct.ThrowIfCancellationRequested();
                var id = (string?)item.Attribute("id");
                var entry = ResolvePath(packagePath, (string?)item.Attribute("href"));
                if (id is null || entry is null) continue;
                items.TryAdd(id, new ManifestItem(entry,
                    (string?)item.Attribute("media-type") ?? "",
                    HasToken((string?)item.Attribute("properties"), "nav")));
            }

            var navPath = items.Values.FirstOrDefault(item => item.IsNavigation)?.Path;
            var ncxId = (string?)spine.Attribute("toc");
            var ncxPath = (ncxId is not null && items.TryGetValue(ncxId, out var ncx)
                ? ncx : items.Values.FirstOrDefault(item => item.MediaType == "application/x-dtbncx+xml"))?.Path;
            var labels = ReadNavigation(archive, navPath, ncxPath, ct);
            var chapters = new List<EpubChapter>();
            var indexes = new List<int>();
            int originalIndex = 0;
            foreach (var reference in spine.Elements().Where(element => element.Name.LocalName == "itemref"))
            {
                ct.ThrowIfCancellationRequested();
                var id = (string?)reference.Attribute("idref");
                if (id is not null && items.TryGetValue(id, out var item) &&
                    (item.MediaType is "application/xhtml+xml" or "text/html") && archive.GetEntry(item.Path) is not null)
                {
                    chapters.Add(new EpubChapter(labels.GetValueOrDefault(item.Path) ?? $"第 {chapters.Count + 1} 章", item.Path));
                    indexes.Add(originalIndex);
                }
                originalIndex++;
            }

            if (chapters.Count == 0) throw new InvalidDataException("EPUB 不含可阅读的正文。");
            return new EpubDocument(path, [.. chapters], [.. indexes]);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("EPUB 书籍目录格式无效。", ex);
        }
    }

    private string ReadChapter(EpubChapter chapter, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(_path);
        var entry = GetEntry(archive, chapter.Href, ChapterLimit);
        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, XmlSettings(ChapterLimit));
            return ExtractChapterText(reader, ct);
        }
        catch (XmlException)
        {
            // Some EPUB 2 XHTML relies on the external DTD for names such as &nbsp;.
            // Translate known names locally, keeping DTD and external resources disabled.
            using var stream = entry.Open();
            using var decoded = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var source = new StringBuilder();
            var buffer = new char[8192];
            for (int count; (count = decoded.Read(buffer)) > 0;)
            {
                ct.ThrowIfCancellationRequested();
                if (source.Length + count > ChapterLimit) throw new InvalidDataException("EPUB 章节超出阅读大小限制。");
                source.Append(buffer, 0, count);
            }
            var xml = source.ToString();
            xml = NamedEntity.Replace(xml, match =>
                match.Value is "&amp;" or "&lt;" or "&gt;" or "&quot;" or "&apos;"
                    ? match.Value : WebUtility.HtmlDecode(match.Value));
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, XmlSettings(ChapterLimit));
            try { return ExtractChapterText(reader, ct); }
            catch (XmlException ex)
            {
                throw new InvalidDataException($"EPUB 章节格式无效或受保护。{chapter.Title}", ex);
            }
        }
    }

    private static string ExtractChapterText(XmlReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        bool pendingSpace = false;
        int ignoredDepth = -1;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (ignoredDepth >= 0)
            {
                if (reader.Depth == ignoredDepth && reader.NodeType == XmlNodeType.EndElement) ignoredDepth = -1;
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                string name = reader.LocalName;
                if (IgnoredElements.Contains(name))
                {
                    if (!reader.IsEmptyElement) ignoredDepth = reader.Depth;
                    continue;
                }
                if (name.Equals("br", StringComparison.OrdinalIgnoreCase)) AddBreak(1);
                else if (ParagraphElements.Contains(name)) AddBreak(2);
                else if (name.Equals("div", StringComparison.OrdinalIgnoreCase)) AddBreak(1);
                else if (name.Equals("img", StringComparison.OrdinalIgnoreCase) && reader.GetAttribute("alt") is { } alternative)
                    AddText(alternative);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (ParagraphElements.Contains(reader.LocalName)) AddBreak(2);
                else if (reader.LocalName.Equals("div", StringComparison.OrdinalIgnoreCase)) AddBreak(1);
                else if (reader.LocalName.Equals("td", StringComparison.OrdinalIgnoreCase)) pendingSpace = true;
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                AddText(reader.Value);
            }
        }

        return text.ToString().TrimEnd();

        void AddText(string value)
        {
            foreach (var character in value)
            {
                if (char.IsWhiteSpace(character))
                {
                    pendingSpace = true;
                    continue;
                }
                if (pendingSpace && text.Length > 0 && text[^1] != '\n') text.Append(' ');
                pendingSpace = false;
                text.Append(character);
            }
        }

        void AddBreak(int count)
        {
            pendingSpace = false;
            if (text.Length == 0) return;
            int existing = 0;
            while (existing < text.Length && existing < count && text[text.Length - 1 - existing] == '\n') existing++;
            text.Append('\n', count - existing);
        }
    }

    private static Dictionary<string, string> ReadNavigation(
        ZipArchive archive, string? navPath, string? ncxPath, CancellationToken ct)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryReadMetadata(archive, navPath) is { } nav)
        {
            var toc = nav.Descendants().FirstOrDefault(element => element.Name.LocalName == "nav" &&
                element.Attributes().Any(attribute => attribute.Name.LocalName == "type" && HasToken(attribute.Value, "toc")));
            foreach (var link in toc?.Descendants().Where(element => element.Name.LocalName == "a") ?? [])
                Add(navPath!, (string?)link.Attribute("href"), link.Value);
        }

        if (TryReadMetadata(archive, ncxPath) is { } ncx)
        {
            foreach (var point in ncx.Descendants().Where(element => element.Name.LocalName == "navPoint"))
            {
                Add(ncxPath!, (string?)FindChild(point, "content")?.Attribute("src"),
                    FindChild(FindChild(point, "navLabel"), "text")?.Value);
            }
        }
        return titles;

        void Add(string containerPath, string? href, string? label)
        {
            ct.ThrowIfCancellationRequested();
            if (ResolvePath(containerPath, href) is { } path && !string.IsNullOrWhiteSpace(label))
                titles.TryAdd(path, string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        }
    }

    private static XDocument? TryReadMetadata(ZipArchive archive, string? path)
    {
        if (path is null) return null;
        try { return ReadMetadata(archive, path); }
        catch (Exception ex) when (ex is XmlException or InvalidDataException) { return null; }
    }

    private static XDocument ReadMetadata(ZipArchive archive, string path)
    {
        using var stream = GetEntry(archive, path, MetadataLimit).Open();
        using var reader = XmlReader.Create(stream, XmlSettings(MetadataLimit));
        return XDocument.Load(reader);
    }

    private static ZipArchiveEntry GetEntry(ZipArchive archive, string path, long limit)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException("EPUB 包中缺少章节或目录文件。");
        if (entry.Length > limit) throw new InvalidDataException("EPUB 章节或目录文件超出阅读大小限制。");
        return entry;
    }

    private static XmlReaderSettings XmlSettings(long limit) => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        MaxCharactersInDocument = limit
    };

    private static XElement? FindChild(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    private static bool HasToken(string? value, string token) =>
        value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.Ordinal) == true;

    private static string? ResolvePath(string from, string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        int fragment = href.IndexOfAny(['#', '?']);
        if (fragment >= 0) href = href[..fragment];
        if (href.Length == 0) return from;
        if (href.StartsWith('/') || href.Contains(':') || href.Contains('\\')) return null;

        var segments = from.Split('/').SkipLast(1).ToList();
        foreach (var component in href.Split('/'))
        {
            var segment = Uri.UnescapeDataString(component);
            if (segment.Contains('/') || segment.Contains('\\') || segment.Contains('\0')) return null;
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        return segments.Count > 0 ? string.Join('/', segments) : null;
    }

    private sealed record ManifestItem(string Path, string MediaType, bool IsNavigation);
}
