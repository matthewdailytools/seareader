using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.ApplicationModel.DataTransfer;

namespace SeaReader.Native;

internal sealed class ReaderShare(Window owner) : IDisposable
{
    private const int RequestLifetimeMilliseconds = 10_000;

    private sealed record SharePayload(string Title, string Text, long ExpiresAt);

    private Window? window = owner;
    private DataTransferManager? transferManager;
    private DispatcherTimer? expiry;
    private SharePayload? pending;

    internal void Show(string title, string text)
    {
        var source = window ?? throw new ObjectDisposedException(nameof(ReaderShare));
        source.Dispatcher.VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var handle = new WindowInteropHelper(source).Handle;
        if (handle == IntPtr.Zero) throw new InvalidOperationException("阅读窗口尚未准备好分享。");

        var payload = new SharePayload(title, text, Environment.TickCount64 + RequestLifetimeMilliseconds);
        if (Interlocked.CompareExchange(ref pending, payload, null) != null)
            throw new InvalidOperationException("Windows 正在准备分享，请稍后重试。");

        try
        {
            if (transferManager == null)
            {
                var manager = DataTransferManagerInterop.GetForWindow(handle);
                manager.DataRequested += OnDataRequested;
                transferManager = manager;
            }
            if (expiry == null)
            {
                expiry = new DispatcherTimer(DispatcherPriority.Background, source.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(RequestLifetimeMilliseconds)
                };
                expiry.Tick += OnExpiry;
            }
            expiry.Stop();
            expiry.Start();
            DataTransferManagerInterop.ShowShareUIForWindow(handle);
        }
        catch (Exception error) when (IsShareInteropFailure(error))
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref pending, null, payload), payload)) expiry?.Stop();
            throw new InvalidOperationException("无法打开 Windows 分享面板。", error);
        }
    }

    private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs e)
    {
        var payload = TakePayload();
        if (payload == null) return;
        DataRequest? request = null;
        try
        {
            request = e.Request;
            PopulateShareData(request.Data, payload);
        }
        catch (Exception error) when (IsShareInteropFailure(error) || error is ArgumentException)
        {
            try { request?.FailWithDisplayText("无法准备要分享的内容。"); }
            catch (Exception failure) when (IsShareInteropFailure(failure)) { }
        }
    }

    private SharePayload? TakePayload()
    {
        var payload = Interlocked.Exchange(ref pending, null);
        return payload != null && Environment.TickCount64 < payload.ExpiresAt ? payload : null;
    }

    private static void PopulateShareData(DataPackage data, SharePayload payload)
    {
        data.Properties.Title = payload.Title;
        data.SetText(payload.Text);
        data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void OnExpiry(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref pending, null);
        expiry?.Stop();
    }

    public void Dispose()
    {
        if (window is not { } source) return;
        source.Dispatcher.VerifyAccess();
        window = null;
        Interlocked.Exchange(ref pending, null);
        if (expiry is { } timer)
        {
            expiry = null;
            timer.Stop();
            timer.Tick -= OnExpiry;
        }
        if (transferManager is { } manager)
        {
            transferManager = null;
            try { manager.DataRequested -= OnDataRequested; }
            catch (Exception error) when (IsShareInteropFailure(error)) { }
        }
    }

    private static bool IsShareInteropFailure(Exception error) => error is ExternalException or InvalidOperationException or
        NotSupportedException or InvalidCastException or TypeLoadException or TypeInitializationException or UnauthorizedAccessException;
}
