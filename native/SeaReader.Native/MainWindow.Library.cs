using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const string LibraryHomeKey = "home";
    private const string LibraryAllKey = "all";
    private const string LibraryWantKey = "want-to-read";
    private const string LibraryFinishedKey = "finished";
    private const int LibraryHomeLimit = 12;
    private const int LibraryPageSize = 48;
    private const double LibraryNarrowWidth = 650;

    private static readonly LibrarySection[] MainLibrarySections =
    [
        new(LibraryHomeKey, "首页", Geometry.Parse("M1,8 L8,2 L15,8 M3,7 L3,14 L13,14 L13,7 M6,14 L6,10 L10,10 L10,14")),
        new(LibraryAllKey, "全部书籍", Geometry.Parse("M2,3 L5,3 L5,13 L2,13 Z M7,3 L10,3 L10,13 L7,13 Z M12,3 L15,12")),
        new(LibraryWantKey, "想读", Geometry.Parse("M4,2 L12,2 L12,14 L8,11 L4,14 Z")),
        new(LibraryFinishedKey, "已读完", Geometry.Parse("M3,3 L13,3 L13,13 L3,13 Z M5,8 L7,10 L11,6")),
        new("epub", "EPUB", Geometry.Parse("M8,4 C6,2 3,2 1,3 L1,13 C3,12 6,12 8,14 C10,12 13,12 15,13 L15,3 C13,2 10,2 8,4 Z M8,4 L8,14")),
        new("txt", "TXT", Geometry.Parse("M3,2 L12,2 L13,3 L13,14 L3,14 Z M5,6 L11,6 M5,9 L11,9 M5,12 L9,12")),
        new("pdf", "PDF", Geometry.Parse("M3,2 L9,2 L13,6 L13,14 L3,14 Z M9,2 L9,6 L13,6 M5,10 L11,10")),
    ];

    private static readonly (string Key, string Light, string Dark, ResourceKey Contrast)[] LibraryPalette =
    [
        ("LibraryCanvas", "#F8F7F3", "#222827", SystemColors.WindowBrushKey),
        ("LibraryRail", "#EEEFE8", "#252F2D", SystemColors.WindowBrushKey),
        ("LibraryPaper", "#FDFCF8", "#2C3733", SystemColors.WindowBrushKey),
        ("LibraryInk", "#233733", "#E4EFEA", SystemColors.WindowTextBrushKey),
        ("LibraryQuiet", "#576862", "#AFC3BA", SystemColors.WindowTextBrushKey),
        ("LibraryRule", "#D6DED4", "#455750", SystemColors.WindowTextBrushKey),
        ("LibraryAccent", "#2E6A5E", "#A0D3BF", SystemColors.WindowTextBrushKey),
        ("LibrarySelection", "#DCEBE2", "#354D44", SystemColors.WindowBrushKey),
        ("LibraryRecent", "#EEF3EA", "#2B3B34", SystemColors.WindowBrushKey),
        ("LibraryOnAccent", "#FFFFFF", "#192D27", SystemColors.WindowBrushKey),
    ];

    private string librarySectionKey = LibraryHomeKey;
    private bool refreshingLibrarySections;
    private bool libraryNarrow;
    private int libraryVisibleLimit = LibraryPageSize;
    private int libraryReloadVersion;

    private sealed record LibrarySection(string Key, string Label, Geometry Glyph);

    private async Task ReloadLibraryAsync()
    {
        int request = ++libraryReloadVersion;
        var entries = store.ListAsync();
        var organization = store.GetLibraryOrganizationAsync();
        var metadata = LoadLibraryBookMetadataAsync();
        var presentation = LoadLibraryViewPreferencesAsync();
        int orderVersion = libraryManualOrderVersion;
        var manualOrders = ReadLibraryManualOrdersAsync();
        await Task.WhenAll(entries, organization, metadata, presentation, manualOrders);
        if (request != libraryReloadVersion || closing) return;

        libraryOrganization = await organization;
        if (orderVersion == libraryManualOrderVersion) libraryManualOrders = await manualOrders;
        books = (await entries).Select(entry => entry with
        {
            ReadingState = libraryOrganization.ReadingStates.GetValueOrDefault(entry.Id),
            Display = GetLibraryDisplay(entry)
        }).ToArray();
        UpdateLibraryMetadataQueue();
        UpdateLibraryColors();

        var assigned = MainLibrarySections.Select(section => section.Key);
        var additional = books.Select(entry => entry.Format)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Except(assigned, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(format => new LibrarySection(
                format.ToLowerInvariant(), format.ToUpperInvariant(),
                MainLibrarySections[^1].Glyph));
        LibrarySection[] sections = [.. MainLibrarySections, .. additional];
        RefreshLibraryRoutes(sections);

        var snapshot = books;
        var recent = UpdateContinueCards(snapshot.Where(entry => entry.OpenedAt.HasValue && !entry.ContinueHidden &&
            entry.ReadingState != LibraryReadingState.Finished).Take(3));
        RefreshLibraryRouteContent(preserveSelection: true);
        PruneLibraryBrowsePositions(sections);
        if (recent.Length > 0) _ = LoadLibraryResumeCardsAsync(recent, snapshot);
    }

    private void UpdateLibraryColors()
    {
        var dark = systemTheme.Dark;
        foreach (var (key, light, night, contrast) in LibraryPalette)
        {
            var color = systemTheme.HighContrast && TryFindResource(contrast) is SolidColorBrush systemBrush
                ? systemBrush.Color
                : (Color)ColorConverter.ConvertFromString(dark ? night : light);
            if (LibrarySurface.Resources[key] is not SolidColorBrush existing || existing.Color != color)
                LibrarySurface.Resources[key] = new SolidColorBrush(color);
        }
    }

    private void SetLibraryStatus(string message)
    {
        if (message.Length != 0 && LibrarySurface.Visibility != Visibility.Visible) return;
        LibraryStatusText.Text = message;
        LibraryStatus.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (LibraryStatus.Visibility == Visibility.Visible && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(LibraryStatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void FilterLibrary(bool preserveSelection = false, LibraryBrowsePosition? restore = null)
    {
        if (LibraryList == null || LibrarySearch == null || LibrarySections == null) return;

        string query = LibrarySearch.Text.Trim();
        bool searching = query.Length > 0;
        var collection = ActiveLibraryCollection;
        bool home = collection == null && librarySectionKey == LibraryHomeKey;
        bool homeShelf = home && !searching;

        IEnumerable<BookEntry> matches = EntriesOnLibraryShelf(LibraryOrderShelf);
        if (searching) matches = matches.Where(entry =>
            entry.Display.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            entry.Display.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            entry.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        if (!homeShelf) matches = OrderLibraryEntries(matches);
        var results = matches.ToArray();

        var anchor = restore ?? (preserveSelection ? CaptureLibraryBrowsePosition() : null);
        if (!homeShelf && anchor is { } old) IncludeSavedLibraryBooks(results, old);
        int visible = homeShelf ? LibraryHomeLimit : libraryVisibleLimit;
        var shown = results.Take(visible).ToArray();
        bool replaced = !LibraryList.Items.OfType<BookEntry>().SequenceEqual(shown);
        if (replaced) UpdateLibraryCards(shown, preserveSelection);
        if (restore is { } saved) LibraryList.SelectedItem = shown.FirstOrDefault(entry => entry.Id == saved.SelectedBook);
        LibraryContinueSection.Visibility = homeShelf && LibraryContinueList.HasItems ? Visibility.Visible : Visibility.Collapsed;
        var resume = homeShelf ? LibraryContinueList.Items.OfType<LibraryResumeCard>().Select(card => card.Book) : [];
        SetLibraryPreviewTargets(resume.Concat(shown));
        LibrarySeeAll.Visibility = homeShelf && results.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryViewToggle.Visibility = results.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LibrarySort.Visibility = !homeShelf && results.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        LibraryManualHint.Visibility = !homeShelf && !searching && CurrentLibrarySort == "manual" && results.Length > 1
            ? Visibility.Visible : Visibility.Collapsed;
        LibraryMoreButton.Visibility = !homeShelf && results.Length > visible ? Visibility.Visible : Visibility.Collapsed;
        var section = LibrarySections.SelectedItem as LibrarySection;
        LibraryViewTitle.Text = searching ? "搜索结果" : collection?.Name ?? section?.Label ?? "首页";
        LibraryCollectionTitle.Text = homeShelf ? "书架" : "书目";
        LibraryCollectionActions.Visibility = collection != null ? Visibility.Visible : Visibility.Collapsed;
        LibraryCount.Text = results.Length > 0 ? $"{results.Length} 本" : "";
        EmptyLibrary.Visibility = results.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyImport.Visibility = !searching && (collection != null ||
            librarySectionKey is not (LibraryWantKey or LibraryFinishedKey)) ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyMessage.Text = searching ? "没有匹配的书籍。" : collection != null
            ? "导入书籍，或通过其他书卡上的更多菜单加入收藏。"
            : librarySectionKey switch
            {
                LibraryWantKey => "通过书卡上的更多菜单把书标为想读。",
                LibraryFinishedKey => "通过书卡上的更多菜单把书标为已读完。",
                _ when books.Count == 0 => "将电子书拖入窗口，或从文件导入。",
                _ => $"书库中还没有 {section?.Label} 书籍。"
            };
        if ((replaced || restore.HasValue) && anchor is { } position) QueueLibraryViewportRestore(position);
    }

    private void UpdateLibraryCards(BookEntry[] shown, bool preserveSelection)
    {
        var selectedId = preserveSelection ? (LibraryList.SelectedItem as BookEntry)?.Id : null;
        var focusedId = preserveSelection ? (FindLibraryBookContainer(Keyboard.FocusedElement)?.DataContext as BookEntry)?.Id : null;
        LibraryList.ItemsSource = shown;
        if (selectedId != null) LibraryList.SelectedItem = shown.FirstOrDefault(entry => entry.Id == selectedId);
        if (focusedId == null || shown.FirstOrDefault(entry => entry.Id == focusedId) is not { } item) return;

        LibraryList.UpdateLayout();
        if (LibraryList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) container.Focus();
    }

    private void OnLibrarySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LibrarySections == null) return;
        bool narrow = e.NewSize.Width < LibraryNarrowWidth;
        LibraryRailScroll.MaxHeight = narrow ? Math.Max(120, Math.Floor(e.NewSize.Height * .44)) : double.PositiveInfinity;
        if (narrow == libraryNarrow) return;
        libraryNarrow = narrow;

        LibraryRailColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(200);
        LibraryContentColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(LibraryContent, narrow ? 0 : 1);
        Grid.SetRow(LibraryContent, narrow ? 1 : 0);
        Grid.SetRowSpan(LibraryNavigationRail, narrow ? 1 : 2);
        Grid.SetRowSpan(LibraryContent, narrow ? 1 : 2);
        LibraryNavigationRail.Padding = narrow ? new Thickness(12, 7, 12, 7) : new Thickness(14, 20, 14, 13);
        LibraryNavigationRail.BorderThickness = narrow ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
        LibraryBrand.Visibility = LibraryRailSeparator.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        LibraryRailScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        LibraryServiceLinks.Orientation = narrow ? Orientation.Horizontal : Orientation.Vertical;
        LibraryContinueList.Tag = narrow ? "narrow" : null;
        LibrarySections.ItemsPanel =
            (ItemsPanelTemplate)LibrarySurface.FindResource(narrow ? "LibraryRailWrap" : "LibraryRailStack");
    }

    private void OnLibrarySectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingLibrarySections || LibrarySections.SelectedItem is not LibrarySection section ||
            section.Key == librarySectionKey && activeLibraryCollectionId == null) return;
        librarySectionKey = section.Key;
        activeLibraryCollectionId = null;
        RefreshLibraryRoutes();
        RefreshLibraryRouteContent();
    }

    private void OnLibrarySearchChanged(object sender, TextChangedEventArgs e)
    {
        if (changingLibrarySearch || LibraryList == null || LibraryContentScroll == null) return;
        if (string.IsNullOrWhiteSpace(LibrarySearch.Text))
        {
            if (librarySearchOrigin is { } origin)
            {
                librarySearchOrigin = null;
                RestoreLibraryBrowse(origin);
            }
            else FilterLibrary(preserveSelection: true);
            return;
        }
        librarySearchOrigin ??= CaptureLibraryBrowsePosition();
        CancelPendingLibraryViewport();
        libraryVisibleLimit = LibraryPageSize;
        LibraryContentScroll.ScrollToTop();
        FilterLibrary();
    }

    private void OnLibrarySortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (libraryViewPreferences is not { IsCompletedSuccessfully: true } || closing) return;
        FilterLibrary(preserveSelection: true);
        QueueLibraryPreference(LibrarySortPreference, CurrentLibrarySort);
    }

    private void OnLibrarySeeAll(object sender, RoutedEventArgs e)
    {
        LibrarySections.SelectedItem = LibrarySections.Items.OfType<LibrarySection>()
            .FirstOrDefault(section => section.Key == LibraryAllKey);
        if (LibrarySections.ItemContainerGenerator.ContainerFromItem(LibrarySections.SelectedItem) is ListBoxItem selected)
            selected.Focus();
    }

    private void OnLibraryMore(object sender, RoutedEventArgs e)
    {
        libraryVisibleLimit += LibraryPageSize;
        FilterLibrary(preserveSelection: true);
    }

    private void OnFocusLibrarySearch(object sender, RoutedEventArgs e) => FocusLibrarySearch();

    private void FocusLibrarySearch()
    {
        LibrarySearchPanel.Visibility = Visibility.Visible;
        LibrarySearch.Focus();
        LibrarySearch.SelectAll();
    }

    private void FocusLibraryAfterClosingReader()
    {
        if (LibrarySearchPanel.Visibility == Visibility.Visible) LibrarySearch.Focus();
        else LibrarySearchToggle.Focus();
    }

    private void OnCloseLibrarySearch(object sender, RoutedEventArgs e)
    {
        var origin = librarySearchOrigin;
        CloseLibrarySearchField();
        if (origin is { } saved) RestoreLibraryBrowse(saved);
        else FilterLibrary(preserveSelection: true);
        LibrarySearchToggle.Focus();
    }

    private async void OnLibrarySearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            OnCloseLibrarySearch(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && LibraryList.HasItems)
        {
            e.Handled = true;
            if (LibraryList.Items.Count == 1 && LibraryList.Items[0] is BookEntry result)
                await OpenLibraryBookAsync(result);
            else
            {
                LibraryList.SelectedIndex = 0;
                LibraryList.UpdateLayout();
                if (LibraryList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first) first.Focus();
            }
        }
    }

    private async void OnOpenLibraryBook(object sender, RoutedEventArgs e)
    {
        var selected = (sender as FrameworkElement)?.DataContext switch
        {
            BookEntry book => book,
            LibraryResumeCard card => card.Book,
            _ => null,
        };
        if (selected != null) await OpenLibraryBookAsync(selected);
    }
}
