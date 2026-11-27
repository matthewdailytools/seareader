using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace SeaReader.Native;

internal readonly record struct FixedEpubTarget(int Page, string Fragment, string? LiteralFragment = null);

internal sealed record FixedEpubReaderLink(int SourcePage, FixedEpubTarget? Target, string Title, IReadOnlyList<Rect> Bounds,
    EpubReference? Footnote = null);

public sealed class FixedEpubLinkLayer : Panel
{
    private static readonly ControlTemplate EmptyTemplate = new(typeof(Button));
    private IReadOnlyList<FixedEpubReaderLink> links = [];

    internal event Action<FixedEpubReaderLink>? LinkFocused;
    internal event Action<FixedEpubReaderLink>? LinkInvoked;

    internal void SetLinks(IReadOnlyList<FixedEpubReaderLink> next)
    {
        if (ReferenceEquals(links, next) || links.Count == 0 && next.Count == 0) return;
        links = next;
        Children.Clear();
        foreach (var link in links)
        {
            var button = new LinkButton(link) { Template = EmptyTemplate, FocusVisualStyle = null };
            AutomationProperties.SetName(button, link.Title);
            if (link.Footnote != null) AutomationProperties.SetHelpText(button, "打开脚注");
            button.GotKeyboardFocus += (_, _) =>
            {
                LinkFocused?.Invoke(link);
                BringIntoView(Scale(link.Bounds[0], RenderSize));
                InvalidateVisual();
            };
            button.LostKeyboardFocus += (_, _) => InvalidateVisual();
            button.Click += (_, _) => LinkInvoked?.Invoke(link);
            Children.Add(button);
        }
        InvalidateVisual();
    }

    internal bool FocusLink(FixedEpubReaderLink link) =>
        Children.OfType<LinkButton>().FirstOrDefault(button => ReferenceEquals(button.Link, link))?.Focus() == true;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children) child.Measure(new Size());
        return new Size();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (LinkButton button in Children)
        {
            var bounds = Rect.Empty;
            foreach (var rect in button.Link.Bounds) bounds.Union(Scale(rect, finalSize));
            button.Arrange(bounds);
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (System.Windows.Input.Keyboard.FocusedElement is not LinkButton button || !Children.Contains(button)) return;
        var outer = new Pen(Brushes.White, 3.5);
        var inner = new Pen(SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.Black, 1.5);
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        foreach (var rectangle in button.Link.Bounds)
        {
            var box = Scale(rectangle, RenderSize);
            box.Inflate(1, 1);
            drawingContext.DrawRectangle(null, outer, box);
            drawingContext.DrawRectangle(null, inner, box);
        }
        drawingContext.Pop();
    }

    private static Rect Scale(Rect box, Size size) => new(box.X * size.Width, box.Y * size.Height,
        box.Width * size.Width, box.Height * size.Height);

    private sealed class LinkButton(FixedEpubReaderLink link) : Button
    {
        internal FixedEpubReaderLink Link { get; } = link;
        protected override AutomationPeer OnCreateAutomationPeer() => new LinkAutomationPeer(this);
    }

    private sealed class LinkAutomationPeer(Button owner) : ButtonAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Hyperlink;
        protected override string GetClassNameCore() => "Hyperlink";
    }
}
