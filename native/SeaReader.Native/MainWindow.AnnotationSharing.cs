using Microsoft.Win32;
using System.Windows;

namespace SeaReader.Native;

public partial class MainWindow
{
    private ReaderShare? readerShare;
    private bool exportingAnnotations;

    private ReaderAnnotationExcerpt CreateSelectionExcerpt(ReaderSelection selection)
    {
        var location = selection.Note is { } anchor ? DescribeExcerptLocation(anchor.Position) :
            DescribeExcerptLocation(CaptureReaderLocation());
        return new ReaderAnnotationExcerpt(location, selection.Text, "");
    }

    private ReaderAnnotationExcerpt CreateSavedExcerpt(AnnotationEntry entry) =>
        new(ReadAnnotationDestination(entry) is { } target ? DescribeExcerptLocation(target.Location) : "未记录原文位置",
            entry.Quotation, entry.Note);

    private string DescribeExcerptLocation(string position) => ReadStoredDestination(position) is { } destination
        ? DescribeExcerptLocation(destination.Location) : "未记录原文位置";

    private string DescribeExcerptLocation(ReaderLocation location)
    {
        if (pdf != null) return $"第 {location.Offset + 1} 页";
        if (epub != null && location.Chapter is { } chapterIndex && chapterIndex >= 0 && chapterIndex < epub.Chapters.Count)
        {
            var label = $"第 {chapterIndex + 1} 章";
            var title = epub.Chapters[chapterIndex].Title;
            return string.IsNullOrWhiteSpace(title) || title == label ? label : $"{label} · {title}";
        }
        var offset = location.TextPosition is { SourceOffset: >= 0 } source ? source.SourceOffset : location.Offset;
        return $"原文位置 {(long)offset + 1}";
    }

    private void CopyExcerpt(ReaderAnnotationExcerpt excerpt)
    {
        if (book is { } opened && !closing)
            CopyReaderText(ReaderAnnotationExport.CreatePlainText(opened.Display, excerpt));
    }

    private void ShareExcerpt(ReaderAnnotationExcerpt excerpt)
    {
        if (book is not { } opened || closing) return;
        try
        {
            readerShare ??= new ReaderShare(this);
            var title = string.IsNullOrWhiteSpace(excerpt.Note) ? $"《{opened.Display.Title}》摘录" : $"《{opened.Display.Title}》笔记";
            readerShare.Show(title, ReaderAnnotationExport.CreatePlainText(opened.Display, excerpt));
        }
        catch (InvalidOperationException error) { ShowStatus(error.Message); }
    }

    private void OnCopyAnnotation(object sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is AnnotationEntry entry) CopyExcerpt(CreateSavedExcerpt(entry));
    }

    private void OnShareAnnotation(object sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is AnnotationEntry entry) ShareExcerpt(CreateSavedExcerpt(entry));
    }

    private async Task<string?> BuildAnnotationExportAsync(BookEntry opened, CancellationTokenSource session)
    {
        await annotationWrite;
        if (!CanUseAnnotationPanel(opened, session)) return null;
        var saved = await store.ListAnnotationsAsync(opened.Id);
        if (!CanUseAnnotationPanel(opened, session) || saved.Count == 0) return null;
        var excerpts = saved.OrderBy(StudyCardOrder).ThenBy(entry => entry.Id).Select(CreateSavedExcerpt).ToArray();
        return ReaderAnnotationExport.CreateMarkdown(opened.Display, excerpts);
    }

    private async void OnExportAnnotations(object sender, RoutedEventArgs e)
    {
        if (exportingAnnotations || book is not { } opened || reading is not { } session ||
            !CanUseAnnotationPanel(opened, session)) return;
        exportingAnnotations = true;
        ExportAnnotationsButton.IsEnabled = false;
        try
        {
            var markdown = await BuildAnnotationExportAsync(opened, session);
            if (markdown == null || !CanUseAnnotationPanel(opened, session)) return;
            var picker = new SaveFileDialog
            {
                Title = "导出本书标注",
                Filter = "Markdown 文档|*.md",
                DefaultExt = ".md",
                FileName = ReaderAnnotationExport.DefaultFileName(opened.Display.Title),
                AddExtension = true,
                OverwritePrompt = true
            };
            if (picker.ShowDialog(this) != true || !CanUseAnnotationPanel(opened, session)) return;
            var token = session.Token;
            try { await ReaderAnnotationExport.WriteMarkdownAsync(picker.FileName, markdown, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            if (CanUseAnnotationPanel(opened, session)) ShowStatus("已导出本书标注。");
        }
        catch (Exception error) { if (ReferenceEquals(book, opened) && !closing) ShowStatus(error.Message); }
        finally
        {
            exportingAnnotations = false;
            if (shownPanel == Panel.Notes && !closing) ShowNoteSelection();
        }
    }
}
