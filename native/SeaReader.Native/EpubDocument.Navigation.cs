using System.IO.Compression;
using System.Xml.Linq;

namespace SeaReader.Native;

internal sealed record EpubTocEntry(string Title, int? Chapter, string? Fragment, int Depth);
internal sealed record EpubPrintedPage(string Label, int Chapter, string Fragment, string? LiteralFragment);

public sealed partial class EpubDocument
{
    private sealed record NavigationLink(string Title, string? Path, string? Fragment, int Depth);
    private static readonly XName EpubType = XName.Get("type", "http://www.idpf.org/2007/ops");
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private const string ReaderStartLandmark = "ibooks:reader-start-page";

    internal IReadOnlyList<EpubTocEntry> Navigation { get; }
    internal IReadOnlyList<EpubPrintedPage> PrintedPages { get; }
    internal (int Chapter, string Fragment)? ReadingStart { get; }

    internal Task<IReadOnlyDictionary<string, int>> ReadChapterAnchorsAsync(int index, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Chapters.Count);
        return Task.Run<IReadOnlyDictionary<string, int>>(() =>
        {
            var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
            ReadChapter(Chapters[index], ct, anchors);
            return anchors;
        }, ct);
    }

    private static List<NavigationLink> ReadEpubNavigation(XDocument? nav, string? path, CancellationToken ct)
    {
        if (path == null || nav == null) return [];
        var toc = nav.Descendants().FirstOrDefault(element => element.Name.LocalName == "nav" &&
            element.Attributes().Any(attribute => attribute.Name.LocalName == "type" && HasToken(attribute.Value, "toc")));
        if (toc == null) return [];

        var result = new List<NavigationLink>();
        foreach (var (item, depth) in TraverseNavigation(toc, ChildItems, ct))
        {
            var label = item.Elements().FirstOrDefault(element => element.Name.LocalName is "a" or "span");
            var href = label?.Name.LocalName == "a" ? (string?)label.Attribute("href") : null;
            result.Add(ReadNavigationLink(path, href, ReadNavigationTitle(label), depth));
        }
        return result;

        static IEnumerable<XElement> ChildItems(XElement item) => item.Elements()
            .Where(element => element.Name.LocalName is "ol" or "ul")
            .SelectMany(list => list.Elements().Where(element => element.Name.LocalName == "li"));
    }

    private static EpubPrintedPage[] ReadPrintedPages(XDocument? nav, string? path,
        IReadOnlyDictionary<string, int> chaptersByPath, CancellationToken ct)
    {
        if (path == null || nav?.Descendants(Xhtml + "nav").FirstOrDefault(element =>
            HasToken((string?)element.Attribute(EpubType), "page-list")) is not { } pageList) return [];

        var result = new List<EpubPrintedPage>();
        foreach (var (item, _) in TraverseNavigation(pageList, ChildItems, ct))
        {
            var link = item.Element(Xhtml + "a");
            if ((string?)link?.Attribute("href") is not { } href ||
                ReadStrictLinkDestination(href, path) is not { } target ||
                !chaptersByPath.TryGetValue(target.Path, out var chapter)) continue;

            var label = ReadNavigationTitle(link);
            if (label.Length != 0)
                result.Add(new EpubPrintedPage(label, chapter, target.Fragment,
                    target.Literal == target.Fragment ? null : target.Literal));
        }
        return result.ToArray();

        static IEnumerable<XElement> ChildItems(XElement item) => item.Elements(Xhtml + "ol")
            .SelectMany(list => list.Elements(Xhtml + "li"));
    }

    private static (int Chapter, string Fragment)? ReadReadingStart(XDocument? nav, string? path,
        IReadOnlyDictionary<string, int> chaptersByPath, CancellationToken ct)
    {
        if (path == null || nav?.Descendants().FirstOrDefault(element => element.Name.LocalName == "nav" &&
            HasToken((string?)element.Attribute(EpubType), "landmarks")) is not { } landmarks) return null;

        (int Chapter, string Fragment)? fallback = null;
        foreach (var link in landmarks.Descendants().Where(element => element.Name.LocalName == "a"))
        {
            ct.ThrowIfCancellationRequested();
            if (link.Attribute(EpubType) is not { } type || (string?)link.Attribute("href") is not { } href) continue;
            var types = type.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var specified = types.Contains(ReaderStartLandmark, StringComparer.Ordinal);
            if (!specified && !types.Any(token => token is "bodymatter" or "acknowledgements" or "dedication" or
                "epigraph" or "foreword" or "preface" or "introduction" or "frontmatter")) continue;

            if (ReadStrictLinkDestination(href, path) is not { } target ||
                !chaptersByPath.TryGetValue(target.Path, out var chapter)) continue;

            if (specified) return (chapter, target.Fragment);
            if (fallback == null || chapter < fallback.Value.Chapter) fallback = (chapter, target.Fragment);
        }
        return fallback;
    }

    private static List<NavigationLink> ReadNcxNavigation(ZipArchive archive, string? path, CancellationToken ct)
    {
        if (path == null || FindChild(TryReadMetadata(archive, path)?.Root, "navMap") is not { } toc) return [];
        var result = new List<NavigationLink>();
        foreach (var (item, depth) in TraverseNavigation(toc,
                     parent => parent.Elements().Where(element => element.Name.LocalName == "navPoint"), ct))
        {
            var title = NormalizeNavigationTitle(FindChild(FindChild(item, "navLabel"), "text")?.Value);
            result.Add(ReadNavigationLink(path, (string?)FindChild(item, "content")?.Attribute("src"), title, depth));
        }
        return result;
    }

    private static IEnumerable<(XElement Item, int Depth)> TraverseNavigation(XElement root,
        Func<XElement, IEnumerable<XElement>> childItems, CancellationToken ct)
    {
        var pending = new Stack<(XElement Item, int Depth)>();
        PushChildren(root, 0);
        while (pending.TryPop(out var entry))
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
            PushChildren(entry.Item, entry.Depth + 1);
        }

        void PushChildren(XElement parent, int depth)
        {
            foreach (var child in childItems(parent).Reverse()) pending.Push((child, depth));
        }
    }

    private static string NormalizeNavigationTitle(string? title) =>
        string.Join(' ', title?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? []);

    private static string ReadNavigationTitle(XElement? label)
    {
        if (label == null) return "";
        var title = NormalizeNavigationTitle(string.Concat(label.DescendantNodes().Select(node => node switch
        {
            XText text => text.Value,
            XElement element when element.Name.LocalName == "img" =>
                (string?)element.Attribute("alt") ?? (string?)element.Attribute("title"),
            _ => null
        })));
        return title.Length == 0 ? NormalizeNavigationTitle((string?)label.Attribute("title")) : title;
    }

    private static NavigationLink ReadNavigationLink(string from, string? href, string title, int depth)
    {
        var path = ResolvePath(from, href);
        int start = href?.IndexOf('#') ?? -1;
        string? fragment = null;
        if (path != null && href != null && start >= 0 && start < href.Length - 1)
            fragment = Uri.UnescapeDataString(href[(start + 1)..]);
        return new NavigationLink(title, path, fragment, depth);
    }

    private static EpubTocEntry[] BindNavigation(List<NavigationLink> entries, IReadOnlyList<EpubChapter> chapters,
        IReadOnlyDictionary<string, int> chaptersByPath)
    {
        if (entries.Count == 0) return [];

        var result = new List<EpubTocEntry>();
        var headings = new List<EpubTocEntry>();
        foreach (var entry in entries)
        {
            while (headings.Count != 0 && headings[^1].Depth >= entry.Depth) headings.RemoveAt(headings.Count - 1);
            if (entry.Path != null && chaptersByPath.TryGetValue(entry.Path, out var chapter))
            {
                result.AddRange(headings);
                headings.Clear();
                var title = entry.Title.Length == 0 ? chapters[chapter].Title : entry.Title;
                result.Add(new EpubTocEntry(title, chapter, entry.Fragment, entry.Depth));
            }
            else if (entry.Title.Length != 0)
                headings.Add(new EpubTocEntry(entry.Title, null, null, entry.Depth));
        }
        return result.ToArray();
    }
}
