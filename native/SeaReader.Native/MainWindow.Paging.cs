using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const double ScrollEdgeTolerance = 1;
    private const int MaximumPendingTurns = 4;
    private const int MaximumChapterPageAnchors = 32;
    private readonly record struct ChapterPageLayout(double Width, double Height, string Font, double Size, double LineHeight,
        bool Bold, string Alignment);
    private readonly LinkedList<int> pendingTurns = new();
    private readonly Dictionary<int, (ChapterPageLayout Layout, ReaderLocation Location)> chapterPageAnchors = [];
    private BookEntry? chapterAnchorBook;
    private bool turningPage;
    private bool pendingTurnScheduled;

    private bool WaitingForInitialSpread => doublePage && !continuous && !HasSpread && !paginationFailed;

    private bool WaitingForSpreadTurn(int direction) => WaitingForInitialSpread ||
        direction > 0 && HasSpread && textPagination == null && !CanTurnPage(direction);

    private bool CanRequestTurn(int direction) => CanTurnPage(direction) || direction > 0 && WaitingForSpreadTurn(direction);

    private bool ReverseHorizontalPageTurns => !continuous && UsesRightToLeftPageProgression;

    private int MapHorizontalPageTurn(int direction) => ReverseHorizontalPageTurns ? -direction : direction;

    private static bool CanScroll(ScrollViewer viewport, int direction) => direction > 0
        ? viewport.VerticalOffset < viewport.ScrollableHeight - ScrollEdgeTolerance
        : viewport.VerticalOffset > ScrollEdgeTolerance;

    private bool CanTurnPage(int direction)
    {
        if (pdf != null)
        {
            var target = NextPdfPage(direction);
            return target >= 0 && target < pdf.PageCount;
        }
        if (text == null) return false;
        if (!continuous && ReadingPageIndex is { } pages)
        {
            var next = NextIndexedPage(pages, direction);
            return doublePage ? CanDisplaySpread(pages, next) : next >= 0 && next < pages.Count;
        }
        if (!continuous && !doublePage && textPagePrefix is { } prefix &&
            NextConfirmedSinglePage(prefix, direction).HasValue) return true;
        if (epub != null && chapter + direction >= 0 && chapter + direction < epub.Chapters.Count) return true;
        if (!ReferenceEquals(windowDocument, text)) return direction > 0;
        if (direction > 0 ? windowEnd < text.Lines.Count : windowStart > 0) return true;
        return textScroll != null && CanStepTextViewport(textScroll, direction);
    }

    private Task TurnAsync(int direction)
    {
        if (closing || moving || seekingPosition || book == null || chapterLoadPending != null && !turningPage)
            return Task.CompletedTask;
        if (pendingTurns.Count == MaximumPendingTurns) pendingTurns.RemoveFirst();
        pendingTurns.AddLast(direction);
        return RunPendingTurnsAsync();
    }

    private bool CanRunPendingTurns() => !closing && book != null && !moving && !turningPage && !seekingPosition &&
        chapterLoadPending == null && pendingTurns.First is { } next && !WaitingForSpreadTurn(next.Value);

    private async Task RunPendingTurnsAsync()
    {
        if (!CanRunPendingTurns()) return;
        turningPage = true;
        var opened = book;
        var session = reading;
        try
        {
            while (ReferenceEquals(reading, session) && ReferenceEquals(book, opened) && !moving && !seekingPosition &&
                   pendingTurns.First is { } next && !WaitingForSpreadTurn(next.Value))
            {
                int direction = next.Value;
                pendingTurns.RemoveFirst();
                if (!CanTurnPage(direction)) continue;
                DismissInlineSelection();
                if (shownPanel == Panel.Footnote) ClosePanel(false);
                var pages = ReadingPageIndex;
                var chapterBeforeTurn = chapter;
                var singlePageIndex = !doublePage && !continuous && epub != null && NavigationTextPages != null
                    ? paginating?.Token : null;
                var frame = BeginPageTransition(direction);
                var changed = false;
                try
                {
                    changed = await TurnOnePageAsync(direction) && ReferenceEquals(reading, session) && ReferenceEquals(book, opened);
                    if (changed)
                    {
                        var hadReflowLocation = pageResizeLocation.HasValue || spreadReflowLocation.HasValue;
                        spreadReflowLocation = null;
                        ForgetLayoutLocation();
                        if (hadReflowLocation) SaveCurrentPosition();
                    }
                }
                finally { CompletePageTransition(frame, changed); }

                var chapterTurnInvalidated = singlePageIndex is { IsCancellationRequested: true } && chapterBeforeTurn == chapter;
                if (!changed && ReferenceEquals(reading, session) && ReferenceEquals(book, opened) &&
                    (chapterTurnInvalidated || pages != null && WaitingForSpreadTurn(direction)))
                {
                    // Resizing can invalidate the index while the next EPUB chapter is loading.
                    if (pendingTurns.Count == MaximumPendingTurns) pendingTurns.RemoveLast();
                    pendingTurns.AddFirst(direction);
                    break;
                }
            }
        }
        finally
        {
            turningPage = false;
            if (!closing && ReferenceEquals(reading, session) && ReferenceEquals(book, opened)) SetProgress();
            else QueuePendingTurns();
        }
    }

    private void QueuePendingTurns()
    {
        if (pendingTurnScheduled || !CanRunPendingTurns()) return;
        pendingTurnScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            pendingTurnScheduled = false;
            _ = RunPendingTurnsAsync();
        }));
    }

    private async Task<bool> TurnOnePageAsync(int direction)
    {
        if (pdf != null)
        {
            var previousPage = pdfPage;
            var focusPage = ActivePdfSelection.IsKeyboardFocusWithin;
            await ChangePdfPageAsync(NextPdfPage(direction));
            if (focusPage && previousPage != pdfPage) FocusReadingSurface();
            return previousPage != pdfPage && CurrentPdfImage != null;
        }

        if (!continuous && ReadingPageIndex is { } pages) return await TurnIndexedTextPageAsync(pages, direction);
        if (!continuous && !doublePage && textPagePrefix is { } prefix &&
            NextConfirmedSinglePage(prefix, direction) is int next)
            return await MoveToIndexedTextPageAsync(prefix, next);

        FindTextViewport();
        if (textScroll is not { } viewport) return false;
        if (NeedsTextWindow(direction))
        {
            var latest = ReadAnchor();
            MoveToText(latest.Offset, latest.Fraction, recenter: true);
        }
        if (CanStepTextViewport(viewport, direction) && TurnTextViewport(viewport, direction)) return true;
        return epub != null && await TurnTextChapterAsync(viewport, direction);
    }

    private async Task<bool> TurnIndexedTextPageAsync(TextPagination pages, int direction)
    {
        var next = NextIndexedPage(pages, direction);
        var focusPage = doublePage && (TextList.IsKeyboardFocusWithin || FacingTextList.IsKeyboardFocusWithin);
        var moved = doublePage ? await MoveToSpreadAsync(pages, next) : await MoveToIndexedTextPageAsync(pages, next);
        if (moved && focusPage) FocusReadingSurface();
        return moved;
    }

    private int NextIndexedPage(TextPagination pages, int direction)
    {
        var current = CurrentTextPage(pages);
        return doublePage ? (current & ~1) + 2 * direction : current + direction;
    }

    private int? NextConfirmedSinglePage(TextPagination pages, int direction)
    {
        if (!pages.ContainsChapter(chapter)) return null;
        var position = ReadTextPagePosition();
        var current = FindTextPage(pages, chapter, position);
        var next = current + direction;
        // Only adopt the measured route from an exact page start. An unindexed viewport can keep
        // turning by visible lines without being snapped backward when a prefix arrives.
        return current < pages.ConfirmedCount && next >= 0 && next < pages.ConfirmedCount &&
            SameTextPageStart(pages.GetPage(current, out _), position) ? next : null;
    }

    private async Task<bool> TurnTextChapterAsync(ScrollViewer viewport, int direction)
    {
        var previous = chapter;
        var target = chapter + direction;
        if (epub == null || target < 0 || target >= epub.Chapters.Count) return false;
        if (!ReferenceEquals(chapterAnchorBook, book))
        {
            chapterAnchorBook = book;
            chapterPageAnchors.Clear();
        }
        var layout = new ChapterPageLayout(viewport.ViewportWidth, viewport.ViewportHeight,
            appearance.Font, appearance.Size, appearance.LineHeight, appearance.Bold, appearance.Alignment);
        if (!continuous && direction > 0)
        {
            if (chapterPageAnchors.Count >= MaximumChapterPageAnchors && !chapterPageAnchors.ContainsKey(previous))
                chapterPageAnchors.Remove(chapterPageAnchors.Keys.First());
            chapterPageAnchors[previous] = (layout, CaptureReaderLocation());
        }
        if (!continuous && direction < 0 && chapterPageAnchors.TryGetValue(target, out var tail) && tail.Layout == layout)
            await ChangeChapterAsync(target, tail.Location.Offset, tail.Location.Fraction);
        else await ChangeChapterAsync(target, direction < 0 ? int.MaxValue : 0);
        return previous != chapter;
    }

    private bool CanStepTextViewport(ScrollViewer viewport, int direction)
    {
        if (continuous || direction < 0) return CanScroll(viewport, direction);
        if (text == null || visibleRows.Count == 0) return false;
        if (windowEnd < text.Lines.Count) return true;
        if (!CanScroll(viewport, direction)) return false;
        var last = visibleRows[^1].Paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        return last.IsEmpty || last.Bottom > TextList.Padding.Top + viewport.ViewportHeight + .1;
    }

    private bool TurnTextViewport(ScrollViewer viewport, int direction)
    {
        TextList.UpdateLayout();
        var offset = viewport.VerticalOffset;
        if (visibleRows.Count == 0 || viewport.ViewportHeight <= 0) return false;
        if (continuous)
        {
            viewport.ScrollToVerticalOffset(offset + direction * viewport.ViewportHeight);
            TextList.UpdateLayout();
            return Math.Abs(viewport.VerticalOffset - offset) > ScrollEdgeTolerance;
        }

        var inset = TextList.Padding;
        var bottom = inset.Top + viewport.ViewportHeight;
        var documentTop = text?.HasIllustrations == true ? inset.Top - offset : TextWindowTop();
        var first = FirstTextVisualLine();
        if (first == null || !double.IsFinite(documentTop)) return false;

        var target = direction > 0
            ? FirstOverflowLine(first, bottom, documentTop + offset)
            : PreviousPageStart(first, bottom, documentTop + offset);
        if (target != null)
        {
            var targetOffset = target.GetCharacterRect(LogicalDirection.Forward).Top - documentTop;
            if (double.IsFinite(targetOffset)) return ScrollToTextPage(viewport, targetOffset);
        }
        return direction > 0 && text != null && windowEnd < text.Lines.Count && OpenNextTextWindowPage(viewport);
    }

    private static TextPointer? FirstOverflowLine(TextPointer first, double bottom, double pageTop)
    {
        var line = first;
        while (line != null)
        {
            var rect = line.GetCharacterRect(LogicalDirection.Forward);
            if (rect.Bottom > bottom + .1)
                return rect.Top > pageTop + .1 ? line : AdjacentVisualLine(line, 1);
            line = AdjacentVisualLine(line, 1);
        }
        return null;
    }

    private static TextPointer? PreviousPageStart(TextPointer first, double bottom, double pageTop)
    {
        var previous = AdjacentVisualLine(first, -1);
        if (previous == null) return null;
        var previousBottom = previous.GetCharacterRect(LogicalDirection.Forward).Bottom;
        var start = previous;
        while (AdjacentVisualLine(start, -1) is { } candidate)
        {
            var candidateTop = candidate.GetCharacterRect(LogicalDirection.Forward).Top;
            if (previousBottom + pageTop - candidateTop > bottom + .1) break;
            start = candidate;
        }
        return start;
    }

    private static TextPointer? AdjacentVisualLine(TextPointer line, int direction)
    {
        var next = line.GetLineStartPosition(direction, out int moved);
        return moved == direction && next != null && Math.Sign(next.CompareTo(line)) == direction ? next : null;
    }

    private bool ScrollToTextPage(ScrollViewer viewport, double target)
    {
        var previous = viewport.VerticalOffset;
        var overflow = target - viewport.ScrollableHeight;
        if (overflow > .1)
        {
            // A short last page should start at its first line, without repeating the previous page's lines.
            var padding = TextList.Document.PagePadding;
            TextList.Document.PagePadding = new Thickness(padding.Left, padding.Top, padding.Right, padding.Bottom + overflow);
            TextList.UpdateLayout();
        }
        viewport.ScrollToVerticalOffset(Math.Max(0, target));
        TextList.UpdateLayout();
        return Math.Abs(viewport.VerticalOffset - previous) > ScrollEdgeTolerance;
    }

    private bool OpenNextTextWindowPage(ScrollViewer viewport)
    {
        var next = windowEnd;
        moving = true;
        try
        {
            SetTextWindow(next);
            ScrollToTextPage(viewport, ParagraphTop(next - windowStart) - TextWindowTop());
            anchor = ReadAnchor();
        }
        finally { moving = false; }
        SaveCurrentPosition();
        return true;
    }

    private async void OnTurnButton(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string direction } || !int.TryParse(direction, out var turn)) return;
        if (ReferenceEquals(sender, LeftPageEdge) || ReferenceEquals(sender, RightPageEdge)) turn = MapHorizontalPageTurn(turn);
        await TurnAsync(turn);
    }

    private async void OnReaderWheel(object sender, MouseWheelEventArgs e)
    {
        if (InOverlay || e.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None) return;
        var direction = e.Delta > 0 ? -1 : 1;
        if (pdf != null && PdfSurface.IsMouseOver)
        {
            // Fit-width and zoomed pages can be taller than the viewport.
            // Preserve native scrolling within the page before crossing a boundary.
            if (CanScroll(PdfSurface, direction)) return;
            e.Handled = true;
            var original = pdf;
            var page = pdfPage;
            await TurnAsync(direction);
            if (continuous && direction < 0 && ReferenceEquals(pdf, original) && pdfPage != page)
                PdfSurface.ScrollToBottom();
            return;
        }
        if (text == null || !TextList.IsMouseOver && !FacingTextList.IsMouseOver) return;
        e.Handled = true;
        if (!continuous)
        {
            await TurnAsync(direction);
            return;
        }
        FindTextViewport();
        if (textScroll is not { } viewport || turningPage) return;
        if (NeedsTextWindow(direction))
        {
            var latest = ReadAnchor();
            MoveToText(latest.Offset, latest.Fraction, recenter: true);
        }
        else if (epub != null && !CanScroll(viewport, direction))
        {
            await TurnAsync(direction);
            return;
        }
        viewport.ScrollToVerticalOffset(viewport.VerticalOffset - e.Delta * .8);
    }
}
