using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static SeaReader.Native.ReaderWindowGeometry;

namespace SeaReader.Native;

internal sealed class ReaderWindowPlacement(MainWindow window)
{
    internal sealed record SavedBounds(int Left, int Top, int Width, int Height, int WorkLeft, int WorkTop,
        int MonitorX, int MonitorY, int Scale, bool Maximized)
    {
        internal bool IsValid => Width > 0 && Height > 0 && Scale is >= 50 and <= 1000;
    }

    private nint handle;
    private SavedBounds? normal;
    private bool maximized;

    internal void Prepare(SavedBounds? saved, Window? origin)
    {
        var originHandle = origin == null ? 0 : new WindowInteropHelper(origin).Handle;
        window.SourceInitialized += OnSourceInitialized;

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            handle = new WindowInteropHelper(window).Handle;
            Restore(saved is { IsValid: true } ? saved : null, originHandle);
            window.LocationChanged += (_, _) => RememberWindowedBounds();
            window.SizeChanged += (_, _) => RememberWindowedBounds();
            window.StateChanged += (_, _) => RememberWindowedBounds();
            if (saved is { IsValid: true, Maximized: true }) window.WindowState = WindowState.Maximized;
        }
    }

    internal SavedBounds? Capture()
    {
        RememberWindowedBounds();
        return normal is { } bounds ? bounds with { Maximized = maximized } : null;
    }

    private void Restore(SavedBounds? saved, nint origin)
    {
        using var coordinates = new PhysicalCoordinates();
        var initialMonitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        var savedMonitor = saved == null ? 0 : MonitorFromPoint(new NativePoint(saved.MonitorX, saved.MonitorY), 0);
        var target = savedMonitor != 0 ? savedMonitor : origin != 0
            ? MonitorFromWindow(origin, DefaultToNearestMonitor) : initialMonitor;
        if (!TryGetMonitorInfo(target, out var screen) || screen.Work.Width <= 0 || screen.Work.Height <= 0 ||
            !GetWindowRect(handle, out var initial)) return;
        var scale = MonitorScale(target);
        var minimumWidth = window.MinWidth * scale / 100;
        var minimumHeight = window.MinHeight * scale / 100;
        var placed = Fit(screen.Work, initial, MonitorScale(initialMonitor), scale, minimumWidth, minimumHeight,
            saved, savedMonitor != 0);
        if (minimumWidth > screen.Work.Width) window.MinWidth = screen.Work.Width * 100.0 / scale;
        if (minimumHeight > screen.Work.Height) window.MinHeight = screen.Work.Height * 100.0 / scale;
        if (placed != initial && !SetWindowPos(handle, 0, placed.Left, placed.Top, placed.Width, placed.Height, NoZOrder | NoActivate)) return;
        normal = SaveBounds(placed, screen, scale);
    }

    private void RememberWindowedBounds()
    {
        if (handle == 0 || !window.CanRememberWindowedBounds || IsIconic(handle)) return;
        maximized = IsZoomed(handle);
        using var coordinates = new PhysicalCoordinates();
        var monitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        if (!TryGetMonitorInfo(monitor, out var screen)) return;
        var scale = MonitorScale(monitor);
        if (maximized)
        {
            // A maximized window can be moved to another monitor before it is closed.
            var center = MonitorCenter(screen.Monitor);
            if (normal is not { } saved || (saved.MonitorX, saved.MonitorY) == (center.X, center.Y) ||
                screen.Work.Width <= 0 || screen.Work.Height <= 0) return;
            var placed = Fit(screen.Work, default, scale, scale, window.MinWidth * scale / 100,
                window.MinHeight * scale / 100, saved, onSavedMonitor: true);
            normal = SaveBounds(placed, screen, scale);
            return;
        }
        if (GetWindowRect(handle, out var bounds) && bounds.Width > 0 && bounds.Height > 0)
            normal = SaveBounds(bounds, screen, scale);
    }

    private static SavedBounds SaveBounds(NativeRect bounds, MonitorInfo screen, int scale)
    {
        var center = MonitorCenter(screen.Monitor);
        return new SavedBounds(bounds.Left, bounds.Top, bounds.Width, bounds.Height, screen.Work.Left, screen.Work.Top,
            center.X, center.Y, scale, false);
    }

    private static NativePoint MonitorCenter(NativeRect bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    internal static NativeRect Fit(NativeRect work, NativeRect initial, int initialScale, int scale,
        double minimumWidth, double minimumHeight, SavedBounds? saved, bool onSavedMonitor)
    {
        var factor = (double)scale / (saved?.Scale ?? initialScale);
        var desiredWidth = Math.Max(1, (saved?.Width ?? initial.Width) * factor);
        var desiredHeight = Math.Max(1, (saved?.Height ?? initial.Height) * factor);
        var shrink = Math.Min(1, Math.Min(work.Width / desiredWidth, work.Height / desiredHeight));
        var width = (int)Math.Clamp(Math.Round(desiredWidth * shrink), Math.Min(minimumWidth, work.Width), work.Width);
        var height = (int)Math.Clamp(Math.Round(desiredHeight * shrink), Math.Min(minimumHeight, work.Height), work.Height);
        var left = onSavedMonitor && saved != null ? work.Left + (saved.Left - (double)saved.WorkLeft) * factor :
            work.Left + (work.Width - width) / 2.0;
        var top = onSavedMonitor && saved != null ? work.Top + (saved.Top - (double)saved.WorkTop) * factor :
            work.Top + (work.Height - height) / 2.0;
        var x = (int)Math.Clamp(Math.Round(left), work.Left, work.Right - width);
        var y = (int)Math.Clamp(Math.Round(top), work.Top, work.Bottom - height);
        return new NativeRect(x, y, x + width, y + height);
    }

    private static int MonitorScale(nint monitor)
    {
        _ = GetScaleFactorForMonitor(monitor, out var scale);
        return scale is >= 50 and <= 1000 ? scale : 100;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint handle);

    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(nint monitor, out int scale);
}
