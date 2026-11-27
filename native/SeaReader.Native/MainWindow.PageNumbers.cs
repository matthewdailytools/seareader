using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const int MinimumCachedTextLength = 1024 * 1024;
    private TextPagination? textPagination;
    private TextPagination? textPagePrefix;
    private TextPaginationLayout? paginationLayout;
    private IReadOnlyList<long>? chapterReadingStarts;
    private CancellationTokenSource? paginating;
    private bool paginationFailed;
    private Task pageIndexCacheWrite = Task.CompletedTask;

    private void ResetTextPageNumbers()
    {
        RememberSpreadPosition();
        if (HasSpread) DiscardSpread();
        paginating?.Cancel();
        paginating?.Dispose();
        paginating = null;
        textPagination = null;
        textPagePrefix = null;
        paginationLayout = null;
        paginationFailed = false;
        RefreshBookmarkPageLabels();
    }

    private void QueueTextPagination()
    {
        if (closing || text is not { } document || !ReferenceEquals(windowDocument, document) ||
            textScroll is not { ViewportWidth: > 0, ViewportHeight: > 0 } viewport) return;
        if (document.HasIllustrations) EpubIllustrationLayout.Resize(TextList.Document, new Size(viewport.ViewportWidth, viewport.ViewportHeight));
        var dpi = VisualTreeHelper.GetDpi(TextList);
        var flow = TextList.Document;
        var layout = new TextPaginationLayout(viewport.ViewportWidth, viewport.ViewportHeight, TextList.FontFamily.Source,
            flow.FontSize, flow.LineHeight, dpi.DpiScaleX, dpi.DpiScaleY, TextOptions.GetTextFormattingMode(TextList),
            flow.FontWeight == FontWeights.Bold, flow.TextAlignment, appearance.Hyphenation);
        if (paginationLayout == layout) return;
        ResetTextPageNumbers();
        paginationLayout = layout;
        paginating = new CancellationTokenSource();
        _ = BuildTextPaginationAsync(document, epub, chapter, layout, paginating.Token);
    }

    private async Task BuildTextPaginationAsync(TextDocument document, IChapterBook? source, int startChapter,
        TextPaginationLayout layout, CancellationToken token)
    {
        var cachedBook = source == null && document.Text.Length >= MinimumCachedTextLength && book is { } opened &&
                         opened.Format.Equals("txt", StringComparison.OrdinalIgnoreCase) ? opened.Id : null;
        var cacheDirectory = cachedBook == null ? null : Path.Combine(store.DataDirectory, "text-page-index");
        try
        {
            // Allow the first page to paint before opening a saved layout or starting a new one.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, token);
            var pages = cachedBook != null && cacheDirectory != null
                ? await TextPaginationCache.TryReadAsync(cacheDirectory, cachedBook, document, layout, token) : null;
            var computed = pages == null;
            if (pages == null)
            {
                // Coalesce resize and font-slider changes before starting a full measurement.
                await Task.Delay(175, token);
                Action<TextPagination>? publishPrefix = null;
                if (!continuous)
                    publishPrefix = prefix => Dispatcher.BeginInvoke(DispatcherPriority.Background,
                        new Action(() => ApplyTextPagePrefix(prefix, token)));
                pages = await TextPagination.BuildAsync(document, source, startChapter, layout, token, publishPrefix);
            }
            if (token.IsCancellationRequested) return;
            // The full index has the same starts as every prefix from this worker.
            if (HasSpread) spreadPagination = pages;
            textPagination = pages;
            textPagePrefix = null;
            if (source != null) chapterReadingStarts = pages.ChapterCharacterStarts;
            if (computed && cachedBook != null && cacheDirectory != null)
                pageIndexCacheWrite = WritePageIndexAfterAsync(pageIndexCacheWrite, cacheDirectory, cachedBook, document, layout, pages);
            if (!doublePage && !continuous && !turningPage && !seekingPosition && !moving) AlignCurrentTextPage(pages);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            if (token.IsCancellationRequested) return;
            ReaderLocation? location = HasSpread ? CaptureLayoutLocation() : null;
            if (HasSpread) DiscardSpread();
            textPagination = null;
            textPagePrefix = null;
            paginationFailed = true;
            SetCenteredLastLeaf(false);
            Trace.TraceWarning($"Unable to paginate the current book: {error.Message}");
            if (location is { } saved)
            {
                spreadReflowLocation = null;
                await RestoreReaderLocationAsync(saved);
            }
        }
        RefreshBookmarkPageLabels();
        SetProgress();
        if (source != null && textPagination != null && ReferenceEquals(source, epub) && !seekingPosition && !turningPage)
            SaveCurrentPosition();
    }

    private static async Task WritePageIndexAfterAsync(Task previous, string directory, string bookId,
        TextDocument document, TextPaginationLayout layout, TextPagination pages)
    {
        await previous.ConfigureAwait(false);
        // This exact layout is usable after a close or resize as soon as its complete index has been measured.
        await TextPaginationCache.WriteAsync(directory, bookId, document, layout, pages, CancellationToken.None).ConfigureAwait(false);
    }

    private void ApplyTextPagePrefix(TextPagination pages, CancellationToken token)
    {
        if (closing || token.IsCancellationRequested || continuous || textPagination != null ||
            paginationFailed || (textPagePrefix?.ConfirmedCount ?? 0) >= pages.ConfirmedCount) return;
        if (HasSpread) spreadPagination = pages;
        textPagePrefix = pages;
        SetProgress();
    }

    private string TextPageLabel()
    {
        if (HasSpread && spreadPagination is { } spread && spreadLeftIndex + 1 < spread.Count)
            return $"页 {spreadLeftIndex + 1}–{spreadLeftIndex + 2} / {(textPagination?.Count.ToString() ?? "…")}";
        if (textPagination is { } pages) return $"页 {CurrentTextPage(pages) + 1} / {pages.Count}";
        if (!doublePage && CurrentConfirmedPrefixPage() is int page) return $"页 {page + 1} / …";
        if (paginationFailed) return "页数暂不可用";
        return OnFirstUnindexedPage ? "页 1 / …" : "正在分页…";
    }

    private bool OnFirstUnindexedPage => chapter == 0 && windowStart == 0 && textScroll is { VerticalOffset: < .1 };

    private int CurrentTextPage(TextPagination pages) => HasSpread && ReferenceEquals(spreadPagination, pages)
        ? spreadLeftIndex + (facingSelected ? 1 : 0) : FindTextPage(pages, chapter, ReadTextPagePosition());

    private static int FindTextPage(TextPagination pages, int chapter, TextPageStart top) => pages.FindPage(chapter, top);

    private int? CurrentConfirmedPrefixPage() => !continuous && textPagePrefix is { } pages
        ? CurrentConfirmedTextPage(pages) : null;

    private int? CurrentConfirmedTextPage(TextPagination pages)
    {
        if (!pages.ContainsChapter(chapter)) return null;
        var page = CurrentTextPage(pages);
        return page < pages.ConfirmedCount ? page : null;
    }

    private static int? FindConfirmedTextPage(TextPagination pages, int chapter, TextPageStart position)
    {
        if (!pages.ContainsChapter(chapter)) return null;
        var page = FindTextPage(pages, chapter, position);
        return page < pages.ConfirmedCount ? page : null;
    }

    private static bool SameTextPageStart(TextPageStart start, TextPageStart position) =>
        start.Illustration == position.Illustration && (start.SourceOffset >= 0
            ? start.SourceOffset == position.SourceOffset
            : start.Offset == position.Offset && Math.Abs(start.Fraction - position.Fraction) < .00001);

    private void AlignCurrentTextPage(TextPagination pages)
    {
        var position = ReadTextPagePosition();
        var requested = pageResizeLocation;
        pageResizeLocation = null;
        if (requested?.Chapter != (epub == null ? null : chapter)) requested = null;
        var target = requested?.TextPosition ?? position;
        var start = pages.GetPage(FindTextPage(pages, chapter, target), out _);
        if (requested.HasValue) retainedLayoutLocation = requested;
        if (SameTextPageStart(start, position)) return;
        MoveToText(start.Offset, start.Fraction, page: start);
        SaveCurrentPosition();
    }

    private TextPageStart IndexedLocation(TextPagination pages, int offset, double fraction)
    {
        var index = fraction > 0 || epub != null && offset == 0
            ? pages.FindPage(chapter, fraction == 0 ? -1 : offset, fraction)
            : pages.FindPage(chapter, offset);
        return pages.GetPage(index, out _);
    }

    private TextPageStart ReadTextPagePosition()
    {
        var first = FirstTextVisualLine();
        var paragraph = first?.Paragraph;
        if (first == null || paragraph == null || text == null)
            return new TextPageStart(anchor.Offset, anchor.Fraction, anchor.Fraction == 0 ? anchor.Offset : -1);
        if (ReferenceEquals(paragraph, chapterHeading))
        {
            var top = TextWindowTop();
            var height = ParagraphTop(0) - top;
            var fraction = height > 0
                ? Math.Clamp((first.GetCharacterRect(LogicalDirection.Forward).Top - top) / height, 0, 1) : 0;
            return new TextPageStart(-1, fraction, -1);
        }
        var index = text.FindLine(anchor.Offset) - windowStart;
        if (index < 0 || index >= visibleRows.Count || !ReferenceEquals(paragraph, visibleRows[index].Paragraph))
            index = visibleRows.FindIndex(row => ReferenceEquals(row.Paragraph, paragraph));
        if (index < 0) return new TextPageStart(anchor.Offset, anchor.Fraction, -1);
        var line = visibleRows[index].Line;
        if (text.HasIllustrations && EpubIllustrationLayout.AtLine(first) is { } figure)
            return new TextPageStart(line.Offset, anchor.Fraction, figure.Offset, figure.Index);
        var offset = ParagraphText.OffsetOf(paragraph, first, line.Length);
        return new TextPageStart(line.Offset, anchor.Fraction, line.Offset + offset);
    }

    private TextPointer? FirstTextVisualLine()
    {
        var inset = TextList.Padding;
        var first = TextList.GetPositionFromPoint(new Point(inset.Left + TextPagination.DocumentPadding.Left + 1, inset.Top + 1), snapToText: true)
                            ?.GetLineStartPosition(0);
        var top = inset.Top;
        if (text?.HasIllustrations == true)
        {
            // Caret hit testing may snap to nearby text at the edge of a wide inline image.
            while (first != null && first.GetCharacterRect(LogicalDirection.Forward).Top > top + .1 &&
                   AdjacentVisualLine(first, -1) is { } previous &&
                   previous.GetCharacterRect(LogicalDirection.Forward).Bottom > top + .1)
                first = previous;
            while (first != null && AdjacentVisualLine(first, 1) is { } next &&
                   next.GetCharacterRect(LogicalDirection.Forward).Top <= top + .1)
                first = next;
        }
        while (first != null && first.GetCharacterRect(LogicalDirection.Forward).Bottom <= top + .1)
            first = AdjacentVisualLine(first, 1);
        return first;
    }

    private double TextPositionTop(TextPageStart page, int localIndex)
    {
        var origin = TextWindowTop();
        if (page.Offset < 0 && chapterHeading != null)
            return (ParagraphTop(0) - origin) * Math.Clamp(page.Fraction, 0, 1);
        if (text?.HasIllustrations == true && textScroll is { } scroll)
            origin = TextList.Padding.Top - scroll.VerticalOffset;
        var top = ParagraphTop(localIndex) - origin;
        var (paragraph, row) = visibleRows[localIndex];
        if (page.Illustration is int figure && EpubIllustrationLayout.Find(paragraph, figure) is { } container)
            return container.ElementStart.GetCharacterRect(LogicalDirection.Forward).Top - origin;
        if (page.SourceOffset >= 0 && ParagraphText.PositionAt(paragraph,
                Math.Clamp(page.SourceOffset - row.Offset, 0, row.Length)) is { } pointer &&
            pointer.GetCharacterRect(LogicalDirection.Forward) is { IsEmpty: false } bounds)
            return bounds.Top - origin;
        return top + ParagraphHeight(localIndex) * Math.Clamp(page.Fraction, 0, 1);
    }
}
