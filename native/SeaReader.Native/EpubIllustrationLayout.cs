using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SeaReader.Native;

internal static class EpubIllustrationLayout
{
    private const double VerticalPadding = 6;

    internal static bool CanFit(Size viewport) => double.IsFinite(viewport.Width) && double.IsFinite(viewport.Height) &&
        viewport.Width > TextPagination.DocumentPadding.Left + TextPagination.DocumentPadding.Right + 3 &&
        viewport.Height > 2 * VerticalPadding + 4;

    internal static InlineUIContainer Create(EpubIllustration figure, Size viewport, RoutedEventHandler? open)
    {
        var image = new Image
        {
            Source = figure.Bitmap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform
        };
        var description = string.IsNullOrWhiteSpace(figure.AlternativeText) ? "插图" : figure.AlternativeText;
        AutomationProperties.SetName(image, description);
        UIElement content = image;
        if (open != null)
        {
            var button = new EpubIllustrationButton(figure) { Content = image, IsTabStop = false, ToolTip = "放大插图" };
            button.SetResourceReference(FrameworkElement.StyleProperty, "ReaderIllustrationButton");
            KeyboardNavigation.SetAcceptsReturn(button, true);
            AutomationProperties.SetName(button, $"放大插图 · {description}");
            button.Click += open;
            content = button;
        }
        var frame = new Border
        {
            Child = content,
            Padding = new Thickness(0, VerticalPadding, 0, VerticalPadding),
            IsHitTestVisible = open != null
        };
        ApplySize(figure, frame, image, viewport);
        return new InlineUIContainer(frame) { BaselineAlignment = BaselineAlignment.Bottom, Tag = figure };
    }

    internal static bool Resize(FlowDocument document, Size viewport)
    {
        if (!CanFit(viewport)) return false;
        bool changed = false;
        foreach (var block in document.Blocks)
        {
            if (block is not Paragraph { LineStackingStrategy: LineStackingStrategy.MaxHeight } paragraph) continue;
            foreach (var inline in paragraph.Inlines)
                if (inline is InlineUIContainer { Tag: EpubIllustration figure, Child: Border frame })
                {
                    var image = frame.Child as Image ?? (frame.Child as EpubIllustrationButton)?.Content as Image;
                    if (image != null) changed |= ApplySize(figure, frame, image, viewport);
                }
        }
        return changed;
    }

    internal static EpubIllustration? AtLine(TextPointer line) =>
        (line.GetAdjacentElement(LogicalDirection.Forward) as InlineUIContainer)?.Tag as EpubIllustration;

    internal static InlineUIContainer? Find(Paragraph paragraph, int index)
    {
        foreach (var inline in paragraph.Inlines)
            if (inline is InlineUIContainer { Tag: EpubIllustration figure } container && figure.Index == index) return container;
        return null;
    }

    internal static double? PartialLineTop(FlowDocument document, double viewportTop, double viewportBottom)
    {
        foreach (var block in document.Blocks)
        {
            if (block is not Paragraph { LineStackingStrategy: LineStackingStrategy.MaxHeight } paragraph) continue;
            foreach (var inline in paragraph.Inlines)
            {
                if (inline is not InlineUIContainer { Tag: EpubIllustration }) continue;
                var bounds = inline.ElementStart.GetCharacterRect(LogicalDirection.Forward);
                if (!bounds.IsEmpty && bounds.Top > viewportTop + .1 && bounds.Top < viewportBottom - .1 &&
                    bounds.Bottom > viewportBottom + .1) return bounds.Top;
            }
        }
        return null;
    }

    internal static IEnumerable<EpubIllustrationButton> Buttons(RichTextBox body)
    {
        foreach (var block in body.Document.Blocks)
        {
            if (block is not Paragraph { LineStackingStrategy: LineStackingStrategy.MaxHeight } paragraph) continue;
            foreach (var inline in paragraph.Inlines)
                if (inline is InlineUIContainer { Child: Border { Child: EpubIllustrationButton button } }) yield return button;
        }
    }

    internal static bool IsVisibleIn(RichTextBox body, EpubIllustrationButton button, bool fully = false)
    {
        if (!body.IsVisible || !button.IsVisible || !button.IsArrangeValid || !body.IsAncestorOf(button) ||
            button.RenderSize is not { Width: > 0, Height: > 0 } ||
            body.Template?.FindName("PART_ContentHost", body) is not ScrollViewer { Content: FrameworkElement content } ||
            VisualTreeHelper.GetParent(content) is not ScrollContentPresenter viewport) return false;

        var bounds = new Rect(button.TranslatePoint(new Point(), body), button.RenderSize);
        var view = new Rect(viewport.TranslatePoint(new Point(), body), viewport.RenderSize);
        if (body.Clip is { } clip) view.Intersect(clip.Bounds);
        if (view.IsEmpty) return false;
        if (fully)
        {
            view.Inflate(.5, .5);
            return view.Contains(bounds);
        }
        view.Intersect(bounds);
        return view.Width > 0 && view.Height > 0;
    }

    private static bool ApplySize(EpubIllustration figure, Border frame, Image image, Size viewport)
    {
        var availableWidth = viewport.Width - TextPagination.DocumentPadding.Left - TextPagination.DocumentPadding.Right - 2;
        var availableHeight = viewport.Height - 2 * VerticalPadding - 3;
        var bitmap = figure.Bitmap;
        var scale = Math.Min(1, Math.Min(availableWidth / bitmap.PixelWidth, availableHeight / bitmap.PixelHeight));
        var width = bitmap.PixelWidth * scale;
        var height = bitmap.PixelHeight * scale;
        if (frame.Width == availableWidth && image.Width == width && image.Height == height) return false;
        frame.Width = availableWidth;
        image.Width = width;
        image.Height = height;
        if (frame.Child is EpubIllustrationButton button)
        {
            button.Width = width;
            button.Height = height;
        }
        return true;
    }
}

internal sealed class EpubIllustrationButton(EpubIllustration figure) : Button
{
    internal EpubIllustration Figure { get; } = figure;
}
