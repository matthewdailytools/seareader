using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SeaReader.Native;

public partial class MainWindow
{
    private void OnSubscriptions(object sender, RoutedEventArgs e)
    {
        bool downloads = sender is Button { Tag: "Downloads" };
        if (subscriptionsWindow != null)
        {
            subscriptionsWindow.SelectSection(downloads);
            subscriptionsWindow.Activate();
            return;
        }

        var window = new SubscriptionWindow(store, OpenDownloadedBookAsync, refreshLibrary: ReloadLibraryAsync)
        {
            Owner = this
        };
        window.SelectSection(downloads);
        subscriptionsWindow = window;
        window.Closed += (_, _) => subscriptionsWindow = null;
        window.Show();
    }

    private async Task OpenDownloadedBookAsync(string id)
    {
        await ReloadLibraryAsync();
        var downloaded = books.FirstOrDefault(entry => entry.Id == id);
        if (downloaded == null) throw new InvalidOperationException("书库中找不到这本书。");
        await OpenLibraryBookAsync(downloaded);
        if (readingWindows == null) Activate();
    }

    private void OnReaderTools(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void OnReaderToolsOpened(object sender, RoutedEventArgs e) => hoverTimer.Stop();

    private void OnReaderToolsClosed(object sender, RoutedEventArgs e)
    {
        if (hoverVisible) RestartHoverTimer();
    }

    private async void OnReaderToolsKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.F11 && modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (!e.IsRepeat) await ToggleReaderFullScreenAsync();
        }
        else if (e.Key == Key.Escape)
        {
            if (e.IsRepeat) e.Handled = true;
            else if (sender is ContextMenu menu && !menu.Items.OfType<MenuItem>().Any(item => item.IsSubmenuOpen))
            {
                e.Handled = true;
                CloseReaderTools();
            }
        }
        else HandleReaderNavigationShortcut(e, modifiers);
    }

    private void CloseReaderTools()
    {
        ReaderToolsButton.ContextMenu.IsOpen = false;
        ReaderToolsButton.Focus();
    }

    private void OnTranslate(object sender, RoutedEventArgs e) => ShowTranslation(this, SelectedReaderText());

    private void ShowTranslation(Window owner, string selection)
    {
        new TranslationWindow(store, selection) { Owner = owner }.ShowDialog();
    }

    private async void OnOcr(object sender, RoutedEventArgs e)
    {
        string? id = book?.Id;
        int? page = null;
        BitmapSource? image = null;
        if (pdf is { } source && ReaderSurface.Visibility == Visibility.Visible)
        {
            page = pdfPage;
            var token = reading?.Token ?? CancellationToken.None;
            try
            {
                var rendered = await source.RenderPageAsync(page.Value, 1800, token);
                if (!ReferenceEquals(source, pdf) || page != pdfPage || closing) return;
                image = rendered;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { ShowStatus(error.Message); }
        }

        var dialog = new OcrWindow(store, id, page + 1, image, result => ShowTranslation(this, result))
        {
            Owner = this
        };
        dialog.ShowDialog();
    }
}
