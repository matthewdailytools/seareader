using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace SeaReader.Native;

internal sealed record EpubGlossaryEntry(string Term, string Definition, string Path, string? Fragment);

public sealed partial class EpubDocument
{
    private const int GlossaryTermLimit = 256;
    private const int GlossaryDefinitionLimit = 8192;
    private const int GlossaryGroupLimit = 32;
    private const int GlossaryEntryLimit = 1024;
    private const int GlossaryTextLimit = 1024 * 1024;
    private const long GlossaryScanLimit = 128L * 1024 * 1024;

    private sealed record GlossaryPart(string Text, string? Fragment, bool MatchesTarget);

    internal Task<IReadOnlyList<EpubGlossaryEntry>> ReadGlossaryAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<EpubGlossaryEntry>>(() =>
        {
            using var archive = ZipFile.OpenRead(_path);
            var results = new List<EpubGlossaryEntry>();
            long bytesLeft = GlossaryScanLimit;
            int textLeft = GlossaryTextLimit;
            foreach (var path in _textManifestMediaTypes.Keys)
            {
                ct.ThrowIfCancellationRequested();
                if (archive.GetEntry(path) is not { Length: <= ChapterLimit } entry || entry.Length > bytesLeft) continue;
                bytesLeft -= entry.Length;
                foreach (var definition in ReadGlossaryFile(entry, path, null, ct))
                {
                    textLeft -= definition.Term.Length + definition.Definition.Length;
                    if (textLeft < 0) return results;
                    results.Add(definition);
                    if (results.Count == GlossaryEntryLimit) return results;
                }
            }
            return results;
        }, ct);

    internal Task<EpubGlossaryEntry?> ReadGlossaryDefinitionAsync(EpubReference reference, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (reference.Kind != EpubReferenceKind.Glossary || !_textManifestMediaTypes.ContainsKey(reference.Path) ||
                reference.Fragment is not { Length: > 0 and <= 1024 } || reference.Fragment.Contains('\0')) return null;
            using var archive = ZipFile.OpenRead(_path);
            return archive.GetEntry(reference.Path) is { Length: <= ChapterLimit } entry
                ? ReadGlossaryFile(entry, reference.Path, reference.Fragment, ct).FirstOrDefault() : null;
        }, ct);

    private static List<EpubGlossaryEntry> ReadGlossaryFile(ZipArchiveEntry entry, string path, string? target, CancellationToken ct)
    {
        try { return ReadXhtml(entry, path, ct, reader => ExtractGlossary(reader, path, target, ct)); }
        catch (InvalidDataException) { return []; }
    }

    private static List<EpubGlossaryEntry> ExtractGlossary(XmlReader reader, string path, string? target, CancellationToken ct)
    {
        var entries = new List<EpubGlossaryEntry>();
        var glossaryScopes = new Stack<int>();
        var listScopes = new Stack<int>();
        var terms = new List<GlossaryPart>();
        var definitions = new List<GlossaryPart>();
        int parentDepth = -1;
        int ignoredDepth = -1;
        int textLeft = GlossaryTextLimit;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (SkipGlossaryContent(reader, ref ignoredDepth)) continue;
            var depth = reader.Depth;
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (parentDepth == depth && FinishGroup()) break;
                if (glossaryScopes.TryPeek(out int glossaryDepth) && glossaryDepth == depth) glossaryScopes.Pop();
                if (listScopes.TryPeek(out int listDepth) && listDepth == depth) listScopes.Pop();
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element) continue;

            var type = reader.GetAttribute("type", EpubNamespace);
            var role = reader.GetAttribute("role");
            bool inGlossary = glossaryScopes.Count != 0;
            bool allowImplicit = inGlossary || target != null && listScopes.Count != 0;
            var name = reader.LocalName;
            bool isTerm = HasToken(type, "glossterm") || inGlossary && HasToken(role, "term") ||
                allowImplicit && name.Equals("dt", StringComparison.OrdinalIgnoreCase);
            bool isDefinition = HasToken(type, "glossdef") || inGlossary && HasToken(role, "definition") ||
                allowImplicit && name.Equals("dd", StringComparison.OrdinalIgnoreCase);
            if (!isTerm && !isDefinition)
            {
                if (reader.IsEmptyElement) continue;
                if (HasToken(type, "glossary") || HasToken(role, "doc-glossary")) glossaryScopes.Push(depth);
                if (target != null && name.Equals("dl", StringComparison.OrdinalIgnoreCase)) listScopes.Push(depth);
                continue;
            }

            if ((parentDepth != depth - 1 || isTerm && definitions.Count > 0) && FinishGroup()) break;
            parentDepth = depth - 1;
            if (!isTerm && terms.Count == 0) continue;
            var part = ReadGlossaryPart(reader, isTerm, target, ct);
            var destination = isTerm ? terms : definitions;
            if (destination.Count < GlossaryGroupLimit) destination.Add(part);
            else if (part.MatchesTarget) destination[^1] = part;
        }
        return entries;

        bool FinishGroup()
        {
            parentDepth = -1;
            if (terms.Count != 0 && definitions.Count != 0 && AddGroup()) return true;
            terms.Clear();
            definitions.Clear();
            return false;
        }

        bool AddGroup()
        {
            if (target != null)
            {
                var term = terms.FirstOrDefault(part => part.MatchesTarget && part.Text.Length > 0);
                if (term != null) return AddEntry(term.Text, JoinDefinitions(definitions), target);
                var definition = definitions.FirstOrDefault(part => part.MatchesTarget && part.Text.Length > 0);
                term = terms.FirstOrDefault(part => part.Text.Length > 0);
                return definition != null && term != null && AddEntry(term.Text, definition.Text, target);
            }

            string text = JoinDefinitions(definitions);
            string? fragment = definitions.FirstOrDefault(part => part.Fragment != null)?.Fragment;
            foreach (var term in terms)
                if (AddEntry(term.Text, text, term.Fragment ?? fragment)) return true;
            return false;
        }

        bool AddEntry(string term, string text, string? fragment)
        {
            if (term.Length == 0 || text.Length == 0) return false;
            textLeft -= term.Length + text.Length;
            if (textLeft < 0) return true;
            entries.Add(new EpubGlossaryEntry(term, text, path, fragment));
            return target != null || entries.Count == GlossaryEntryLimit;
        }
    }

    private static GlossaryPart ReadGlossaryPart(XmlReader reader, bool term, string? target, CancellationToken ct)
    {
        using var subtree = reader.ReadSubtree();
        int ignoredDepth = -1;
        string? fragment = null;
        bool matches = false;
        int limit = term ? GlossaryTermLimit : GlossaryDefinitionLimit;
        var text = ExtractChapterText(subtree, ct, null, null, excerptLimit: limit + 2, stopBefore: ObserveAnchor).Text;
        if (subtree.ReadState == ReadState.Interactive) ObserveAnchor(subtree);
        while (subtree.Read())
        {
            ct.ThrowIfCancellationRequested();
            ObserveAnchor(subtree);
        }
        if (term) text = NormalizeNavigationTitle(text);
        return new GlossaryPart(LimitGlossaryText(text, limit), fragment, matches);

        bool ObserveAnchor(XmlReader node)
        {
            if (!SkipGlossaryContent(node, ref ignoredDepth) && node.NodeType == XmlNodeType.Element)
            {
                Remember(node.GetAttribute("id"));
                Remember(node.GetAttribute("id", XNamespace.Xml.NamespaceName));
                if (node.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase)) Remember(node.GetAttribute("name"));
            }
            return false;
        }

        void Remember(string? anchor)
        {
            if (anchor is not { Length: > 0 and <= 1024 } || anchor.Contains('\0')) return;
            fragment ??= anchor;
            matches |= anchor == target;
        }
    }

    private static bool SkipGlossaryContent(XmlReader reader, ref int ignoredDepth)
    {
        if (ignoredDepth >= 0)
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == ignoredDepth) ignoredDepth = -1;
            return true;
        }
        if (reader.NodeType != XmlNodeType.Element ||
            !IgnoredElements.Contains(reader.LocalName) && !IsFootnoteBacklink(reader)) return false;
        if (!reader.IsEmptyElement) ignoredDepth = reader.Depth;
        return true;
    }

    private static string JoinDefinitions(IEnumerable<GlossaryPart> definitions) =>
        LimitGlossaryText(string.Join("\n\n", definitions.Select(part => part.Text).Where(text => text.Length > 0)),
            GlossaryDefinitionLimit);

    private static string LimitGlossaryText(string text, int limit)
    {
        if (text.Length <= limit) return text;
        int end = char.IsHighSurrogate(text[limit - 2]) ? limit - 2 : limit - 1;
        return string.Concat(text.AsSpan(0, end).TrimEnd(), "…");
    }
}
