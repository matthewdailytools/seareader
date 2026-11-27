using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private readonly record struct LineGuidePosition(object Source, int Chapter, int Offset);
    private sealed record LineGuidePage(RichTextBox Body, object Source, int Chapter, IReadOnlyList<ReaderGuideLine> Lines);

    private ReadingLineGuide? primaryLineGuide;
    private ReadingLineGuide? facingLineGuide;
    private LineGuidePosition? lineGuidePosition;
    private bool lineGuideTracking;
    private bool lineGuideRefreshQueued;
    private bool lineGuideStepping;
    private (RichTextBox Body, Point Point)? pendingLineGuideClick;

    private bool LineGuideActive => appearance.LineGuide is "soft" or "strong" && book != null && text != null &&
        pdf == null && ReaderSurface.IsVisible && !closing;

    private ReaderLocation? CaptureGuidedLocation()
    {
        if (!LineGuideActive || lineGuidePosition is not { } line || !ReferenceEquals(line.Source, (object?)epub ?? text)) return null;
        return new ReaderLocation(epub == null ? null : line.Chapter, line.Offset, 0,
            new TextPageStart(line.Offset, 0, line.Offset));
    }

    private void OnLineGuideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!populating && LineGuidePicker?.SelectedItem is ComboBoxItem { Tag: string mode }) SetLineGuideMode(mode);
    }

    private void OnTurnOffLineGuide(object sender, RoutedEventArgs e)
    {
        SetLineGuideMode("off");
        FocusReadingSurface();
    }

    private void SetLineGuideMode(string mode)
    {
        if (appearance.LineGuide == mode) return;
        appearance = appearance with { LineGuide = mode };
        SelectTag(LineGuidePicker, mode);
        UpdateLineGuideActivation();
        QueueAppearanceSave();
    }

    private void UpdateLineGuideActivation()
    {
        if (!LineGuideActive)
        {
            ClearLineGuide();
            return;
        }

        LineGuideExit.Visibility = Visibility.Visible;
        if (!lineGuideTracking)
        {
            TextList.LayoutUpdated += OnLineGuideLayoutUpdated;
            lineGuideTracking = true;
        }
        QueueLineGuideRefresh();
    }

    private void ClearLineGuide()
    {
        if (lineGuideTracking) TextList.LayoutUpdated -= OnLineGuideLayoutUpdated;
        lineGuideTracking = false;
        lineGuidePosition = null;
        pendingLineGuideClick = null;
        primaryLineGuide?.Remove();
        primaryLineGuide = null;
        facingLineGuide?.Remove();
        facingLineGuide = null;
        LineGuideExit.Visibility = Visibility.Collapsed;
    }

    private void OnLineGuideLayoutUpdated(object? sender, EventArgs e) => QueueLineGuideRefresh();

    private void QueueLineGuideRefresh()
    {
        if (!lineGuideTracking || lineGuideRefreshQueued || lineGuideStepping || closing) return;
        lineGuideRefreshQueued = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            lineGuideRefreshQueued = false;
            if (LineGuideActive && !lineGuideStepping && !moving && !turningPage && !seekingPosition && !updatingSpread &&
                chapterLoadPending == null && (HasSpread || spreadReflowLocation == null))
                RefreshLineGuide();
        }, DispatcherPriority.Render);
    }

    private LineGuidePage? ReadLineGuidePage(RichTextBox body)
    {
        if (ReferenceEquals(body, FacingTextList))
        {
            if (!HasSpread || facingPage is not { Document: { } document }) return null;
            return new LineGuidePage(body, (object?)epub ?? document, epub == null ? -1 : facingPage.Chapter,
                ReadPagedGuideLines(body, facingPage.Rows, facingPage.Chapter));
        }
        if (text == null || !ReferenceEquals(windowDocument, text)) return null;
        return new LineGuidePage(body, (object?)epub ?? text, epub == null ? -1 : chapter,
            ReadPagedGuideLines(body, visibleRows, chapter));
    }

    private IReadOnlyList<ReaderGuideLine> ReadPagedGuideLines(RichTextBox body,
        IReadOnlyList<(Paragraph Paragraph, TextLine Line)> rows, int currentChapter)
    {
        var lines = ReadingLineGuide.ReadLines(body, rows);
        if (continuous || lines.Count == 0 || ReadingPageIndex is not { } pages) return lines;

        int index = HasSpread ? spreadLeftIndex + (ReferenceEquals(body, FacingTextList) ? 1 : 0) : CurrentTextPage(pages);
        int chapterIndex = epub == null ? 0 : currentChapter;
        var start = pages.GetPage(index, out var pageChapter);
        if (pageChapter != chapterIndex) return lines;
        int first = SourceStart(start);
        int end = int.MaxValue;
        if (index + 1 < pages.Count)
        {
            var next = pages.GetPage(index + 1, out var nextChapter);
            if (nextChapter == pageChapter) end = SourceStart(next);
        }
        return lines.Where(line => line.Offset >= first && line.Offset < end).ToArray();

        static int SourceStart(TextPageStart page) => Math.Max(0, page.SourceOffset >= 0 ? page.SourceOffset : page.Offset);
    }

    private int ResolveGuideLine(LineGuidePage page)
    {
        if (lineGuidePosition is not { } position || !ReferenceEquals(position.Source, page.Source) || position.Chapter != page.Chapter)
            return 0;
        int index = 0;
        while (index + 1 < page.Lines.Count && page.Lines[index + 1].Offset <= position.Offset) index++;
        return index;
    }

    private void RefreshLineGuide()
    {
        if (ReadLineGuidePage(ActiveTextList) is not { Lines.Count: > 0 } page)
        {
            primaryLineGuide?.Remove();
            primaryLineGuide = null;
            facingLineGuide?.Remove();
            facingLineGuide = null;
            return;
        }

        var line = page.Lines[ResolveGuideLine(page)];
        if (lineGuidePosition is not { } position || !ReferenceEquals(position.Source, page.Source) || position.Chapter != page.Chapter ||
            position.Offset < line.Offset || position.Offset > line.Offset && position.Offset >= line.EndOffset)
            lineGuidePosition = new LineGuidePosition(page.Source, page.Chapter, line.Offset);
        RenderLineGuide(page.Body, line.Bounds);
    }

    private void ChooseGuideLine(LineGuidePage page, int index)
    {
        var line = page.Lines[index];
        lineGuidePosition = new LineGuidePosition(page.Source, page.Chapter, line.Offset);
        RenderLineGuide(page.Body, line.Bounds);
    }

    private void RenderLineGuide(RichTextBox body, Rect line)
    {
        var paper = (Brush)Resources["ReaderPaper"];
        var accent = (Brush)Resources["AccentInk"];
        double dim = appearance.LineGuide == "strong" ? .75 : .45;
        primaryLineGuide = ReadingLineGuide.Render(primaryLineGuide, TextList, ReferenceEquals(body, TextList) ? line : null,
            paper, accent, dim);
        if (HasSpread && FacingPaper.IsVisible)
            facingLineGuide = ReadingLineGuide.Render(facingLineGuide, FacingTextList, ReferenceEquals(body, FacingTextList) ? line : null,
                paper, accent, dim);
        else
        {
            facingLineGuide?.Remove();
            facingLineGuide = null;
        }
    }

    private bool MoveReaderLineGuideToPoint(RichTextBox body, Point point)
    {
        if (!LineGuideActive || moving || turningPage || seekingPosition || !body.Selection.IsEmpty ||
            ReadLineGuidePage(body) is not { Lines.Count: > 0 } page) return false;
        int index = 0;
        while (index + 1 < page.Lines.Count &&
            point.Y >= (page.Lines[index].Bounds.Bottom + page.Lines[index + 1].Bounds.Top) / 2) index++;
        SelectFacingPage(ReferenceEquals(body, FacingTextList));
        ChooseGuideLine(page, index);
        return true;
    }

    private async Task MoveReaderLineAsync(int direction)
    {
        if (!LineGuideActive || lineGuideStepping || moving || turningPage || seekingPosition ||
            chapterLoadPending != null || !ActiveTextList.Selection.IsEmpty ||
            ReadLineGuidePage(ActiveTextList) is not { Lines.Count: > 0 } page) return;

        int current = ResolveGuideLine(page);
        int next = current + direction;
        if (next >= 0 && next < page.Lines.Count)
        {
            ChooseGuideLine(page, next);
            return;
        }

        bool toFacingPage = direction > 0;
        if (HasSpread && FacingPaper.IsVisible && facingSelected != toFacingPage)
        {
            var nextBody = toFacingPage ? FacingTextList : TextList;
            if (ReadLineGuidePage(nextBody) is { Lines.Count: > 0 } other)
            {
                SelectFacingPage(toFacingPage);
                FocusReadingSurface();
                ChooseGuideLine(other, direction > 0 ? 0 : other.Lines.Count - 1);
                return;
            }
        }

        var opened = book;
        lineGuideStepping = true;
        try
        {
            if (continuous && ScrollGuideLine(page.Lines[current].Offset, direction)) return;
            if (!CanTurnPage(direction)) return;
            await TurnAsync(direction);
            if (!ReferenceEquals(book, opened) || !LineGuideActive) return;
            if (HasSpread && FacingPaper.IsVisible) SelectFacingPage(direction < 0);
            FocusReadingSurface();
            if (ReadLineGuidePage(ActiveTextList) is { Lines.Count: > 0 } entered)
                ChooseGuideLine(entered, direction > 0 ? 0 : entered.Lines.Count - 1);
        }
        finally
        {
            lineGuideStepping = false;
            QueueLineGuideRefresh();
        }
    }

    private bool ScrollGuideLine(int sourceOffset, int direction)
    {
        FindTextViewport();
        if (textScroll is not { } viewport) return false;
        if (NeedsTextWindow(direction))
        {
            var latest = ReadAnchor();
            MoveToText(latest.Offset, latest.Fraction, recenter: true);
        }
        if (!CanScroll(viewport, direction)) return false;

        double step = TextList.Document.LineHeight;
        if (!double.IsFinite(step) || step <= 0) step = appearance.Size;
        viewport.ScrollToVerticalOffset(viewport.VerticalOffset + step * direction);
        TextList.UpdateLayout();
        if (ReadLineGuidePage(TextList) is { Lines.Count: > 0 } page)
        {
            var original = ResolveGuideLine(page);
            int next = original + direction;
            bool stillVisible = page.Lines[original].Offset == sourceOffset;
            if (stillVisible && next >= 0 && next < page.Lines.Count) ChooseGuideLine(page, next);
            else ChooseGuideLine(page, direction > 0 ? page.Lines.Count - 1 : 0);
        }
        return true;
    }

    private RichTextBox? LineGuideBodyAt(MouseEventArgs e)
    {
        if (!LineGuideActive) return null;
        if (HasSpread && FacingTextList.IsVisible && new Rect(FacingTextList.RenderSize).Contains(e.GetPosition(FacingTextList)))
            return FacingTextList;
        return new Rect(TextList.RenderSize).Contains(e.GetPosition(TextList)) ? TextList : null;
    }
}
