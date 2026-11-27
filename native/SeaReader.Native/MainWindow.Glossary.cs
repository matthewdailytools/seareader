using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SeaReader.Native;

public partial class MainWindow
{
    private EpubDocument? glossarySource;
    private IReadOnlyList<EpubGlossaryEntry>? bookGlossary;
    private (RichTextBox Body, TextPointer Start)? glossarySelectionAnchor;

    private async void OnShowBookGlossary(object sender, RoutedEventArgs e)
    {
        if (shownPanel == Panel.Footnote && GlossaryBrowsePanel.IsVisible) ClosePanel();
        else await OpenGlossaryAsync();
    }

    private async Task OpenGlossaryAsync(RichTextBox? body = null, string? selection = null)
    {
        if (epub is not EpubDocument document || reading is not { IsCancellationRequested: false } session ||
            !ReaderSurface.IsVisible || closing || moving || turningPage || seekingPosition) return;

        var browse = selection == null;
        var pending = BeginReferencePanel(session.Token, browse ? "本书术语表" : "术语释义", "正在读取本书术语表…");
        var token = pending.Token;
        if (body is { IsVisible: true } && !body.Selection.IsEmpty)
            glossarySelectionAnchor = (body, body.Selection.Start);
        GlossaryBrowsePanel.Visibility = browse ? Visibility.Visible : Visibility.Collapsed;
        OpenPanel(Panel.Footnote);
        if (browse) GlossarySearchBox.Focus();
        try
        {
            if (!ReferenceEquals(glossarySource, document) || bookGlossary == null)
            {
                var entries = await document.ReadGlossaryAsync(token);
                if (!ReferenceEquals(footnoteRequest, pending) || token.IsCancellationRequested ||
                    !ReferenceEquals(epub, document)) return;
                glossarySource = document;
                bookGlossary = entries;
            }

            if (browse) FilterBookGlossary();
            else
            {
                var query = NormalizeGlossaryQuery(selection!);
                var entry = bookGlossary.FirstOrDefault(term => string.Equals(NormalizeGlossaryQuery(term.Term), query,
                    StringComparison.OrdinalIgnoreCase));
                DisplayGlossaryDefinition(entry, bookGlossary.Count == 0 ? "本书没有可用的术语表。" : "本书术语表没有收录这段文字。");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(footnoteRequest, pending) && !token.IsCancellationRequested)
                DisplayFootnoteText("暂时无法读取本书的术语表。");
        }
    }

    private static string NormalizeGlossaryQuery(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private void OnGlossarySearchChanged(object sender, TextChangedEventArgs e)
    {
        if (shownPanel == Panel.Footnote && GlossaryBrowsePanel.IsVisible && ReferenceEquals(epub, glossarySource))
            FilterBookGlossary();
    }

    private void FilterBookGlossary()
    {
        if (bookGlossary is not { } entries) return;
        var query = NormalizeGlossaryQuery(GlossarySearchBox.Text);
        var matching = entries.Where(entry => NormalizeGlossaryQuery(entry.Term).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var previous = GlossaryTermList.SelectedItem as EpubGlossaryEntry;
        GlossaryTermList.ItemsSource = matching;
        GlossaryTermList.SelectedItem = previous != null && matching.Contains(previous) ? previous : matching.FirstOrDefault();
        if (GlossaryTermList.SelectedItem != null) GlossaryTermList.ScrollIntoView(GlossaryTermList.SelectedItem);
        else DisplayFootnoteText(entries.Count == 0 ? "本书没有可用的术语表。" : "没有匹配的术语。");
    }

    private void OnGlossaryTermChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GlossaryBrowsePanel.IsVisible && GlossaryTermList.SelectedItem is EpubGlossaryEntry entry)
            DisplayGlossaryDefinition(entry, "找不到这条术语的释义。");
    }

    private void DisplayGlossaryDefinition(EpubGlossaryEntry? entry, string empty) =>
        DisplayFootnoteText(entry == null ? empty : $"{entry.Term}\n\n{entry.Definition}");

    private Rect? ReadGlossarySelectionAnchor()
    {
        if (glossarySelectionAnchor is not { } anchor || !anchor.Body.IsVisible || !anchor.Start.HasValidLayout ||
            !ReferenceEquals(anchor.Start.Paragraph?.Parent, anchor.Body.Document)) return null;
        var rect = anchor.Start.GetCharacterRect(LogicalDirection.Forward);
        return rect.IsEmpty ? null : new Rect(anchor.Body.TranslatePoint(rect.TopLeft, ReaderSurface), rect.Size);
    }

    private void EndGlossaryView()
    {
        glossarySelectionAnchor = null;
        GlossaryBrowsePanel.Visibility = Visibility.Collapsed;
        GlossaryTermList.ItemsSource = null;
        GlossarySearchBox.Clear();
    }

    private void ResetBookGlossary()
    {
        glossarySource = null;
        bookGlossary = null;
    }
}
