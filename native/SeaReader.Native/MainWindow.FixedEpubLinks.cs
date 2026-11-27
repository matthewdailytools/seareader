using System.IO;
using System.Windows;

namespace SeaReader.Native;

public partial class MainWindow
{
    private sealed record FixedFootnoteAnchor(FixedEpubPageBook Book, FixedEpubReaderLink Link, Rect Bounds, bool ReturnToLink);
    private FixedFootnoteAnchor? fixedFootnoteAnchor;

    private void InitializeFixedEpubLinks()
    {
        Attach(PdfSelection, FixedEpubPrimaryLinks);
        Attach(PdfFacingSelection, FixedEpubFacingLinks);
        PdfSurface.ScrollChanged += (_, e) =>
        {
            if (!updatingPdfPages && (e.HorizontalChange != 0 || e.VerticalChange != 0 ||
                e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)) UpdateFixedFootnotePlacement();
        };

        void Attach(PdfSelectionLayer body, FixedEpubLinkLayer overlay)
        {
            body.FixedEpubLinkInvoked += OnFixedEpubLinkInvoked;
            overlay.LinkInvoked += link => OnFixedEpubLinkInvoked(body, link, null);
            overlay.LinkFocused += link =>
            {
                if (!moving && !turningPage && !seekingPosition && PdfPageFor(body) == link.SourcePage)
                    ActivatePdfPage(body);
            };
        }
    }

    private void SetFixedEpubLinks(PdfSelectionLayer selection, IReadOnlyList<FixedEpubReaderLink> links)
    {
        selection.SetFixedEpubLinks(links);
        FixedLinksFor(selection).SetLinks(links);
    }

    private FixedEpubLinkLayer FixedLinksFor(PdfSelectionLayer body) =>
        ReferenceEquals(body, PdfSelection) ? FixedEpubPrimaryLinks : FixedEpubFacingLinks;

    private async void OnFixedEpubLinkInvoked(PdfSelectionLayer body, FixedEpubReaderLink link, Rect? pointerBounds)
    {
        if (pdf is not FixedEpubPageBook document || !body.IsVisible || !body.IsArrangeValid ||
            PdfPageFor(body) != link.SourcePage || closing || moving || turningPage || seekingPosition) return;
        ActivatePdfPage(body);
        StopReaderTap();
        PdfSurface.UpdateLayout();
        if (link.Footnote is { } note)
        {
            var source = new FixedFootnoteAnchor(document, link, pointerBounds ?? link.Bounds[0], pointerBounds == null);
            await OpenFixedFootnoteAsync(source, note);
        }
        else if (link.Target is { } target)
        {
            if (shownPanel == Panel.Footnote) ClosePanel(false);
            await NavigateFixedEpubTargetAsync(document, target);
        }
    }

    private async Task OpenFixedFootnoteAsync(FixedFootnoteAnchor source, EpubReference note)
    {
        if (reading is not { IsCancellationRequested: false } session || ReadFixedFootnoteAnchor(source) == null) return;
        epubLinkRequest?.Cancel();
        var request = BeginReferencePanel(session.Token, "脚注", "正在读取脚注…");
        fixedFootnoteAnchor = source;
        var token = request.Token;
        OpenPanel(Panel.Footnote);
        try
        {
            var text = await source.Book.Publication.ReadFootnoteAsync(note, token);
            if (IsCurrentNote()) DisplayFootnoteText(string.IsNullOrWhiteSpace(text) ? "找不到这条脚注的正文。" : text);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (IsCurrentNote()) DisplayFootnoteText("暂时无法读取这条脚注。");
        }

        bool IsCurrentNote() => !token.IsCancellationRequested && ReferenceEquals(footnoteRequest, request) &&
            ReferenceEquals(fixedFootnoteAnchor, source) && ReferenceEquals(reading, session) && ReferenceEquals(pdf, source.Book);
    }

    private Rect? ReadFixedFootnoteAnchor(FixedFootnoteAnchor source)
    {
        if (!ReferenceEquals(pdf, source.Book) || PdfSelectionFor(source.Link.SourcePage) is not
            { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } body) return null;
        var box = source.Bounds;
        var bounds = new Rect(box.X * body.ActualWidth, box.Y * body.ActualHeight,
            box.Width * body.ActualWidth, box.Height * body.ActualHeight);
        bounds.Intersect(ReadMarkViewport(body, PdfSurface));
        return bounds.IsEmpty ? null : new Rect(body.TranslatePoint(bounds.TopLeft, ReaderSurface), bounds.Size);
    }

    private bool FocusFixedFootnoteSource(FixedFootnoteAnchor source)
    {
        if (ReadFixedFootnoteAnchor(source) == null || PdfSelectionFor(source.Link.SourcePage) is not { } body) return false;
        return source.ReturnToLink && FixedLinksFor(body).FocusLink(source.Link) || body.Focus();
    }

    private void UpdateFixedFootnotePlacement()
    {
        if (shownPanel != Panel.Footnote || fixedFootnoteAnchor is not { } source) return;
        if (ReadFixedFootnoteAnchor(source) == null) ClosePanel(false);
        else PlaceFootnotePanel();
    }

    private async Task<bool> NavigateFixedEpubTargetAsync(FixedEpubPageBook document, FixedEpubTarget target,
        CancellationToken selectionToken = default)
    {
        if (book is not { } opened || reading is not { } session || !ReferenceEquals(document, pdf) ||
            target.Page < 0 || target.Page >= document.PageCount || closing || moving || turningPage || seekingPosition) return false;
        var origin = CaptureNavigationLocation();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(session.Token, selectionToken);
        epubLinkRequest?.Cancel();
        epubLinkRequest = request;
        try
        {
            Rect? anchor = null;
            if (target.Fragment.Length != 0)
            {
                anchor = await document.FindAnchorAsync(target, request.Token);
                if (!IsCurrentLinkRequest()) return false;
                if (anchor == null && !target.Fragment.Equals("top", StringComparison.OrdinalIgnoreCase))
                {
                    ShowStatus("找不到这条书内链接的位置。");
                    return false;
                }
            }
            if (!IsCurrentLinkRequest()) return false;
            ShowStatus("");
            IReadOnlyList<Rect>? bounds = anchor is { } position ? [position] : null;
            return await NavigateReaderLocationAsync(new ReaderLocation(null, target.Page, 0), bounds,
                showTransientAnnotation: false, jumpOrigin: origin);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (IsCurrentLinkRequest()) ShowStatus("暂时无法读取这条书内链接的位置。");
            return false;
        }
        finally { if (ReferenceEquals(epubLinkRequest, request)) epubLinkRequest = null; }

        bool IsCurrentLinkRequest() => !closing && !moving && !turningPage && !seekingPosition && !request.IsCancellationRequested &&
            ReferenceEquals(epubLinkRequest, request) && ReferenceEquals(book, opened) && ReferenceEquals(reading, session) &&
            ReferenceEquals(pdf, document) && SameReaderLocation(origin, CaptureNavigationLocation());
    }
}
