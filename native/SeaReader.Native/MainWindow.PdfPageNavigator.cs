using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace SeaReader.Native;

public partial class MainWindow
{
    private void FocusSelectedPdfThumbnail()
    {
        if (PdfThumbnailList.SelectedItem is { } selected)
        {
            PdfThumbnailList.ScrollIntoView(selected);
            PdfThumbnailList.UpdateLayout();
            if (PdfThumbnailList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item && item.Focus()) return;
        }
        PdfThumbnailList.Focus();
    }

    private void PlacePdfNavigationPanel()
    {
        const double inset = 12;
        const double bottomClearance = 58;
        const double headerHeight = 102;
        const double rowHeight = 121;
        var width = Math.Min(236, Math.Max(0, ReaderSurface.ActualWidth - inset * 2));
        var anchor = ContentsButton.TranslatePoint(new Point(ContentsButton.ActualWidth / 2, ContentsButton.ActualHeight), ReaderSurface);
        var top = anchor.Y + 7;
        var left = Math.Clamp(anchor.X - width / 2, inset, Math.Max(inset, ReaderSurface.ActualWidth - width - inset));
        var availableHeight = Math.Max(0, ReaderSurface.ActualHeight - top - bottomClearance);
        var desiredHeight = headerHeight + Math.Min(pdf?.PageCount ?? 0, 4) * rowHeight;
        PdfNavigationPanel.Width = width;
        PdfNavigationPanel.MaxHeight = Math.Min(availableHeight, desiredHeight);
        PdfNavigationPanel.Margin = new Thickness(left, top, 0, 0);
    }

    private async void OnPdfThumbnailClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject element ||
            ItemsControl.ContainerFromElement(PdfThumbnailList, element) is not ListBoxItem { DataContext: PdfPageThumbnail thumbnail }) return;
        e.Handled = true;
        await SelectPdfNavigationPageAsync(thumbnail.Index);
    }

    private async void OnPdfThumbnailKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || PdfThumbnailList.SelectedItem is not PdfPageThumbnail thumbnail) return;
        e.Handled = true;
        await SelectPdfNavigationPageAsync(thumbnail.Index);
    }

    private async Task SelectPdfNavigationPageAsync(int target)
    {
        if (shownPanel != Panel.PdfPages || pdf is not { } document ||
            moving || turningPage || seekingPosition || target < 0 || target >= document.PageCount) return;
        ClosePanel(false);
        await SeekPageAsync(target);
    }

    private void OnPdfJumpTextChanged(object sender, TextChangedEventArgs e) => SetPdfJumpError("");

    private void SetPdfJumpError(string message)
    {
        if (PdfJumpError is not { } error || error.Text == message) return;
        error.Text = message;
        error.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (message.Length != 0 && error.IsVisible && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(error)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
