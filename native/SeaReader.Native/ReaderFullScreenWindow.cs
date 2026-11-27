using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using static SeaReader.Native.ReaderWindowGeometry;

namespace SeaReader.Native;

internal sealed class ReaderFullScreenWindow(Window window)
{
    private const uint ShowNoActivate = 4;
    private const uint ShowMaximized = 3;
    private const uint FrameChanged = 0x0020;
    private const int DisplayChanged = 0x007e;
    private const int WindowPositionChanged = 0x0047;
    private const int DpiChanged = 0x02e0;

    private sealed record WindowSnapshot(WindowStyle Style, ResizeMode ResizeMode, NativePlacement Placement);

    private WindowSnapshot? savedWindow;
    private HwndSource? source;
    private DispatcherOperation? pendingFit;

    internal bool IsActive => savedWindow != null;

    internal bool TryEnter()
    {
        window.VerifyAccess();
        if (IsActive) return true;
        if (window.WindowState == WindowState.Minimized ||
            PresentationSource.FromVisual(window) is not HwndSource { IsDisposed: false } hwndSource) return false;

        var handle = hwndSource.Handle;
        if (!TryReadWindow(handle, out var placement, out var monitor)) return false;
        var snapshot = new WindowSnapshot(window.WindowStyle, window.ResizeMode, placement);
        if (placement.ShowCommand == ShowMaximized)
        {
            placement.ShowCommand = ShowNoActivate;
            if (!ApplyPlacement(handle, placement)) return false;
        }

        window.SetCurrentValue(Window.WindowStyleProperty, WindowStyle.None);
        window.SetCurrentValue(Window.ResizeModeProperty, ResizeMode.NoResize);
        if (!PlaceOnMonitor(handle, monitor, FrameChanged))
        {
            RestoreWindow(handle, snapshot);
            return false;
        }

        savedWindow = snapshot;
        source = hwndSource;
        source.AddHook(OnWindowMessage);
        window.Closed += OnWindowClosed;
        SetShellFullScreen(handle, true);
        return true;
    }

    internal bool Exit()
    {
        window.VerifyAccess();
        if (savedWindow is not { } snapshot || source is not { IsDisposed: false } hwndSource) return false;

        var handle = hwndSource.Handle;
        Detach();
        var restored = RestoreWindow(handle, snapshot);
        SetShellFullScreen(handle, false);
        return restored;
    }

    private bool RestoreWindow(nint handle, WindowSnapshot snapshot)
    {
        window.SetCurrentValue(Window.WindowStyleProperty, snapshot.Style);
        window.SetCurrentValue(Window.ResizeModeProperty, snapshot.ResizeMode);
        return ApplyPlacement(handle, snapshot.Placement);
    }

    private nint OnWindowMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is DisplayChanged or DpiChanged or WindowPositionChanged && pendingFit == null &&
            !window.Dispatcher.HasShutdownStarted)
            pendingFit = window.Dispatcher.InvokeAsync(FitCurrentMonitor, DispatcherPriority.Loaded);
        return 0;
    }

    private void FitCurrentMonitor()
    {
        try
        {
            if (source is not { IsDisposed: false } hwndSource || window.WindowState == WindowState.Minimized) return;
            var handle = hwndSource.Handle;
            using var coordinates = new PhysicalCoordinates();
            if (!TryGetMonitorBounds(handle, out var monitor)) return;
            if (GetWindowRect(handle, out var current) && current == monitor) return;
            SetWindowPos(handle, 0, monitor.Left, monitor.Top, monitor.Width, monitor.Height, NoZOrder | NoActivate);
        }
        finally { pendingFit = null; }
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Detach();

    private void Detach()
    {
        if (source is { IsDisposed: false } hwndSource) hwndSource.RemoveHook(OnWindowMessage);
        window.Closed -= OnWindowClosed;
        pendingFit?.Abort();
        pendingFit = null;
        source = null;
        savedWindow = null;
    }

    private static bool TryReadWindow(nint handle, out NativePlacement placement, out NativeRect monitor)
    {
        using var coordinates = new PhysicalCoordinates();
        placement = new NativePlacement { Size = Marshal.SizeOf<NativePlacement>() };
        return TryGetMonitorBounds(handle, out monitor) && GetWindowPlacement(handle, ref placement);
    }

    private static bool ApplyPlacement(nint handle, NativePlacement placement)
    {
        using var coordinates = new PhysicalCoordinates();
        // The native placement uses work-area coordinates for ordinary windows.
        // Return it through the matching API to preserve the correct position near the taskbar.
        if (placement.ShowCommand != ShowMaximized) placement.ShowCommand = ShowNoActivate;
        return SetWindowPlacement(handle, in placement);
    }

    private static bool PlaceOnMonitor(nint handle, NativeRect monitor, uint flags)
    {
        using var coordinates = new PhysicalCoordinates();
        return SetWindowPos(handle, 0, monitor.Left, monitor.Top, monitor.Width, monitor.Height, flags | NoZOrder | NoActivate);
    }

    private static bool TryGetMonitorBounds(nint handle, out NativeRect bounds)
    {
        var monitor = MonitorFromWindow(handle, DefaultToNearestMonitor);
        var found = TryGetMonitorInfo(monitor, out var info);
        bounds = info.Monitor;
        return found && bounds.Width > 0 && bounds.Height > 0;
    }

    private void SetShellFullScreen(nint handle, bool enabled)
    {
        if (!window.ShowInTaskbar) return;
        object? instance = null;
        try
        {
            instance = new TaskbarList();
            if (instance is ITaskbarList2 taskbar && taskbar.HrInit() >= 0)
                taskbar.MarkFullscreenWindow(handle, enabled);
        }
        catch (COMException) { } // Explorer can detect the borderless bounds if the shell is unavailable.
        finally
        {
            if (instance != null) Marshal.ReleaseComObject(instance);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        internal int Size;
        internal uint Flags;
        internal uint ShowCommand;
        internal int MinimumX;
        internal int MinimumY;
        internal int MaximumX;
        internal int MaximumY;
        internal NativeRect RestoreBounds;
    }

    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private sealed class TaskbarList { }

    [ComImport, Guid("602D4995-B13A-429B-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint window);
        [PreserveSig] int DeleteTab(nint window);
        [PreserveSig] int ActivateTab(nint window);
        [PreserveSig] int SetActiveAlt(nint window);
        [PreserveSig] int MarkFullscreenWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint handle, ref NativePlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint handle, in NativePlacement placement);

}
