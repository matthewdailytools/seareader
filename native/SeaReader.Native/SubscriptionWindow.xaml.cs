using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class SubscriptionWindow : Window
{
    internal void SelectSection(bool downloads) => Sections.SelectedItem = downloads ? DownloadsTab : SubscriptionsTab;

    private readonly SubscriptionCatalog subscriptions;
    private readonly DownloadQueue queue;
    private readonly Func<string, Task> openBook;
    private readonly Func<string, Task>? previewBook;
    private readonly Func<Task>? refreshLibrary;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly SemaphoreSlim downloadRefresh = new(1);
    private readonly Stack<FeedCatalog> previousCatalogs = new();
    private FeedCatalog? currentCatalog;
    private bool refreshingFeeds;
    private bool settingFeedList;
    private bool changingQueue;
    private bool downloadActionPending;
    private bool closed;

    public SubscriptionWindow(LibraryStore store, Func<string, Task> openBook, Func<string, Task>? previewBook = null,
        Func<Task>? refreshLibrary = null)
    {
        subscriptions = new SubscriptionCatalog(store);
        queue = DownloadQueue.For(store);
        this.openBook = openBook;
        this.previewBook = previewBook;
        this.refreshLibrary = refreshLibrary;
        InitializeComponent();
        UpdateFeedControls();
        UpdateDownloadControls();
        Loaded += OnLoaded;
        Closed += OnClosed;
        timer.Tick += OnTimer;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await Task.WhenAll(RunFeedAsync(async () => await ReloadFeedsAsync()),
            RunDownloadAsync(async () =>
            {
                await queue.StartAsync();
                WifiOnly.IsChecked = await queue.GetNetworkAsync() == "wifi";
                await LoadDownloadsAsync();
                if (!closed) timer.Start();
            }));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        timer.Stop();
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (previousCatalogs.TryPop(out var previous)) ShowCatalog(previous);
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (currentCatalog?.Next is { } next) await ReadLinkedCatalogAsync(next);
    }

    private async void OnSubscribe(object sender, RoutedEventArgs e) => await SubscribeAsync();

    private async void OnFeedAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SubscribeAsync();
    }

    private Task SubscribeAsync() => RunFeedAsync(async () =>
    {
        FeedStatus.Text = "正在读取书源…";
        var feed = await subscriptions.SubscribeAsync(FeedAddress.Text, lifetime.Token);
        await ReloadFeedsAsync(feed.Url);
        if (closed) return;
        FeedAddress.Clear();
        FeedStatus.Text = "已订阅。";
    });

    private async void OnRefreshFeed(object sender, RoutedEventArgs e)
    {
        if (FeedList.SelectedItem is not SavedSubscription selected) return;
        await RunFeedAsync(async () =>
        {
            FeedStatus.Text = "正在刷新…";
            var updated = await subscriptions.SubscribeAsync(selected.Url, lifetime.Token);
            await ReloadFeedsAsync(updated.Url);
            if (!closed) FeedStatus.Text = "已刷新。";
        });
    }

    private async void OnRefreshAll(object sender, RoutedEventArgs e)
    {
        if (FeedList.ItemsSource is not IReadOnlyList<SavedSubscription> feeds) return;
        await RunFeedAsync(async () =>
        {
            FeedStatus.Text = "正在刷新订阅…";
            var failures = new List<string>();
            var token = lifetime.Token;
            var previouslySelected = (FeedList.SelectedItem as SavedSubscription)?.Url;
            foreach (var feed in feeds)
            {
                try { await subscriptions.SubscribeAsync(feed.Url, token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{feed.Title}（{ex.Message}）");
                }
            }
            await ReloadFeedsAsync(previouslySelected);
            if (closed) return;
            FeedStatus.Text = failures.Count == 0 ? "已刷新全部订阅。" :
                $"有 {failures.Count} 个书源刷新失败。{failures[0]}";
        });
    }

    private async void OnRemoveFeed(object sender, RoutedEventArgs e)
    {
        if (FeedList.SelectedItem is not SavedSubscription selected) return;
        await RunFeedAsync(async () =>
        {
            await subscriptions.RemoveAsync(selected.Url);
            await ReloadFeedsAsync();
            if (!closed) FeedStatus.Text = "已取消订阅。";
        });
    }

    private async Task ReloadFeedsAsync(string? selectAddress = null)
    {
        var feeds = await subscriptions.ListAsync();
        if (closed) return;
        settingFeedList = true;
        try
        {
            FeedList.ItemsSource = feeds;
            FeedList.SelectedItem = feeds.FirstOrDefault(feed => feed.Url == selectAddress) ?? feeds.FirstOrDefault();
        }
        finally { settingFeedList = false; }
        ShowSelectedFeed();
    }

    private void OnFeedSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!settingFeedList) ShowSelectedFeed();
    }

    private void ShowSelectedFeed()
    {
        previousCatalogs.Clear();
        FeedStatus.Text = "";
        try
        {
            ShowCatalog(FeedList.SelectedItem is SavedSubscription feed ? subscriptions.ReadSaved(feed) : null);
        }
        catch (Exception ex)
        {
            ShowCatalog(null);
            FeedStatus.Text = ex.Message;
        }
    }

    private void ShowCatalog(FeedCatalog? catalog)
    {
        currentCatalog = catalog;
        CatalogTitle.Text = catalog?.Title ?? "书源目录";
        CatalogList.ItemsSource = catalog?.Entries;
        if (CatalogList.Items.Count > 0) CatalogList.ScrollIntoView(CatalogList.Items[0]);
        UpdateFeedControls();
    }

    private Task ReadLinkedCatalogAsync(string url) => RunFeedAsync(async () =>
    {
        FeedStatus.Text = "正在读取目录…";
        var catalog = await SubscriptionCatalog.LoadAsync(url, lifetime.Token);
        if (closed) return;
        if (currentCatalog is { } current) previousCatalogs.Push(current);
        ShowCatalog(catalog);
        FeedStatus.Text = catalog.Entries.Count == 0 ? "这个目录没有条目。" : "";
    });

    private async void OnFeedLink(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FeedLink link }) return;
        switch (link.Action)
        {
            case "catalog":
                await ReadLinkedCatalogAsync(link.Url);
                break;
            case "web":
                try { Process.Start(new ProcessStartInfo(SubscriptionCatalog.ValidateUrl(link.Url).AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) { FeedStatus.Text = ex.Message; }
                break;
            case "download":
                await QueueFromFeedAsync(link, null, "已加入下载队列。");
                break;
        }
    }

    private async void OnFeedPreview(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FeedLink link })
            await QueueFromFeedAsync(link, async (id, token) =>
            {
                await ShowPreviewAsync(id, true, token);
                if (!closed) DownloadStatus.Text = "";
            }, "下载完成后会打开临时阅读，可在队列中取消下载。");
    }

    private async void OnFeedImport(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FeedLink link })
            await QueueFromFeedAsync(link, async (id, token) =>
            {
                await queue.CompletedPathAsync(id, true, token);
                var book = await ImportAsync(id, token);
                if (!closed) DownloadStatus.Text = $"已将《{book.Title}》加入书库。";
            }, "下载完成后会加入书库，可在队列中取消下载。");
    }

    private async Task QueueFromFeedAsync(FeedLink link, Func<string, CancellationToken, Task>? afterDownload, string message)
    {
        if (closed || (afterDownload is not null && downloadActionPending)) return;
        if (afterDownload is not null) downloadActionPending = true;
        var token = lifetime.Token;
        bool showingDownloads = false;
        try
        {
            var id = await queue.EnqueueAsync(link.Url, link.Extension);
            token.ThrowIfCancellationRequested();
            Sections.SelectedItem = DownloadsTab;
            showingDownloads = true;
            DownloadStatus.Text = message;
            await LoadDownloadsAsync(id);
            if (afterDownload is not null) await afterDownload(id, token);
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            if (!closed) (showingDownloads ? DownloadStatus : FeedStatus).Text = ex.Message;
        }
        finally { if (afterDownload is not null) downloadActionPending = false; }
    }

    private async void OnAddDownload(object sender, RoutedEventArgs e) => await AddDownloadAsync();

    private async void OnDownloadAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddDownloadAsync();
    }

    private Task AddDownloadAsync() => RunDownloadAsync(async () =>
    {
        var id = await queue.EnqueueAsync(DownloadAddress.Text, DownloadFormat.SelectedValue as string);
        if (closed) return;
        DownloadAddress.Clear();
        DownloadStatus.Text = "已加入下载队列。";
        await LoadDownloadsAsync(id);
    });

    private async void OnNetworkChanged(object sender, RoutedEventArgs e) => await RunDownloadAsync(async () =>
    {
        await queue.SetNetworkAsync(WifiOnly.IsChecked == true ? "wifi" : "any");
        if (!closed) DownloadStatus.Text = WifiOnly.IsChecked == true ? "接下来的下载仅使用 Wi-Fi。" : "接下来的下载可使用任意网络。";
    });

    private async void OnTimer(object? sender, EventArgs e)
    {
        if (changingQueue || downloadRefresh.CurrentCount == 0 || !ReferenceEquals(Sections.SelectedItem, DownloadsTab)) return;
        try { await LoadDownloadsAsync(); }
        catch (Exception ex) { if (!closed) DownloadStatus.Text = ex.Message; }
    }

    private async Task LoadDownloadsAsync(string? selectId = null)
    {
        await downloadRefresh.WaitAsync();
        try
        {
            var jobs = await queue.ListAsync();
            if (closed) return;
            var selected = selectId ?? (DownloadList.SelectedItem as DownloadEntry)?.Id;
            if (DownloadList.ItemsSource is not IEnumerable<DownloadEntry> existing || !existing.SequenceEqual(jobs))
                DownloadList.ItemsSource = jobs;
            if (selected is not null) DownloadList.SelectedItem = jobs.FirstOrDefault(job => job.Id == selected);
            UpdateDownloadControls();
        }
        finally { downloadRefresh.Release(); }
    }

    private void OnDownloadSelected(object sender, SelectionChangedEventArgs e) => UpdateDownloadControls();

    private async void OnPreviewDownload(object sender, RoutedEventArgs e) =>
        await WithSelectedAsync(async job => await ShowPreviewAsync(job.Id, false, lifetime.Token));

    private async void OnImportDownload(object sender, RoutedEventArgs e) => await WithSelectedAsync(async job =>
    {
        var book = await ImportAsync(job.Id, lifetime.Token);
        if (!closed) DownloadStatus.Text = $"已将《{book.Title}》加入书库。";
    });

    private async void OnOpenDownload(object sender, RoutedEventArgs e) => await WithSelectedAsync(async job =>
    {
        var book = await ImportAsync(job.Id, lifetime.Token);
        if (closed) return;
        await openBook(book.Id);
        if (!closed) Close();
    });

    private async void OnCancelDownload(object sender, RoutedEventArgs e) =>
        await WithSelectedAsync(async job => { await queue.CancelAsync(job.Id); await LoadDownloadsAsync(); });

    private async void OnRetryDownload(object sender, RoutedEventArgs e) =>
        await WithSelectedAsync(async job => { await queue.RetryAsync(job.Id); await LoadDownloadsAsync(); });

    private async void OnRemoveDownload(object sender, RoutedEventArgs e) =>
        await WithSelectedAsync(async job => { await queue.RemoveAsync(job.Id); await LoadDownloadsAsync(); });

    private async void OnClearDownloads(object sender, RoutedEventArgs e) => await RunDownloadAsync(async () =>
    {
        if (MessageBox.Show(this, "清除已完成的下载文件？已加入书库的书籍仍可阅读。", "清除已完成",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await queue.ClearCompletedAsync();
        await LoadDownloadsAsync();
    });

    private async Task ShowPreviewAsync(string id, bool wait, CancellationToken token)
    {
        string path = await queue.CompletedPathAsync(id, wait, token);
        token.ThrowIfCancellationRequested();
        if (previewBook is not null)
        {
            await previewBook(path);
            return;
        }
        var preview = new DownloadPreviewWindow(path, async ct => await ImportAsync(id, ct)) { Owner = this };
        preview.ShowDialog();
    }

    private async Task<BookEntry> ImportAsync(string id, CancellationToken ct)
    {
        var book = await queue.ImportAsync(id, false, ct);
        if (refreshLibrary is not null) await refreshLibrary();
        return book;
    }

    private Task WithSelectedAsync(Func<DownloadEntry, Task> action) =>
        DownloadList.SelectedItem is DownloadEntry job ? RunDownloadAsync(() => action(job)) : Task.CompletedTask;

    private async Task RunFeedAsync(Func<Task> action)
    {
        if (refreshingFeeds || closed) return;
        refreshingFeeds = true;
        UpdateFeedControls();
        try { await action(); }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex) { if (!closed) FeedStatus.Text = ex.Message; }
        finally
        {
            refreshingFeeds = false;
            if (!closed) UpdateFeedControls();
        }
    }

    private async Task RunDownloadAsync(Func<Task> action)
    {
        if (changingQueue || closed) return;
        changingQueue = true;
        UpdateDownloadControls();
        try { await action(); }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex) { if (!closed) DownloadStatus.Text = ex.Message; }
        finally
        {
            changingQueue = false;
            if (!closed) UpdateDownloadControls();
        }
    }

    private void UpdateFeedControls()
    {
        FeedForm.IsEnabled = FeedBrowse.IsEnabled = !refreshingFeeds;
        RefreshFeedButton.IsEnabled = RemoveFeedButton.IsEnabled = FeedList.SelectedItem is SavedSubscription;
        RefreshAllButton.IsEnabled = FeedList.HasItems;
        BackButton.IsEnabled = previousCatalogs.Count != 0;
        NextButton.IsEnabled = currentCatalog?.Next is not null;
    }

    private void UpdateDownloadControls()
    {
        if (ClearButton is null) return;
        var status = (DownloadList.SelectedItem as DownloadEntry)?.Status;
        DownloadForm.IsEnabled = WifiOnly.IsEnabled = !changingQueue;
        PreviewButton.IsEnabled = ImportButton.IsEnabled = OpenButton.IsEnabled = !changingQueue && status == "completed";
        CancelButton.IsEnabled = !changingQueue && status is "queued" or "running";
        RetryButton.IsEnabled = !changingQueue && status is "failed" or "cancelled";
        RemoveButton.IsEnabled = !changingQueue && status is "completed" or "failed" or "cancelled";
        ClearButton.IsEnabled = !changingQueue && DownloadList.Items.OfType<DownloadEntry>().Any(job => job.Status == "completed");
    }
}
