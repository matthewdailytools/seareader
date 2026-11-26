using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace SeaReader.Native;

internal static class ReflowBooks
{
    private const long DocumentLimit = 64 * 1024 * 1024;
    private const int MaximumElementDepth = 256;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Regex MarkupEncoding = new(
        """(?:<meta\b[^>]*?\bcharset|<\?xml\b[^>]*?\bencoding)\s*=\s*["']?\s*(?<name>[a-z0-9_-]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static ReflowBooks() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static Task<IChapterBook> LoadAsync(string path, string format, CancellationToken ct = default) => Task.Run<IChapterBook>(() =>
    {
        try
        {
            using var stream = File.OpenRead(path);
            ct.ThrowIfCancellationRequested();
            return format switch
            {
                "fb2" => ReadFictionBook(stream, ct),
                "html" => ReadHtml(stream, ct),
                "docx" => ReadWordDocument(stream, ct),
                _ => throw new InvalidDataException("不支持这种图书格式。")
            };
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("文档的 XML 格式无效。", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("无法识别文档的文字编码。", ex);
        }
    }, ct);

    private static IChapterBook ReadFictionBook(FileStream stream, CancellationToken ct)
    {
        XDocument xml;
        Span<byte> magic = stackalloc byte[4];
        int count = stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        if (count == magic.Length && magic.SequenceEqual("PK\x03\x04"u8))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.Entries.FirstOrDefault(item => item.Length != 0 &&
                !item.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) &&
                item.Name.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("ZIP 中没有 FB2 图书。");
            xml = ReadXml(entry, ct);
        }
        else
        {
            CheckSize(stream.Length);
            xml = ReadXml(stream, ct);
        }

        var root = xml.Root;
        if (root?.Name.LocalName != "FictionBook")
            throw new InvalidDataException("文档缺少 FictionBook 根元素。");
        var titleInfo = Child(Child(root, "description"), "title-info");
        var flow = new ChapterFlow(Compact(Child(titleInfo, "book-title")?.Value));
        foreach (var body in root.Elements().Where(element => element.Name.LocalName == "body"))
        {
            flow.Start((string?)body.Attribute("name"));
            AppendFictionBook(body, flow, 0, ct);
        }
        return flow.Build();
    }

    private static void AppendFictionBook(XElement container, ChapterFlow flow, int depth, CancellationToken ct)
    {
        if (depth >= MaximumElementDepth) throw new InvalidDataException("FB2 章节的嵌套层数过多。");
        foreach (var element in container.Elements())
        {
            ct.ThrowIfCancellationRequested();
            switch (element.Name.LocalName)
            {
                case "title":
                    var title = Compact(element.HasElements ? string.Join(' ', element.Elements().Select(part => part.Value)) : element.Value);
                    if (title.Length != 0)
                    {
                        flow.Start(title);
                        flow.Paragraph(title);
                    }
                    break;
                case "section":
                    flow.Start();
                    AppendFictionBook(element, flow, depth + 1, ct);
                    break;
                case "epigraph" or "cite" or "poem" or "stanza" or "table" or "tr":
                    flow.Break(2);
                    AppendFictionBook(element, flow, depth + 1, ct);
                    flow.Break(2);
                    break;
                case "p" or "subtitle" or "text-author" or "date":
                    flow.Paragraph(element.Value);
                    break;
                case "v":
                    flow.Append(element.Value);
                    flow.Break(1);
                    break;
                case "th" or "td":
                    flow.Append(element.Value);
                    flow.Append(" ");
                    break;
                case "image":
                    if (element.Attribute("alt") is { } alt) flow.Paragraph(alt.Value);
                    break;
                case "empty-line":
                    flow.Break(2);
                    break;
            }
        }
    }

    private static IChapterBook ReadHtml(FileStream stream, CancellationToken ct)
    {
        CheckSize(stream.Length);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        ct.ThrowIfCancellationRequested();
        var document = new HtmlDocument
        {
            OptionMaxNestedChildNodes = MaximumElementDepth,
            OptionFixNestedTags = true,
            OptionUseIdAttribute = false,
            OptionReadEncoding = false
        };
        try { document.LoadHtml(DecodeMarkup(bytes)); }
        catch (InvalidProgramException ex) { throw new InvalidDataException("HTML 的嵌套层数过多。", ex); }
        var title = document.DocumentNode.Descendants().FirstOrDefault(node => HtmlName(node) == "title")?.InnerText;
        var flow = new ChapterFlow(Compact(WebUtility.HtmlDecode(title)));
        var body = document.DocumentNode.Descendants().FirstOrDefault(node => HtmlName(node) == "body");
        AppendHtml(body ?? document.DocumentNode, flow, ct);
        return flow.Build();
    }

    private static string DecodeMarkup(byte[] data)
    {
        var header = data.AsSpan(0, Math.Min(8192, data.Length));
        foreach (var encoding in new[] { Encoding.UTF32, new UTF32Encoding(true, true), Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            var bom = encoding.GetPreamble();
            if (header.StartsWith(bom)) return encoding.GetString(data.AsSpan(bom.Length));
        }
        if (header.StartsWith("<\0"u8)) return Encoding.Unicode.GetString(data);
        if (header.StartsWith("\0<"u8)) return Encoding.BigEndianUnicode.GetString(data);
        var declaration = MarkupEncoding.Match(Encoding.ASCII.GetString(header));
        if (declaration.Success)
        {
            try { return Encoding.GetEncoding(declaration.Groups["name"].Value).GetString(data); }
            catch (ArgumentException) { }
        }
        try { return StrictUtf8.GetString(data); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding("gb18030").GetString(data); }
    }

    private static void AppendHtml(HtmlNode node, ChapterFlow flow, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (node is HtmlTextNode text)
        {
            flow.Append(WebUtility.HtmlDecode(text.Text));
            return;
        }
        var name = HtmlName(node);
        if (node.NodeType is not (HtmlNodeType.Element or HtmlNodeType.Document) || node.Attributes.Contains("hidden") ||
            name is "head" or "script" or "style" or "noscript" or "template" or "iframe" or "object" or "embed" or "svg" or "canvas" or "form" or "nav")
            return;

        if (name is "h1" or "h2" or "h3")
        {
            var title = Compact(WebUtility.HtmlDecode(node.InnerText));
            if (title.Length != 0) flow.Start(title);
        }
        if (name is "br" or "hr") flow.Break(1);
        else if (name == "img") flow.Append(WebUtility.HtmlDecode(node.GetAttributeValue("alt", "")));
        else if (name == "pre")
        {
            flow.AppendPreformatted(WebUtility.HtmlDecode(node.InnerText));
            return;
        }
        int spacing = name switch
        {
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "p" or "section" or "article" or "blockquote" or "figcaption" => 2,
            "div" or "li" or "tr" or "dt" or "dd" => 1,
            _ => 0
        };
        if (spacing != 0) flow.Break(spacing);
        foreach (var child in node.ChildNodes) AppendHtml(child, flow, ct);
        if (spacing != 0) flow.Break(spacing);
        if (name is "th" or "td") flow.Append(" ");
    }

    private static string HtmlName(HtmlNode node) => node.Name[(node.Name.LastIndexOf(':') + 1)..];

    private static IChapterBook ReadWordDocument(FileStream stream, CancellationToken ct)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var document = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("DOCX 缺少正文文件。");
        var root = ReadXml(document, ct).Root;
        var wordNs = root?.Name.Namespace;
        if (root?.Name.LocalName != "document" || wordNs?.NamespaceName is not
            ("http://schemas.openxmlformats.org/wordprocessingml/2006/main" or "http://purl.oclc.org/ooxml/wordprocessingml/main"))
            throw new InvalidDataException("DOCX 正文格式无效。");
        var headingStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Heading1", "Heading2", "Heading3" };
        if (archive.GetEntry("word/styles.xml") is { } styles)
        {
            foreach (var style in ReadXml(styles, ct).Descendants(wordNs + "style"))
            {
                var name = (string?)style.Element(wordNs + "name")?.Attribute(wordNs + "val");
                var level = (string?)style.Element(wordNs + "pPr")?.Element(wordNs + "outlineLvl")?.Attribute(wordNs + "val");
                var id = (string?)style.Attribute(wordNs + "styleId");
                if (id != null && (level is "0" or "1" or "2" ||
                    (name != null && headingStyles.Contains(name.Replace(" ", "", StringComparison.Ordinal)))))
                    headingStyles.Add(id);
            }
        }
        var flow = new ChapterFlow("");
        foreach (var paragraph in root.Element(wordNs + "body")?.Descendants(wordNs + "p") ?? [])
        {
            ct.ThrowIfCancellationRequested();
            if (paragraph.Ancestors(wordNs + "del").Any() || paragraph.Ancestors(wordNs + "p").Any()) continue;
            var value = new StringBuilder();
            foreach (var node in paragraph.Descendants().Where(node => node.Name.Namespace == wordNs))
            {
                if (node.Name.LocalName == "t" && !node.Ancestors(wordNs + "del").Any())
                    value.Append(node.Value);
                else if (node.Name.LocalName == "tab") value.Append('\t');
                else if (node.Name.LocalName is "br" or "cr") value.Append('\n');
            }
            var properties = paragraph.Element(wordNs + "pPr");
            var style = (string?)properties?.Element(wordNs + "pStyle")?.Attribute(wordNs + "val");
            var outline = (string?)properties?.Element(wordNs + "outlineLvl")?.Attribute(wordNs + "val");
            if (outline is "0" or "1" or "2" || style != null && headingStyles.Contains(style))
            {
                var title = Compact(value.ToString());
                if (title.Length != 0) flow.Start(title);
            }
            flow.AppendPreformatted(value.ToString());
        }
        return flow.Build();
    }

    private static XDocument ReadXml(ZipArchiveEntry entry, CancellationToken ct)
    {
        CheckSize(entry.Length);
        using var stream = entry.Open();
        return ReadXml(stream, ct);
    }

    private static XDocument ReadXml(Stream stream, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = DocumentLimit
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        ct.ThrowIfCancellationRequested();
        return document;
    }

    private static void CheckSize(long size)
    {
        if (size > DocumentLimit) throw new InvalidDataException("文档超出 64 MB 的阅读大小限制。");
    }

    private static string Compact(string? value) => string.Join(' ', value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? []);
    private static XElement? Child(XElement? element, string name) => element?.Elements().FirstOrDefault(child => child.Name.LocalName == name);

    private sealed record ChapterText(string Title, string Text);

    private sealed class ParsedBook(IReadOnlyList<ChapterText> contents) : IChapterBook
    {
        public IReadOnlyList<EpubChapter> Chapters { get; } = contents.Select((chapter, index) => new EpubChapter(chapter.Title, (index + 1).ToString())).ToArray();

        public Task<string> ReadChapterAsync(int index, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, contents.Count);
            return Task.FromResult(contents[index].Text);
        }

        public int GetChapterForPosition(string? position) => BookFormats.GetChapterForPosition(position, contents.Count);
    }

    private sealed class ChapterFlow(string defaultTitle)
    {
        private readonly List<ChapterText> chapters = [];
        private readonly StringBuilder text = new();
        private string? heading;
        private bool space;

        internal void Start(string? title = null)
        {
            Finish();
            heading = string.IsNullOrWhiteSpace(title) ? null : title;
        }

        internal void Paragraph(string value)
        {
            Append(value);
            Break(2);
        }

        internal void Append(string value)
        {
            foreach (char character in value)
            {
                if (char.IsWhiteSpace(character))
                {
                    space = true;
                    continue;
                }
                if (space && text.Length != 0 && text[^1] != '\n') text.Append(' ');
                space = false;
                text.Append(character);
            }
        }

        internal void AppendPreformatted(string value)
        {
            Break(2);
            text.Append(value);
            Break(2);
        }

        internal void Break(int count)
        {
            space = false;
            if (text.Length == 0) return;
            int existing = 0;
            while (existing < count && existing < text.Length && text[text.Length - 1 - existing] == '\n') existing++;
            text.Append('\n', count - existing);
        }

        private void Finish()
        {
            var content = text.ToString().Trim();
            if (content.Length != 0)
                chapters.Add(new ChapterText(heading ?? (chapters.Count == 0 && defaultTitle.Length != 0 ? defaultTitle : $"第 {chapters.Count + 1} 章"), content));
            text.Clear();
            space = false;
        }

        internal IChapterBook Build()
        {
            Finish();
            if (chapters.Count == 0) throw new InvalidDataException("文档不含可阅读的正文。");
            return new ParsedBook(chapters.ToArray());
        }
    }
}
