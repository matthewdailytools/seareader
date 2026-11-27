using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SeaReader.Native;

public partial class MainWindow
{
    private readonly Dictionary<(string BookId, string Position), string> inlineNoteDrafts = [];
    private readonly Dictionary<(string BookId, string Position), string?> pendingInlineNoteWrites = [];
    private InlineReaderSelection? inlineNoteEditor;
    private string inlineNoteSavedText = "";

    private void BeginInlineSelectionNote()
    {
        if (inlineSelection is not { Selection.Note: { } anchor } snapshot ||
            !ReferenceEquals(book, snapshot.Book) || closing) return;

        if (!ReferenceEquals(inlineNoteEditor, snapshot))
        {
            RememberInlineNoteDraft(discard: false);
            var key = (snapshot.Book.Id, anchor.Position);
            inlineNoteEditor = snapshot;
            inlineNoteSavedText = AnnotationAt(anchor.Position)?.Note ?? "";
            InlineSelectionNoteQuote.Text = anchor.Quotation;
            InlineSelectionNoteInput.Text = inlineNoteDrafts.GetValueOrDefault(key) ??
                pendingInlineNoteWrites.GetValueOrDefault(key) ?? inlineNoteSavedText;
            InlineSelectionNoteInput.CaretIndex = InlineSelectionNoteInput.Text.Length;
        }

        if (!ShowInlineSelectionView(InlineSelectionEditor))
        {
            RememberInlineNoteDraft(discard: false);
            return;
        }
        UpdateInlineNoteButtons();
        InlineSelectionNoteInput.Focus();
    }

    private void RememberInlineNoteDraft(bool discard)
    {
        if (inlineNoteEditor is not { Selection.Note: { } anchor } editor) return;
        var key = (editor.Book.Id, anchor.Position);
        var content = InlineSelectionNoteInput.Text;
        if (discard || content == inlineNoteSavedText) inlineNoteDrafts.Remove(key);
        else inlineNoteDrafts[key] = content;
        inlineNoteEditor = null;
        inlineNoteSavedText = "";
        UpdateInlineNoteButtons();
    }

    private void ResetInlineNoteDrafts()
    {
        inlineNoteEditor = null;
        inlineNoteSavedText = "";
        inlineNoteDrafts.Clear();
        InlineSelectionNoteQuote.Text = "";
        InlineSelectionNoteInput.Clear();
        UpdateInlineNoteButtons();
    }

    private InlineReaderSelection? ActiveInlineNoteEditor() => inlineNoteEditor is { } editor &&
        ReferenceEquals(inlineSelection, editor) && ReferenceEquals(book, editor.Book) && !closing ? editor : null;

    private void UpdateInlineNoteButtons()
    {
        var editor = ActiveInlineNoteEditor();
        bool editable = editor?.Selection.Note is { } anchor &&
            !pendingInlineNoteWrites.ContainsKey((editor.Book.Id, anchor.Position));
        if (InlineSelectionNoteSave != null)
            InlineSelectionNoteSave.IsEnabled = editable && !string.IsNullOrWhiteSpace(InlineSelectionNoteInput.Text);
        if (InlineSelectionNoteRemove != null)
        {
            bool canRemove = editor != null && !string.IsNullOrWhiteSpace(inlineNoteSavedText);
            InlineSelectionNoteRemove.Visibility = canRemove ? Visibility.Visible : Visibility.Collapsed;
            InlineSelectionNoteRemove.IsEnabled = canRemove && editable;
        }
    }

    private void OnInlineSelectionNoteInputChanged(object sender, TextChangedEventArgs e) => UpdateInlineNoteButtons();

    private void OnCancelInlineSelectionNote(object sender, RoutedEventArgs e) =>
        DismissInlineSelection(restoreFocus: true, discardDraft: true);

    private async void OnSaveInlineSelectionNote(object sender, RoutedEventArgs e) => await SaveInlineSelectionNoteAsync();

    private async void OnInlineSelectionNoteRemove(object sender, RoutedEventArgs e) => await RemoveInlineSelectionNoteAsync();

    private async void OnInlineSelectionNoteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        if (!e.IsRepeat) await SaveInlineSelectionNoteAsync();
    }

    private async Task SaveInlineSelectionNoteAsync()
    {
        if (ActiveInlineNoteEditor() is not { Selection.Note: { } anchor } snapshot) return;
        var content = InlineSelectionNoteInput.Text;
        if (string.IsNullOrWhiteSpace(content)) return;

        await QueueInlineNoteChangeAsync(snapshot, anchor.Position, content,
            () => store.SaveAnnotationAsync(snapshot.Book.Id, anchor.Position, anchor.Quotation, content),
            () =>
            {
                var key = (snapshot.Book.Id, anchor.Position);
                if (inlineNoteDrafts.TryGetValue(key, out var draft) && draft == content) inlineNoteDrafts.Remove(key);
                if (IsEditingInlineNoteAt(snapshot.Book, anchor.Position)) inlineNoteSavedText = content;
                if (ReferenceEquals(ActiveInlineNoteEditor(), snapshot) && InlineSelectionNoteInput.Text == content)
                {
                    DismissInlineSelection(restoreFocus: true, discardDraft: true);
                    ClearMarkedTextSelection(snapshot.Body, anchor);
                }
            });
    }

    private async Task RemoveInlineSelectionNoteAsync()
    {
        if (ActiveInlineNoteEditor() is not { Selection.Note: { } anchor } snapshot ||
            AnnotationAt(anchor.Position) is not { } annotation || string.IsNullOrWhiteSpace(annotation.Note) ||
            snapshot.Mark is { } mark && annotation.Id != mark.Id) return;

        await QueueInlineNoteChangeAsync(snapshot, anchor.Position, null,
            () => store.RemoveAnnotationNoteAsync(snapshot.Book.Id, annotation.Id),
            () =>
            {
                inlineNoteDrafts.Remove((snapshot.Book.Id, anchor.Position));
                if (ReferenceEquals(ActiveInlineNoteEditor(), snapshot))
                    DismissInlineSelection(restoreFocus: true, discardDraft: true);
                else if (IsEditingInlineNoteAt(snapshot.Book, anchor.Position))
                {
                    var unchanged = InlineSelectionNoteInput.Text == inlineNoteSavedText;
                    inlineNoteSavedText = "";
                    if (unchanged) InlineSelectionNoteInput.Clear();
                }
            });
    }

    private bool IsEditingInlineNoteAt(BookEntry opened, string position) => ActiveInlineNoteEditor() is { } current &&
        ReferenceEquals(current.Book, opened) && current.Selection.Note?.Position == position;

    private async Task QueueInlineNoteChangeAsync(InlineReaderSelection snapshot, string position, string? pendingContent,
        Func<Task> change, Action afterSave)
    {
        var key = (snapshot.Book.Id, position);
        if (!pendingInlineNoteWrites.TryAdd(key, pendingContent)) return;
        UpdateInlineNoteButtons();

        try
        {
            await QueueAnnotationChange(snapshot.Book, change, afterSave);
        }
        finally
        {
            pendingInlineNoteWrites.Remove(key);
            UpdateInlineNoteButtons();
        }
    }
}
