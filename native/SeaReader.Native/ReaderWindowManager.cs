using System.Windows;

namespace SeaReader.Native;

internal sealed class ReaderWindowManager(LibraryStore store)
{
    private sealed class ReaderRegistration(MainWindow window)
    {
        internal MainWindow Window { get; } = window;
        internal TaskCompletionSource<bool> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<string, ReaderRegistration> readers = new(StringComparer.Ordinal);
    private MainWindow? library;
    private bool libraryNeedsRefresh;

    internal MainWindow EnsureLibrary()
    {
        if (library is { IsClosingWindow: false } current) return current;

        var window = new MainWindow(store, []) { ShowInTaskbar = true };
        window.AttachReadingWindows(this);
        library = window;
        libraryNeedsRefresh = false;
        Application.Current.MainWindow = window;
        window.Activated += (_, _) => RefreshLibraryIfActive();
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(library, window)) return;
            library = null;
            if (ReferenceEquals(Application.Current.MainWindow, window)) Application.Current.MainWindow = null;
        };
        window.Show();
        return window;
    }

    internal MainWindow ShowLibrary()
    {
        var window = EnsureLibrary();
        Activate(window);
        return window;
    }

    internal async Task<bool> OpenReaderAsync(BookEntry book)
    {
        while (readers.TryGetValue(book.Id, out var known))
        {
            if (known.Window.IsClosingWindow)
            {
                await known.Closed.Task;
                if (Application.Current.Dispatcher.HasShutdownStarted) return false;
                continue;
            }
            Activate(known.Window);
            var alreadyOpened = await known.Opened.Task;
            if (alreadyOpened && await known.Window.RestoreContinueEntryAsync(book)) NotifyReaderPositionSaved();
            return alreadyOpened;
        }

        var window = new MainWindow(store, []) { ShowInTaskbar = true };
        window.AttachReadingWindows(this, book);
        var reader = new ReaderRegistration(window);
        readers.Add(book.Id, reader);
        window.Closed += (_, _) =>
        {
            readers.Remove(book.Id);
            reader.Opened.TrySetResult(false);
            reader.Closed.SetResult();
            NotifyReaderPositionSaved();
        };

        var opened = false;
        try
        {
            await window.PrepareReaderWindowPlacementAsync(book, library);
            if (!window.IsClosingWindow)
            {
                window.Show();
                opened = await window.OpenDedicatedBookAsync(book);
                if (opened)
                    NotifyReaderPositionSaved();
                else if (!window.IsClosingWindow)
                    CloseFailedReader(window, window.ReaderOpenFailure);
            }
        }
        catch (Exception error)
        {
            if (!window.IsClosingWindow) CloseFailedReader(window, error.Message);
        }
        finally
        {
            reader.Opened.TrySetResult(opened && !window.IsClosingWindow);
        }
        return await reader.Opened.Task;
    }

    internal void ApplySharedReadingGoalsSettings(MainWindow origin, ReadingGoalsSettings settings)
    {
        foreach (var reader in readers.Values)
        {
            var window = reader.Window;
            if (!ReferenceEquals(window, origin) && !window.IsClosingWindow)
                window.ApplySharedReadingGoalsSettings(settings);
        }
    }

    internal Task FlushReadersForExportAsync() =>
        Task.WhenAll(readers.Values.Select(reader => reader.Window.FlushReadingDataForExportAsync()));

    internal Task RefreshReadersAfterDataRestoreAsync() => Task.WhenAll(readers.Values
        .Where(reader => !reader.Window.IsClosingWindow)
        .Select(reader => reader.Window.RefreshReadingDataAfterRestoreAsync()));

    internal void NotifyReaderPositionSaved()
    {
        libraryNeedsRefresh = true;
        RefreshLibraryIfActive();
    }

    private void CloseFailedReader(MainWindow window, string message)
    {
        ShowLibrary().ShowLibraryFailure(string.IsNullOrWhiteSpace(message) ? "无法打开这本书。" : message);
        window.Close();
    }

    private void RefreshLibraryIfActive()
    {
        if (!libraryNeedsRefresh || library is not { IsClosingWindow: false, IsLoaded: true, IsActive: true } current)
            return;
        libraryNeedsRefresh = false;
        _ = current.RefreshSharedLibraryAsync();
    }

    private static void Activate(MainWindow window)
    {
        if (!window.IsVisible) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
