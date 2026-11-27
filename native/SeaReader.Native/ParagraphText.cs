using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SeaReader.Native;

internal static class ParagraphText
{
    private static readonly ConditionalWeakTable<Run, int[]> InsertedBreaks = new();

    internal static Run CreateRun(string source, ReadOnlySpan<int> breakOffsets)
    {
        if (breakOffsets.IsEmpty) return new Run(source);

        var offsets = breakOffsets.ToArray();
        var display = string.Create(source.Length + offsets.Length, (source, offsets), static (buffer, state) =>
        {
            int sourceStart = 0;
            int displayStart = 0;
            foreach (int sourceEnd in state.offsets)
            {
                var segment = state.source.AsSpan(sourceStart, sourceEnd - sourceStart);
                segment.CopyTo(buffer[displayStart..]);
                displayStart += segment.Length;
                buffer[displayStart++] = '\u00ad';
                sourceStart = sourceEnd;
            }
            state.source.AsSpan(sourceStart).CopyTo(buffer[displayStart..]);
        });
        var run = new Run(display);
        InsertedBreaks.Add(run, offsets);
        return run;
    }

    internal static int SourceLength(Run run)
    {
        int displayLength = run.ContentStart.GetOffsetToPosition(run.ContentEnd);
        return InsertedBreaks.TryGetValue(run, out var offsets) ? displayLength - offsets.Length : displayLength;
    }

    internal static TextPointer? PositionAt(Run run, int sourceOffset, LogicalDirection direction = LogicalDirection.Forward)
    {
        var start = run.ContentStart;
        int displayLength = start.GetOffsetToPosition(run.ContentEnd);
        if (InsertedBreaks.TryGetValue(run, out var offsets))
        {
            if (sourceOffset < 0 || sourceOffset > displayLength - offsets.Length) return null;
            int at = Array.BinarySearch(offsets, sourceOffset);
            sourceOffset += at < 0 ? ~at : at + (direction == LogicalDirection.Forward ? 1 : 0);
        }
        else if (sourceOffset < 0 || sourceOffset > displayLength) return null;
        return start.GetPositionAtOffset(sourceOffset, direction);
    }

    // WPF inline boundaries and generated soft hyphens have no stored source position.
    // Authored characters, including a book's own soft hyphens, keep their UTF-16 offsets.
    internal static TextPointer? PositionAt(Paragraph paragraph, int offset, LogicalDirection direction = LogicalDirection.Forward)
    {
        if (offset < 0) return null;
        var run = FirstRun(paragraph);
        if (run == null)
            return offset == 0 ? paragraph.ContentStart.GetPositionAtOffset(0, direction) : null;

        while (run != null)
        {
            var length = SourceLength(run);
            if (offset < length || offset == length && direction == LogicalDirection.Backward)
                return PositionAt(run, offset, direction);
            var next = NextRun(run);
            if (next == null) return offset == length ? PositionAt(run, offset, direction) : null;
            offset -= length;
            run = next;
        }
        return null;
    }

    internal static int OffsetOf(Paragraph paragraph, TextPointer pointer, int maximumLength)
    {
        if (maximumLength == 0) return 0;
        if (paragraph.Inlines.FirstInline is Run { NextInline: null } first)
            return Math.Clamp(ReadSourceOffset(first, first.ContentStart.GetOffsetToPosition(pointer)), 0, maximumLength);

        var offset = 0;
        for (var run = FirstRun(paragraph); run != null; run = NextRun(run))
        {
            var start = run.ContentStart;
            var end = run.ContentEnd;
            if (pointer.CompareTo(end) <= 0)
                return Math.Clamp(offset + Math.Max(0, ReadSourceOffset(run, start.GetOffsetToPosition(pointer))), 0, maximumLength);
            offset += SourceLength(run);
        }
        return Math.Min(offset, maximumLength);
    }

    internal static Run? FirstRun(Paragraph paragraph) => FindRun(paragraph.Inlines.FirstInline);

    internal static Run? NextRun(Run run) => FindRun(FollowingInline(run));

    private static Run? FindRun(Inline? inline)
    {
        while (inline != null)
        {
            if (inline is Run run) return run;
            inline = inline is Span { Inlines.FirstInline: { } child } ? child : FollowingInline(inline);
        }
        return null;
    }

    private static Inline? FollowingInline(Inline inline)
    {
        while (inline.NextInline == null && inline.Parent is Span parent) inline = parent;
        return inline.NextInline;
    }

    internal static string ReadSelectionText(RichTextBox body)
    {
        var selection = body.Selection;
        if (selection.IsEmpty) return "";

        var next = selection.Start;
        var end = selection.End;
        var startParagraph = next.Paragraph;
        var startBlock = ReferenceEquals(startParagraph?.Parent, body.Document)
            ? startParagraph : body.Document.Blocks.FirstBlock;
        StringBuilder? text = null;

        for (var block = startBlock; block != null && block.ContentStart.CompareTo(end) < 0; block = block.NextBlock)
        {
            if (block is not Paragraph paragraph) continue;
            for (var run = FirstRun(paragraph); run != null; run = NextRun(run))
            {
                if (!InsertedBreaks.TryGetValue(run, out var offsets)) continue;
                var runStart = run.ContentStart;
                if (runStart.CompareTo(end) >= 0) break;
                if (run.ContentEnd.CompareTo(next) <= 0) continue;

                for (int index = 0; index < offsets.Length; index++)
                {
                    int displayOffset = offsets[index] + index;
                    var before = runStart.GetPositionAtOffset(displayOffset, LogicalDirection.Forward)!;
                    if (before.CompareTo(end) >= 0) break;
                    var after = runStart.GetPositionAtOffset(displayOffset + 1, LogicalDirection.Backward)!;
                    if (after.CompareTo(next) <= 0) continue;

                    text ??= new StringBuilder();
                    if (next.CompareTo(before) < 0) text.Append(new TextRange(next, before).Text);
                    next = after;
                }
            }
        }
        if (text == null) return selection.Text;
        if (next.CompareTo(end) < 0) text.Append(new TextRange(next, end).Text);
        return text.ToString();
    }

    private static int ReadSourceOffset(Run run, int displayOffset)
    {
        if (!InsertedBreaks.TryGetValue(run, out var offsets)) return displayOffset;

        int first = 0;
        int last = offsets.Length;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            if (offsets[middle] + middle < displayOffset) first = middle + 1;
            else last = middle;
        }
        return displayOffset - first;
    }
}
