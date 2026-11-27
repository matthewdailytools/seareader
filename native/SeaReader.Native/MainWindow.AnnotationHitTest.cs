using System.Windows;
using System.Windows.Controls;

namespace SeaReader.Native;

public partial class MainWindow
{
    private AnnotationEntry? FindSavedMarkAt(FrameworkElement target, Point point) => FindSavedMarkHitAt(target, point)?.Entry;

    private (AnnotationEntry Entry, Rect Bounds)? FindSavedMarkHitAt(FrameworkElement target, Point point)
    {
        if (book == null || !ReferenceEquals(savedReaderMarksBook, book) || closing || moving || turningPage ||
            seekingPosition || PageTurnLayer.HasFrame || !target.IsVisible || !target.IsArrangeValid ||
            !new Rect(target.RenderSize).Contains(point)) return null;

        if (pdf != null)
        {
            if (target is not PdfSelectionLayer layer || PdfPageFor(layer) < 0) return null;
            var pdfViewport = ReadMarkViewport(layer, PdfSurface);
            if (!pdfViewport.Contains(point) || layer.FindSavedMarkHitAt(point) is not { } pdfHit) return null;
            var pdfBounds = pdfHit.Bounds;
            if (!pdfViewport.Contains(pdfBounds)) pdfBounds.Intersect(pdfViewport);
            return (pdfHit.Entry, pdfBounds);
        }

        if (target is not RichTextBox body || !ReferenceEquals(body, TextList) &&
            (!ReferenceEquals(body, FacingTextList) || !HasSpread)) return null;
        var scroll = body.Template?.FindName("PART_ContentHost", body) as ScrollViewer ?? Descendant<ScrollViewer>(body);
        if (scroll == null) return null;
        var viewport = ReadMarkViewport(body, scroll);
        if (!viewport.Contains(point)) return null;
        RefreshTextPageEdgeClip();
        if (body.Clip != null && !body.Clip.FillContains(point)) return null;
        // Use current WPF line positions even if the layout update has queued a redraw for later in the frame.
        RefreshSavedTextMarks();
        if ((ReferenceEquals(body, TextList) ? primarySavedMarks : facingSavedMarks)?.FindSavedMarkHitAt(point)
            is not { } hit) return null;
        var bounds = hit.Bounds;
        bounds.Intersect(viewport);
        if (body.Clip != null) bounds.Intersect(body.Clip.Bounds);
        return (hit.Entry, bounds);
    }

    private static Rect ReadMarkViewport(FrameworkElement target, ScrollViewer scroll)
    {
        var viewport = scroll.Template?.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter ??
            Descendant<ScrollContentPresenter>(scroll);
        return viewport is { RenderSize: { Width: > 0, Height: > 0 } }
            ? viewport.TransformToVisual(target).TransformBounds(new Rect(viewport.RenderSize)) : Rect.Empty;
    }
}
