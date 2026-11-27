using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private sealed record InlineReaderSelection(BookEntry Book, FrameworkElement Body, ReaderSelection Selection,
        (long Id, Rect Bounds)? Mark = null);
    private InlineReaderSelection? inlineSelection;
    private int inlineSelectionRevision;

    private void InitializeInlineSelectionTools()
    {
        foreach (var body in new FrameworkElement[] { TextList, FacingTextList, PdfSelection, PdfFacingSelection })
        {
            body.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnInlineSelectionReleased), handledEventsToo: true);
            body.AddHandler(ContextMenuService.ContextMenuOpeningEvent,
                new ContextMenuEventHandler((_, _) => DismissInlineSelection()), handledEventsToo: true);
        }
        PdfSurface.ScrollChanged += (_, e) =>
        {
            if (e.HorizontalChange != 0 || e.VerticalChange != 0 || e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0 ||
                e.ExtentWidthChange != 0 || e.ExtentHeightChange != 0)
                DismissInlineSelection();
        };
        foreach (var button in InlineSelectionColors.Children.OfType<Button>())
        {
            var name = (string)button.Content;
            var style = Enum.Parse<AnnotationHighlight>((string)button.Tag);
            var icon = new Border { Width = 10, Height = style == AnnotationHighlight.Underline ? 2 : 10,
                Margin = new Thickness(0, 0, 5, 0), CornerRadius = new CornerRadius(2) };
            if (style == AnnotationHighlight.Underline) icon.SetResourceReference(BackgroundProperty, "MainInk");
            else icon.Background = ReaderHighlightPalette.Get(style);
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(icon);
            label.Children.Add(new TextBlock { Text = name });
            button.Content = label;
            AutomationProperties.SetName(button, name);
        }
    }

    private void OnInlineSelectionReleased(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement body || book is not { } opened) return;
        var revision = ++inlineSelectionRevision;
        Dispatcher.InvokeAsync(() =>
        {
            if (revision != inlineSelectionRevision || !ReferenceEquals(book, opened) || !CanShowInlineSelection(body) ||
                Mouse.LeftButton == MouseButtonState.Pressed || !body.IsKeyboardFocusWithin || shownPanel != null ||
                AppearancePopup.IsOpen || body.ContextMenu?.IsOpen == true || ReadReaderSelection(body) is not { } selection) return;
            OpenInlineSelection(new InlineReaderSelection(opened, body, selection));
        }, DispatcherPriority.Input);
    }

    private void OpenInlineSelection(InlineReaderSelection current, AnnotationEntry? saved = null)
    {
        DismissInlineSelection();
        if (current.Body is RichTextBox rich)
        {
            if (HasSpread)
            {
                var other = ReferenceEquals(rich, TextList) ? FacingTextList : TextList;
                if (!other.Selection.IsEmpty) other.Selection.Select(other.Selection.Start, other.Selection.Start);
            }
            if (saved == null) rich.IsInactiveSelectionHighlightEnabled = true;
        }
        else if (current.Body is PdfSelectionLayer layer) ActivatePdfPage(layer);
        inlineSelection = current;
        var hasHighlight = saved != null && ReaderHighlightPalette.IsMark(saved.Highlight);
        InlineSelectionHighlight.IsEnabled = InlineSelectionNote.IsEnabled = current.Selection.Note != null;
        InlineSelectionHighlight.Content = hasHighlight ? "改色" : "高亮";
        InlineSelectionNote.Content = saved == null ? "笔记" : string.IsNullOrWhiteSpace(saved.Note) ? "添加笔记" : "编辑笔记";
        AutomationProperties.SetName(InlineSelectionCard, saved == null ? "选中文字的快捷操作" :
            hasHighlight ? "已保存高亮的快捷操作" : "编辑已保存的笔记");
        AutomationProperties.SetName(InlineSelectionNote, saved == null ? "为选中文字写笔记" : (string)InlineSelectionNote.Content);
        InlineSelectionClearMark.Visibility = hasHighlight ? Visibility.Visible : Visibility.Collapsed;
        if (saved != null && !hasHighlight) BeginInlineSelectionNote();
        else ShowInlineSelectionView(InlineSelectionActions);
    }

    private bool CanShowInlineSelection(FrameworkElement body) =>
        !closing && !moving && !turningPage && !seekingPosition && !updatingSpread && body.IsVisible &&
        !PageTurnLayer.HasFrame && (!ReferenceEquals(body, FacingTextList) || HasSpread) &&
        (body is not PdfSelectionLayer layer || PdfPageFor(layer) >= 0);

    private InlineReaderSelection? ActiveInlineSelection()
    {
        if (inlineSelection is { } current && ReferenceEquals(book, current.Book) && CanShowInlineSelection(current.Body) &&
            (current.Mark.HasValue ? CurrentInlineAnnotation(current) != null : Equals(ReadReaderSelection(current.Body), current.Selection)))
            return current;
        DismissInlineSelection();
        return null;
    }

    private void UpdateInlineSelection(FrameworkElement body, ReaderSelection? selection)
    {
        if (inlineSelection is not { } active) return;
        var changed = ReferenceEquals(active.Body, body) && active.Mark == null
            ? !Equals(active.Selection, selection) : selection != null;
        if (changed) DismissInlineSelection();
    }

    private bool ShowInlineSelectionView(FrameworkElement view)
    {
        if (ActiveInlineSelection() is not { } current) return false;
        var anchor = InlineSelectionBounds(current);
        if (anchor.IsEmpty || ReaderSurface.ActualWidth < 24 || ReaderSurface.ActualHeight < 24)
        {
            DismissInlineSelection();
            return false;
        }

        InlineSelectionActions.Visibility = view == InlineSelectionActions ? Visibility.Visible : Visibility.Collapsed;
        InlineSelectionColors.Visibility = view == InlineSelectionColors ? Visibility.Visible : Visibility.Collapsed;
        InlineSelectionEditor.Visibility = view == InlineSelectionEditor ? Visibility.Visible : Visibility.Collapsed;
        InlineSelectionCard.Visibility = Visibility.Visible;
        const double inset = 8;
        InlineSelectionCard.MaxWidth = ReaderSurface.ActualWidth - 2 * inset;
        InlineSelectionCard.Measure(new Size(InlineSelectionCard.MaxWidth, double.PositiveInfinity));
        var size = InlineSelectionCard.DesiredSize;
        var top = ReaderHeader.IsVisible
            ? ReaderHeader.TranslatePoint(new Point(0, ReaderHeader.ActualHeight), ReaderSurface).Y + 4 : inset;
        var bottom = ReaderToolbar.IsVisible
            ? ReaderToolbar.TranslatePoint(new Point(), ReaderSurface).Y - 4 : ReaderSurface.ActualHeight - inset;
        var above = anchor.Top - size.Height - 6;
        var below = anchor.Bottom + 6;
        var y = above >= top || below + size.Height > bottom && anchor.Top - top > bottom - anchor.Bottom ? above : below;
        Canvas.SetLeft(InlineSelectionCard, Math.Clamp(anchor.Left + (anchor.Width - size.Width) / 2,
            inset, Math.Max(inset, ReaderSurface.ActualWidth - size.Width - inset)));
        Canvas.SetTop(InlineSelectionCard, Math.Clamp(y, top, Math.Max(top, bottom - size.Height)));
        return true;
    }

    private Rect InlineSelectionBounds(InlineReaderSelection current)
    {
        var body = current.Body;
        var scroll = body is PdfSelectionLayer ? PdfSurface : Descendant<ScrollViewer>(body);
        if (scroll == null || Descendant<ScrollContentPresenter>(scroll) is not { } presenter) return Rect.Empty;
        var transform = body.TransformToVisual(ReaderSurface);
        var viewport = presenter.TransformToVisual(ReaderSurface).TransformBounds(new Rect(presenter.RenderSize));
        var bounds = Rect.Empty;
        if (current.Mark is { } mark) AddVisible(mark.Bounds);
        else if (body is RichTextBox rich)
        {
            var first = rich.Selection.Start.GetCharacterRect(LogicalDirection.Forward);
            var last = rich.Selection.End.GetCharacterRect(LogicalDirection.Backward);
            first.Union(last);
            AddVisible(first);
        }
        else if (body is PdfSelectionLayer layer)
        {
            foreach (var rect in layer.SelectedBounds)
                AddVisible(new Rect(rect.X * body.ActualWidth, rect.Y * body.ActualHeight,
                    rect.Width * body.ActualWidth, rect.Height * body.ActualHeight));
        }
        return bounds;

        void AddVisible(Rect rect)
        {
            if (rect.IsEmpty) return;
            var visible = transform.TransformBounds(rect);
            visible.Intersect(viewport);
            bounds.Union(visible);
        }
    }

    private void DismissInlineSelection(bool restoreFocus = false, bool discardDraft = false)
    {
        inlineSelectionRevision++;
        if (pendingSavedMarkTap.HasValue) StopReaderTap();
        if (inlineSelection is not { } current) return;
        RememberInlineNoteDraft(discardDraft);
        inlineSelection = null;
        if (current.Body is RichTextBox rich) rich.ClearValue(RichTextBox.IsInactiveSelectionHighlightEnabledProperty);
        InlineSelectionCard.Visibility = Visibility.Collapsed;
        InlineSelectionEditor.Visibility = Visibility.Collapsed;
        if (restoreFocus && current.Body.IsVisible) current.Body.Focus();
    }

    private void OnInlineSelectionHighlight(object sender, RoutedEventArgs e)
    {
        if (ActiveInlineSelection() is not { } current) return;
        var selected = current.Selection.Note is { } note ? AnnotationAt(note.Position)?.Highlight : null;
        Button? currentColor = null;
        foreach (var button in InlineSelectionColors.Children.OfType<Button>())
        {
            var active = Equals(button.Tag, selected?.ToString());
            button.SetResourceReference(StyleProperty, active ? "ReaderActiveAction" : "ReaderAction");
            AutomationProperties.SetItemStatus(button, active ? "当前高亮" : "");
            if (active) currentColor = button;
        }
        if (ShowInlineSelectionView(InlineSelectionColors)) (currentColor ?? (Button)InlineSelectionColors.Children[0]).Focus();
    }

    private void OnInlineSelectionNote(object sender, RoutedEventArgs e)
    {
        if (ActiveInlineSelection() != null) BeginInlineSelectionNote();
    }

    private async void OnInlineSelectionColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } || !Enum.TryParse<AnnotationHighlight>(value, out var highlight) ||
            ActiveInlineSelection() is not { Selection.Note: { } note } current) return;
        DismissInlineSelection(restoreFocus: true);
        await MarkReaderSelectionAsync(current.Body, note, highlight);
    }

    private void OnInlineSelectionCopy(object sender, RoutedEventArgs e)
    {
        if (ActiveInlineSelection() is not { } current) return;
        DismissInlineSelection(restoreFocus: true);
        CopyReaderText(current.Selection.Text);
    }

    private void OnInlineSelectionSearch(object sender, RoutedEventArgs e)
    {
        if (ActiveInlineSelection() is not { } current) return;
        DismissInlineSelection();
        SearchSelectedText(current.Selection.Text);
    }

    private void OnInlineSelectionTranslate(object sender, RoutedEventArgs e)
    {
        if (ActiveInlineSelection() is not { } current) return;
        DismissInlineSelection(restoreFocus: true);
        ShowTranslation(this, current.Selection.Text);
    }
}
