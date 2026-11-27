using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SeaReader.Native;

internal sealed class FacingTextPage(RichTextBox body, RoutedEventHandler openIllustration)
{
    private const int TextWindowSize = 128;
    private const int LongLineWindowSize = 48;
    private const int MaximumTextWindowSize = 768;
    private const double LayoutTolerance = .1;

    private readonly List<(Paragraph Paragraph, TextLine Line)> rows = [];
    private ScrollViewer? viewport;
    private Paragraph? heading;
    private string? title;
    private int windowStart;
    private int windowEnd;
    private TextPageStart? displayedPage;
    private Size displayedViewport;
    private bool displayedHyphenation;

    internal TextDocument? Document { get; private set; }
    internal int Chapter { get; private set; } = -1;
    internal IReadOnlyList<(Paragraph Paragraph, TextLine Line)> Rows => rows;
    internal bool IsUpdating { get; private set; }
    internal string SelectedText
    {
        get
        {
            if (IsUpdating || Document is not { } document) return "";
            // WPF inserts nonbreaking spaces for empty displayed rows and normalizes line endings.
            return TryReadSelection(out var start, out var end) ? document.Text[start..end] : ParagraphText.ReadSelectionText(body);
        }
    }

    internal void Show(TextDocument document, int chapter, string? chapterTitle, TextPageStart page,
        double fontSize, double lineHeight, bool bold = false, TextAlignment alignment = TextAlignment.Left, bool hyphenation = false)
    {
        if (IsUpdating) return;
        IsUpdating = true;
        try
        {
            body.UpdateLayout();
            if (FindViewport() is not { ViewportWidth: > 0, ViewportHeight: > 0 } scroll) return;

            var viewportSize = new Size(scroll.ViewportWidth, scroll.ViewportHeight);
            var flow = body.Document;
            bool sameDocument = ReferenceEquals(Document, document) && Chapter == chapter && title == chapterTitle &&
                flow.FontSize == fontSize && flow.LineHeight == lineHeight &&
                flow.FontWeight == (bold ? FontWeights.Bold : FontWeights.Normal) && flow.TextAlignment == alignment &&
                displayedHyphenation == hyphenation;
            if (sameDocument && displayedPage == page && displayedViewport == viewportSize) return;

            var index = document.FindLine(page.Offset);
            var screenRows = (int)Math.Clamp(Math.Ceiling(scroll.ViewportHeight / lineHeight), 1,
                (MaximumTextWindowSize - 4) / 2);
            if (!sameDocument || index < windowStart || index >= windowEnd ||
                windowEnd < document.Lines.Count && index >= windowEnd - screenRows - 1)
            {
                var usualCapacity = document.Lines[index].Length >= 512 ? LongLineWindowSize : TextWindowSize;
                var capacity = Math.Max(usualCapacity, screenRows * 2 + 4);
                BuildWindow(document, chapter, chapterTitle, index, capacity, fontSize, lineHeight, bold, alignment, hyphenation, viewportSize);
                body.UpdateLayout();
            }
            else if (!body.Selection.IsEmpty)
            {
                body.Selection.Select(body.Selection.Start, body.Selection.Start);
            }
            if (document.HasIllustrations && EpubIllustrationLayout.Resize(body.Document, viewportSize)) body.UpdateLayout();

            var target = PageTop(page, index - windowStart);
            if (!double.IsFinite(target)) return;
            target = Math.Max(0, target);
            if (target > scroll.ScrollableHeight + LayoutTolerance)
            {
                var inset = body.Document.PagePadding;
                body.Document.PagePadding = new Thickness(inset.Left, inset.Top, inset.Right,
                    inset.Bottom + target - scroll.ScrollableHeight);
                body.UpdateLayout();
            }
            scroll.ScrollToVerticalOffset(target);
            body.UpdateLayout();
            displayedPage = page;
            displayedViewport = viewportSize;
        }
        finally { IsUpdating = false; }
    }

    internal void Clear()
    {
        if (IsUpdating || Document == null) return;
        IsUpdating = true;
        try
        {
            Document = null;
            Chapter = -1;
            displayedPage = null;
            title = null;
            heading = null;
            rows.Clear();
            body.Document = new FlowDocument { PagePadding = TextPagination.DocumentPadding };
            viewport?.ScrollToTop();
        }
        finally { IsUpdating = false; }
    }

    internal bool TryReadSelection(out int start, out int end)
    {
        start = end = 0;
        if (IsUpdating || Document == null || body.Selection.IsEmpty) return false;
        return TryReadOffset(body.Selection.Start, out start) && TryReadOffset(body.Selection.End, out end) && end > start;
    }

    private void BuildWindow(TextDocument document, int chapter, string? chapterTitle, int index, int capacity,
        double fontSize, double lineHeight, bool bold, TextAlignment alignment, bool hyphenation, Size viewportSize)
    {
        var flow = new FlowDocument
        {
            ColumnWidth = double.PositiveInfinity,
            PagePadding = TextPagination.DocumentPadding
        };
        TextPagination.ApplyTypography(flow, fontSize, lineHeight, bold, alignment);
        windowStart = Math.Clamp(index - capacity / 2, 0, Math.Max(0, document.Lines.Count - capacity));
        windowEnd = Math.Min(windowStart + capacity, document.Lines.Count);
        heading = windowStart == 0 && chapterTitle != null
            ? TextPagination.CreateHeading(TextPagination.ChapterHeading(document, chapter, chapterTitle), fontSize) : null;
        if (heading != null) flow.Blocks.Add(heading);
        rows.Clear();
        for (var row = windowStart; row < windowEnd; row++)
        {
            var line = document.Lines[row];
            var paragraph = TextPagination.CreateBodyParagraph(document, line, hyphenation, viewportSize, openIllustration);
            flow.Blocks.Add(paragraph);
            rows.Add((paragraph, line));
        }
        Document = document;
        Chapter = chapter;
        title = chapterTitle;
        displayedHyphenation = hyphenation;
        displayedPage = null;
        body.Document = flow;
    }

    private double PageTop(TextPageStart page, int index)
    {
        var origin = heading == null ? RowTop(0) : ParagraphTop(heading);
        var fraction = Math.Clamp(page.Fraction, 0, 1);
        if (page.Offset < 0 && heading != null) return (RowTop(0) - origin) * fraction;
        if (Document?.HasIllustrations == true && viewport is { } scroll) origin = body.Padding.Top - scroll.VerticalOffset;
        if (page.Illustration is int figure && EpubIllustrationLayout.Find(rows[index].Paragraph, figure) is { } container)
            return container.ElementStart.GetCharacterRect(LogicalDirection.Forward).Top - origin;

        var top = RowTop(index);
        var (paragraph, row) = rows[index];
        if (page.SourceOffset >= 0 && ParagraphText.PositionAt(paragraph,
                Math.Clamp(page.SourceOffset - row.Offset, 0, row.Length)) is { } pointer &&
            pointer.GetCharacterRect(LogicalDirection.Forward) is { IsEmpty: false } bounds)
            return bounds.Top - origin;
        var bottom = index + 1 < rows.Count ? RowTop(index + 1) :
            rows[index].Paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Bottom;
        return top - origin + (bottom - top) * fraction;
    }

    private double RowTop(int index) => ParagraphTop(rows[index].Paragraph);

    private static double ParagraphTop(Paragraph paragraph) =>
        paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;

    private bool TryReadOffset(TextPointer pointer, out int offset)
    {
        var row = rows.FindIndex(item => ReferenceEquals(item.Paragraph, pointer.Paragraph));
        if (row < 0)
        {
            offset = 0;
            return false;
        }
        var (paragraph, line) = rows[row];
        offset = line.Offset + ParagraphText.OffsetOf(paragraph, pointer, line.Length);
        return true;
    }

    private ScrollViewer? FindViewport()
    {
        if (viewport != null) return viewport;
        body.ApplyTemplate();
        viewport = body.Template?.FindName("PART_ContentHost", body) as ScrollViewer ?? FindScrollViewer(body);
        return viewport;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if ((child as ScrollViewer ?? FindScrollViewer(child)) is { } scroll) return scroll;
        }
        return null;
    }
}
