using System.Windows;

namespace SeaReader.Native;

internal readonly record struct ReaderPageGeometry(double LeafWidth, double LeafHeight, bool Spread, Thickness StageInsets)
{
    internal const double LeafWidthToHeight = 2.0 / 3.0;
    internal const double DefaultReaderWindowWidth = 980;
    private const double MaximumLeafWidth = 660;
    private const double DefaultReaderMargin = 32;
    internal const double FolioHeight = 22;

    internal double FrameWidth => Spread ? LeafWidth * 2 : LeafWidth;
    internal double FolioInset => Math.Max(0, LeafHeight * .063 - FolioHeight / 2);

    internal static ReaderPageGeometry Measure(double width, double height, bool spread)
    {
        width = Extent(width);
        height = Extent(height);
        var side = Math.Min(width / 2, Math.Clamp(width * .018, 8, 26));
        var vertical = Math.Min(height / 2, Math.Clamp(height * .018, 8, 18));
        var usableWidth = width - 2 * side;
        var usableHeight = height - 2 * vertical;
        var widthLimit = spread ? usableWidth / 2 : usableWidth;
        var leafWidth = Math.Min(widthLimit, Math.Min(MaximumLeafWidth, usableHeight * LeafWidthToHeight));
        var leafHeight = leafWidth / LeafWidthToHeight;
        return new ReaderPageGeometry(leafWidth, leafHeight, spread,
            new Thickness(side, vertical, side, vertical));
    }

    internal static bool CanFitSpread(double width, double height, double fontSize, bool explicitDouble, bool wasSpread)
    {
        var spreadWidth = Measure(width, height, spread: true).LeafWidth;
        var minimum = Math.Clamp(ReadingSize(fontSize) * 16, explicitDouble ? 304 : 330, 520);
        if (wasSpread) minimum -= 10;
        if (spreadWidth < minimum) return false;

        return explicitDouble || spreadWidth >= Measure(width, height, spread: false).LeafWidth * (wasSpread ? .87 : .92);
    }

    // Passing a measured line height adds the remaining fraction of a line to the bottom margin.
    // Without a line height the result is the nominal text padding on the physical leaf.
    internal static (Thickness Primary, Thickness Facing) TextInsets(double leafWidth, double textHeight,
        bool spread, double margin, double lineHeight, double paperInset)
    {
        leafWidth = Extent(leafWidth);
        textHeight = Extent(textHeight);
        paperInset = Extent(paperInset);
        margin = double.IsFinite(margin) ? Math.Clamp(margin, 12, 52) : DefaultReaderMargin;
        var customization = (margin - DefaultReaderMargin) * Math.Min(1, leafWidth / 520);
        var outerLimit = leafWidth * .24;
        var outer = Math.Clamp(leafWidth * .126 - paperInset + customization, Math.Min(8, outerLimit), outerLimit);
        var innerLimit = leafWidth * .18;
        // Based on the text bounds in Apple's Mac reading views. The middle is whitespace on both leaves.
        var inner = spread ?
            Math.Clamp(leafWidth * .0675 - paperInset + customization, Math.Min(6, innerLimit), innerLimit) : outer;
        var top = textHeight * .10;
        var bottom = textHeight * .125;
        var bodyHeight = textHeight - top - bottom;
        if (double.IsFinite(lineHeight) && lineHeight > 0 && bodyHeight > lineHeight)
            bottom += Math.Max(0, bodyHeight - Math.Floor((bodyHeight + .05) / lineHeight) * lineHeight);

        return (new Thickness(outer, top, inner, bottom), new Thickness(inner, top, outer, bottom));
    }

    private static double ReadingSize(double fontSize) => Extent(fontSize) > 0 ? Math.Clamp(fontSize, 12, 44) : 18;

    private static double Extent(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
