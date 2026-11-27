using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const int MaximumNoteQuotation = 12000;

    private sealed record ReaderSelection(string Text, NoteSelection? Note);

    private void InitializeSelectionTools()
    {
        AttachSelectionMenu(TextList);
        AttachSelectionMenu(FacingTextList);
        AttachSelectionMenu(PdfSelection);
        AttachSelectionMenu(PdfFacingSelection);
        CommandManager.AddPreviewExecutedHandler(TextList, OnReaderCopy);
        CommandManager.AddPreviewExecutedHandler(FacingTextList, OnReaderCopy);
        AutomationProperties.SetHelpText(TextList, "选中文字后，右键或按 Shift+F10 打开选区操作。也可右键编辑已有高亮。");
        AutomationProperties.SetHelpText(FacingTextList, AutomationProperties.GetHelpText(TextList));
        AutomationProperties.SetHelpText(PdfSelection, "拖动选中文字。按 Ctrl+C 复制，或按 Shift+F10 打开选区操作。也可右键编辑已有高亮。");
        AutomationProperties.SetHelpText(PdfFacingSelection, AutomationProperties.GetHelpText(PdfSelection));
    }

    private void AttachSelectionMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { MinWidth = 148 };
        menu.SetResourceReference(BackgroundProperty, "ReaderPaper");
        menu.SetResourceReference(ForegroundProperty, "MainInk");
        menu.SetResourceReference(BorderBrushProperty, "ReaderPaperBorder");
        AutomationProperties.SetName(menu, "选区操作");

        BookEntry? menuBook = null;
        ReaderSelection? menuSelection = null;
        var positionedSavedMark = false;
        var copy = AddAction("复制", selection => CopyReaderText(selection.Text));
        copy.InputGestureText = "Ctrl+C";
        AddAction("复制含出处", selection => CopyExcerpt(CreateSelectionExcerpt(selection)));
        AddAction("分享摘录…", selection => ShareExcerpt(CreateSelectionExcerpt(selection)));

        var highlightMenu = new MenuItem { Header = "高亮" };
        var highlights = new[]
        {
            AddHighlight("琥珀", AnnotationHighlight.Amber),
            AddHighlight("薄荷绿", AnnotationHighlight.Mint),
            AddHighlight("淡紫", AnnotationHighlight.Lavender),
            AddHighlight("下划线", AnnotationHighlight.Underline)
        };
        highlightMenu.Items.Add(new Separator());
        var clearHighlight = AddAction("清除标记", selection =>
        {
            if (selection.Note is { } anchor && AnnotationAt(anchor.Position) is { } entry)
                _ = ClearAnnotationMarkAsync(entry, () => ClearMarkedTextSelection(target, anchor));
        }, highlightMenu.Items);
        menu.Items.Add(highlightMenu);

        var note = AddAction("添加笔记…", selection =>
        {
            if (selection.Note is { } anchor) BeginSelectionNote(target, anchor);
        });
        var glossary = AddAction("查本书释义", selection =>
        {
            if (target is RichTextBox body) _ = OpenGlossaryAsync(body, selection.Text);
        });
        glossary.Visibility = Visibility.Collapsed;
        AddAction("翻译…", selection => ShowTranslation(this, selection.Text));
        AddAction("书内查找", selection => SearchSelectedText(selection.Text));

        if (target is RichTextBox richText)
        {
            richText.ContextMenu = null;
            richText.SelectionChanged += (_, _) =>
            {
                richText.ContextMenu = richText.Selection.IsEmpty ? null : menu;
            };
        }
        else target.ContextMenu = menu;

        target.AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler((_, e) =>
        {
            if (menu.IsOpen) return;
            var keyboard = e.CursorLeft == -1 && e.CursorTop == -1;
            var point = keyboard ? default : SelectionMenuPoint(target, e);
            menuSelection = ReadReaderSelection(target);
            var outsideSelection = !keyboard && menuSelection != null && !SelectionContainsPoint(target, point);
            var savedMark = !keyboard && (menuSelection == null || outsideSelection)
                ? FindSavedMarkAt(target, point) : null;
            if (savedMark != null)
                menuSelection = new ReaderSelection(savedMark.Quotation, new NoteSelection(savedMark.Position, savedMark.Quotation));
            else if (outsideSelection)
            {
                menuSelection = null;
                menuBook = null;
                ResetSavedMarkPlacement();
                e.Handled = true;
                return;
            }
            menuBook = menuSelection == null ? null : book;
            if (menuSelection == null)
            {
                // Complete the native open/close cycle for RichTextBox so it resets its caret state.
                if (target is not RichTextBox || target.ContextMenu == null) e.Handled = true;
                return;
            }
            if (target is PdfSelectionLayer pdfLayer) ActivatePdfPage(pdfLayer);
            AutomationProperties.SetName(menu, savedMark == null ? "选区操作" :
                ReaderHighlightPalette.IsMark(savedMark.Highlight) ? "高亮操作" : "笔记操作");
            note.IsEnabled = menuSelection.Note != null;
            glossary.Visibility = epub is EpubDocument && menuSelection.Text.Trim().Length <= 256
                ? Visibility.Visible : Visibility.Collapsed;
            highlightMenu.IsEnabled = menuSelection.Note != null;
            var saved = savedMark ?? (menuSelection.Note is { } anchor ? AnnotationAt(anchor.Position) : null);
            note.Header = string.IsNullOrWhiteSpace(saved?.Note) ? "添加笔记…" : "编辑笔记…";
            var current = saved?.Highlight;
            foreach (var option in highlights) option.IsChecked = Equals(option.Tag, current);
            clearHighlight.IsEnabled = current is { } style && style != AnnotationHighlight.None;
            if (savedMark != null)
            {
                e.Handled = true;
                positionedSavedMark = true;
                target.ContextMenu = menu;
                menu.PlacementTarget = target;
                menu.Placement = PlacementMode.RelativePoint;
                menu.HorizontalOffset = point.X;
                menu.VerticalOffset = point.Y;
                menu.IsOpen = true;
            }
            else
            {
                ResetSavedMarkPlacement();
                PositionSelectionMenu(target, keyboard);
            }
        }), handledEventsToo: target is PdfSelectionLayer);
        menu.Opened += (_, _) =>
        {
            if (menuSelection == null) menu.IsOpen = false;
            else hoverTimer.Stop();
        };
        menu.Closed += (_, _) =>
        {
            if (hoverVisible) RestartHoverTimer();
            // WPF posts MenuItem.Click after closing the menu.
            var closedSelection = menuSelection;
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(menuSelection, closedSelection) || menu.IsOpen) return;
                ResetSavedMarkPlacement();
                menuSelection = null;
                menuBook = null;
            }, DispatcherPriority.Background);
        };

        void ResetSavedMarkPlacement()
        {
            if (!positionedSavedMark) return;
            positionedSavedMark = false;
            menu.ClearValue(ContextMenu.PlacementTargetProperty);
            menu.ClearValue(ContextMenu.PlacementProperty);
            menu.ClearValue(ContextMenu.HorizontalOffsetProperty);
            menu.ClearValue(ContextMenu.VerticalOffsetProperty);
            if (target is RichTextBox { Selection.IsEmpty: true }) target.ContextMenu = null;
        }

        MenuItem AddHighlight(string label, AnnotationHighlight highlight)
        {
            var item = AddAction(label, selection =>
            {
                if (selection.Note is { } anchor) _ = MarkReaderSelectionAsync(target, anchor, highlight);
            }, highlightMenu.Items);
            item.Tag = highlight;
            item.IsCheckable = true;
            item.Icon = new Border
            {
                Width = 12,
                Height = highlight == AnnotationHighlight.Underline ? 3 : 10,
                CornerRadius = new CornerRadius(2),
                Background = ReaderHighlightPalette.Get(highlight)
            };
            return item;
        }

        MenuItem AddAction(string label, Action<ReaderSelection> action, ItemCollection? items = null)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) =>
            {
                if (menuSelection is not { } selection || !ReferenceEquals(book, menuBook) || closing) return;
                menu.IsOpen = false;
                action(selection);
            };
            (items ?? menu.Items).Add(item);
            return item;
        }
    }

    private static Point SelectionMenuPoint(FrameworkElement target, ContextMenuEventArgs e) =>
        e.OriginalSource is UIElement origin
            ? origin.TranslatePoint(new Point(e.CursorLeft, e.CursorTop), target)
            : Mouse.GetPosition(target);

    private bool SelectionContainsPoint(FrameworkElement target, Point point)
    {
        if (target is RichTextBox body)
            return body.GetPositionFromPoint(point, snapToText: false) is { } pointer && body.Selection.Contains(pointer);
        return target is PdfSelectionLayer { ActualWidth: > 0, ActualHeight: > 0 } layer &&
            ReadMarkViewport(layer, PdfSurface).Contains(point) &&
            layer.SelectedBounds.Any(box => box.Contains(point.X / layer.ActualWidth, point.Y / layer.ActualHeight));
    }

    private void PositionSelectionMenu(FrameworkElement target, bool viaKeyboard)
    {
        ContextMenuService.SetPlacement(target, PlacementMode.MousePoint);
        ContextMenuService.SetHorizontalOffset(target, 0);
        ContextMenuService.SetVerticalOffset(target, 0);
        if (!viaKeyboard) return;

        var position = default(Point);
        if (target is RichTextBox textBox)
        {
            var caret = textBox.Selection.Start.GetCharacterRect(LogicalDirection.Forward);
            if (!caret.IsEmpty) position = caret.BottomLeft;
        }
        else if (target is PdfSelectionLayer { SelectedBounds: [var first, ..] })
        {
            position = new Point(first.Left * target.ActualWidth, first.Bottom * target.ActualHeight);
        }
        ContextMenuService.SetPlacement(target, PlacementMode.RelativePoint);
        ContextMenuService.SetHorizontalOffset(target, Math.Clamp(position.X, 0, target.ActualWidth));
        ContextMenuService.SetVerticalOffset(target, Math.Clamp(position.Y, 0, target.ActualHeight));
    }

    private ReaderSelection? ReadReaderSelection(FrameworkElement target) => ReadSelectionForAction(target, out _);

    private ReaderSelection? ReadSelectionForAction(FrameworkElement target, out bool clipped)
    {
        clipped = false;
        if (book == null || moving || updatingSpread) return null;
        if (target is PdfSelectionLayer layer)
        {
            var page = PdfPageFor(layer);
            var text = layer.SelectedText;
            if (page < 0 || !layer.IsVisible || string.IsNullOrWhiteSpace(text)) return null;
            var rectangles = layer.SelectedBounds.Take(80).Select(rect => new[]
            {
                Math.Round(rect.X, 5), Math.Round(rect.Y, 5), Math.Round(rect.Width, 5), Math.Round(rect.Height, 5)
            });
            var position = JsonSerializer.Serialize(new { page = page + 1, rects = rectangles }, PositionJson);
            var quote = text.Trim();
            return new ReaderSelection(text, new NoteSelection(position, quote[..Math.Min(quote.Length, MaximumNoteQuotation)]));
        }
        if (pdf != null || target is not RichTextBox textBox || textBox.Selection.IsEmpty) return null;
        var facing = ReferenceEquals(target, FacingTextList);
        if (!facing && !ReferenceEquals(target, TextList)) return null;
        var document = text;
        var locationChapter = chapter;
        IReadOnlyList<(Paragraph Paragraph, TextLine Line)> rows = visibleRows;
        if (facing)
        {
            if (!HasSpread || facingPage is not { IsUpdating: false } right) return null;
            document = right.Document;
            locationChapter = right.Chapter;
            rows = right.Rows;
        }
        if (document == null || rows.Count == 0) return ReadUnanchoredSelection(textBox);

        var selected = textBox.Selection;
        var start = ReadSelectionBoundary(rows, selected.Start);
        var end = ReadSelectionBoundary(rows, selected.End);
        var (visibleStart, visibleEnd) = ReadVisibleSelectionRange(facing, document, locationChapter, rows);
        var startOffset = Math.Max(start, visibleStart);
        var endOffset = Math.Min(end, visibleEnd);
        clipped = startOffset != start || endOffset != end;
        if (endOffset <= startOffset) return clipped ? null : ReadUnanchoredSelection(textBox);
        var quotation = document.Text[startOffset..endOffset];
        if (string.IsNullOrWhiteSpace(quotation)) return null;
        var lengthLimit = Math.Min(quotation.Length, MaximumNoteQuotation);
        var anchor = new NoteSelection(SerializeTextPosition(startOffset, end: startOffset + lengthLimit,
            locationChapter: locationChapter), quotation[..lengthLimit]);
        return new ReaderSelection(quotation, anchor);

        static ReaderSelection? ReadUnanchoredSelection(RichTextBox box)
        {
            var displayed = ParagraphText.ReadSelectionText(box);
            return string.IsNullOrWhiteSpace(displayed) ? null : new ReaderSelection(displayed, null);
        }
    }

    private (int Start, int End) ReadVisibleSelectionRange(bool facing, TextDocument document, int locationChapter,
        IReadOnlyList<(Paragraph Paragraph, TextLine Line)> rows)
    {
        if (continuous) return (0, document.Text.Length);
        if (ReadingPageIndex is { } pages)
        {
            var page = HasSpread ? spreadLeftIndex + (facing ? 1 : 0) : CurrentTextPage(pages);
            var start = pages.GetPage(page, out var indexedChapter);
            if (indexedChapter == locationChapter)
            {
                var endOffset = document.Text.Length;
                if (page + 1 < pages.Count)
                {
                    var next = pages.GetPage(page + 1, out var nextChapter);
                    if (nextChapter == indexedChapter) endOffset = SourceBoundary(next);
                }
                return (SourceBoundary(start), endOffset);
            }
        }
        if (!facing && textScroll is { ViewportHeight: > 0 } scroll && FirstTextVisualLine() is { } first)
        {
            var top = TextList.Padding.Top;
            var bottom = top + scroll.ViewportHeight;
            var overflow = FirstOverflowLine(first, bottom, top);
            return (ReadSelectionBoundary(rows, first), overflow != null ? ReadSelectionBoundary(rows, overflow) :
                rows[^1].Line.Offset + rows[^1].Line.Length);
        }
        return (0, document.Text.Length);

        int SourceBoundary(TextPageStart page) => Math.Clamp(page.SourceOffset >= 0 ? page.SourceOffset : page.Offset,
            0, document.Text.Length);
    }

    private static int ReadSelectionBoundary(IReadOnlyList<(Paragraph Paragraph, TextLine Line)> rows, TextPointer pointer)
    {
        var paragraph = pointer.Paragraph;
        foreach (var row in rows)
        {
            if (ReferenceEquals(row.Paragraph, paragraph))
                return ReadSelectionOffset(row, pointer);
            if (pointer.CompareTo(row.Paragraph.ContentStart) < 0) return row.Line.Offset;
        }
        return rows[^1].Line.Offset + rows[^1].Line.Length;
    }

    private static int ReadSelectionOffset((Paragraph Paragraph, TextLine Line) row, TextPointer pointer) =>
        row.Line.Offset + ParagraphText.OffsetOf(row.Paragraph, pointer, row.Line.Length);

    private void OnReaderCopy(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Copy ||
            sender is not RichTextBox { Selection.IsEmpty: false } body) return;
        if (!continuous)
        {
            var selection = ReadSelectionForAction(body, out var clipped);
            if (clipped)
            {
                e.Handled = true;
                if (selection != null) CopyReaderText(selection.Text);
                return;
            }
        }
        if (!appearance.Hyphenation) return;
        var displayed = body.Selection.Text;
        if (!displayed.Contains('\u00ad')) return;
        var source = ParagraphText.ReadSelectionText(body);
        if (source == displayed) return;
        e.Handled = true;
        CopyReaderText(source);
    }

    private void CopyReaderText(string value)
    {
        if (value.Length == 0) return;
        try { Clipboard.SetText(value); }
        catch (ExternalException) { ShowStatus("剪贴板暂不可用，请重试。"); }
    }

    private void BeginSelectionNote(FrameworkElement target, NoteSelection selected)
    {
        if (target is RichTextBox) SelectFacingPage(ReferenceEquals(target, FacingTextList));
        else if (target is PdfSelectionLayer layer) ActivatePdfPage(layer);
        SetNoteEditor(selected, AnnotationAt(selected.Position));
        OpenPanel(Panel.Notes);
        UpdateNotesList();
        NoteInput.Focus();
    }
}
