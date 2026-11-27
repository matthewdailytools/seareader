using System.IO;
using System.Text;

namespace SeaReader.Native;

internal sealed record ReaderAnnotationExcerpt(string Location, string Quotation, string Note);

internal static class ReaderAnnotationExport
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly HashSet<char> InvalidFileNameCharacters = [.. Path.GetInvalidFileNameChars()];

    internal static string CreatePlainText(BookLibraryDisplay book, ReaderAnnotationExcerpt excerpt)
    {
        var text = new StringBuilder(DisplayTitle(book));
        var author = SingleLine(book.Author);
        if (author.Length != 0) text.Append("\n作者 ").Append(author);
        text.Append("\n出处 ").Append(DisplayLocation(excerpt));
        if (excerpt.Quotation.Length != 0) text.Append("\n\n摘录\n").Append(NormalizeNewlines(excerpt.Quotation));
        if (excerpt.Note.Length != 0) text.Append("\n\n笔记\n").Append(NormalizeNewlines(excerpt.Note));
        return text.ToString();
    }

    internal static string CreateMarkdown(BookLibraryDisplay book, IReadOnlyList<ReaderAnnotationExcerpt> excerpts)
    {
        var markdown = new StringBuilder("# ").Append(EscapeInline(DisplayTitle(book))).Append('\n');
        var author = SingleLine(book.Author);
        if (author.Length != 0) markdown.Append("\n作者 ").Append(EscapeInline(author)).Append('\n');
        foreach (var excerpt in excerpts)
        {
            markdown.Append("\n## ").Append(EscapeInline(DisplayLocation(excerpt))).Append('\n');
            AppendLiteralSection(markdown, "摘录", excerpt.Quotation);
            AppendLiteralSection(markdown, "笔记", excerpt.Note);
        }
        return markdown.ToString();
    }

    internal static string DefaultFileName(string bookTitle)
    {
        const int maximumTitleLength = 100;
        var sanitized = new StringBuilder();
        foreach (var rune in bookTitle.EnumerateRunes())
        {
            if (sanitized.Length + rune.Utf16SequenceLength > maximumTitleLength) break;
            if (Rune.IsControl(rune) || rune.IsBmp && InvalidFileNameCharacters.Contains((char)rune.Value))
                sanitized.Append('_');
            else sanitized.Append(rune.ToString());
        }

        var title = sanitized.ToString().Trim().Trim(' ', '.');
        if (title.Length == 0) return "阅读标注.md";

        var name = title + " - 标注.md";
        var stem = name[..name.IndexOf('.')].TrimEnd();
        return IsWindowsDevice(stem) ? "_" + name : name;
    }

    internal static Task WriteMarkdownAsync(string path, string markdown, CancellationToken ct = default) =>
        AtomicExportFile.WriteAsync(path, "annotations", async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, Utf8, leaveOpen: true);
            await writer.WriteAsync(markdown.AsMemory(), token).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
        }, ct);

    private static string DisplayTitle(BookLibraryDisplay book) =>
        SingleLine(book.Title) is { Length: > 0 } title ? title : "阅读标注";

    private static string DisplayLocation(ReaderAnnotationExcerpt excerpt) =>
        SingleLine(excerpt.Location) is { Length: > 0 } location ? location : "原文";

    private static string SingleLine(string value) => value.ReplaceLineEndings(" ").Trim();

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string EscapeInline(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~')
                escaped.Append('\\');
            escaped.Append(character);
        }
        return escaped.ToString();
    }

    private static void AppendLiteralSection(StringBuilder markdown, string label, string value)
    {
        if (value.Length == 0) return;
        var text = NormalizeNewlines(value);
        int fenceLength = 3, run = 0;
        foreach (char character in text)
        {
            run = character == '`' ? run + 1 : 0;
            if (run >= fenceLength) fenceLength = run + 1;
        }

        var fence = new string('`', fenceLength);
        markdown.Append("\n### ").Append(label).Append("\n\n").Append(fence).Append('\n').Append(text);
        if (text[^1] != '\n') markdown.Append('\n');
        markdown.Append(fence).Append('\n');
    }

    private static bool IsWindowsDevice(string stem)
    {
        var name = stem.ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name[..3] is "COM" or "LPT") && "0123456789¹²³".Contains(name[^1]);
    }
}
