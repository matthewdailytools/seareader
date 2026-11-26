using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SeaReader.Native;

public sealed class PdfSelectionLayer : FrameworkElement
{
    private static readonly Brush SelectionBrush = CreateBrush(Color.FromArgb(108, 59, 135, 255));
    private static readonly Brush MatchBrush = CreateBrush(Color.FromArgb(155, 255, 188, 57));
    private PdfTextPage? page;
    private IReadOnlyList<Rect> matches = [];
    private IReadOnlyList<Rect> selection = [];
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
    internal string SelectedText => page?.Text[Math.Min(anchor, active)..Math.Max(anchor, active)] ?? "";
    internal IReadOnlyList<Rect> SelectedBounds => selection;

    internal void SetPage(PdfTextPage? text)
    {
        FinishDrag();
        page = text;
        Cursor = null;
        anchor = active = 0;
        matches = [];
        UpdateSelection();
    }

    internal void ShowMatch(PdfTextHit hit)
    {
        matches = hit.FromOcr ? [] : page?.Bounds(hit.Offset, hit.Offset + hit.Length) ?? [];
        InvalidateVisual();
        if (matches.Count != 0) BringIntoView(Scale(matches[0]));
    }

    internal void ShowAnnotation(IReadOnlyList<Rect> bounds)
    {
        matches = bounds;
        InvalidateVisual();
        if (matches.Count != 0) BringIntoView(Scale(matches[0]));
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        foreach (var box in matches) drawing.DrawRectangle(MatchBrush, null, Scale(box));
        foreach (var box in selection) drawing.DrawRectangle(SelectionBrush, null, Scale(box));
    }

    private Rect Scale(Rect box) => new(box.X * ActualWidth, box.Y * ActualHeight, box.Width * ActualWidth, box.Height * ActualHeight);

    private Point Normalize(Point point) => new(point.X / Math.Max(1, ActualWidth), point.Y / Math.Max(1, ActualHeight));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (page is not { HasText: true }) return;
        var pointer = e.GetPosition(this);
        var normalized = Normalize(pointer);
        if (!page.Contains(normalized))
        {
            anchor = active = 0;
            UpdateSelection();
            return;
        }
        Focus();
        anchor = active = page.NearestInsertion(normalized);
        if (e.ClickCount == 2)
        {
            while (anchor > 0 && !char.IsWhiteSpace(page.Text[anchor - 1])) anchor--;
            while (active < page.Text.Length && !char.IsWhiteSpace(page.Text[active])) active++;
        }
        else if (CaptureMouse()) dragStart = pointer;
        UpdateSelection();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (page is not { HasText: true }) return;
        var pointer = e.GetPosition(this);
        var normalized = Normalize(pointer);
        Cursor = dragStart.HasValue || page.Contains(normalized) ? Cursors.IBeam : null;
        if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        if (active == anchor && Math.Abs(pointer.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pointer.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var next = page.NearestInsertion(normalized);
        if (active == next) return;
        active = next;
        UpdateSelection();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        FinishDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragStart = null;
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
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

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
}
