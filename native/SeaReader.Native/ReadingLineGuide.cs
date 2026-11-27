using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SeaReader.Native;

internal readonly record struct ReaderGuideLine(int Offset, Rect Bounds, int EndOffset);

internal sealed class ReadingLineGuide : Adorner
{
    private readonly AdornerLayer layer;
    private readonly DrawingVisual visual = new();

    private ReadingLineGuide(FrameworkElement scope, AdornerLayer layer) : base(scope)
    {
        this.layer = layer;
        IsHitTestVisible = false;
        AddVisualChild(visual);
        layer.Add(this);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index == 0
        ? visual : throw new ArgumentOutOfRangeException(nameof(index));

    internal void Remove() => layer.Remove(this);

    internal static IReadOnlyList<ReaderGuideLine> ReadLines(RichTextBox body,
        IReadOnlyList<(Paragraph Paragraph, TextLine Line)> rows)
    {
        if (rows.Count == 0 || GetViewport(body) is not (_, var viewport)) return [];

        var lines = new List<ReaderGuideLine>();
        for (int index = FirstVisibleRow(rows, viewport.Top); index < rows.Count; index++)
        {
            var (paragraph, line) = rows[index];
            if (ParagraphText.PositionAt(paragraph, 0) is not { HasValidLayout: true } start) continue;
            var first = start.GetCharacterRect(LogicalDirection.Forward);
            if (first.IsEmpty) continue;
            if (first.Top >= viewport.Bottom) break;

            var pointer = start;
            if (first.Bottom <= viewport.Top &&
                FindOffsetBelow(paragraph, 0, line.Length, viewport.Top, useBottom: true) is { } visibleOffset)
                pointer = ParagraphText.PositionAt(paragraph, visibleOffset) ?? start;
            pointer = pointer.GetLineStartPosition(0) ?? pointer;
            while (pointer.Paragraph == paragraph)
            {
                var caret = pointer.GetCharacterRect(LogicalDirection.Forward);
                if (caret.IsEmpty || caret.Top >= viewport.Bottom) break;
                var next = pointer.GetLineStartPosition(1, out int moved);
                if (moved != 1 || next == null || next.CompareTo(pointer) <= 0) next = null;
                double top = Math.Max(viewport.Top, caret.Top);
                double bottom = Math.Min(viewport.Bottom, caret.Bottom);
                if (bottom > top)
                {
                    int local = ParagraphText.OffsetOf(paragraph, pointer, line.Length);
                    int end = next?.Paragraph == paragraph
                        ? Math.Max(local, ParagraphText.OffsetOf(paragraph, next, line.Length))
                        : EndOfVisualLine(paragraph, local, line.Length, caret.Top, next == null);
                    lines.Add(new ReaderGuideLine(line.Offset + local,
                        new Rect(viewport.X, top, viewport.Width, bottom - top), line.Offset + end));
                }

                if (next == null) break;
                pointer = next;
            }
        }
        return lines;
    }

    internal static ReadingLineGuide? Render(ReadingLineGuide? existing, RichTextBox body, Rect? activeLine,
        Brush paper, Brush accent, double dimOpacity)
    {
        if (GetViewport(body) is not (var scope, var viewport))
        {
            existing?.Remove();
            return null;
        }

        bool highContrast = SystemParameters.HighContrast;
        double dim = highContrast || !double.IsFinite(dimOpacity) ? 0 : Math.Clamp(dimOpacity, 0, .75);
        var band = activeLine.GetValueOrDefault(Rect.Empty);
        band.Intersect(viewport);
        if ((band.Width <= 0 || band.Height <= 0) && dim == 0)
        {
            existing?.Remove();
            return null;
        }

        if (!ReferenceEquals(existing?.AdornedElement, scope))
        {
            existing?.Remove();
            if (AdornerLayer.GetAdornerLayer(scope) is not { } layer) return null;
            existing = new ReadingLineGuide(scope, layer);
        }

        var origin = body.TranslatePoint(new Point(), scope);
        viewport.Offset(origin.X, origin.Y);
        if (!band.IsEmpty) band.Offset(origin.X, origin.Y);
        existing.Draw(viewport, band, paper, accent, dim, highContrast);
        return existing;
    }

    private void Draw(Rect viewport, Rect band, Brush paper, Brush accent, double dim, bool highContrast)
    {
        using var drawing = visual.RenderOpen();
        bool hasBand = band.Width > 0 && band.Height > 0;
        if (dim > 0)
        {
            drawing.PushOpacity(dim);
            if (hasBand)
            {
                if (band.Top > viewport.Top)
                    drawing.DrawRectangle(paper, null, new Rect(viewport.X, viewport.Y, viewport.Width, band.Top - viewport.Top));
                if (band.Bottom < viewport.Bottom)
                    drawing.DrawRectangle(paper, null, new Rect(viewport.X, band.Bottom, viewport.Width, viewport.Bottom - band.Bottom));
            }
            else drawing.DrawRectangle(paper, null, viewport);
            drawing.Pop();
        }

        if (!hasBand) return;
        // Keep the active line at normal contrast, with a cue inside the document's margin.
        var cue = highContrast ? SystemColors.WindowTextBrush : accent;
        if (highContrast && band.Height > 1 && band.Width > 1)
        {
            var outline = band;
            outline.Inflate(-.5, -.5);
            drawing.DrawRectangle(null, new Pen(cue, 1), outline);
        }

        double width = Math.Min(3, band.Width);
        double inset = Math.Min(1, (band.Width - width) / 2);
        var marker = new Rect(band.Left + inset, band.Top, width, band.Height);
        drawing.DrawRoundedRectangle(cue, null, marker, 1.5, 1.5);
    }

    private static (FrameworkElement Scope, Rect Bounds)? GetViewport(RichTextBox body)
    {
        if (!body.IsVisible || !body.IsArrangeValid ||
            body.Template?.FindName("PART_ContentHost", body) is not ScrollViewer { Content: FrameworkElement scope }) return null;

        // FlowDocument caret rectangles are relative to the control, including its padding and scroll offset.
        var viewport = VisualTreeHelper.GetParent(scope) as ScrollContentPresenter;
        if (viewport is not { RenderSize: { Width: > 0, Height: > 0 } size }) return null;
        return (scope, new Rect(viewport.TranslatePoint(new Point(), body), size));
    }

    private static int FirstVisibleRow(IReadOnlyList<(Paragraph Paragraph, TextLine Line)> rows, double top)
    {
        int first = 0;
        int last = rows.Count;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            var pointer = rows[middle].Paragraph.ContentEnd;
            if (!pointer.HasValidLayout) return first;
            var caret = pointer.GetCharacterRect(LogicalDirection.Backward);
            if (caret.IsEmpty) return first;
            if (caret.Bottom <= top) first = middle + 1;
            else last = middle;
        }
        return first;
    }

    private static int EndOfVisualLine(Paragraph paragraph, int local, int length, double top, bool needsFallback)
    {
        if (!needsFallback || local >= length) return length;
        // WPF may withhold the next line-start pointer just beyond the formatted viewport.
        var last = paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        if (last.IsEmpty || last.Top <= top + .01) return length;
        return FindOffsetBelow(paragraph, local + 1, length, top + .01, useBottom: false) ?? length;
    }

    private static int? FindOffsetBelow(Paragraph paragraph, int first, int length, double edge, bool useBottom)
    {
        int last = length;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            var pointer = ParagraphText.PositionAt(paragraph, middle);
            if (pointer == null) return null;
            var caret = pointer.GetCharacterRect(LogicalDirection.Forward);
            if (caret.IsEmpty) return null;
            if ((useBottom ? caret.Bottom : caret.Top) <= edge) first = middle + 1;
            else last = middle;
        }
        return first;
    }
}
