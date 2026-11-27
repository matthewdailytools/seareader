using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SeaReader.Native;

public sealed class PdfSelectionLayer : FrameworkElement
{
    internal readonly record struct TextSelection(int Anchor, int Active, string Text);

    private static readonly Brush SelectionBrush = CreateBrush(Color.FromArgb(108, 59, 135, 255));
    private static readonly Brush MatchBrush = CreateBrush(Color.FromArgb(155, 255, 188, 57));
    private static readonly Pen ContrastHalo = CreateContrastPen(Brushes.White, 2.6, DashStyles.Solid);
    private static readonly Pen ContrastSelection = CreateContrastPen(Brushes.Black, 1.0, DashStyles.Solid);
    private static readonly Pen ContrastMatch = CreateContrastPen(Brushes.Black, 1.0, DashStyles.Dash);
    private static readonly Pen ContrastMark = CreateContrastPen(Brushes.Black, 1.0, DashStyles.Dot);
    private PdfTextPage? page;
    private IReadOnlyList<PdfReaderMark> savedMarks = [];
    private IReadOnlyList<Rect> matches = [];
    private IReadOnlyList<Rect> selection = [];
    private IReadOnlyList<FixedEpubReaderLink> fixedEpubLinks = [];
    private (FixedEpubReaderLink Link, Point Point)? pressedLink;
    private Point? dragStart;
    private int anchor;
    private int active;

    public PdfSelectionLayer()
    {
        Focusable = true;
        var copy = new MenuItem { Header = "复制", InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => Copy();
        ContextMenu = new ContextMenu();
        ContextMenu.Items.Add(copy);
        ContextMenuOpening += (_, e) => e.Handled = selection.Count == 0;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => Copy(),
            (_, e) => e.CanExecute = selection.Count != 0));
    }

    internal event EventHandler? SelectionChanged;
    internal event Action<PdfSelectionLayer, FixedEpubReaderLink, Rect?>? FixedEpubLinkInvoked;
    internal string SelectedText => page?.Text[Math.Min(anchor, active)..Math.Max(anchor, active)] ?? "";
    internal IReadOnlyList<Rect> SelectedBounds => selection;

    internal void SetFixedEpubLinks(IReadOnlyList<FixedEpubReaderLink> links)
    {
        if (ReferenceEquals(fixedEpubLinks, links)) return;
        pressedLink = null;
        fixedEpubLinks = links;
        if (links.Count == 0 && Cursor == Cursors.Hand) Cursor = null;
    }

    internal FixedEpubReaderLink? ReadFixedEpubLink(Point point)
    {
        if (fixedEpubLinks.Count == 0) return null;
        var position = Normalize(point);
        return fixedEpubLinks.FirstOrDefault(link => link.Bounds.Any(rect => rect.Contains(position)));
    }

    internal TextSelection? ReadSelection() => selection.Count == 0 ? null : new TextSelection(anchor, active, SelectedText);

    internal void RestoreSelection(TextSelection saved)
    {
        var start = Math.Min(saved.Anchor, saved.Active);
        var end = Math.Max(saved.Anchor, saved.Active);
        if (page is not { HasText: true } source || start < 0 || end > source.Text.Length || end <= start ||
            !source.Text.AsSpan(start, end - start).SequenceEqual(saved.Text.AsSpan())) return;
        anchor = saved.Anchor;
        active = saved.Active;
        UpdateSelection();
    }

    internal void ClearSelection()
    {
        FinishDrag();
        if (anchor == active) return;
        active = anchor;
        UpdateSelection();
    }

    internal void SetPage(PdfTextPage? text)
    {
        FinishDrag();
        page = text;
        Cursor = null;
        anchor = active = 0;
        matches = [];
        if (text == null) SetSavedMarks([]);
        UpdateSelection();
    }

    internal void SetSavedMarks(IReadOnlyList<PdfReaderMark> marks)
    {
        if (ReferenceEquals(savedMarks, marks) || savedMarks.Count == 0 && marks.Count == 0) return;
        savedMarks = marks;
        InvalidateVisual();
    }

    internal AnnotationEntry? FindSavedMarkAt(Point point) => FindSavedMarkHitAt(point)?.Entry;

    internal (AnnotationEntry Entry, Rect Bounds)? FindSavedMarkHitAt(Point point)
    {
        if (!new Rect(RenderSize).Contains(point)) return null;
        ReaderMarkHit? hit = null;
        var bounds = Rect.Empty;
        foreach (var mark in savedMarks)
        {
            var fragment = mark.Bounds.Select(Scale).FirstOrDefault(box => box.Contains(point), Rect.Empty);
            if (fragment.IsEmpty) continue;
            var candidate = new ReaderMarkHit(mark.Entry, mark.Bounds.Sum(box => box.Width * box.Height));
            if (!candidate.Precedes(hit)) continue;
            hit = candidate;
            bounds = fragment;
        }
        return hit is { } saved ? (saved.Entry, bounds) : null;
    }

    internal void ShowMatch(PdfTextHit hit)
    {
        matches = hit.FromOcr ? [] : page?.Bounds(hit.Offset, hit.Offset + hit.Length) ?? [];
        InvalidateVisual();
        if (matches.Count != 0) BringIntoView(Scale(matches[0]));
    }

    internal void ShowAnnotation(IReadOnlyList<Rect> bounds, bool showTransient = true)
    {
        matches = showTransient ? bounds : [];
        InvalidateVisual();
        if (bounds.Count != 0) BringIntoView(Scale(bounds[0]));
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var viewport = new Rect(RenderSize);
        drawing.DrawRectangle(Brushes.Transparent, null, viewport);
        bool highContrast = SystemParameters.HighContrast;
        if (highContrast) drawing.PushClip(new RectangleGeometry(viewport));
        foreach (var mark in savedMarks)
        {
            if (mark.Highlight == AnnotationHighlight.None) continue;
            bool underline = mark.Highlight == AnnotationHighlight.Underline;
            var brush = underline ? ReaderHighlightPalette.TextUnderline(Brushes.Black) : ReaderHighlightPalette.Get(mark.Highlight);
            foreach (var box in mark.Bounds)
            {
                var target = SavedMarkBounds(box, mark.Highlight);
                if (underline && highContrast)
                {
                    var start = new Point(target.Left, target.Y + target.Height / 2);
                    var end = new Point(target.Right, start.Y);
                    drawing.DrawLine(ContrastHalo, start, end);
                    drawing.DrawLine(ContrastSelection, start, end);
                    continue;
                }
                DrawMark(drawing, target, brush, highContrast ? ContrastMark : null);
            }
        }
        foreach (var box in matches) DrawMark(drawing, Scale(box), MatchBrush, highContrast ? ContrastMatch : null);
        foreach (var box in selection) DrawMark(drawing, Scale(box), SelectionBrush, highContrast ? ContrastSelection : null);
        foreach (var mark in savedMarks)
            if (mark.Highlight == AnnotationHighlight.None)
                foreach (var box in mark.Bounds) ReaderHighlightPalette.DrawPdfNoteIndicator(drawing, Scale(box));
        if (highContrast) drawing.Pop();
    }

    // The white halo remains continuous so the black pattern is legible over either light or dark PDF content.
    private static void DrawMark(DrawingContext drawing, Rect bounds, Brush fill, Pen? contrast)
    {
        if (contrast == null)
        {
            drawing.DrawRectangle(fill, null, bounds);
            return;
        }
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        drawing.DrawRectangle(null, ContrastHalo, bounds);
        drawing.DrawRectangle(null, contrast, bounds);
    }

    private Rect SavedMarkBounds(Rect box, AnnotationHighlight highlight)
    {
        var bounds = Scale(box);
        return highlight == AnnotationHighlight.Underline ? ReaderHighlightPalette.UnderlineBounds(bounds) : bounds;
    }

    private Rect Scale(Rect box) => new(box.X * ActualWidth, box.Y * ActualHeight, box.Width * ActualWidth, box.Height * ActualHeight);

    private Point Normalize(Point point) => new(point.X / Math.Max(1, ActualWidth), point.Y / Math.Max(1, ActualHeight));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var pointer = e.GetPosition(this);
        var normalized = Normalize(pointer);
        var link = e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None ? ReadFixedEpubLink(pointer) : null;
        if (page is not { HasText: true } || !page.Contains(normalized))
        {
            if (page is { HasText: true })
            {
                anchor = active = 0;
                UpdateSelection();
            }
            if (link != null && CaptureMouse())
            {
                Focus();
                pressedLink = (link, pointer);
                e.Handled = true;
            }
            return;
        }
        Focus();
        anchor = active = page.NearestInsertion(normalized);
        if (e.ClickCount == 2)
        {
            while (anchor > 0 && !char.IsWhiteSpace(page.Text[anchor - 1])) anchor--;
            while (active < page.Text.Length && !char.IsWhiteSpace(page.Text[active])) active++;
        }
        else if (CaptureMouse())
        {
            dragStart = pointer;
            if (link != null) pressedLink = (link, pointer);
        }
        UpdateSelection();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pointer = e.GetPosition(this);
        if (pressedLink is { } pressed && ExceedsDragDistance(pointer, pressed.Point)) pressedLink = null;
        if (ReadFixedEpubLink(pointer) != null && (dragStart == null || pressedLink.HasValue)) Cursor = Cursors.Hand;
        else Cursor = page is { HasText: true } && (dragStart.HasValue || page.Contains(Normalize(pointer))) ? Cursors.IBeam : null;
        if (page is not { HasText: true }) return;
        var normalized = Normalize(pointer);
        if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        if (active == anchor && !ExceedsDragDistance(pointer, start)) return;
        var next = page.NearestInsertion(normalized);
        if (active == next) return;
        active = next;
        UpdateSelection();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var pointer = e.GetPosition(this);
        var link = pressedLink is { } pressed && Keyboard.Modifiers == ModifierKeys.None && anchor == active &&
            !ExceedsDragDistance(pointer, pressed.Point) && ReferenceEquals(ReadFixedEpubLink(pointer), pressed.Link)
            ? pressed.Link : null;
        FinishDrag();
        if (link != null)
        {
            e.Handled = true;
            var position = Normalize(pointer);
            FixedEpubLinkInvoked?.Invoke(this, link, link.Bounds.First(rect => rect.Contains(position)));
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragStart = null;
        pressedLink = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (page is not { HasText: true }) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            anchor = 0;
            active = page.Text.Length;
        }
        else if (e.Key == Key.Escape && selection.Count != 0) anchor = active = 0;
        else if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key is Key.Left or Key.Right)
            active = page.StepInsertion(active, e.Key == Key.Right);
        else return;
        UpdateSelection();
        e.Handled = true;
    }

    private void FinishDrag()
    {
        dragStart = null;
        pressedLink = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private static bool ExceedsDragDistance(Point current, Point start) =>
        Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private void UpdateSelection()
    {
        selection = page?.Bounds(Math.Min(anchor, active), Math.Max(anchor, active)) ?? [];
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Copy()
    {
        var text = SelectedText;
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (ExternalException) { }
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateContrastPen(Brush brush, double thickness, DashStyle pattern)
    {
        var pen = new Pen(brush, thickness) { DashStyle = pattern, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
