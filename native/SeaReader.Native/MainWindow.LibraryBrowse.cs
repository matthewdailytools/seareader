using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private readonly record struct LibraryBrowseRoute(string Section, long? Collection);

    private readonly record struct LibraryBrowsePosition(int Limit, double Offset, string? FirstBook = null,
        int FirstIndex = -1, double FirstBookTop = 0, string? SelectedBook = null);

    private readonly Dictionary<LibraryBrowseRoute, LibraryBrowsePosition> libraryBrowsePositions = [];
    private LibraryBrowseRoute? displayedLibraryRoute;
    private LibraryBrowsePosition? librarySearchOrigin;
    private bool changingLibrarySearch;
    private int libraryViewportVersion;
    private LibraryBrowsePosition? pendingLibraryViewport;

    private LibraryBrowseRoute CurrentLibraryBrowseRoute => activeLibraryCollectionId is long collection
        ? new LibraryBrowseRoute("", collection) : new LibraryBrowseRoute(librarySectionKey, null);

    private void RefreshLibraryRouteContent(bool preserveSelection = false)
    {
        var route = CurrentLibraryBrowseRoute;
        if (displayedLibraryRoute == route)
        {
            FilterLibrary(preserveSelection);
            return;
        }

        if (displayedLibraryRoute is { } old)
            libraryBrowsePositions[old] = librarySearchOrigin ?? CaptureLibraryBrowsePosition();
        displayedLibraryRoute = route;
        CloseLibrarySearchField();
        RestoreLibraryBrowse(libraryBrowsePositions.GetValueOrDefault(route, new LibraryBrowsePosition(LibraryPageSize, 0)));
    }

    private void PruneLibraryBrowsePositions(IReadOnlyList<LibrarySection> sections)
    {
        foreach (var route in libraryBrowsePositions.Keys.ToArray())
            if (route.Collection is long collection
                ? libraryOrganization?.Collections.Any(item => item.Id == collection) != true
                : !sections.Any(section => section.Key == route.Section)) libraryBrowsePositions.Remove(route);
    }

    private void CloseLibrarySearchField()
    {
        changingLibrarySearch = true;
        try
        {
            LibrarySearch.Clear();
            LibrarySearchPanel.Visibility = Visibility.Collapsed;
        }
        finally { changingLibrarySearch = false; }
        librarySearchOrigin = null;
    }

    private void RestoreLibraryBrowse(LibraryBrowsePosition position)
    {
        libraryVisibleLimit = position.Limit;
        FilterLibrary(restore: position);
    }

    private void IncludeSavedLibraryBooks(BookEntry[] results, LibraryBrowsePosition position)
    {
        int first = position.FirstBook == null ? -1 : Array.FindIndex(results, book => book.Id == position.FirstBook);
        int selected = position.SelectedBook == null ? -1 : Array.FindIndex(results, book => book.Id == position.SelectedBook);
        int last = Math.Max(first, selected);
        if (last >= libraryVisibleLimit) libraryVisibleLimit = (last / LibraryPageSize + 1) * LibraryPageSize;
    }

    private LibraryBrowsePosition CaptureLibraryBrowsePosition()
    {
        if (pendingLibraryViewport is { } pending) return pending with
        {
            Limit = libraryVisibleLimit, SelectedBook = (LibraryList.SelectedItem as BookEntry)?.Id
        };
        var selected = (LibraryList.SelectedItem as BookEntry)?.Id;
        var offset = LibraryContentScroll.VerticalOffset;
        var position = new LibraryBrowsePosition(libraryVisibleLimit, offset, SelectedBook: selected);
        if (offset < .5 || Descendant<ScrollContentPresenter>(LibraryContentScroll) is not { ActualHeight: > 0 } viewport)
            return position;

        for (int index = 0; index < LibraryList.Items.Count; index++)
        {
            if (LibraryList.Items[index] is not BookEntry book ||
                LibraryList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem { IsArrangeValid: true } item) continue;
            double top = item.TranslatePoint(new Point(), viewport).Y;
            if (top < viewport.ActualHeight && top + item.ActualHeight > 0)
                return position with { FirstBook = book.Id, FirstIndex = index, FirstBookTop = top };
        }
        return position;
    }

    private void QueueLibraryViewportRestore(LibraryBrowsePosition position)
    {
        pendingLibraryViewport = position;
        var version = ++libraryViewportVersion;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (version != libraryViewportVersion || closing) return;
            if (!LibrarySurface.IsVisible)
            {
                pendingLibraryViewport = null;
                return;
            }

            LibraryContentScroll.UpdateLayout();
            if (version != libraryViewportVersion || closing) return;
            double target = position.Offset;
            if (position.FirstBook is { } id && LibraryList.HasItems &&
                Descendant<ScrollContentPresenter>(LibraryContentScroll) is { ActualHeight: > 0 } viewport)
            {
                var anchor = LibraryList.Items.OfType<BookEntry>().FirstOrDefault(book => book.Id == id) ??
                    LibraryList.Items[Math.Clamp(position.FirstIndex, 0, LibraryList.Items.Count - 1)];
                if (LibraryList.ItemContainerGenerator.ContainerFromItem(anchor) is ListBoxItem { ActualHeight: > 0 } item)
                {
                    // Keep some of the anchor card visible if a grid card has become a shorter list row.
                    double visible = Math.Min(18, item.ActualHeight / 2);
                    double top = Math.Clamp(position.FirstBookTop, -item.ActualHeight + visible, viewport.ActualHeight - visible);
                    target = LibraryContentScroll.VerticalOffset + item.TranslatePoint(new Point(), viewport).Y - top;
                }
            }
            LibraryContentScroll.ScrollToVerticalOffset(Math.Clamp(target, 0, LibraryContentScroll.ScrollableHeight));
            LibraryContentScroll.UpdateLayout();
            if (version == libraryViewportVersion) pendingLibraryViewport = null;
        }, DispatcherPriority.Loaded);
    }

    private void CancelPendingLibraryViewport()
    {
        pendingLibraryViewport = null;
        libraryViewportVersion++;
    }
}
