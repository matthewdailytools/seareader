using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace SeaReader.Native;

public partial class App : Application
{
    private readonly CancellationTokenSource closing = new();
    private Mutex? instance;
    private bool ownsInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string data = Path.GetFullPath(Environment.GetEnvironmentVariable("SEAREADER_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "seareader"));
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..20];
            string channel = "SeaReader-" + identity;
            instance = new Mutex(true, @"Local\" + channel, out ownsInstance);
            var files = e.Args.Where(Path.IsPathFullyQualified).ToArray();
            if (!ownsInstance)
            {
                using var client = new NamedPipeClientStream(".", channel, PipeDirection.Out, PipeOptions.Asynchronous);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(timeout.Token);
                await JsonSerializer.SerializeAsync(client, files, cancellationToken: timeout.Token);
                Shutdown();
                return;
            }

            var store = new LibraryStore(data);
            await store.InitializeAsync();
            var window = new MainWindow(store, files);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            _ = ReceiveFilesAsync(channel, window, closing.Token);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "SeaReader", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static async Task ReceiveFilesAsync(string channel, MainWindow window, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(channel, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var files = await JsonSerializer.DeserializeAsync<string[]>(server, cancellationToken: deadline.Token)
                    .ConfigureAwait(false);
                if (files is null) continue;
                await window.Dispatcher.InvokeAsync(async () =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                    await window.OpenFilesAsync(files.Where(Path.IsPathFullyQualified));
                }).Task.Unwrap().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (JsonException) { }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        closing.Cancel();
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
