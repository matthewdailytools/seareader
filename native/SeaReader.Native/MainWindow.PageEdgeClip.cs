using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private RectangleGeometry? primaryPageEdgeClip;
    private RectangleGeometry? facingPageEdgeClip;
    private bool pageEdgeClipQueued;

    private void QueueTextPageEdgeClip()
    {
        if (pageEdgeClipQueued || closing) return;
        pageEdgeClipQueued = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            pageEdgeClipQueued = false;
            RefreshTextPageEdgeClip();
        }, DispatcherPriority.Render);
    }

    private void RefreshTextPageEdgeClip()
    {
        if (closing || pdf != null || text == null || !ReaderSurface.IsVisible)
        {
            ResetTextPageEdgeClip();
            return;
        }

        if (continuous) ResetTextPageEdgeClip();
        else
        {
            ApplyPageEdgeClip(TextList, text.HasIllustrations, ref primaryPageEdgeClip);
            if (HasSpread && FacingTextList.IsVisible)
                ApplyPageEdgeClip(FacingTextList, facingPage?.Document?.HasIllustrations == true, ref facingPageEdgeClip);
            else ClearPageEdgeClip(FacingTextList, facingPageEdgeClip);
        }
        if (text.HasIllustrations) UpdateIllustrationTabStops(TextList);
        if (HasSpread && facingPage?.Document?.HasIllustrations == true) UpdateIllustrationTabStops(FacingTextList);
        if (text.HasReferences) UpdateReferenceTabStops(TextList);
        if (HasSpread && facingPage?.Document?.HasReferences == true) UpdateReferenceTabStops(FacingTextList);
    }

    private void ResetTextPageEdgeClip()
    {
        ClearPageEdgeClip(TextList, primaryPageEdgeClip);
        ClearPageEdgeClip(FacingTextList, facingPageEdgeClip);
    }

    private static void ApplyPageEdgeClip(RichTextBox body, bool hasIllustrations, ref RectangleGeometry? ownedClip)
    {
        if (body.Clip != null && !ReferenceEquals(body.Clip, ownedClip)) return;
        if (ReadPartialLastLineTop(body, hasIllustrations) is not { } edge)
        {
            ClearPageEdgeClip(body, ownedClip);
            return;
        }

        // Keep the measured viewport stable for pagination, search, and selection.
        var bounds = new Rect(0, 0, body.RenderSize.Width, edge);
        ownedClip ??= new RectangleGeometry();
        if (ownedClip.Rect != bounds) ownedClip.Rect = bounds;
        if (!ReferenceEquals(body.Clip, ownedClip)) body.SetCurrentValue(ClipProperty, ownedClip);
    }

    private static void ClearPageEdgeClip(RichTextBox body, RectangleGeometry? clip)
    {
        if (clip != null && ReferenceEquals(body.Clip, clip)) body.SetCurrentValue(ClipProperty, null);
    }

    private static double? ReadPartialLastLineTop(RichTextBox body, bool hasIllustrations)
    {
        const double tolerance = .1;
        if (!body.IsVisible || !body.IsArrangeValid ||
            body.Template?.FindName("PART_ContentHost", body) is not ScrollViewer { Content: FrameworkElement scrollContent } ||
            VisualTreeHelper.GetParent(scrollContent) is not ScrollContentPresenter { RenderSize: { Width: > 0, Height: > 0 } size } viewport)
            return null;

        var origin = viewport.TranslatePoint(new Point(), body);
        var bottom = origin.Y + size.Height;
        if (hasIllustrations && EpubIllustrationLayout.PartialLineTop(body.Document, origin.Y, bottom) is { } imageTop)
            return RoundEdge(imageTop);
        var line = body.GetPositionFromPoint(new Point(origin.X + size.Width / 2, bottom - tolerance), snapToText: true)
            ?.GetLineStartPosition(0);
        if (line == null) return null;

        var bounds = line.GetCharacterRect(LogicalDirection.Forward);
        if (!bounds.IsEmpty && bounds.Top >= bottom - tolerance)
            line = AdjacentVisualLine(line, -1);
        else if (!bounds.IsEmpty && bounds.Bottom <= bottom + tolerance)
            line = AdjacentVisualLine(line, 1);
        if (line == null) return null;

        bounds = line.GetCharacterRect(LogicalDirection.Forward);
        if (bounds.IsEmpty || bounds.Top <= origin.Y + tolerance || bounds.Top >= bottom - tolerance ||
            bounds.Bottom <= bottom + tolerance) return null;

        return RoundEdge(bounds.Top);

        double RoundEdge(double value)
        {
            var scale = VisualTreeHelper.GetDpi(body).DpiScaleY;
            return Math.Floor(value * scale) / scale;
        }
    }
}
