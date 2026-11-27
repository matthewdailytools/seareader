using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private BookEntry? savedReaderMarksBook;
    private Dictionary<int, TextReaderMarkIndex> savedTextMarks = [];
    private Dictionary<int, PdfReaderMark[]> savedPdfMarks = [];
    private TextSearchMatchAdorner? primarySavedMarks;
    private TextSearchMatchAdorner? facingSavedMarks;
    private bool savedMarksRefreshQueued;

    private void SetSavedReaderMarks(IReadOnlyList<AnnotationEntry> entries)
    {
        if (book == null || closing) return;
        if (!ReferenceEquals(savedReaderMarksBook, book)) ClearSavedReaderMarks();
        savedReaderMarksBook = book;

        if (pdf is { } document)
        {
            var pages = new Dictionary<int, List<PdfReaderMark>>();
            foreach (var entry in entries)
            {
                if (!ReaderHighlightPalette.IsVisibleAnnotation(entry) ||
                    ReadPdfReaderMark(entry.Position, document.PageCount) is not (var page, var bounds)) continue;
                if (!pages.TryGetValue(page, out var marks)) pages.Add(page, marks = []);
                marks.Add(new PdfReaderMark(entry, bounds));
            }
            savedPdfMarks = pages.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
            RefreshSavedPdfMarks();
            return;
        }

        var chapters = new Dictionary<int, List<TextReaderMark>>();
        foreach (var entry in entries)
        {
            if (!ReaderHighlightPalette.IsVisibleAnnotation(entry) ||
                ParsePosition(entry.Position) is not { Offset: >= 0, End: int end } location || end <= location.Offset) continue;
            var chapterIndex = location.Chapter ?? -1;
            if (epub is { } source ? chapterIndex < 0 || chapterIndex >= source.Chapters.Count : location.Chapter.HasValue)
                continue;
            if (!chapters.TryGetValue(chapterIndex, out var marks)) chapters.Add(chapterIndex, marks = []);
            marks.Add(new TextReaderMark(location.Offset, end, entry.Highlight, location.Encoding, entry.Quotation) { Entry = entry });
        }

        if (savedTextMarks.Count > 0) TextList.LayoutUpdated -= OnSavedMarksLayoutUpdated;
        savedTextMarks = chapters.ToDictionary(pair => pair.Key, pair => new TextReaderMarkIndex(pair.Value));
        if (savedTextMarks.Count > 0) TextList.LayoutUpdated += OnSavedMarksLayoutUpdated;
        RefreshSavedTextMarks();
    }

    private void ClearSavedReaderMarks()
    {
        savedReaderMarksBook = null;
        if (savedTextMarks.Count > 0) TextList.LayoutUpdated -= OnSavedMarksLayoutUpdated;
        savedTextMarks.Clear();
        savedPdfMarks.Clear();
        primarySavedMarks?.Remove();
        primarySavedMarks = null;
        facingSavedMarks?.Remove();
        facingSavedMarks = null;
        PdfSelection.SetSavedMarks([]);
        PdfFacingSelection.SetSavedMarks([]);
    }

    private void OnSavedMarksLayoutUpdated(object? sender, EventArgs e)
    {
        if (savedMarksRefreshQueued || closing) return;
        savedMarksRefreshQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            savedMarksRefreshQueued = false;
            RefreshSavedTextMarks();
        }, DispatcherPriority.Render);
    }

    private void RefreshSavedTextMarks()
    {
        if (closing || !ReferenceEquals(savedReaderMarksBook, book)) return;
        int primaryChapter = epub == null ? -1 : chapter;
        primarySavedMarks = RenderSavedTextMarks(TextList, primarySavedMarks,
            ReferenceEquals(windowDocument, text) ? text : null, visibleRows, savedTextMarks.GetValueOrDefault(primaryChapter));

        var facing = HasSpread ? facingPage : null;
        int facingChapter = epub == null ? -1 : facing?.Chapter ?? -1;
        facingSavedMarks = RenderSavedTextMarks(FacingTextList, facingSavedMarks,
            facing?.Document, facing?.Rows, savedTextMarks.GetValueOrDefault(facingChapter));
    }

    private static TextSearchMatchAdorner? RenderSavedTextMarks(RichTextBox body, TextSearchMatchAdorner? adorner,
        TextDocument? document, IReadOnlyList<(Paragraph Paragraph, TextLine Line)>? rows, TextReaderMarkIndex? marks)
    {
        if (!body.IsVisible || document == null || rows is not { Count: > 0 } || marks == null)
        {
            adorner?.Clear();
            return adorner;
        }

        var scroll = body.Template?.FindName("PART_ContentHost", body) as ScrollViewer ?? Descendant<ScrollViewer>(body);
        if (scroll?.Content is not FrameworkElement scope)
        {
            adorner?.Remove();
            return null;
        }
        if (!ReferenceEquals(adorner?.AdornedElement, scope))
        {
            adorner?.Remove();
            adorner = AdornerLayer.GetAdornerLayer(scope) is { } layer ? new TextSearchMatchAdorner(scope, layer) : null;
        }
        adorner?.UpdateSaved(body, document, rows, marks);
        return adorner;
    }

    private void RefreshSavedPdfMarks()
    {
        ApplyMarks(PdfSelection);
        ApplyMarks(PdfFacingSelection);

        void ApplyMarks(PdfSelectionLayer layer) => layer.SetSavedMarks(
            ReferenceEquals(savedReaderMarksBook, book) ? savedPdfMarks.GetValueOrDefault(PdfPageFor(layer)) ?? [] : []);
    }

    private static (int Page, IReadOnlyList<Rect> Bounds)? ReadPdfReaderMark(string position, int pageCount)
    {
        try
        {
            using var metadata = JsonDocument.Parse(position);
            var mark = metadata.RootElement;
            if (mark.ValueKind != JsonValueKind.Object || !mark.TryGetProperty("page", out var rawPage) ||
                rawPage.ValueKind != JsonValueKind.Number || !rawPage.TryGetInt32(out int page) || page < 1 || page > pageCount)
                return null;
            var bounds = ReadPdfAnnotationBounds(mark).Where(box => box.Width > 0 && box.Height > 0).Take(80).ToArray();
            return bounds.Length == 0 ? null : (page - 1, bounds);
        }
        catch (Exception error) when (error is JsonException or FormatException) { return null; }
    }
}

internal readonly record struct PdfReaderMark(AnnotationEntry Entry, IReadOnlyList<Rect> Bounds)
{
    internal AnnotationHighlight Highlight => Entry.Highlight;
}

internal readonly record struct TextReaderMark(int Start, int End, AnnotationHighlight Highlight, string? Encoding, string Quotation)
{
    internal AnnotationEntry? Entry { get; init; }

    internal bool CanDraw(TextDocument document) => End <= document.Text.Length &&
        (Encoding == null || string.Equals(Encoding, document.EncodingName, StringComparison.OrdinalIgnoreCase) ||
        Quotation.Length == End - Start && document.Text.AsSpan(Start, End - Start).SequenceEqual(Quotation.AsSpan()));
}

internal readonly record struct ReaderMarkHit(AnnotationEntry Entry, double Size)
{
    internal bool Precedes(ReaderMarkHit? other) => other is not { } previous || Size < previous.Size ||
        Size == previous.Size && (Entry.UpdatedAt > previous.Entry.UpdatedAt ||
        Entry.UpdatedAt == previous.Entry.UpdatedAt && Entry.Id > previous.Entry.Id);
}

internal sealed class TextReaderMarkIndex
{
    private readonly TextReaderMark[] marks;
    private readonly int[] furthestEnds;

    internal TextReaderMarkIndex(IEnumerable<TextReaderMark> entries)
    {
        marks = entries.OrderBy(mark => mark.Start).ToArray();
        furthestEnds = new int[marks.Length];
        int end = 0;
        for (int index = 0; index < marks.Length; index++)
        {
            end = Math.Max(end, marks[index].End);
            furthestEnds[index] = end;
        }
    }

    internal IEnumerable<TextReaderMark> Between(int start, int end)
    {
        int first = 0;
        int last = marks.Length;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            if (furthestEnds[middle] <= start) first = middle + 1;
            else last = middle;
        }

        for (int index = first; index < marks.Length && marks[index].Start < end; index++)
            if (marks[index].End > start) yield return marks[index];
    }
}

internal static class ReaderHighlightPalette
{
    private static readonly Brush Amber = Make(Color.FromArgb(105, 254, 185, 75));
    private static readonly Brush Mint = Make(Color.FromArgb(104, 98, 204, 166));
    private static readonly Brush Lavender = Make(Color.FromArgb(110, 177, 152, 238));
    private static readonly Brush Underline = Make(Color.FromRgb(15, 111, 91));
    private static readonly Brush DarkPaperUnderline = Make(Color.FromRgb(150, 231, 203));
    private static readonly Pen NoteIndicator = MakeNotePen(Make(Color.FromRgb(41, 105, 94)), 1.4);
    private static readonly Pen DarkPaperNoteIndicator = MakeNotePen(Make(Color.FromRgb(151, 221, 198)), 1.4);
    private static readonly Pen PdfNoteHalo = MakeNotePen(Brushes.White, 2.5);
    private static readonly Pen PdfNoteContrast = MakeNotePen(Brushes.Black, 1.4);

    internal static bool IsMark(AnnotationHighlight highlight) => highlight is AnnotationHighlight.Amber or
        AnnotationHighlight.Mint or AnnotationHighlight.Lavender or AnnotationHighlight.Underline;

    internal static bool IsVisibleAnnotation(AnnotationEntry entry) => IsMark(entry.Highlight) ||
        entry.Highlight == AnnotationHighlight.None && !string.IsNullOrWhiteSpace(entry.Note);

    internal static Brush Get(AnnotationHighlight highlight) => highlight switch
    {
        AnnotationHighlight.Amber => Amber,
        AnnotationHighlight.Mint => Mint,
        AnnotationHighlight.Lavender => Lavender,
        AnnotationHighlight.Underline => Underline,
        _ => Brushes.Transparent
    };

    internal static Brush TextUnderline(Brush foreground) => SystemParameters.HighContrast ? SystemColors.WindowTextBrush :
        IsLightInk(foreground) ? DarkPaperUnderline : Underline;

    internal static Pen TextNoteIndicator(Brush foreground) => SystemParameters.HighContrast
        ? MakeNotePen(SystemColors.WindowTextBrush, 1.4) : IsLightInk(foreground) ? DarkPaperNoteIndicator : NoteIndicator;

    private static bool IsLightInk(Brush foreground) => foreground is SolidColorBrush { Color: var ink } &&
        ink.R + ink.G + ink.B > 384;

    internal static Rect UnderlineBounds(Rect box, bool textLine = false)
    {
        var thickness = Math.Min(box.Height, Math.Clamp(box.Height * .075, 1.4, 2.6));
        var inset = textLine ? Math.Min(box.Height * .13, 7) : 0;
        return new Rect(box.X, Math.Max(box.Top, box.Bottom - inset - thickness), box.Width, thickness);
    }

    internal static Rect NoteIndicatorBounds(Rect glyph, bool pdf = false)
    {
        var height = Math.Min(glyph.Height, pdf ? PdfNoteHalo.Thickness : NoteIndicator.Thickness);
        var inset = pdf ? Math.Min(glyph.Height * .11, 3.2) : Math.Min(glyph.Height * .06, 2);
        return new Rect(glyph.X, Math.Max(glyph.Top, glyph.Bottom - inset - height), glyph.Width, height);
    }

    internal static void DrawPdfNoteIndicator(DrawingContext drawing, Rect glyph) => DrawNoteIndicator(drawing,
        NoteIndicatorBounds(glyph, pdf: true), SystemParameters.HighContrast ? PdfNoteContrast : NoteIndicator, PdfNoteHalo);

    internal static void DrawNoteIndicator(DrawingContext drawing, Rect band, Pen ink, Pen? halo = null)
    {
        var diameter = (halo ?? ink).Thickness;
        var center = new Point(band.X + band.Width / 2, band.Y + band.Height / 2);
        if (band.Width <= diameter || band.Height < diameter)
        {
            if (halo != null)
                drawing.DrawEllipse(halo.Brush, null, center, Math.Min(band.Width, halo.Thickness) / 2,
                    Math.Min(band.Height, halo.Thickness) / 2);
            drawing.DrawEllipse(ink.Brush, null, center, Math.Min(band.Width, ink.Thickness) / 2,
                Math.Min(band.Height, ink.Thickness) / 2);
            return;
        }

        var start = new Point(band.Left + diameter / 2, center.Y);
        var end = new Point(band.Right - diameter / 2, center.Y);
        if (halo != null) drawing.DrawLine(halo, start, end);
        drawing.DrawLine(ink, start, end);
    }

    private static Pen MakeNotePen(Brush brush, double thickness)
    {
        // Dash lengths are specified in stroke widths. Keep the PDF halo aligned with the ink dots.
        var pen = new Pen(brush, thickness) { DashStyle = new DashStyle([0, 3.8 / thickness], 0), DashCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static Brush Make(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
