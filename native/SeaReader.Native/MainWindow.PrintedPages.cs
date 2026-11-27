using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace SeaReader.Native;

public partial class MainWindow
{
    private sealed record PrintedPageChoice(EpubPrintedPage Page, string Display);

    private bool enteringPrintedPage;
    private CancellationTokenSource? printedPageRequest;
    private bool HasPrintedPages => epub is EpubDocument { PrintedPages.Count: > 0 };

    private void OnPageJumpMode(object sender, RoutedEventArgs e)
    {
        if (!HasPrintedPages || seekingPosition) return;
        SetPrintedPageMode(!enteringPrintedPage);
        UpdateGoToPageAvailability();
        GoToPageBox.Focus();
        GoToPageBox.SelectAll();
    }

    private void SetPrintedPageMode(bool printed)
    {
        if (enteringPrintedPage == printed) return;
        enteringPrintedPage = printed;
        CancelPrintedPageRequest();
        var prompt = printed ? "原书页码" : "前往页码";
        GoToPageLabel.Content = prompt;
        AutomationProperties.SetName(GoToPageBox, prompt);
        PageJumpModeButton.Content = printed ? pdf != null ? "物理页码" : "排版页码" : "原书页码";
        GoToPageBox.Width = printed ? 118 : 76;
        GoToPageBox.MaxLength = printed ? 256 : 10;
        GoToPageBox.InputScope = printed ? null : new InputScope { Names = { new InputScopeName(InputScopeNameValue.Digits) } };
        if (printed)
        {
            if (GoToPageBox.Text.Length != 0) GoToPageBox.Clear();
            else UpdatePrintedPageMatches();
        }
        else
        {
            PrintedPageMatches.Visibility = Visibility.Collapsed;
            PrintedPageMatches.ItemsSource = null;
            if (PageJumpPanel.IsVisible) FillCurrentPageNumber();
        }
    }

    private void OnPrintedPageQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (!enteringPrintedPage) return;
        CancelPrintedPageRequest();
        ShowStatus("");
        UpdatePrintedPageMatches();
    }

    private void UpdatePrintedPageMatches()
    {
        if (epub is not EpubDocument source) return;
        var query = string.Join(' ', GoToPageBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        IReadOnlyList<EpubPrintedPage> matches = source.PrintedPages;
        if (query.Length != 0)
        {
            var exact = matches.Where(page => page.Label.Equals(query, StringComparison.Ordinal)).ToArray();
            matches = exact.Length != 0 ? exact : matches.Where(page =>
                page.Label.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        var repeated = matches.GroupBy(page => (page.Label, page.Chapter)).Where(group => group.Skip(1).Any())
            .ToDictionary(group => group.Key, _ => 0);
        var choices = new PrintedPageChoice[matches.Count];
        for (int index = 0; index < matches.Count; index++)
        {
            var page = matches[index];
            var display = $"{page.Label} · {source.Chapters[page.Chapter].Title}";
            var key = (page.Label, page.Chapter);
            if (repeated.TryGetValue(key, out var occurrence))
            {
                repeated[key] = ++occurrence;
                display += $" · 第 {occurrence} 处";
            }
            choices[index] = new PrintedPageChoice(page, display);
        }
        PrintedPageMatches.ItemsSource = choices;
        PrintedPageMatches.Visibility = choices.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool FocusPrintedPageMatches()
    {
        if (!PrintedPageMatches.HasItems || !PrintedPageMatches.IsVisible) return false;
        if (PrintedPageMatches.SelectedIndex < 0) PrintedPageMatches.SelectedIndex = 0;
        PrintedPageMatches.ScrollIntoView(PrintedPageMatches.SelectedItem);
        PrintedPageMatches.UpdateLayout();
        if (PrintedPageMatches.ItemContainerGenerator.ContainerFromIndex(PrintedPageMatches.SelectedIndex) is ListBoxItem item)
            item.Focus();
        else PrintedPageMatches.Focus();
        return true;
    }

    private Task SeekEnteredPrintedPageAsync()
    {
        if (PrintedPageMatches.SelectedItem is PrintedPageChoice selected) return SeekPrintedPageAsync(selected.Page);
        if (PrintedPageMatches.Items.Count == 1 && PrintedPageMatches.Items[0] is PrintedPageChoice only)
            return SeekPrintedPageAsync(only.Page);
        if (!FocusPrintedPageMatches())
        {
            ShowStatus("找不到这个原书页码。");
            GoToPageBox.Focus();
            GoToPageBox.SelectAll();
        }
        return Task.CompletedTask;
    }

    private async void OnPrintedPageListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SeekEnteredPrintedPageAsync();
    }

    private async void OnPrintedPageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject element ||
            ItemsControl.ContainerFromElement(PrintedPageMatches, element) is not ListBoxItem { DataContext: PrintedPageChoice choice }) return;
        e.Handled = true;
        await SeekPrintedPageAsync(choice.Page);
    }

    private async Task SeekPrintedPageAsync(EpubPrintedPage page)
    {
        if (!enteringPrintedPage || !PageJumpPanel.IsVisible || reading is not { } session ||
            epub is not EpubDocument source || moving || turningPage || seekingPosition) return;
        CancelPrintedPageRequest();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        printedPageRequest = request;
        var origin = CaptureNavigationLocation();
        ShowStatus("");
        try
        {
            if (pdf is FixedEpubPageBook fixedBook)
            {
                await NavigateFixedEpubTargetAsync(fixedBook, new FixedEpubTarget(page.Chapter, page.Fragment, page.LiteralFragment), request.Token);
                return;
            }

            var target = new TextPageStart(-1, 0, -1);
            if (page.Fragment.Length != 0)
            {
                var anchors = await source.ReadChapterAnchorsAsync(page.Chapter, request.Token);
                if (!IsCurrent()) return;
                if (!anchors.TryGetValue(page.Fragment, out var offset))
                {
                    ShowStatus("找不到这个原书页码在正文中的位置。");
                    return;
                }
                target = new TextPageStart(offset, 0, offset);
            }
            if (IsCurrent())
                await NavigateReaderLocationAsync(new ReaderLocation(page.Chapter, Math.Max(0, target.Offset), 0, target),
                    showTransientAnnotation: false, jumpOrigin: origin);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            if (IsCurrent()) ShowStatus("暂时无法读取这个原书页码的位置。");
        }
        finally
        {
            if (ReferenceEquals(printedPageRequest, request)) printedPageRequest = null;
        }

        bool IsCurrent() => !closing && !moving && !turningPage && !seekingPosition && !request.IsCancellationRequested &&
            ReferenceEquals(printedPageRequest, request) && ReferenceEquals(reading, session) && ReferenceEquals(epub, source) &&
            SameReaderLocation(origin, CaptureNavigationLocation());
    }

    private void CancelPrintedPageRequest()
    {
        printedPageRequest?.Cancel();
        printedPageRequest = null;
    }
}
