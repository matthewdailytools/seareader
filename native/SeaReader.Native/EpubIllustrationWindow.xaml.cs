using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SeaReader.Native;

internal partial class EpubIllustrationWindow : Window
{
    private const double FitZoom = 1;
    private const double MaximumZoom = 4;
    private const double ZoomStep = 1.25;
    private const double ZoomTolerance = .001;
    private static readonly string[] ReaderBrushes = ["WindowSurface", "PaperSurface", "MainInk", "MutedInk", "BorderInk", "AccentInk"];

    private readonly EpubIllustration figure;
    private readonly EpubDocument? publication;
    private CancellationTokenSource? detailLoad;
    private double zoom = FitZoom;
    private Point? dragOrigin;
    private Point dragOffsets;

    internal EpubIllustrationWindow(EpubIllustration figure, Window owner, EpubDocument? publication = null)
    {
        this.figure = figure;
        this.publication = publication;
        InitializeComponent();
        Owner = owner;
        foreach (string key in ReaderBrushes)
            if (owner.TryFindResource(key) is { } brush) Resources[key] = brush;
        if (owner.TryFindResource(typeof(Button)) is Style buttonStyle) Resources[typeof(Button)] = buttonStyle;

        var workArea = SystemParameters.WorkArea;
        Width = Math.Clamp(workArea.Width - 32, MinWidth, Width);
        Height = Math.Clamp(workArea.Height - 32, MinHeight, Height);
        IllustrationImage.Source = figure.Bitmap;
        AutomationProperties.SetName(IllustrationImage,
            string.IsNullOrWhiteSpace(figure.AlternativeText) ? "插图" : figure.AlternativeText);
        RefreshZoomControls();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var theme = WindowsReaderTheme.Read();
        WindowsReaderTheme.SetDarkCaption(this, theme.Dark && !theme.HighContrast);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResizeImage();
        IllustrationScroll.Focus();
        if (publication == null || figure.DetailPath == null || detailLoad != null) return;

        using var load = new CancellationTokenSource();
        detailLoad = load;
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, load.Token);
            var detail = await publication.ReadIllustrationDetailAsync(figure, load.Token);
            if (detail != null && !load.IsCancellationRequested)
            {
                // The detailed source uses the dimensions of the existing viewing surface.
                IllustrationImage.Source = detail;
            }
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        finally { detailLoad = null; }
    }

    protected override void OnClosed(EventArgs e)
    {
        detailLoad?.Cancel();
        base.OnClosed(e);
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (zoom == FitZoom) ResizeImage();
        else ResizeAround(ViewportCenter());
    }

    private Point ViewportCenter() => new(IllustrationScroll.ViewportWidth / 2, IllustrationScroll.ViewportHeight / 2);

    private void OnZoomIn(object sender, RoutedEventArgs e) => ChangeZoom(zoom * ZoomStep, ViewportCenter());

    private void OnZoomOut(object sender, RoutedEventArgs e) => ChangeZoom(zoom / ZoomStep, ViewportCenter());

    private void OnZoomFit(object sender, RoutedEventArgs e) => ChangeZoom(FitZoom, ViewportCenter());

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnZoomWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        ChangeZoom(zoom * Math.Pow(ZoomStep, e.Delta / 120.0), e.GetPosition(IllustrationScroll));
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;

        switch (e.Key)
        {
            case Key.Add:
            case Key.OemPlus:
                ChangeZoom(zoom * ZoomStep, ViewportCenter());
                break;
            case Key.Subtract:
            case Key.OemMinus:
                ChangeZoom(zoom / ZoomStep, ViewportCenter());
                break;
            case Key.D0:
            case Key.NumPad0:
                ChangeZoom(FitZoom, ViewportCenter());
                break;
            case Key.Escape when Keyboard.Modifiers == ModifierKeys.None:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ChangeZoom(double value, Point anchor)
    {
        var next = Math.Clamp(value, FitZoom, MaximumZoom);
        if (Math.Abs(next - zoom) < ZoomTolerance) return;
        zoom = next;
        EndPan();
        ResizeAround(anchor);
        RefreshZoomControls();
    }

    private void ResizeAround(Point anchor)
    {
        if (zoom == FitZoom || IllustrationImage.ActualWidth <= 0 || IllustrationImage.ActualHeight <= 0)
        {
            ResizeImage();
            IllustrationScroll.ScrollToHome();
            return;
        }

        var origin = IllustrationImage.TranslatePoint(new Point(), IllustrationScroll);
        double imageX = Math.Clamp((anchor.X - origin.X) / IllustrationImage.ActualWidth, 0, 1);
        double imageY = Math.Clamp((anchor.Y - origin.Y) / IllustrationImage.ActualHeight, 0, 1);
        if (!ResizeImage()) return;

        IllustrationScroll.UpdateLayout();
        origin = IllustrationImage.TranslatePoint(new Point(), IllustrationScroll);
        IllustrationScroll.ScrollToHorizontalOffset(IllustrationScroll.HorizontalOffset + origin.X +
            imageX * IllustrationImage.ActualWidth - anchor.X);
        IllustrationScroll.ScrollToVerticalOffset(IllustrationScroll.VerticalOffset + origin.Y +
            imageY * IllustrationImage.ActualHeight - anchor.Y);
    }

    private bool ResizeImage()
    {
        var padding = IllustrationSurface.Padding;
        double width = IllustrationScroll.ActualWidth - padding.Left - padding.Right - 2;
        double height = IllustrationScroll.ActualHeight - padding.Top - padding.Bottom - 2;
        if (width <= 0 || height <= 0) return false;

        var bitmap = figure.Bitmap;
        var scale = Math.Min(1, Math.Min(width / bitmap.PixelWidth, height / bitmap.PixelHeight)) * zoom;
        double imageWidth = bitmap.PixelWidth * scale;
        double imageHeight = bitmap.PixelHeight * scale;
        if (IllustrationImage.Width == imageWidth && IllustrationImage.Height == imageHeight) return false;
        IllustrationImage.Width = imageWidth;
        IllustrationImage.Height = imageHeight;
        return true;
    }

    private void RefreshZoomControls()
    {
        ZoomLevelText.Text = $"{zoom * 100:0}%";
        AutomationProperties.SetName(ZoomLevelText, $"缩放 {ZoomLevelText.Text}");
        ZoomOutButton.IsEnabled = ZoomFitButton.IsEnabled = zoom > FitZoom + ZoomTolerance;
        ZoomInButton.IsEnabled = zoom < MaximumZoom - ZoomTolerance;
    }

    private void OnPanDown(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice != null || IllustrationScroll.ScrollableWidth <= 0 && IllustrationScroll.ScrollableHeight <= 0 ||
            !IllustrationSurface.CaptureMouse()) return;

        IllustrationScroll.Focus();
        dragOrigin = e.GetPosition(IllustrationScroll);
        dragOffsets = new Point(IllustrationScroll.HorizontalOffset, IllustrationScroll.VerticalOffset);
        IllustrationSurface.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (dragOrigin is not { } origin) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPan();
            return;
        }

        var movement = e.GetPosition(IllustrationScroll) - origin;
        IllustrationScroll.ScrollToHorizontalOffset(dragOffsets.X - movement.X);
        IllustrationScroll.ScrollToVerticalOffset(dragOffsets.Y - movement.Y);
        e.Handled = true;
    }

    private void OnPanUp(object sender, MouseButtonEventArgs e)
    {
        if (dragOrigin == null) return;
        EndPan();
        e.Handled = true;
    }

    private void OnPanLostCapture(object sender, MouseEventArgs e) => EndPan();

    private void EndPan()
    {
        dragOrigin = null;
        IllustrationSurface.ClearValue(CursorProperty);
        if (IllustrationSurface.IsMouseCaptured) IllustrationSurface.ReleaseMouseCapture();
    }
}
