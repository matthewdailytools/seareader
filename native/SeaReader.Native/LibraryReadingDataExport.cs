using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace SeaReader.Native;

internal static class LibraryReadingDataExport
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    internal static Task WriteJsonAsync(string path, LibraryReadingData data, CancellationToken token) =>
        AtomicExportFile.WriteAsync(path, "reading-data",
            (stream, cancellationToken) => JsonSerializer.SerializeAsync(stream, data, ExportJson, cancellationToken), token);
}
