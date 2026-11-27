using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const int MouseWheelMessage = 0x020a;
    private const int HorizontalMouseWheelMessage = 0x020e;
    private const int MouseShiftFlag = 0x0004;
    private const int MouseControlFlag = 0x0008;
    private const int WheelDetent = 120;
    private const int WheelAxisDecisionDelay = 42;
    private const int WheelBurstQuietPeriod = 190;
    private const int TouchSelectionDelay = 320;
    private const double TouchAxisThreshold = 18;
    private const double TouchTurnThreshold = 62;
    private const double AxisDominance = 1.5;

    private readonly HashSet<TouchDevice> readerContacts = [];
    private readonly ReaderWheelSwipe readerWheelSwipe = new();
    private ReaderTouchSwipe? readerTouchSwipe;
    private HwndSource? readerGestureSource;
    private DispatcherTimer? readerWheelDecision;
    private (IPageBook Book, int Page, double Limit, double Target, long LastInput)? pdfWheelPan;

    internal enum ReaderSwipeAxis { None, Horizontal, Vertical }

    private sealed class ReaderTouchSwipe(TouchDevice device, Point origin, double pageAnchorY)
    {
        internal TouchDevice Device { get; } = device;
        internal Point Origin { get; } = origin;
        internal double PageAnchorY { get; } = pageAnchorY;
        internal long Started { get; } = Environment.TickCount64;
        internal Point Last { get; set; } = origin;
        internal ReaderSwipeAxis Axis { get; set; }
        internal double? ScrollTarget { get; set; }
        internal int ScrollWindowStart { get; set; }
        internal int ScrollPdfPage { get; set; } = -1;
        internal bool Scrolled { get; set; }
    }

    internal static ReaderSwipeAxis DominantReaderAxis(double horizontal, double vertical, double minimum)
    {
        horizontal = Math.Abs(horizontal);
        vertical = Math.Abs(vertical);
        if (horizontal >= minimum && horizontal >= vertical * AxisDominance) return ReaderSwipeAxis.Horizontal;
        if (vertical >= minimum && vertical >= horizontal * AxisDominance) return ReaderSwipeAxis.Vertical;
        return ReaderSwipeAxis.None;
    }

    private bool OnReaderPaper(Point point) => point.X >= 0 && point.X < SpreadPageContent.ActualWidth &&
        point.Y >= 0 && point.Y < SpreadPageContent.ActualHeight;

    private void OnReaderTouchDown(object sender, TouchEventArgs e)
    {
        if (book == null || !ReaderSurface.IsVisible) return;
        if (readerContacts.Add(e.TouchDevice)) e.TouchDevice.Deactivated += OnReaderTouchDeactivated;
        if (readerContacts.Count != 1)
        {
            StopReaderTap();
            CancelReaderTouch();
            if (IsReaderPageEdge(Mouse.Captured as DependencyObject)) Mouse.Capture(null);
            return;
        }

        var origin = e.GetTouchPoint(SpreadPageContent).Position;
        var source = e.OriginalSource as DependencyObject;
        var onPageEdge = IsReaderPageEdge(source);
        if ((!onPageEdge && (!OnReaderPaper(origin) || Within<ButtonBase>(source) && !Within<EpubIllustrationButton>(source))) ||
            Within<ScrollBar>(source) || Within<Slider>(source) || Within<Hyperlink>(source) ||
            source is Visual visual && (ReaderHeader.IsAncestorOf(visual) || ReaderToolbar.IsAncestorOf(visual) ||
                                       ReaderPanel.IsAncestorOf(visual) || SearchPanel.IsAncestorOf(visual) ||
                                       ContentsPanel.IsAncestorOf(visual) || PdfNavigationPanel.IsAncestorOf(visual) ||
                                       FootnotePanel.IsAncestorOf(visual)) ||
            (pdf == null ? !ActiveTextList.Selection.IsEmpty : ActivePdfSelection.SelectedBounds.Count != 0)) return;

        readerTouchSwipe = new ReaderTouchSwipe(e.TouchDevice, e.GetTouchPoint(ReaderSurface).Position,
            origin.Y / Math.Max(1, SpreadPageContent.ActualHeight));
    }

    private void OnReaderTouchMove(object sender, TouchEventArgs e)
    {
        if (readerTouchSwipe is not { } swipe || !ReferenceEquals(swipe.Device, e.TouchDevice)) return;
        var point = e.GetTouchPoint(ReaderSurface).Position;
        if (swipe.Axis == ReaderSwipeAxis.None)
        {
            if (Environment.TickCount64 - swipe.Started > TouchSelectionDelay)
            {
                readerTouchSwipe = null;
                return;
            }

            var distance = point - swipe.Origin;
            var axis = DominantReaderAxis(distance.X, distance.Y, TouchAxisThreshold);
            if (axis == ReaderSwipeAxis.None) return;
            if (axis == ReaderSwipeAxis.Horizontal && continuous && !CanPanPdfHorizontally)
            {
                readerTouchSwipe = null;
                return;
            }
            if (!e.TouchDevice.Capture(ReaderSurface, CaptureMode.SubTree))
            {
                readerTouchSwipe = null;
                return;
            }

            swipe.Axis = axis;
            StopReaderTap();
            if (pdf != null) ActivePdfSelection.ClearSelection();
            else if (!ActiveTextList.Selection.IsEmpty)
                ActiveTextList.Selection.Select(ActiveTextList.Selection.Start, ActiveTextList.Selection.Start);
            var promotedCapture = Mouse.Captured as DependencyObject;
            if (Within<RichTextBox>(promotedCapture) || IsReaderPageEdge(promotedCapture) || promotedCapture is PdfSelectionLayer)
                Mouse.Capture(null);
        }

        e.Handled = true;
        var step = point - swipe.Last;
        swipe.Last = point;
        if (swipe.Axis == ReaderSwipeAxis.Vertical && (continuous || pdf != null))
            ScrollWithReaderTouch(swipe, -step.Y);
        else if (swipe.Axis == ReaderSwipeAxis.Horizontal && continuous && CanPanPdfHorizontally)
            PanPdfWithReaderTouch(swipe, -step.X);
        else if (swipe.Axis == ReaderSwipeAxis.Horizontal)
            PreviewTouchPageTurn(point.X - swipe.Origin.X, swipe.PageAnchorY);
    }

    private bool IsReaderPageEdge(DependencyObject? source) => source is Visual visual &&
        (ReferenceEquals(visual, LeftPageEdge) || ReferenceEquals(visual, RightPageEdge) ||
         LeftPageEdge.IsAncestorOf(visual) || RightPageEdge.IsAncestorOf(visual));

    private async void OnReaderTouchUp(object sender, TouchEventArgs e)
    {
        var swipe = readerTouchSwipe;
        bool active = swipe != null && ReferenceEquals(swipe.Device, e.TouchDevice) && swipe.Axis != ReaderSwipeAxis.None;
        var point = active ? e.GetTouchPoint(ReaderSurface).Position : default;
        var direction = 0;
        if (active && swipe is { Scrolled: false })
        {
            var movement = point - swipe.Origin;
            if (DominantReaderAxis(movement.X, movement.Y, TouchTurnThreshold) == swipe.Axis)
                direction = swipe.Axis == ReaderSwipeAxis.Horizontal ? MapHorizontalPageTurn(-Math.Sign(movement.X)) : -Math.Sign(movement.Y);
        }
        var releasedPreview = direction != 0 && !continuous && swipe?.Axis == ReaderSwipeAxis.Horizontal
            ? ReleaseTouchPageTurn(direction) : null;
        if (active) e.Handled = true;
        ReleaseReaderTouch(e.TouchDevice);
        if (!active || swipe == null) return;
        if (swipe.Axis == ReaderSwipeAxis.Horizontal && continuous)
        {
            if (CanPanPdfHorizontally) PanPdfWithReaderTouch(swipe, swipe.Last.X - point.X);
            return;
        }
        if (direction == 0) return;
        if (swipe.Axis == ReaderSwipeAxis.Vertical && (continuous || pdf != null))
        {
            FindTextViewport();
            var viewport = pdf != null ? PdfSurface : textScroll;
            if (viewport == null || CanScroll(viewport, direction)) return;
        }

        var currentPdf = pdf;
        var currentPage = pdfPage;
        try { await TurnAsync(direction); }
        finally
        {
            if (releasedPreview.HasValue) RewindTouchPageTurn(releasedPreview);
        }
        if (swipe.Axis == ReaderSwipeAxis.Vertical && continuous && direction < 0 &&
            currentPdf != null && ReferenceEquals(pdf, currentPdf) && pdfPage != currentPage)
            PdfSurface.ScrollToBottom();
    }

    private void ScrollWithReaderTouch(ReaderTouchSwipe swipe, double distance)
    {
        if (moving || turningPage || seekingPosition || Math.Abs(distance) < .01) return;
        FindTextViewport();
        var viewport = pdf != null ? PdfSurface : textScroll;
        if (viewport == null) return;
        if (pdf == null && NeedsTextWindow(distance))
        {
            var current = ReadAnchor();
            MoveToText(current.Offset, current.Fraction, recenter: true);
            swipe.ScrollTarget = null;
        }

        var start = swipe.ScrollWindowStart == windowStart ? swipe.ScrollTarget ?? viewport.VerticalOffset : viewport.VerticalOffset;
        var target = Math.Clamp(start + distance, 0, viewport.ScrollableHeight);
        swipe.ScrollWindowStart = windowStart;
        swipe.ScrollTarget = target;
        if (Math.Abs(target - start) < .01) return;
        swipe.Scrolled = true;
        viewport.ScrollToVerticalOffset(target);
    }

    private bool CanPanPdfHorizontally => pdf != null && PdfSurface.ScrollableWidth > ScrollEdgeTolerance;

    private void PanPdfWithReaderTouch(ReaderTouchSwipe swipe, double distance)
    {
        if (moving || turningPage || seekingPosition || Math.Abs(distance) < .01) return;
        var start = swipe.ScrollPdfPage == pdfPage ? swipe.ScrollTarget ?? PdfSurface.HorizontalOffset : PdfSurface.HorizontalOffset;
        var target = Math.Clamp(start + distance, 0, PdfSurface.ScrollableWidth);
        swipe.ScrollPdfPage = pdfPage;
        swipe.ScrollTarget = target;
        if (Math.Abs(target - start) > .01) PdfSurface.ScrollToHorizontalOffset(target);
    }

    private void PanPdfWithReaderWheel(int delta, long now)
    {
        if (pdf == null || moving || turningPage || seekingPosition) return;
        var limit = PdfSurface.ScrollableWidth;
        var start = pdfWheelPan is { } pan && ReferenceEquals(pan.Book, pdf) && pan.Page == pdfPage &&
            pan.Limit == limit && now - pan.LastInput <= WheelBurstQuietPeriod ? pan.Target : PdfSurface.HorizontalOffset;
        var target = Math.Clamp(start + delta * .8, 0, limit);
        pdfWheelPan = (pdf, pdfPage, limit, target, now);
        if (Math.Abs(target - start) > .01) PdfSurface.ScrollToHorizontalOffset(target);
    }

    private void OnReaderTouchDeactivated(object? sender, EventArgs e)
    {
        if (sender is TouchDevice device) ReleaseReaderTouch(device);
    }

    private void OnReaderTouchLostCapture(object sender, TouchEventArgs e)
    {
        if (readerTouchSwipe is { Axis: not ReaderSwipeAxis.None } swipe && ReferenceEquals(swipe.Device, e.TouchDevice) &&
            !ReferenceEquals(e.TouchDevice.Captured, ReaderSurface)) CancelReaderTouch();
    }

    private void ReleaseReaderTouch(TouchDevice device)
    {
        if (readerContacts.Remove(device)) device.Deactivated -= OnReaderTouchDeactivated;
        if (ReferenceEquals(readerTouchSwipe?.Device, device)) CancelReaderTouch();
    }

    private void CancelReaderTouch()
    {
        var device = readerTouchSwipe?.Device;
        readerTouchSwipe = null;
        RewindTouchPageTurn();
        if (device != null && ReferenceEquals(device.Captured, ReaderSurface)) device.Capture(null);
    }

    private void StopReaderTap()
    {
        tapStart = null;
        pendingLineGuideClick = null;
        pendingSavedMarkTap = null;
        tapTimer.Stop();
    }

    private void ResetReaderGestures()
    {
        CancelReaderTouch();
        foreach (var device in readerContacts) device.Deactivated -= OnReaderTouchDeactivated;
        readerContacts.Clear();
        readerWheelDecision?.Stop();
        readerWheelSwipe.Reset();
        pdfWheelPan = null;
    }

    private void OnReaderGestureSourceInitialized(object? sender, EventArgs e)
    {
        readerGestureSource = PresentationSource.FromVisual(this) as HwndSource;
        readerGestureSource?.AddHook(OnReaderGestureMessage);
        Closed += OnReaderGestureClosed;
    }

    private void OnReaderGestureClosed(object? sender, EventArgs e)
    {
        if (readerGestureSource is { IsDisposed: false } source) source.RemoveHook(OnReaderGestureMessage);
        readerGestureSource = null;
        ResetReaderGestures();
    }

    private IntPtr OnReaderGestureMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is not (MouseWheelMessage or HorizontalMouseWheelMessage) || book == null ||
            !ReaderSurface.IsVisible || InOverlay || Keyboard.Modifiers != ModifierKeys.None ||
            readerContacts.Count != 0 || Within<ScrollBar>(Mouse.DirectlyOver as DependencyObject)) return IntPtr.Zero;

        var position = lParam.ToInt64();
        var cursor = new Point((short)position, (short)(position >> 16));
        if (!OnReaderPaper(SpreadPageContent.PointFromScreen(cursor))) return IntPtr.Zero;
        var wheelState = wParam.ToInt64();
        if ((wheelState & (MouseShiftFlag | MouseControlFlag)) != 0)
        {
            // Touchpad pinch promotion can carry MK_CONTROL without a physical key press.
            handled = (wheelState & MouseControlFlag) != 0;
            return IntPtr.Zero;
        }
        var delta = (short)(wheelState >> 16);
        if (delta == 0) return IntPtr.Zero;
        readingSessionTimer.MarkInteraction();

        var horizontal = message == HorizontalMouseWheelMessage;
        var now = Environment.TickCount64;
        if (continuous)
        {
            if (horizontal && CanPanPdfHorizontally)
            {
                handled = true;
                PanPdfWithReaderWheel(delta, now);
            }
            return IntPtr.Zero;
        }
        if (!horizontal && pdf != null && CanScroll(PdfSurface, delta > 0 ? -1 : 1))
        {
            if (readerWheelSwipe.HasRecentHorizontalTurn(now))
            {
                handled = true;
                readerWheelSwipe.Push(ReaderSwipeAxis.Vertical, -delta, now);
            }
            else readerWheelSwipe.ObserveNativeVertical(now);
            return IntPtr.Zero;
        }
        if (!horizontal && Math.Abs((int)delta) >= WheelDetent && readerWheelSwipe.ContinueVerticalDetents(now))
            return IntPtr.Zero;

        handled = true;
        if (readerWheelSwipe.Push(horizontal ? ReaderSwipeAxis.Horizontal : ReaderSwipeAxis.Vertical,
            horizontal ? delta : -delta, now))
        {
            // Windows can deliver the two axes of one touchpad stroke in separate messages.
            readerWheelDecision ??= CreateTimer(WheelAxisDecisionDelay, FinishReaderWheelGesture);
            if (!readerWheelDecision.IsEnabled) readerWheelDecision.Start();
        }
        return IntPtr.Zero;
    }

    private void FinishReaderWheelGesture()
    {
        var decision = readerWheelSwipe.Complete(Environment.TickCount64);
        if (closing || continuous || readerContacts.Count != 0 || !ReaderSurface.IsVisible) return;
        if (decision.Detents is { } detents)
        {
            foreach (var direction in detents) _ = TurnAsync(direction);
        }
        else if (decision.Axis != ReaderSwipeAxis.None)
            _ = TurnAsync(decision.Axis == ReaderSwipeAxis.Horizontal ? MapHorizontalPageTurn(decision.Direction) : decision.Direction);
    }

    internal readonly record struct ReaderWheelDecision(ReaderSwipeAxis Axis, int Direction, int[]? Detents = null);

    internal sealed class ReaderWheelSwipe
    {
        private const int TurnDistance = 100;
        private readonly List<int> verticalDetents = [];
        private long lastInput = -WheelBurstQuietPeriod - 1;
        private int horizontal;
        private int vertical;
        private ReaderSwipeAxis completedAxis;
        private bool sawHorizontal;
        private bool sawFineVertical;
        private bool nativeVerticalDetents;

        internal bool Push(ReaderSwipeAxis axis, int delta, long now)
        {
            if (now - lastInput > WheelBurstQuietPeriod) Reset();
            lastInput = now;
            if (axis == ReaderSwipeAxis.Horizontal) sawHorizontal = true;
            else if (Math.Abs(delta) < WheelDetent) sawFineVertical = true;
            if (completedAxis != ReaderSwipeAxis.None) return false;

            if (axis == ReaderSwipeAxis.Horizontal) horizontal += delta;
            else
            {
                vertical += delta;
                if (Math.Abs(delta) >= WheelDetent && verticalDetents.Count <= MaximumPendingTurns)
                    verticalDetents.Add(Math.Sign(delta));
            }
            return verticalDetents.Count > 0 || Math.Abs(horizontal) >= TurnDistance || Math.Abs(vertical) >= TurnDistance;
        }

        internal ReaderWheelDecision Complete(long now)
        {
            if (now - lastInput > WheelBurstQuietPeriod || completedAxis != ReaderSwipeAxis.None) return default;
            if (!sawHorizontal && !sawFineVertical && verticalDetents.Count > 0)
            {
                completedAxis = ReaderSwipeAxis.Vertical;
                nativeVerticalDetents = true;
                return new ReaderWheelDecision(completedAxis, 0, verticalDetents.ToArray());
            }
            completedAxis = DominantReaderAxis(horizontal, vertical, TurnDistance);
            return new ReaderWheelDecision(completedAxis,
                Math.Sign(completedAxis == ReaderSwipeAxis.Horizontal ? horizontal : vertical));
        }

        internal void ObserveNativeVertical(long now)
        {
            if (now - lastInput > WheelBurstQuietPeriod) Reset();
            lastInput = now;
            completedAxis = ReaderSwipeAxis.Vertical;
            nativeVerticalDetents = true;
        }

        internal bool ContinueVerticalDetents(long now)
        {
            if (!nativeVerticalDetents || sawHorizontal || sawFineVertical || now - lastInput > WheelBurstQuietPeriod) return false;
            lastInput = now;
            return true;
        }

        internal bool HasRecentHorizontalTurn(long now) =>
            completedAxis == ReaderSwipeAxis.Horizontal && now - lastInput <= WheelBurstQuietPeriod;

        internal void Reset()
        {
            lastInput = -WheelBurstQuietPeriod - 1;
            horizontal = vertical = 0;
            completedAxis = ReaderSwipeAxis.None;
            verticalDetents.Clear();
            sawHorizontal = sawFineVertical = nativeVerticalDetents = false;
        }
    }
}
