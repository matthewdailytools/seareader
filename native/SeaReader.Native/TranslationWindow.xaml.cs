using System.Windows;

namespace SeaReader.Native;

public partial class TranslationWindow : Window
{
    private readonly NativeTranslation translator;
    private CancellationTokenSource? active;

    public TranslationWindow(LibraryStore store, string selectedText)
    {
        InitializeComponent();
        translator = new NativeTranslation(store.DataDirectory);
        if (selectedText.Length > 6000)
        {
            int length = char.IsHighSurrogate(selectedText[5999]) ? 5999 : 6000;
            selectedText = selectedText[..length];
            StatusText.Text = "选区较长，已载入前 6000 字符以内的内容。";
        }
        InputText.Text = selectedText;
        var detected = NativeTranslation.DetectLanguage(selectedText);
        TargetLanguage.SelectedValue = detected == "zh" ? "en" : "zh";
        Closed += (_, _) => active?.Cancel();
    }

    private async void OnTranslate(object sender, RoutedEventArgs e) => await RunAsync(false);
    private async void OnInstall(object sender, RoutedEventArgs e) => await RunAsync(true);
    private void OnCancel(object sender, RoutedEventArgs e) => active?.Cancel();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(OutputText.Text); StatusText.Text = "译文已复制。"; }
        catch (System.Runtime.InteropServices.ExternalException) { StatusText.Text = "剪贴板暂时不可用。"; }
    }

    private async Task RunAsync(bool install)
    {
        if (active != null) return;
        var input = InputText.Text.Trim();
        var source = (string)SourceLanguage.SelectedValue;
        var target = (string)TargetLanguage.SelectedValue;
        if ((!install || source == "auto") && input.Length == 0)
        {
            StatusText.Text = install ? "请选择原文语言，或先输入原文。" : "请输入要翻译的文字。";
            return;
        }
        if (source == "auto") source = NativeTranslation.DetectLanguage(input);
        if (source == target)
        {
            StatusText.Text = "原文和目标语言不能相同。";
            return;
        }

        using var request = new CancellationTokenSource();
        active = request;
        TranslateButton.IsEnabled = InstallButton.IsEnabled = SourceLanguage.IsEnabled =
            TargetLanguage.IsEnabled = CopyButton.IsEnabled = false;
        InputText.IsReadOnly = true;
        CancelButton.IsEnabled = true;
        StatusText.Text = install ? "正在下载并校验模型。安装完成后可离线使用。" : "正在本地翻译…";
        try
        {
            var translation = await translator.RunAsync(source, target, install ? "" : input, install, request.Token);
            if (!install) OutputText.Text = translation;
            StatusText.Text = install ? "模型已安装，可以离线翻译。" : "翻译完成。";
        }
        catch (OperationCanceledException) { StatusText.Text = "任务已取消。"; }
        catch (Exception error) { StatusText.Text = error.Message; }
        finally
        {
            active = null;
            InputText.IsReadOnly = false;
            TranslateButton.IsEnabled = InstallButton.IsEnabled = SourceLanguage.IsEnabled = TargetLanguage.IsEnabled = true;
            CopyButton.IsEnabled = OutputText.Text.Length > 0;
            CancelButton.IsEnabled = false;
        }
    }
}
