using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SeaReader.Native;

internal sealed class TextSearchMatchAdorner : Adorner
{
    private static readonly Brush Highlight = CreateHighlight();
    private readonly DrawingVisual visual = new();
    private readonly FrameworkElement scope;
    private readonly AdornerLayer layer;
    private readonly List<(Rect Bounds, ReaderMarkHit Mark)> savedHitAreas = [];
    private bool hasDrawing;

    internal TextSearchMatchAdorner(FrameworkElement scope, AdornerLayer layer) : base(scope)
    {
        this.scope = scope;
        this.layer = layer;
        IsHitTestVisible = false;
        AddVisualChild(visual);
        layer.Add(this);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index == 0
        ? visual : throw new ArgumentOutOfRangeException(nameof(index));

    internal void Remove() => layer.Remove(this);

    internal void Clear()
    {
        savedHitAreas.Clear();
        if (!hasDrawing) return;
        using var drawing = visual.RenderOpen();
        hasDrawing = false;
    }

    internal void Update(RichTextBox body, IReadOnlyList<(Paragraph Paragraph, TextLine Line)> rows,
        int matchStart, int matchEnd)
    {
        // TextPointer rectangles use RichTextBox coordinates, including its padding and scroll offset.
        var origin = scope.TranslatePoint(new Point(), body);
        var viewport = new Rect(origin, scope.RenderSize);
        bool highContrast = SystemParameters.HighContrast;
        var geometry = BuildRange(rows, 0, rows.Count, matchStart, matchEnd, viewport, underline: false,
            outline: highContrast);
        if (geometry == null) { Clear(); return; }

        using var drawing = visual.RenderOpen();
        drawing.PushTransform(new TranslateTransform(-origin.X, -origin.Y));
        if (highContrast)
        {
            drawing.PushClip(new RectangleGeometry(viewport));
            drawing.DrawGeometry(null, CreateOutline(DashStyles.Solid), geometry);
            drawing.Pop();
        }
        else drawing.DrawGeometry(Highlight, null, geometry);
        drawing.Pop();
        hasDrawing = true;
    }

    internal void UpdateSaved(RichTextBox body, TextDocument document,
        IReadOnlyList<(Paragraph Paragraph, TextLine Line)> rows, TextReaderMarkIndex marks)
    {
        savedHitAreas.Clear();
        var origin = scope.TranslatePoint(new Point(), body);
        var viewport = new Rect(origin, scope.RenderSize);
        if (viewport.Width <= 0 || viewport.Height <= 0) { Clear(); return; }
        int first = FindVisibleRow(rows, 0, viewport.Top, considerEnd: true);
        int last = FindVisibleRow(rows, first, viewport.Bottom, considerEnd: false);
        if (first >= last) { Clear(); return; }

        using var drawing = visual.RenderOpen();
        drawing.PushTransform(new TranslateTransform(-origin.X, -origin.Y));
        bool highContrast = SystemParameters.HighContrast;
        var outline = highContrast ? CreateOutline(DashStyles.Dash) : null;
        if (highContrast) drawing.PushClip(new RectangleGeometry(viewport));
        hasDrawing = false;
        int start = rows[first].Line.Offset;
        var finalLine = rows[last - 1].Line;
        List<Rect>? noteIndicators = null;
        foreach (var mark in marks.Between(start, finalLine.Offset + finalLine.Length))
        {
            if (!mark.CanDraw(document)) continue;
            bool underline = mark.Highlight == AnnotationHighlight.Underline;
            var notes = mark.Highlight == AnnotationHighlight.None ? noteIndicators ??= [] : null;
            var fragments = mark.Entry != null ? new List<Rect>() : null;
            var geometry = BuildRange(rows, first, last, mark.Start, mark.End, viewport, underline,
                highContrast && !underline, fragments, notes);
            if (geometry == null) continue;
            if (mark.Entry is { } entry && fragments != null)
            {
                var hit = new ReaderMarkHit(entry, mark.End - mark.Start);
                foreach (var fragment in fragments) savedHitAreas.Add((fragment, hit));
            }
            if (notes == null)
            {
                if (underline) drawing.DrawGeometry(ReaderHighlightPalette.TextUnderline(body.Foreground), null, geometry);
                else if (highContrast) drawing.DrawGeometry(null, outline, geometry);
                else drawing.DrawGeometry(ReaderHighlightPalette.Get(mark.Highlight), null, geometry);
            }
            hasDrawing = true;
        }
        if (noteIndicators is { Count: > 0 })
        {
            if (!highContrast) drawing.PushClip(new RectangleGeometry(viewport));
            var ink = ReaderHighlightPalette.TextNoteIndicator(body.Foreground);
            foreach (var band in noteIndicators) ReaderHighlightPalette.DrawNoteIndicator(drawing, band, ink);
            if (!highContrast) drawing.Pop();
        }
        if (highContrast) drawing.Pop();
        drawing.Pop();
    }

    internal AnnotationEntry? FindSavedMarkAt(Point point) => FindSavedMarkHitAt(point)?.Entry;

    internal (AnnotationEntry Entry, Rect Bounds)? FindSavedMarkHitAt(Point point)
    {
        ReaderMarkHit? hit = null;
        var bounds = Rect.Empty;
        foreach (var (fragment, mark) in savedHitAreas)
        {
            if (!mark.Precedes(hit) || !fragment.Contains(point)) continue;
            hit = mark;
            bounds = fragment;
        }
        return hit is { } saved ? (saved.Entry, bounds) : null;
    }

    private static int FindVisibleRow(IReadOnlyList<(Paragraph Paragraph, TextLine Line)> rows,
        int first, double edge, bool considerEnd)
    {
        int last = rows.Count;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            var paragraph = rows[middle].Paragraph;
            var pointer = considerEnd ? paragraph.ContentEnd : paragraph.ContentStart;
            if (!pointer.HasValidLayout) return considerEnd ? 0 : rows.Count;
            var caret = pointer.GetCharacterRect(considerEnd ? LogicalDirection.Backward : LogicalDirection.Forward);
            if (considerEnd ? caret.Bottom <= edge : caret.Top < edge) first = middle + 1;
            else last = middle;
        }
        return first;
    }

    private static StreamGeometry? BuildRange(IReadOnlyList<(Paragraph Paragraph, TextLine Line)> rows,
        int first, int last, int matchStart, int matchEnd, Rect viewport, bool underline, bool outline,
        List<Rect>? fragments = null, List<Rect>? notes = null)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var path = geometry.Open())
        {
            for (int index = first; index < last; index++)
            {
                var (paragraph, line) = rows[index];
                if (line.Offset >= matchEnd) break;
                int start = Math.Max(0, matchStart - line.Offset);
                int end = Math.Min(line.Length, matchEnd - line.Offset);
                if (start >= end || !paragraph.ContentStart.HasValidLayout)
                    continue;
                if (paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top >= viewport.Bottom) break;
                if (paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Bottom <= viewport.Top) continue;
                int runOffset = 0;
                var joined = Rect.Empty;
                for (var run = ParagraphText.FirstRun(paragraph); run != null; run = ParagraphText.NextRun(run))
                {
                    int length = ParagraphText.SourceLength(run);
                    int from = Math.Max(0, start - runOffset);
                    int through = Math.Min(length, end - runOffset);
                    if (from < through) AppendRun(path, run, from, through, viewport, underline, outline, fragments, notes, ref joined);
                    runOffset += length;
                    if (runOffset >= end) break;
                }
                AppendRangeRectangle(path, joined, viewport, underline, outline, fragments, notes);
            }
        }

        if (geometry.IsEmpty()) return null;
        geometry.Freeze();
        return geometry;
    }

    private static void AppendRun(StreamGeometryContext path, Run run, int start, int end, Rect viewport,
        bool underline, bool outline, List<Rect>? fragments, List<Rect>? notes, ref Rect joined)
    {
        var first = ParagraphText.PositionAt(run, start)?.GetInsertionPosition(LogicalDirection.Backward);
        var last = ParagraphText.PositionAt(run, end, LogicalDirection.Backward)?.GetInsertionPosition(LogicalDirection.Forward);
        if (first == null || last == null) return;

        var runStart = run.ContentStart;
        var runEnd = run.ContentEnd;
        if (first.CompareTo(runStart) < 0) first = runStart;
        if (last.CompareTo(runEnd) > 0) last = runEnd;
        for (var pointer = first; pointer.CompareTo(last) < 0;)
        {
            var next = pointer.GetNextInsertionPosition(LogicalDirection.Forward);
            if (next == null || next.CompareTo(runEnd) > 0) next = runEnd;
            var left = pointer.GetCharacterRect(LogicalDirection.Forward);
            var right = next.GetCharacterRect(LogicalDirection.Backward);
            pointer = next;
            if (left.IsEmpty || right.IsEmpty) continue;
            if (left.Top >= viewport.Bottom && right.Top >= viewport.Bottom) break;

            var rect = new Rect(new Point(Math.Min(left.X, right.X), Math.Min(left.Top, right.Top)),
                new Point(Math.Max(left.X, right.X), Math.Max(left.Bottom, right.Bottom)));
            if (rect.Width <= 0 || rect.Height <= 0 || !rect.IntersectsWith(viewport)) continue;

            if (!joined.IsEmpty && Math.Abs(joined.Top - rect.Top) < .01 && Math.Abs(joined.Bottom - rect.Bottom) < .01 &&
                rect.Left <= joined.Right + .01 && rect.Right >= joined.Left - .01)
            {
                joined.Union(rect);
            }
            else
            {
                AppendRangeRectangle(path, joined, viewport, underline, outline, fragments, notes);
                joined = rect;
            }
        }
    }

    private static void AppendRangeRectangle(StreamGeometryContext path, Rect rect, Rect viewport, bool underline,
        bool outline, List<Rect>? fragments, List<Rect>? notes)
    {
        if (rect.IsEmpty) return;
        var band = notes != null ? ReaderHighlightPalette.NoteIndicatorBounds(rect) : Rect.Empty;
        var painted = notes != null ? band : underline ? ReaderHighlightPalette.UnderlineBounds(rect, textLine: true) : rect;
        painted.Intersect(viewport);
        if (painted.Width <= 0 || painted.Height <= 0) return;
        AppendRectangle(path, painted, outline);
        notes?.Add(band);
        if (fragments != null)
        {
            rect.Intersect(viewport);
            fragments.Add(rect);
        }
    }

    private static void AppendRectangle(StreamGeometryContext path, Rect rect, bool outline)
    {
        if (rect.IsEmpty) return;
        path.BeginFigure(rect.TopLeft, isFilled: true, isClosed: true);
        path.LineTo(rect.TopRight, isStroked: outline, isSmoothJoin: false);
        path.LineTo(rect.BottomRight, isStroked: outline, isSmoothJoin: false);
        path.LineTo(rect.BottomLeft, isStroked: outline, isSmoothJoin: false);
    }

    private static Pen CreateOutline(DashStyle pattern)
    {
        var pen = new Pen(SystemColors.WindowTextBrush, 1.25) { DashStyle = pattern, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static Brush CreateHighlight()
    {
        var brush = new SolidColorBrush(Color.FromArgb(94, 255, 187, 51));
        brush.Freeze();
        return brush;
    }
}
