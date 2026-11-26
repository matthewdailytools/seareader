using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
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
    private sealed record TextLocation(string? Encoding, int Offset, double LineFraction = 0, int? Chapter = null, int? End = null);
    private sealed record ReaderAppearance(string Theme = "system", string Font = "system-ui", double Size = 18, double LineHeight = 1.8, double Margin = 24);
    private sealed record SearchResult(int? Chapter, int Offset, string Excerpt, PdfTextHit? PdfHit = null);
    private sealed record ContentsEntry(int Index, string Title);
    private sealed record BookmarkLocation(BookmarkEntry Entry, string Label);
    private sealed record NoteSelection(string Position, string Quotation);
    private enum Panel { Search, Appearance, Flow, Contents, Bookmarks, Notes }
    private static readonly JsonSerializerOptions PositionJson = new(JsonSerializerDefaults.Web);
    private const int TextWindowSize = 128;
    private const int TextWindowOverlap = 24;

    private readonly LibraryStore store;
    private readonly string[] startupPaths;
    private readonly SemaphoreSlim importGate = new(1);
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer hoverTimer;
    private readonly DispatcherTimer tapTimer;
    private readonly DispatcherTimer pdfResizeTimer;
    private readonly DispatcherTimer appearanceTimer;
    private IReadOnlyList<BookEntry> books = [];
    private BookEntry? book;
    private string? bookPath;
    private TextDocument? text;
    private IChapterBook? epub;
    private PdfBook? pdf;
    private CancellationTokenSource? reading;
    private CancellationTokenSource? pageRender;
    private CancellationTokenSource? pdfTextReading;
    private Task? pdfTextLoad;
    private PdfTextHit? pdfMatch;
    private CancellationTokenSource? finding;
    private ScrollViewer? textScroll;
    private readonly List<(Paragraph Paragraph, TextLine Line)> visibleRows = [];
    private TextDocument? windowDocument;
    private int windowStart;
    private int windowEnd;
    private bool rewindowQueued;
    private (string Id, string Position)? pendingPosition;
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
    private SubscriptionWindow? subscriptionsWindow;
    private bool windowReady;
    private bool continuous = true;
    private int chapter;
    private int pdfPage;
    private string pdfZoom = "page";
    private (int? Chapter, int Offset, double Fraction)? returnLocation;

    public MainWindow(LibraryStore store, IEnumerable<string> startupPaths)
    {
        this.store = store;
        this.startupPaths = startupPaths.ToArray();
        populating = true;
        InitializeComponent();
        PdfSelection.SelectionChanged += OnPdfSelectionChanged;
        Resources["ReaderFont"] = new FontFamily("Segoe UI");
        Resources["ReaderSize"] = appearance.Size;
        saveTimer = CreateTimer(450, async () => await FlushPositionAsync());
        hoverTimer = CreateTimer(1800, HideHoverControls);
        tapTimer = CreateTimer(260, CompleteReaderTap);
        pdfResizeTimer = CreateTimer(150, async () => await RenderPdfAsync());
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
        try
        {
            var saved = await store.GetPreferenceAsync("reading-appearance");
            if (saved != null)
            {
                try { appearance = JsonSerializer.Deserialize<ReaderAppearance>(saved, PositionJson) ?? new(); }
                catch (JsonException) { appearance = new(); }
            }
            ApplyAppearance();
            UpdateAppearanceControls();
            await ReloadLibraryAsync();
            if (startupPaths.Length != 0) await OpenFilesAsync(startupPaths);
        }
        catch (Exception error) { ShowStatus(error.Message); }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        closing = true;
        reading?.Cancel();
        finding?.Cancel();
        pageRender?.Cancel();
        SaveCurrentPosition();
        await FlushPositionAsync();
        if (appearanceTimer.IsEnabled)
        {
            appearanceTimer.Stop();
            await SaveAppearanceAsync();
        }
        Close();
    }

    public async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        var files = paths.ToArray();
        if (files.Length == 0) return;
        await importGate.WaitAsync();
        try
        {
            ShowStatus("正在导入…");
            BookEntry? completed = null;
            var failures = new List<string>();
            foreach (var path in files)
            {
                try { completed = await store.ImportAsync(path); }
                catch (Exception error) { failures.Add(Path.GetFileName(path) + "：" + error.Message); }
            }
            await ReloadLibraryAsync();
            if (completed != null) await OpenBookAsync(completed);
            if (failures.Count != 0) ShowStatus(string.Join(Environment.NewLine, failures));
        }
        catch (Exception error) { ShowStatus(error.Message); }
        finally { importGate.Release(); }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            await OpenFilesAsync(paths);
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "导入文件", Multiselect = true,
            Filter = BookFormats.FileFilter
        };
        if (picker.ShowDialog(this) == true) await OpenFilesAsync(picker.FileNames);
    }

    private async Task ReloadLibraryAsync()
    {
        books = await store.ListAsync();
        FilterLibrary();
    }

    private void OnLibrarySearchChanged(object sender, TextChangedEventArgs e)
    {
        if (LibraryList != null) FilterLibrary();
    }

    private void FilterLibrary()
    {
        var query = LibrarySearch.Text.Trim();
        var results = query.Length == 0
            ? books
            : books.Where(item => item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        LibraryList.ItemsSource = results;
        EmptyLibrary.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLibrary.Text = query.Length == 0
            ? "将电子书拖入窗口，或点击导入文件。" : "没有匹配的书籍。";
    }

    private async void OnOpenSelection(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is BookEntry selected) await OpenBookAsync(selected);
    }

    private async void OnLibraryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject origin &&
            ItemsControl.ContainerFromElement(LibraryList, origin) is ListBoxItem row &&
            row.DataContext is BookEntry selected) await OpenBookAsync(selected);
    }

    private async Task OpenBookAsync(BookEntry selected)
    {
        SaveCurrentPosition();
        await FlushPositionAsync();
        reading?.Cancel();
        reading?.Dispose();
        reading = new CancellationTokenSource();
        var token = reading.Token;
        pageRender?.Cancel();
        finding?.Cancel();
        moving = true;
        ShowStatus("正在打开 " + selected.Title + "…");
        try
        {
            var path = await store.OpenAsync(selected.Id);
            var position = await store.GetPositionAsync(selected.Id);
            var saved = ParsePosition(position);
            var mode = await store.GetPreferenceAsync(selected.Format.ToLowerInvariant() + "-reading-mode");
            token.ThrowIfCancellationRequested();
            TextDocument? document = null;
            IChapterBook? epubFile = null;
            PdfBook? pdfFile = null;
            var startChapter = 0;
            var format = selected.Format.ToLowerInvariant();
            if (format == "txt")
                document = await TextDocument.LoadAsync(path, saved?.Encoding, token);
            else if (BookFormats.IsChapterBook(format))
            {
                epubFile = await BookFormats.ReadChaptersAsync(path, format, token);
                startChapter = epubFile.GetChapterForPosition(position);
                var chapterText = await epubFile.ReadChapterAsync(startChapter, token);
                document = await Task.Run(() => TextDocument.FromText(chapterText), token);
            }
            else if (format == "pdf") pdfFile = await PdfBook.OpenAsync(path, token);
            else throw new InvalidDataException("无法打开此格式的电子书。");
            token.ThrowIfCancellationRequested();

            book = selected;
            bookPath = path;
            text = document;
            epub = epubFile;
            pdf = pdfFile;
            chapter = startChapter;
            pdfPage = pdf == null ? 0 : Math.Clamp(int.TryParse(position, out var page) ? page - 1 : 0, 0, pdf.PageCount - 1);
            ResetPdfSelection();
            continuous = mode != "page";
            returnLocation = null;
            ReturnPositionButton.Visibility = Visibility.Collapsed;
            noteSelection = null;
            ReaderTitle.Text = selected.Title;
            ReaderSearch.Clear();
            SearchStatus.Text = "";
            SearchResults.ItemsSource = null;
            BookmarksList.ItemsSource = null;
            NotesList.ItemsSource = null;
            LibrarySurface.Visibility = Visibility.Collapsed;
            ReaderSurface.Visibility = Visibility.Visible;
            ClosePanel(false);
            SetControls(false);
            UpdateReaderControls();
            ShowStatus("");
            if (text != null)
            {
                MoveToText(saved?.Offset ?? 0, saved?.LineFraction ?? 0);
                TextList.Focus();
            }
            else
            {
                ClearTextWindow();
                PdfPageImage.Source = null;
                PdfSurface.UpdateLayout();
                await RenderPdfAsync();
                if (!token.IsCancellationRequested) PdfSelection.Focus();
            }
            moving = false;
            SaveCurrentPosition();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested) ShowStatus(error.Message); }
        finally { if (!token.IsCancellationRequested) moving = false; }
    }

    private static TextLocation? ParsePosition(string? position)
    {
        if (position == null) return null;
        if (position.StartsWith("native-epub:", StringComparison.Ordinal)) position = position[12..];
        if (!position.StartsWith('{')) return null;
        try { return JsonSerializer.Deserialize<TextLocation>(position, PositionJson); }
        catch (JsonException) { return null; }
    }

    private void UpdateReaderControls()
    {
        TextList.Visibility = pdf == null ? Visibility.Visible : Visibility.Collapsed;
        PdfSurface.Visibility = pdf != null ? Visibility.Visible : Visibility.Collapsed;
        TextAppearancePanel.Visibility = pdf == null ? Visibility.Visible : Visibility.Collapsed;
        PdfAppearancePanel.Visibility = pdf != null ? Visibility.Visible : Visibility.Collapsed;
        ContentsButton.Visibility = epub != null || pdf != null ? Visibility.Visible : Visibility.Collapsed;
        FlowButton.Visibility = pdf == null ? Visibility.Visible : Visibility.Collapsed;
        AppearanceButton.Content = pdf == null ? "Aa 排版" : "页面";
        Paper.MaxWidth = pdf == null ? 820 : double.PositiveInfinity;
        EncodingPanel.Visibility = epub == null && pdf == null ? Visibility.Visible : Visibility.Collapsed;
        FlowButton.ToolTip = continuous ? "连续滚动" : "按页翻阅";
        populating = true;
        if (text != null) SelectTag(EncodingPicker, text.EncodingName);
        populating = false;
        SetProgress();
    }

    private async void OnBack(object sender, RoutedEventArgs e)
    {
        SaveCurrentPosition();
        await FlushPositionAsync();
        reading?.Cancel();
        finding?.Cancel();
        pageRender?.Cancel();
        pdfResizeTimer.Stop();
        book = null;
        text = null;
        epub = null;
        pdf = null;
        ResetPdfSelection();
        ClearTextWindow();
        PdfPageImage.Source = null;
        ClosePanel(false);
        SetControls(false);
        ReaderSurface.Visibility = Visibility.Collapsed;
        LibrarySurface.Visibility = Visibility.Visible;
        LibrarySearch.Focus();
        try { await ReloadLibraryAsync(); }
        catch (Exception error) { ShowStatus(error.Message); }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusSurface.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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
        var origin = ParagraphTop(0);
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

    private double ParagraphHeight(int index) => index + 1 < visibleRows.Count
        ? ParagraphTop(index + 1) - ParagraphTop(index)
        : visibleRows[index].Paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Bottom - ParagraphTop(index);

    private void ClearTextWindow()
    {
        visibleRows.Clear();
        TextList.Document = new FlowDocument();
        windowDocument = null;
    }

    private void SetTextWindow(int aroundIndex)
    {
        if (text == null) return;
        windowDocument = text;
        windowStart = Math.Clamp(aroundIndex - TextWindowSize / 2, 0, Math.Max(0, text.Lines.Count - TextWindowSize));
        windowEnd = Math.Min(windowStart + TextWindowSize, text.Lines.Count);
        var document = new FlowDocument { ColumnWidth = double.PositiveInfinity };
        visibleRows.Clear();
        for (var index = windowStart; index < windowEnd; index++)
        {
            var line = text.Lines[index];
            var paragraph = new Paragraph(new Run(line.Text.Length == 0 ? "\u00a0" : line.Text));
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

    private void MoveToText(int offset, double fraction = 0, bool recenter = false)
    {
        if (text == null || text.Lines.Count == 0) return;
        var previous = moving;
        moving = true;
        try
        {
            var index = text.FindLine(offset);
            if (recenter || !ReferenceEquals(windowDocument, text) || index < windowStart || index >= windowEnd)
                SetTextWindow(index);
            TextList.UpdateLayout();
            FindTextViewport();
            if (textScroll != null && visibleRows.Count > 0)
            {
                var localIndex = index - windowStart;
                var top = ParagraphTop(localIndex) - ParagraphTop(0);
                if (double.IsFinite(top))
                {
                    var height = ParagraphHeight(localIndex);
                    textScroll.ScrollToVerticalOffset(Math.Max(0, top + (double.IsFinite(height) ? height : 0) * Math.Clamp(fraction, 0, 1)));
                    TextList.UpdateLayout();
                }
            }
            anchor = ReadAnchor();
            SetProgress();
        }
        finally { moving = previous; }
    }

    private void OnTextScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (moving || text == null || pdf != null || textScroll != null && !ReferenceEquals(e.OriginalSource, textScroll)) return;
        if (Math.Abs(e.VerticalChange) < .01 && Math.Abs(e.ViewportHeightChange) < .01) return;
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
        return direction < 0 && windowStart > 0 && line < windowStart + TextWindowOverlap ||
            direction > 0 && windowEnd < text.Lines.Count &&
                (line >= windowEnd - TextWindowOverlap || textScroll.VerticalOffset >= textScroll.ScrollableHeight - 1);
    }

    private string ReadPosition()
    {
        if (pdf != null) return (pdfPage + 1).ToString(CultureInfo.InvariantCulture);
        anchor = ReadAnchor();
        return SerializeTextPosition(anchor.Offset, anchor.Fraction);
    }

    private string SerializeTextPosition(int offset, double fraction = 0, int? end = null)
    {
        var value = JsonSerializer.Serialize(new TextLocation(text?.EncodingName, offset, fraction,
            epub == null ? null : chapter, end), PositionJson);
        return book?.Format == "epub" ? "native-epub:" + value : value;
    }

    private void SaveCurrentPosition()
    {
        if (book == null || text == null && pdf == null || moving) return;
        pendingPosition = (book.Id, ReadPosition());
        saveTimer.Stop();
        saveTimer.Start();
    }

    private async Task FlushPositionAsync()
    {
        saveTimer.Stop();
        if (pendingPosition is not { } save) return;
        pendingPosition = null;
        try { await store.SavePositionAsync(save.Id, save.Position); }
        catch (Exception error) { ShowStatus(error.Message); }
    }

    private void SetProgress()
    {
        var previous = populating;
        populating = true;
        PositionSlider.Minimum = 0;
        PositionSlider.Maximum = Math.Max(1, (pdf?.PageCount ?? text?.Text.Length ?? 1) - 1);
        PositionSlider.Value = pdf != null ? pdfPage : anchor.Offset;
        PositionSlider.ToolTip = pdf != null ? $"{pdfPage + 1} / {pdf.PageCount}" :
            epub != null ? $"{chapter + 1} / {epub.Chapters.Count}" :
            text != null ? $"{Math.Clamp(anchor.Offset * 100.0 / Math.Max(text.Text.Length, 1), 0, 100):0.#}%" : null;
        populating = previous;
    }

    private async Task ChangeChapterAsync(int next, int offset = 0, double fraction = 0)
    {
        if (epub == null || reading == null || next < 0 || next >= epub.Chapters.Count) return;
        if (next == chapter)
        {
            MoveToText(offset, fraction);
            SaveCurrentPosition();
            return;
        }
        var current = epub;
        var token = reading.Token;
        try
        {
            var content = await current.ReadChapterAsync(next, token);
            var document = await Task.Run(() => TextDocument.FromText(content), token);
            token.ThrowIfCancellationRequested();
            moving = true;
            text = document;
            chapter = next;
            MoveToText(offset, fraction);
            moving = false;
            SaveCurrentPosition();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { ShowStatus(error.Message); }
        finally { if (!token.IsCancellationRequested) moving = false; }
    }

    private async Task RenderPdfAsync()
    {
        if (pdf == null || reading == null) return;
        pdfResizeTimer.Stop();
        pageRender?.Cancel();
        pageRender?.Dispose();
        pageRender = CancellationTokenSource.CreateLinkedTokenSource(reading.Token);
        var token = pageRender.Token;
        var width = Math.Max(280, (int)PdfSurface.ActualWidth - 32);
        var actualWidth = pdfZoom == "150" ? (int)(width * 1.5) : width;
        var pixelWidth = (int)Math.Ceiling(actualWidth * VisualTreeHelper.GetDpi(PdfPageImage).DpiScaleX);
        try
        {
            var bitmap = await pdf.RenderPageAsync(pdfPage, pixelWidth, token);
            token.ThrowIfCancellationRequested();
            PdfPageImage.Source = bitmap;
            var scale = pdfZoom == "page"
                ? Math.Min((double)width / bitmap.PixelWidth, Math.Max(150, PdfSurface.ActualHeight - 24) / bitmap.PixelHeight)
                : (double)actualWidth / bitmap.PixelWidth;
            PdfPageHost.Width = Math.Max(1, bitmap.PixelWidth * scale);
            PdfPageHost.Height = Math.Max(1, bitmap.PixelHeight * scale);
            System.Windows.Automation.AutomationProperties.SetName(PdfPageImage, $"第 {pdfPage + 1} 页");
            if (pdfTextLoad == null)
            {
                pdfTextReading = CancellationTokenSource.CreateLinkedTokenSource(reading.Token);
                pdfTextLoad = LoadPdfTextAsync(pdf, pdfPage, pdfTextReading.Token);
            }
            ShowStatus("");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested) ShowStatus(error.Message); }
    }

    private async Task ChangePdfPageAsync(int index)
    {
        if (pdf == null) return;
        var next = Math.Clamp(index, 0, pdf.PageCount - 1);
        if (next != pdfPage)
        {
            pdfPage = next;
            ResetPdfSelection();
            PdfPageImage.Source = null;
        }
        PdfSurface.ScrollToHome();
        SetProgress();
        SaveCurrentPosition();
        await RenderPdfAsync();
    }

    internal BitmapSource? CurrentPdfImage => PdfPageImage.Source as BitmapSource;

    internal string SelectedReaderText() => pdf == null ? TextList.Selection.Text : PdfSelection.SelectedText;

    private void ResetPdfSelection()
    {
        pdfTextReading?.Cancel();
        pdfTextReading?.Dispose();
        pdfTextReading = null;
        pdfTextLoad = null;
        pdfMatch = null;
        PdfSelection.SetPage(null);
    }

    private async Task LoadPdfTextAsync(PdfBook source, int page, CancellationToken token)
    {
        try
        {
            var textPage = await source.ReadTextPageAsync(page, token);
            if (token.IsCancellationRequested) return;
            PdfSelection.SetPage(textPage);
            if (pdfMatch is { } hit) PdfSelection.ShowMatch(hit);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning($"PDF text extraction failed on page {page + 1}: {error.Message}");
        }
    }

    private void OnPdfSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (pdf == null || pdfResizeTimer == null) return;
        pdfResizeTimer.Stop();
        pdfResizeTimer.Start();
    }

    private async Task TurnAsync(int direction)
    {
        if (pdf != null) { await ChangePdfPageAsync(pdfPage + direction); return; }
        FindTextViewport();
        if (textScroll == null) return;
        var atWindowBoundary = direction > 0
            ? textScroll.VerticalOffset >= textScroll.ScrollableHeight - 2 : textScroll.VerticalOffset <= 2;
        if (atWindowBoundary && text != null)
        {
            var hasMoreRows = direction > 0 ? windowEnd < text.Lines.Count : windowStart > 0;
            if (hasMoreRows)
            {
                var latest = ReadAnchor();
                MoveToText(latest.Offset, latest.Fraction, recenter: true);
            }
            else if (epub != null)
            {
                await ChangeChapterAsync(chapter + direction, direction < 0 ? int.MaxValue : 0);
                return;
            }
        }
        var content = Math.Max(0, textScroll.ViewportHeight - Math.Max(appearance.Size, 12));
        textScroll.ScrollToVerticalOffset(textScroll.VerticalOffset + direction * content);
    }

    private async void OnTurnButton(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string direction } && int.TryParse(direction, out var turn)) await TurnAsync(turn);
    }

    private async void OnReaderWheel(object sender, MouseWheelEventArgs e)
    {
        if (text == null || InOverlay || !TextList.IsMouseOver || e.Delta == 0) return;
        e.Handled = true;
        var direction = e.Delta > 0 ? -1 : 1;
        if (!continuous)
        {
            await TurnAsync(direction);
            return;
        }
        FindTextViewport();
        if (textScroll == null) return;
        if (NeedsTextWindow(direction))
        {
            var latest = ReadAnchor();
            MoveToText(latest.Offset, latest.Fraction, recenter: true);
        }
        else if (epub != null && (direction > 0 && textScroll.VerticalOffset >= textScroll.ScrollableHeight - 1 ||
                 direction < 0 && textScroll.VerticalOffset <= 1))
        {
            await TurnAsync(direction);
            return;
        }
        textScroll.ScrollToVerticalOffset(textScroll.VerticalOffset - e.Delta * .8);
    }

    private async void OnPositionSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (populating || book == null) return;
        var position = (int)e.NewValue;
        if (pdf != null) await ChangePdfPageAsync(position);
        else
        {
            MoveToText(position);
            SaveCurrentPosition();
        }
    }

    private void SetControls(bool visible, bool hover = false, bool focus = false)
    {
        if (!visible) ClosePanel(false);
        controlsVisible = visible;
        hoverVisible = visible && hover;
        ReaderToolbar.Visibility = ReaderHeader.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ShowControlsButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
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
        if (shownPanel != null || ReaderToolsButton.ContextMenu.IsOpen || ReaderToolbar.IsMouseOver || ReaderHeader.IsMouseOver ||
            ReaderToolbar.IsKeyboardFocusWithin || ReaderHeader.IsKeyboardFocusWithin)
        {
            RestartHoverTimer();
            return;
        }
        SetControls(false);
    }

    private void OnToggleControls(object sender, RoutedEventArgs e) => SetControls(!controlsVisible, focus: true);

    private bool InOverlay =>
        ReaderHeader.IsMouseOver || ReaderToolbar.IsMouseOver || ReaderPanel.IsMouseOver || ShowControlsButton.IsMouseOver;

    private void OnReaderPointerMove(object sender, MouseEventArgs e)
    {
        if (book == null || e.LeftButton == MouseButtonState.Pressed) return;
        var point = e.GetPosition(ReaderSurface);
        if (lastPointer == point) return;
        lastPointer = point;
        if (point.Y > 22 && point.Y < ReaderSurface.ActualHeight - 22)
        {
            if (hoverVisible && InOverlay) RestartHoverTimer();
            return;
        }
        if (!controlsVisible) SetControls(true, hover: true);
        else if (hoverVisible) RestartHoverTimer();
    }

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
        tapTimer.Stop();
        tapStart = null;
        if (InOverlay || e.ClickCount != 1 || Within<ScrollBar>(e.OriginalSource as DependencyObject)) return;
        var point = e.GetPosition(ReaderSurface);
        var width = ReaderSurface.ActualWidth;
        var height = ReaderSurface.ActualHeight;
        if (point.X >= width * .22 && point.X <= width * .78 &&
            point.Y >= height * .24 && point.Y <= height * .76) tapStart = point;
    }

    private void OnReaderPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (tapStart is not { } start) return;
        tapStart = null;
        var delta = e.GetPosition(ReaderSurface) - start;
        if (Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance || InOverlay) return;
        tapTimer.Stop();
        tapTimer.Start();
    }

    private void CompleteReaderTap()
    {
        if (book == null || (pdf == null ? !TextList.Selection.IsEmpty : PdfSelection.SelectedBounds.Count != 0)) return;
        if (shownPanel != null) ClosePanel();
        else SetControls(!controlsVisible);
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (book == null)
        {
            if (modifiers == ModifierKeys.Control && e.Key == Key.F) { LibrarySearch.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && LibraryList.IsKeyboardFocusWithin && LibraryList.SelectedItem is BookEntry selected)
            {
                e.Handled = true;
                await OpenBookAsync(selected);
            }
            return;
        }
        if (modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            OpenPanel(Panel.Search);
        }
        else if (e.Key == Key.Escape && shownPanel != null)
        {
            e.Handled = true;
            ClosePanel();
        }
        else if (e.Key == Key.Escape && controlsVisible)
        {
            e.Handled = true;
            SetControls(false, focus: true);
        }
        else if (modifiers == ModifierKeys.None && e.Key == Key.F &&
                 Keyboard.FocusedElement is not TextBox { IsReadOnly: false })
        {
            e.Handled = true;
            SetControls(!controlsVisible, focus: true);
        }
        else if (modifiers == ModifierKeys.None && shownPanel == null && Keyboard.FocusedElement is not Slider &&
                 (e.Key is Key.PageDown or Key.PageUp || (pdf != null || !continuous) && e.Key is Key.Left or Key.Right))
        {
            e.Handled = true;
            await TurnAsync(e.Key is Key.PageDown or Key.Right ? 1 : -1);
        }
    }

    private FrameworkElement PanelContent(Panel panel) => panel switch
    {
        Panel.Search => SearchPanel,
        Panel.Appearance => AppearancePanel,
        Panel.Flow => FlowPanel,
        Panel.Contents => ContentsPanel,
        Panel.Bookmarks => BookmarksPanel,
        _ => NotesPanel
    };

    private void OnPanelButton(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<Panel>(value, out var panel))
        {
            if (shownPanel == panel) ClosePanel();
            else OpenPanel(panel);
        }
    }

    private void OpenPanel(Panel panel)
    {
        SetControls(true);
        if (shownPanel is { } visible) PanelContent(visible).Visibility = Visibility.Collapsed;
        shownPanel = panel;
        PanelContent(panel).Visibility = Visibility.Visible;
        ReaderPanel.Visibility = Visibility.Visible;
        PanelTitle.Text = panel switch
        {
            Panel.Search => "书内查找", Panel.Appearance => pdf == null ? "排版" : "页面设置",
            Panel.Flow => "阅读方式", Panel.Contents => pdf == null ? "目录" : "页码",
            Panel.Bookmarks => "书签", _ => "笔记"
        };
        UpdatePanelPlacement();
        switch (panel)
        {
            case Panel.Search:
                ReaderSearch.Focus();
                ReaderSearch.SelectAll();
                break;
            case Panel.Contents:
                UpdateContents();
                if (pdf != null) JumpInput.Focus();
                else ContentsList.Focus();
                break;
            case Panel.Bookmarks:
                _ = ReloadBookmarksAsync();
                BookmarksList.Focus();
                break;
            case Panel.Notes:
                _ = ReloadNotesAsync();
                ShowNoteSelection();
                break;
        }
    }

    private void UpdatePanelPlacement()
    {
        ReaderToolbar.UpdateLayout();
        ReaderPanel.Margin = new Thickness(12, 0, 12, ReaderToolbar.ActualHeight + 18);
    }

    private void OnReaderToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (shownPanel != null) ReaderPanel.Margin = new Thickness(12, 0, 12, e.NewSize.Height + 18);
    }

    private void ClosePanel(bool focus = true)
    {
        if (shownPanel is not { } visible) return;
        PanelContent(visible).Visibility = Visibility.Collapsed;
        shownPanel = null;
        ReaderPanel.Visibility = Visibility.Collapsed;
        if (!focus) return;
        if (pdf != null) PdfSelection.Focus();
        else TextList.Focus();
    }

    private void OnClosePanel(object sender, RoutedEventArgs e) => ClosePanel();

    private void UpdateContents()
    {
        ContentsList.Visibility = epub == null ? Visibility.Collapsed : Visibility.Visible;
        JumpPanel.Visibility = pdf == null ? Visibility.Collapsed : Visibility.Visible;
        ContentsList.ItemsSource = epub?.Chapters.Select((entry, index) => new ContentsEntry(index,
            string.IsNullOrWhiteSpace(entry.Title) ? $"第 {index + 1} 章" : entry.Title)).ToArray();
        if (pdf != null) JumpInput.Text = (pdfPage + 1).ToString(CultureInfo.InvariantCulture);
    }

    private async Task JumpToSelectedChapterAsync()
    {
        if (ContentsList.SelectedItem is ContentsEntry selected)
        {
            ClosePanel();
            await ChangeChapterAsync(selected.Index);
        }
    }

    private async void OnContentsDoubleClick(object sender, MouseButtonEventArgs e) => await JumpToSelectedChapterAsync();
    private async void OnContentsKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await JumpToSelectedChapterAsync(); }
    }

    private async Task JumpToPageAsync()
    {
        if (pdf == null || !int.TryParse(JumpInput.Text, out var target) || target < 1 || target > pdf.PageCount)
        {
            ShowStatus($"请输入 1 到 {pdf?.PageCount} 之间的页码。");
            return;
        }
        ShowStatus("");
        ClosePanel();
        await ChangePdfPageAsync(target - 1);
    }

    private async void OnJump(object sender, RoutedEventArgs e) => await JumpToPageAsync();
    private async void OnJumpKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await JumpToPageAsync(); }
    }

    private async Task FindAsync()
    {
        var query = ReaderSearch.Text.Trim();
        var current = text;
        var currentPdf = pdf;
        var opened = book;
        if (query.Length == 0 || opened == null || current == null && currentPdf == null) return;
        finding?.Cancel();
        finding?.Dispose();
        finding = new CancellationTokenSource();
        var token = finding.Token;
        SearchStatus.Text = "正在查找…";
        SearchResults.ItemsSource = null;
        try
        {
            var results = new List<SearchResult>();
            if (currentPdf != null)
            {
                var recognized = await store.ListRecognizedPagesAsync(opened.Id);
                var hits = await currentPdf.SearchAsync(query, recognized, 100, token);
                results.AddRange(hits.Select(hit => new SearchResult(null, hit.Offset,
                    $"第 {hit.Page + 1} 页{(hit.FromOcr ? "（OCR）" : "")} · {hit.Excerpt}", hit)));
            }
            else if (epub == null)
                results.AddRange((await current!.SearchAsync(query, 100, token)).Select(hit => new SearchResult(null, hit.Offset, hit.Excerpt)));
            else
            {
                var source = epub;
                for (var index = 0; index < source.Chapters.Count && results.Count < 100; index++)
                {
                    var pageText = index == chapter ? null : await source.ReadChapterAsync(index, token);
                    var candidate = pageText == null ? current! : await Task.Run(() => TextDocument.FromText(pageText), token);
                    var chapterResults = await candidate.SearchAsync(query, 100 - results.Count, token);
                    results.AddRange(chapterResults.Select(hit => new SearchResult(index, hit.Offset, hit.Excerpt)));
                }
            }
            token.ThrowIfCancellationRequested();
            SearchResults.ItemsSource = results;
            SearchStatus.Text = results.Count == 0 ? "没有匹配结果。" : results.Count == 100 ? "找到 100 个结果（已达上限）" : $"找到 {results.Count} 个结果";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested) SearchStatus.Text = error.Message; }
    }

    private async void OnFind(object sender, RoutedEventArgs e) => await FindAsync();
    private async void OnReaderSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await FindAsync(); }
    }

    private async Task OpenSearchResultAsync()
    {
        if (SearchResults.SelectedItem is not SearchResult selected) return;
        if (selected.PdfHit is { } hit && pdf is { } source)
        {
            returnLocation ??= (null, pdfPage, PdfSurface.VerticalOffset);
            ReturnPositionButton.Visibility = Visibility.Visible;
            ClosePanel();
            await ChangePdfPageAsync(hit.Page);
            if (!ReferenceEquals(pdf, source) || pdfPage != hit.Page) return;
            pdfMatch = hit;
            if (pdfTextLoad is { } loading) await loading;
            if (ReferenceEquals(pdfMatch, hit)) PdfSelection.ShowMatch(hit);
            return;
        }
        var location = ReadAnchor();
        returnLocation = (epub == null ? null : chapter, location.Offset, location.Fraction);
        ReturnPositionButton.Visibility = Visibility.Visible;
        ClosePanel();
        if (selected.Chapter is { } index) await ChangeChapterAsync(index, selected.Offset);
        else
        {
            MoveToText(selected.Offset);
            SaveCurrentPosition();
        }
    }

    private async void OnReturnPosition(object sender, RoutedEventArgs e)
    {
        if (returnLocation is not { } saved) return;
        returnLocation = null;
        ReturnPositionButton.Visibility = Visibility.Collapsed;
        if (pdf != null)
        {
            pdfMatch = null;
            PdfSelection.ShowAnnotation([]);
            await ChangePdfPageAsync(saved.Offset);
            PdfSurface.ScrollToVerticalOffset(saved.Fraction);
        }
        else if (saved.Chapter is { } index) await ChangeChapterAsync(index, saved.Offset, saved.Fraction);
        else
        {
            MoveToText(saved.Offset, saved.Fraction);
            SaveCurrentPosition();
        }
    }

    private async void OnSearchResultDoubleClick(object sender, MouseButtonEventArgs e) => await OpenSearchResultAsync();
    private async void OnSearchResultKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await OpenSearchResultAsync(); }
    }

    private void UpdateAppearanceControls()
    {
        var prior = populating;
        populating = true;
        FontSizeSlider.Value = Math.Clamp(appearance.Size, 12, 44);
        ParagraphSlider.Value = Math.Clamp(appearance.LineHeight, 1.4, 2.2);
        MarginSlider.Value = Math.Clamp(appearance.Margin, 12, 52);
        FontSizeLabel.Text = FontSizeSlider.Value.ToString("0", CultureInfo.CurrentCulture);
        SelectTag(ThemePicker, appearance.Theme);
        SelectTag(FontPicker, appearance.Font);
        SelectTag(ZoomPicker, pdfZoom);
        populating = prior;
    }

    private static void SelectTag(ComboBox box, string value) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == value) ??
            box.Items.OfType<ComboBoxItem>().FirstOrDefault();

    private void BrushColor(string key, string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        if (Resources[key] is not SolidColorBrush existing || existing.Color != color)
            Resources[key] = new SolidColorBrush(color);
    }

    private void ApplyAppearance()
    {
        var dark = appearance.Theme == "dark" ||
            appearance.Theme == "system" && SystemColors.WindowColor.R + SystemColors.WindowColor.G + SystemColors.WindowColor.B < 384;
        var sepia = appearance.Theme == "sepia";
        BrushColor("WindowSurface", dark ? "#191817" : sepia ? "#E5D8BF" : "#F2EEE5");
        BrushColor("PaperSurface", dark ? "#241F1B" : sepia ? "#FAEDD3" : "#FFFDFA");
        BrushColor("ToolbarSurface", dark ? "#302922" : sepia ? "#F2E2C6" : "#FFFCF5");
        BrushColor("MainInk", dark ? "#F0E7D9" : "#30271F");
        BrushColor("MutedInk", dark ? "#C8BBAB" : "#66594B");
        BrushColor("BorderInk", dark ? "#55493E" : "#D7C9B8");
        BrushColor("AccentInk", dark ? "#A34C39" : "#873B2A");
        BrushColor("HoverSurface", dark ? "#43362B" : "#EDE3D5");
        Resources["ReaderFont"] = new FontFamily(appearance.Font == "Georgia" ? "Georgia, SimSun" : "Segoe UI, Microsoft YaHei UI");
        Resources["ReaderSize"] = Math.Clamp(appearance.Size, 12, 44);
        ApplyDocumentAppearance();
    }

    private void ApplyDocumentAppearance()
    {
        var pageMargin = Math.Clamp(appearance.Margin, 12, 52);
        Resources["ReaderInsets"] = new Thickness(pageMargin, 10, pageMargin, 10);
        TextList.Document.PagePadding = new Thickness(0);
        TextList.Document.FontSize = Math.Clamp(appearance.Size, 12, 44);
        TextList.Document.LineHeight = appearance.Size * Math.Clamp(appearance.LineHeight, 1.4, 2.2);
        TextList.Document.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        foreach (var (paragraph, _) in visibleRows) paragraph.Margin = new Thickness(0);
    }

    private void ChangeAppearance(ReaderAppearance value)
    {
        if (populating) return;
        var saved = ReadAnchor();
        var wasMoving = moving;
        moving = true;
        try
        {
            appearance = value;
            ApplyAppearance();
            TextList.UpdateLayout();
            MoveToText(saved.Offset, saved.Fraction);
        }
        finally { moving = wasMoving; }
        SaveCurrentPosition();
        appearanceTimer.Stop();
        appearanceTimer.Start();
    }

    private async Task SaveAppearanceAsync()
    {
        try { await store.SavePreferenceAsync("reading-appearance", JsonSerializer.Serialize(appearance, PositionJson)); }
        catch (Exception error) { ShowStatus(error.Message); }
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
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemePicker?.SelectedItem is ComboBoxItem { Tag: string value }) ChangeAppearance(appearance with { Theme = value });
    }
    private async void OnZoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!populating && ZoomPicker?.SelectedItem is ComboBoxItem { Tag: string value })
        {
            pdfZoom = value;
            await RenderPdfAsync();
        }
    }

    private async void OnFlowMode(object sender, RoutedEventArgs e)
    {
        if (book == null || sender is not Button { Tag: string mode }) return;
        continuous = mode == "scroll";
        FlowButton.ToolTip = continuous ? "连续滚动" : "按页翻阅";
        ClosePanel();
        try { await store.SavePreferenceAsync(book.Format.ToLowerInvariant() + "-reading-mode", mode); }
        catch (Exception error) { ShowStatus(error.Message); }
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
            MoveToText(saved.Offset, saved.Fraction);
            moving = false;
            SaveCurrentPosition();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { ShowStatus(error.Message); }
        finally { if (!token.IsCancellationRequested) moving = false; }
    }

    private async Task ReloadBookmarksAsync()
    {
        if (book == null) return;
        var id = book.Id;
        try
        {
            var saved = await store.ListBookmarksAsync(id);
            if (book?.Id == id) BookmarksList.ItemsSource = saved.Select(entry => new BookmarkLocation(entry, LabelPosition(entry.Position))).ToArray();
        }
        catch (Exception error) { ShowStatus(error.Message); }
    }

    private string LabelPosition(string position)
    {
        if (pdf != null && int.TryParse(position, out var page)) return $"第 {page} 页";
        if (epub != null)
        {
            var at = epub.GetChapterForPosition(position);
            var title = epub.Chapters[at].Title;
            return string.IsNullOrWhiteSpace(title) ? $"第 {at + 1} 章" : title;
        }
        var offset = ParsePosition(position)?.Offset ?? 0;
        if (text != null && offset >= 0 && offset < text.Text.Length)
        {
            var span = text.Text.AsSpan(offset, Math.Min(50, text.Text.Length - offset));
            var end = span.IndexOfAny('\r', '\n');
            var excerpt = (end >= 0 ? span[..end] : span).Trim().ToString();
            if (excerpt.Length != 0) return excerpt;
        }
        return $"字符位置 {offset}";
    }

    private async Task RelocateAsync(string position)
    {
        ClosePanel();
        if (pdf != null)
        {
            if (int.TryParse(position, out var page)) await ChangePdfPageAsync(page - 1);
            else
            {
                try
                {
                    using var metadata = JsonDocument.Parse(position);
                    var mark = metadata.RootElement;
                    if (mark.ValueKind != JsonValueKind.Object || !mark.TryGetProperty("page", out var pageProperty) ||
                        pageProperty.ValueKind != JsonValueKind.Number || !pageProperty.TryGetInt32(out var markedPage) ||
                        markedPage < 1 || markedPage > pdf.PageCount) return;
                    var source = pdf;
                    await ChangePdfPageAsync(markedPage - 1);
                    if (pdfTextLoad is { } loading) await loading;
                    if (!ReferenceEquals(pdf, source) || pdfPage != markedPage - 1) return;
                    pdfMatch = null;
                    PdfSelection.ShowAnnotation(ReadPdfAnnotationBounds(mark));
                }
                catch (JsonException) { }
            }
            return;
        }
        var saved = ParsePosition(position);
        if (epub != null) { await ChangeChapterAsync(epub.GetChapterForPosition(position), saved?.Offset ?? 0, saved?.LineFraction ?? 0); return; }
        if (saved != null)
        {
            MoveToText(saved.Offset, saved.LineFraction);
            SaveCurrentPosition();
        }
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

    private async void OnAddBookmark(object sender, RoutedEventArgs e)
    {
        if (book == null) return;
        try
        {
            await store.AddBookmarkAsync(book.Id, ReadPosition());
            await ReloadBookmarksAsync();
        }
        catch (Exception error) { ShowStatus(error.Message); }
    }
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
        if (moving || book == null || TextList.Selection.IsEmpty || TextList.Selection.Start.Paragraph is not { } first) return;
        var row = visibleRows.FindIndex(item => ReferenceEquals(item.Paragraph, first));
        if (row < 0) return;
        var line = visibleRows[row].Line;
        var localOffset = new TextRange(first.ContentStart, TextList.Selection.Start).Text.Length;
        var start = checked(line.Offset + localOffset);
        var quote = TextList.Selection.Text;
        if (string.IsNullOrWhiteSpace(quote)) return;
        var end = start + Math.Min(quote.Length, 12000);
        if (quote.Length <= 12000 && TextList.Selection.End.Paragraph is { } last)
        {
            var lastRow = visibleRows.FindIndex(item => ReferenceEquals(item.Paragraph, last));
            if (lastRow >= 0) end = visibleRows[lastRow].Line.Offset + new TextRange(last.ContentStart, TextList.Selection.End).Text.Length;
        }
        if (quote.Length > 12000) quote = quote[..12000];
        noteSelection = new NoteSelection(SerializeTextPosition(start, end: end), quote);
        if (shownPanel == Panel.Notes) ShowNoteSelection();
    }

    private void OnPdfSelectionChanged(object? sender, EventArgs e)
    {
        if (pdf == null) return;
        var quote = PdfSelection.SelectedText.Trim();
        if (quote.Length != 0)
        {
            var rectangles = PdfSelection.SelectedBounds.Take(80).Select(rect => new[]
            {
                Math.Round(rect.X, 5), Math.Round(rect.Y, 5), Math.Round(rect.Width, 5), Math.Round(rect.Height, 5)
            });
            var position = JsonSerializer.Serialize(new { page = pdfPage + 1, rects = rectangles }, PositionJson);
            noteSelection = new NoteSelection(position, quote[..Math.Min(quote.Length, 12000)]);
        }
        else if (NotesList.SelectedItem == null) noteSelection = null;
        if (shownPanel == Panel.Notes) ShowNoteSelection();
    }

    private void ShowNoteSelection()
    {
        NoteQuote.Text = noteSelection?.Quotation ?? "选中文章文字后可添加笔记。";
        SaveNoteButton.IsEnabled = noteSelection != null;
        NoteInput.IsEnabled = noteSelection != null;
    }

    private async Task ReloadNotesAsync()
    {
        if (book == null) return;
        var id = book.Id;
        try
        {
            var saved = await store.ListAnnotationsAsync(id);
            if (book?.Id == id) NotesList.ItemsSource = saved;
        }
        catch (Exception error) { ShowStatus(error.Message); }
    }
    private void OnNoteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NotesList.SelectedItem is not AnnotationEntry selected) return;
        noteSelection = new NoteSelection(selected.Position, selected.Quotation);
        NoteInput.Text = selected.Note;
        ShowNoteSelection();
    }
    private async void OnSaveNote(object sender, RoutedEventArgs e)
    {
        if (book == null || noteSelection is not { } selected) return;
        try
        {
            await store.SaveAnnotationAsync(book.Id, selected.Position, selected.Quotation, NoteInput.Text);
            NoteInput.Clear();
            await ReloadNotesAsync();
        }
        catch (Exception error) { ShowStatus(error.Message); }
    }
    private async void OnRemoveNote(object sender, RoutedEventArgs e)
    {
        if (book == null || NotesList.SelectedItem is not AnnotationEntry selected) return;
        try
        {
            await store.RemoveAnnotationAsync(book.Id, selected.Id);
            NoteInput.Clear();
            noteSelection = null;
            ShowNoteSelection();
            await ReloadNotesAsync();
        }
        catch (Exception error) { ShowStatus(error.Message); }
    }
    private async void OnNoteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NotesList.SelectedItem is AnnotationEntry selected) await RelocateAsync(selected.Position);
    }
    private async void OnNoteKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && NotesList.SelectedItem is AnnotationEntry selected)
        {
            e.Handled = true;
            await RelocateAsync(selected.Position);
        }
    }
}
