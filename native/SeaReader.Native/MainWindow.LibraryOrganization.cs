using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SeaReader.Native;

public partial class MainWindow
{
    private readonly SemaphoreSlim libraryOrganizationGate = new(1);
    private LibraryOrganization? libraryOrganization;
    private long? activeLibraryCollectionId;

    private LibraryCollection? ActiveLibraryCollection => libraryOrganization?.Collections
        .FirstOrDefault(item => item.Id == activeLibraryCollectionId);

    private bool IsInLibraryCollection(long collection, string bookId) =>
        libraryOrganization?.Members.GetValueOrDefault(collection)?.Contains(bookId) == true;

    private void RefreshLibraryRoutes(LibrarySection[]? sections = null)
    {
        sections ??= LibrarySections.Items.OfType<LibrarySection>().ToArray();
        if (sections.Length == 0) return;
        var collections = libraryOrganization?.Collections ?? [];
        var selectedCollection = ActiveLibraryCollection;
        if (selectedCollection == null) activeLibraryCollectionId = null;
        var selectedSection = sections.FirstOrDefault(section => section.Key == librarySectionKey) ?? sections[0];
        librarySectionKey = selectedSection.Key;

        refreshingLibrarySections = true;
        try
        {
            if (!LibrarySections.Items.OfType<LibrarySection>().SequenceEqual(sections))
                LibrarySections.ItemsSource = sections;
            if (!LibraryCollections.Items.OfType<LibraryCollection>().SequenceEqual(collections))
                LibraryCollections.ItemsSource = collections;
            if (!LibraryCollectionRail.Items.OfType<LibraryCollection>().SequenceEqual(collections))
                LibraryCollectionRail.ItemsSource = collections;
            LibrarySections.SelectedItem = selectedCollection == null ? selectedSection : null;
            LibraryCollections.SelectedItem = selectedCollection;
            LibraryCollectionRail.SelectedItem = selectedCollection;
        }
        finally { refreshingLibrarySections = false; }
    }

    private void OnLibraryCollectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingLibrarySections || sender is not Selector { SelectedItem: LibraryCollection selected } ||
            selected.Id == activeLibraryCollectionId) return;
        activeLibraryCollectionId = selected.Id;
        RefreshLibraryRoutes();
        RefreshLibraryRouteContent();
    }

    private async void OnCreateLibraryCollection(object sender, RoutedEventArgs e) => await CreateLibraryCollectionAsync();

    private async Task CreateLibraryCollectionAsync(string? bookId = null)
    {
        var name = PromptLibraryCollectionName("新建收藏", "");
        if (name == null) return;
        await ChangeLibraryOrganizationAsync(async () =>
        {
            var id = await store.CreateCollectionAsync(name);
            if (bookId != null) await store.SetCollectionMembershipAsync(id, bookId, true);
            activeLibraryCollectionId = id;
        });
    }

    private async void OnRenameLibraryCollection(object sender, RoutedEventArgs e)
    {
        if (ActiveLibraryCollection is not { } collection) return;
        var name = PromptLibraryCollectionName("重命名收藏", collection.Name);
        if (name == null || name == collection.Name) return;
        await ChangeLibraryOrganizationAsync(() => store.RenameCollectionAsync(collection.Id, name));
    }

    private async void OnRemoveLibraryCollection(object sender, RoutedEventArgs e)
    {
        if (ActiveLibraryCollection is not { } collection) return;
        if (MessageBox.Show(this, $"删除「{collection.Name}」收藏？收藏里的书会保留在书库。",
            "删除收藏", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await ChangeLibraryOrganizationAsync(async () =>
        {
            await store.DeleteCollectionAsync(collection.Id);
            activeLibraryCollectionId = null;
            librarySectionKey = LibraryAllKey;
        });
    }

    private void OnShowBookOrganizer(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BookEntry entry } target)
        {
            LibraryList.SelectedItem = entry;
            ShowBookOrganizer(target, entry);
        }
    }

    private void OnContinueBookOrganizer(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryResumeCard card } target) return;
        e.Handled = true;
        ShowBookOrganizer(target, card.Book, inContinue: true);
    }

    private void OnContinueBookKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)
            OnContinueBookOrganizer(sender, e);
    }

    private async void OnLibraryBookKeyDown(object sender, KeyEventArgs e)
    {
        var container = FindLibraryBookContainer(e.OriginalSource);
        if ((container?.DataContext ?? LibraryList.SelectedItem) is not BookEntry entry) return;
        if (ManualReorderShelf is { } shelf && Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            await QueueManualLibraryMove(shelf, entry.Id, e.Key == Key.Up ? LibraryMove.Earlier : LibraryMove.Later);
            return;
        }
        if (e.Key != Key.Apps && (e.Key != Key.F10 || Keyboard.Modifiers != ModifierKeys.Shift)) return;
        e.Handled = true;
        ShowBookOrganizer((FrameworkElement?)container ?? LibraryList, entry);
    }

    private void OnLibraryBookMenuPointer(object sender, MouseButtonEventArgs e)
    {
        if (FindLibraryBookContainer(e.OriginalSource) is not { DataContext: BookEntry entry } container) return;
        e.Handled = true;
        LibraryList.SelectedItem = entry;
        ShowBookOrganizer(container, entry);
    }

    private ListBoxItem? FindLibraryBookContainer(object? source) => source is DependencyObject element
        ? ItemsControl.ContainerFromElement(LibraryList, element) as ListBoxItem : null;

    private void ShowBookOrganizer(FrameworkElement target, BookEntry entry, bool inContinue = false)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            MinWidth = 172,
            Background = (Brush)LibrarySurface.FindResource("LibraryPaper"),
            Foreground = (Brush)LibrarySurface.FindResource("LibraryInk")
        };
        if (inContinue || entry.ContinueHidden && entry.OpenedAt.HasValue && entry.ReadingState != LibraryReadingState.Finished)
        {
            var item = new MenuItem { Header = inContinue ? "移出继续阅读" : "恢复到继续阅读" };
            item.Click += async (_, _) =>
            {
                await ChangeLibraryOrganizationAsync(() => store.SetContinueHiddenAsync(entry.Id, inContinue));
                if (inContinue && !target.IsVisible && !closing && LibrarySurface.IsVisible)
                    LibrarySearchToggle.Focus();
            };
            menu.Items.Add(item);
            menu.Items.Add(new Separator());
        }
        AddLibraryManualMoveActions(menu, entry);
        AddReadingState("想读", LibraryReadingState.WantToRead);
        AddReadingState("已读完", LibraryReadingState.Finished);

        var clear = new MenuItem { Header = "清除阅读状态", IsEnabled = entry.ReadingState != LibraryReadingState.None };
        clear.Click += async (_, _) => await ChangeLibraryOrganizationAsync(() => store.SetReadingStateAsync(entry.Id, LibraryReadingState.None));
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());

        var collections = new MenuItem { Header = "加入或移出收藏" };
        foreach (var collection in libraryOrganization?.Collections ?? [])
        {
            var included = IsInLibraryCollection(collection.Id, entry.Id);
            var item = new MenuItem { Header = collection.Name, IsCheckable = true, IsChecked = included };
            item.Click += async (_, _) => await ChangeLibraryOrganizationAsync(
                () => store.SetCollectionMembershipAsync(collection.Id, entry.Id, !included));
            collections.Items.Add(item);
        }
        if (collections.HasItems) collections.Items.Add(new Separator());
        var create = new MenuItem { Header = "新建收藏并加入…" };
        create.Click += async (_, _) => await CreateLibraryCollectionAsync(entry.Id);
        collections.Items.Add(create);
        menu.Items.Add(collections);
        target.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(target.ContextMenu, menu)) target.ContextMenu = null;
        };
        menu.IsOpen = true;

        void AddReadingState(string label, LibraryReadingState state)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = entry.ReadingState == state };
            item.Click += async (_, _) => await ChangeLibraryOrganizationAsync(() => store.SetReadingStateAsync(entry.Id, state));
            menu.Items.Add(item);
        }
    }

    private async Task ChangeLibraryOrganizationAsync(Func<Task> change, string successMessage = "")
    {
        await libraryOrganizationGate.WaitAsync();
        try
        {
            await change();
            await ReloadLibraryAsync();
            SetLibraryStatus(successMessage);
        }
        catch (Exception error) { SetLibraryStatus(error.Message); }
        finally { libraryOrganizationGate.Release(); }
    }

    private string? PromptLibraryCollectionName(string title, string current)
    {
        Brush Color(string key) => (Brush)LibrarySurface.FindResource(key);
        var paper = Color("LibraryPaper");
        var ink = Color("LibraryInk");
        var border = Color("LibraryRule");
        var input = new TextBox
        {
            Name = "LibraryCollectionNameInput", Text = current, MaxLength = LibraryStore.MaxCollectionNameLength,
            Width = 286,
            Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 6, 0, 14),
            Foreground = ink, Background = paper, BorderBrush = border, CaretBrush = ink
        };
        AutomationProperties.SetName(input, "收藏名称");
        var label = new Label { Content = "收藏名称", Target = input, Padding = new Thickness(0), Foreground = ink };
        var cancel = new Button
        {
            Content = "取消", IsCancel = true, Style = (Style)LibrarySurface.FindResource("LibraryButton"),
            Foreground = ink, Background = paper, BorderBrush = border
        };
        var confirm = new Button
        {
            Name = "LibraryCollectionConfirm", Content = current.Length == 0 ? "创建" : "保存",
            IsDefault = true, IsEnabled = current.Trim().Length > 0,
            Style = (Style)LibrarySurface.FindResource("LibraryPrimaryButton"),
            Foreground = Color("LibraryOnAccent"), Background = Color("LibraryAccent"), BorderBrush = Color("LibraryAccent")
        };
        input.TextChanged += (_, _) => confirm.IsEnabled = input.Text.Trim().Length > 0;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(label);
        body.Children.Add(input);
        body.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Owner = this, Content = body, FontFamily = FontFamily, FontSize = FontSize,
            Background = paper, Foreground = ink,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        NameScope.SetNameScope(dialog, new NameScope());
        dialog.RegisterName(input.Name, input);
        dialog.RegisterName(confirm.Name, confirm);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        confirm.Click += (_, _) => { if (confirm.IsEnabled) dialog.DialogResult = true; };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
