using Microsoft.Win32;
using System.Windows;

namespace SeaReader.Native;

public partial class MainWindow
{
    private CancellationTokenSource? libraryDataRestore;
    private Task libraryDataRestoreTask = Task.CompletedTask;

    private async void OnRestoreLibraryReadingData(object sender, RoutedEventArgs e)
    {
        if (!CanStartLibraryDataOperation) return;
        libraryDataRestoreTask = RestoreLibraryReadingDataAsync();
        await libraryDataRestoreTask;
    }

    private async Task RestoreLibraryReadingDataAsync()
    {
        using var operation = new CancellationTokenSource();
        libraryDataRestore = operation;
        SetLibraryDataActionsEnabled(false);
        bool committed = false;
        try
        {
            var picker = new OpenFileDialog
            {
                Title = "选择阅读数据",
                Filter = "JSON 文件 (*.json)|*.json",
                CheckFileExists = true
            };
            if (picker.ShowDialog(this) != true) return;
            var token = operation.Token;
            token.ThrowIfCancellationRequested();

            SetLibraryStatus("正在检查阅读数据…");
            var data = await LibraryReadingDataImport.ReadJsonAsync(picker.FileName, token);
            var preview = await store.PreviewReadingDataRestoreAsync(data, token);
            token.ThrowIfCancellationRequested();
            var summary = $"可匹配 {preview.MatchedBooks} 本书。{MissingBooksMessage(preview.MissingBooks)}";
            var answer = MessageBox.Show(this,
                $"{summary}\n\n恢复会补入缺少的阅读记录、收藏和偏好，保留当前已有记录。",
                "恢复阅读数据", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK)
            {
                SetLibraryStatus("");
                return;
            }

            SetLibraryStatus("正在恢复阅读数据…");
            await FlushLibraryReadingDataAsync(token);
            var result = await store.RestoreReadingDataAsync(data, token);
            committed = true;
            if (closing) return;
            libraryViewPreferences = null;
            readerAppearanceLoad = null;
            await ReloadLibraryAsync();
            var goals = await store.LoadReadingGoalsSettingsAsync();
            ApplySharedReadingGoalsSettings(goals);
            if (readingWindows is { } manager)
            {
                manager.ApplySharedReadingGoalsSettings(this, goals);
                await manager.RefreshReadersAfterDataRestoreAsync();
            }
            if (!closing)
                SetLibraryStatus($"已恢复阅读数据。匹配 {result.MatchedBooks} 本书，补入 {result.AddedRecords} 条记录。" +
                    MissingBooksMessage(result.MissingBooks));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closing) SetLibraryStatus(committed ? $"阅读数据已恢复，刷新书库时出错。{error.Message}" :
                $"无法恢复阅读数据。{error.Message}");
        }
        finally
        {
            libraryDataRestore = null;
            if (!closing) SetLibraryDataActionsEnabled(true);
        }
    }

    private static string MissingBooksMessage(int count) => count == 0 ? "" :
        $"\n有 {count} 本书需先导入原书。导入后可再次恢复。";

    internal Task RefreshReadingDataAfterRestoreAsync() => Task.WhenAll(ReloadBookmarksAsync(), ReloadNotesAsync());
}
