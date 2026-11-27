using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private readonly Dictionary<string, BookLibraryDisplay> libraryDisplays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LibraryBookMetadata> libraryMetadata = new(StringComparer.Ordinal);
    private readonly HashSet<string> failedLibraryPreviews = new(StringComparer.Ordinal);
    private Task? loadLibraryMetadata;
    private Dictionary<string, BookEntry> libraryPreviewTargets = new(StringComparer.Ordinal);
    private Queue<BookEntry> libraryMetadataQueue = new();
    private CancellationTokenSource? libraryPreviewWork;
    private DispatcherTimer? libraryMetadataRefreshTimer;
    private string? libraryCoverInProgress;
    private bool libraryPreviewsPaused;

    private Task LoadLibraryBookMetadataAsync() => loadLibraryMetadata ??= ReadLibraryBookMetadataAsync();

    private async Task ReadLibraryBookMetadataAsync()
    {
        try
        {
            foreach (var pair in await store.GetBookMetadataAsync()) libraryMetadata[pair.Key] = pair.Value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Trace.TraceWarning("SeaReader library metadata: {0}", error.Message);
        }
    }

    private BookLibraryDisplay GetLibraryDisplay(BookEntry entry)
    {
        if (libraryDisplays.TryGetValue(entry.Id, out var display)) return display;

        display = entry.Display;
        if (libraryMetadata.TryGetValue(entry.Id, out var metadata)) display.Apply(metadata);
        libraryDisplays.Add(entry.Id, display);
        return display;
    }

    private void UpdateLibraryMetadataQueue()
    {
        libraryMetadataQueue = new(books.Where(entry => entry.Format == "epub" && !libraryMetadata.ContainsKey(entry.Id)));
    }

    private void SetLibraryPreviewTargets(IEnumerable<BookEntry> entries)
    {
        // The current filter already includes all metadata received before this call.
        libraryMetadataRefreshTimer?.Stop();
        var targets = entries.Where(entry => entry.Format is "epub" or "pdf")
            .DistinctBy(entry => entry.Id).ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        foreach (var entry in libraryPreviewTargets.Values)
            if (!targets.ContainsKey(entry.Id)) entry.Display.SetCover(null);
        libraryPreviewTargets = targets;
        if (libraryCoverInProgress is { } id && libraryPreviewWork is { IsCancellationRequested: false } work &&
            (!targets.ContainsKey(id) || ShouldRefreshLibraryMetadataOrder && PeekLibraryMetadata() != null))
            work.Cancel();
        EnsureLibraryPreviewWork();
    }

    private void StopLibraryPreviews()
    {
        libraryPreviewsPaused = true;
        libraryMetadataRefreshTimer?.Stop();
        libraryPreviewWork?.Cancel();
        foreach (var entry in libraryPreviewTargets.Values) entry.Display.SetCover(null);
        libraryPreviewTargets.Clear();
    }

    private void ResumeLibraryPreviews()
    {
        libraryPreviewsPaused = false;
        EnsureLibraryPreviewWork();
    }

    private (BookEntry Entry, bool IncludeCover)? NextLibraryPreview()
    {
        if (ShouldRefreshLibraryMetadataOrder && PeekLibraryMetadata() is { } metadataEntry)
            return (metadataEntry, false);

        foreach (var entry in libraryPreviewTargets.Values)
        {
            if (failedLibraryPreviews.Contains(entry.Id)) continue;
            if (!libraryMetadata.TryGetValue(entry.Id, out var metadata) || !metadata.CoverChecked ||
                metadata.HasCover && entry.Display.Cover == null)
                return (entry, true);
        }

        return PeekLibraryMetadata() is { } next ? (next, false) : null;
    }

    private BookEntry? PeekLibraryMetadata()
    {
        while (libraryMetadataQueue.TryPeek(out var entry))
        {
            if (!libraryMetadata.ContainsKey(entry.Id) && !failedLibraryPreviews.Contains(entry.Id)) return entry;
            libraryMetadataQueue.Dequeue();
        }
        return null;
    }

    private void QueueLibraryMetadataRefresh()
    {
        if (!ShouldRefreshLibraryMetadataOrder) return;
        var timer = libraryMetadataRefreshTimer ??= CreateTimer(120, () =>
        {
            if (!closing && !libraryPreviewsPaused && LibrarySurface.IsVisible && ShouldRefreshLibraryMetadataOrder)
                FilterLibrary(preserveSelection: true);
        });
        if (!timer.IsEnabled) timer.Start();
    }

    private void EnsureLibraryPreviewWork()
    {
        if (closing || libraryPreviewsPaused || !LibrarySurface.IsVisible ||
            libraryPreviewWork != null || NextLibraryPreview() == null) return;

        libraryPreviewWork = new CancellationTokenSource();
        _ = RunLibraryPreviewWorkAsync(libraryPreviewWork);
    }

    private async Task RunLibraryPreviewWorkAsync(CancellationTokenSource work)
    {
        var ct = work.Token;
        try
        {
            while (true)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                ct.ThrowIfCancellationRequested();
                if (NextLibraryPreview() is not (var entry, var includeCover)) break;
                libraryCoverInProgress = includeCover ? entry.Id : null;
                try { await LoadLibraryPreviewAsync(entry, includeCover, ct); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    ct.ThrowIfCancellationRequested();
                    failedLibraryPreviews.Add(entry.Id);
                    Trace.TraceWarning("SeaReader library preview {0}: {1}", entry.Id, error.Message);
                }
                finally { libraryCoverInProgress = null; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            work.Dispose();
            libraryPreviewWork = null;
            EnsureLibraryPreviewWork();
        }
    }

    private async Task LoadLibraryPreviewAsync(BookEntry entry, bool includeCover, CancellationToken ct)
    {
        string cachePath = store.CoverCachePath(entry.Id);
        var previous = libraryMetadata.GetValueOrDefault(entry.Id);
        if (includeCover && previous?.HasCover == true && await LibraryThumbnail.LoadAsync(cachePath, ct) is { } cached)
        {
            ct.ThrowIfCancellationRequested();
            if (libraryPreviewTargets.ContainsKey(entry.Id)) entry.Display.SetCover(cached);
            return;
        }

        string path = store.SourceBookPath(entry.Id);
        string title = previous?.Title ?? "";
        string author = previous?.Author ?? "";
        byte[]? thumbnail = null;
        if (entry.Format == "epub")
        {
            try
            {
                var epubMetadata = await EpubDocument.ReadLibraryMetadataAsync(path, ct, includeCover);
                if (epubMetadata.Title.Length > 0) title = epubMetadata.Title;
                if (epubMetadata.Author.Length > 0) author = epubMetadata.Author;
                if (epubMetadata.Cover is { } cover) thumbnail = await LibraryThumbnail.FromEpubAsync(cover, ct);
            }
            catch (InvalidDataException) { includeCover = true; }
        }
        else thumbnail = await LibraryThumbnail.FromPdfAsync(path, ct);

        await store.SaveBookMetadataAsync(entry.Id, title, author, thumbnail, ct, coverChecked: includeCover,
            discardInvalidCover: includeCover && previous?.HasCover == true && thumbnail == null);
        ct.ThrowIfCancellationRequested();
        var metadata = new LibraryBookMetadata(title, author, thumbnail != null, includeCover);
        libraryMetadata[entry.Id] = metadata;
        if (entry.Display.Apply(metadata)) QueueLibraryMetadataRefresh();

        ct.ThrowIfCancellationRequested();
        if (thumbnail == null || !libraryPreviewTargets.ContainsKey(entry.Id)) return;
        var bitmap = await LibraryThumbnail.LoadAsync(cachePath, ct);
        ct.ThrowIfCancellationRequested();
        if (bitmap == null) failedLibraryPreviews.Add(entry.Id);
        else if (libraryPreviewTargets.ContainsKey(entry.Id)) entry.Display.SetCover(bitmap);
    }
}
