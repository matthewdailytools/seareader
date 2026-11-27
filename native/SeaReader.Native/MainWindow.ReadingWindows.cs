using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const string ReaderWindowPreferencePrefix = "reader-window:";
    private ReaderWindowManager? readingWindows;
    private ReaderWindowPlacement? readerWindowPlacement;
    private bool readerOpenedForPlacement;
    private bool isDedicatedReader;
    private bool refreshingSharedLibrary;
    private bool sharedLibraryRefreshRequested;

    internal bool IsClosingWindow => closing;
    internal string ReaderOpenFailure => StatusText.Text;
    internal bool CanRememberWindowedBounds => !IsReaderFullScreen && !switchingReaderFullScreen;

    internal void AttachReadingWindows(ReaderWindowManager manager, BookEntry? dedicatedBook = null)
    {
        readingWindows = manager;
        isDedicatedReader = dedicatedBook != null;
        if (dedicatedBook == null) return;

        if (!dedicatedBook.Format.Equals("pdf", StringComparison.OrdinalIgnoreCase))
            Width = ReaderPageGeometry.DefaultReaderWindowWidth;
        var display = dedicatedBook.Display;
        SetBinding(TitleProperty, new Binding(nameof(BookLibraryDisplay.Title))
        {
            Source = display, StringFormat = "{0} – SeaReader"
        });
        ReaderLoadingText.Text = $"正在打开《{display.Title}》…";
        ReaderLoadingSurface.Visibility = Visibility.Visible;
        LibrarySurface.Visibility = Visibility.Collapsed;
        ReaderLibraryButton.Content = "书库";
        ReaderLibraryButton.ToolTip = "打开书库窗口（Ctrl+L）";
        AutomationProperties.SetAcceleratorKey(ReaderLibraryButton, "Ctrl+L");
    }

    internal async Task<bool> OpenDedicatedBookAsync(BookEntry entry)
    {
        if (!isDedicatedReader || closing) return false;
        await OpenBookAsync(entry);
        readerOpenedForPlacement = !closing && book?.Id == entry.Id && (text != null || PdfPageImage.Source != null);
        return readerOpenedForPlacement;
    }

    internal async Task PrepareReaderWindowPlacementAsync(BookEntry entry, Window? origin)
    {
        ReaderWindowPlacement.SavedBounds? saved = null;
        try
        {
            var value = await store.GetPreferenceAsync(ReaderWindowPreferencePrefix + entry.Id);
            if (value != null) saved = JsonSerializer.Deserialize<ReaderWindowPlacement.SavedBounds>(value, PositionJson);
        }
        catch (Exception error) { Trace.TraceWarning($"Unable to load reader window placement: {error.Message}"); }
        if (closing) return;
        readerWindowPlacement = new ReaderWindowPlacement(this);
        readerWindowPlacement.Prepare(saved, origin);
    }

    private async Task SaveReaderWindowPlacementAsync()
    {
        if (!readerOpenedForPlacement || book is not { } opened || readerWindowPlacement?.Capture() is not { } bounds) return;
        try { await store.SavePreferenceAsync(ReaderWindowPreferencePrefix + opened.Id, JsonSerializer.Serialize(bounds, PositionJson)); }
        catch (Exception error) { Trace.TraceWarning($"Unable to save reader window placement: {error.Message}"); }
    }

    internal async Task<bool> RestoreContinueEntryAsync(BookEntry entry)
    {
        if (closing || !entry.ContinueHidden || book?.Id != entry.Id) return false;
        try
        {
            await store.SetContinueHiddenAsync(entry.Id, hidden: false);
            return true;
        }
        catch (Exception error)
        {
            if (!closing && book?.Id == entry.Id) ShowStatus(error.Message);
            return false;
        }
    }

    private Task OpenLibraryBookAsync(BookEntry entry)
    {
        if (closing) return Task.CompletedTask;
        return readingWindows is { } manager ? manager.OpenReaderAsync(entry with { Display = GetLibraryDisplay(entry) })
            : OpenBookAsync(entry);
    }

    private async Task ShowReadingLibraryAsync()
    {
        if (readingWindows is not { } manager || closing) return;
        SaveCurrentPosition();
        await FlushPositionAsync();
        if (!closing) manager.ShowLibrary();
    }

    internal void ShowLibraryFailure(string message) => SetLibraryStatus(message);

    internal async Task RefreshSharedLibraryAsync()
    {
        if (closing || !LibrarySurface.IsVisible) return;
        sharedLibraryRefreshRequested = true;
        if (refreshingSharedLibrary) return;
        refreshingSharedLibrary = true;
        try
        {
            while (sharedLibraryRefreshRequested && !closing)
            {
                sharedLibraryRefreshRequested = false;
                await ReloadLibraryAsync();
            }
        }
        catch (Exception error) { if (!closing) SetLibraryStatus(error.Message); }
        finally { refreshingSharedLibrary = false; }
    }
}
