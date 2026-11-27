using System.Windows;

namespace SeaReader.Native;

internal static class PdfDisplayGeometry
{
    internal const double PageInset = 12;
    internal const double FacingGap = 10;

    internal static (Size Primary, Size Facing) Measure(Size primary, Size? facing, Size viewport, string zoom) =>
        Measure(primary, facing, viewport, zoom, FacingGap);

    internal static (Size Primary, Size Facing) Measure(Size primary, Size? facing, Size viewport, string zoom, double facingGap)
    {
        if (!IsPageSize(primary)) throw new ArgumentOutOfRangeException(nameof(primary));
        if (facing is Size other && !IsPageSize(other)) throw new ArgumentOutOfRangeException(nameof(facing));

        var gap = facing.HasValue ? facingGap : 0;
        var width = Math.Max(1, Extent(viewport.Width) - 2 * PageInset - gap);
        var height = Math.Max(1, Extent(viewport.Height) - 2 * PageInset);
        var widthScale = width / (primary.Width + (facing?.Width ?? 0));
        var heightScale = height / Math.Max(primary.Height, facing?.Height ?? 0);
        var scale = zoom switch
        {
            "width" => widthScale,
            "150" => widthScale * 1.5,
            _ => Math.Min(widthScale, heightScale)
        };

        return (Scale(primary, scale), facing is Size second ? Scale(second, scale) : Size.Empty);
    }

    private static Size Scale(Size size, double scale) => new(size.Width * scale, size.Height * scale);

    private static bool IsPageSize(Size size) => Extent(size.Width) > 0 && Extent(size.Height) > 0;

    private static double Extent(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
