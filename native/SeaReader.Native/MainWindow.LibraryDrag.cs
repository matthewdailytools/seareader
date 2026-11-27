using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const string LibraryBookDragFormat = "SeaReader.Native.LibraryBookDrag";
    private const double LibraryDragScrollBand = 34;

    private sealed record LibraryBookPress(string? Shelf, BookEntry Book, Point Point);
    private sealed record LibraryBookDrag(string Token, string? Shelf, string BookId);
    private sealed record LibraryDropTarget(ListBoxItem Container, string BookId, bool After, bool Rows);

    private LibraryBookPress? libraryBookPress;
    private LibraryBookDrag? libraryBookDrag;
    private ScrollContentPresenter? libraryDragViewport;
    private Point? libraryDragPoint;
    private DispatcherTimer? libraryDragScrollTimer;
    private ListBoxItem? libraryDropMarked;
    private ListBoxItem? libraryCollectionDropMarked;

    private bool CanDragLibraryBooks => !closing && LibrarySurface.IsVisible && !LibrarySearchPanel.IsVisible;
    private bool HasCollectionDropDestinations => LibraryCollectionRail.IsVisible && LibraryCollectionRail.HasItems;

    private void OnLibraryDragPointerDown(object sender, MouseButtonEventArgs e)
    {
        libraryBookPress = null;
        var shelf = ManualReorderShelf;
        if (e.ClickCount != 1 || libraryBookDrag != null || !CanDragLibraryBooks ||
            shelf == null && !HasCollectionDropDestinations ||
            FindLibraryBookContainer(e.OriginalSource) is not { DataContext: BookEntry book } item ||
            IsLibraryOrganizerPress(e.OriginalSource as DependencyObject, item)) return;

        libraryBookPress = new(shelf, book, e.GetPosition(LibraryList));
    }

    private bool IsLibraryOrganizerPress(DependencyObject? source, ListBoxItem item)
    {
        var organizerStyle = LibrarySurface.FindResource("LibraryBookOrganizerButton");
        while (source != null && !ReferenceEquals(source, item))
        {
            if (source is Button button) return ReferenceEquals(button.Style, organizerStyle);
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnLibraryDragPointerUp(object sender, MouseButtonEventArgs e) => libraryBookPress = null;

    private void OnLibraryDragPointerMove(object sender, MouseEventArgs e)
    {
        if (libraryBookPress is not { } press) return;
        if (e.LeftButton != MouseButtonState.Pressed || !CanDragLibraryBooks || ManualReorderShelf != press.Shelf ||
            !LibraryList.Items.Contains(press.Book))
        {
            libraryBookPress = null;
            return;
        }

        var movement = e.GetPosition(LibraryList) - press.Point;
        if (Math.Abs(movement.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(movement.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        libraryBookPress = null;
        e.Handled = true;
        if (FindLibraryBookContainer(Mouse.Captured) != null) Mouse.Capture(null);
        LibraryList.SelectedItem = press.Book;
        StartLibraryBookDrag(press);
    }

    private void StartLibraryBookDrag(LibraryBookPress press)
    {
        var effects = press.Shelf == null ? DragDropEffects.None : DragDropEffects.Move;
        if (HasCollectionDropDestinations) effects |= DragDropEffects.Copy;
        if (effects == DragDropEffects.None) return;

        var drag = new LibraryBookDrag(Guid.NewGuid().ToString("N"), press.Shelf, press.Book.Id);
        var data = new DataObject();
        data.SetData(LibraryBookDragFormat, drag.Token, autoConvert: false);

        libraryBookDrag = drag;
        libraryDragViewport = Descendant<ScrollContentPresenter>(LibraryContentScroll);
        libraryDragScrollTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        libraryDragScrollTimer.Tick += OnLibraryDragScrollTick;
        LibraryContentScroll.ScrollChanged += OnLibraryDragScrollChanged;
        try { DragDrop.DoDragDrop(LibraryList, data, effects); }
        catch (Exception error) when (error is ExternalException or InvalidOperationException)
        {
            SetLibraryStatus(error.Message);
        }
        finally
        {
            ResetLibraryDragFeedback();
            LibraryContentScroll.ScrollChanged -= OnLibraryDragScrollChanged;
            libraryDragScrollTimer.Tick -= OnLibraryDragScrollTick;
            libraryDragScrollTimer = null;
            libraryDragViewport = null;
            libraryBookDrag = null;
        }
    }

    private LibraryBookDrag? ReadLibraryBookDrag(DragEventArgs e)
    {
        if (libraryBookDrag is not { } drag || e.Data.GetDataPresent(DataFormats.FileDrop) ||
            !e.Data.GetDataPresent(LibraryBookDragFormat, autoConvert: false)) return null;
        return e.Data.GetData(LibraryBookDragFormat, autoConvert: false) is string token && token == drag.Token ? drag : null;
    }

    private bool TryHandleLibraryBookDragOver(DragEventArgs e)
    {
        if (ReadLibraryBookDrag(e) is not { } drag) return false;

        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (FindLibraryCollectionDropTarget(e, drag) is { } collection)
        {
            ResetLibraryReorderFeedback();
            SetLibraryCollectionDropMark(collection);
            e.Effects = DragDropEffects.Copy;
            return true;
        }
        SetLibraryCollectionDropMark(null);
        if (!CanTargetLibraryReorder(e, drag) || libraryDragViewport is not { } viewport)
        {
            ResetLibraryReorderFeedback();
            return true;
        }

        var point = e.GetPosition(viewport);
        if (!new Rect(viewport.RenderSize).Contains(point))
        {
            ResetLibraryReorderFeedback();
            return true;
        }

        libraryDragPoint = point;
        var target = FindLibraryDropTarget(viewport.TranslatePoint(point, LibraryList), drag.BookId);
        SetLibraryDropMark(target);
        if (target != null) e.Effects = DragDropEffects.Move;
        if (LibraryDragScrollDelta(point) != 0) libraryDragScrollTimer?.Start();
        else libraryDragScrollTimer?.Stop();
        return true;
    }

    private async Task<bool> TryHandleLibraryBookDropAsync(DragEventArgs e)
    {
        if (ReadLibraryBookDrag(e) is not { } drag) return false;

        e.Handled = true;
        e.Effects = DragDropEffects.None;
        ResetLibraryDragFeedback();
        if (FindLibraryCollectionDropTarget(e, drag)?.DataContext is LibraryCollection collection)
        {
            e.Effects = DragDropEffects.Copy;
            await ChangeLibraryOrganizationAsync(() => store.SetCollectionMembershipAsync(collection.Id, drag.BookId, true),
                successMessage: $"已加入「{collection.Name}」。");
            return true;
        }
        if (!CanTargetLibraryReorder(e, drag) || drag.Shelf is not { } shelf || libraryDragViewport is not { } viewport ||
            !new Rect(viewport.RenderSize).Contains(e.GetPosition(viewport)) ||
            FindLibraryDropTarget(e.GetPosition(LibraryList), drag.BookId) is not { } target) return true;

        e.Effects = DragDropEffects.Move;
        await MoveLibraryBookAsync(shelf, drag.BookId, target.BookId, target.After);
        return true;
    }

    private bool CanTargetLibraryReorder(DragEventArgs e, LibraryBookDrag drag) =>
        CanDragLibraryBooks && (e.AllowedEffects & DragDropEffects.Move) != 0 && drag.Shelf != null &&
        ManualReorderShelf == drag.Shelf;

    private ListBoxItem? FindLibraryCollectionDropTarget(DragEventArgs e, LibraryBookDrag drag)
    {
        if (!CanDragLibraryBooks || !HasCollectionDropDestinations || (e.AllowedEffects & DragDropEffects.Copy) == 0 ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(LibraryCollectionRail, source) is not ListBoxItem { DataContext: LibraryCollection collection } item ||
            IsInLibraryCollection(collection.Id, drag.BookId))
            return null;
        return item;
    }

    private LibraryDropTarget? FindLibraryDropTarget(Point point, string draggedBookId)
    {
        if (!new Rect(LibraryList.RenderSize).Contains(point)) return null;
        var item = FindLibraryBookContainer(LibraryList.InputHitTest(point)) ?? NearestLibraryDropContainer(point);
        if (item?.DataContext is not BookEntry book || book.Id == draggedBookId) return null;

        var local = LibraryList.TranslatePoint(point, item);
        bool rows = Equals(LibraryList.Tag, LibraryRowsMode);
        bool after = rows ? local.Y >= item.ActualHeight / 2 : local.X >= item.ActualWidth / 2;
        return new(item, book.Id, after, rows);
    }

    private ListBoxItem? NearestLibraryDropContainer(Point point)
    {
        ListBoxItem? nearest = null;
        double nearestX = double.PositiveInfinity, nearestY = double.PositiveInfinity;
        for (int i = 0; i < LibraryList.Items.Count; i++)
        {
            if (LibraryList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem { ActualWidth: > 0, ActualHeight: > 0 } item)
                continue;

            var top = item.TranslatePoint(new Point(), LibraryList);
            var bounds = new Rect(top, item.RenderSize);
            double y = Math.Max(bounds.Top - point.Y, Math.Max(0, point.Y - bounds.Bottom));
            double x = Math.Max(bounds.Left - point.X, Math.Max(0, point.X - bounds.Right));
            if (y > nearestY || y == nearestY && x >= nearestX) continue;
            nearest = item;
            nearestX = x;
            nearestY = y;
        }
        return nearest;
    }

    private void OnLibraryBookDragLeave(object sender, DragEventArgs e)
    {
        if (ReadLibraryBookDrag(e) != null) ResetLibraryDragFeedback();
    }

    private double LibraryDragScrollDelta(Point point)
    {
        if (libraryDragViewport is not { } viewport) return 0;
        double top = Math.Max(0, LibraryDragScrollBand - point.Y);
        double bottom = Math.Max(0, LibraryDragScrollBand - (viewport.ActualHeight - point.Y));
        if (top > bottom && LibraryContentScroll.VerticalOffset > 0) return -(.5 * top + 3);
        if (bottom > top && LibraryContentScroll.VerticalOffset < LibraryContentScroll.ScrollableHeight) return .5 * bottom + 3;
        return 0;
    }

    private void OnLibraryDragScrollTick(object? sender, EventArgs e)
    {
        if (!CanDragLibraryBooks || libraryBookDrag is not { } drag || ManualReorderShelf != drag.Shelf ||
            libraryDragPoint is not { } point)
        {
            ResetLibraryDragFeedback();
            return;
        }

        double delta = LibraryDragScrollDelta(point);
        if (delta == 0) libraryDragScrollTimer?.Stop();
        else LibraryContentScroll.ScrollToVerticalOffset(
            Math.Clamp(LibraryContentScroll.VerticalOffset + delta, 0, LibraryContentScroll.ScrollableHeight));
    }

    private void OnLibraryDragScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!CanDragLibraryBooks || libraryBookDrag is not { } drag || libraryDragViewport is not { } viewport ||
            libraryDragPoint is not { } point || ManualReorderShelf != drag.Shelf) return;
        SetLibraryDropMark(FindLibraryDropTarget(viewport.TranslatePoint(point, LibraryList), drag.BookId));
    }

    private void ResetLibraryDragFeedback()
    {
        ResetLibraryReorderFeedback();
        SetLibraryCollectionDropMark(null);
    }

    private void SetLibraryCollectionDropMark(ListBoxItem? item)
    {
        if (ReferenceEquals(libraryCollectionDropMarked, item)) return;
        libraryCollectionDropMarked?.ClearValue(TagProperty);
        libraryCollectionDropMarked = item;
        if (item != null) item.Tag = "collection-drop";
    }

    private void ResetLibraryReorderFeedback()
    {
        libraryDragScrollTimer?.Stop();
        libraryDragPoint = null;
        SetLibraryDropMark(null);
    }

    private void SetLibraryDropMark(LibraryDropTarget? target)
    {
        if (!ReferenceEquals(libraryDropMarked, target?.Container))
        {
            libraryDropMarked?.ClearValue(TagProperty);
            libraryDropMarked = target?.Container;
        }
        if (target != null)
        {
            var mark = (target.Rows, target.After) switch
            {
                (true, true) => "row-after",
                (true, false) => "row-before",
                (false, true) => "column-after",
                _ => "column-before"
            };
            if (!Equals(target.Container.Tag, mark)) target.Container.Tag = mark;
        }
    }
}
