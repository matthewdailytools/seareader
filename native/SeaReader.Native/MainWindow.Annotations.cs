using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private IReadOnlyList<AnnotationEntry> readerAnnotations = [];
    private IReadOnlyList<AnnotationEntry> annotationsInReadingOrder = [];
    private readonly Dictionary<string, string> noteDrafts = new(StringComparer.Ordinal);
    private int annotationReadVersion;
    private int annotationJumpVersion;
    private Task annotationWrite = Task.CompletedTask;
    private bool updatingNotesList;
    private long? selectedAnnotationId;
    private bool openingStudyCards;
    private StudyCardsWindow? studyCardsWindow;

    private AnnotationEntry? AnnotationAt(string position) => readerAnnotations.FirstOrDefault(entry => entry.Position == position);

    private void ResetReaderAnnotations()
    {
        DismissInlineSelection(discardDraft: true);
        ResetInlineNoteDrafts();
        if (studyCardsWindow is { IsVisible: true } visibleCards) visibleCards.Close();
        annotationReadVersion++;
        annotationJumpVersion++;
        readerAnnotations = [];
        annotationsInReadingOrder = [];
        selectedAnnotationId = null;
        noteDrafts.Clear();
        noteSelection = null;
        NoteInput.Clear();
        updatingNotesList = true;
        try
        {
            NotesList.ItemsSource = null;
            NotesFilterBox.Clear();
            NotesFilterAll.IsChecked = true;
            NotesEmptyMessage.Text = "暂无标注";
        }
        finally { updatingNotesList = false; }
        StudyCardsButton.IsEnabled = false;
        ExportAnnotationsButton.IsEnabled = false;
        ClearSavedReaderMarks();
    }

    private async Task ReloadNotesAsync()
    {
        if (book is not { } opened || closing) return;
        int version = ++annotationReadVersion;
        try
        {
            var saved = await store.ListAnnotationsAsync(opened.Id);
            if (!ReferenceEquals(book, opened) || closing || version != annotationReadVersion) return;

            var ordered = saved.Select(entry => (Entry: entry, Destination: ReadAnnotationDestination(entry)))
                .OrderBy(row => AnnotationOrder(row.Destination)).ThenBy(row => row.Entry.Id)
                .Select(row => row.Entry with
                {
                    LocationLabel = row.Destination is { } target ? DescribeExcerptLocation(target.Location) : "原文位置不可用"
                }).ToArray();
            readerAnnotations = saved;
            annotationsInReadingOrder = ordered;
            RefreshNoteEditor();
            UpdateNotesList();
            SetSavedReaderMarks(saved);
        }
        catch (Exception error) { if (ReferenceEquals(book, opened) && !closing) ShowStatus(error.Message); }
    }

    private void UpdateSelectionForNotes(NoteSelection? selection)
    {
        if (selectedAnnotationId.HasValue || NoteInput.Text.Length != 0 || NoteInput.IsKeyboardFocusWithin) return;
        noteSelection = selection;
        if (shownPanel == Panel.Notes) ShowNoteSelection();
    }

    private void ShowNoteSelection()
    {
        var saved = NotesList.SelectedItem as AnnotationEntry;
        NoteQuote.Text = noteSelection?.Quotation ?? "选中文字后可添加高亮或笔记。";
        NoteInput.IsEnabled = noteSelection != null;
        SaveNoteButton.IsEnabled = noteSelection != null && !string.IsNullOrWhiteSpace(NoteInput.Text);
        RemoveNoteButton.IsEnabled = saved != null && !string.IsNullOrWhiteSpace(saved.Note);
        ClearAnnotationMarkButton.IsEnabled = saved != null && saved.Highlight != AnnotationHighlight.None;
        GoToAnnotationButton.IsEnabled = saved != null && ReadAnnotationDestination(saved) != null;
        StudyCardsButton.IsEnabled = !openingStudyCards && readerAnnotations.Count > 0;
        ExportAnnotationsButton.IsEnabled = !exportingAnnotations && readerAnnotations.Count > 0;
        CopyAnnotationButton.Visibility = ShareAnnotationButton.Visibility = saved != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNoteInputChanged(object sender, TextChangedEventArgs e)
    {
        if (noteSelection is { } selection)
        {
            var saved = AnnotationAt(selection.Position)?.Note ?? "";
            if (NoteInput.Text == saved) noteDrafts.Remove(selection.Position);
            else noteDrafts[selection.Position] = NoteInput.Text;
        }
        if (SaveNoteButton != null) SaveNoteButton.IsEnabled = noteSelection != null && !string.IsNullOrWhiteSpace(NoteInput.Text);
    }

    private void OnNoteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingNotesList || NotesList.SelectedItem is not AnnotationEntry selected) return;
        SetNoteEditor(new NoteSelection(selected.Position, selected.Quotation), selected);
        ShowNoteSelection();
    }

    private void SetNoteEditor(NoteSelection selection, AnnotationEntry? entry)
    {
        annotationJumpVersion++;
        selectedAnnotationId = entry?.Id;
        noteSelection = selection;
        NoteInput.Text = noteDrafts.GetValueOrDefault(selection.Position) ?? entry?.Note ?? "";
    }

    private void RefreshNoteEditor()
    {
        foreach (var entry in readerAnnotations)
            if (noteDrafts.TryGetValue(entry.Position, out var draft) && draft == entry.Note)
                noteDrafts.Remove(entry.Position);
        if (noteSelection is not { } selection) return;
        var saved = AnnotationAt(selection.Position);
        if (selectedAnnotationId.HasValue && saved == null && !noteDrafts.ContainsKey(selection.Position))
        {
            noteSelection = null;
            NoteInput.Clear();
        }
        else if (!noteDrafts.ContainsKey(selection.Position)) NoteInput.Text = saved?.Note ?? "";
        selectedAnnotationId = saved?.Id;
    }

    private void OnNotesSearchChanged(object sender, TextChangedEventArgs e) => UpdateNotesList();

    private void OnNotesFilterChanged(object sender, RoutedEventArgs e) => UpdateNotesList();

    private void FocusOpenedNotesFilter()
    {
        var opened = book;
        var previous = Keyboard.FocusedElement;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!closing && ReferenceEquals(book, opened) && shownPanel == Panel.Notes &&
                (ReferenceEquals(Keyboard.FocusedElement, previous) || Keyboard.FocusedElement == null ||
                 NotesButton.IsKeyboardFocusWithin || ReaderToolsButton.IsKeyboardFocusWithin)) NotesFilterBox.Focus();
        }));
    }

    private void UpdateNotesList()
    {
        if (updatingNotesList || NotesList == null || NotesEmptyMessage == null || NotesFilterChoices == null) return;
        var query = NotesFilterBox.Text.Trim();
        var filter = NotesFilterChoices.Children.OfType<RadioButton>().FirstOrDefault(choice => choice.IsChecked == true)?.Tag as string;
        var matches = filter is not ("highlight" or "note") && query.Length == 0 ? annotationsInReadingOrder :
            annotationsInReadingOrder.Where(entry => MatchesType(entry) && (query.Length == 0 ||
                entry.Quotation.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Note.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        var selected = selectedAnnotationId is long id ? matches.FirstOrDefault(entry => entry.Id == id) : null;
        if (selected == null && NotesList.SelectedItem != null) annotationJumpVersion++;
        updatingNotesList = true;
        try
        {
            if (!ReferenceEquals(NotesList.ItemsSource, matches)) NotesList.ItemsSource = matches;
            NotesList.SelectedItem = selected;
            NotesEmptyMessage.Text = readerAnnotations.Count == 0 ? "暂无标注" : "没有符合条件的标注";
        }
        finally { updatingNotesList = false; }
        if (shownPanel == Panel.Notes) ShowNoteSelection();

        bool MatchesType(AnnotationEntry entry) => filter switch
        {
            "highlight" => entry.Highlight != AnnotationHighlight.None,
            "note" => !string.IsNullOrWhiteSpace(entry.Note),
            _ => true
        };
    }

    private Task QueueAnnotationChange(BookEntry opened, Func<Task> change, Action? afterSave = null) =>
        annotationWrite = ApplyAnnotationChangeAsync(annotationWrite, opened, change, afterSave);

    private async Task ApplyAnnotationChangeAsync(Task prior, BookEntry opened, Func<Task> change, Action? afterSave)
    {
        await prior;
        try
        {
            await change();
            if (!ReferenceEquals(book, opened) || closing) return;
            afterSave?.Invoke();
            await ReloadNotesAsync();
        }
        catch (Exception error) { if (ReferenceEquals(book, opened) && !closing) ShowStatus(error.Message); }
    }

    private Task MarkReaderSelectionAsync(FrameworkElement target, NoteSelection selected, AnnotationHighlight highlight)
    {
        if (book is not { } opened || closing) return Task.CompletedTask;
        return QueueAnnotationChange(opened,
            () => store.SaveAnnotationHighlightAsync(opened.Id, selected.Position, selected.Quotation, highlight),
            () => ClearMarkedTextSelection(target, selected));
    }

    private void ClearMarkedTextSelection(FrameworkElement target, NoteSelection selection)
    {
        if (ReadReaderSelection(target)?.Note?.Position != selection.Position) return;
        if (target is RichTextBox body) body.Selection.Select(body.Selection.Start, body.Selection.Start);
        else if (target is PdfSelectionLayer layer) layer.ClearSelection();
    }

    private async void OnSaveNote(object sender, RoutedEventArgs e)
    {
        if (book is not { } opened || noteSelection is not { } selected || closing) return;
        var content = NoteInput.Text;
        if (string.IsNullOrWhiteSpace(content)) return;
        await QueueAnnotationChange(opened,
            () => store.SaveAnnotationAsync(opened.Id, selected.Position, selected.Quotation, content));
    }

    private async void OnRemoveNote(object sender, RoutedEventArgs e)
    {
        if (book is not { } opened || closing || NotesList.SelectedItem is not AnnotationEntry selected) return;
        var content = NoteInput.Text;
        await QueueAnnotationChange(opened, () => store.RemoveAnnotationNoteAsync(opened.Id, selected.Id), () =>
        {
            if (noteSelection?.Position == selected.Position && NoteInput.Text == content) NoteInput.Clear();
        });
    }

    private Task ClearAnnotationMarkAsync(AnnotationEntry entry, Action? afterSave = null) => book is { } opened && !closing
        ? QueueAnnotationChange(opened, () => store.RemoveAnnotationHighlightAsync(opened.Id, entry.Id), afterSave) : Task.CompletedTask;

    private async void OnClearAnnotationMark(object sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is AnnotationEntry entry) await ClearAnnotationMarkAsync(entry);
    }

    private Task GoToSelectedAnnotationAsync() => NotesList.SelectedItem is AnnotationEntry entry
        ? GoToSavedAnnotationAsync(entry) : Task.CompletedTask;

    private async void OnGoToAnnotation(object sender, RoutedEventArgs e) => await GoToSelectedAnnotationAsync();

    private async void OnNoteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject target &&
            ItemsControl.ContainerFromElement(NotesList, target) is ListBoxItem) await GoToSelectedAnnotationAsync();
    }

    private async void OnNoteKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await GoToSelectedAnnotationAsync();
    }

    private async void OnShowStudyCards(object sender, RoutedEventArgs e)
    {
        if (openingStudyCards || book is not { } opened || reading is not { } session || closing ||
            shownPanel != Panel.Notes || moving || turningPage || seekingPosition) return;
        openingStudyCards = true;
        StudyCardsButton.IsEnabled = false;
        try
        {
            await annotationWrite;
            if (!CanUseAnnotationPanel(opened, session)) return;
            var selected = (NotesList.SelectedItem as AnnotationEntry)?.Id;
            var saved = await store.ListAnnotationsAsync(opened.Id);
            if (!CanUseAnnotationPanel(opened, session) || saved.Count == 0) return;

            var cards = saved.OrderBy(StudyCardOrder).ThenBy(entry => entry.Id).ToArray();
            var dialog = studyCardsWindow = new StudyCardsWindow(cards, this, selected);
            var confirmed = dialog.ShowDialog() == true;
            studyCardsWindow = null;
            if (!confirmed || dialog.Destination is not { } destination ||
                !CanUseAnnotationPanel(opened, session)) return;
            await GoToSavedAnnotationAsync(destination);
        }
        catch (Exception error) { if (ReferenceEquals(book, opened) && !closing) ShowStatus(error.Message); }
        finally
        {
            studyCardsWindow = null;
            openingStudyCards = false;
            if (shownPanel == Panel.Notes && !closing)
            {
                ShowNoteSelection();
                if (StudyCardsButton.IsEnabled) StudyCardsButton.Focus();
            }
        }
    }

    private bool CanUseAnnotationPanel(BookEntry opened, CancellationTokenSource session) =>
        ReferenceEquals(book, opened) && ReferenceEquals(reading, session) && ReaderSurface.IsVisible &&
        shownPanel == Panel.Notes && !closing && !moving && !turningPage && !seekingPosition;

    private async Task GoToSavedAnnotationAsync(AnnotationEntry entry)
    {
        if (book is not { } opened || reading is not { } session || !CanUseAnnotationPanel(opened, session)) return;
        var request = ++annotationJumpVersion;
        var token = session.Token;
        try
        {
            var target = ReadAnnotationDestination(entry);
            if (target is { } legacy && epub is EpubDocument publication && IsLegacyEpubPosition(entry.Position))
            {
                var index = legacy.Location.Chapter!.Value;
                var offset = await publication.ResolveLegacyPositionAsync(index, entry.Position, token);
                target = offset is int exact ? (new ReaderLocation(index, exact, 0, new TextPageStart(exact, 0, exact)), null) : null;
            }
            if (request != annotationJumpVersion || !CanUseAnnotationPanel(opened, session)) return;
            if (target is { } destination)
                await NavigateReaderLocationAsync(destination.Location, destination.Marks,
                    showTransientAnnotation: !ReaderHighlightPalette.IsVisibleAnnotation(entry));
            else ShowStatus("找不到这条标注的原文位置。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (request == annotationJumpVersion && CanUseAnnotationPanel(opened, session)) ShowStatus(error.Message);
        }
    }

    private (ReaderLocation Location, IReadOnlyList<Rect>? Marks)? ReadAnnotationDestination(AnnotationEntry entry)
    {
        if (pdf != null) return ReadStoredDestination(entry.Position);
        if (text == null) return null;
        if (epub is EpubDocument publication && IsLegacyEpubPosition(entry.Position))
            return publication.FindLegacyChapter(entry.Position) is int legacyChapter
                ? (new ReaderLocation(legacyChapter, 0, 0), null) : null;

        if (ParsePosition(entry.Position, requireOffset: true) is not { Offset: >= 0 } saved ||
            saved.End is int end && end <= saved.Offset) return null;
        if (epub == null ? saved.Chapter.HasValue : saved.Chapter is not int index || index < 0 || index >= epub.Chapters.Count)
            return null;
        var source = epub == null || chapter == saved.Chapter ? text :
            facingPage?.Chapter == saved.Chapter ? facingPage?.Document : null;
        long? knownLength = source?.Text.Length;
        if (knownLength == null && saved.Chapter is int section && chapterReadingStarts is { } starts)
            knownLength = starts[section + 1] - starts[section];
        if (knownLength is long length && (saved.Offset > length || saved.End > length)) return null;
        if (epub == null && saved.End is int limit &&
            !new TextReaderMark(saved.Offset, limit, entry.Highlight, saved.Encoding, entry.Quotation).CanDraw(text)) return null;

        var fraction = Math.Clamp(saved.LineFraction, 0, 1);
        var position = saved.TextPosition ?? new TextPageStart(saved.Offset, fraction, fraction == 0 ? saved.Offset : -1);
        return (new ReaderLocation(saved.Chapter, saved.Offset, fraction, position), null);
    }

    private (int Section, double Offset, double Horizontal) StudyCardOrder(AnnotationEntry entry) =>
        AnnotationOrder(ReadAnnotationDestination(entry));

    private (int Section, double Offset, double Horizontal) AnnotationOrder((ReaderLocation Location, IReadOnlyList<Rect>? Marks)? target)
    {
        if (target is not { } destination) return (int.MaxValue, 0, 0);
        var location = destination.Location;
        if (pdf == null) return (location.Chapter ?? 0, location.TextPosition == null ? double.MaxValue : location.Offset, 0);
        var start = destination.Marks is { Count: > 0 } marks ? marks[0] : Rect.Empty;
        return (location.Offset, start.IsEmpty ? 0 : start.Top, start.IsEmpty ? 0 : start.Left);
    }
}
