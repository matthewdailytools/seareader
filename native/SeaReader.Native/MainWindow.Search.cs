using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const int ReaderSearchLimit = 100;
    private const int ReaderSearchQueryLimit = 1024;

    private sealed record SearchResult(int? Chapter, int Offset, string Excerpt, PdfTextHit? PdfHit = null,
        int Length = 0, int? TargetPage = null, string? Location = null)
    {
        public string SpokenLabel => Location == null ? Excerpt : $"{Location}，{Excerpt}";
    }

    private CancellationTokenSource? finding;
    private string? lastSearchQuery;
    private ObservableCollection<SearchResult>? readerSearchItems;
    private int? readerSearchPage;
    private bool readerSearchFinished;

    private void OnReaderHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (shownPanel is Panel.Search or Panel.PdfPages or Panel.Contents or Panel.Footnote) UpdatePanelPlacement();
    }

    private void UpdateSearchPanelPlacement()
    {
        const double inset = 12;
        double width = Math.Min(368, Math.Max(0, ReaderSurface.ActualWidth - inset * 2));
        var anchor = SearchButton.TranslatePoint(new Point(SearchButton.ActualWidth / 2, SearchButton.ActualHeight), ReaderSurface);
        double top = anchor.Y + 7;
        double left = Math.Clamp(anchor.X - width / 2, inset, Math.Max(inset, ReaderSurface.ActualWidth - width - inset));
        SearchPanel.Width = width;
        SearchPanel.MaxHeight = Math.Max(0, ReaderSurface.ActualHeight - top - inset);
        SearchPanel.Margin = new Thickness(left, top, 0, 0);
    }

    private void SetSearchMessage(string message)
    {
        if (SearchStatus.Text == message) return;
        SearchStatus.Text = message;
        SearchStatus.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (message.Length != 0 && SearchPanel.IsVisible && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(SearchStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void CancelReaderSearch()
    {
        if (finding is not { } pending) return;
        finding = null;
        pending.Cancel();
        SetSearchMessage(readerSearchItems is { Count: > 0 } ? "查找已停止，保留已显示的结果。" : "");
    }

    private void ClearSearchResults()
    {
        CancelReaderSearch();
        lastSearchQuery = null;
        readerSearchItems = null;
        readerSearchPage = null;
        readerSearchFinished = false;
        SetSearchMessage("");
        SearchResults.ItemsSource = null;
        SearchResults.Visibility = Visibility.Collapsed;
        ClearTextSearchMatch();
        ClearPdfSearchMatch();
    }

    private void ClearPdfSearchMatch()
    {
        if (pdfMatch == null) return;
        pdfMatch = null;
        ActivePdfSelection.ShowAnnotation([]);
    }

    private void ResetReaderSearch()
    {
        ReaderSearch.Clear();
        ClearSearchResults();
    }

    private void OnReaderSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchResults != null) ClearSearchResults();
    }

    private async void SearchSelectedText(string selection)
    {
        if (book == null) return;
        var firstLine = selection.AsSpan().Trim();
        var end = firstLine.IndexOfAny('\r', '\n');
        if (end >= 0) firstLine = firstLine[..end].TrimEnd();
        var length = Math.Min(firstLine.Length, ReaderSearchQueryLimit);
        if (length == 0) return;
        if (char.IsHighSurrogate(firstLine[length - 1])) length--;
        ReaderSearch.Text = firstLine[..length].ToString();
        OpenPanel(Panel.Search);
        await FindAsync();
    }

    private async Task FindAsync()
    {
        string query = ReaderSearch.Text.Trim();
        var opened = book;
        var document = text;
        var source = epub;
        var pdfSource = pdf;
        int documentChapter = chapter;
        if (query.Length == 0 || opened == null || document == null && pdfSource == null ||
            finding != null && lastSearchQuery == query) return;

        ClearSearchResults();
        using var work = new CancellationTokenSource();
        finding = work;
        var token = work.Token;
        var items = new ObservableCollection<SearchResult>();
        readerSearchItems = items;
        lastSearchQuery = query;
        readerSearchPage = ParseSearchPage(query);
        SearchResults.ItemsSource = items;
        SetSearchMessage("正在查找…");
        UpdateSearchPageResult();
        var matches = 0;
        try
        {
            if (pdfSource != null)
            {
                var recognized = await store.ListRecognizedPagesAsync(opened.Id);
                token.ThrowIfCancellationRequested();
                await pdfSource.SearchAsync(query, recognized, ReaderSearchLimit, token, hits => Dispatcher.InvokeAsync(() =>
                {
                    AppendMatches(hits.Select(hit => new SearchResult(null, hit.Offset, hit.Excerpt, hit,
                        Location: $"第 {hit.Page + 1} 页{(hit.FromOcr ? "（OCR）" : "")}")));
                }, DispatcherPriority.Background, token).Task);
            }
            else if (source == null)
            {
                var hits = await document!.SearchAsync(query, ReaderSearchLimit, token);
                AppendMatches(hits.Select(hit => new SearchResult(null, hit.Offset, hit.Excerpt, Length: query.Length)));
            }
            else
            {
                for (int index = 0; index < source.Chapters.Count && matches < ReaderSearchLimit; index++)
                {
                    var candidate = document!;
                    if (index != documentChapter)
                    {
                        var chapterText = await source.ReadChapterAsync(index, token);
                        candidate = await Task.Run(() => TextDocument.FromText(chapterText, [], token), token);
                    }
                    var hits = await candidate.SearchAsync(query, ReaderSearchLimit - matches, token);
                    string title = source.Chapters[index].Title;
                    string location = string.IsNullOrWhiteSpace(title) ? $"第 {index + 1} 章" : title;
                    AppendMatches(hits.Select(hit => new SearchResult(index, hit.Offset, hit.Excerpt,
                        Length: query.Length, Location: location)));
                }
            }

            token.ThrowIfCancellationRequested();
            if (!IsCurrentSearch()) return;
            UpdateSearchPageResult();
            readerSearchFinished = true;
            ShowSearchSummary(items);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && IsCurrentSearch())
                SetSearchMessage(matches == 0 ? error.Message : $"查找中断，保留已找到的 {matches} 个文字匹配。");
        }
        finally { if (ReferenceEquals(finding, work)) finding = null; }

        bool IsCurrentSearch() => ReferenceEquals(readerSearchItems, items) && ReferenceEquals(book, opened) && !closing;

        void AppendMatches(IEnumerable<SearchResult> batch)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrentSearch()) return;
            var previous = matches;
            foreach (var hit in batch)
            {
                items.Add(hit);
                matches++;
            }
            if (matches == previous) return;
            RefreshSearchResultVisibility(items);
            if (previous == 0) SetSearchMessage("已找到匹配，正在继续查找…");
        }
    }

    private void UpdateSearchPageResult()
    {
        if (readerSearchItems is not { } items || readerSearchPage is not int target) return;
        var visible = items.Count > 0 && items[0].TargetPage.HasValue;
        var available = target < NavigationPageCount;
        if (available != visible)
        {
            if (available) items.Insert(0, new SearchResult(null, 0, $"前往第 {target + 1} 页", TargetPage: target));
            else items.RemoveAt(0);
            RefreshSearchResultVisibility(items);
        }
        if (readerSearchFinished) ShowSearchSummary(items);
    }

    private void ShowSearchSummary(IReadOnlyList<SearchResult> results)
    {
        var pageAvailable = results.Count > 0 && results[0].TargetPage.HasValue;
        var matches = results.Count - (pageAvailable ? 1 : 0);
        var summary = matches switch
        {
            0 when pageAvailable => "",
            0 => "没有匹配结果。",
            ReaderSearchLimit => $"显示前 {ReaderSearchLimit} 个文字匹配",
            _ => $"{matches} 个文字匹配"
        };
        if (readerSearchPage is int page && !pageAvailable && !HasFinalNavigationPageCount && !paginationFailed)
            summary = (matches == 0 ? "" : summary + " · ") + $"第 {page + 1} 页正在排版。";
        SetSearchMessage(summary);
    }

    private void RefreshSearchResultVisibility(IReadOnlyList<SearchResult> results)
    {
        SearchResults.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (results.Count > 0 && SearchResults.SelectedIndex < 0) SearchResults.SelectedIndex = 0;
    }

    private static int? ParseSearchPage(string input) =>
        input.All(c => char.IsAsciiDigit(c) || c is >= '０' and <= '９') &&
        int.TryParse(input.Normalize(NormalizationForm.FormKC), NumberStyles.None, CultureInfo.InvariantCulture, out int page) &&
        page >= 1 ? page - 1 : null;

    private async void OnFind(object sender, RoutedEventArgs e) => await FindAsync();

    private async void OnReaderSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (lastSearchQuery == ReaderSearch.Text.Trim() && SearchResults.SelectedItem != null)
            await OpenSearchResultAsync();
        else await FindAsync();
    }

    private void OnReaderSearchPreviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || Keyboard.Modifiers != ModifierKeys.None || SearchResults.SelectedItem is not { } selected) return;
        SearchResults.ScrollIntoView(selected);
        SearchResults.UpdateLayout();
        e.Handled = SearchResults.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem row && row.Focus();
    }

    private async void OnSearchResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject element ||
            ItemsControl.ContainerFromElement(SearchResults, element) is not ListBoxItem { DataContext: SearchResult result }) return;
        e.Handled = true;
        SearchResults.SelectedItem = result;
        await OpenSearchResultAsync();
    }

    private async void OnSearchResultKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await OpenSearchResultAsync();
    }

    private async Task OpenSearchResultAsync()
    {
        if (book is not { } opened || moving || turningPage || seekingPosition ||
            SearchResults.SelectedItem is not SearchResult selected) return;

        var items = readerSearchItems;
        if (selected.TargetPage is { } target)
        {
            if (target >= NavigationPageCount)
            {
                UpdateSearchPageResult();
                return;
            }
            CloseSearchForNavigation();
            await SeekPageAsync(target);
        }
        else if (selected.PdfHit is { } match)
        {
            CloseSearchForNavigation();
            if (await NavigateReaderLocationAsync(new ReaderLocation(null, match.Page, 0), match: match) &&
                !ReferenceEquals(readerSearchItems, items) && ReferenceEquals(pdfMatch, match)) ClearPdfSearchMatch();
        }
        else
        {
            var source = epub;
            var document = text;
            var position = new TextPageStart(selected.Offset, 0, selected.Offset);
            var destination = new ReaderLocation(selected.Chapter, selected.Offset, 0, position);
            CloseSearchForNavigation();
            if (await NavigateReaderLocationAsync(destination) && ReferenceEquals(book, opened) && ReferenceEquals(readerSearchItems, items))
                ShowTextSearchMatch(document, source, selected.Chapter, selected.Offset, selected.Length);
        }
    }

    private void CloseSearchForNavigation()
    {
        if (shownPanel == Panel.Search) CloseReaderPanel(focus: false, cancelSearch: false);
    }
}
