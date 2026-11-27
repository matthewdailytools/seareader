using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private IChapterBook? contentsBook;
    private ContentsEntry[] contentsEntries = [];
    private ContentsEntry? currentContentsEntry;
    private (ContentsEntry Entry, ReaderLocation Location)? chosenContentsEntry;
    private CancellationTokenSource? contentsSession;
    private (int Chapter, Task<IReadOnlyDictionary<string, int>> Load)? contentsAnchors;
    private bool contentsRefreshQueued;
    private bool contentsResolvingTarget;

    private void PlaceContentsPanel()
    {
        const double inset = 12;
        var width = Math.Min(310, Math.Max(0, ReaderSurface.ActualWidth - inset * 2));
        var anchor = ContentsButton.TranslatePoint(new Point(ContentsButton.ActualWidth / 2, ContentsButton.ActualHeight), ReaderSurface);
        var top = anchor.Y + 7;
        var left = Math.Clamp(anchor.X - width / 2, inset, Math.Max(inset, ReaderSurface.ActualWidth - width - inset));
        ContentsPanel.Width = width;
        ContentsPanel.MaxHeight = Math.Clamp(ReaderSurface.ActualHeight - top - 58, 0, 440);
        ContentsPanel.Margin = new Thickness(left, top, 0, 0);
    }

    private void DisplayContents()
    {
        if (epub is not { } source || reading is not { } session) return;
        EndContentsSession();
        if (!ReferenceEquals(contentsBook, source))
        {
            contentsBook = source;
            contentsEntries = source is EpubDocument { Navigation.Count: > 0 } document
                ? document.Navigation.Select(entry => new ContentsEntry(entry.Title, entry.Chapter, entry.Fragment, entry.Depth)).ToArray()
                : source.Chapters.Select((entry, index) => new ContentsEntry(
                    string.IsNullOrWhiteSpace(entry.Title) ? $"第 {index + 1} 章" : entry.Title, index, null, 0)).ToArray();
            ContentsList.ItemsSource = contentsEntries;
        }
        contentsSession = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        SetContentsStatus("");
        UpdateCurrentContentsEntry(FindCurrentContentsEntry(CaptureContentsLocation(), null), initialize: true);
        ContentsList.Focus();
        QueueContentsRefresh();
    }

    private void EndContentsSession()
    {
        if (contentsSession is { } session)
        {
            contentsSession = null;
            session.Cancel();
            session.Dispose();
        }
        contentsAnchors = null;
        contentsResolvingTarget = false;
        contentsRefreshQueued = false;
    }

    private void ResetContents()
    {
        EndContentsSession();
        contentsBook = null;
        contentsEntries = [];
        currentContentsEntry = null;
        chosenContentsEntry = null;
        ContentsList.ItemsSource = null;
    }

    private void QueueContentsRefresh()
    {
        if (shownPanel != Panel.Contents || contentsSession is not { } session || contentsRefreshQueued) return;
        contentsRefreshQueued = true;
        Dispatcher.InvokeAsync(() => _ = RefreshContentsAsync(session), DispatcherPriority.Background);
    }

    private async Task RefreshContentsAsync(CancellationTokenSource session)
    {
        if (!ReferenceEquals(contentsSession, session)) return;
        var token = session.Token;
        var location = CaptureContentsLocation();
        try
        {
            IReadOnlyDictionary<string, int>? anchors = null;
            if (pdf == null && location.Chapter is { } index && contentsBook is EpubDocument document &&
                contentsEntries.Any(entry => entry.Chapter == index && entry.Fragment != null))
                anchors = await LookupContentsAnchorsAsync(document, index, token);
            token.ThrowIfCancellationRequested();
            var latest = CaptureContentsLocation();
            if (latest.Chapter == location.Chapter)
                UpdateCurrentContentsEntry(FindCurrentContentsEntry(latest, anchors));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            if (!token.IsCancellationRequested)
                UpdateCurrentContentsEntry(FindCurrentContentsEntry(CaptureContentsLocation(), null));
        }
        finally
        {
            if (ReferenceEquals(contentsSession, session))
            {
                contentsRefreshQueued = false;
                if (CaptureContentsLocation().Chapter != location.Chapter) QueueContentsRefresh();
            }
        }
    }

    private ReaderLocation CaptureContentsLocation() => pdf is FixedEpubPageBook ? new ReaderLocation(pdfPage, 0, 0) :
        ReadRetainedLayoutLocation() ?? CaptureReaderLocation();

    private Task<IReadOnlyDictionary<string, int>> LookupContentsAnchorsAsync(EpubDocument document, int index, CancellationToken token)
    {
        if (contentsAnchors is { } previous && previous.Chapter == index) return previous.Load;
        var load = document.ReadChapterAnchorsAsync(index, token);
        contentsAnchors = (index, load);
        return load;
    }

    private ContentsEntry? FindCurrentContentsEntry(ReaderLocation location, IReadOnlyDictionary<string, int>? anchors)
    {
        if (chosenContentsEntry is { } chosen && SameReaderLocation(chosen.Location, location)) return chosen.Entry;
        if (pdf is FixedEpubPageBook) return contentsEntries.FirstOrDefault(entry => entry.Chapter == location.Chapter);
        var sourceOffset = Math.Max(0, location.TextPosition is { SourceOffset: >= 0 } textPosition
            ? textPosition.SourceOffset : location.Offset);
        ContentsEntry? best = null;
        int bestOffset = -1;
        foreach (var entry in contentsEntries)
        {
            if (entry.Chapter != location.Chapter || !entry.CanNavigate) continue;
            var offset = 0;
            if (entry.Fragment is { } fragment && (anchors == null || !anchors.TryGetValue(fragment, out offset))) continue;
            if (offset <= sourceOffset && (offset > bestOffset ||
                offset == bestOffset && entry.Fragment != null && best?.Fragment == null))
            {
                best = entry;
                bestOffset = offset;
            }
        }
        return best;
    }

    private void UpdateCurrentContentsEntry(ContentsEntry? entry, bool initialize = false)
    {
        var previous = currentContentsEntry;
        if (!initialize && ReferenceEquals(previous, entry)) return;
        if (previous != null) previous.IsCurrent = false;
        if (entry != null) entry.IsCurrent = true;
        currentContentsEntry = entry;
        if (!initialize && ContentsList.SelectedItem != null && !ReferenceEquals(ContentsList.SelectedItem, previous)) return;
        ContentsList.SelectedItem = entry;
        if (entry != null) ContentsList.ScrollIntoView(entry);
    }

    private async void OnContentsClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject element ||
            ItemsControl.ContainerFromElement(ContentsList, element) is not ListBoxItem { DataContext: ContentsEntry entry }) return;
        e.Handled = true;
        await SelectContentsEntryAsync(entry);
    }

    private async void OnContentsKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ContentsList.SelectedItem is not ContentsEntry entry) return;
        e.Handled = true;
        await SelectContentsEntryAsync(entry);
    }

    private async Task SelectContentsEntryAsync(ContentsEntry entry)
    {
        if (contentsSession is not { } session || shownPanel != Panel.Contents || contentsResolvingTarget ||
            moving || turningPage || seekingPosition || epub is not { } source || entry.Chapter is not { } index) return;
        var token = session.Token;
        contentsResolvingTarget = true;
        SetContentsStatus("");
        try
        {
            if (pdf is FixedEpubPageBook fixedBook)
            {
                if (await NavigateFixedEpubTargetAsync(fixedBook, new FixedEpubTarget(index, entry.Fragment ?? "")) &&
                    ReferenceEquals(epub, source))
                    chosenContentsEntry = (entry, CaptureContentsLocation());
                return;
            }
            TextPageStart target = new(-1, 0, -1);
            if (entry.Fragment is { } fragment && source is EpubDocument document)
            {
                var anchors = await LookupContentsAnchorsAsync(document, index, token);
                token.ThrowIfCancellationRequested();
                if (!anchors.TryGetValue(fragment, out var offset))
                {
                    SetContentsStatus("找不到此目录项在正文中的位置。");
                    return;
                }
                target = new TextPageStart(offset, 0, offset);
            }
            var destination = new ReaderLocation(index, Math.Max(0, target.Offset), 0, target);
            if (!await NavigateReaderLocationAsync(destination) || !ReferenceEquals(epub, source)) return;
            chosenContentsEntry = (entry, CaptureContentsLocation());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            if (!token.IsCancellationRequested) SetContentsStatus("暂时无法读取此目录项的正文位置。");
        }
        finally
        {
            if (ReferenceEquals(contentsSession, session)) contentsResolvingTarget = false;
        }
    }

    private void SetContentsStatus(string message)
    {
        if (ContentsStatus.Text == message) return;
        ContentsStatus.Text = message;
        ContentsStatus.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (message.Length != 0 && ContentsStatus.IsVisible && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(ContentsStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private sealed class ContentsEntry(string title, int? chapter, string? fragment, int depth) : INotifyPropertyChanged
    {
        private bool isCurrent;
        public string Title { get; } = title;
        public int? Chapter { get; } = chapter;
        public string? Fragment { get; } = fragment;
        public Thickness Indent { get; } = new(Math.Clamp(depth, 0, 8) * 13, 0, 0, 0);
        public bool CanNavigate => Chapter.HasValue;
        public bool IsCurrent
        {
            get => isCurrent;
            set
            {
                if (isCurrent == value) return;
                isCurrent = value;
                PropertyChanged?.Invoke(this, new(nameof(IsCurrent)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
