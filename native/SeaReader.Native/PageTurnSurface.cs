using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace SeaReader.Native;

internal enum PageTurnEffect { None, Paper, Fade }

public sealed class PageTurnSurface : FrameworkElement
{
    private const int MaximumSnapshotSide = 2048;
    private const int MaximumSnapshotPixels = 1_300_000;
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(PageTurnSurface),
        new PropertyMetadata(0.0, (target, change) => ((PageTurnSurface)target).UpdateFrame((double)change.NewValue)));

    private readonly DrawingVisual layer = new();
    private DrawingGroup? outgoing;
    private PaperFold? paper;
    private bool animating;
    private long clockVersion;

    public PageTurnSurface()
    {
        IsHitTestVisible = false;
        AddVisualChild(layer);
    }

    internal double Progress => (double)GetValue(ProgressProperty);
    internal bool HasFrame => outgoing != null;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index == 0
        ? layer : throw new ArgumentOutOfRangeException(nameof(index));

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Clear();
    }

    internal static BitmapSource? Capture(FrameworkElement source, Brush paper)
    {
        var size = source.RenderSize;
        if (size.Width < 1 || size.Height < 1 || !double.IsFinite(size.Width) || !double.IsFinite(size.Height)) return null;

        var dpi = VisualTreeHelper.GetDpi(source);
        var scale = Math.Min(1.5, Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
        scale = Math.Min(scale, MaximumSnapshotSide / Math.Max(size.Width, size.Height));
        scale = Math.Min(scale, Math.Sqrt(MaximumSnapshotPixels / (size.Width * size.Height)));
        int width = Math.Max(1, (int)Math.Floor(size.Width * scale));
        int height = Math.Max(1, (int)Math.Floor(size.Height * scale));
        var bounds = new Rect(size);
        var viewport = new DrawingVisual();
        using (var drawing = viewport.RenderOpen())
        {
            drawing.DrawRectangle(paper, null, bounds);
            drawing.DrawRectangle(new VisualBrush(source)
            {
                AutoLayoutContent = false,
                Viewbox = bounds,
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            }, null, bounds);
        }

        var snapshot = new RenderTargetBitmap(width, height, width * 96 / size.Width, height * 96 / size.Height, PixelFormats.Pbgra32);
        snapshot.Render(viewport);
        snapshot.Freeze();
        return snapshot;
    }

    internal bool Hold(BitmapSource snapshot, int direction, PageTurnEffect effect, Brush paper, double anchorY = .5)
    {
        Clear();
        var bounds = new Rect(RenderSize);
        if (bounds.Width < 1 || bounds.Height < 1 || direction == 0 || effect == PageTurnEffect.None) return false;

        layer.Clip = new RectangleGeometry(bounds);
        outgoing = new DrawingGroup();
        outgoing.Children.Add(new ImageDrawing(snapshot, bounds));
        if (effect == PageTurnEffect.Paper)
        {
            this.paper = new PaperFold(bounds, direction > 0, paper, Math.Clamp(double.IsFinite(anchorY) ? anchorY : .5, 0, 1));
            outgoing.ClipGeometry = this.paper.FrontGeometry;
        }

        UpdateFrame(0);
        using var drawing = layer.RenderOpen();
        if (this.paper is { } fold)
        {
            drawing.DrawDrawing(fold.Shadow);
            drawing.DrawDrawing(outgoing);
            drawing.DrawDrawing(fold.Back);
        }
        else drawing.DrawDrawing(outgoing);
        return true;
    }

    internal void Preview(double progress)
    {
        if (!HasFrame || !double.IsFinite(progress)) return;
        StopClock();
        SetCurrentValue(ProgressProperty, Math.Clamp(progress, 0, 1));
    }

    internal void Reveal(Action completed) => AnimateTo(1, completed);

    internal void Rewind(Action completed) => AnimateTo(0, completed);

    internal void Clear()
    {
        if (!HasFrame) return;
        outgoing = null;
        paper = null;
        StopClock();
        SetCurrentValue(ProgressProperty, 0.0);
        layer.Clip = null;
        using var drawing = layer.RenderOpen();
    }

    private void UpdateFrame(double progress)
    {
        if (paper != null) paper.Update(progress);
        else if (outgoing != null) outgoing.Opacity = 1 - progress;
    }

    private void StopClock()
    {
        if (!animating) return;
        animating = false;
        clockVersion++;
        BeginAnimation(ProgressProperty, null);
    }

    private void AnimateTo(double target, Action completed)
    {
        if (!HasFrame)
        {
            completed();
            return;
        }

        var start = Progress;
        StopClock();
        SetCurrentValue(ProgressProperty, start);
        var distance = Math.Abs(target - start);
        if (distance < .001)
        {
            SetCurrentValue(ProgressProperty, target);
            completed();
            return;
        }

        var full = paper == null ? 125 : 260;
        var milliseconds = Math.Max(paper == null ? 35 : 55, full * Math.Pow(distance, .72));
        if (target == 0) milliseconds *= .8;
        var animation = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = start > 0 ? EasingMode.EaseOut : EasingMode.EaseInOut }
        };
        var version = ++clockVersion;
        animating = true;
        animation.Completed += (_, _) =>
        {
            if (version != clockVersion || !HasFrame) return;
            StopClock();
            SetCurrentValue(ProgressProperty, target);
            completed();
        };
        BeginAnimation(ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private readonly record struct Curve(double Top, double Upper, double Lower, double Bottom)
    {
        internal Curve Move(double distance) => new(Top + distance, Upper + distance, Lower + distance, Bottom + distance);
    }

    private sealed class PaperFold
    {
        private readonly Rect bounds;
        private readonly double anchor;
        private readonly Strip front;
        private readonly Strip back;
        private readonly Strip shadow;

        internal Geometry FrontGeometry => front.Geometry;
        internal DrawingGroup Back { get; }
        internal DrawingGroup Shadow { get; }

        internal PaperFold(Rect bounds, bool forward, Brush paper, double anchor)
        {
            this.bounds = bounds;
            this.anchor = anchor;
            Transform direction = forward ? Transform.Identity : new ScaleTransform(-1, 1, bounds.Width / 2, 0);
            front = new Strip(direction);
            back = new Strip(direction);
            shadow = new Strip(direction);
            var tone = paper is SolidColorBrush solid ? solid.Color : SystemColors.WindowColor;
            bool darkPaper = .2126 * tone.R + .7152 * tone.G + .0722 * tone.B < 110;
            var paperBack = Blend(tone, darkPaper ? Colors.White : Colors.DimGray, darkPaper ? .20 : .09);
            Back = Coat(back.Geometry, Gradient(forward,
                (0, Blend(paperBack, Colors.Black, darkPaper ? .10 : .19)),
                (.25, paperBack),
                (.72, Blend(paperBack, Colors.White, darkPaper ? .16 : .52)),
                (1, Blend(paperBack, Colors.Black, darkPaper ? .13 : .16))));
            Shadow = Coat(shadow.Geometry, Gradient(forward,
                (0, Color.FromArgb(darkPaper ? (byte)172 : (byte)116, 0, 0, 0)),
                (.35, Color.FromArgb(darkPaper ? (byte)92 : (byte)62, 0, 0, 0)),
                (1, Color.FromArgb(0, 0, 0, 0))));
        }

        internal void Update(double progress)
        {
            var lift = progress is 0 or 1 ? 0 : Math.Sin(Math.PI * progress);
            var center = bounds.Width * (1 - progress);
            var lean = Math.Min(bounds.Width * .25, bounds.Height * .36) * (.62 - anchor) * lift;
            var bow = Math.Min(bounds.Width * .057, 34) * lift;
            var crease = new Curve(center - lean, center - lean / 3 + bow, center + lean / 3 + bow, center + lean);
            var depth = Math.Min(bounds.Width * .15, 76) * lift;
            var shade = Math.Min(bounds.Width * .05, 27) * lift;
            front.Set(default, crease, bounds.Height);
            back.Set(crease.Move(-depth), crease, bounds.Height);
            shadow.Set(crease, crease.Move(shade), bounds.Height);
            Back.Opacity = Math.Min(1, lift * 4);
            Shadow.Opacity = Math.Min(1, lift * 2.5);
        }

        private static DrawingGroup Coat(Geometry geometry, Brush brush)
        {
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(brush, null, geometry));
            return drawing;
        }

        private static Brush Gradient(bool forward, params (double Offset, Color Color)[] colors)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(forward ? 0 : 1, .5),
                EndPoint = new Point(forward ? 1 : 0, .5)
            };
            foreach (var (offset, color) in colors) brush.GradientStops.Add(new GradientStop(color, offset));
            brush.Freeze();
            return brush;
        }

        private static Color Blend(Color basis, Color tint, double strength) => Color.FromRgb(
            Channel(basis.R, tint.R, strength), Channel(basis.G, tint.G, strength), Channel(basis.B, tint.B, strength));

        private static byte Channel(byte basis, byte tint, double strength) =>
            (byte)Math.Round(basis + (tint - basis) * strength);
    }

    private sealed class Strip
    {
        private readonly PathFigure figure = new() { IsClosed = true };
        private readonly LineSegment top = new();
        private readonly BezierSegment outer = new();
        private readonly LineSegment bottom = new();
        private readonly BezierSegment inner = new();

        internal PathGeometry Geometry { get; }

        internal Strip(Transform direction)
        {
            figure.Segments = new PathSegmentCollection([top, outer, bottom, inner]);
            Geometry = new PathGeometry([figure]) { Transform = direction };
        }

        internal void Set(Curve left, Curve right, double height)
        {
            var upper = height / 3;
            var lower = height - upper;
            figure.StartPoint = new Point(left.Top, 0);
            top.Point = new Point(right.Top, 0);
            outer.Point1 = new Point(right.Upper, upper);
            outer.Point2 = new Point(right.Lower, lower);
            outer.Point3 = new Point(right.Bottom, height);
            bottom.Point = new Point(left.Bottom, height);
            inner.Point1 = new Point(left.Lower, lower);
            inner.Point2 = new Point(left.Upper, upper);
            inner.Point3 = figure.StartPoint;
        }
    }
}
