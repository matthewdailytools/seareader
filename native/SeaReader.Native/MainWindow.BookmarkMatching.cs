using System.Diagnostics;
using System.IO;

namespace SeaReader.Native;

public partial class MainWindow
{
    private readonly record struct BookmarkPageSelection(string Position, IChapterBook? Source, TextPagination? Pages,
        int? Page, ReaderLocation? Location)
    {
        public int? SourceLength { get; init; }
    }

    private static async Task<BookmarkEntry?> FindMatchingBookmarkAsync(IReadOnlyList<BookmarkEntry> entries,
        BookmarkPageSelection selection, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var raw = entries.FirstOrDefault(entry => entry.Position == selection.Position);
        if (raw != null || selection.Location is not { TextPosition: not null } location) return raw;

        BookmarkEntry? exact = null;
        BookmarkEntry? nearest = null;
        var nearestDistance = double.PositiveInfinity;
        var legacy = new List<BookmarkEntry>();
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (ReadStoredTextDestination(entry.Position, selection.Source, requireValidChapter: true) is not { } destination ||
                destination.Chapter != location.Chapter) continue;
            if (IsLegacyEpubPosition(entry.Position)) legacy.Add(entry);
            else Consider(entry, destination);
        }

        foreach (var entry in legacy)
        {
            if (!IsNewerBookmark(entry, exact)) continue;
            if (await ResolveBookmarkDestinationAsync(entry.Position, selection.Source, token).ConfigureAwait(false) is { } destination)
                Consider(entry, destination);
        }
        return exact ?? nearest;

        void Consider(BookmarkEntry entry, ReaderLocation destination)
        {
            if (CompareBookmarkDestination(destination, selection) is not { } match) return;
            if (match.Exact)
            {
                if (IsNewerBookmark(entry, exact)) exact = entry;
                return;
            }
            if (match.Distance < nearestDistance || match.Distance == nearestDistance && IsNewerBookmark(entry, nearest))
            {
                nearest = entry;
                nearestDistance = match.Distance;
            }
        }
    }

    private static bool BookmarkMatchesSelection(ReaderLocation? destination, BookmarkPageSelection selection) =>
        CompareBookmarkDestination(destination, selection).HasValue;

    private static (bool Exact, double Distance)? CompareBookmarkDestination(ReaderLocation? destination,
        BookmarkPageSelection selection)
    {
        if (destination is not { TextPosition: { } position } saved ||
            selection.Location is not { TextPosition: { } current } location || saved.Chapter != location.Chapter ||
            position.Offset < -1 || position.SourceOffset < -1 || position.Illustration is < 0 ||
            !double.IsFinite(position.Fraction) || position.Fraction is < 0 or > 1 ||
            selection.SourceLength is int length && Math.Max(position.Offset, position.SourceOffset) > length) return null;
        if (SameBookmarkSource(position, current)) return (true, 0);
        if (selection.Pages is not { } pages || selection.Page is not int page || page < 0 || page >= pages.Count) return null;

        pages.GetPage(page, out var pageChapter);
        if (pageChapter != (location.Chapter ?? 0) || pages.FindPage(pageChapter, position) != page) return null;
        var distance = position.SourceOffset >= 0 && current.SourceOffset >= 0
            ? Math.Abs((double)position.SourceOffset - current.SourceOffset)
            : Math.Abs(position.Offset + position.Fraction - (current.Offset + current.Fraction));
        return (false, distance);
    }

    private static bool SameBookmarkSource(TextPageStart first, TextPageStart second)
    {
        if (first.Illustration.HasValue || second.Illustration.HasValue) return first.Illustration == second.Illustration;
        if (first.SourceOffset >= 0 && second.SourceOffset >= 0) return first.SourceOffset == second.SourceOffset;
        return first.SourceOffset < 0 && second.SourceOffset < 0 && first.Offset == second.Offset &&
            Math.Abs(first.Fraction - second.Fraction) <= .00001;
    }

    private static bool IsNewerBookmark(BookmarkEntry entry, BookmarkEntry? other) => other == null ||
        entry.CreatedAt > other.CreatedAt || entry.CreatedAt == other.CreatedAt && entry.Id > other.Id;

    private static async Task<ReaderLocation?> ResolveBookmarkDestinationAsync(string position, IChapterBook? source,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var destination = ReadStoredTextDestination(position, source, requireValidChapter: true);
        if (!IsLegacyEpubPosition(position)) return destination;
        if (source is not EpubDocument publication || destination?.Chapter is not int chapter) return null;
        try
        {
            return await publication.ResolveLegacyPositionAsync(chapter, position, token).ConfigureAwait(false) is int offset
                ? new ReaderLocation(chapter, offset, 0, new TextPageStart(offset, 0, offset)) : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Unable to read the bookmark anchor in section {chapter + 1}: {error.Message}");
            return null;
        }
    }

    private static ReaderLocation? ReadStoredTextDestination(string position, IChapterBook? publication,
        bool requireValidChapter = false)
    {
        var saved = ParsePosition(position);
        if (saved is { Offset: < 0 } || saved == null && (publication == null || !IsLegacyEpubPosition(position))) return null;
        int? chapter = null;
        if (publication != null)
        {
            if (requireValidChapter && saved?.Chapter is int index)
            {
                if (index < 0 || index >= publication.Chapters.Count) return null;
                chapter = index;
            }
            else chapter = publication.GetChapterForPosition(position);
        }

        var offset = saved?.Offset ?? 0;
        var fraction = Math.Clamp(saved?.LineFraction ?? 0, 0, 1);
        // Older bookmarks store a rendered position within a paragraph. Notes instead store a character range.
        var atHeading = chapter != null && offset == 0 && fraction == 0 && saved?.End == null;
        var source = saved?.TextPosition ?? (atHeading
            ? new TextPageStart(-1, 0, -1)
            : new TextPageStart(offset, fraction, fraction == 0 ? offset : -1));
        return new ReaderLocation(chapter, offset, fraction, source);
    }
}
