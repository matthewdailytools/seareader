using System.Runtime.InteropServices;

namespace SeaReader.Native;

internal static class ReaderWindowGeometry
{
    internal const uint DefaultToNearestMonitor = 2;
    internal const uint NoZOrder = 0x0004;
    internal const uint NoActivate = 0x0010;

    internal static bool TryGetMonitorInfo(nint monitor, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != 0 && GetMonitorInfo(monitor, ref info);
    }

    internal readonly struct PhysicalCoordinates : IDisposable
    {
        // Window and monitor rectangles need to use the same virtual desktop coordinates.
        private readonly nint previous;

        public PhysicalCoordinates() => previous = SetThreadDpiAwarenessContext(new nint(-4));

        public void Dispose()
        {
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint handle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint handle, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
