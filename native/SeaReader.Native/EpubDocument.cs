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

public sealed partial class EpubDocument : IChapterBook
{
    private const long MetadataLimit = 4 * 1024 * 1024;
    private const long ChapterLimit = 16 * 1024 * 1024;
    private static readonly Regex NamedEntity = new(
        @"&[a-zA-Z][a-zA-Z0-9]{1,31};",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    private static readonly Regex LanguageTag = new(
        @"\A[a-zA-Z]{1,8}(?:-[a-zA-Z0-9]{1,8})*\z",
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
    private readonly string?[] _spineIds;
    private readonly EpubLegacyCfiStep _spineElement;
    private readonly string? _publicationLanguage;
    private readonly IReadOnlyDictionary<string, string> _textManifestMediaTypes;
    private readonly IReadOnlyDictionary<string, int> _chaptersByPath;
    private readonly Dictionary<string, ManifestItem> _rasterManifestItems;

    private EpubDocument(string path, EpubChapter[] chapters, int[] spineIndexes, string?[] spineIds,
        IReadOnlyDictionary<string, int> chaptersByPath, EpubLegacyCfiStep spineElement, EpubTocEntry[] navigation,
        EpubPrintedPage[] printedPages, (int Chapter, string Fragment)? readingStart,
        string? publicationLanguage, IReadOnlyDictionary<string, string> textManifestMediaTypes, Dictionary<string, ManifestItem> rasterManifestItems,
        (string Title, string Author) metadata, bool rightToLeftPageProgression, FixedEpubLayout? fixedLayout)
    {
        _path = path;
        Chapters = Array.AsReadOnly(chapters);
        _chaptersByPath = chaptersByPath;
        _spineIndexes = spineIndexes;
        _spineIds = spineIds;
        _spineElement = spineElement;
        _publicationLanguage = publicationLanguage;
        _textManifestMediaTypes = textManifestMediaTypes;
        _rasterManifestItems = rasterManifestItems;
        Navigation = Array.AsReadOnly(navigation);
        PrintedPages = Array.AsReadOnly(printedPages);
        ReadingStart = readingStart;
        PublicationTitle = metadata.Title;
        PublicationAuthor = metadata.Author;
        RightToLeftPageProgression = rightToLeftPageProgression;
        FixedLayout = fixedLayout;
    }

    public IReadOnlyList<EpubChapter> Chapters { get; }
    internal string PublicationTitle { get; }
    internal string PublicationAuthor { get; }
    internal bool RightToLeftPageProgression { get; }

    public static Task<EpubDocument> LoadAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Load(path, ct), ct);

    public Task<string> ReadChapterAsync(int index, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Chapters.Count);
        return Task.Run(() => ReadChapter(Chapters[index], ct).Text, ct);
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
            var (packagePath, package) = ReadPackage(archive);
            var manifest = FindChild(package.Root, "manifest");
            var spine = FindChild(package.Root, "spine");
            if (manifest is null || spine is null) throw new InvalidDataException("EPUB 缺少书籍目录。");

            var items = ReadManifest(manifest, packagePath, ct);
            var textMediaTypes = items.Values.Where(item => item.MediaType is "application/xhtml+xml" or "text/html")
                .GroupBy(item => item.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => group.Any(item => item.MediaType == "application/xhtml+xml") ? "application/xhtml+xml" : "text/html",
                    StringComparer.Ordinal);
            var rasterItems = items.Values.Where(IsRasterImage).DistinctBy(item => item.Path, StringComparer.Ordinal)
                .ToDictionary(item => item.Path, StringComparer.Ordinal);
            var navPath = items.Values.FirstOrDefault(item => HasToken(item.Properties, "nav"))?.Path;
            var ncxId = (string?)spine.Attribute("toc");
            var ncxPath = (ncxId is not null && items.TryGetValue(ncxId, out var ncx)
                ? ncx : items.Values.FirstOrDefault(item => item.MediaType == "application/x-dtbncx+xml"))?.Path;
            var navDocument = TryReadMetadata(archive, navPath);
            var nav = ReadEpubNavigation(navDocument, navPath, ct);
            var ncxNavigation = ReadNcxNavigation(archive, ncxPath, ct);
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in nav.Concat(ncxNavigation))
                if (entry.Path is { } href && entry.Title.Length != 0) labels.TryAdd(href, entry.Title);

            var metadata = FindChild(package.Root, "metadata");
            var rightToLeft = ((string?)spine.Attribute("page-progression-direction"))?.Trim() == "rtl";
            var references = spine.Elements().Where(element => element.Name.LocalName == "itemref").ToArray();
            var fixedLayout = TryReadFixedLayout(path, archive, metadata, references, items, labels, rightToLeft, ct);
            var chapters = new List<EpubChapter>();
            var indexes = new List<int>();
            var spineIds = new List<string?>();
            if (fixedLayout != null)
            {
                foreach (var page in fixedLayout.Pages)
                {
                    chapters.Add(new EpubChapter(page.Title, page.Path));
                    indexes.Add(page.SpineIndex);
                    spineIds.Add(page.SpineId);
                }
            }
            else
            {
                for (int originalIndex = 0; originalIndex < references.Length; originalIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    var reference = references[originalIndex];
                    var id = (string?)reference.Attribute("idref");
                    if (id is not null && items.TryGetValue(id, out var item) &&
                        (item.MediaType is "application/xhtml+xml" or "text/html") && archive.GetEntry(item.Path) is not null)
                    {
                        chapters.Add(new EpubChapter(labels.GetValueOrDefault(item.Path) ?? $"第 {chapters.Count + 1} 章", item.Path));
                        indexes.Add(originalIndex);
                        spineIds.Add((string?)reference.Attribute("id"));
                    }
                }
            }

            if (chapters.Count == 0) throw new InvalidDataException("EPUB 不含可阅读的正文。");
            var chaptersByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < chapters.Count; index++) chaptersByPath.TryAdd(chapters[index].Href, index);
            var navigation = BindNavigation(nav, chapters, chaptersByPath);
            if (navigation.Length == 0) navigation = BindNavigation(ncxNavigation, chapters, chaptersByPath);
            var printedPages = ReadPrintedPages(navDocument, navPath, chaptersByPath, ct);
            var readingStart = fixedLayout == null ? ReadReadingStart(navDocument, navPath, chaptersByPath, ct) : null;
            var publicationLanguage = metadata?.Elements(DublinCore + "language")
                .Select(element => NormalizeLanguageTag(element.Value)).FirstOrDefault(tag => tag != null);
            ct.ThrowIfCancellationRequested();
            var spineElement = new EpubLegacyCfiStep(spine.ElementsBeforeSelf().Count(), (string?)spine.Attribute("id"));
            return new EpubDocument(path, [.. chapters], [.. indexes], [.. spineIds], chaptersByPath, spineElement, navigation,
                printedPages, readingStart,
                publicationLanguage, textMediaTypes, rasterItems, ReadPublicationTitleAndAuthor(metadata), rightToLeft, fixedLayout);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("EPUB 书籍目录格式无效。", ex);
        }
    }

    private (string Text, string? Language) ReadChapter(EpubChapter chapter, CancellationToken ct, Dictionary<string, int>? anchors = null,
        List<TextStyleSpan>? styles = null, ReferenceCollector? references = null, EpubLegacyAnchor? legacyAnchor = null,
        IllustrationCollector? illustrations = null)
    {
        using var archive = ZipFile.OpenRead(_path);
        var entry = GetEntry(archive, chapter.Href, ChapterLimit);
        var result = ReadXhtml(entry, chapter.Title, ct,
            reader => ExtractChapterText(reader, ct, anchors, styles, references, legacyAnchor: legacyAnchor, illustrations: illustrations),
            ClearMetadata);
        illustrations?.ReadImages(archive, result.Text.Length, ct);
        return result;

        void ClearMetadata()
        {
            anchors?.Clear();
            styles?.Clear();
            references?.Clear();
            legacyAnchor?.Reset();
            illustrations?.Clear();
        }
    }

    private static T ReadXhtml<T>(ZipArchiveEntry entry, string title, CancellationToken ct, Func<XmlReader, T> extract,
        Action? clearMetadata = null)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, XmlSettings(ChapterLimit));
            return extract(reader);
        }
        catch (XmlException)
        {
            clearMetadata?.Invoke();
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
            try { return extract(reader); }
            catch (XmlException ex)
            {
                throw new InvalidDataException($"EPUB 章节格式无效或受保护。{title}", ex);
            }
        }
    }

    private static (string Text, string? Language) ExtractChapterText(XmlReader reader, CancellationToken ct, Dictionary<string, int>? anchors,
        List<TextStyleSpan>? styles, ReferenceCollector? references = null, int excerptLimit = int.MaxValue,
        Func<XmlReader, bool>? stopBefore = null, EpubLegacyAnchor? legacyAnchor = null, IllustrationCollector? illustrations = null)
    {
        var text = new StringBuilder();
        var pendingAnchors = anchors == null ? null : new List<string>();
        Stack<(int Depth, TextEmphasis Parent)>? styleStack = null;
        var emphasis = TextEmphasis.None;
        var spaceEmphasis = TextEmphasis.None;
        string? language = null;
        bool pendingSpace = false;
        bool pendingLegacyOffset = false;
        int ignoredDepth = -1;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (text.Length >= excerptLimit || stopBefore?.Invoke(reader) == true) break;
            legacyAnchor?.Observe(reader);
            if (ignoredDepth >= 0)
            {
                if (reader.Depth == ignoredDepth && reader.NodeType == XmlNodeType.EndElement) ignoredDepth = -1;
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                string name = reader.LocalName;
                if (IgnoredElements.Contains(name) || excerptLimit != int.MaxValue && IsFootnoteBacklink(reader))
                {
                    if (!reader.IsEmptyElement) ignoredDepth = reader.Depth;
                    continue;
                }
                if (styles != null &&
                    (reader.Depth == 0 && name.Equals("html", StringComparison.OrdinalIgnoreCase) ||
                     reader.Depth <= 1 && name.Equals("body", StringComparison.OrdinalIgnoreCase)))
                    language = ReadChapterLanguage(reader) ?? language;
                if (references != null && name.Equals("a", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement)
                    references.Begin(reader, text.Length);
                if (anchors != null)
                {
                    AddAnchor(reader.GetAttribute("id"));
                    AddAnchor(reader.GetAttribute("id", "http://www.w3.org/XML/1998/namespace"));
                    if (name.Equals("a", StringComparison.OrdinalIgnoreCase)) AddAnchor(reader.GetAttribute("name"));
                }
                if (styles != null && !reader.IsEmptyElement && EmphasisElements.TryGetValue(name, out var added) &&
                    (emphasis & added) == TextEmphasis.None)
                {
                    (styleStack ??= new()).Push((reader.Depth, emphasis));
                    emphasis |= added;
                }
                if (name.Equals("br", StringComparison.OrdinalIgnoreCase)) AddBreak(1);
                else if (ParagraphElements.Contains(name)) AddBreak(2);
                else if (name.Equals("div", StringComparison.OrdinalIgnoreCase)) AddBreak(1);
                else if (name.Equals("img", StringComparison.OrdinalIgnoreCase))
                {
                    var alternative = reader.GetAttribute("alt");
                    int offset = text.Length;
                    if (alternative != null) AddText(alternative);
                    if (offset < text.Length && text[offset] == ' ') offset++;
                    illustrations?.Add(reader.GetAttribute("src"), offset, alternative);
                }
                else if (illustrations != null && name == "image" && reader.NamespaceURI == Svg.NamespaceName)
                {
                    illustrations.Add(reader.GetAttribute("href") ?? reader.GetAttribute("href", XLink.NamespaceName),
                        text.Length, reader.GetAttribute("aria-label"));
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                references?.Complete(reader.Depth, text);
                if (styleStack?.Count > 0 && styleStack.Peek().Depth == reader.Depth)
                    emphasis = styleStack.Pop().Parent;
                if (ParagraphElements.Contains(reader.LocalName)) AddBreak(2);
                else if (reader.LocalName.Equals("div", StringComparison.OrdinalIgnoreCase)) AddBreak(1);
                else if (reader.LocalName.Equals("td", StringComparison.OrdinalIgnoreCase)) QueueSpace();
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                AddText(reader.Value);
            }
        }

        var result = text.ToString().TrimEnd();
        if (excerptLimit != int.MaxValue && result.Length > 0 && char.IsHighSurrogate(result[^1])) result = result[..^1];
        SetPendingOffsets(result.Length);
        if (pendingLegacyOffset) legacyAnchor!.Resolve(result.Length);
        return (result, language);

        void AddAnchor(string? id)
        {
            if (!string.IsNullOrEmpty(id) && anchors!.TryAdd(id, 0)) pendingAnchors!.Add(id);
        }

        void SetPendingOffsets(int offset)
        {
            if (pendingAnchors == null) return;
            foreach (var id in pendingAnchors) anchors![id] = offset;
            pendingAnchors.Clear();
        }

        void AddText(string value)
        {
            if (legacyAnchor?.TakeCharacterBoundary(value) is int offset)
            {
                AddTextCharacters(value.AsSpan(0, offset));
                pendingLegacyOffset = true;
                AddTextCharacters(value.AsSpan(offset));
            }
            else AddTextCharacters(value);
        }

        void AddTextCharacters(ReadOnlySpan<char> value)
        {
            if (pendingLegacyOffset)
            {
                // A raw boundary inside collapsed whitespace resolves to the next visible glyph.
                var visible = value.TrimStart();
                if (!visible.IsEmpty)
                {
                    bool addSpace = (pendingSpace || visible.Length < value.Length) && text.Length > 0 && text[^1] != '\n';
                    legacyAnchor!.Resolve(text.Length + (addSpace ? 1 : 0));
                    pendingLegacyOffset = false;
                }
            }
            bool resolveAnchors = pendingAnchors is { Count: > 0 };
            int start = text.Length;
            foreach (var character in value)
            {
                if (text.Length >= excerptLimit) break;
                if (char.IsWhiteSpace(character))
                {
                    QueueSpace();
                    continue;
                }
                if (pendingSpace && text.Length > 0 && text[^1] != '\n')
                {
                    if (spaceEmphasis != emphasis)
                    {
                        // A collapsed space before inline text can belong to its preceding element.
                        AddStyle(text.Length, 1, spaceEmphasis);
                        start = text.Length + 1;
                    }
                    text.Append(' ');
                }
                if (resolveAnchors)
                {
                    SetPendingOffsets(text.Length);
                    resolveAnchors = false;
                }
                pendingSpace = false;
                if (text.Length < excerptLimit) text.Append(character);
            }
            AddStyle(start, text.Length - start, emphasis);
        }

        void QueueSpace()
        {
            if (!pendingSpace) spaceEmphasis = emphasis;
            pendingSpace = true;
        }

        void AddStyle(int start, int length, TextEmphasis style)
        {
            if (styles == null || style == TextEmphasis.None || length == 0) return;
            if (styles.Count > 0 && styles[^1] is var previous && previous.Emphasis == style &&
                previous.Offset + previous.Length == start)
            {
                styles[^1] = new TextStyleSpan(previous.Offset, previous.Length + length, style);
            }
            else styles.Add(new TextStyleSpan(start, length, style));
        }

        void AddBreak(int count)
        {
            pendingSpace = false;
            if (text.Length == 0) return;
            int existing = 0;
            while (existing < text.Length && existing < count && text[text.Length - 1 - existing] == '\n') existing++;
            text.Append('\n', Math.Min(count - existing, excerptLimit - text.Length));
        }
    }

    private static string? ReadChapterLanguage(XmlReader reader)
    {
        var xmlLanguage = reader.GetAttribute("lang", XNamespace.Xml.NamespaceName);
        // An empty xml:lang explicitly resets an inherited language.
        if (xmlLanguage == "") return "und";
        return NormalizeLanguageTag(xmlLanguage) ?? NormalizeLanguageTag(reader.GetAttribute("lang"));
    }

    private static string? NormalizeLanguageTag(string? value)
    {
        var tag = value?.Trim();
        if (string.IsNullOrEmpty(tag) || tag.Length > 84 || !LanguageTag.IsMatch(tag)) return null;
        return tag;
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

    private static (string Path, XDocument Package) ReadPackage(ZipArchive archive)
    {
        var container = ReadMetadata(archive, "META-INF/container.xml");
        var packagePath = container.Descendants()
            .Where(element => element.Name.LocalName == "rootfile")
            .Select(element => ResolvePath("", (string?)element.Attribute("full-path")))
            .FirstOrDefault(entry => entry is not null && archive.GetEntry(entry) is not null);
        if (packagePath is null) throw new InvalidDataException("EPUB 缺少有效的书籍描述文件。");
        return (packagePath, ReadMetadata(archive, packagePath));
    }

    private static Dictionary<string, ManifestItem> ReadManifest(XElement manifest, string packagePath, CancellationToken ct)
    {
        var items = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        foreach (var item in manifest.Elements().Where(element => element.Name.LocalName == "item"))
        {
            ct.ThrowIfCancellationRequested();
            var id = (string?)item.Attribute("id");
            var path = ResolvePath(packagePath, (string?)item.Attribute("href"));
            if (string.IsNullOrWhiteSpace(id) || path is null) continue;
            items.TryAdd(id, new ManifestItem(path, ((string?)item.Attribute("media-type"))?.Trim().ToLowerInvariant() ?? "",
                (string?)item.Attribute("properties")));
        }
        return items;
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

    private sealed record ManifestItem(string Path, string MediaType, string? Properties);
}
