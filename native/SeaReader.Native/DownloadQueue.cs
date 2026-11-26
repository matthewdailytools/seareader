using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SeaReader.Native;

internal sealed record DownloadEntry(string Id, string Url, string Name, string Status, string Error)
{
    public string StatusText => Status switch
    {
        "queued" => "等待", "running" => "下载中", "completed" => "完成",
        "failed" => "失败", "cancelled" => "已取消", _ => Status
    };
}

internal sealed partial class DownloadQueue
{
    private static readonly ConditionalWeakTable<LibraryStore, DownloadQueue> Instances = new();
    private const long MaximumSize = 512L * 1024 * 1024;
    private const string SelectFields = "SELECT id,url,name,status,COALESCE(error,'') FROM downloads";
    private readonly LibraryStore store;
    private readonly Lazy<Task> initialized;
    private readonly SemaphoreSlim worker = new(1);
    private readonly SemaphoreSlim state = new(1);
    private (string Id, CancellationTokenSource Cancellation)? active;
    private readonly CancellationTokenSource shuttingDown = new();

    private DownloadQueue(LibraryStore store)
    {
        this.store = store;
        initialized = new Lazy<Task>(() => Run(database =>
        {
            database.ExecuteScript("""
                CREATE TABLE IF NOT EXISTS downloads (
                    id TEXT PRIMARY KEY, url TEXT NOT NULL, name TEXT NOT NULL,
                    status TEXT NOT NULL, error TEXT, created_at INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS download_preferences (
                    id INTEGER PRIMARY KEY CHECK (id = 1), network TEXT NOT NULL);
                INSERT OR IGNORE INTO download_preferences VALUES (1, 'any');
                UPDATE downloads SET status='failed', error='下载已中断，可以重试'
                    WHERE status='running';
                """);
            return true;
        }));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shuttingDown.Cancel();
    }

    internal static DownloadQueue For(LibraryStore store) => Instances.GetValue(store, value => new DownloadQueue(value));

    internal async Task StartAsync()
    {
        await initialized.Value.ConfigureAwait(false);
        ScheduleWork();
    }

    internal async Task<IReadOnlyList<DownloadEntry>> ListAsync()
    {
        await initialized.Value.ConfigureAwait(false);
        return await Run(database => database.Read(SelectFields + " ORDER BY created_at DESC, id", ReadRow)).ConfigureAwait(false);
    }

    internal async Task<string> GetNetworkAsync()
    {
        await initialized.Value.ConfigureAwait(false);
        return await Run(Network).ConfigureAwait(false);
    }

    internal async Task SetNetworkAsync(string mode)
    {
        if (mode is not ("any" or "wifi")) throw new InvalidDataException("无效的下载网络偏好。");
        await initialized.Value.ConfigureAwait(false);
        await Run(database => { database.Execute("UPDATE download_preferences SET network=? WHERE id=1", mode); return true; })
            .ConfigureAwait(false);
    }

    internal async Task<string> EnqueueAsync(string address, string? extension)
    {
        string url = SubscriptionCatalog.ValidateUrl(address, allowFtp: true).AbsoluteUri;
        string name = DownloadName(new Uri(url), extension);
        await initialized.Value.ConfigureAwait(false);
        await state.WaitAsync().ConfigureAwait(false);
        string id;
        try
        {
            id = await Run(database =>
            {
                var previous = database.Read(SelectFields +
                    " WHERE url=? AND status IN ('queued','running','completed') ORDER BY created_at DESC LIMIT 1",
                    ReadRow, url).FirstOrDefault();
                if (previous is not null) return previous.Id;
                string nextId = Guid.NewGuid().ToString("D");
                database.Execute("""
                    INSERT INTO downloads(id,url,name,status,error,created_at) VALUES (?, ?, ?, 'queued', NULL, ?)
                    """, nextId, url, name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return nextId;
            }).ConfigureAwait(false);
        }
        finally { state.Release(); }
        ScheduleWork();
        return id;
    }

    internal async Task CancelAsync(string id)
    {
        await initialized.Value.ConfigureAwait(false);
        await state.WaitAsync().ConfigureAwait(false);
        try
        {
            if (active is { } current && current.Id == id)
            {
                current.Cancellation.Cancel();
                return;
            }
            await Run(database =>
            {
                database.Execute("UPDATE downloads SET status='cancelled', error=NULL WHERE id=? AND status='queued'", id);
                return true;
            }).ConfigureAwait(false);
        }
        finally { state.Release(); }
    }

    internal async Task RetryAsync(string id)
    {
        await initialized.Value.ConfigureAwait(false);
        await state.WaitAsync().ConfigureAwait(false);
        try
        {
            await Run(database =>
            {
                database.Execute("""
                    UPDATE downloads SET status='queued', error=NULL WHERE id=? AND status IN ('failed','cancelled')
                    """, id);
                return true;
            }).ConfigureAwait(false);
        }
        finally { state.Release(); }
        ScheduleWork();
    }

    internal async Task RemoveAsync(string id)
    {
        await initialized.Value.ConfigureAwait(false);
        await state.WaitAsync().ConfigureAwait(false);
        try
        {
            await Run(database =>
            {
                var job = Get(database, id);
                if (job.Status is "queued" or "running") throw new InvalidOperationException("请先取消下载。");
                string path = JobPath(job);
                string directory = JobDirectory(id);
                if (Directory.Exists(directory))
                {
                    File.Delete(path);
                    File.Delete(Path.Combine(directory, "partial"));
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                }
                database.Execute("DELETE FROM downloads WHERE id=?", id);
                return true;
            }).ConfigureAwait(false);
        }
        finally { state.Release(); }
    }

    internal async Task ClearCompletedAsync()
    {
        foreach (var job in await ListAsync().ConfigureAwait(false))
        {
            if (job.Status == "completed") await RemoveAsync(job.Id).ConfigureAwait(false);
        }
    }

    internal async Task<string> CompletedPathAsync(string id, bool wait, CancellationToken ct)
    {
        await initialized.Value.ConfigureAwait(false);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var job = await Run(database => Get(database, id)).ConfigureAwait(false);
            if (job.Status == "completed")
            {
                var path = JobPath(job);
                if (!File.Exists(path)) throw new FileNotFoundException("找不到下载文件。", path);
                return path;
            }
            if (!wait || job.Status is not ("queued" or "running"))
                throw new InvalidOperationException(job.Error.Length != 0 ? job.Error :
                    job.Status == "cancelled" ? "下载已取消。" : "下载尚未完成。");
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    internal async Task<BookEntry> ImportAsync(string id, bool wait, CancellationToken ct)
    {
        await CompletedPathAsync(id, wait, ct).ConfigureAwait(false);
        await state.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string path = await CompletedPathAsync(id, false, ct).ConfigureAwait(false);
            return await store.ImportAsync(path, ct).ConfigureAwait(false);
        }
        finally { state.Release(); }
    }

    private void ScheduleWork() => _ = Task.Run(DrainAsync);

    private async Task DrainAsync()
    {
        await worker.WaitAsync().ConfigureAwait(false);
        try
        {
            while (!shuttingDown.IsCancellationRequested)
            {
                DownloadEntry? job;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shuttingDown.Token);
                using (cancellation)
                {
                    await state.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        job = await Run(database =>
                        {
                            var next = database.Read(SelectFields + " WHERE status='queued' ORDER BY created_at, id LIMIT 1",
                                ReadRow).FirstOrDefault();
                            if (next is not null)
                                database.Execute("UPDATE downloads SET status='running', error=NULL WHERE id=?", next.Id);
                            return next;
                        }).ConfigureAwait(false);
                        if (job is null) break;
                        active = (job.Id, cancellation);
                    }
                    finally { state.Release(); }
                    await TransferAsync(job, cancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) { Trace.WriteLine(error); }
        finally { worker.Release(); }
    }

    private async Task TransferAsync(DownloadEntry job, CancellationToken ct)
    {
        string? partial = null;
        string? failure = null;
        string finalName = job.Name;
        try
        {
            var directory = JobDirectory(job.Id);
            Directory.CreateDirectory(directory);
            partial = Path.Combine(directory, "partial");
            var mode = await Run(Network).ConfigureAwait(false);
            var wifi = mode == "wifi" ? AvailableWifi() : null;
            if (mode == "wifi" && wifi is null)
                throw new IOException("当前没有可用的 Wi-Fi 下载连接。连接 Wi-Fi 后可以重试。");
            var mime = await FetchFileAsync(SubscriptionCatalog.ValidateUrl(job.Url, true), partial, wifi, ct).ConfigureAwait(false);
            string format = await BookFormats.DetectFileAsync(partial, ct).ConfigureAwait(false);
            string? expected = SubscriptionCatalog.FormatForName(job.Name);
            if (expected != "html" && (format == "html" || (format == "txt" && SubscriptionCatalog.FormatForMime(mime) == "html")))
                throw new InvalidDataException("下载地址返回了网页。若网页就是要阅读的文档，请在下载格式中选择 HTML。");
            finalName = ApplyFormat(job.Name, format);
        }
        catch (Exception error) { failure = error.Message; }

        await state.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ct.IsCancellationRequested)
            {
                await SetStatusAsync(job.Id, "cancelled", "").ConfigureAwait(false);
                return;
            }
            if (failure is null && partial is not null)
            {
                try { File.Move(partial, JobPath(job with { Name = finalName }), overwrite: true); }
                catch (IOException error) { failure = error.Message; }
                catch (UnauthorizedAccessException error) { failure = error.Message; }
                catch (InvalidDataException error) { failure = error.Message; }
            }
            if (failure is null)
            {
                await Run(database =>
                {
                    database.Execute("UPDATE downloads SET status='completed', error=NULL, name=? WHERE id=?", finalName, job.Id);
                    return true;
                }).ConfigureAwait(false);
            }
            else await SetStatusAsync(job.Id, "failed", failure).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (partial is not null) File.Delete(partial);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            active = null;
            state.Release();
        }
    }

    private Task SetStatusAsync(string id, string status, string error) => Run(database =>
    {
        database.Execute("UPDATE downloads SET status=?, error=NULLIF(?, '') WHERE id=?", status, error, id);
        return true;
    });

    private Task<T> Run<T>(Func<LibraryDatabase, T> operation) => Task.Run(() =>
    {
        using var database = new LibraryDatabase(Path.Combine(store.DataDirectory, "library.sqlite"));
        return operation(database);
    });

    private static string Network(LibraryDatabase database) =>
        database.Read("SELECT network FROM download_preferences WHERE id=1", row => row.Text(0)).Single();

    private static DownloadEntry Get(LibraryDatabase database, string id) =>
        database.Read(SelectFields + " WHERE id=?", ReadRow, id).FirstOrDefault() ?? throw new InvalidDataException("找不到下载任务。");

    private static DownloadEntry ReadRow(LibraryRow row) => new(row.Text(0), row.Text(1), row.Text(2), row.Text(3), row.Text(4));

    private string JobDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new InvalidDataException("无效的下载任务。");
        return Path.Combine(store.DataDirectory, "downloads", id);
    }

    private string JobPath(DownloadEntry job)
    {
        if (!IsSafeName(job.Name)) throw new InvalidDataException("下载文件名无效。");
        return Path.Combine(JobDirectory(job.Id), job.Name);
    }

    private static string DownloadName(Uri url, string? extension)
    {
        var raw = Uri.UnescapeDataString(url.Segments.LastOrDefault()?.TrimEnd('/') ?? "");
        var name = new string(raw.Select(value => Path.GetInvalidFileNameChars().Contains(value) ? '_' : value).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || ReservedName().IsMatch(name)) name = "download-" + (name.Length == 0 ? "file" : name);
        if (!IsSafeName(name)) throw new InvalidDataException("下载文件名无效。");
        return extension is "txt" or "pdf" || (extension is not null && BookFormats.IsChapterBook(extension))
            ? ApplyFormat(name, extension) : name;
    }

    private static string ApplyFormat(string name, string format)
    {
        string? current = SubscriptionCatalog.FormatForName(name);
        if (current == format) return name;
        if (current is not null || Path.GetExtension(name).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);
        if (name.Length == 0) name = "download-file";
        int length = Math.Min(name.Length, 159 - format.Length);
        if (char.IsHighSurrogate(name[length - 1])) length--;
        string title = name[..length].TrimEnd('.', ' ');
        return (title.Length == 0 ? "download-file" : title) + "." + format;
    }

    private static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 160 && !name.Equals("partial", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith('.') && !name.EndsWith(' ') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !ReservedName().IsMatch(name);

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();

    private static IPAddress? AvailableWifi()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 || network.OperationalStatus != OperationalStatus.Up)
                continue;
            var properties = network.GetIPProperties();
            if (properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
                return properties.UnicastAddresses.FirstOrDefault(address =>
                    address.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
        }
        return null;
    }

    private static async Task<string> FetchFileAsync(Uri url, string path, IPAddress? wifi, CancellationToken ct)
    {
        var options = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "curl.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string[] flags =
        [
            "--disable", "--globoff", "--silent", "--show-error", "--fail", "--location",
            "--max-redirs", "5", "--connect-timeout", "15", "--max-time", "600",
            "--max-filesize", MaximumSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--proto", "=http,https,ftp", "--proto-redir", "=http,https,ftp",
            "--write-out", "%{stderr}%{content_type}"
        ];
        foreach (var flag in flags) options.ArgumentList.Add(flag);
        if (wifi is not null)
        {
            options.ArgumentList.Add("--interface");
            options.ArgumentList.Add(wifi.ToString());
        }
        options.ArgumentList.Add("--");
        options.ArgumentList.Add(url.AbsoluteUri);
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(options) ?? throw new IOException("无法启动文件下载。");
        using var cancellation = ct.Register(() => Kill(process));
        var errors = process.StandardError.ReadToEndAsync();
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            var source = process.StandardOutput.BaseStream;
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
            {
                if (file.Length + count > MaximumSize) throw new IOException("下载文件超过 512 MB。");
                await file.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var contentType = (await errors.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0) throw new IOException($"下载失败，请检查地址和网络。（{process.ExitCode}）");
            if (file.Length == 0) throw new IOException("下载文件为空。");
            return contentType;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Kill(process);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
