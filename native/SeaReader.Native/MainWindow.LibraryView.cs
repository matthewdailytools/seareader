using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const string LibraryDisplayPreference = "library-display-mode";
    private const string LibrarySortPreference = "library-sort-order";
    private const string LibraryRowsMode = "rows";
    private Task? libraryViewPreferences;
    private Task libraryPreferenceWrite = Task.CompletedTask;

    private string CurrentLibrarySort => LibrarySort.SelectedItem is ComboBoxItem { Tag: string order } ? order : "recent";

    private bool ShouldRefreshLibraryMetadataOrder => LibrarySearch.Text.Trim().Length > 0 ||
        (librarySectionKey != LibraryHomeKey || ActiveLibraryCollection != null) && CurrentLibrarySort is "title" or "author";

    private Task LoadLibraryViewPreferencesAsync() => libraryViewPreferences ??= ReadLibraryViewPreferencesAsync();

    private async Task ReadLibraryViewPreferencesAsync()
    {
        string? presentation = null;
        string? order = null;
        try
        {
            var display = store.GetPreferenceAsync(LibraryDisplayPreference);
            var sort = store.GetPreferenceAsync(LibrarySortPreference);
            await Task.WhenAll(display, sort);
            presentation = await display;
            order = await sort;
        }
        catch (InvalidOperationException error) { Trace.TraceWarning("SeaReader library preferences: {0}", error.Message); }
        if (closing) return;

        ApplyLibraryDisplay(presentation == LibraryRowsMode, keepLocation: false);
        if (order is "recent" or "title" or "author" or "manual") SelectTag(LibrarySort, order);
    }

    private IEnumerable<BookEntry> OrderLibraryEntries(IEnumerable<BookEntry> entries)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        return CurrentLibrarySort switch
        {
            "title" => entries.OrderBy(entry => entry.Display.Title, comparer),
            "author" => entries.OrderBy(entry => string.IsNullOrWhiteSpace(entry.Display.Author))
                .ThenBy(entry => entry.Display.Author, comparer)
                .ThenBy(entry => entry.Display.Title, comparer)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal),
            "manual" => OrderLibraryManually(entries, LibraryOrderShelf),
            _ => entries
        };
    }

    private void OnToggleLibraryView(object sender, RoutedEventArgs e)
    {
        if (libraryViewPreferences is not { IsCompletedSuccessfully: true } || closing) return;
        bool rows = !Equals(LibraryList.Tag, LibraryRowsMode);
        ApplyLibraryDisplay(rows, keepLocation: true);
        QueueLibraryPreference(LibraryDisplayPreference, rows ? LibraryRowsMode : "grid");
    }

    private void ApplyLibraryDisplay(bool rows, bool keepLocation)
    {
        var focused = keepLocation ? FindLibraryBookContainer(Keyboard.FocusedElement)?.DataContext as BookEntry : null;
        var position = keepLocation ? CaptureLibraryBrowsePosition() : (LibraryBrowsePosition?)null;
        LibraryList.Tag = rows ? LibraryRowsMode : null;
        LibraryViewToggle.Content = rows ? "封面" : "列表";
        string action = rows ? "切换为封面视图" : "切换为列表视图";
        LibraryViewToggle.ToolTip = action;
        AutomationProperties.SetName(LibraryViewToggle, action);

        if (focused != null)
        {
            LibraryList.UpdateLayout();
            if (LibraryList.ItemContainerGenerator.ContainerFromItem(focused) is ListBoxItem item)
            {
                CancelPendingLibraryViewport();
                item.Focus();
                item.BringIntoView();
                return;
            }
        }
        if (position is { } saved) QueueLibraryViewportRestore(saved);
    }

    private void QueueLibraryPreference(string key, string value) =>
        libraryPreferenceWrite = SaveLibraryPreferenceAsync(libraryPreferenceWrite, key, value);

    private async Task SaveLibraryPreferenceAsync(Task priorWrite, string key, string value)
    {
        await priorWrite;
        try { await store.SavePreferenceAsync(key, value); }
        catch (InvalidOperationException error) { SetLibraryStatus(error.Message); }
    }
}
