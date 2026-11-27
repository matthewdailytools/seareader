using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SeaReader.Native;

internal interface IChapterBook
{
    IReadOnlyList<EpubChapter> Chapters { get; }
    Task<string> ReadChapterAsync(int index, CancellationToken ct = default);
    int GetChapterForPosition(string? position);
}

internal static partial class BookFormats
{
    internal const string FileFilter = "电子书|*.txt;*.epub;*.pdf;*.fb2;*.zip;*.htm;*.html;*.xhtml;*.docx|所有文件|*.*";

    internal static bool IsChapterBook(string format) => format is "epub" or "fb2" or "html" or "docx";

    internal static async Task<IChapterBook> ReadChaptersAsync(string path, string format, CancellationToken ct = default) =>
        format == "epub" ? await EpubDocument.LoadAsync(path, ct).ConfigureAwait(false)
                         : await ReflowBooks.LoadAsync(path, format, ct).ConfigureAwait(false);

    internal static async Task<TextDocument> ReadChapterDocumentAsync(IChapterBook book, int index, CancellationToken ct)
    {
        if (book is EpubDocument epub) return await epub.ReadStyledChapterAsync(index, ct).ConfigureAwait(false);
        var content = await book.ReadChapterAsync(index, ct).ConfigureAwait(false);
        return await Task.Run(() => TextDocument.FromText(content, [], ct), ct).ConfigureAwait(false);
    }

    internal static async Task<string> DetectFileAsync(string path, CancellationToken ct = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await DetectAsync(file, ct).ConfigureAwait(false);
    }

    internal static async Task<string> DetectAsync(Stream input, CancellationToken ct)
    {
        const int probeSize = 16384;
        byte[] bytes = new byte[(int)Math.Min(input.Length, probeSize)];
        if (bytes.Length == 0) throw new InvalidDataException("文件为空。");
        long start = input.Position;
        try
        {
            await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (bytes.AsSpan().StartsWith("PK\u0003\u0004"u8) || bytes.AsSpan().StartsWith("PK\u0005\u0006"u8))
            {
                input.Position = start;
                return ReadArchiveFormat(input, ct);
            }
            return ReadPrefixFormat(bytes);
        }
        finally { input.Position = start; }
    }

    private static string ReadArchiveFormat(Stream input, CancellationToken ct)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.GetEntry("META-INF/container.xml") != null) return "epub";
        if (archive.GetEntry("[Content_Types].xml") != null && archive.GetEntry("word/document.xml") != null) return "docx";
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Length == 0 || entry.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Name.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase)) return "fb2";
        }
        throw new InvalidDataException("ZIP 中找不到受支持的电子书内容。");
    }

    private static string ReadPrefixFormat(ReadOnlySpan<byte> bytes)
    {
        int pdf = bytes[..Math.Min(bytes.Length, 1024)].IndexOf("%PDF-"u8);
        if (pdf >= 0 && pdf + 8 <= bytes.Length && IsPdfPreamble(bytes[..pdf]) &&
            bytes[pdf + 5] is (byte)'1' or (byte)'2' && bytes[pdf + 6] == '.' && bytes[pdf + 7] is >= (byte)'0' and <= (byte)'9')
            return "pdf";
        if (bytes.Length >= 68 && bytes[60..68].SequenceEqual("BOOKMOBI"u8))
            throw new InvalidDataException("此 Kindle 电子书格式暂不支持。");

        var (encoding, bomLength) = ReadSignature(bytes);
        if (encoding == null && bytes.Contains((byte)0))
            throw new InvalidDataException("无法识别此文件的电子书格式。");

        string prefix = (encoding ?? Encoding.UTF8).GetString(bytes[bomLength..]);
        if (prefix.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal))
        {
            if (FictionBookRoot().IsMatch(prefix)) return "fb2";
            if (HtmlRoot().IsMatch(prefix)) return "html";
        }
        if (encoding != null && bomLength == 0)
            throw new InvalidDataException("无法识别此文件的电子书格式。");
        int binaryControls = 0;
        foreach (var character in prefix)
        {
            if (character == '\0') throw new InvalidDataException("无法识别此文件的电子书格式。");
            if (character < ' ' && character is not ('\t' or '\r' or '\n' or '\f')) binaryControls++;
        }
        if (binaryControls > 2 && binaryControls * 20 > prefix.Length)
            throw new InvalidDataException("无法识别此文件的电子书格式。");
        return "txt";
    }

    private static bool IsPdfPreamble(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[3..];
        foreach (var value in bytes)
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
        return true;
    }

    private static (Encoding? Encoding, int BomLength) ReadSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff })) return (new UTF32Encoding(true, true), 4);
        if (bytes.StartsWith(Encoding.UTF32.Preamble)) return (Encoding.UTF32, 4);
        if (bytes.StartsWith(Encoding.BigEndianUnicode.Preamble)) return (Encoding.BigEndianUnicode, 2);
        if (bytes.StartsWith(Encoding.Unicode.Preamble)) return (Encoding.Unicode, 2);
        if (bytes.StartsWith(Encoding.UTF8.Preamble)) return (Encoding.UTF8, 3);
        if (bytes.StartsWith("<\0"u8)) return (Encoding.Unicode, 0);
        if (bytes.StartsWith("\0<"u8)) return (Encoding.BigEndianUnicode, 0);
        return (null, 0);
    }

    internal static int GetChapterForPosition(string? position, int count)
    {
        if (position?.StartsWith("native-epub:", StringComparison.Ordinal) == true) position = position[12..];
        if (string.IsNullOrWhiteSpace(position) || position.Length > 4096 || position[0] != '{') return 0;
        try
        {
            using var json = JsonDocument.Parse(position);
            if (json.RootElement.TryGetProperty("chapter", out var chapter) && chapter.ValueKind == JsonValueKind.Number &&
                chapter.TryGetInt32(out int index)) return Math.Clamp(index, 0, count - 1);
        }
        catch (JsonException) { }
        return 0;
    }

    [GeneratedRegex(@"\A\s*(?:(?:<\?xml\b[\s\S]*?\?>|<!--[\s\S]*?-->)\s*)*<(?:[\w.-]+:)?FictionBook\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex FictionBookRoot();

    [GeneratedRegex(@"\A\s*(?:(?:<\?xml\b[\s\S]*?\?>|<!--[\s\S]*?-->)\s*)*(?:<!doctype\s+html\b|<(?:[\w.-]+:)?html\b|<(?:head|body|article|main|section|div|table|h[1-6]|p)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HtmlRoot();
}
