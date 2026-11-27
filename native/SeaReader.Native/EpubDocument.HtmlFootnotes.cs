using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using HtmlAgilityPack;

namespace SeaReader.Native;

public sealed partial class EpubDocument
{
    private static string? ReadHtmlFootnote(ZipArchiveEntry entry, string fragment, CancellationToken ct)
    {
        using var stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        const int chunkSize = 64 * 1024;
        for (int offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            stream.ReadExactly(bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)));
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException("EPUB 脚注文档大小与书籍记录不符。");
        var document = ReflowBooks.ParseHtml(bytes, ct);
        foreach (var node in document.DocumentNode.Descendants())
        {
            ct.ThrowIfCancellationRequested();
            if (node.NodeType != HtmlNodeType.Element || !MatchesFragment(node, fragment) ||
                node.AncestorsAndSelf().Any(IgnoreHtmlFootnoteNode)) continue;
            return ExtractHtmlFootnote(node, ct);
        }
        return null;
    }

    private static bool MatchesFragment(HtmlNode node, string fragment) =>
        ReadHtmlAttribute(node, "id") == fragment || ReadHtmlAttribute(node, "xml:id") == fragment ||
        node.Name == "a" && ReadHtmlAttribute(node, "name") == fragment;

    private static string? ReadHtmlAttribute(HtmlNode node, string name) => WebUtility.HtmlDecode(node.Attributes[name]?.Value);

    private static bool IgnoreHtmlFootnoteNode(HtmlNode node) => IgnoredElements.Contains(node.Name) ||
        node.Name is "embed" or "canvas" or "form" || node.Name == "a" &&
        HasFootnoteBacklinkMark(ReadHtmlAttribute(node, "epub:type"), ReadHtmlAttribute(node, "role"), ReadHtmlAttribute(node, "rel"));

    private static string? ExtractHtmlFootnote(HtmlNode target, CancellationToken ct)
    {
        const int excerptLimit = FootnoteLimit + 2;
        var text = new StringBuilder();
        bool pendingSpace = false;
        Append(target);
        if (target.Name == "a")
        {
            // A legacy named anchor starts a note within its parent or immediately before one block.
            for (var sibling = target.NextSibling; sibling != null && text.Length < excerptLimit; sibling = sibling.NextSibling)
            {
                ct.ThrowIfCancellationRequested();
                if (IgnoreHtmlFootnoteNode(sibling)) continue;
                var block = ParagraphElements.Contains(sibling.Name) || sibling.Name is "div" or "aside" or "section" or "article";
                var identified = sibling.Attributes["id"] != null || sibling.Attributes["xml:id"] != null;
                if ((sibling.Name == "a" && (identified || sibling.Attributes["name"] != null)) || (block && identified)) break;
                Append(sibling);
                if (block) break;
            }
        }
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text.Length--;
        return LimitExcerpt(text.ToString().TrimEnd());

        void Append(HtmlNode node)
        {
            ct.ThrowIfCancellationRequested();
            if (text.Length >= excerptLimit || IgnoreHtmlFootnoteNode(node)) return;
            if (node is HtmlTextNode value)
            {
                AppendText(WebUtility.HtmlDecode(value.Text));
                return;
            }
            if (node.NodeType != HtmlNodeType.Element) return;
            if (node.Name is "br" or "hr") Break(1);
            if (node.Name == "img") AppendText(ReadHtmlAttribute(node, "alt") ?? "");
            int spacing = ParagraphElements.Contains(node.Name) ? 2 : node.Name == "div" ? 1 : 0;
            if (spacing != 0) Break(spacing);
            foreach (var child in node.ChildNodes)
            {
                if (text.Length >= excerptLimit) break;
                Append(child);
            }
            if (spacing != 0) Break(spacing);
            else if (node.Name is "td" or "th") pendingSpace = true;
        }

        void AppendText(string value)
        {
            foreach (var character in value)
            {
                if (text.Length >= excerptLimit) break;
                if (char.IsWhiteSpace(character))
                {
                    pendingSpace = true;
                    continue;
                }
                if (pendingSpace && text.Length > 0 && text[^1] != '\n') text.Append(' ');
                pendingSpace = false;
                if (text.Length < excerptLimit) text.Append(character);
            }
        }

        void Break(int count)
        {
            pendingSpace = false;
            if (text.Length == 0) return;
            var existing = 0;
            while (existing < count && existing < text.Length && text[text.Length - 1 - existing] == '\n') existing++;
            text.Append('\n', Math.Min(count - existing, excerptLimit - text.Length));
        }
    }
}
