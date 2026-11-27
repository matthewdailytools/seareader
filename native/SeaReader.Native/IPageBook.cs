using System.Windows;
using System.Windows.Media.Imaging;

namespace SeaReader.Native;

internal interface IPageBook
{
    int PageCount { get; }
    Task<Size> ReadPageSizeAsync(int index, CancellationToken token);
    Task<BitmapSource> RenderPageAsync(int index, int pixelWidth, CancellationToken token);
    Task<BitmapSource> RenderThumbnailAsync(int index, CancellationToken token);
    Task<PdfTextPage> ReadTextPageAsync(int index, CancellationToken token);
    Task<IReadOnlyList<PdfTextHit>> SearchAsync(string query, IReadOnlyDictionary<int, string> recognized,
        int limit, CancellationToken token, Func<IReadOnlyList<PdfTextHit>, Task>? publish = null);
}
