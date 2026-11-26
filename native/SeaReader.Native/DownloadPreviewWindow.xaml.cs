using System.IO;
using System.Windows;

namespace SeaReader.Native;

public partial class DownloadPreviewWindow : Window
{
    private const int RowsPerScreen = 36;
    private readonly string path;
    private readonly Func<CancellationToken, Task<BookEntry>> import;
    private readonly CancellationTokenSource lifetime = new();
    private TextDocument? text;
    private IChapterBook? chapterBook;
    private PdfBook? pdf;
    private int chapter;
    private int page;
    private bool reading;
    private bool importing;
    private bool imported;
    private bool closed;

    internal DownloadPreviewWindow(string path, Func<CancellationToken, Task<BookEntry>> import)
    {
        this.path = path;
        this.import = import;
        InitializeComponent();
        Title = $"临时阅读 · {Path.GetFileName(path)}";
        UpdateControls();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ReadAsync(async token =>
        {
            var format = await BookFormats.DetectFileAsync(path, token);
            if (BookFormats.IsChapterBook(format))
            {
                chapterBook = await BookFormats.ReadChaptersAsync(path, format, token);
                await ReadChapterAsync(0, token);
            }
            else if (format == "pdf")
            {
                pdf = await PdfBook.OpenAsync(path, token);
                PreviousButton.Content = "上一页";
                NextButton.Content = "下一页";
            }
            else text = await TextDocument.LoadAsync(path, cancellationToken: token);
            await RenderAsync(token);
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private async void OnPrevious(object sender, RoutedEventArgs e) => await MoveAsync(-1);

    private async void OnNext(object sender, RoutedEventArgs e) => await MoveAsync(1);

    private Task MoveAsync(int direction) => ReadAsync(async token =>
    {
        if (pdf is not null) page = Math.Clamp(page + direction, 0, pdf.PageCount - 1);
        else if (text is not null)
        {
            int next = page + direction;
            if (next >= 0 && next < TextPageCount) page = next;
            else if (chapterBook is not null)
            {
                await ReadChapterAsync(chapter + direction, token);
                page = direction < 0 ? TextPageCount - 1 : 0;
            }
        }
        await RenderAsync(token);
    });

    private async Task ReadChapterAsync(int index, CancellationToken token)
    {
        if (chapterBook is null) return;
        string content = await chapterBook.ReadChapterAsync(index, token);
        var document = await Task.Run(() => TextDocument.FromText(content), token);
        token.ThrowIfCancellationRequested();
        text = document;
        chapter = index;
    }

    private int TextPageCount => text is null ? 0 : (text.Lines.Count + RowsPerScreen - 1) / RowsPerScreen;

    private async Task RenderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pdf is not null)
        {
            var bitmap = await pdf.RenderPageAsync(page, 1400, token);
            token.ThrowIfCancellationRequested();
            PdfImage.Source = bitmap;
            PdfView.Visibility = Visibility.Visible;
            PdfView.ScrollToTop();
            Position.Text = $"第 {page + 1} / {pdf.PageCount} 页";
        }
        else if (text is not null)
        {
            int first = page * RowsPerScreen;
            int end = Math.Min(text.Lines.Count, first + RowsPerScreen);
            int startOffset = text.Lines[first].Offset;
            int endOffset = end == text.Lines.Count ? text.Text.Length : text.Lines[end].Offset;
            PreviewText.Text = text.Text[startOffset..endOffset];
            PreviewText.Visibility = Visibility.Visible;
            PreviewText.ScrollToHome();
            var chapterTitle = chapterBook is null ? "" : chapterBook.Chapters[chapter].Title + " · ";
            Position.Text = $"{chapterTitle}第 {page + 1} / {TextPageCount} 屏";
        }
    }

    private async Task ReadAsync(Func<CancellationToken, Task> read)
    {
        if (closed || reading) return;
        reading = true;
        UpdateControls();
        Status.Text = "正在加载…";
        try
        {
            await read(lifetime.Token);
            if (!closed) Status.Text = "";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex) { if (!closed) Status.Text = ex.Message; }
        finally
        {
            reading = false;
            if (!closed) UpdateControls();
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (closed || importing || imported) return;
        importing = true;
        UpdateControls();
        try
        {
            var book = await import(lifetime.Token);
            imported = true;
            if (!closed) Status.Text = $"已将《{book.Title}》加入书库。";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex) { if (!closed) Status.Text = ex.Message; }
        finally
        {
            importing = false;
            if (!closed) UpdateControls();
        }
    }

    private void UpdateControls()
    {
        int count = pdf?.PageCount ?? TextPageCount;
        PreviousButton.IsEnabled = !reading && (page > 0 || chapter > 0);
        NextButton.IsEnabled = !reading && (page + 1 < count || (chapterBook is not null && chapter + 1 < chapterBook.Chapters.Count));
        ImportButton.IsEnabled = !reading && !importing && !imported && count > 0;
    }
}
