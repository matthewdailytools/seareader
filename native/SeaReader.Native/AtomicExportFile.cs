using System.IO;

namespace SeaReader.Native;

internal static class AtomicExportFile
{
    internal static async Task WriteAsync(string path, string kind, Func<Stream, CancellationToken, Task> write, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination) ?? throw new ArgumentException("请选择文件路径。", nameof(path));
        var temporary = Path.Combine(directory, $".seareader-{kind}-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous))
            {
                created = true;
                await write(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
