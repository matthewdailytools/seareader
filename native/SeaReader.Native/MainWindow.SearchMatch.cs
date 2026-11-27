using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private sealed record TextSearchMatch(object Source, int? Chapter, int Start, int End);

    private TextSearchMatch? textSearchMatch;
    private TextSearchMatchAdorner? primarySearchMatch;
    private TextSearchMatchAdorner? facingSearchMatch;
    private bool textSearchMatchRefreshQueued;

    private void ShowTextSearchMatch(TextDocument? document, IChapterBook? source, int? matchChapter, int offset, int length)
    {
        var identity = (object?)source ?? document;
        if (identity == null || !ReferenceEquals(identity, (object?)epub ?? text) ||
            (source != null) != matchChapter.HasValue || offset < 0 || length <= 0 || length > int.MaxValue - offset)
            return;

        if (textSearchMatch == null) TextList.LayoutUpdated += OnTextSearchMatchLayoutUpdated;
        textSearchMatch = new TextSearchMatch(identity, matchChapter, offset, offset + length);
        RefreshTextSearchMatch();
    }

    private void ClearTextSearchMatch()
    {
        if (textSearchMatch == null) return;
        textSearchMatch = null;
        TextList.LayoutUpdated -= OnTextSearchMatchLayoutUpdated;
        primarySearchMatch?.Remove();
        primarySearchMatch = null;
        facingSearchMatch?.Remove();
        facingSearchMatch = null;
    }

    private void OnTextSearchMatchLayoutUpdated(object? sender, EventArgs e)
    {
        if (textSearchMatchRefreshQueued || closing) return;
        textSearchMatchRefreshQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            textSearchMatchRefreshQueued = false;
            RefreshTextSearchMatch();
        }, DispatcherPriority.Render);
    }

    private void RefreshTextSearchMatch()
    {
        if (textSearchMatch is not { } match) return;
        if (closing || !ReferenceEquals(match.Source, (object?)epub ?? text))
        {
            ClearTextSearchMatch();
            return;
        }

        var primaryRows = ReferenceEquals(windowDocument, text) && (match.Chapter == null || match.Chapter == chapter)
            ? visibleRows : null;
        var facingRows = HasSpread && facingPage is { Document: not null } other &&
            (match.Chapter == null || match.Chapter == other.Chapter) ? other.Rows : null;
        primarySearchMatch = RenderTextSearchMatch(TextList, primarySearchMatch, primaryRows, match);
        facingSearchMatch = RenderTextSearchMatch(FacingTextList, facingSearchMatch, facingRows, match);
    }

    private static TextSearchMatchAdorner? RenderTextSearchMatch(RichTextBox body, TextSearchMatchAdorner? adorner,
        IReadOnlyList<(Paragraph Paragraph, TextLine Line)>? rows, TextSearchMatch match)
    {
        if (!body.IsVisible || rows is not { Count: > 0 } || rows[0].Line.Offset >= match.End ||
            rows[^1].Line.Offset + rows[^1].Line.Length <= match.Start)
        {
            adorner?.Clear();
            return adorner;
        }

        var scroll = body.Template?.FindName("PART_ContentHost", body) as ScrollViewer ?? Descendant<ScrollViewer>(body);
        if (scroll?.Content is not FrameworkElement scope)
        {
            adorner?.Remove();
            return null;
        }
        if (!ReferenceEquals(adorner?.AdornedElement, scope))
        {
            adorner?.Remove();
            adorner = AdornerLayer.GetAdornerLayer(scope) is { } layer ? new TextSearchMatchAdorner(scope, layer) : null;
        }
        adorner?.Update(body, rows, match.Start, match.End);
        return adorner;
    }
}
