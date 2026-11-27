using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private FacingTextPage? facingPage;
    private TextPagination? spreadPagination;
    // The first indexed page in reading order can occupy either side of the spread.
    private int spreadLeftIndex = -1;
    private int spreadRevision;
    private bool doublePage;
    private bool centeredLastLeaf;
    private bool facingSelected;
    private bool updatingSpread;
    private bool spreadUpdateQueued;
    private int? chapterLoadPending;
    private ReaderLocation? spreadReflowLocation;
    private ReaderLocation? retainedLayoutLocation;

    private bool WantsSpread => ReaderSurface.IsVisible && CanUseSpread(ReadingLane.ActualWidth);

    private bool CanUseSpread(double width) => text != null && !continuous && appearance.Layout != "single" &&
        CanFitTextSpread(width);

    private TextPagination? AvailableSpreadPages => textPagination ?? textPagePrefix;

    private bool HasSpread => doublePage && spreadPagination != null && ReferenceEquals(spreadPagination, AvailableSpreadPages);

    private TextPagination? ReadingPageIndex => HasSpread ? spreadPagination : textPagination;

    // A partial index can display a pair only when the end of both leaves is known.
    private bool CanDisplaySpread(TextPagination pages, int target) => target >= 0 && target < pages.Count &&
        (ReferenceEquals(pages, textPagination) || pages.ConfirmedCount - (target & ~1) >= 2);

    private bool TryGetSpreadPage(TextPagination pages, ReaderLocation location, out int page)
    {
        var locationChapter = location.Chapter ?? 0;
        page = -1;
        if (!pages.ContainsChapter(locationChapter)) return false;
        page = location.TextPosition is { } position ? FindTextPage(pages, locationChapter, position) :
            pages.FindPage(locationChapter, location.Offset, location.Fraction);
        return CanDisplaySpread(pages, page);
    }

    private RichTextBox ActiveTextList => HasSpread && facingSelected ? FacingTextList : TextList;

    private bool HasFacingTextColumn => doublePage && !centeredLastLeaf;
    private bool PrimaryPageOnRight => HasFacingTextColumn && UsesRightToLeftPageProgression;

    private void ConfigureSpreadLayout(bool wide)
    {
        var primaryOnRight = wide && !centeredLastLeaf && UsesRightToLeftPageProgression;
        if (doublePage == wide && (Grid.GetColumn(Paper) == 1) == primaryOnRight) return;
        doublePage = wide;
        centeredLastLeaf = false;
        DiscardSpread();
        ArrangeTextLeaves();
        ResetPageTransition();
    }

    private void SetCenteredLastLeaf(bool centered)
    {
        if (centeredLastLeaf == centered) return;
        centeredLastLeaf = centered;
        ArrangeTextLeaves();
    }

    private void ArrangeTextLeaves()
    {
        var primaryOnRight = PrimaryPageOnRight;
        Grid.SetColumn(Paper, primaryOnRight ? 1 : 0);
        Grid.SetColumn(FacingPaper, primaryOnRight ? 0 : 1);
        FacingColumn.Width = HasFacingTextColumn ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        AutomationProperties.SetName(TextList, centeredLastLeaf ? "末页正文" : !doublePage ? "正文" :
            primaryOnRight ? "右页正文" : "左页正文");
        AutomationProperties.SetName(FacingTextList, primaryOnRight ? "左页正文" : "右页正文");
        UpdatePaperGeometry();
        UpdateTextInsets();
    }

    private void DiscardSpread()
    {
        spreadRevision++;
        spreadPagination = null;
        spreadLeftIndex = -1;
        facingSelected = false;
        FacingPaper.Visibility = Visibility.Collapsed;
        facingPage?.Clear();
    }

    private void ResetSpreadForBook()
    {
        chapterLoadPending = null;
        pendingTurns.Clear();
        spreadReflowLocation = null;
        ForgetLayoutLocation();
        DiscardSpread();
        SetCenteredLastLeaf(false);
    }

    private void RememberSpreadPosition()
    {
        if (HasSpread) spreadReflowLocation ??= CaptureLayoutLocation();
    }

    private void ForgetLayoutLocation()
    {
        retainedLayoutLocation = null;
        pageResizeLocation = null;
    }

    private ReaderLocation CaptureLayoutLocation() =>
        CaptureGuidedLocation() ?? pageResizeLocation ?? ReadRetainedLayoutLocation() ?? CaptureReaderLocation();

    private ReaderLocation? ReadRetainedLayoutLocation()
    {
        if (continuous || retainedLayoutLocation is not { } location) return null;
        // The wider layout can put the original source inside a page whose first visible line is earlier.
        if (location.TextPosition is { } position && ReadingPageIndex is { } pages &&
            CurrentTextPage(pages) != FindTextPage(pages, location.Chapter ?? 0, position)) return null;
        return location;
    }

    private ReaderLocation CurrentSpreadLocation()
    {
        var index = spreadLeftIndex + (facingSelected ? 1 : 0);
        var position = spreadPagination!.GetPage(index, out var locationChapter);
        return new ReaderLocation(epub == null ? null : locationChapter, Math.Max(0, position.Offset),
            position.Fraction, position);
    }

    private void QueueSpreadUpdate()
    {
        if (spreadUpdateQueued || closing || !ReaderSurface.IsVisible ||
            doublePage == WantsSpread && (!doublePage || AvailableSpreadPages == null || HasSpread)) return;
        spreadUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateSpreadAsync));
    }

    private async void UpdateSpreadAsync()
    {
        var startedPagination = AvailableSpreadPages;
        try
        {
            if (closing || book == null || moving || turningPage || seekingPosition || chapterLoadPending != null) return;
            if (doublePage != WantsSpread)
            {
                await ApplySpreadLayoutAsync(spreadReflowLocation ?? CaptureLayoutLocation());
                return;
            }
            if (!doublePage || AvailableSpreadPages is not { } pages || HasSpread) return;
            var location = spreadReflowLocation ?? CaptureLayoutLocation();
            if (TryGetSpreadPage(pages, location, out var pageIndex) && await MoveToSpreadAsync(pages, pageIndex))
            {
                pageResizeLocation = null;
                retainedLayoutLocation = location;
                SaveCurrentPosition();
            }
        }
        catch (Exception error)
        {
            if (!closing) ShowStatus(error.Message);
        }
        finally
        {
            spreadUpdateQueued = false;
            if (!moving && !turningPage && !seekingPosition && chapterLoadPending == null &&
                (doublePage != WantsSpread || !ReferenceEquals(startedPagination, AvailableSpreadPages)))
                QueueSpreadUpdate();
        }
    }

    private async Task ApplySpreadLayoutAsync(ReaderLocation location)
    {
        if (doublePage == WantsSpread) return;
        var previous = seekingPosition;
        var opened = book;
        seekingPosition = true;
        try
        {
            ConfigureSpreadLayout(WantsSpread);
            pageResizeLocation = !continuous && !doublePage ? location : null;
            spreadReflowLocation = null;
            ResetTextPageNumbers();
            PaperFrame.UpdateLayout();
            if (await RestoreReaderLocationAsync(location) && ReferenceEquals(book, opened))
            {
                if (!continuous) retainedLayoutLocation = location;
                if (!AppearancePopup.IsOpen && shownPanel == null) FocusReadingSurface();
            }
        }
        finally
        {
            seekingPosition = previous;
            if (ReferenceEquals(book, opened)) SetProgress();
        }
    }

    private async Task<bool> MoveToSpreadAsync(TextPagination pages, int target)
    {
        if (!doublePage || !ReferenceEquals(AvailableSpreadPages, pages) || text == null || reading == null ||
            !CanDisplaySpread(pages, target)) return false;
        var firstIndex = target & ~1;
        if (HasSpread && spreadLeftIndex == firstIndex)
        {
            SelectFacingPage(target != firstIndex);
            return true;
        }

        var version = ++spreadRevision;
        var opened = book;
        var token = reading.Token;
        var indexToken = paginating?.Token;
        var source = epub;
        var firstStart = pages.GetPage(firstIndex, out var firstChapter);
        var facingIndex = firstIndex + 1;
        var facingStart = facingIndex < pages.Count ? pages.GetPage(facingIndex, out _) : (TextPageStart?)null;
        try
        {
            var first = await ReadSpreadChapterAsync(source, firstChapter, token);
            TextDocument? facing = null;
            var facingChapter = firstChapter;
            if (facingStart != null)
            {
                pages.GetPage(facingIndex, out facingChapter);
                facing = facingChapter == firstChapter ? first : await ReadSpreadChapterAsync(source, facingChapter, token);
            }
            if (token.IsCancellationRequested || version != spreadRevision || !ReferenceEquals(book, opened) ||
                !doublePage || AvailableSpreadPages is not { } committed ||
                !ReferenceEquals(committed, pages) && (indexToken == null || indexToken != paginating?.Token)) return false;

            updatingSpread = true;
            try
            {
                spreadPagination = null;
                facingSelected = false;
                chapterLoadVersion++;
                text = first;
                chapter = firstChapter;
                if (!TextList.Selection.IsEmpty) TextList.Selection.Select(TextList.Selection.Start, TextList.Selection.Start);
                SetCenteredLastLeaf(facingStart == null);
                MoveToText(firstStart.Offset, firstStart.Fraction, page: firstStart);
                if (facingStart is { } start && facing != null)
                {
                    FacingPaper.Visibility = Visibility.Visible;
                    PaperFrame.UpdateLayout();
                    (facingPage ??= new FacingTextPage(FacingTextList, OnOpenIllustration)).Show(facing, facingChapter,
                        source?.Chapters[facingChapter].Title, start, TextList.Document.FontSize, TextList.Document.LineHeight,
                        TextList.Document.FontWeight == FontWeights.Bold, TextList.Document.TextAlignment, appearance.Hyphenation);
                }
                else
                {
                    FacingPaper.Visibility = Visibility.Collapsed;
                    facingPage?.Clear();
                }
                // A larger snapshot from this worker keeps the already confirmed page starts unchanged.
                spreadPagination = committed;
                spreadLeftIndex = firstIndex;
                facingSelected = target == facingIndex;
                spreadReflowLocation = null;
            }
            finally { updatingSpread = false; }
            SetProgress();
            SaveCurrentPosition();
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            if (ReferenceEquals(book, opened) && !token.IsCancellationRequested) ShowStatus(error.Message);
            return false;
        }
    }

    private async Task<TextDocument> ReadSpreadChapterAsync(IChapterBook? source, int index, CancellationToken token)
    {
        if (source == null || index == chapter) return text!;
        if (facingPage is { Document: { } previous } && index == facingPage.Chapter) return previous;
        return await BookFormats.ReadChapterDocumentAsync(source, index, token);
    }

    private void OnTextPageFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!moving && !updatingSpread && !Within<EpubIllustrationButton>(e.NewFocus as DependencyObject))
            SelectFacingPage(ReferenceEquals(sender, FacingTextList));
    }

    private void SelectFacingPage(bool select)
    {
        if (!HasSpread || facingSelected == select || select && FacingPaper.Visibility != Visibility.Visible) return;
        facingSelected = select;
        ForgetLayoutLocation();
        SetProgress();
        SaveCurrentPosition();
    }

}
