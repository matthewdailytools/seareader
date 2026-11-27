using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const string PdfViewPreference = "pdf-reading-view";
    private const string FixedEpubViewPreference = "fixed-epub-reading-view";
    private static Task pdfViewWrite = Task.CompletedTask;
    private FixedEpubRenderer? fixedEpubRenderer;
    private int pdfPrimaryPage = -1;
    private int pdfFacingPage = -1;
    private bool pdfSpread;
    private bool updatingPdfPages;
    private Task? pdfFacingTextLoad;

    private bool WantsPdfSpread => pdfZoom == "spread" && !continuous &&
        ReadingLane.ActualWidth >= 660 && ReadingLaneHeight() >= 370;

    private PdfSelectionLayer ActivePdfSelection => pdfFacingPage >= 0 && pdfPage == pdfFacingPage ? PdfFacingSelection : PdfSelection;
    private FrameworkElement ActivePdfPageHost => ReferenceEquals(ActivePdfSelection, PdfSelection) ? PdfPageHost : PdfFacingPageHost;

    private int PdfPageFor(PdfSelectionLayer layer) => ReferenceEquals(layer, PdfSelection) ? pdfPrimaryPage :
        ReferenceEquals(layer, PdfFacingSelection) ? pdfFacingPage : -1;

    private PdfSelectionLayer? PdfSelectionFor(int index) => index < 0 ? null :
        index == pdfPrimaryPage ? PdfSelection : index == pdfFacingPage ? PdfFacingSelection : null;

    private void ActivatePdfPage(PdfSelectionLayer layer)
    {
        var index = PdfPageFor(layer);
        if (pdf == null || updatingPdfPages || index < 0 || pdfPage == index) return;
        if (fixedFootnoteAnchor is { } note && note.Link.SourcePage != index) ClosePanel(false);
        // ReaderSurface has already dismissed the previous card when it records a fresh mark tap.
        if (pendingSavedMarkTap is not { } tap || !ReferenceEquals(tap.Body, layer)) DismissInlineSelection();
        pdfMatch = null;
        ActivePdfSelection.ShowAnnotation([]);
        ActivePdfSelection.ClearSelection();
        pdfPage = index;
        UpdateActivePdfPage();
        SetProgress();
        SaveCurrentPosition();
    }

    private void UpdateActivePdfPage()
    {
        if (shownPanel == Panel.PdfPages)
        {
            JumpInput.Text = (pdfPage + 1).ToString(CultureInfo.InvariantCulture);
            UpdatePdfThumbnailSelection();
        }
    }

    private void OnPdfLeafFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!moving && !turningPage && !seekingPosition && sender is PdfSelectionLayer layer) ActivatePdfPage(layer);
    }

    private void OnPdfLeafPressed(object sender, MouseButtonEventArgs e)
    {
        if (!moving && !turningPage && !seekingPosition && sender is PdfSelectionLayer layer) ActivatePdfPage(layer);
    }

    private Task<bool> RenderPdfAsync() => RenderPdfPageAsync(pdfPage, resetScroll: false);

    private async Task ResizePdfAsync()
    {
        if (closing || pdf == null) return;
        if (moving || turningPage || seekingPosition || switchingReaderFullScreen)
        {
            pdfResizeTimer.Start();
            return;
        }
        if (await RenderPdfAsync()) SetProgress();
    }

    private async Task<bool> RenderPdfPageAsync(int target, bool resetScroll)
    {
        if (pdf is not { } document || reading is not { } session) return false;
        SuspendPdfThumbnails();
        pdfResizeTimer.Stop();
        pageRender?.Cancel();
        pageRender?.Dispose();
        pageRender = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        var token = pageRender.Token;
        var spread = WantsPdfSpread;
        var authored = document as FixedEpubPageBook;
        var layout = authored?.Layout.GetSpread(target, spread, ReadingLane.ActualWidth >= ReadingLaneHeight());
        var primary = layout is { } epubSpread ? epubSpread.Center ?? epubSpread.Left ?? epubSpread.Right ?? target :
            spread ? target & ~1 : target;
        var facing = layout is { } authoredSpread ? (authoredSpread.Left.HasValue ? authoredSpread.Right ?? -1 : -1) :
            spread && primary + 1 < document.PageCount ? primary + 1 : -1;
        bool blankLeft = layout is { Center: null, Left: null };
        bool blankRight = layout is { Center: null, Right: null };
        var zoom = pdfZoom;
        var viewport = new Size(Math.Max(1, PdfSurface.ActualWidth), Math.Max(1, PdfSurface.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(PdfPageImage).DpiScaleX;
        try
        {
            var leftSize = await document.ReadPageSizeAsync(primary, token);
            Size? rightSize = facing >= 0 ? await document.ReadPageSizeAsync(facing, token) :
                blankLeft || blankRight ? leftSize : null;
            var sizes = PdfDisplayGeometry.Measure(leftSize, rightSize, viewport, zoom, authored != null ? 0 : PdfDisplayGeometry.FacingGap);
            var left = await document.RenderPageAsync(primary, Pixels(sizes.Primary.Width, dpi), token);
            var right = facing < 0 ? null : await document.RenderPageAsync(facing, Pixels(sizes.Facing.Width, dpi), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(pdf, document) || !ReferenceEquals(reading, session) || closing) return false;
            if (!resetScroll && pdfPage != target)
            {
                if (pdfPage != primary && pdfPage != facing)
                {
                    QueuePdfResize();
                    return false;
                }
                target = pdfPage;
            }

            var layoutChanged = primary != pdfPrimaryPage || facing != pdfFacingPage;
            var restoreFocus = layoutChanged && ActivePdfSelection.IsKeyboardFocusWithin;
            var preserveText = authored != null && layoutChanged && !resetScroll && target == pdfPage;
            var savedMatch = preserveText && pdfMatch?.Page == target ? pdfMatch : null;
            var savedSelection = preserveText ? ActivePdfSelection.ReadSelection() : null;
            updatingPdfPages = true;
            try
            {
                if (layoutChanged)
                {
                    ResetPdfText();
                    pdfMatch = savedMatch;
                }
                pdfPrimaryPage = primary;
                pdfFacingPage = facing;
                pdfPage = target;
                pdfSpread = spread;
                SetAuthoredPageSpace(FixedEpubLeftSpace, blankLeft, sizes.Primary.Width);
                SetAuthoredPageSpace(FixedEpubRightSpace, blankRight, sizes.Primary.Width);
                PdfPrimaryBacking.Visibility = PdfFacingBacking.Visibility = authored == null ? Visibility.Visible : Visibility.Collapsed;
                PdfFacingPageHost.Margin = authored != null ? new Thickness(0) : new Thickness(PdfDisplayGeometry.FacingGap, 0, 0, 0);
                ShowPdfLeaf(PdfPageHost, PdfPageImage, PdfSelection, left, sizes.Primary, primary);
                ShowPdfLeaf(PdfFacingPageHost, PdfFacingImage, PdfFacingSelection, right, sizes.Facing, facing);
                SetFixedEpubLinks(PdfSelection, authored?.ReadLinks(primary) ?? []);
                SetFixedEpubLinks(PdfFacingSelection, authored != null && facing >= 0 ? authored.ReadLinks(facing) : []);
                if (resetScroll) PdfSurface.ScrollToHome();
                if (resetScroll || savedMatch != null) PdfSurface.UpdateLayout();
                RefreshSavedPdfMarks();
                if (layoutChanged)
                {
                    pdfTextReading = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    pdfTextLoad = LoadPdfTextAsync(document, primary, PdfSelection, pdfTextReading.Token,
                        primary == target ? savedSelection : null);
                    if (facing >= 0) pdfFacingTextLoad = LoadPdfTextAsync(document, facing, PdfFacingSelection, pdfTextReading.Token,
                        facing == target ? savedSelection : null);
                }
            }
            finally { updatingPdfPages = false; }
            if (fixedFootnoteAnchor != null)
            {
                PdfSurface.UpdateLayout();
                UpdateFixedFootnotePlacement();
            }
            if (restoreFocus && !ActivePdfSelection.IsKeyboardFocusWithin)
            {
                PdfSurface.UpdateLayout();
                ActivePdfSelection.Focus();
            }
            UpdateActivePdfPage();
            UpdatePdfSpreadStatus();
            if (!readingGoalsTimer.IsEnabled) RefreshReadingGoalsTracking();
            ShowStatus("");
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested) ShowStatus(error.Message); }
        finally
        {
            if (!token.IsCancellationRequested && shownPanel == Panel.PdfPages) UpdatePdfThumbnailSelection();
        }
        return false;

        static int Pixels(double width, double dpi) => (int)Math.Clamp(Math.Ceiling(width * dpi), 1, 4096);
    }

    private static void SetAuthoredPageSpace(FrameworkElement space, bool visible, double width)
    {
        space.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) space.Width = width;
    }

    private void ShowPdfLeaf(FrameworkElement host, Image image, PdfSelectionLayer selection,
        BitmapSource? bitmap, Size size, int page)
    {
        if (bitmap == null)
        {
            host.Visibility = Visibility.Collapsed;
            image.Source = null;
            return;
        }
        image.Source = bitmap;
        host.Width = size.Width;
        host.Height = size.Height;
        host.Visibility = Visibility.Visible;
        AutomationProperties.SetName(image, $"第 {page + 1} 页");
        AutomationProperties.SetName(selection, $"{(pdf is FixedEpubPageBook ? "EPUB" : "PDF")} 第 {page + 1} 页正文");
    }

    private async Task ChangePdfPageAsync(int index)
    {
        if (pdf == null) return;
        DismissInlineSelection();
        var next = Math.Clamp(index, 0, pdf.PageCount - 1);
        if (fixedFootnoteAnchor is { } note && note.Link.SourcePage != next) ClosePanel(false);
        if (pdfSpread == WantsPdfSpread && pdfSpread && PdfSelectionFor(next) is { } displayed)
            ActivatePdfPage(displayed);
        else if (!await RenderPdfPageAsync(next, resetScroll: true)) return;
        SetProgress();
        SaveCurrentPosition();
    }

    private int NextPdfPage(int direction)
    {
        if (pdf is FixedEpubPageBook)
        {
            if (pdfFacingPage < 0) return pdfPage + direction;
            return direction < 0 ? Math.Min(pdfPrimaryPage, pdfFacingPage) - 1 : Math.Max(pdfPrimaryPage, pdfFacingPage) + 1;
        }
        return (pdfSpread ? pdfPrimaryPage : pdfPage) + direction * (pdfSpread ? 2 : 1);
    }

    private void ResetPdfSelection()
    {
        pdfPrimaryPage = pdfFacingPage = -1;
        pdfSpread = false;
        ResetPdfText();
        PdfPageImage.Source = PdfFacingImage.Source = null;
        PdfPageHost.Visibility = PdfFacingPageHost.Visibility = Visibility.Collapsed;
        FixedEpubLeftSpace.Visibility = FixedEpubRightSpace.Visibility = Visibility.Collapsed;
        SetFixedEpubLinks(PdfSelection, []);
        SetFixedEpubLinks(PdfFacingSelection, []);
    }

    private void ResetPdfText()
    {
        pdfTextReading?.Cancel();
        pdfTextReading?.Dispose();
        pdfTextReading = null;
        pdfTextLoad = pdfFacingTextLoad = null;
        pdfMatch = null;
        PdfSelection.SetPage(null);
        PdfFacingSelection.SetPage(null);
    }

    private Task? ActivePdfTextLoad => ReferenceEquals(ActivePdfSelection, PdfSelection) ? pdfTextLoad : pdfFacingTextLoad;

    private async Task LoadPdfTextAsync(IPageBook source, int page, PdfSelectionLayer layer, CancellationToken token,
        PdfSelectionLayer.TextSelection? selection)
    {
        try
        {
            var textPage = await source.ReadTextPageAsync(page, token);
            if (token.IsCancellationRequested || !ReferenceEquals(pdf, source) || PdfPageFor(layer) != page) return;
            layer.SetPage(textPage);
            if (page != pdfPage) return;
            if (selection is { } saved) layer.RestoreSelection(saved);
            if (pdfMatch is { } hit) layer.ShowMatch(hit);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning($"Fixed page text extraction failed on page {page + 1}: {error.Message}");
        }
    }

    private void OnPdfSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => QueuePdfResize();

    private void QueuePdfResize()
    {
        if (pdf == null || pdfResizeTimer == null || switchingReaderFullScreen) return;
        pdfResizeTimer.Stop();
        pdfResizeTimer.Start();
    }

    private void UpdatePdfSpreadStatus()
    {
        PdfLayoutStatus.Text = pdfZoom != "spread" ? "" : continuous ? "左右翻页时可显示双页" :
            !WantsPdfSpread ? "窗口空间不足，暂用单页" : "";
        PdfLayoutStatus.Visibility = PdfLayoutStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task<string> LoadPdfViewAsync(IPageBook document)
    {
        await pdfViewWrite;
        var authored = document is FixedEpubPageBook;
        var saved = await store.GetPreferenceAsync(authored ? FixedEpubViewPreference : PdfViewPreference);
        return saved is "page" or "width" or "spread" or "150" ? saved : authored ? "spread" : "width";
    }

    private async Task SavePdfViewAfterAsync(Task previous, string preference, string view)
    {
        await previous;
        try { await store.SavePreferenceAsync(preference, view); }
        catch (Exception error) { if (!closing) ShowStatus(error.Message); }
    }
}
