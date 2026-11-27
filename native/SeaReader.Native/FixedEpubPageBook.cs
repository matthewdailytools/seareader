using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using HtmlAgilityPack;

namespace SeaReader.Native;

internal sealed partial class FixedEpubPageBook(EpubDocument publication, FixedEpubLayout layout,
    FixedEpubRenderer renderer) : IPageBook
{
    private const int PageCacheCapacity = 24;
    private static readonly PdfTextPage EmptyTextPage = new("", []);
    private static readonly PageNavigation EmptyNavigation = new([], new Dictionary<string, Rect>(StringComparer.Ordinal));
    private readonly Dictionary<int, LinkedListNode<PageDetails>> detailsByPage = [];
    private readonly LinkedList<PageDetails> recentPages = [];

    private sealed record PageNavigation(IReadOnlyList<FixedEpubReaderLink> Links, IReadOnlyDictionary<string, Rect> Anchors);
    private sealed record PageDetails(int Page, PdfTextPage? Text, PageNavigation? Navigation);

    internal EpubDocument Publication { get; } = publication;
    internal FixedEpubLayout Layout { get; } = layout;
    public int PageCount => Layout.Pages.Count;

    public Task<Size> ReadPageSizeAsync(int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Layout.Pages[index].Size);
    }

    public async Task<BitmapSource> RenderPageAsync(int index, int pixelWidth, CancellationToken token)
    {
        var page = Layout.Pages[index];
        if (GetCachedPage(index) is { Text: not null, Navigation: not null })
            return await renderer.CaptureAsync(Layout, page, pixelWidth, token);

        var frame = await renderer.CapturePageAsync(Layout, page, pixelWidth, token);
        token.ThrowIfCancellationRequested();
        CachePage(index, frame.Text ?? EmptyTextPage, ResolveNavigation(index, frame.Navigation));
        return frame.Bitmap;
    }

    public Task<BitmapSource> RenderThumbnailAsync(int index, CancellationToken token) =>
        renderer.CaptureAsync(Layout, Layout.Pages[index], 192, token);

    public async Task<PdfTextPage> ReadTextPageAsync(int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (GetCachedPage(index)?.Text is { } cached) return cached;
        var text = await renderer.CaptureTextAsync(Layout, Layout.Pages[index], token);
        token.ThrowIfCancellationRequested();
        return CachePage(index, text ?? EmptyTextPage).Text!;
    }

    internal IReadOnlyList<FixedEpubReaderLink> ReadLinks(int index) => GetCachedPage(index)?.Navigation?.Links ?? [];

    internal async Task<Rect?> FindAnchorAsync(FixedEpubTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var navigation = GetCachedPage(target.Page)?.Navigation;
        if (navigation == null)
        {
            var raw = await renderer.CaptureNavigationAsync(Layout, Layout.Pages[target.Page], token);
            token.ThrowIfCancellationRequested();
            navigation = CachePage(target.Page, navigation: ResolveNavigation(target.Page, raw)).Navigation!;
        }
        if (target.LiteralFragment is { } literal && navigation.Anchors.TryGetValue(literal, out var original)) return original;
        return navigation.Anchors.TryGetValue(target.Fragment, out var anchor) ? anchor : null;
    }

    public async Task<IReadOnlyList<PdfTextHit>> SearchAsync(string query, IReadOnlyDictionary<int, string> recognized,
        int limit, CancellationToken token, Func<IReadOnlyList<PdfTextHit>, Task>? publish = null)
    {
        var hits = new List<PdfTextHit>();
        var term = WhiteSpace().Replace(query, "").Normalize();
        if (term.Length == 0) return hits;
        for (var index = 0; index < PageCount && hits.Count < limit; index++)
        {
            token.ThrowIfCancellationRequested();
            var text = GetCachedPage(index)?.Text;
            if (text == null)
            {
                // Interleaved markup can split a single displayed word across multiple nodes.
                var source = await ReadPageTextAsync(index, token);
                if (WhiteSpace().Replace(source, "").Normalize().Contains(term, StringComparison.OrdinalIgnoreCase))
                    text = await ReadTextPageAsync(index, token);
            }
            IReadOnlyList<PdfTextHit> matches = text == null ? [] :
                PdfTextPage.Find(text.Text, query, index, limit - hits.Count, canonical: true);
            if (matches.Count == 0 && recognized.TryGetValue(index + 1, out var recognizedText))
                matches = PdfTextPage.Find(recognizedText, query, index, limit - hits.Count, fromOcr: true, canonical: true);
            if (matches.Count == 0) continue;
            hits.AddRange(matches);
            if (publish != null) await publish(matches);
        }
        return hits;
    }

    private PageNavigation ResolveNavigation(int index, FixedEpubNavigation? navigation)
    {
        if (navigation == null) return EmptyNavigation;
        var links = new List<FixedEpubReaderLink>();
        foreach (var source in navigation.Links)
        {
            if (source.Bounds.Count == 0) continue;
            var target = source.IsFootnote ? null : Publication.ResolveFixedLink(index, source.Href);
            var note = source.IsFootnote ? Publication.ResolveFixedFootnote(index, source.Href) : null;
            if (target == null && note == null) continue;
            var title = !string.IsNullOrWhiteSpace(source.Title) ? source.Title :
                target is { } page ? $"前往第 {page.Page + 1} 页" : "打开脚注";
            links.Add(new FixedEpubReaderLink(index, target, title, source.Bounds, note));
        }
        return new PageNavigation(links, navigation.Anchors);
    }

    private PageDetails? GetCachedPage(int page)
    {
        if (!detailsByPage.TryGetValue(page, out var entry)) return null;
        recentPages.Remove(entry);
        recentPages.AddFirst(entry);
        return entry.Value;
    }

    private PageDetails CachePage(int page, PdfTextPage? text = null, PageNavigation? navigation = null)
    {
        if (detailsByPage.TryGetValue(page, out var existing))
        {
            text ??= existing.Value.Text;
            navigation ??= existing.Value.Navigation;
            recentPages.Remove(existing);
        }
        var details = new PageDetails(page, text, navigation);
        detailsByPage[page] = recentPages.AddFirst(details);
        if (recentPages.Count > PageCacheCapacity)
        {
            detailsByPage.Remove(recentPages.Last!.Value.Page);
            recentPages.RemoveLast();
        }
        return details;
    }

    private async Task<string> ReadPageTextAsync(int index, CancellationToken token)
    {
        var entry = Layout.Pages[index];
        if (await Layout.ReadAssetAsync(entry.Path, token).ConfigureAwait(false) is not { } asset) return "";
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var document = new HtmlDocument();
            using var content = new System.IO.MemoryStream(asset.Bytes, writable: false);
            document.Load(content);
            var svg = asset.MediaType == "image/svg+xml";
            var root = svg ? document.DocumentNode : document.DocumentNode.SelectSingleNode("//body");
            if (root == null) return "";
            var fragments = new List<string>();
            foreach (var node in root.Descendants())
            {
                token.ThrowIfCancellationRequested();
                if (node.NodeType != HtmlNodeType.Text) continue;
                var parents = node.Ancestors().ToArray();
                if (parents.Any(parent => LocalName(parent) is "script" or "style" or "noscript" or "head" or "defs" ||
                    parent.Attributes["hidden"] != null)) continue;
                if (svg && !parents.Any(parent => LocalName(parent) == "text")) continue;
                fragments.Add(HtmlEntity.DeEntitize(node.InnerText));
            }
            return WhiteSpace().Replace(string.Join(' ', fragments), " ").Trim();
        }, token).ConfigureAwait(false);
    }

    private static string LocalName(HtmlNode node) => node.Name[(node.Name.LastIndexOf(':') + 1)..];

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
