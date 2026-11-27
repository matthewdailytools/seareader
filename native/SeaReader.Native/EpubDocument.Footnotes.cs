using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SeaReader.Native;

internal enum EpubReferenceKind { Footnote, Glossary, InternalLink }

internal sealed record EpubReference(int Offset, int Length, string Path, string Fragment, EpubReferenceKind Kind,
    int? Chapter = null);

public sealed partial class EpubDocument
{
    private const string EpubNamespace = "http://www.idpf.org/2007/ops";
    private const int FootnoteLimit = 8192;

    internal Task<string?> ReadFootnoteAsync(EpubReference reference, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (reference.Kind != EpubReferenceKind.Footnote ||
                !_textManifestMediaTypes.TryGetValue(reference.Path, out var mediaType) || reference.Fragment.Length == 0) return null;
            using var archive = ZipFile.OpenRead(_path);
            if (archive.GetEntry(reference.Path) is not { Length: <= ChapterLimit } entry) return null;
            return mediaType == "text/html" ? ReadHtmlFootnote(entry, reference.Fragment, ct) :
                ReadXhtml(entry, reference.Path, ct, reader => ExtractFootnote(reader, reference.Fragment, ct));
        }, ct);

    private static string? ExtractFootnote(XmlReader reader, string fragment, CancellationToken ct)
    {
        int ignoredDepth = -1;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (ignoredDepth >= 0)
            {
                if (reader.Depth == ignoredDepth && reader.NodeType == XmlNodeType.EndElement) ignoredDepth = -1;
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (IgnoredElements.Contains(reader.LocalName))
            {
                if (!reader.IsEmptyElement) ignoredDepth = reader.Depth;
                continue;
            }

            bool isAnchor = reader.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase);
            if (reader.GetAttribute("id") != fragment && reader.GetAttribute("id", XNamespace.Xml.NamespaceName) != fragment &&
                (!isAnchor || reader.GetAttribute("name") != fragment)) continue;

            // Older books use an empty named link as the start of the containing note.
            if (isAnchor)
            {
                int anchorDepth = reader.Depth;
                return LimitExcerpt(ExtractChapterText(reader, ct, null, null, excerptLimit: FootnoteLimit + 2,
                    stopBefore: node => EndOfLegacyFootnote(node, anchorDepth)).Text);
            }
            using var subtree = reader.ReadSubtree();
            return LimitExcerpt(ExtractChapterText(subtree, ct, null, null, excerptLimit: FootnoteLimit + 2).Text);
        }
        return null;
    }

    private static bool EndOfLegacyFootnote(XmlReader reader, int depth) =>
        reader.NodeType == XmlNodeType.EndElement &&
            (reader.Depth < depth || reader.Depth == depth &&
             (ParagraphElements.Contains(reader.LocalName) || reader.LocalName.Equals("div", StringComparison.OrdinalIgnoreCase))) ||
        reader.NodeType == XmlNodeType.Element && reader.Depth == depth &&
            reader.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase) &&
            (reader.GetAttribute("name") != null ||
             reader.GetAttribute("id") != null && !IsFootnoteBacklink(reader));

    private static string? LimitExcerpt(string text)
    {
        if (text.Length == 0) return null;
        if (text.Length <= FootnoteLimit) return text;
        var end = char.IsHighSurrogate(text[FootnoteLimit - 1]) ? FootnoteLimit - 1 : FootnoteLimit;
        return string.Concat(text.AsSpan(0, end).TrimEnd(), "…");
    }

    private static bool IsFootnoteBacklink(XmlReader reader) =>
        reader.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase) &&
        HasFootnoteBacklinkMark(reader.GetAttribute("type", EpubNamespace), reader.GetAttribute("role"), reader.GetAttribute("rel"));

    private static bool HasFootnoteBacklinkMark(string? type, string? role, string? relationship) =>
        HasToken(type, "backlink") || HasToken(role, "doc-backlink") || HasToken(relationship, "back");

    private sealed class ReferenceCollector(string chapterPath, int sourceChapter, IReadOnlyDictionary<string, string> textManifestMediaTypes,
        IReadOnlyDictionary<string, int> chaptersByPath)
    {
        private List<EpubReference>? references;
        private (int Depth, EpubReference Reference)? active;

        internal EpubReference[] ToArray() => references?.ToArray() ?? [];

        internal void Clear()
        {
            references?.Clear();
            active = null;
        }

        internal void Begin(XmlReader reader, int offset)
        {
            if (active != null) return;
            var href = reader.GetAttribute("href")?.Trim();
            if (href is not { Length: > 0 and <= 4096 }) return;
            if (ReadKind(reader) is { } kind)
            {
                int separator = href.IndexOf('#');
                if (separator < 0 || separator == href.Length - 1) return;
                var path = ResolvePath(chapterPath, href);
                if (path == null || !textManifestMediaTypes.ContainsKey(path)) return;
                var fragment = Uri.UnescapeDataString(href[(separator + 1)..]);
                if (fragment.Length == 0 || fragment.Length > 1024 || fragment.Contains('\0')) return;
                active = (reader.Depth, new EpubReference(offset, 0, path, fragment, kind));
                return;
            }

            if (!IsFootnoteBacklink(reader) &&
                ReadInternalLink(href, chapterPath, sourceChapter, chaptersByPath) is { } target)
                active = (reader.Depth, new EpubReference(offset, 0, target.Path, target.Fragment,
                    EpubReferenceKind.InternalLink, target.Chapter));
        }

        internal void Complete(int depth, StringBuilder text)
        {
            if (active is not { } pending || depth != pending.Depth) return;
            active = null;
            int start = pending.Reference.Offset;
            int end = text.Length;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (start < end && char.IsWhiteSpace(text[end - 1])) end--;
            if (start < end)
                (references ??= []).Add(pending.Reference with { Offset = start, Length = end - start });
        }

        private static EpubReferenceKind? ReadKind(XmlReader reader)
        {
            var type = reader.GetAttribute("type", EpubNamespace);
            var role = reader.GetAttribute("role");
            if (HasToken(type, "noteref") || HasToken(role, "doc-noteref") ||
                HasToken(reader.GetAttribute("rel"), "footnote")) return EpubReferenceKind.Footnote;
            if (HasToken(type, "glossref") || HasToken(role, "doc-glossref")) return EpubReferenceKind.Glossary;
            var classes = reader.GetAttribute("class");
            return HasToken(classes, "noteref") || HasToken(classes, "footnote-ref") || HasToken(classes, "footnoteref")
                ? EpubReferenceKind.Footnote : null;
        }
    }
}
