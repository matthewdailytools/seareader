using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace SeaReader.Native;

public partial class MainWindow
{
    private CancellationTokenSource? epubLinkRequest;

    private async Task FollowEpubLinkAsync(EpubDocument document, CancellationTokenSource session, RichTextBox body,
        Hyperlink link, EpubReference reference)
    {
        if (ReadEpubLinkOrigin(body, link) is not { } origin) return;
        StopReaderTap();
        SelectFacingPage(ReferenceEquals(body, FacingTextList));
        var opened = book;
        var view = CaptureReaderLocation();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        epubLinkRequest?.Cancel();
        epubLinkRequest = request;
        try
        {
            var destination = await document.ResolveInternalLinkAsync(reference, request.Token);
            if (!IsCurrentLinkRequest()) return;
            if (destination is not { } target)
            {
                ShowStatus("找不到这条书内链接的位置。");
                return;
            }
            ShowStatus("");
            var position = target.SourceOffset is int offset ? new TextPageStart(offset, 0, offset) : new TextPageStart(-1, 0, -1);
            await NavigateReaderLocationAsync(new ReaderLocation(target.Chapter, Math.Max(0, position.Offset), 0, position),
                jumpOrigin: origin);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (IsCurrentLinkRequest()) ShowStatus("暂时无法读取这条书内链接的位置。");
        }
        finally
        {
            if (ReferenceEquals(epubLinkRequest, request)) epubLinkRequest = null;
        }

        bool IsCurrentLinkRequest() => !closing && !moving && !turningPage && !seekingPosition && !request.IsCancellationRequested &&
            ReferenceEquals(epubLinkRequest, request) && ReferenceEquals(epub, document) && ReferenceEquals(book, opened) &&
            ReferenceEquals(reading, session) && SameReaderLocation(view, CaptureReaderLocation()) &&
            link.Parent is Paragraph row && ReferenceEquals(row.Parent, body.Document);
    }

    private ReaderLocation? ReadEpubLinkOrigin(RichTextBox body, Hyperlink link)
    {
        if (link.Parent is not Paragraph row || !ReferenceEquals(row.Parent, body.Document) ||
            ReadReferenceViewport(body) is not { } viewport || !ReferenceIntersectsViewport(link, viewport)) return null;

        var first = link.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var pointer = first;
        if (link.IsMouseOver && body.GetPositionFromPoint(Mouse.GetPosition(body), snapToText: false) is { } hit &&
            hit.CompareTo(first) >= 0 && hit.CompareTo(link.ContentEnd) < 0)
            pointer = hit;
        else if (first.GetCharacterRect(LogicalDirection.Forward).Bottom <= viewport.Top &&
            body.GetPositionFromPoint(new Point(viewport.Left + viewport.Width / 2, viewport.Top + 1), snapToText: true)
                ?.GetLineStartPosition(0) is { } visible && visible.CompareTo(first) > 0 && visible.CompareTo(link.ContentEnd) < 0)
            pointer = visible;

        var facing = ReferenceEquals(body, FacingTextList);
        if (facing && !HasSpread) return null;
        var rows = facing ? facingPage?.Rows : visibleRows;
        if (rows == null) return null;
        foreach (var candidate in rows)
        {
            if (!ReferenceEquals(candidate.Paragraph, row)) continue;
            var offset = ReadSelectionOffset(candidate, pointer);
            return new ReaderLocation(facing ? facingPage?.Chapter : chapter, offset, 0,
                new TextPageStart(candidate.Line.Offset, 0, offset));
        }
        return null;
    }

    private static Rect? ReadReferenceViewport(RichTextBox body)
    {
        if (!body.IsVisible || !body.IsArrangeValid || body.Template?.FindName("PART_ContentHost", body) is not ScrollViewer scroll ||
            scroll.Template?.FindName("PART_ScrollContentPresenter", scroll) is not ScrollContentPresenter viewport) return null;
        var bounds = new Rect(viewport.TranslatePoint(new Point(), body), viewport.RenderSize);
        if (body.Clip != null) bounds.Intersect(body.Clip.Bounds);
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
    }

    private static bool ReferenceIntersectsViewport(Hyperlink link, Rect viewport)
    {
        if (!link.ContentStart.HasValidLayout || !link.ContentEnd.HasValidLayout) return false;
        var first = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var last = link.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        return !first.IsEmpty && !last.IsEmpty && first.Top < viewport.Bottom && last.Bottom > viewport.Top;
    }

    private static IEnumerable<Hyperlink> ReaderReferences(RichTextBox body) =>
        body.Document.Blocks.OfType<Paragraph>().SelectMany(row => row.Inlines.OfType<Hyperlink>());

    private static void UpdateReferenceTabStops(RichTextBox body)
    {
        var viewport = ReadReferenceViewport(body);
        foreach (var link in ReaderReferences(body))
            KeyboardNavigation.SetIsTabStop(link, viewport is { } visible && ReferenceIntersectsViewport(link, visible));
    }
}
