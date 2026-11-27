using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const int ReaderTapDelayMilliseconds = 260;
    private readonly record struct SavedMarkTap(BookEntry Book, FrameworkElement Body, long Id, Point Point);
    private SavedMarkTap? pendingSavedMarkTap;

    private SavedMarkTap? ReadSavedMarkPress(MouseButtonEventArgs e)
    {
        if (book is not { } opened || shownPanel != null || AppearancePopup.IsOpen || Keyboard.Modifiers != ModifierKeys.None ||
            !ReferenceEquals(savedReaderMarksBook, opened) || savedTextMarks.Count == 0 && savedPdfMarks.Count == 0) return null;

        FrameworkElement body;
        if (pdf != null)
            body = PdfPageFor(PdfFacingSelection) >= 0 && PdfFacingSelection.IsVisible &&
                new Rect(PdfFacingSelection.RenderSize).Contains(e.GetPosition(PdfFacingSelection)) ? PdfFacingSelection : PdfSelection;
        else
            body = HasSpread && new Rect(FacingTextList.RenderSize).Contains(e.GetPosition(FacingTextList)) ? FacingTextList : TextList;
        var point = e.GetPosition(body);
        return FindSavedMarkHitAt(body, point) is { } mark ? new SavedMarkTap(opened, body, mark.Entry.Id, point) : null;
    }

    private void OpenTappedSavedMark(SavedMarkTap tap)
    {
        if (!ReferenceEquals(book, tap.Book) || !CanShowInlineSelection(tap.Body) || shownPanel != null || AppearancePopup.IsOpen ||
            Mouse.LeftButton == MouseButtonState.Pressed || tap.Body.ContextMenu?.IsOpen == true ||
            ReadReaderSelection(tap.Body) != null || FindSavedMarkHitAt(tap.Body, tap.Point) is not { } hit ||
            hit.Entry.Id != tap.Id) return;
        var entry = hit.Entry;
        var selection = new ReaderSelection(entry.Quotation, new NoteSelection(entry.Position, entry.Quotation));
        OpenInlineSelection(new InlineReaderSelection(tap.Book, tap.Body, selection, (entry.Id, hit.Bounds)), entry);
    }

    private AnnotationEntry? CurrentInlineAnnotation(InlineReaderSelection current) =>
        current.Mark is { } mark && current.Selection.Note is { } selection && AnnotationAt(selection.Position) is { } entry &&
        entry.Id == mark.Id && ReaderHighlightPalette.IsVisibleAnnotation(entry) ? entry : null;

    private async void OnInlineSelectionClearMark(object sender, RoutedEventArgs e)
    {
        if (ActiveInlineSelection() is not { } current || CurrentInlineAnnotation(current) is not { } entry ||
            !ReaderHighlightPalette.IsMark(entry.Highlight)) return;
        DismissInlineSelection(restoreFocus: true);
        await ClearAnnotationMarkAsync(entry);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
