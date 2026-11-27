using System.Text;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const int ProgressPreviewLimit = 72;
    private const int ProgressPreviewScanLimit = 256;

    private string? ProgressPreviewContext(int target)
    {
        if (pdf != null || target < 0) return null;

        // This is the same page index the slider and its eventual seek use throughout the drag.
        var targetChapter = target;
        var sourceOffset = target;
        var pages = sliderPagination;
        if (pages != null)
        {
            if (target >= pages.Count) return null;
            var page = pages.GetPage(target, out targetChapter);
            sourceOffset = page.SourceOffset >= 0 ? page.SourceOffset : page.Offset;
        }

        if (epub is { } chapters)
            return targetChapter < chapters.Chapters.Count ? CompactProgressPreview(chapters.Chapters[targetChapter].Title) : null;
        if (text is not { } document || sourceOffset < 0 || sourceOffset >= document.Text.Length) return null;

        // The unindexed slider seeks to the start of the target display row.
        if (pages == null) sourceOffset = document.Lines[document.FindLine(sourceOffset)].Offset;
        if (sourceOffset > 0 && char.IsLowSurrogate(document.Text[sourceOffset]) && char.IsHighSurrogate(document.Text[sourceOffset - 1]))
            sourceOffset--;
        return CompactProgressPreview(document.Text.AsSpan(sourceOffset));
    }

    private static string? CompactProgressPreview(ReadOnlySpan<char> source)
    {
        var end = Math.Min(source.Length, ProgressPreviewScanLimit);
        if (end < source.Length && char.IsHighSurrogate(source[end - 1]) && char.IsLowSurrogate(source[end])) end--;
        var preview = new StringBuilder(Math.Min(end, ProgressPreviewLimit));
        var skippedSpace = false;
        var clipped = end < source.Length;
        Span<char> utf16 = stackalloc char[2];
        foreach (var rune in source[..end].EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            {
                skippedSpace = preview.Length > 0;
                continue;
            }
            // Directional overrides and isolates in a book must not reorder the surrounding preview.
            if (rune.Value is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069) continue;

            var length = rune.EncodeToUtf16(utf16);
            if (preview.Length + length + (skippedSpace ? 1 : 0) > ProgressPreviewLimit)
            {
                clipped = true;
                break;
            }
            if (skippedSpace) preview.Append(' ');
            preview.Append(utf16[..length]);
            skippedSpace = false;
        }

        if (preview.Length == 0) return null;
        if (clipped) preview.Append('…');
        return preview.ToString();
    }
}
