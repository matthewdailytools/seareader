using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const string WaitingForPageCount = "正在计算页数，请稍后再试。";
    private BookEntry? pageNavigationBook;

    private TextPagination? NavigationTextPages => textPagination ?? (HasSpread ? spreadPagination :
        !doublePage && !continuous ? textPagePrefix : null);
    private int NavigationPageCount => pdf?.PageCount ??
        (NavigationTextPages is { } pages ? NavigableTextPageCount(pages) : 0);
    private bool HasFinalNavigationPageCount => pdf != null || textPagination != null;

    private int NavigableTextPageCount(TextPagination pages) =>
        doublePage && !ReferenceEquals(pages, textPagination) ? pages.ConfirmedCount & ~1 : pages.ConfirmedCount;

    private bool IsCurrentPageIndex(CancellationToken? indexToken) =>
        indexToken == paginating?.Token && NavigationTextPages != null;

    private void UpdateGoToPageAvailability()
    {
        if (!ReferenceEquals(book, pageNavigationBook))
        {
            pageNavigationBook = book;
            SetPrintedPageMode(false);
            GoToPageBox.Clear();
        }
        PageJumpModeButton.Visibility = HasPrintedPages ? Visibility.Visible : Visibility.Collapsed;
        var count = NavigationPageCount;
        GoToPageBox.IsEnabled = !seekingPosition && (enteringPrintedPage || count > 0);
        GoToPageBox.ToolTip = enteringPrintedPage ? "输入书籍原有的页码或选择下面的条目" :
            count == 0 ? PageNavigationWaitingMessage() :
            HasFinalNavigationPageCount ? $"输入 1 到 {count} 之间的页码后按 Enter" :
            $"已排版第 1 到 {count} 页，可按 Enter 跳转。正在计算全书页数。";
        if (StatusText.Text == WaitingForPageCount)
        {
            if (count > 0) ShowStatus("");
            else if (paginationFailed) ShowStatus(PageNavigationWaitingMessage());
        }
    }

    private string PageNavigationWaitingMessage() =>
        paginationFailed ? "暂时无法获取页数。" : WaitingForPageCount;

    private void OnShowGoToPage(object sender, RoutedEventArgs e)
    {
        if (book == null || moving || turningPage || seekingPosition) return;
        DismissInlineSelection();
        ClosePanel(false);
        SetControls(true);
        SetProgress();
        if (NavigationPageCount == 0 && !HasPrintedPages)
        {
            ShowStatus(PageNavigationWaitingMessage());
            return;
        }
        PageJumpPanel.Visibility = Visibility.Visible;
        if (NavigationPageCount == 0)
        {
            SetPrintedPageMode(true);
            UpdateGoToPageAvailability();
        }
        UpdateJumpHistoryControls();
        if (!enteringPrintedPage) FillCurrentPageNumber();
        GoToPageBox.Focus();
        GoToPageBox.SelectAll();
    }

    private async void OnGoToPageKeyDown(object sender, KeyEventArgs e)
    {
        if (enteringPrintedPage && e.Key == Key.Down && FocusPrintedPageMatches()) e.Handled = true;
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SeekEnteredPageAsync();
        }
    }

    private async void OnGoToPage(object sender, RoutedEventArgs e) => await SeekEnteredPageAsync();

    private async Task SeekEnteredPageAsync()
    {
        if (book == null || moving || turningPage || seekingPosition) return;
        if (enteringPrintedPage)
        {
            await SeekEnteredPrintedPageAsync();
            return;
        }
        var count = NavigationPageCount;
        if (count == 0)
        {
            ShowStatus(PageNavigationWaitingMessage());
            return;
        }
        if (!int.TryParse(GoToPageBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var page) ||
            page < 1 || page > count)
        {
            ShowStatus(HasFinalNavigationPageCount ? $"请输入 1 到 {count} 之间的页码。" :
                $"目前可前往第 1 到 {count} 页。正在计算全书页数。");
            GoToPageBox.Focus();
            GoToPageBox.SelectAll();
            return;
        }
        ShowStatus("");
        await SeekPageAsync(page - 1);
    }

    private Task SeekPageAsync(int target, bool focusReader = true)
    {
        if (book == null || moving || turningPage || seekingPosition || target < 0 || target >= NavigationPageCount)
            return Task.CompletedTask;
        var document = pdf;
        var pages = NavigationTextPages;
        var current = document != null ? pdfPage : CurrentConfirmedTextPage(pages!);
        if (target == current)
        {
            pendingTurns.Clear();
            ShowStatus("");
            ClosePageJump();
            if (focusReader) FocusReadingSurface();
            return Task.CompletedTask;
        }

        return SeekWithHistoryAsync(MoveToTargetAsync, focusReader);

        async Task<bool> MoveToTargetAsync()
        {
            if (document == null) return await MoveToIndexedTextPageAsync(pages!, target);
            await ChangePdfPageAsync(target);
            return IsRenderedPdfPage(document, target);
        }
    }

    private bool IsRenderedPdfPage(IPageBook document, int page) =>
        ReferenceEquals(pdf, document) && pdfPage == page && CurrentPdfImage != null;

    private Task SeekChapterBoundaryAsync(Key key)
    {
        if (epub == null || text == null || moving || turningPage || seekingPosition) return Task.CompletedTask;
        var current = CaptureNavigationLocation().Chapter ?? chapter;
        var target = key switch
        {
            Key.Left => current - 1,
            Key.Right => current + 1,
            _ => current
        };
        if (target < 0 || target >= epub.Chapters.Count) return Task.CompletedTask;

        var position = new TextPageStart(-1, 0, -1);
        if (key == Key.Down)
        {
            if (!continuous && textPagination is { } pages)
                position = pages.GetPage(pages.LastPageOfChapter(target), out _);
            else
            {
                var document = target == chapter ? text : facingPage?.Document;
                if (document == null) return Task.CompletedTask;
                position = new TextPageStart(document.Lines[^1].Offset, 0, document.Text.Length);
            }
        }

        return NavigateReaderLocationAsync(new ReaderLocation(target, Math.Max(0, position.Offset), position.Fraction, position));
    }

    private async Task SeekWithHistoryAsync(Func<Task<bool>> move, bool focusReader)
    {
        if (book is not { } opened || moving || turningPage || seekingPosition) return;
        pendingTurns.Clear();
        var session = reading;
        var origin = CaptureNavigationLocation();
        var previousView = CaptureJumpView();
        var moved = false;
        ForgetLayoutLocation();
        seekingPosition = true;
        ResetPageTransition();
        SetProgress();
        try
        {
            moved = await move();
            if (!ReferenceEquals(book, opened) || !ReferenceEquals(reading, session) || !moved) return;

            RecordReaderJump(origin);
            ClosePageJump();
            if (focusReader) FocusReadingSurface();
        }
        catch (Exception error)
        {
            if (ReferenceEquals(book, opened) && ReferenceEquals(reading, session)) ShowStatus(error.Message);
        }
        finally
        {
            seekingPosition = false;
            if (ReferenceEquals(book, opened) && ReferenceEquals(reading, session))
            {
                if (!moved) RetainFailedJumpLocation(origin, previousView);
                SetProgress();
            }
        }
    }

    private ReaderLocation CaptureNavigationLocation() => !continuous && text != null
        ? pageResizeLocation ?? ReadRetainedLayoutLocation() ?? CaptureReaderLocation()
        : CaptureReaderLocation();

    private ReaderLocation? CaptureJumpView() => text != null && !continuous ? CaptureReaderLocation() : null;

    private void RetainFailedJumpLocation(ReaderLocation origin, ReaderLocation? previousView)
    {
        if (text != null && !continuous && previousView is { } previous && SameReaderLocation(previous, CaptureReaderLocation()))
            RetainNavigationTextLocation(origin);
    }

    private void RecordReaderJump(ReaderLocation origin)
    {
        if (SameReaderLocation(origin, CaptureNavigationLocation())) return;
        if (returnLocation == null || returnLeadsToDestination) returnLocation = origin;
        returnLeadsToDestination = false;
        UpdateJumpHistoryControls();
    }

    private async Task<bool> RestoreNavigationLocationAsync(ReaderLocation location)
    {
        var session = reading;
        var opened = book;
        if (!await RestoreReaderLocationAsync(location) || !ReferenceEquals(book, opened) || !ReferenceEquals(reading, session))
            return false;
        if (!continuous && text != null && location.TextPosition.HasValue)
        {
            RetainNavigationTextLocation(location);
            SaveCurrentPosition();
        }
        return true;
    }

    private void RetainNavigationTextLocation(ReaderLocation location)
    {
        retainedLayoutLocation = location;
        if (doublePage && !HasSpread) spreadReflowLocation = location;
        else if (textPagination == null) pageResizeLocation = location;
    }

    private async Task<bool> MoveToIndexedTextPageAsync(TextPagination pages, int target)
    {
        if (!ReferenceEquals(NavigationTextPages, pages) || target < 0 || target >= NavigableTextPageCount(pages))
            return false;
        if (doublePage) return await MoveToSpreadAsync(pages, target);
        var indexToken = paginating?.Token;
        var page = pages.GetPage(target, out var nextChapter);
        if (chapter != nextChapter)
            await ChangeChapterAsync(nextChapter, page.Offset, page.Fraction, page, requiredIndex: indexToken);
        else
        {
            MoveToText(page.Offset, page.Fraction, page: page);
            SaveCurrentPosition();
        }
        return IsCurrentPageIndex(indexToken) && NavigationTextPages is { } latest &&
            CurrentConfirmedTextPage(latest) == target;
    }

    private async Task<bool> RestoreReaderLocationAsync(ReaderLocation location)
    {
        if (pdf is { } document)
        {
            await ChangePdfPageAsync(location.Offset);
            if (!IsRenderedPdfPage(document, location.Offset)) return false;
            pdfMatch = null;
            ActivePdfSelection.ShowAnnotation([]);
            PdfSurface.ScrollToHorizontalOffset(location.PdfHorizontalOffset);
            PdfSurface.ScrollToVerticalOffset(location.Fraction);
            PdfSurface.UpdateLayout();
            return true;
        }

        var destinationChapter = location.Chapter ?? 0;
        var position = location.TextPosition;
        if (!continuous && position is { } source && textPagination is { } pages)
        {
            var page = pages.FindPage(destinationChapter, source);
            return await MoveToIndexedTextPageAsync(pages, page);
        }
        if (textPagination == null && HasSpread && spreadPagination is { } prefix &&
            TryGetSpreadPage(prefix, location, out var confirmedPage))
            return await MoveToIndexedTextPageAsync(prefix, confirmedPage);
        if (!doublePage && !continuous && position is { } measured && textPagePrefix is { } singlePrefix &&
            FindConfirmedTextPage(singlePrefix, destinationChapter, measured) is int singlePage)
            return await MoveToIndexedTextPageAsync(singlePrefix, singlePage);
        if (location.Chapter is { } index)
        {
            await ChangeChapterAsync(index, location.Offset, location.Fraction, position, preserveSource: position != null);
            return chapter == index;
        }
        MoveToText(location.Offset, location.Fraction, page: position, preserveSource: position != null);
        SaveCurrentPosition();
        return true;
    }

    private void ClosePageJump()
    {
        PageJumpPanel.Visibility = Visibility.Collapsed;
        SetPrintedPageMode(false);
        GoToPageBox.Clear();
        UpdateJumpHistoryControls();
    }

    private void FillCurrentPageNumber()
    {
        var page = pdf != null ? pdfPage : NavigationTextPages is { } pages ? CurrentConfirmedTextPage(pages) : null;
        GoToPageBox.Text = page is int current ? (current + 1).ToString(CultureInfo.InvariantCulture) : "";
    }

    private void FocusReadingSurface()
    {
        if (pdf != null) ActivePdfSelection.Focus();
        else ActiveTextList.Focus();
    }
}
