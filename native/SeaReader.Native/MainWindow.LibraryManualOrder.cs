using System.Diagnostics;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const string CollectionShelfPrefix = "collection:";
    private IReadOnlyDictionary<string, IReadOnlyList<string>> libraryManualOrders = new Dictionary<string, IReadOnlyList<string>>();
    private int libraryManualOrderVersion;
    private Task libraryManualOrderWrite = Task.CompletedTask;

    private enum LibraryMove { Earlier, Later, First, Last, BeforeTarget, AfterTarget }

    private string LibraryOrderShelf => ActiveLibraryCollection is { } collection
        ? CollectionShelfPrefix + collection.Id.ToString(CultureInfo.InvariantCulture)
        : librarySectionKey == LibraryHomeKey ? LibraryAllKey : librarySectionKey;

    private string? ManualReorderShelf => !closing && LibrarySurface.IsVisible && CurrentLibrarySort == "manual" &&
        string.IsNullOrWhiteSpace(LibrarySearch.Text) && (librarySectionKey != LibraryHomeKey || ActiveLibraryCollection != null)
        ? LibraryOrderShelf : null;

    private IEnumerable<BookEntry> EntriesOnLibraryShelf(string shelf)
    {
        if (shelf.StartsWith(CollectionShelfPrefix, StringComparison.Ordinal) &&
            long.TryParse(shelf.AsSpan(CollectionShelfPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long collection))
            return books.Where(entry => IsInLibraryCollection(collection, entry.Id));
        return shelf switch
        {
            LibraryAllKey => books,
            LibraryWantKey => books.Where(entry => entry.ReadingState == LibraryReadingState.WantToRead),
            LibraryFinishedKey => books.Where(entry => entry.ReadingState == LibraryReadingState.Finished),
            _ => books.Where(entry => entry.Format.Equals(shelf, StringComparison.OrdinalIgnoreCase))
        };
    }

    private IEnumerable<BookEntry> OrderLibraryManually(IEnumerable<BookEntry> entries, string shelf)
    {
        var unranked = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        if (libraryManualOrders.TryGetValue(shelf, out var ranks))
            foreach (string id in ranks)
                if (unranked.Remove(id, out var ranked)) yield return ranked;
        foreach (var entry in unranked.Values.OrderBy(entry => entry.ImportedAt).ThenBy(entry => entry.Id, StringComparer.Ordinal))
            yield return entry;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadLibraryManualOrdersAsync()
    {
        try { return await store.GetLibraryManualOrdersAsync(); }
        catch (InvalidOperationException error)
        {
            Trace.TraceWarning("SeaReader manual book order: {0}", error.Message);
            return libraryManualOrders;
        }
    }

    private Task MoveLibraryBookAsync(string shelf, string bookId, string anchorId, bool after) =>
        QueueManualLibraryMove(shelf, bookId, after ? LibraryMove.AfterTarget : LibraryMove.BeforeTarget, anchorId);

    private Task QueueManualLibraryMove(string shelf, string bookId, LibraryMove move, string? anchorId = null)
    {
        if (ManualReorderShelf != shelf) return Task.CompletedTask;
        return libraryManualOrderWrite = CommitManualLibraryMoveAsync(libraryManualOrderWrite, shelf, bookId, move, anchorId);
    }

    private async Task CommitManualLibraryMoveAsync(Task prior, string shelf, string bookId, LibraryMove move, string? anchorId)
    {
        await prior;
        await libraryOrganizationGate.WaitAsync();
        try
        {
            var order = OrderLibraryManually(EntriesOnLibraryShelf(shelf), shelf).ToArray();
            int from = Array.FindIndex(order, entry => entry.Id == bookId);
            if (from < 0) return;
            int to = move switch
            {
                LibraryMove.Earlier => from - 1,
                LibraryMove.Later => from + 1,
                LibraryMove.First => 0,
                LibraryMove.Last => order.Length - 1,
                _ => Array.FindIndex(order, entry => entry.Id == anchorId)
            };
            if (to < 0 || to >= order.Length || to == from) return;
            bool after = move is LibraryMove.Later or LibraryMove.Last or LibraryMove.AfterTarget;
            if (!await store.MoveLibraryBookAsync(shelf, bookId, order[to].Id, after)) return;

            libraryManualOrderVersion++;
            libraryManualOrders = await store.GetLibraryManualOrdersAsync();
            if (closing || !LibrarySurface.IsVisible || LibraryOrderShelf != shelf || CurrentLibrarySort != "manual") return;

            int position = Array.FindIndex(OrderLibraryManually(EntriesOnLibraryShelf(shelf), shelf).ToArray(), entry => entry.Id == bookId);
            bool revealMovedBook = ManualReorderShelf == shelf && position >= 0;
            if (revealMovedBook && (move is LibraryMove.Earlier or LibraryMove.Later) && position == libraryVisibleLimit)
                libraryVisibleLimit += LibraryPageSize;
            if (revealMovedBook) CancelPendingLibraryViewport();
            FilterLibrary(preserveSelection: !revealMovedBook);
            if (!revealMovedBook || ManualReorderShelf != shelf) return;
            FocusReorderedLibraryBook(bookId, from);
            SetLibraryStatus($"《{order[from].Display.Title}》已移到第 {position + 1} 位。");
        }
        catch (Exception error) { if (!closing) SetLibraryStatus(error.Message); }
        finally { libraryOrganizationGate.Release(); }
    }

    private void FocusReorderedLibraryBook(string bookId, int previousIndex)
    {
        var shown = LibraryList.Items.OfType<BookEntry>().ToArray();
        if (shown.Length == 0) return;
        var next = shown.FirstOrDefault(entry => entry.Id == bookId) ?? shown[Math.Min(previousIndex, shown.Length - 1)];
        LibraryList.SelectedItem = next;
        LibraryList.UpdateLayout();
        if (LibraryList.ItemContainerGenerator.ContainerFromItem(next) is not ListBoxItem item) return;
        item.Focus();
        item.BringIntoView();
    }

    private void AddLibraryManualMoveActions(ContextMenu menu, BookEntry entry)
    {
        if (ManualReorderShelf is not { } shelf) return;
        var order = OrderLibraryManually(EntriesOnLibraryShelf(shelf), shelf).ToArray();
        int index = Array.FindIndex(order, book => book.Id == entry.Id);
        if (index < 0 || order.Length < 2) return;

        var move = new MenuItem { Header = "调整顺序" };
        AddMove("移到开头", LibraryMove.First, index > 0);
        AddMove("前移一位", LibraryMove.Earlier, index > 0, "Ctrl+↑");
        AddMove("后移一位", LibraryMove.Later, index < order.Length - 1, "Ctrl+↓");
        AddMove("移到末尾", LibraryMove.Last, index < order.Length - 1);
        menu.Items.Add(move);
        menu.Items.Add(new Separator());

        void AddMove(string label, LibraryMove direction, bool enabled, string gesture = "")
        {
            var action = new MenuItem { Header = label, IsEnabled = enabled, InputGestureText = gesture };
            action.Click += async (_, _) => await QueueManualLibraryMove(shelf, entry.Id, direction);
            move.Items.Add(action);
        }
    }
}
