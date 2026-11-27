using System.ComponentModel;
using System.IO;

namespace SeaReader.Native;

public partial class MainWindow
{
    private sealed class BookmarkLocation(BookmarkEntry entry, ReaderLocation? destination, string section,
        ReaderLocation? matchDestination, bool legacyPositionPending) : INotifyPropertyChanged
    {
        public BookmarkEntry Entry { get; } = entry;
        public ReaderLocation? Destination { get; private set; } = destination;
        public ReaderLocation? MatchDestination { get; private set; } = matchDestination;
        public bool LegacyPositionPending { get; private set; } = legacyPositionPending;
        public string Section { get; } = section;
        public string LocationLabel { get; private set; } = section;
        public string? Excerpt { get; private set; }
        public string AccessibleName => Excerpt == null ? LocationLabel : $"{LocationLabel}。{Excerpt}";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetLegacyLocation(ReaderLocation? location)
        {
            LegacyPositionPending = false;
            MatchDestination = location;
            if (location.HasValue) Destination = location;
        }

        public void Update(string label, string? excerpt)
        {
            if (LocationLabel != label)
            {
                LocationLabel = label;
                PropertyChanged?.Invoke(this, new(nameof(LocationLabel)));
            }
            else if (Excerpt == excerpt) return;
            if (Excerpt != excerpt)
            {
                Excerpt = excerpt;
                PropertyChanged?.Invoke(this, new(nameof(Excerpt)));
            }
            PropertyChanged?.Invoke(this, new(nameof(AccessibleName)));
        }
    }

    private CancellationTokenSource? bookmarkPreviews;

    private BookmarkPageSelection CaptureBookmarkPage(string position)
    {
        if (continuous || text == null) return new BookmarkPageSelection(position, null, null, null, null);

        var location = CaptureNavigationLocation();
        // A partial index only contributes pages once both leaves and the next boundary are known.
        var pages = HasSpread ? spreadPagination : doublePage ? null : textPagination;
        int? page = pages != null ? CurrentTextPage(pages) : null;
        var activeChapter = epub == null ? (int?)null : HasSpread && facingSelected ? facingPage?.Chapter : chapter;
        var document = HasSpread && facingSelected ? facingPage?.Document : text;
        return new BookmarkPageSelection(position, epub, pages, page, location)
        {
            SourceLength = activeChapter == location.Chapter ? document?.Text.Length : null
        };
    }

    private string NewBookmarkPosition(BookmarkPageSelection selection)
    {
        if (selection.Location is not { } location) return selection.Position;
        if (selection.Pages is { } pages && selection.Page is int page)
        {
            var start = pages.GetPage(page, out var index);
            return SerializeTextPosition(Math.Max(0, start.Offset), start.Fraction, textPosition: start, locationChapter: index);
        }
        return location.TextPosition is { } position ? SerializeTextPosition(location.Offset, location.Fraction,
            textPosition: position, locationChapter: location.Chapter) : selection.Position;
    }

    private bool HasBookmarkOnActivePage()
    {
        if (readerBookmarksBookId != book?.Id || readerBookmarkPositions.Count == 0) return false;
        var position = ReadPosition();
        if (readerBookmarkPositions.Contains(position)) return true;
        if (continuous || text == null || BookmarksList.ItemsSource is not BookmarkLocation[] items) return false;

        var selection = CaptureBookmarkPage(position);
        var marked = items.Any(item => BookmarkMatchesSelection(item.MatchDestination, selection));
        if (!marked && bookmarkPreviews == null && epub is EpubDocument source && reading is { } session &&
            selection.Location?.Chapter is int index)
        {
            var pending = items.Where(item => item.LegacyPositionPending && item.Destination?.Chapter == index).ToArray();
            if (pending.Length > 0) _ = LoadBookmarkPreviewsAsync(source, pending, session.Token, includeExcerpts: false);
        }
        return marked;
    }

    private async Task ReloadBookmarksAsync()
    {
        if (book is not { } open || closing) return;
        var version = ++readerBookmarksVersion;
        try
        {
            var saved = await store.ListBookmarksAsync(open.Id);
            if (closing || !ReferenceEquals(book, open) || version != readerBookmarksVersion) return;
            readerBookmarksBookId = open.Id;
            readerBookmarkPositions = saved.Select(entry => entry.Position).ToHashSet(StringComparer.Ordinal);
            var hadFocus = shownPanel == Panel.Bookmarks && BookmarksList.IsKeyboardFocusWithin;
            var selected = (BookmarksList.SelectedItem as BookmarkLocation)?.Entry.Id;
            var items = saved.Select(CreateBookmarkLocation).ToArray();
            BookmarksList.ItemsSource = items;
            if (hadFocus)
            {
                BookmarksList.SelectedItem = items.FirstOrDefault(item => item.Entry.Id == selected) ?? items.FirstOrDefault();
                BookmarksList.Focus();
            }
            CancelBookmarkPreviews();
            if (shownPanel == Panel.Bookmarks && epub is { } source && reading is { } session && items.Length > 0)
                _ = LoadBookmarkPreviewsAsync(source, items, session.Token);
            UpdateBookmarkButton();
        }
        catch (Exception error) { if (!closing && ReferenceEquals(book, open)) ShowStatus(error.Message); }
    }

    private BookmarkLocation CreateBookmarkLocation(BookmarkEntry entry)
    {
        var destination = ReadStoredDestination(entry.Position)?.Location;
        var section = destination switch
        {
            null => "位置不可用",
            { } location when pdf != null => $"第 {location.Offset + 1} 页",
            { Chapter: int index } when epub != null => CompactProgressPreview(epub.Chapters[index].Title) ?? $"第 {index + 1} 章",
            { Offset: int offset } => $"字符位置 {offset}"
        };
        var legacy = epub is EpubDocument && IsLegacyEpubPosition(entry.Position);
        var match = !legacy && text != null ? ReadStoredTextDestination(entry.Position, epub, requireValidChapter: true) : null;
        var item = new BookmarkLocation(entry, destination, section, match, legacy);
        item.Update(BookmarkPageLabel(item), epub == null && pdf == null && text != null ? BookmarkExcerpt(item, text.Text) : null);
        return item;
    }

    private string BookmarkPageLabel(BookmarkLocation item)
    {
        if (textPagination is not { } pages || item.Destination is not { TextPosition: { } position } location)
            return item.Section;
        var number = pages.FindPage(location.Chapter ?? 0, position) + 1;
        return location.Chapter.HasValue ? $"第 {number} 页 · {item.Section}" : $"第 {number} 页";
    }

    private string? BookmarkExcerpt(BookmarkLocation item, string source)
    {
        if (item.Destination is not { TextPosition: { } position } location) return null;
        if (position.Illustration.HasValue) return "插图";
        var offset = position.SourceOffset >= 0 ? position.SourceOffset : Math.Max(0, position.Offset);
        if (position.SourceOffset < 0 && position.Fraction > 0 && textPagination is { } pages)
        {
            // Older positions store a fraction of a display row. Use the indexed line when it is in that same row.
            var start = pages.GetPage(pages.FindPage(location.Chapter ?? 0, position), out _);
            if (start.Offset == position.Offset && start.SourceOffset >= 0) offset = start.SourceOffset;
        }
        if (offset >= source.Length) return null;
        if (offset > 0 && char.IsLowSurrogate(source[offset]) && char.IsHighSurrogate(source[offset - 1])) offset--;
        return CompactProgressPreview(source.AsSpan(offset));
    }

    private void RefreshBookmarkPageLabels()
    {
        if (shownPanel != Panel.Bookmarks || BookmarksList.ItemsSource is not BookmarkLocation[] items) return;
        foreach (var item in items)
            item.Update(BookmarkPageLabel(item), epub == null && pdf == null && text != null ? BookmarkExcerpt(item, text.Text) : item.Excerpt);
    }

    private async Task LoadBookmarkPreviewsAsync(IChapterBook source, BookmarkLocation[] items, CancellationToken readingToken,
        bool includeExcerpts = true)
    {
        var load = bookmarkPreviews = CancellationTokenSource.CreateLinkedTokenSource(readingToken);
        var token = load.Token;
        var current = chapter;
        var currentText = text;
        var facing = facingPage?.Chapter;
        var facingText = facingPage?.Document;
        try
        {
            await Task.Yield();
            foreach (var group in items.GroupBy(item => item.Destination?.Chapter))
            {
                token.ThrowIfCancellationRequested();
                if (group.Key is not int index) continue;
                try
                {
                    var content = includeExcerpts ? (index == current ? currentText?.Text : index == facing ? facingText?.Text : null) ??
                        await source.ReadChapterAsync(index, token) : null;
                    foreach (var item in group)
                    {
                        token.ThrowIfCancellationRequested();
                        if (item.LegacyPositionPending)
                        {
                            var location = await ResolveBookmarkDestinationAsync(item.Entry.Position, source, token);
                            token.ThrowIfCancellationRequested();
                            item.SetLegacyLocation(location);
                        }
                        item.Update(BookmarkPageLabel(item), content != null ? BookmarkExcerpt(item, content) : item.Excerpt);
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceWarning($"Unable to preview bookmarks in section {index + 1}: {error.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(bookmarkPreviews, load))
            {
                bookmarkPreviews = null;
                load.Dispose();
                if (!token.IsCancellationRequested) UpdateBookmarkButton();
            }
        }
    }

    private void CancelBookmarkPreviews()
    {
        if (bookmarkPreviews is not { } current) return;
        bookmarkPreviews = null;
        current.Cancel();
        current.Dispose();
    }
}
