using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SeaReader.Native;

public partial class MainWindow
{
    private readonly record struct ReaderLocation(int? Chapter, int Offset, double Fraction, TextPageStart? TextPosition = null,
        double PdfHorizontalOffset = 0);
    private bool previewingPosition;
    private CancellationTokenSource? positionPreviewSession;
    private Track? positionDragTrack;
    private double positionPointerOffset;
    private bool seekingPosition;
    private bool returnLeadsToDestination;
    private TextPagination? sliderPagination;
    private string? readerBookmarksBookId;
    private int readerBookmarksVersion;
    private HashSet<string> readerBookmarkPositions = [];
    private Task bookmarkWrite = Task.CompletedTask;
    private int keyboardBookmarkNoticeVersion;
    private string? keyboardBookmarkNotice;

    private int ProgressMaximum => Math.Max(0, (pdf?.PageCount ?? epub?.Chapters.Count ?? text?.Text.Length ?? 1) - 1);

    private async Task HandleReaderEscapeAsync(KeyEventArgs e)
    {
        if (e.IsRepeat)
        {
            e.Handled = true;
            return;
        }
        if (Within<ContextMenu>(e.OriginalSource as DependencyObject)) return;
        if (AppearancePopup.IsOpen)
        {
            OnAppearancePopupKeyDown(this, e);
            return;
        }

        e.Handled = true;
        if (ReaderToolsButton.ContextMenu.IsOpen) CloseReaderTools();
        else if (inlineSelection != null) DismissInlineSelection(restoreFocus: true);
        else if (AbortPositionPreview()) return;
        else if (PageJumpPanel.IsVisible)
        {
            ClosePageJump();
            ShowStatus("");
            FocusReadingSurface();
        }
        else if (shownPanel != null) ClosePanel();
        else if (controlsVisible)
        {
            SetControls(false);
            FocusReadingSurface();
        }
        else if (IsReaderFullScreen) await ToggleReaderFullScreenAsync();
        else if (LineGuideActive && ActiveTextList.IsKeyboardFocusWithin) SetLineGuideMode("off");
        else e.Handled = false;
    }

    private bool HandleReaderNavigationShortcut(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (e.Key != Key.T || book == null) return false;
        var thumbnails = modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        Panel navigation;
        if (thumbnails && pdf != null) navigation = Panel.PdfPages;
        else if (modifiers == ModifierKeys.Control && (epub != null || pdf != null))
            navigation = epub != null ? Panel.Contents : Panel.PdfPages;
        else return false;

        e.Handled = true;
        if (e.IsRepeat) return true;
        ReaderToolsButton.ContextMenu.IsOpen = false;
        ClosePageJump();
        if (shownPanel != navigation) ShowReaderPanel(navigation, revealControls: true, focusThumbnail: thumbnails);
        else if (thumbnails && !PdfThumbnailList.IsKeyboardFocusWithin) FocusSelectedPdfThumbnail();
        else ClosePanel();
        return true;
    }

    private int CurrentProgress
    {
        get
        {
            if (pdf != null) return pdfPage;
            if (epub != null) return chapter;
            if (HasSpread)
            {
                var location = CurrentSpreadLocation();
                return Math.Max(0, location.TextPosition?.SourceOffset ?? location.Offset);
            }
            return !CanTurnPage(1) ? ProgressMaximum : anchor.Offset;
        }
    }

    private void SetProgress()
    {
        QueueTextPagination();
        var previous = populating;
        populating = true;
        try
        {
            if (!previewingPosition)
            {
                sliderPagination = textPagination;
                var maximum = sliderPagination is { } pages ? pages.Count - 1 : ProgressMaximum;
                var position = sliderPagination is { } indexed ? CurrentTextPage(indexed) : CurrentProgress;
                var offsetBased = pdf == null && epub == null && sliderPagination == null;
                PositionSlider.Maximum = Math.Max(1, maximum);
                PositionSlider.SmallChange = offsetBased ? Math.Max(maximum / 100, 1) : 1;
                PositionSlider.LargeChange = offsetBased ? Math.Max(maximum / 10, 1) : 1;
                PositionSlider.Value = position;
                PositionSlider.IsEnabled = maximum > 0 && !seekingPosition;
                SetProgressLabel(position);
            }
            PreviousButton.IsEnabled = !seekingPosition && CanTurnPage(-1);
            NextButton.IsEnabled = !seekingPosition && CanRequestTurn(1);
            NextButton.ToolTip = WaitingForInitialSpread ? "正在排版双页…" :
                WaitingForSpreadTurn(1) ? "正在排版下一跨页…" : null;
            var reverse = ReverseHorizontalPageTurns;
            LeftPageEdge.IsEnabled = reverse ? NextButton.IsEnabled : PreviousButton.IsEnabled;
            RightPageEdge.IsEnabled = reverse ? PreviousButton.IsEnabled : NextButton.IsEnabled;
            AutomationProperties.SetName(LeftPageEdge, reverse ? "下一页，左页边" : "上一页，左页边");
            AutomationProperties.SetName(RightPageEdge, reverse ? "上一页，右页边" : "下一页，右页边");
            UpdateReaderLayoutStatus();
            UpdateBookmarkButton();
            UpdateGoToPageAvailability();
            UpdateSearchPageResult();
        }
        finally { populating = previous; }
        QueueSpreadUpdate();
        QueuePendingTurns();
        QueueLineGuideRefresh();
        QueueContentsRefresh();
        QueueTextPageEdgeClip();
    }

    private void SetProgressLabel(int position)
    {
        var current = text != null ? TextPageLabel() : pdf is { } document && pdfFacingPage >= 0
            ? $"页 {Math.Min(pdfPrimaryPage, pdfFacingPage) + 1}–{Math.Max(pdfPrimaryPage, pdfFacingPage) + 1} / {document.PageCount}"
            : ProgressLocationLabel(position);
        ProgressText.Text = current;
        CompactProgressText.Text = current;
        var showFolio = text != null && !continuous;
        ReaderFolio.Visibility = showFolio ? Visibility.Visible : Visibility.Collapsed;
        CompactProgressButton.Visibility = showFolio ? Visibility.Collapsed : Visibility.Visible;
        if (showFolio)
        {
            ReaderFolio.Content = current;
            AutomationProperties.SetName(ReaderFolio, $"{current}，前往页码");
        }
        if (epub != null && textPagination is { } pages)
        {
            var pageIndex = CurrentTextPage(pages);
            pages.GetPage(pageIndex, out var currentChapter);
            var remaining = pages.LastPageOfChapter(currentChapter) - pageIndex;
            ChapterRemainingText.Text = remaining > 0 ? $"本章余 {remaining} 页" : "本章末页";
            ChapterRemainingText.Visibility = Visibility.Visible;
        }
        else ChapterRemainingText.Visibility = Visibility.Collapsed;
        PositionSlider.ToolTip = sliderPagination == null && epub != null && position >= 0 && position < epub.Chapters.Count
            ? epub.Chapters[position].Title : current;
    }

    private string ProgressLocationLabel(int position) => pdf != null ? $"页 {position + 1} / {pdf.PageCount}" :
        sliderPagination is { } pages ? $"页 {position + 1} / {pages.Count}" :
        epub != null ? $"章 {position + 1} / {epub.Chapters.Count}" :
        text != null ? $"{position * 100.0 / Math.Max(ProgressMaximum, 1):0.#}%" : "";

    private void OnPositionPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (previewingPosition || !PositionSlider.IsEnabled ||
            PositionSlider.Template?.FindName("PART_Track", PositionSlider) is not Track { Thumb: { } thumb } track ||
            !PositionSlider.CaptureMouse()) return;
        if (!BeginPositionPreview())
        {
            PositionSlider.ReleaseMouseCapture();
            return;
        }

        e.Handled = true;
        PositionSlider.Focus();
        positionDragTrack = track;
        positionPointerOffset = 0;
        if (e.OriginalSource is Visual source && (ReferenceEquals(source, thumb) || thumb.IsAncestorOf(source)))
            positionPointerOffset = e.GetPosition(thumb).X - thumb.ActualWidth / 2;
        SetPositionFromPointer(track, e.GetPosition(track).X);
    }

    private bool BeginPositionPreview()
    {
        if (previewingPosition || !PositionSlider.IsEnabled || book == null || moving || turningPage || seekingPosition ||
            reading is not { IsCancellationRequested: false } session) return false;
        previewingPosition = true;
        positionPreviewSession = session;
        UpdatePositionPreview((int)PositionSlider.Value);
        return true;
    }

    private void OnPositionPointerMove(object sender, MouseEventArgs e)
    {
        if (positionDragTrack is not { } track || !PositionSlider.IsMouseCaptured) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            AbortPositionPreview();
            return;
        }
        e.Handled = true;
        SetPositionFromPointer(track, e.GetPosition(track).X);
    }

    private void SetPositionFromPointer(Track track, double x)
    {
        var thumbWidth = track.Thumb?.ActualWidth ?? 0;
        var travel = track.ActualWidth - thumbWidth;
        if (travel <= 0 || !double.IsFinite(x)) return;
        var fraction = Math.Clamp((x - positionPointerOffset - thumbWidth / 2) / travel, 0, 1);
        if (track.IsDirectionReversed) fraction = 1 - fraction;
        PositionSlider.Value = Math.Round(PositionSlider.Minimum + (PositionSlider.Maximum - PositionSlider.Minimum) * fraction);
    }

    private async void OnPositionPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (!previewingPosition) return;
        if (positionDragTrack is { } track)
        {
            e.Handled = true;
            SetPositionFromPointer(track, e.GetPosition(track).X);
        }
        await CommitPositionPreviewAsync();
    }

    private void OnPositionLostCapture(object sender, MouseEventArgs e)
    {
        if (!PositionSlider.IsMouseCaptured) AbortPositionPreview();
    }

    private void OnReaderDeactivated(object? sender, EventArgs e)
    {
        DismissInlineSelection();
        AbortPositionPreview();
    }

    private void UpdatePositionPreview(int position)
    {
        var label = ProgressLocationLabel(position);
        var context = ProgressPreviewContext(position);
        PositionPreviewLabel.Text = $"前往 {label}";
        PositionPreviewDetail.Text = context ?? "";
        PositionPreviewDetail.Visibility = context == null ? Visibility.Collapsed : Visibility.Visible;
        PositionPreviewBubble.Visibility = Visibility.Visible;
        AutomationProperties.SetItemStatus(PositionSlider, PositionPreviewLabel.Text);
    }

    private async Task CommitPositionPreviewAsync()
    {
        if (!previewingPosition) return;
        var session = positionPreviewSession;
        var target = (int)PositionSlider.Value;
        CancelPositionPreview();
        if (moving || turningPage || seekingPosition || !ReferenceEquals(reading, session) ||
            session is not { IsCancellationRequested: false })
        {
            SetProgress();
            return;
        }
        await SeekSliderTargetAsync(target);
    }

    private void CancelPositionPreview()
    {
        previewingPosition = false;
        positionPreviewSession = null;
        positionDragTrack = null;
        PositionPreviewBubble.Visibility = Visibility.Collapsed;
        PositionPreviewLabel.Text = PositionPreviewDetail.Text = "";
        AutomationProperties.SetItemStatus(PositionSlider, "");
        if (PositionSlider.IsMouseCaptured) PositionSlider.ReleaseMouseCapture();
    }

    private bool AbortPositionPreview()
    {
        if (!previewingPosition) return false;
        CancelPositionPreview();
        SetProgress();
        return true;
    }

    private async void OnPositionSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (populating || book == null) return;
        var target = (int)e.NewValue;
        if (previewingPosition) UpdatePositionPreview(target);
        else await SeekSliderTargetAsync(target);
    }

    private async Task SeekSliderTargetAsync(int target)
    {
        if (sliderPagination is not { } previewIndex) await SeekPositionAsync(target);
        else if (ReferenceEquals(previewIndex, textPagination)) await SeekPageAsync(target, focusReader: false);
        else if (text != null && target >= 0 && target < previewIndex.Count)
        {
            // While dragging, the slider continues to show the index the user saw at pointer-down.
            var position = previewIndex.GetPage(target, out var targetChapter);
            var destination = new ReaderLocation(epub == null ? null : targetChapter, Math.Max(0, position.Offset),
                position.Fraction, position);
            await SeekWithHistoryAsync(() => RestoreNavigationLocationAsync(destination), focusReader: false);
        }
        else SetProgress();
    }

    private Task SeekPositionAsync(int position)
    {
        if (moving || seekingPosition || turningPage || book == null || position == CurrentProgress)
        {
            SetProgress();
            return Task.CompletedTask;
        }
        var target = Math.Clamp(position, 0, ProgressMaximum);
        var document = pdf;
        var chapters = epub;
        return SeekWithHistoryAsync(MoveToTargetAsync, focusReader: false);

        async Task<bool> MoveToTargetAsync()
        {
            ClosePanel(false);
            if (document != null)
            {
                await ChangePdfPageAsync(target);
                return IsRenderedPdfPage(document, target);
            }
            if (chapters != null)
            {
                await ChangeChapterAsync(target);
                return ReferenceEquals(epub, chapters) && chapter == target;
            }
            MoveToText(target);
            SaveCurrentPosition();
            return true;
        }
    }

    private ReaderLocation CaptureReaderLocation()
    {
        if (pdf != null) return new ReaderLocation(null, pdfPage, PdfSurface.VerticalOffset,
            PdfHorizontalOffset: PdfSurface.HorizontalOffset);
        if (HasSpread) return CurrentSpreadLocation();
        if (spreadReflowLocation is { } pending && doublePage) return pending;
        var location = ReadAnchor();
        return new ReaderLocation(epub != null ? chapter : null, location.Offset, location.Fraction, ReadTextPagePosition());
    }

    private void UpdateJumpHistoryControls()
    {
        var show = returnLocation.HasValue && PageJumpPanel.Visibility != Visibility.Visible;
        ReaderJumpBackButton.Visibility = show && !returnLeadsToDestination ? Visibility.Visible : Visibility.Collapsed;
        ReaderJumpForwardButton.Visibility = show && returnLeadsToDestination ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool JumpCornerUnderPointer => ReaderJumpBackButton.IsMouseOver || ReaderJumpForwardButton.IsMouseOver;

    private void OnReaderSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (shownPanel != null) UpdatePanelPlacement();
        if (AppearancePopup?.IsOpen == true) UpdateAppearancePopupBounds();
        QueueSpreadUpdate();
    }

    private void UpdatePanelPlacement()
    {
        if (shownPanel == Panel.Search)
        {
            UpdateSearchPanelPlacement();
            return;
        }
        if (shownPanel == Panel.PdfPages)
        {
            PlacePdfNavigationPanel();
            return;
        }
        if (shownPanel == Panel.Contents)
        {
            PlaceContentsPanel();
            return;
        }
        if (shownPanel == Panel.Footnote)
        {
            PlaceFootnotePanel();
            return;
        }
        var wide = ReaderSurface.ActualWidth >= 780;
        ReaderPanel.VerticalAlignment = wide ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        ReaderPanel.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        ReaderPanel.Width = wide ? 304 : Math.Clamp(ReaderSurface.ActualWidth - 24, 0, 460);
        ReaderPanel.MaxHeight = wide ? double.PositiveInfinity : Math.Clamp(ReaderSurface.ActualHeight - 116, 160, 360);
        ReaderPanel.Margin = wide ? new Thickness(0, 56, 12, 54) : new Thickness(0, 0, 0, 54);
    }

    private void UpdateFlowButton()
    {
        FlowButton.Header = continuous ? "切换到左右翻页" : "切换到上下滚动";
        FlowButton.ToolTip = continuous ? "当前为上下滚动" : "当前为左右翻页";
        AutomationProperties.SetName(FlowButton, (string)FlowButton.Header);
        LeftPageEdge.Visibility = RightPageEdge.Visibility = continuous ? Visibility.Collapsed : Visibility.Visible;

        var reverse = ReverseHorizontalPageTurns;
        PositionSlider.IsDirectionReversed = reverse;
        Grid.SetColumn(PreviousButton, reverse ? 3 : 0);
        Grid.SetColumn(NextButton, reverse ? 0 : 3);
        PreviousButton.Content = reverse ? "›" : "‹";
        NextButton.Content = reverse ? "‹" : "›";
    }

    private void OnTextViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTextInsets();
    }

    private void RefreshBookmarkWhenShown()
    {
        if (book is { } open && readerBookmarksBookId != open.Id)
        {
            readerBookmarksBookId = open.Id;
            readerBookmarkPositions.Clear();
            _ = ReloadBookmarksAsync();
        }
        UpdateBookmarkButton();
    }

    private void UpdateBookmarkButton()
    {
        if (!controlsVisible || book == null || closing) return;
        var marked = HasBookmarkOnActivePage();
        var content = marked ? "已加书签" : "书签";
        if (Equals(BookmarkButton.Content, content)) return;
        BookmarkButton.Content = content;
        BookmarkButton.Style = (Style)FindResource(marked ? "ReaderActiveAction" : "ReaderAction");
        AutomationProperties.SetName(BookmarkButton, marked ? "移除当前位置的书签" : "添加当前位置为书签");
    }

    private async Task FocusOpenedBookmarksAsync()
    {
        var open = book;
        var priorFocus = Keyboard.FocusedElement;
        await ReloadBookmarksAsync();
        await Dispatcher.InvokeAsync(() =>
        {
            if (closing || !ReferenceEquals(book, open) || shownPanel != Panel.Bookmarks ||
                Keyboard.FocusedElement != null && !ReferenceEquals(Keyboard.FocusedElement, priorFocus) &&
                !BookmarksList.IsKeyboardFocusWithin) return;
            if (BookmarksList.SelectedIndex < 0 && BookmarksList.HasItems) BookmarksList.SelectedIndex = 0;
            BookmarksList.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async void OnToggleBookmark(object sender, RoutedEventArgs e) => await ToggleCurrentBookmarkAsync();

    private Task ToggleCurrentBookmarkAsync(bool announce = false) => ChangeCurrentBookmarkAsync(announce, addOnly: false);

    private Task ChangeCurrentBookmarkAsync(bool announce, bool addOnly)
    {
        if (book is not { } open || closing || moving || turningPage || seekingPosition || !BookmarkButton.IsEnabled)
            return Task.CompletedTask;
        var selection = CaptureBookmarkPage(ReadPosition());
        var position = NewBookmarkPosition(selection);
        BookmarkButton.IsEnabled = false;
        return bookmarkWrite = WriteCurrentBookmarkAsync(open, selection, position, announce, addOnly);
    }

    private async Task WriteCurrentBookmarkAsync(BookEntry open, BookmarkPageSelection selection, string position, bool announce, bool addOnly)
    {
        try
        {
            BookmarkEntry? existing = null;
            if (selection.Location != null)
            {
                var entries = await store.ListBookmarksAsync(open.Id);
                existing = await FindMatchingBookmarkAsync(entries, selection);
            }

            bool marked;
            if (addOnly)
            {
                if (existing == null) await store.AddBookmarkAsync(open.Id, position);
                marked = true;
            }
            else if (existing != null)
            {
                await store.RemoveBookmarkAsync(open.Id, existing.Id);
                marked = false;
            }
            else marked = await store.ToggleBookmarkAsync(open.Id, position);

            if (closing || !ReferenceEquals(book, open)) return;
            if (announce) _ = AnnounceKeyboardBookmarkAsync(open, marked);
            await ReloadBookmarksAsync();
        }
        catch (Exception error) { if (!closing && ReferenceEquals(book, open)) ShowStatus(error.Message); }
        finally { BookmarkButton.IsEnabled = true; }
    }

    private async Task AnnounceKeyboardBookmarkAsync(BookEntry open, bool marked)
    {
        if (StatusText.Text.Length != 0 && StatusText.Text != keyboardBookmarkNotice) return;
        var version = ++keyboardBookmarkNoticeVersion;
        var message = marked ? "已添加书签" : "已移除书签";
        keyboardBookmarkNotice = message;
        StatusText.Text = message;
        StatusSurface.Visibility = Visibility.Visible;
        await Task.Delay(1400);
        if (keyboardBookmarkNoticeVersion != version) return;
        keyboardBookmarkNotice = null;
        if (closing || !ReferenceEquals(book, open) || StatusText.Text != message) return;
        StatusText.Text = "";
        StatusSurface.Visibility = Visibility.Collapsed;
    }
}
