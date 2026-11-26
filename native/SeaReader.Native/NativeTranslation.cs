using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SeaReader.Native;

internal sealed class NativeTranslation(string dataDirectory)
{
    private static readonly SemaphoreSlim ModelJob = new(1);
    private const string ResultPrefix = "SEAREADER_RESULT ";

    internal static string DetectLanguage(string source)
    {
        if (source.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\uff66' and <= '\uff9f')) return "ja";
        return source.Any(c => c is >= '\u3400' and <= '\u9fff') ? "zh" : "en";
    }

    internal async Task<string> RunAsync(string source, string target, string text, bool install, CancellationToken token)
    {
        await ModelJob.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var worker = Path.GetFullPath(Environment.GetEnvironmentVariable("SEAREADER_NATIVE_WORKER_DIR")
                ?? Path.Combine(AppContext.BaseDirectory, "local-worker"));
            var runner = Path.Combine(worker, "node.exe");
            var script = Path.Combine(worker, "native", "worker", "translate.mjs");
            if (!File.Exists(runner) || !File.Exists(script))
                throw new InvalidOperationException("缺少离线翻译组件，请使用原生便携包启动。");

            var start = new ProcessStartInfo(runner)
            {
                WorkingDirectory = worker,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add(script);
            start.Environment.Remove("NODE_OPTIONS");
            start.Environment.Remove("NODE_PATH");
            token.ThrowIfCancellationRequested();
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动离线翻译。");
            using var cancellation = token.Register(() => Stop(process));
            var output = process.StandardOutput.ReadToEndAsync();
            var errorLog = process.StandardError.ReadToEndAsync();
            var cacheDirectory = Path.Combine(dataDirectory, "translation-models");
            try
            {
                var request = JsonSerializer.Serialize(new { source, target, text, install, cacheDirectory });
                await process.StandardInput.WriteAsync(request.AsMemory(), token).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                string reply = await output.ConfigureAwait(false);
                await errorLog.ConfigureAwait(false);
                var result = reply.Split('\n').LastOrDefault(line => line.StartsWith(ResultPrefix, StringComparison.Ordinal));
                if (result == null) throw new InvalidOperationException("离线翻译进程未返回结果。");
                using var json = JsonDocument.Parse(result[ResultPrefix.Length..]);
                if (json.RootElement.TryGetProperty("error", out var failure))
                    throw new InvalidOperationException(failure.GetString() ?? "离线翻译失败。");
                if (process.ExitCode != 0) throw new InvalidOperationException("离线翻译进程意外退出。");
                return json.RootElement.GetProperty("result").GetString() ?? "";
            }
            catch (IOException) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
            finally
            {
                Stop(process);
                if (install && token.IsCancellationRequested)
                {
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    RemovePartialDownloads(cacheDirectory, process.Id);
                }
            }
        }
        finally { ModelJob.Release(); }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void RemovePartialDownloads(string cacheDirectory, int processId)
    {
        if (!Directory.Exists(cacheDirectory)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(cacheDirectory, $"*.{processId}.*.part", SearchOption.AllDirectories))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
