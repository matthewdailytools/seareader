using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow : Window
{
    private const double DefaultReaderFontSize = 18;
    private sealed record TextLocation(string? Encoding, int Offset, double LineFraction = 0, int? Chapter = null, int? End = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TextPageStart? TextPosition = null);
    private sealed record ReaderAppearance(string Theme = "system", string Font = "book", double Size = DefaultReaderFontSize, double LineHeight = 1.8,
        double Margin = 32, bool AnimatePageTurns = true, string LineGuide = "off", string Layout = "auto",
        string Alignment = "left", bool Bold = false, bool Hyphenation = false, string? PageTurnStyle = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReaderTypography? CustomTypography = null);
    private sealed record NoteSelection(string Position, string Quotation);
    private enum Panel { Search, Encoding, Contents, PdfPages, Bookmarks, Notes, Footnote }
    private static readonly JsonSerializerOptions PositionJson = new(JsonSerializerDefaults.Web);
    private const int TextWindowSize = 128;
    private const int TextWindowOverlap = 24;
    private const int LongLineWindowSize = 48;
    private const int MaximumTextWindowSize = 768;

    private readonly LibraryStore store;
    private readonly string[] startupPaths;
    private readonly SemaphoreSlim importGate = new(1);
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer hoverTimer;
    private readonly DispatcherTimer tapTimer;
    private readonly DispatcherTimer pdfResizeTimer;
    private readonly DispatcherTimer appearanceTimer;
    private Task appearanceWrite = Task.CompletedTask;
    private Task positionWrite = Task.CompletedTask;
    private IReadOnlyList<BookEntry> books = [];
    private BookEntry? book;
    private string? bookPath;
    private TextDocument? text;
    private IChapterBook? epub;
    private bool UsesRightToLeftPageProgression => epub is EpubDocument { RightToLeftPageProgression: true };
    private IPageBook? pdf;
    private CancellationTokenSource? reading;
    private CancellationTokenSource? pageRender;
    private CancellationTokenSource? pdfTextReading;
    private Task? pdfTextLoad;
    private PdfTextHit? pdfMatch;
    private ScrollViewer? textScroll;
    private readonly List<(Paragraph Paragraph, TextLine Line)> visibleRows = [];
    private Paragraph? chapterHeading;
    private TextDocument? windowDocument;
    private int windowStart;
    private int windowEnd;
    private int windowOverlap = TextWindowOverlap;
    private bool rewindowQueued;
    private (string Id, string Position, int? Percent)? pendingPosition;
    private (int Offset, double Fraction) anchor;
    private NoteSelection? noteSelection;
    private ReaderAppearance appearance = new();
    private Panel? shownPanel;
    private Point? tapStart;
    private Point? lastPointer;
    private bool controlsVisible;
    private bool hoverVisible;
    private bool populating;
    private bool moving;
    private bool closing;
    private bool closeReady;
    private SubscriptionWindow? subscriptionsWindow;
    private bool windowReady;
    private Task? readerAppearanceLoad;
    private bool continuous;
    private int chapter;
    private int chapterLoadVersion;
    private int pdfPage;
    private string pdfZoom = "width";
    private ReaderLocation? returnLocation;

    public MainWindow(LibraryStore store, IEnumerable<string> startupPaths)
    {
        this.store = store;
        this.startupPaths = startupPaths.ToArray();
        populating = true;
        InitializeComponent();
        InitializeSelectionTools();
        InitializeInlineSelectionTools();
        InitializeReadingGoals();
        TextList.AddHandler(Hyperlink.ClickEvent, new RoutedEventHandler(OnReaderReference));
        FacingTextList.AddHandler(Hyperlink.ClickEvent, new RoutedEventHandler(OnReaderReference));
        SourceInitialized += OnReaderGestureSourceInitialized;
        SourceInitialized += OnSystemThemeInitialized;
        foreach (var layer in new[] { PdfSelection, PdfFacingSelection })
        {
            layer.SelectionChanged += OnPdfSelectionChanged;
            layer.GotKeyboardFocus += OnPdfLeafFocused;
            layer.PreviewMouseLeftButtonDown += OnPdfLeafPressed;
            layer.PreviewMouseRightButtonDown += OnPdfLeafPressed;
        }
        InitializeFixedEpubLinks();
        Resources["ReaderFont"] = new FontFamily(ReaderFontSource(appearance.Font));
        Resources["ReaderSize"] = appearance.Size;
        saveTimer = CreateTimer(450, async () => await FlushPositionAsync());
        hoverTimer = CreateTimer(1800, HideHoverControls);
        tapTimer = CreateTimer(ReaderTapDelayMilliseconds, CompleteReaderTap);
        pdfResizeTimer = CreateTimer(150, async () => await ResizePdfAsync());
        appearanceTimer = CreateTimer(350, async () => await SaveAppearanceAsync());
        UpdateAppearanceControls();
        populating = false;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private DispatcherTimer CreateTimer(int milliseconds, Action callback)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) => { timer.Stop(); callback(); };
        return timer;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (windowReady) return;
        windowReady = true;
        _ = LoadReadingGoalsSettingsAsync();
        if (isDedicatedReader) return;
        try
        {
            await EnsureReaderAppearanceAsync();
            await ReloadLibraryAsync();
            if (startupPaths.Length != 0) await OpenFilesAsync(startupPaths);
        }
        catch (Exception error) { ShowStatus(error.Message); }
    }

    private Task EnsureReaderAppearanceAsync() => readerAppearanceLoad ??= LoadReaderAppearanceAsync();

    private async Task LoadReaderAppearanceAsync()
    {
        var saved = await store.GetPreferenceAsync("reading-appearance");
        if (saved != null)
        {
            try { appearance = JsonSerializer.Deserialize<ReaderAppearance>(saved, PositionJson) ?? new(); }
            catch (JsonException) { appearance = new(); }
        }
        var turnStyle = ReadPageTurnStyle(appearance);
        var layout = await LoadReaderLayoutPreferenceAsync(appearance.Layout);
        appearance = appearance with
        {
            Layout = layout,
            PageTurnStyle = turnStyle,
            AnimatePageTurns = turnStyle != "none"
        };
        ApplyReaderTheme();
        ApplyAppearance();
        UpdateAppearanceControls();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closeReady) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        var windowPlacementWrite = SaveReaderWindowPlacementAsync();
        libraryDataExport?.Cancel();
        libraryDataRestore?.Cancel();
        readerShare?.Dispose();
        var goalsWrite = FinishReadingGoalsAsync();
        StopLibraryPreviews();
        EndFootnoteSession();
        CancelBookmarkPreviews();
        ResetPdfThumbnails();
        ResetContents();
        ClearSavedReaderMarks();
        ClearLineGuide();
        hoverTimer.Stop();
        tapTimer.Stop();
        pdfResizeTimer.Stop();
        ResetReaderGestures();
        ResetPageTransition();
        ResetTextPageNumbers();
        reading?.Cancel();
        CancelReaderSearch();
        pageRender?.Cancel();
        SaveCurrentPosition();
        await FlushPositionAsync();
        await FlushAppearanceAsync();
        await readerLayoutWrite;
        await pdfViewWrite;
        await libraryPreferenceWrite;
        await libraryManualOrderWrite;
        await bookmarkWrite;
        await annotationWrite;
        await pageIndexCacheWrite;
        await libraryDataExportTask;
        await libraryDataRestoreTask;
        await goalsWrite;
        await windowPlacementWrite;
        fixedEpubRenderer?.Dispose();
        _ = Dispatcher.InvokeAsync(() =>
        {
            closeReady = true;
            Close();
        });
    }

    public Task OpenFilesAsync(IEnumerable<string> paths) => ImportFilesAsync(paths.ToArray(), openLast: true);

    private Task ImportFromLibraryAsync(string[] files) => ImportFilesAsync(files, openLast: files.Length == 1);

    private async Task ImportFilesAsync(string[] files, bool openLast)
    {
        if (files.Length == 0 || closing) return;
        if (isDedicatedReader && readingWindows is { } manager)
        {
            await manager.EnsureLibrary().ImportFilesAsync(files, openLast);
            return;
        }
        await importGate.WaitAsync();
        try
        {
            if (closing) return;
            ShowStatus("正在导入…");
            long? collection = LibrarySurface.IsVisible ? ActiveLibraryCollection?.Id : null;
            BookEntry? completed = null;
            var importedIds = new HashSet<string>(StringComparer.Ordinal);
            var messages = new List<string>();
            foreach (var path in files)
            {
                try
                {
                    var imported = await store.ImportAsync(path);
                    if (collection is { } id) await store.SetCollectionMembershipAsync(id, imported.Id, true);
                    importedIds.Add(imported.Id);
                    completed = imported;
                }
                catch (Exception error) { messages.Add(Path.GetFileName(path) + "：" + error.Message); }
            }
            if (completed != null && openLast)
            {
                ShowStatus("");
                await OpenLibraryBookAsync(completed);
            }
            if (LibrarySurface.IsVisible) await ReloadLibraryAsync();
            if (!openLast && importedIds.Count > 0) messages.Insert(0, $"{importedIds.Count} 本书已在书库中。");
            if (messages.Count != 0) ShowStatus(string.Join(Environment.NewLine, messages));
        }
        catch (Exception error) { ShowStatus(error.Message); }
        finally { importGate.Release(); }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (TryHandleLibraryBookDragOver(e)) return;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (await TryHandleLibraryBookDropAsync(e)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            if (LibrarySurface.IsVisible) await ImportFromLibraryAsync(paths);
            else await OpenFilesAsync(paths);
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "导入文件", Multiselect = true,
            Filter = BookFormats.FileFilter
        };
        if (picker.ShowDialog(this) == true) await ImportFromLibraryAsync(picker.FileNames);
    }

    private async Task OpenBookAsync(BookEntry selected)
    {
        if (closing) return;
        StopLibraryPreviews();
        selected = selected with { Display = GetLibraryDisplay(selected) };
        SaveCurrentPosition();
        await FlushPositionAsync();
        if (closing) return;
        reading?.Cancel();
        reading?.Dispose();
        reading = new CancellationTokenSource();
        var token = reading.Token;
        pageRender?.Cancel();
        CancelReaderSearch();
        moving = true;
        readingGoalsOpeningBook = true;
        RefreshReadingGoalsTracking();
        ShowStatus("正在打开 " + selected.Display.Title + "…");
        try
        {
            await EnsureReaderAppearanceAsync();
            token.ThrowIfCancellationRequested();
            var path = await store.OpenAsync(selected.Id);
            var position = await store.GetPositionAsync(selected.Id);
            var saved = ParsePosition(position);
            var mode = await store.GetPreferenceAsync(selected.Format.ToLowerInvariant() + "-reading-mode");
            token.ThrowIfCancellationRequested();
            TextDocument? document = null;
            IChapterBook? epubFile = null;
            IPageBook? pdfFile = null;
            var startChapter = 0;
            var format = selected.Format.ToLowerInvariant();
            if (format == "txt")
                document = await TextDocument.LoadAsync(path, saved?.Encoding, token);
            else if (BookFormats.IsChapterBook(format))
            {
                epubFile = await BookFormats.ReadChaptersAsync(path, format, token);
                startChapter = epubFile.GetChapterForPosition(position);
                if (epubFile is EpubDocument { FixedLayout: { } fixedLayout } fixedPublication)
                    pdfFile = new FixedEpubPageBook(fixedPublication, fixedLayout,
                        fixedEpubRenderer ??= new FixedEpubRenderer(Path.Combine(store.DataDirectory, "fixed-epub-renderer")));
                else if (epubFile is EpubDocument legacyEpub && saved == null && position is not null && IsLegacyEpubPosition(position))
                {
                    var restored = await legacyEpub.ReadChapterWithLegacyPositionAsync(startChapter, position, token);
                    document = restored.Document;
                    if (restored.SourceOffset is int offset)
                        saved = new TextLocation(document.EncodingName, offset, Chapter: startChapter,
                            TextPosition: new TextPageStart(offset, 0, offset));
                }
                else if (position == null && epubFile is EpubDocument { ReadingStart: { } beginning } publicationStart)
                {
                    try
                    {
                        var initial = await publicationStart.ReadChapterAtAnchorAsync(beginning.Chapter, beginning.Fragment, token);
                        document = initial.Document;
                        startChapter = beginning.Chapter;
                        if (initial.SourceOffset is int offset)
                            saved = new TextLocation(document.EncodingName, offset, Chapter: startChapter,
                                TextPosition: new TextPageStart(offset, 0, offset));
                    }
                    catch (Exception error) when (beginning.Chapter != startChapter && error is (IOException or InvalidDataException))
                    {
                        document = await BookFormats.ReadChapterDocumentAsync(epubFile, startChapter, token);
                    }
                }
                else document = await BookFormats.ReadChapterDocumentAsync(epubFile, startChapter, token);
            }
            else if (format == "pdf") pdfFile = await PdfBook.OpenAsync(path, token);
            else throw new InvalidDataException("无法打开此格式的电子书。");
            var pdfView = pdfFile != null ? await LoadPdfViewAsync(pdfFile) : pdfZoom;
            token.ThrowIfCancellationRequested();

            ResetPdfThumbnails();
            ResetContents();
            ResetBookGlossary();
            ResetFullScreenLocations();
            ClearLineGuide();
            if (epubFile is EpubDocument publication && (publication.PublicationTitle.Length > 0 || publication.PublicationAuthor.Length > 0))
                selected.Display.Apply(new LibraryBookMetadata(publication.PublicationTitle, publication.PublicationAuthor, false, CoverChecked: false));
            book = selected;
            bookPath = path;
            text = document;
            ResetSpreadForBook();
            ResetTextPageNumbers();
            chapterReadingStarts = null;
            epub = epubFile;
            pdf = pdfFile;
            pdfZoom = pdfView;
            chapter = startChapter;
            pdfPage = pdf == null ? 0 : Math.Clamp(int.TryParse(position, out var page) ? page - 1 : startChapter, 0, pdf.PageCount - 1);
            ResetPdfSelection();
            continuous = mode == "scroll";
            ResetReaderGestures();
            ResetPageTransition();
            returnLocation = null;
            returnLeadsToDestination = false;
            UpdateJumpHistoryControls();
            ReaderTitle.Text = selected.Display.Title;
            ResetReaderSearch();
            BookmarksList.ItemsSource = null;
            readerBookmarksBookId = null;
            readerBookmarkPositions.Clear();
            ResetReaderAnnotations();
            LibrarySurface.Visibility = Visibility.Collapsed;
            ReaderLoadingSurface.Visibility = Visibility.Collapsed;
            ReaderSurface.Visibility = Visibility.Visible;
            ConfigureSpreadLayout(CanUseSpread(((FrameworkElement)Content).ActualWidth));
            ClosePanel(false);
            SetControls(false);
            UpdateReaderControls();
            ShowStatus("");
            _ = ReloadNotesAsync();
            if (text != null)
            {
                var sourcePosition = saved?.TextPosition;
                MoveToText(saved?.Offset ?? 0, saved?.LineFraction ?? 0,
                    page: sourcePosition, preserveSource: sourcePosition != null);
                if (!continuous && sourcePosition is { SourceOffset: >= 0 } source)
                    RetainNavigationTextLocation(new ReaderLocation(epub == null ? null : chapter, source.Offset, source.Fraction, source));
                TextList.Focus();
            }
            else
            {
                ClearTextWindow();
                PdfPageImage.Source = null;
                PdfSurface.UpdateLayout();
                if (await RenderPdfAsync()) SetProgress();
                if (!token.IsCancellationRequested) ActivePdfSelection.Focus();
            }
            moving = false;
            QueueSpreadUpdate();
            SaveCurrentPosition();
            if (!token.IsCancellationRequested && (text != null || PdfPageImage.Source != null))
                await RestoreContinueEntryAsync(selected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested) ShowStatus(error.Message); }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                moving = false;
                readingGoalsOpeningBook = false;
                RefreshReadingGoalsTracking();
                if (LibrarySurface.IsVisible)
                {
                    FilterLibrary();
                    ResumeLibraryPreviews();
                }
            }
        }
    }

    private static TextLocation? ParsePosition(string? position, bool requireOffset = false)
    {
        if (position == null) return null;
        if (position.StartsWith("native-epub:", StringComparison.Ordinal)) position = position[12..];
        if (!position.StartsWith('{')) return null;
        try
        {
            if (!requireOffset) return JsonSerializer.Deserialize<TextLocation>(position, PositionJson);
            using var json = JsonDocument.Parse(position);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("offset", out var offset) &&
                   offset.ValueKind == JsonValueKind.Number ? root.Deserialize<TextLocation>(PositionJson) : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsLegacyEpubPosition(string position) => position.StartsWith("epubcfi(/", StringComparison.Ordinal);

    private void UpdateReaderControls()
    {
        TextList.Visibility = pdf == null ? Visibility.Visible : Visibility.Collapsed;
        PdfSurface.Visibility = pdf != null ? Visibility.Visible : Visibility.Collapsed;
        ReaderPaperBackdrop.Visibility = pdf == null ? Visibility.Visible : Visibility.Collapsed;
        if (pdf == null) Paper.SetResourceReference(BackgroundProperty, "ReaderPaper");
        else Paper.Background = Brushes.Transparent;
        TextAppearancePanel.Visibility = pdf == null ? Visibility.Visible : Visibility.Collapsed;
        PdfAppearancePanel.Visibility = pdf != null ? Visibility.Visible : Visibility.Collapsed;
        ContentsButton.Visibility = epub != null || pdf != null ? Visibility.Visible : Visibility.Collapsed;
        ContentsButton.Content = epub != null ? "目录" : "页码";
        ContentsButton.Tag = epub != null ? nameof(Panel.Contents) : nameof(Panel.PdfPages);
        ContentsButton.ToolTip = pdf is FixedEpubPageBook ? "打开目录（Ctrl+T）。Ctrl+Shift+T 浏览页面缩略图。" : epub != null
            ? "打开目录（Ctrl+T）。Alt+← / → 切换章节，Alt+↑ / ↓ 前往章首或章末。"
            : "输入页码（Ctrl+T），或直接浏览缩略图（Ctrl+Shift+T）。";
        AutomationProperties.SetAcceleratorKey(ContentsButton, "Ctrl+T");
        AppearanceButton.Content = pdf == null ? "Aa 排版" : "视图";
        System.Windows.Automation.AutomationProperties.SetName(ZoomPicker,
            pdf is FixedEpubPageBook ? "EPUB 显示方式" : "PDF 显示方式");
        UpdatePaperGeometry();
        EncodingMenu.Visibility = epub == null && pdf == null ? Visibility.Visible : Visibility.Collapsed;
        TurnMotionMenu.Visibility = pdf is PdfBook ? Visibility.Collapsed : Visibility.Visible;
        GlossaryMenuItem.Visibility = epub is EpubDocument && pdf == null ? Visibility.Visible : Visibility.Collapsed;
        FixedEpubPagesMenuItem.Visibility = pdf is FixedEpubPageBook ? Visibility.Visible : Visibility.Collapsed;
        UpdateFlowButton();
        populating = true;
        if (text != null) SelectTag(EncodingPicker, text.EncodingName);
        if (pdf != null) SelectTag(ZoomPicker, pdfZoom);
        populating = false;
        SetProgress();
        UpdateLineGuideActivation();
    }

    private async void OnBack(object sender, RoutedEventArgs e)
    {
        if (isDedicatedReader)
        {
            await ShowReadingLibraryAsync();
            return;
        }
        SaveCurrentPosition();
        await FlushPositionAsync();
        reading?.Cancel();
        ResetReaderSearch();
        ResetPdfThumbnails();
        ResetContents();
        ResetBookGlossary();
        pageRender?.Cancel();
        pdfResizeTimer.Stop();
        ResetReaderGestures();
        ResetPageTransition();
        book = null;
        ClearLineGuide();
        ResetReaderAnnotations();
        text = null;
        ResetSpreadForBook();
        ConfigureSpreadLayout(false);
        ResetTextPageNumbers();
        chapterReadingStarts = null;
        epub = null;
        pdf = null;
        ResetPdfSelection();
        ClearTextWindow();
        PdfPageImage.Source = null;
        ClosePanel(false);
        SetControls(false);
        ReaderSurface.Visibility = Visibility.Collapsed;
        LibrarySurface.Visibility = Visibility.Visible;
        LeaveReaderFullScreen();
        FocusLibraryAfterClosingReader();
        try { await ReloadLibraryAsync(); }
        catch (Exception error) { ShowStatus(error.Message); }
        finally { if (LibrarySurface.IsVisible) ResumeLibraryPreviews(); }
    }

    private void ShowStatus(string message)
    {
        SetLibraryStatus(message);
        StatusText.Text = message;
        StatusSurface.Visibility = message.Length != 0 && ReaderSurface.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static T? Descendant<T>(DependencyObject root) where T : class
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            var nested = Descendant<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private void FindTextViewport()
    {
        textScroll ??= Descendant<ScrollViewer>(TextList);
    }

    private (int Offset, double Fraction) ReadAnchor()
    {
        FindTextViewport();
        if (visibleRows.Count == 0 || textScroll == null || text == null) return anchor;
        var origin = TextWindowTop();
        if (!double.IsFinite(origin)) return anchor;
        var top = textScroll.VerticalOffset;
        var low = 0;
        var high = visibleRows.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (ParagraphTop(middle) - origin <= top + .1) low = middle;
            else high = middle - 1;
        }
        var start = ParagraphTop(low) - origin;
        var height = ParagraphHeight(low);
        return (visibleRows[low].Line.Offset, height > 0 ? Math.Clamp((top - start) / height, 0, 1) : 0);
    }

    private double ParagraphTop(int index) =>
        visibleRows[index].Paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;

    private double TextWindowTop() => chapterHeading?.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top ?? ParagraphTop(0);

    private double ParagraphHeight(int index) => index + 1 < visibleRows.Count
        ? ParagraphTop(index + 1) - ParagraphTop(index)
        : visibleRows[index].Paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Bottom - ParagraphTop(index);

    private void ClearTextWindow()
    {
        ResetTextPageEdgeClip();
        visibleRows.Clear();
        chapterHeading = null;
        TextList.Document = new FlowDocument();
        windowDocument = null;
    }

    private void SetTextWindow(int aroundIndex, bool coverPage = false)
    {
        if (text == null) return;
        windowDocument = text;
        var (capacity, overlap) = GetTextWindowLayout(aroundIndex, coverPage);
        windowOverlap = overlap;
        windowStart = Math.Clamp(aroundIndex - capacity / 2, 0, Math.Max(0, text.Lines.Count - capacity));
        var document = new FlowDocument { ColumnWidth = double.PositiveInfinity };
        chapterHeading = windowStart == 0 && epub != null
            ? TextPagination.CreateHeading(TextPagination.ChapterHeading(text, chapter, epub.Chapters[chapter].Title), appearance.Size)
            : null;
        if (chapterHeading != null) document.Blocks.Add(chapterHeading);
        windowEnd = Math.Min(windowStart + capacity, text.Lines.Count);
        visibleRows.Clear();
        var figureViewport = default(Size);
        if (text.HasIllustrations)
        {
            TextList.UpdateLayout();
            FindTextViewport();
            figureViewport = textScroll == null ? default : new Size(textScroll.ViewportWidth, textScroll.ViewportHeight);
            if (!EpubIllustrationLayout.CanFit(figureViewport)) figureViewport = new Size(240, 320);
        }
        for (var index = windowStart; index < windowEnd; index++)
        {
            var line = text.Lines[index];
            var paragraph = TextPagination.CreateBodyParagraph(text, line, appearance.Hyphenation, figureViewport, OnOpenIllustration);
            document.Blocks.Add(paragraph);
            visibleRows.Add((paragraph, line));
        }
        TextList.Document = document;
        ApplyDocumentAppearance();
        FindTextViewport();
        textScroll?.ScrollToTop();
        TextList.UpdateLayout();
        FindTextViewport();
    }

    private (int Capacity, int Overlap) GetTextWindowLayout(int aroundIndex, bool coverPage = false)
    {
        var normal = text!.Lines[aroundIndex].Length >= 512 ? LongLineWindowSize : TextWindowSize;
        var chapterSpace = epub == null ? 0 : 1;
        var height = Math.Max(ActualHeight, TextList.ActualHeight) - TextList.Padding.Top - TextList.Padding.Bottom;
        var lineHeight = Math.Clamp(appearance.Size, 12, 44) * Math.Clamp(appearance.LineHeight, 1.4, 2.2);
        // A tall window can show more than half the ordinary buffer. Keep one page either
        // side of the current anchor without growing the usual 128/48-row windows.
        var pageRows = (int)Math.Clamp(Math.Ceiling(height / lineHeight), 0, (MaximumTextWindowSize - 4) / 2);
        var reach = pageRows * 2 + 4;
        var capacity = (reach <= TextWindowSize && !coverPage ? normal : Math.Max(normal, reach)) - chapterSpace;
        var overlap = Math.Clamp(pageRows, Math.Min(TextWindowOverlap, capacity / 5), (capacity - 1) / 2);
        return (capacity, overlap);
    }

    private void MoveToText(int offset, double fraction = 0, bool recenter = false, TextPageStart? page = null, bool preserveSource = false)
    {
        if (text == null || text.Lines.Count == 0) return;
        DismissInlineSelection();
        if (doublePage && !updatingSpread && !recenter)
        {
            if (!preserveSource) spreadReflowLocation = null;
            if (HasSpread) DiscardSpread();
            else spreadRevision++;
        }
        var previous = moving;
        moving = true;
        try
        {
            if (page == null && !recenter && !continuous && textPagination is { } pages)
                page = IndexedLocation(pages, offset, fraction);
            if (page is { } start) offset = start.Offset;
            var index = text.FindLine(offset);
            if (recenter || !ReferenceEquals(windowDocument, text) || index < windowStart || index >= windowEnd ||
                page != null && windowEnd < text.Lines.Count && index >= windowEnd - windowOverlap)
                SetTextWindow(index, coverPage: page != null);
            TextList.UpdateLayout();
            FindTextViewport();
            if (textScroll != null && visibleRows.Count > 0)
            {
                if (text.HasIllustrations && EpubIllustrationLayout.Resize(TextList.Document,
                    new Size(textScroll.ViewportWidth, textScroll.ViewportHeight))) TextList.UpdateLayout();
                var localIndex = index - windowStart;
                var top = ParagraphTop(localIndex) - TextWindowTop();
                var height = ParagraphHeight(localIndex);
                var target = page is { } indexed ? TextPositionTop(indexed, localIndex) :
                    offset == 0 && fraction == 0 ? 0 : top + (double.IsFinite(height) ? height : 0) * Math.Clamp(fraction, 0, 1);
                if (double.IsFinite(target))
                {
                    target = Math.Max(0, target);
                    if (!continuous && offset != int.MaxValue && windowEnd == text.Lines.Count && target > textScroll.ScrollableHeight + .1)
                        ScrollToTextPage(textScroll, target);
                    else
                    {
                        textScroll.ScrollToVerticalOffset(target);
                        TextList.UpdateLayout();
                    }
                }
            }
            anchor = ReadAnchor();
            if (!updatingSpread) SetProgress();
        }
        finally { moving = previous; }
    }

    private void OnTextScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (moving || updatingSpread || text == null || pdf != null || textScroll != null && !ReferenceEquals(e.OriginalSource, textScroll)) return;
        if (Math.Abs(e.VerticalChange) < .01 && Math.Abs(e.ViewportHeightChange) < .01 &&
            Math.Abs(e.ViewportWidthChange) < .01) return;
        DismissInlineSelection();
        if (shownPanel == Panel.Footnote && Math.Abs(e.VerticalChange) > .1) ClosePanel(false);
        if (HasSpread)
        {
            if (Math.Abs(e.ViewportHeightChange) < .01 && Math.Abs(e.ViewportWidthChange) < .01) return;
            RememberSpreadPosition();
        }
        anchor = ReadAnchor();
        SetProgress();
        SaveCurrentPosition();
        if (!rewindowQueued && NeedsTextWindow(e.VerticalChange))
        {
            rewindowQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                rewindowQueued = false;
                if (moving || text == null || !ReferenceEquals(windowDocument, text)) return;
                var latest = ReadAnchor();
                if (NeedsTextWindow(e.VerticalChange)) MoveToText(latest.Offset, latest.Fraction, recenter: true);
            });
        }
    }

    private bool NeedsTextWindow(double direction)
    {
        if (text == null || textScroll == null) return false;
        var line = text.FindLine(anchor.Offset);
        var nextWindow = GetTextWindowLayout(line);
        return windowEnd - windowStart < Math.Min(text.Lines.Count, nextWindow.Capacity) || nextWindow.Overlap > windowOverlap ||
            direction < 0 && windowStart > 0 && line < windowStart + windowOverlap ||
            direction > 0 && windowEnd < text.Lines.Count &&
                (line >= windowEnd - windowOverlap || textScroll.VerticalOffset >= textScroll.ScrollableHeight - 1);
    }

    private string ReadPosition()
    {
        if (pdf != null) return (pdfPage + 1).ToString(CultureInfo.InvariantCulture);
        if (HasSpread)
        {
            var location = CurrentSpreadLocation();
            return SerializeTextPosition(location.Offset, location.Fraction, locationChapter: location.Chapter,
                textPosition: location.TextPosition is { Illustration: not null } ? location.TextPosition : null);
        }
        anchor = ReadAnchor();
        if (text?.HasIllustrations == true && ReadTextPagePosition() is { Illustration: not null } figure)
            return SerializeTextPosition(figure.Offset, figure.Fraction, textPosition: figure);
        return SerializeTextPosition(anchor.Offset, anchor.Fraction);
    }

    private string SerializeTextPosition(int offset, double fraction = 0, int? end = null, TextPageStart? textPosition = null,
        int? locationChapter = null)
    {
        var value = JsonSerializer.Serialize(new TextLocation(text?.EncodingName, offset, fraction,
            epub == null ? null : locationChapter ?? chapter, end, textPosition), PositionJson);
        return book?.Format == "epub" ? "native-epub:" + value : value;
    }

    private void SaveCurrentPosition()
    {
        if (book == null || text == null && pdf == null || moving) return;
        if (pdf != null) pendingPosition = (book.Id, ReadPosition(), PdfPageImage.Source != null
            ? ResumePercent(pdfPage + 1, pdf.PageCount) : null);
        else
        {
            var location = !continuous ? pageResizeLocation ?? ReadRetainedLayoutLocation() ?? CaptureReaderLocation()
                : CaptureReaderLocation();
            pendingPosition = (book.Id, SerializeTextPosition(location.Offset, location.Fraction,
                textPosition: location.TextPosition, locationChapter: location.Chapter), ReadTextResumePercent(location));
        }
        saveTimer.Stop();
        saveTimer.Start();
    }

    private Task FlushPositionAsync()
    {
        saveTimer.Stop();
        if (pendingPosition is not { } save) return positionWrite;
        pendingPosition = null;
        return positionWrite = SavePositionAfterAsync(positionWrite, save);
    }

    private async Task SavePositionAfterAsync(Task prior, (string Id, string Position, int? Percent) save)
    {
        await prior;
        try
        {
            await store.SavePositionAsync(save.Id, save.Position, save.Percent);
            readingWindows?.NotifyReaderPositionSaved();
        }
        catch (Exception error) { ShowStatus(error.Message); }
    }

    private async Task ChangeChapterAsync(int next, int offset = 0, double fraction = 0, TextPageStart? page = null,
        bool preserveSource = false, CancellationToken? requiredIndex = null)
    {
        if (epub == null || reading == null || next < 0 || next >= epub.Chapters.Count ||
            requiredIndex != null && !IsCurrentPageIndex(requiredIndex)) return;
        var request = ++chapterLoadVersion;
        if (next == chapter)
        {
            chapterLoadPending = null;
            MoveToText(offset, fraction, page: page, preserveSource: preserveSource);
            SaveCurrentPosition();
            return;
        }
        var current = epub;
        var token = reading.Token;
        chapterLoadPending = request;
        try
        {
            var document = await BookFormats.ReadChapterDocumentAsync(current, next, token);
            token.ThrowIfCancellationRequested();
            if (request != chapterLoadVersion || requiredIndex != null && !IsCurrentPageIndex(requiredIndex)) return;
            moving = true;
            text = document;
            chapter = next;
            MoveToText(offset, fraction, page: page, preserveSource: preserveSource);
            moving = false;
            SaveCurrentPosition();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested && request == chapterLoadVersion) ShowStatus(error.Message); }
        finally
        {
            if (!token.IsCancellationRequested && request == chapterLoadVersion) moving = false;
            if (chapterLoadPending == request)
            {
                chapterLoadPending = null;
                QueueSpreadUpdate();
            }
        }
    }

    internal BitmapSource? CurrentPdfImage => (ReferenceEquals(ActivePdfSelection, PdfSelection) ? PdfPageImage : PdfFacingImage).Source as BitmapSource;

    internal string SelectedReaderText() => ReadReaderSelection(pdf != null ? ActivePdfSelection : ActiveTextList)?.Text ?? "";

    private void SetControls(bool visible, bool hover = false, bool focus = false)
    {
        if (!visible)
        {
            AppearancePopup.IsOpen = false;
            ReaderToolsButton.ContextMenu.IsOpen = false;
            ClosePageJump();
            ClosePanel(false);
            AbortPositionPreview();
        }
        controlsVisible = visible;
        hoverVisible = visible && hover;
        ReaderToolbar.Visibility = ReaderHeader.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CompactReaderControls.Visibility = visible ? Visibility.Hidden : Visibility.Visible;
        if (visible) RefreshBookmarkWhenShown();
        if (hoverVisible) RestartHoverTimer();
        else hoverTimer.Stop();
        if (!focus) return;
        if (!visible) ShowControlsButton.Focus();
        else if (SearchButton.Visibility == Visibility.Visible) SearchButton.Focus();
        else AppearanceButton.Focus();
    }

    private void RestartHoverTimer()
    {
        hoverTimer.Stop();
        hoverTimer.Start();
    }

    private void HideHoverControls()
    {
        if (!hoverVisible) return;
        if (shownPanel != null || AppearancePopup.IsOpen || ReaderToolsButton.ContextMenu.IsOpen || ReaderToolbar.IsMouseOver || ReaderHeader.IsMouseOver ||
            ReaderToolbar.IsKeyboardFocusWithin || ReaderHeader.IsKeyboardFocusWithin ||
            ReaderSurface.IsMouseOver && IsReaderControlRevealEdge(Mouse.GetPosition(SpreadPageContent)))
        {
            RestartHoverTimer();
            return;
        }
        SetControls(false);
    }

    private void OnToggleControls(object sender, RoutedEventArgs e) => SetControls(!controlsVisible, focus: true);

    private bool InOverlay =>
        ReaderHeader.IsMouseOver || ReaderToolbar.IsMouseOver || ReaderPanel.IsMouseOver || CompactReaderControls.IsMouseOver ||
        SearchPanel.IsMouseOver || ContentsPanel.IsMouseOver || PdfNavigationPanel.IsMouseOver || FootnotePanel.IsMouseOver ||
        InlineSelectionCard.IsMouseOver || LeftPageEdge.IsMouseOver || RightPageEdge.IsMouseOver || JumpCornerUnderPointer;

    private void OnReaderPointerMove(object sender, MouseEventArgs e)
    {
        if (book == null) return;
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (pendingSavedMarkTap.HasValue && tapStart is { } start && ExceedsReaderDragDistance(e.GetPosition(ReaderSurface) - start))
                StopReaderTap();
            return;
        }
        var point = e.GetPosition(ReaderSurface);
        if (lastPointer == point) return;
        lastPointer = point;
        if (!IsReaderControlRevealEdge(e.GetPosition(SpreadPageContent)))
        {
            if (hoverVisible && InOverlay) RestartHoverTimer();
            return;
        }
        if (!controlsVisible) SetControls(true, hover: true);
        else if (hoverVisible) RestartHoverTimer();
    }

    private bool IsReaderControlRevealEdge(Point point) => !InlineSelectionCard.IsMouseOver && !LeftPageEdge.IsMouseOver &&
        !RightPageEdge.IsMouseOver && !JumpCornerUnderPointer &&
        IsReaderControlRevealEdge(point, SpreadPageContent.RenderSize);

    private static bool IsReaderControlRevealEdge(Point point, Size paper) => paper.Width > 0 && paper.Height > 0 &&
        point.X >= 0 && point.X <= paper.Width && Math.Min(point.Y, paper.Height - point.Y) is >= -14 and <= 26;

    private void OnReaderPointerLeave(object sender, MouseEventArgs e)
    {
        lastPointer = null;
        if (hoverVisible) RestartHoverTimer();
    }

    private static bool Within<T>(DependencyObject? item) where T : DependencyObject
    {
        while (item != null)
        {
            if (item is T) return true;
            item = item is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        return false;
    }

    private void OnReaderPointerDown(object sender, MouseButtonEventArgs e)
    {
        StopReaderTap();
        if (InlineSelectionCard.IsMouseOver) return;
        DismissInlineSelection();
        if (InOverlay) return;
        if (shownPanel == Panel.Footnote)
        {
            ClosePanel(false);
            return;
        }
        var source = e.OriginalSource as DependencyObject;
        if (e.ClickCount != 1 || Within<ScrollBar>(source) || Within<Hyperlink>(source) || Within<EpubIllustrationButton>(source)) return;
        if (source is PdfSelectionLayer layer && Keyboard.Modifiers == ModifierKeys.None &&
            layer.ReadFixedEpubLink(e.GetPosition(layer)) != null) return;
        pendingSavedMarkTap = ReadSavedMarkPress(e);
        var point = e.GetPosition(SpreadPageContent);
        var width = SpreadPageContent.ActualWidth;
        var height = SpreadPageContent.ActualHeight;
        if (pendingSavedMarkTap.HasValue || LineGuideBodyAt(e) != null || point.X >= width * .18 && point.X <= width * .82 &&
            point.Y >= height * .22 && point.Y <= height * .78) tapStart = e.GetPosition(ReaderSurface);
    }

    private void OnReaderPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (tapStart is not { } start) return;
        tapStart = null;
        if (ExceedsReaderDragDistance(e.GetPosition(ReaderSurface) - start) || InOverlay)
        {
            StopReaderTap();
            return;
        }
        if (pendingSavedMarkTap is { } mark) pendingSavedMarkTap = mark with { Point = e.GetPosition(mark.Body) };
        else if (LineGuideBodyAt(e) is { } body) pendingLineGuideClick = (body, e.GetPosition(body));
        tapTimer.Stop();
        tapTimer.Interval = TimeSpan.FromMilliseconds(pendingSavedMarkTap.HasValue
            ? Math.Max((uint)ReaderTapDelayMilliseconds, GetDoubleClickTime()) : ReaderTapDelayMilliseconds);
        tapTimer.Start();
    }

    private static bool ExceedsReaderDragDistance(Vector delta) => Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private void CompleteReaderTap()
    {
        var guideClick = pendingLineGuideClick;
        pendingLineGuideClick = null;
        if (pendingSavedMarkTap is { } mark)
        {
            pendingSavedMarkTap = null;
            OpenTappedSavedMark(mark);
            return;
        }
        if (book == null || (pdf == null ? !ActiveTextList.Selection.IsEmpty : ActivePdfSelection.SelectedBounds.Count != 0)) return;
        if (shownPanel != null) ClosePanel();
        else if (guideClick is { } target && MoveReaderLineGuideToPoint(target.Body, target.Point)) return;
        else SetControls(!controlsVisible);
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (readingWindows != null && modifiers == ModifierKeys.Control && e.Key == Key.W)
        {
            e.Handled = true;
            if (!e.IsRepeat) Close();
            return;
        }
        if (isDedicatedReader && modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            e.Handled = true;
            if (!e.IsRepeat) await ShowReadingLibraryAsync();
            return;
        }
        if (book == null)
        {
            if (modifiers == ModifierKeys.Control && e.Key == Key.F) { FocusLibrarySearch(); e.Handled = true; }
            else if (e.Key == Key.Enter && LibraryList.IsKeyboardFocusWithin &&
                     !Within<Button>(e.OriginalSource as DependencyObject) && LibraryList.SelectedItem is BookEntry selected)
            {
                e.Handled = true;
                await OpenLibraryBookAsync(selected);
            }
            return;
        }
        if (modifiers == ModifierKeys.None && e.Key == Key.F11)
        {
            e.Handled = true;
            if (!e.IsRepeat) await ToggleReaderFullScreenAsync();
            return;
        }
        if (e.Key == Key.Escape)
        {
            await HandleReaderEscapeAsync(e);
            return;
        }
        if (HandleReaderNavigationShortcut(e, modifiers)) return;
        if (GoToPageBox.IsKeyboardFocusWithin) return;
        if (HandleFontSizeShortcut(e, modifiers)) return;
        if (e.Key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift &&
            MoveReaderInlineFocus(e, modifiers == ModifierKeys.Shift))
        {
            e.Handled = true;
            return;
        }
        if (LineGuideActive && modifiers == ModifierKeys.None && ActiveTextList.IsKeyboardFocusWithin && e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            await MoveReaderLineAsync(e.Key == Key.Up ? -1 : 1);
            return;
        }
        if (modifiers == ModifierKeys.Control && e.Key == Key.G)
        {
            e.Handled = true;
            OnShowGoToPage(sender, e);
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            OpenPanel(Panel.Search);
        }
        else if (e.Key == Key.D && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) &&
                 !AppearancePopup.IsOpen && ReaderToolsButton.ContextMenu?.IsOpen != true && illustrationWindow == null &&
                 Keyboard.FocusedElement is not (TextBox { IsReadOnly: false } or RichTextBox { IsReadOnly: false }))
        {
            e.Handled = true;
            if (e.IsRepeat || closing || moving || turningPage || seekingPosition || previewingPosition) return;
            if (modifiers == ModifierKeys.Control) await ToggleCurrentBookmarkAsync(announce: true);
            else if (shownPanel == Panel.Bookmarks) ClosePanel();
            else ShowReaderPanel(Panel.Bookmarks, revealControls: false);
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            e.Handled = true;
            if (!e.IsRepeat)
            {
                if (shownPanel == Panel.Notes) ClosePanel();
                else OpenPanel(Panel.Notes);
            }
        }
        else if (modifiers == ModifierKeys.None && e.Key == Key.F &&
                 Keyboard.FocusedElement is not TextBox { IsReadOnly: false })
        {
            e.Handled = true;
            SetControls(!controlsVisible, focus: true);
        }
        else if (!AppearancePopup.IsOpen && ReaderToolsButton.ContextMenu?.IsOpen != true &&
                 !InlineSelectionCard.IsKeyboardFocusWithin && !ReaderPanel.IsKeyboardFocusWithin && !SearchPanel.IsKeyboardFocusWithin &&
                 !ContentsPanel.IsKeyboardFocusWithin && !PdfNavigationPanel.IsKeyboardFocusWithin &&
                 !FootnotePanel.IsKeyboardFocusWithin && !PageJumpPanel.IsKeyboardFocusWithin &&
                 !Within<Slider>(e.OriginalSource as DependencyObject))
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (ReverseHorizontalPageTurns)
                key = key switch { Key.Left => Key.Right, Key.Right => Key.Left, _ => key };
            if (modifiers == ModifierKeys.Alt && text != null && epub != null && key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                e.Handled = true;
                if (!e.IsRepeat) await SeekChapterBoundaryAsync(key);
            }
            else if (modifiers == ModifierKeys.None && (key is Key.PageDown or Key.PageUp || !continuous &&
                     (key is Key.Left or Key.Right || pdf == null && (key is Key.Up or Key.Down))))
            {
                e.Handled = true;
                await TurnAsync(key is Key.PageDown or Key.Right or Key.Down ? 1 : -1);
            }
        }
    }

    private FrameworkElement PanelContent(Panel panel) => panel switch
    {
        Panel.Search => SearchPanel,
        Panel.Encoding => EncodingPanel,
        Panel.Contents => ContentsPanel,
        Panel.PdfPages => PdfNavigationPanel,
        Panel.Bookmarks => BookmarksPanel,
        Panel.Footnote => FootnotePanel,
        _ => NotesPanel
    };

    private void OnPanelButton(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value } && Enum.TryParse<Panel>(value, out var panel))
        {
            if (shownPanel == panel) ClosePanel();
            else OpenPanel(panel);
        }
    }

    private void OpenPanel(Panel panel) => ShowReaderPanel(panel, revealControls: true);

    private void ShowReaderPanel(Panel panel, bool revealControls, bool focusThumbnail = false)
    {
        if (panel == Panel.PdfPages && pdf == null || panel == Panel.Contents && epub == null) return;
        DismissInlineSelection();
        AppearancePopup.IsOpen = false;
        if (revealControls && panel != Panel.Footnote) SetControls(true);
        if (shownPanel == Panel.Search && panel != Panel.Search) CancelReaderSearch();
        if (shownPanel == Panel.PdfPages && panel != Panel.PdfPages) SuspendPdfThumbnails();
        if (shownPanel == Panel.Contents && panel != Panel.Contents) EndContentsSession();
        if (shownPanel == Panel.Footnote && panel != Panel.Footnote) EndFootnoteSession();
        if (shownPanel == Panel.Bookmarks && panel != Panel.Bookmarks) CancelBookmarkPreviews();
        if (shownPanel is { } visible) PanelContent(visible).Visibility = Visibility.Collapsed;
        shownPanel = panel;
        PanelContent(panel).Visibility = Visibility.Visible;
        ReaderPanel.Visibility = panel is Panel.Search or Panel.PdfPages or Panel.Contents or Panel.Footnote
            ? Visibility.Collapsed : Visibility.Visible;
        PanelTitle.Text = panel switch
        {
            Panel.Search => "书内查找",
            Panel.Encoding => "文本编码", Panel.Contents => "目录", Panel.PdfPages => "页码",
            Panel.Bookmarks => "书签", _ => "标注与笔记"
        };
        UpdatePanelPlacement();
        switch (panel)
        {
            case Panel.Search:
                ReaderHeader.UpdateLayout();
                UpdateSearchPanelPlacement();
                ReaderSearch.Focus();
                ReaderSearch.SelectAll();
                break;
            case Panel.Contents:
                ReaderHeader.UpdateLayout();
                PlaceContentsPanel();
                DisplayContents();
                break;
            case Panel.PdfPages:
                ReaderHeader.UpdateLayout();
                PlacePdfNavigationPanel();
                SetPdfJumpError("");
                JumpInput.Text = (pdfPage + 1).ToString(CultureInfo.InvariantCulture);
                DisplayPdfThumbnails();
                if (focusThumbnail) FocusSelectedPdfThumbnail();
                else
                {
                    JumpInput.Focus();
                    JumpInput.SelectAll();
                }
                break;
            case Panel.Bookmarks:
                _ = FocusOpenedBookmarksAsync();
                break;
            case Panel.Notes:
                _ = ReloadNotesAsync();
                ShowNoteSelection();
                FocusOpenedNotesFilter();
                break;
            case Panel.Footnote:
                FootnoteBody.Focus();
                break;
        }
    }

    private void ClosePanel(bool focus = true) => CloseReaderPanel(focus, cancelSearch: true);

    private void CloseReaderPanel(bool focus, bool cancelSearch)
    {
        if (shownPanel is not { } visible) return;
        var restoreFootnote = visible == Panel.Footnote ? footnoteAnchor : null;
        var restoreFixedFootnote = visible == Panel.Footnote ? fixedFootnoteAnchor : null;
        if (visible == Panel.Search && cancelSearch) CancelReaderSearch();
        if (visible == Panel.PdfPages) SuspendPdfThumbnails();
        if (visible == Panel.Contents) EndContentsSession();
        if (visible == Panel.Footnote) EndFootnoteSession();
        if (visible == Panel.Bookmarks) CancelBookmarkPreviews();
        if (visible == Panel.Notes) annotationJumpVersion++;
        PanelContent(visible).Visibility = Visibility.Collapsed;
        shownPanel = null;
        ReaderPanel.Visibility = Visibility.Collapsed;
        UpdatePanelPlacement();
        if (focus && !(restoreFootnote is { } anchor && ReadFootnoteAnchor(anchor).HasValue && anchor.Link.Focus()) &&
            !(restoreFixedFootnote is { } fixedAnchor && FocusFixedFootnoteSource(fixedAnchor)))
            FocusReadingSurface();
        if (visible == Panel.Bookmarks) UpdateBookmarkButton();
    }

    private void OnClosePanel(object sender, RoutedEventArgs e) => ClosePanel();

    private async Task JumpToPageAsync()
    {
        if (pdf is not { } document) return;
        if (!int.TryParse(JumpInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var target) ||
            target < 1 || target > document.PageCount)
        {
            JumpInput.Focus();
            JumpInput.SelectAll();
            SetPdfJumpError($"请输入 1 到 {document.PageCount} 之间的页码。");
            return;
        }
        await SelectPdfNavigationPageAsync(target - 1);
    }

    private async void OnJump(object sender, RoutedEventArgs e) => await JumpToPageAsync();
    private async void OnJumpKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await JumpToPageAsync(); }
        else if (e.Key == Key.Down && PdfThumbnailList.SelectedItem != null)
        {
            e.Handled = true;
            FocusSelectedPdfThumbnail();
        }
    }

    private async void OnReturnPosition(object sender, RoutedEventArgs e)
    {
        if (book is not { } open || closing || moving || turningPage || seekingPosition || previewingPosition ||
            returnLocation is not { } saved) return;
        var session = reading;
        var origin = CaptureNavigationLocation();
        var previousView = CaptureJumpView();
        var restored = false;
        pendingTurns.Clear();
        ForgetLayoutLocation();
        ResetPageTransition();
        ClosePanel(false);
        ClosePageJump();
        seekingPosition = true;
        SetProgress();
        try
        {
            if (!await RestoreNavigationLocationAsync(saved) || !ReferenceEquals(book, open) || !ReferenceEquals(reading, session)) return;
            restored = true;
            returnLocation = origin;
            returnLeadsToDestination = !returnLeadsToDestination;
            UpdateJumpHistoryControls();
            FocusReadingSurface();
        }
        catch (Exception error)
        {
            if (ReferenceEquals(book, open) && ReferenceEquals(reading, session)) ShowStatus(error.Message);
        }
        finally
        {
            seekingPosition = false;
            if (ReferenceEquals(book, open) && ReferenceEquals(reading, session))
            {
                if (!restored) RetainFailedJumpLocation(origin, previousView);
                SetProgress();
            }
        }
    }

    private void UpdateAppearanceControls()
    {
        var prior = populating;
        populating = true;
        FontSizeSlider.Value = Math.Clamp(appearance.Size, 12, 44);
        ParagraphSlider.Value = Math.Clamp(appearance.LineHeight, 1.4, 2.2);
        MarginSlider.Value = Math.Clamp(appearance.Margin, 12, 52);
        FontSizeLabel.Text = FontSizeSlider.Value.ToString("0", CultureInfo.CurrentCulture);
        UpdateThemeButton();
        UpdatePageTurnEffectMenu();
        SelectTag(FontPicker, appearance.Font);
        SelectTag(LineGuidePicker, appearance.LineGuide);
        SelectCurrentReaderLayout();
        SelectTag(ParagraphAlignmentPicker, appearance.Alignment);
        ReaderBoldToggle.IsChecked = appearance.Bold;
        ReaderHyphenationCheckBox.IsChecked = appearance.Hyphenation;
        SelectTag(ZoomPicker, pdfZoom);
        UpdateTypographyPresets();
        populating = prior;
    }

    private static void SelectTag(ComboBox box, string value) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == value) ??
            box.Items.OfType<ComboBoxItem>().FirstOrDefault();

    private void ApplyAppearance()
    {
        Resources["ReaderFont"] = new FontFamily(ReaderFontSource(appearance.Font));
        Resources["ReaderSize"] = Math.Clamp(appearance.Size, 12, 44);
        ApplyDocumentAppearance();
    }

    private static string ReaderFontSource(string name) => name switch
    {
        "book" => "Cambria, SimSun, Microsoft YaHei UI",
        "serif" => "SimSun, Georgia",
        "Georgia" => "Georgia, SimSun",
        _ => "Segoe UI, Microsoft YaHei UI"
    };

    private void ApplyDocumentAppearance()
    {
        TextList.Document.PagePadding = TextPagination.DocumentPadding;
        TextPagination.ApplyTypography(TextList.Document, Math.Clamp(appearance.Size, 12, 44),
            appearance.Size * Math.Clamp(appearance.LineHeight, 1.4, 2.2), appearance.Bold,
            appearance.Alignment == "justify" ? TextAlignment.Justify : TextAlignment.Left);
        UpdateTextInsets();
        if (chapterHeading != null) chapterHeading.FontSize = Math.Clamp(appearance.Size * 1.22, 16, 53);
        foreach (var (paragraph, _) in visibleRows) paragraph.Margin = new Thickness(0);
    }

    private void ChangeAppearance(ReaderAppearance value, bool preserveCustomTypography = false)
    {
        if (populating || value == appearance) return;
        if (!preserveCustomTypography) value = TrackCustomTypography(value);
        var hyphenationChanged = appearance.Hyphenation != value.Hyphenation;
        var sizeChanged = appearance.Size != value.Size;
        var reflow = hyphenationChanged || appearance.Font != value.Font || sizeChanged ||
            appearance.LineHeight != value.LineHeight || appearance.Margin != value.Margin ||
            appearance.Bold != value.Bold || appearance.Alignment != value.Alignment;
        if (!reflow)
        {
            appearance = value;
            UpdateTypographyPresets();
            QueueSpreadUpdate();
            QueueAppearanceSave();
            return;
        }

        ResetPageTransition();
        ReaderLocation? location = text == null ? null : continuous
            ? CaptureGuidedLocation() ?? CaptureReaderLocation() : CaptureLayoutLocation();
        if (!continuous && location is { } paged)
        {
            // Keep the same source through successive changes, even before a new index is available.
            if (doublePage) spreadReflowLocation = paged;
            else pageResizeLocation = paged;
        }
        var visiblePosition = location?.Chapter == (epub == null ? null : chapter) ? location?.TextPosition : null;
        if (visiblePosition == null && text != null) visiblePosition = ReadTextPagePosition();
        var saved = ReadAnchor();
        var wasMoving = moving;
        moving = true;
        try
        {
            appearance = value;
            if (hyphenationChanged && text != null) windowDocument = null;
            ResetTextPageNumbers();
            ApplyAppearance();
            TextList.UpdateLayout();
            MoveToText(visiblePosition?.Offset ?? saved.Offset, saved.Fraction, page: visiblePosition,
                preserveSource: visiblePosition != null);
        }
        finally { moving = wasMoving; }
        UpdateTypographyPresets();
        SaveCurrentPosition();
        QueueAppearanceSave();
    }

    private Task FlushAppearanceAsync()
    {
        if (!appearanceTimer.IsEnabled) return appearanceWrite;
        appearanceTimer.Stop();
        return SaveAppearanceAsync();
    }

    private Task SaveAppearanceAsync() => appearanceWrite = SaveAppearanceAfterAsync(appearanceWrite, appearance);

    private async Task SaveAppearanceAfterAsync(Task prior, ReaderAppearance snapshot)
    {
        await prior;
        try { await store.SavePreferenceAsync("reading-appearance", JsonSerializer.Serialize(snapshot, PositionJson)); }
        catch (Exception error) { if (!closing) ShowStatus(error.Message); }
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (populating || FontSizeLabel == null) return;
        FontSizeLabel.Text = e.NewValue.ToString("0", CultureInfo.CurrentCulture);
        ChangeAppearance(appearance with { Size = e.NewValue });
    }
    private void OnParagraphSpacingChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ChangeAppearance(appearance with { LineHeight = e.NewValue });
    private void OnMarginChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ChangeAppearance(appearance with { Margin = e.NewValue });
    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontPicker?.SelectedItem is ComboBoxItem { Tag: string value }) ChangeAppearance(appearance with { Font = value });
    }
    private async void OnZoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!populating && pdf != null && ZoomPicker?.SelectedItem is ComboBoxItem { Tag: string value } && value != pdfZoom)
        {
            DismissInlineSelection();
            ResetPageTransition();
            pdfZoom = value;
            pdfViewWrite = SavePdfViewAfterAsync(pdfViewWrite, pdf is FixedEpubPageBook ? FixedEpubViewPreference : PdfViewPreference, value);
            UpdatePaperGeometry();
            PaperFrame.UpdateLayout();
            if (await RenderPdfAsync()) SetProgress();
        }
    }

    private async void OnFlowMode(object sender, RoutedEventArgs e)
    {
        if (book is not { } open || !FlowButton.IsEnabled || moving || turningPage || seekingPosition) return;
        var location = continuous ? CaptureGuidedLocation() ?? CaptureReaderLocation() : CaptureLayoutLocation();
        ForgetLayoutLocation();
        FlowButton.IsEnabled = false;
        seekingPosition = true;
        continuous = !continuous;
        ResetReaderGestures();
        if (continuous)
        {
            ResetPageTransition();
            if (text != null) TextList.Document.PagePadding = TextPagination.DocumentPadding;
        }
        UpdateFlowButton();
        ClosePanel(false);
        UpdatePaperGeometry();
        UpdateTextInsets();
        TextList.UpdateLayout();
        try
        {
            if (pdf != null)
            {
                PaperFrame.UpdateLayout();
                await RestoreReaderLocationAsync(location);
            }
            else if (doublePage != WantsSpread) await ApplySpreadLayoutAsync(location);
            else if (text != null)
            {
                if (!continuous) pageResizeLocation = location;
                await RestoreReaderLocationAsync(location);
            }
            if (!ReferenceEquals(book, open)) return;
            FocusReadingSurface();
            SaveCurrentPosition();
            await store.SavePreferenceAsync(open.Format.ToLowerInvariant() + "-reading-mode", continuous ? "scroll" : "page");
        }
        catch (Exception error) { ShowStatus(error.Message); }
        finally
        {
            seekingPosition = false;
            FlowButton.IsEnabled = true;
            if (ReferenceEquals(book, open)) SetProgress();
        }
    }

    private async void OnEncodingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (populating || bookPath == null || reading == null || text == null || epub != null || pdf != null ||
            EncodingPicker.SelectedItem is not ComboBoxItem { Tag: string encoding } || encoding == text.EncodingName) return;
        var token = reading.Token;
        var saved = ReadAnchor();
        try
        {
            var changed = await TextDocument.LoadAsync(bookPath, encoding, token);
            token.ThrowIfCancellationRequested();
            moving = true;
            text = changed;
            ForgetLayoutLocation();
            ClearSearchResults();
            ResetTextPageNumbers();
            MoveToText(saved.Offset, saved.Fraction);
            moving = false;
            SaveCurrentPosition();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { ShowStatus(error.Message); }
        finally { if (!token.IsCancellationRequested) moving = false; }
    }

    private async Task RelocateAsync(string position)
    {
        if (ReadStoredDestination(position) is { } target)
            await NavigateReaderLocationAsync(target.Location, target.Marks,
                showTransientAnnotation: AnnotationAt(position) is not { } annotation || !ReaderHighlightPalette.IsVisibleAnnotation(annotation),
                storedPosition: position);
    }

    private async Task<bool> NavigateReaderLocationAsync(ReaderLocation destination, IReadOnlyList<Rect>? marks = null,
        PdfTextHit? match = null, bool showTransientAnnotation = true, string? storedPosition = null,
        ReaderLocation? jumpOrigin = null)
    {
        if (book is not { } opened || moving || turningPage || seekingPosition) return false;
        var session = reading;
        var source = pdf;
        var origin = CaptureNavigationLocation();
        var previousView = CaptureJumpView();
        var restored = false;
        pendingTurns.Clear();
        ForgetLayoutLocation();
        seekingPosition = true;
        ResetPageTransition();
        ClosePanel(false);
        ClosePageJump();
        SetProgress();
        try
        {
            if (epub is EpubDocument publication && destination.Chapter is int index && session != null &&
                storedPosition != null && IsLegacyEpubPosition(storedPosition))
            {
                var offset = await publication.ResolveLegacyPositionAsync(index, storedPosition, session.Token);
                if (!ReferenceEquals(book, opened) || !ReferenceEquals(reading, session)) return false;
                if (offset is int exact)
                    destination = new ReaderLocation(index, exact, 0, new TextPageStart(exact, 0, exact));
            }
            if (!await RestoreNavigationLocationAsync(destination) ||
                !ReferenceEquals(book, opened) || !ReferenceEquals(reading, session)) return false;
            if (source != null)
            {
                if ((marks != null || match != null) && ActivePdfTextLoad is { } loading) await loading;
                if (!ReferenceEquals(reading, session) || !ReferenceEquals(pdf, source) ||
                    !IsRenderedPdfPage(source, destination.Offset)) return false;
                if (marks != null) ActivePdfSelection.ShowAnnotation(marks, showTransientAnnotation);
                if (match != null)
                {
                    pdfMatch = match;
                    ActivePdfSelection.ShowMatch(match);
                }
                PdfSurface.UpdateLayout();
            }

            restored = true;
            RecordReaderJump(jumpOrigin ?? origin);
            FocusReadingSurface();
            return true;
        }
        catch (Exception error)
        {
            if (ReferenceEquals(book, opened) && ReferenceEquals(reading, session)) ShowStatus(error.Message);
            return false;
        }
        finally
        {
            seekingPosition = false;
            if (ReferenceEquals(book, opened) && ReferenceEquals(reading, session))
            {
                if (!restored) RetainFailedJumpLocation(origin, previousView);
                SetProgress();
            }
        }
    }

    private (ReaderLocation Location, IReadOnlyList<Rect>? Marks)? ReadStoredDestination(string position)
    {
        if (pdf is { } document)
        {
            if (int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
                return page >= 1 && page <= document.PageCount ? (new ReaderLocation(null, page - 1, 0), null) : null;

            if (document is FixedEpubPageBook fixedBook)
            {
                var index = IsLegacyEpubPosition(position) ? fixedBook.Publication.FindLegacyChapter(position) :
                    ParsePosition(position, requireOffset: true)?.Chapter;
                if (index is int chapterIndex && chapterIndex >= 0 && chapterIndex < document.PageCount)
                    return (new ReaderLocation(null, chapterIndex, 0), null);
            }

            try
            {
                using var metadata = JsonDocument.Parse(position);
                var mark = metadata.RootElement;
                if (mark.ValueKind != JsonValueKind.Object || !mark.TryGetProperty("page", out var pageProperty) ||
                    pageProperty.ValueKind != JsonValueKind.Number || !pageProperty.TryGetInt32(out var markedPage) ||
                    markedPage < 1 || markedPage > document.PageCount) return null;
                return (new ReaderLocation(null, markedPage - 1, 0), ReadPdfAnnotationBounds(mark));
            }
            catch (JsonException) { return null; }
        }

        return text != null && ReadStoredTextDestination(position, epub) is { } destination
            ? (destination, null) : null;
    }

    private bool SameReaderLocation(ReaderLocation first, ReaderLocation second)
    {
        if (first.Chapter != second.Chapter) return false;
        if (!continuous && pdf == null && first.TextPosition is { } start && second.TextPosition is { } end)
        {
            if (start.Illustration != null || end.Illustration != null) return start.Illustration == end.Illustration;
            if (start.SourceOffset >= 0 && end.SourceOffset >= 0) return start.SourceOffset == end.SourceOffset;
            return start.Offset == end.Offset && Math.Abs(start.Fraction - end.Fraction) <= .00001;
        }
        if (pdf != null) return first.Offset == second.Offset && Math.Abs(first.Fraction - second.Fraction) <= .1 &&
            Math.Abs(first.PdfHorizontalOffset - second.PdfHorizontalOffset) <= .1;
        return first.Offset == second.Offset && Math.Abs(first.Fraction - second.Fraction) <= .00001;
    }

    private static IReadOnlyList<Rect> ReadPdfAnnotationBounds(JsonElement mark)
    {
        if (!mark.TryGetProperty("rects", out var rects) || rects.ValueKind != JsonValueKind.Array) return [];
        var bounds = new List<Rect>();
        foreach (var raw in rects.EnumerateArray())
        {
            if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() != 4) continue;
            var values = raw.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN).ToArray();
            if (values.Any(value => !double.IsFinite(value) || value is < 0 or > 1) || values[2] == 0 || values[3] == 0) continue;
            bounds.Add(new Rect(values[0], values[1], Math.Min(1 - values[0], values[2]), Math.Min(1 - values[1], values[3])));
        }
        return bounds;
    }

    private async void OnAddBookmark(object sender, RoutedEventArgs e) => await ChangeCurrentBookmarkAsync(announce: false, addOnly: true);
    private async void OnRemoveBookmark(object sender, RoutedEventArgs e)
    {
        if (book == null || BookmarksList.SelectedItem is not BookmarkLocation selected) return;
        try
        {
            await store.RemoveBookmarkAsync(book.Id, selected.Entry.Id);
            await ReloadBookmarksAsync();
        }
        catch (Exception error) { ShowStatus(error.Message); }
    }
    private async void OnBookmarkDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BookmarksList.SelectedItem is BookmarkLocation selected) await RelocateAsync(selected.Entry.Position);
    }
    private async void OnBookmarkKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BookmarksList.SelectedItem is BookmarkLocation selected)
        {
            e.Handled = true;
            await RelocateAsync(selected.Entry.Position);
        }
    }

    private void OnTextSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox source || moving || updatingSpread || book == null) return;
        var selected = ReadReaderSelection(source);
        UpdateInlineSelection(source, selected);
        if (selected != null) SelectFacingPage(ReferenceEquals(source, FacingTextList));
        if (ReferenceEquals(source, ActiveTextList)) UpdateSelectionForNotes(selected?.Note);
    }

    private void OnPdfSelectionChanged(object? sender, EventArgs e)
    {
        if (pdf == null || moving || updatingPdfPages || sender is not PdfSelectionLayer layer) return;
        var selected = ReadReaderSelection(layer);
        if (selected != null) ActivatePdfPage(layer);
        UpdateInlineSelection(layer, selected);
        if (ReferenceEquals(layer, ActivePdfSelection)) UpdateSelectionForNotes(selected?.Note);
    }

}
