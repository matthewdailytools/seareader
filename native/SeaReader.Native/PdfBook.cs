using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SeaReader.Native;

public sealed class PdfBook : IPageBook
{
    private const int MaximumPageDimension = 4096;
    private const int ThumbnailWidth = 192;
    private const int ThumbnailHeight = 256;
    private readonly PdfDocument _document;
    private readonly string _path;
    private readonly SemaphoreSlim _renderLock = new(1, 1);

    private PdfBook(PdfDocument document, string path)
    {
        _document = document;
        _path = path;
    }

    public int PageCount => checked((int)_document.PageCount);

    public static async Task<PdfBook> OpenAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var file = await StorageFile.GetFileFromPathAsync(fullPath).AsTask(ct).ConfigureAwait(false);
            var document = await PdfDocument.LoadFromFileAsync(file).AsTask(ct).ConfigureAwait(false);
            if (document.PageCount == 0) throw new InvalidDataException("PDF 不含可阅读的页面。");
            return new PdfBook(document, fullPath);
        }
        catch (COMException ex)
        {
            throw new InvalidDataException("无法打开 PDF。文件可能损坏或需要密码。", ex);
        }
    }

    internal Task<PdfTextPage> ReadTextPageAsync(int index, CancellationToken ct) => Task.Run(() =>
    {
        using var source = UglyToad.PdfPig.PdfDocument.Open(_path);
        ct.ThrowIfCancellationRequested();
        return PdfTextPage.Read(source.GetPage(index + 1), ct);
    }, ct);

    internal Task<IReadOnlyList<PdfTextHit>> SearchAsync(string query, IReadOnlyDictionary<int, string> recognized,
        int limit, CancellationToken ct, Func<IReadOnlyList<PdfTextHit>, Task>? publish = null) => Task.Run<IReadOnlyList<PdfTextHit>>(async () =>
    {
        var results = new List<PdfTextHit>();
        using var source = UglyToad.PdfPig.PdfDocument.Open(_path);
        for (int index = 0; index < PageCount && results.Count < limit; index++)
        {
            ct.ThrowIfCancellationRequested();
            var page = PdfTextPage.Read(source.GetPage(index + 1), ct);
            var matches = PdfTextPage.Find(page.Text, query, index, limit - results.Count);
            if (matches.Count == 0 && recognized.TryGetValue(index + 1, out var ocrText))
                matches = PdfTextPage.Find(ocrText, query, index, limit - results.Count, fromOcr: true);
            if (matches.Count == 0) continue;

            ct.ThrowIfCancellationRequested();
            results.AddRange(matches);
            if (publish != null) await publish(matches).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        return results;
    }, ct);

    public Task<BitmapSource> RenderPageAsync(int zeroBasedIndex, int pixelWidth, CancellationToken ct = default) =>
        RenderAsync(zeroBasedIndex, Math.Min(pixelWidth, MaximumPageDimension), MaximumPageDimension, ct);

    internal Task<BitmapSource> RenderThumbnailAsync(int zeroBasedIndex, CancellationToken ct = default) =>
        RenderAsync(zeroBasedIndex, ThumbnailWidth, ThumbnailHeight, ct);

    Task<System.Windows.Size> IPageBook.ReadPageSizeAsync(int index, CancellationToken token) => ReadPageSizeAsync(index, token);
    Task<BitmapSource> IPageBook.RenderThumbnailAsync(int index, CancellationToken token) => RenderThumbnailAsync(index, token);
    Task<PdfTextPage> IPageBook.ReadTextPageAsync(int index, CancellationToken token) => ReadTextPageAsync(index, token);
    Task<IReadOnlyList<PdfTextHit>> IPageBook.SearchAsync(string query, IReadOnlyDictionary<int, string> recognized,
        int limit, CancellationToken token, Func<IReadOnlyList<PdfTextHit>, Task>? publish) => SearchAsync(query, recognized, limit, token, publish);

    internal async Task<System.Windows.Size> ReadPageSizeAsync(int index, CancellationToken token)
    {
        await _renderLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var page = _document.GetPage((uint)index);
                var size = page.Size;
                if (size.Width <= 0 || size.Height <= 0 || !double.IsFinite(size.Width) || !double.IsFinite(size.Height))
                    throw new InvalidDataException("PDF 页面尺寸无效。");
                return new System.Windows.Size(size.Width, size.Height);
            }, token).ConfigureAwait(false);
        }
        finally { _renderLock.Release(); }
    }

    private async Task<BitmapSource> RenderAsync(int zeroBasedIndex, int pixelWidth, int pixelHeight, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zeroBasedIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(zeroBasedIndex, PageCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        await _renderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                using var page = _document.GetPage((uint)zeroBasedIndex);
                var size = page.Size;
                if (size.Width <= 0 || size.Height <= 0 || !double.IsFinite(size.Width) || !double.IsFinite(size.Height))
                    throw new InvalidDataException("PDF 页面尺寸无效。");
                var scale = Math.Min(pixelWidth / size.Width, pixelHeight / size.Height);
                int width = (int)Math.Clamp(Math.Round(size.Width * scale), 1, pixelWidth);
                int height = (int)Math.Clamp(Math.Round(size.Height * scale), 1, pixelHeight);
                // Windows.Data.Pdf takes DIPs and creates a physical bitmap at the system DPI.
                double deviceScale = Math.Max(GetDpiForSystem(), 96u) / 96.0;
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Ceiling(width / deviceScale),
                    DestinationHeight = (uint)Math.Ceiling(height / deviceScale)
                };

                using var rendered = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(rendered, options).AsTask(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                rendered.Seek(0);
                using var stream = rendered.AsStreamForRead();
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = width;
                bitmap.DecodePixelHeight = height;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }, ct).ConfigureAwait(false);
        }
        catch (COMException ex)
        {
            throw new InvalidDataException($"无法渲染 PDF 第 {zeroBasedIndex + 1} 页。", ex);
        }
        finally
        {
            _renderLock.Release();
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
