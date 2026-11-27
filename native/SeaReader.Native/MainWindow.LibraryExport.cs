using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace SeaReader.Native;

public partial class MainWindow
{
    private CancellationTokenSource? libraryDataExport;
    private Task libraryDataExportTask = Task.CompletedTask;
    private bool CanStartLibraryDataOperation => !closing && libraryDataExport == null && libraryDataRestore == null && LibrarySurface.IsVisible;

    private void SetLibraryDataActionsEnabled(bool enabled)
    {
        LibraryExportButton.IsEnabled = enabled;
        LibraryRestoreButton.IsEnabled = enabled;
    }

    private async void OnExportLibraryReadingData(object sender, RoutedEventArgs e)
    {
        if (!CanStartLibraryDataOperation) return;
        libraryDataExportTask = ExportLibraryReadingDataAsync();
        await libraryDataExportTask;
    }

    private async Task ExportLibraryReadingDataAsync()
    {
        using var operation = new CancellationTokenSource();
        libraryDataExport = operation;
        SetLibraryDataActionsEnabled(false);
        try
        {
            var picker = new SaveFileDialog
            {
                Title = "导出阅读数据",
                FileName = "SeaReader-阅读数据.json",
                Filter = "JSON 文件 (*.json)|*.json",
                DefaultExt = ".json",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (picker.ShowDialog(this) != true) return;
            operation.Token.ThrowIfCancellationRequested();
            if (!Path.GetExtension(picker.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                SetLibraryStatus("请使用 .json 文件名保存阅读数据。");
                return;
            }

            SetLibraryStatus("正在导出阅读数据…");
            await FlushLibraryReadingDataAsync(operation.Token);
            var data = await store.GetReadingDataExportAsync(operation.Token);
            await LibraryReadingDataExport.WriteJsonAsync(picker.FileName, data, operation.Token);
            if (!closing) SetLibraryStatus("阅读数据已导出。");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closing) SetLibraryStatus($"无法导出阅读数据。{error.Message}");
        }
        finally
        {
            libraryDataExport = null;
            if (!closing) SetLibraryDataActionsEnabled(true);
        }
    }

    private Task FlushLibraryReadingDataAsync(CancellationToken token) =>
        Task.WhenAll(FlushReadingDataForExportAsync(), readingWindows?.FlushReadersForExportAsync() ?? Task.CompletedTask).WaitAsync(token);

    internal Task FlushReadingDataForExportAsync()
    {
        if (!closing && !turningPage && !seekingPosition) SaveCurrentPosition();
        return Task.WhenAll(FlushPositionAsync(), FlushAppearanceAsync(), annotationWrite, readerLayoutWrite,
            libraryPreferenceWrite, libraryManualOrderWrite, FlushReadingGoalTimeAsync());
    }
}
