using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private readonly record struct PdfFullScreenLocation(IPageBook Document, int Page, string Zoom, double X, double Y);
    private ReaderFullScreenWindow? readerFullScreen;
    private bool switchingReaderFullScreen;
    private (ReaderLocation Windowed, ReaderLocation FullScreen)? continuousFullScreenLocation;
    private (PdfFullScreenLocation Windowed, PdfFullScreenLocation FullScreen)? pdfFullScreenLocation;

    private bool IsReaderFullScreen => readerFullScreen?.IsActive == true;

    private async void OnToggleReaderFullScreen(object sender, RoutedEventArgs e) => await ToggleReaderFullScreenAsync();

    private async Task ToggleReaderFullScreenAsync()
    {
        if (book is not { } opened || reading is not { } session || closing || moving || turningPage ||
            seekingPosition || switchingReaderFullScreen) return;

        var entering = !IsReaderFullScreen;
        var textLocation = text == null ? (ReaderLocation?)null : CaptureLayoutLocation();
        var pdfLocation = ReadPdfFullScreenLocation();
        if (!entering)
        {
            if (continuous && textLocation is { } current && continuousFullScreenLocation is { } remembered &&
                SameReaderLocation(current, remembered.FullScreen)) textLocation = remembered.Windowed;
            if (pdfLocation is { } viewport && pdfFullScreenLocation is { } pdfRemembered &&
                SamePdfFullScreenLocation(viewport, pdfRemembered.FullScreen)) pdfLocation = pdfRemembered.Windowed;
        }

        var previousResize = pageResizeLocation;
        var previousSpread = spreadReflowLocation;
        var previousPageLayout = paginationLayout;
        if (!continuous && textLocation is { } pagedPosition)
        {
            if (HasSpread) spreadReflowLocation = pagedPosition;
            else pageResizeLocation = pagedPosition;
        }

        switchingReaderFullScreen = true;
        try
        {
            readerFullScreen ??= new ReaderFullScreenWindow(this);
            var changed = entering ? readerFullScreen.TryEnter() : readerFullScreen.Exit();
            UpdateReaderFullScreenControls();
            if (!changed)
            {
                pageResizeLocation = previousResize;
                spreadReflowLocation = previousSpread;
                ShowStatus(entering ? "暂时无法进入全屏。" : "暂时无法恢复窗口。");
                return;
            }

            ResetFullScreenLocations();
            SetControls(false);
            ResetPageTransition();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Loaded);
            if (!ReferenceEquals(book, opened) || !ReferenceEquals(reading, session) || closing) return;

            if (pdf != null)
            {
                await RenderPdfAsync();
                if (pdfLocation is { } pdfPosition && ReferenceEquals(pdf, pdfPosition.Document) &&
                    pdfPage == pdfPosition.Page && pdfZoom == pdfPosition.Zoom && !closing)
                {
                    PdfSurface.UpdateLayout();
                    PdfSurface.ScrollToHorizontalOffset(pdfPosition.X * ActivePdfPageHost.ActualWidth);
                    PdfSurface.ScrollToVerticalOffset(pdfPosition.Y * ActivePdfPageHost.ActualHeight);
                    PdfSurface.UpdateLayout();
                    if (entering && ReadPdfFullScreenLocation() is { } displayed)
                        pdfFullScreenLocation = (pdfPosition, displayed);
                }
            }
            else if (continuous && textLocation is { } scrollPosition)
            {
                await RestoreReaderLocationAsync(scrollPosition);
                if (entering && ReferenceEquals(book, opened) && ReferenceEquals(reading, session))
                    continuousFullScreenLocation = (scrollPosition, CaptureReaderLocation());
            }
            else
            {
                SetProgress();
                if (doublePage == WantsSpread && paginationLayout is { } currentLayout && currentLayout == previousPageLayout)
                {
                    pageResizeLocation = previousResize;
                    spreadReflowLocation = previousSpread;
                }
            }
            if (ReferenceEquals(book, opened) && ReferenceEquals(reading, session) && !closing &&
                !controlsVisible && shownPanel == null) FocusReadingSurface();
        }
        catch (Exception error) { if (ReferenceEquals(book, opened) && !closing) ShowStatus(error.Message); }
        finally
        {
            switchingReaderFullScreen = false;
            QueueSpreadUpdate();
        }
    }

    private void LeaveReaderFullScreen()
    {
        ResetFullScreenLocations();
        if (!IsReaderFullScreen) return;
        if (!readerFullScreen!.Exit()) ShowStatus("暂时无法恢复窗口。");
        UpdateReaderFullScreenControls();
    }

    private void ResetFullScreenLocations()
    {
        continuousFullScreenLocation = null;
        pdfFullScreenLocation = null;
    }

    private void UpdateReaderFullScreenControls()
    {
        var label = IsReaderFullScreen ? "退出全屏" : "全屏阅读";
        ReaderFullScreenMenu.Header = label;
        AutomationProperties.SetName(ReaderFullScreenMenu, label);
        ExitReaderFullScreenButton.Visibility = IsReaderFullScreen ? Visibility.Visible : Visibility.Collapsed;
    }

    private PdfFullScreenLocation? ReadPdfFullScreenLocation()
    {
        if (pdf is not { } document || ActivePdfPageHost.ActualWidth <= 0 || ActivePdfPageHost.ActualHeight <= 0) return null;
        return new PdfFullScreenLocation(document, pdfPage, pdfZoom,
            PdfSurface.HorizontalOffset / ActivePdfPageHost.ActualWidth,
            PdfSurface.VerticalOffset / ActivePdfPageHost.ActualHeight);
    }

    private static bool SamePdfFullScreenLocation(PdfFullScreenLocation a, PdfFullScreenLocation b) =>
        ReferenceEquals(a.Document, b.Document) && a.Page == b.Page && a.Zoom == b.Zoom &&
        Math.Abs(a.X - b.X) < .0001 && Math.Abs(a.Y - b.Y) < .0001;
}
