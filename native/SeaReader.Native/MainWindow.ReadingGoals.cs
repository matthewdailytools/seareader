using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private readonly ReadingSessionTimer readingSessionTimer = new(TimeProvider.System);
    private ReadingGoalsSettings readingGoalsSettings = new();
    private Task readingGoalsWrite = Task.CompletedTask;
    private DispatcherTimer readingGoalsTimer = null!;
    private bool openingReadingGoals;
    private bool readingGoalsOpeningBook;
    private bool readingGoalsSettingsLoaded;
    private bool savingReadingGoals;
    private bool watchingReadingGoalsSession;
    private bool readingGoalsSessionAvailable = true;
    private int readingGoalsTicks;

    private void InitializeReadingGoals()
    {
        readingGoalsTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        readingGoalsTimer.Tick += OnReadingGoalsTick;
        Activated += (_, _) => RefreshReadingGoalsTracking();
        Deactivated += (_, _) => RefreshReadingGoalsTracking();
        StateChanged += (_, _) => RefreshReadingGoalsTracking();
        ReaderSurface.IsVisibleChanged += (_, _) => RefreshReadingGoalsTracking();
        ReaderSurface.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnReadingGoalsActivity), true);
        ReaderSurface.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnReadingGoalsActivity), true);
        ReaderSurface.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnReadingGoalsActivity), true);
        ReaderSurface.AddHandler(PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnReadingGoalsActivity), true);
        ReaderSurface.PreviewMouseMove += OnReadingGoalsActivity;
    }

    private async Task LoadReadingGoalsSettingsAsync()
    {
        try
        {
            var settings = await store.LoadReadingGoalsSettingsAsync();
            if (!readingGoalsSettingsLoaded && !closing) ApplyReadingGoalsSettings(settings);
        }
        catch (Exception error) { Trace.TraceWarning($"Unable to load reading goals: {error.Message}"); }
    }

    private static DateOnly ReadingGoalsToday() => DateOnly.FromDateTime(DateTime.Now);

    internal void ApplySharedReadingGoalsSettings(ReadingGoalsSettings settings)
    {
        if (!closing) ApplyReadingGoalsSettings(settings);
    }

    private void ApplyReadingGoalsSettings(ReadingGoalsSettings settings)
    {
        if (settings.Enabled != watchingReadingGoalsSession)
        {
            if (settings.Enabled) SystemEvents.SessionSwitch += OnReadingGoalsSessionSwitch;
            else SystemEvents.SessionSwitch -= OnReadingGoalsSessionSwitch;
            watchingReadingGoalsSession = settings.Enabled;
            readingGoalsSessionAvailable = true;
        }
        readingGoalsSettings = settings;
        readingGoalsSettingsLoaded = true;
        RefreshReadingGoalsTracking();
    }

    private void OnReadingGoalsSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var available = e.Reason switch
        {
            SessionSwitchReason.SessionLock or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect => false,
            SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect => true,
            _ => (bool?)null
        };
        if (available is not { } connected) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (closing || !watchingReadingGoalsSession) return;
            readingGoalsSessionAvailable = connected;
            RefreshReadingGoalsTracking();
        }, DispatcherPriority.Send);
    }

    private void OnReadingGoalsActivity(object? sender, RoutedEventArgs e) => readingSessionTimer.MarkInteraction();

    private void OnReadingGoalsTick(object? sender, EventArgs e)
    {
        RefreshReadingGoalsTracking();
        if (!readingGoalsTimer.IsEnabled || ++readingGoalsTicks < 10) return;
        readingGoalsTicks = 0;
        _ = FlushReadingGoalTimeAsync();
    }

    private void RefreshReadingGoalsTracking()
    {
        var shouldCount = readingGoalsSettings.Enabled && readingGoalsSessionAvailable && IsActive &&
            WindowState != WindowState.Minimized && ReaderSurface.IsVisible && !closing && !readingGoalsOpeningBook && book != null &&
            !savingReadingGoals &&
            (text != null || pdf != null && readingGoalsSettings.IncludePdf && PdfPageImage.Source != null);
        readingSessionTimer.Sample(shouldCount, readingGoalsSettings.DailyMinutes);
        if (shouldCount) readingGoalsTimer.Start();
        else if (readingGoalsTimer.IsEnabled)
        {
            readingGoalsTimer.Stop();
            readingGoalsTicks = 0;
            _ = FlushReadingGoalTimeAsync();
        }
    }

    private Task FlushReadingGoalTimeAsync()
    {
        var durations = readingSessionTimer.TakePending();
        if (durations.Length != 0)
            readingGoalsWrite = WriteReadingGoalTimeAsync(readingGoalsWrite, durations);
        return readingGoalsWrite;
    }

    private async Task WriteReadingGoalTimeAsync(Task prior, ReadingGoalDay[] durations)
    {
        await prior;
        try { await store.AddReadingGoalTimeAsync(durations); }
        catch (Exception error)
        {
            readingSessionTimer.ReturnPending(durations);
            Trace.TraceWarning($"Unable to save reading time: {error.Message}");
        }
    }

    private async Task FlushReadingGoalTimeWithRetryAsync()
    {
        await FlushReadingGoalTimeAsync();
        // A failed write returns its batch to the timer.
        await FlushReadingGoalTimeAsync();
    }

    private async void OnShowReadingGoals(object sender, RoutedEventArgs e)
    {
        if (closing || openingReadingGoals) return;
        openingReadingGoals = true;
        try
        {
            await FlushReadingGoalTimeWithRetryAsync();
            var snapshot = await store.GetReadingGoalsSnapshotAsync(ReadingGoalsToday());
            if (closing || !LibrarySurface.IsVisible) return;
            ApplyReadingGoalsSettings(snapshot.Settings);
            var goals = new ReadingGoalsWindow(snapshot, this);
            if (goals.ShowDialog() != true || goals.Settings is not { } settings || settings == snapshot.Settings) return;
            savingReadingGoals = true;
            RefreshReadingGoalsTracking();
            readingGoalsWrite = SaveReadingGoalsAfterAsync(readingGoalsWrite, settings);
            await readingGoalsWrite;
        }
        catch (Exception error) { if (!closing) SetLibraryStatus(error.Message); }
        finally
        {
            openingReadingGoals = false;
            if (savingReadingGoals)
            {
                savingReadingGoals = false;
                RefreshReadingGoalsTracking();
            }
        }
    }

    private async Task SaveReadingGoalsAfterAsync(Task prior, ReadingGoalsSettings settings)
    {
        await prior;
        try
        {
            await store.SaveReadingGoalsSettingsAsync(settings, ReadingGoalsToday());
            if (!closing) ApplyReadingGoalsSettings(settings);
            readingWindows?.ApplySharedReadingGoalsSettings(this, settings);
        }
        catch (Exception error) { if (!closing) SetLibraryStatus(error.Message); }
    }

    private async Task FinishReadingGoalsAsync()
    {
        RefreshReadingGoalsTracking();
        if (watchingReadingGoalsSession)
        {
            SystemEvents.SessionSwitch -= OnReadingGoalsSessionSwitch;
            watchingReadingGoalsSession = false;
        }
        await FlushReadingGoalTimeWithRetryAsync();
    }
}
