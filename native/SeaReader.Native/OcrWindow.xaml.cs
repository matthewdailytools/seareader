using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SeaReader.Native;

public partial class OcrWindow : Window
{
    private readonly LibraryStore store;
    private readonly string? bookId;
    private readonly int? page;
    private readonly BitmapSource? currentPage;
    private readonly Action<string>? translate;
    private BitmapSource? image;
    private CancellationTokenSource? operation;
    private bool pageImageSelected;
    private bool savingPage;
    private bool closed;

    public OcrWindow(LibraryStore store, string? bookId = null, int? page = null,
        BitmapSource? currentPage = null, Action<string>? translate = null)
    {
        this.store = store;
        this.bookId = bookId;
        this.page = page;
        this.currentPage = currentPage;
        this.translate = translate;
        InitializeComponent();
        UsePageButton.Visibility = currentPage == null ? Visibility.Collapsed : Visibility.Visible;
        SavePageButton.Visibility = currentPage == null || bookId == null || page is null
            ? Visibility.Collapsed : Visibility.Visible;
        TranslateButton.Visibility = translate == null ? Visibility.Collapsed : Visibility.Visible;
        if (currentPage != null) SetImage(currentPage, true);
        else UpdateButtons();
        Loaded += InitializePageAsync;
    }

    private async void InitializePageAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= InitializePageAsync;
        if (!pageImageSelected || bookId == null || page is not int pageNumber) return;
        using var loading = new CancellationTokenSource();
        operation = loading;
        UpdateButtons();
        try
        {
            string? text = await store.GetRecognizedPageAsync(bookId, pageNumber);
            if (closed || loading.IsCancellationRequested) return;
            if (text != null) Result.Text = text;
        }
        catch (Exception error) { SetStatus(error.Message); }
        finally
        {
            operation = null;
            if (!closed) UpdateButtons();
        }
    }

    private void SetImage(BitmapSource value, bool isPage)
    {
        image = value;
        pageImageSelected = isPage;
        Preview.Source = value;
        ImageHint.Visibility = Visibility.Collapsed;
        Result.Clear();
        SetStatus(isPage ? $"PDF 第 {page} 页" : "");
        UpdateButtons();
    }

    private async void OnChooseImage(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp|所有文件|*.*",
            Title = "选择需要识别的图片"
        };
        if (picker.ShowDialog(this) != true) return;
        using var request = new CancellationTokenSource();
        operation = request;
        SetStatus("正在读取图片…");
        UpdateButtons();
        try
        {
            var photo = await OcrEngine.LoadImageAsync(picker.FileName, request.Token);
            if (!closed) SetImage(photo, false);
        }
        catch (OperationCanceledException) { SetStatus("已取消。"); }
        catch (Exception error) { SetStatus($"无法打开图片。{error.Message}"); }
        finally
        {
            operation = null;
            if (!closed) UpdateButtons();
        }
    }

    private void OnUsePage(object sender, RoutedEventArgs e)
    {
        if (currentPage != null && !pageImageSelected)
        {
            SetImage(currentPage, true);
            InitializePageAsync(sender, e);
        }
    }

    private async void OnRecognize(object sender, RoutedEventArgs e)
    {
        if (image == null || operation != null || RecognitionLanguage.SelectedItem is not ComboBoxItem { Tag: string choice }) return;
        using var request = new CancellationTokenSource();
        operation = request;
        SetStatus("正在本机识别…");
        UpdateButtons();
        try
        {
            string text = await OcrEngine.RecognizeAsync(image, choice, request.Token);
            if (closed || request.IsCancellationRequested) return;
            Result.Text = text;
            SetStatus(text.Length == 0 ? "未识别到文字。" : $"已识别 {text.Length} 个字符。");
        }
        catch (OperationCanceledException) { SetStatus("已取消识别。"); }
        catch (Exception error) { SetStatus(error.Message); }
        finally
        {
            operation = null;
            if (!closed) UpdateButtons();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        operation?.Cancel();
        CancelButton.IsEnabled = false;
        SetStatus("正在取消…");
    }

    private void OnResultChanged(object sender, TextChangedEventArgs e)
    {
        if (CopyButton != null) UpdateButtons();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Result.Text);
            SetStatus("已复制。");
        }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("剪贴板忙，请重试。"); }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "TXT 文本|*.txt", FileName = "OCR.txt", AddExtension = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            await File.WriteAllTextAsync(picker.FileName, Result.Text, Encoding.UTF8);
            SetStatus("已导出。");
        }
        catch (Exception error) { SetStatus(error.Message); }
    }

    private async void OnSavePage(object sender, RoutedEventArgs e)
    {
        if (!pageImageSelected || bookId == null || page == null || operation != null || savingPage) return;
        savingPage = true;
        UpdateButtons();
        try
        {
            await store.SaveRecognizedPageAsync(bookId, page.Value, Result.Text);
            SetStatus(Result.Text.Trim().Length == 0 ? "已清除本页识别文字。" : "已保存本页识别文字。");
        }
        catch (Exception error) { SetStatus(error.Message); }
        finally
        {
            savingPage = false;
            if (!closed) UpdateButtons();
        }
    }

    private void OnTranslate(object sender, RoutedEventArgs e)
    {
        string text = Result.Text.Trim();
        if (translate == null || text.Length == 0) return;
        Close();
        Dispatcher.InvokeAsync(() => translate(text));
    }

    private void SetStatus(string text)
    {
        if (closed) return;
        Status.Text = text;
        UIElementAutomationPeer.FromElement(Status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void UpdateButtons()
    {
        bool busy = operation != null || savingPage;
        bool hasText = !string.IsNullOrWhiteSpace(Result.Text);
        ChooseImageButton.IsEnabled = !busy;
        UsePageButton.IsEnabled = !busy && !pageImageSelected;
        RecognitionLanguage.IsEnabled = !busy;
        RecognizeButton.IsEnabled = !busy && image != null;
        CancelButton.Visibility = operation == null ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.IsEnabled = operation is { IsCancellationRequested: false };
        Result.IsReadOnly = busy;
        CopyButton.IsEnabled = !busy && hasText;
        ExportButton.IsEnabled = !busy && hasText;
        SavePageButton.IsEnabled = !busy && pageImageSelected;
        TranslateButton.IsEnabled = !busy && hasText;
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        operation?.Cancel();
        base.OnClosed(e);
    }
}
