using System.Windows;
using System.Text.Json.Serialization;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeaReader.Native;

internal readonly record struct TextPaginationLayout(double Width, double Height, string FontFamily, double Size,
    double LineHeight, double DpiX, double DpiY, TextFormattingMode FormattingMode,
    bool Bold = false, TextAlignment Alignment = TextAlignment.Left, bool Hyphenation = false);

internal readonly record struct TextPageStart(int Offset, double Fraction, int SourceOffset,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Illustration = null);

internal sealed class TextPagination
{
    // RichTextBox reserves space for the caret on its first document. Keep the same
    // width for replacement documents and for the detached layout.
    internal static readonly Thickness DocumentPadding = new(5, 0, 5, 0);

    private const int MaximumChunkRows = 256;
    private const int MaximumChunkCharacters = 32 * 1024;
    private const double LayoutTolerance = .1;
    private readonly TextPageStart[][] chapters;
    private readonly int[] chapterStarts;
    private readonly Dictionary<int, int>?[]? illustratedPages;

    private TextPagination(TextPageStart[][] chapters, Dictionary<int, int>?[]? illustratedPages = null,
        IReadOnlyList<long>? chapterCharacterStarts = null, bool lastPageIncomplete = false)
    {
        this.chapters = chapters;
        this.illustratedPages = illustratedPages;
        ChapterCharacterStarts = chapterCharacterStarts;
        chapterStarts = new int[chapters.Length + 1];
        for (var chapter = 0; chapter < chapters.Length; chapter++)
            chapterStarts[chapter + 1] = checked(chapterStarts[chapter] + chapters[chapter].Length);
        ConfirmedCount = Count - (lastPageIncomplete ? 1 : 0);
    }

    internal static TextPagination FromTextStarts(TextPageStart[] starts) => new([starts]);

    internal int Count => chapterStarts[^1];

    internal int ConfirmedCount { get; }

    internal bool ContainsChapter(int chapter) => chapter >= 0 && chapter < chapters.Length;

    internal IReadOnlyList<long>? ChapterCharacterStarts { get; }

    internal int LastPageOfChapter(int chapter) => chapterStarts[chapter + 1] - 1;

    internal int FindPage(int chapter, int offset, double fraction) => FindPage(chapter, offset, fraction, false);

    internal int FindPage(int chapter, int sourceOffset) => FindPage(chapter, sourceOffset, 0, sourceOffset >= 0);

    internal int FindPage(int chapter, TextPageStart position)
    {
        if (position.Illustration is int figure && illustratedPages != null && chapter >= 0 && chapter < chapters.Length &&
            illustratedPages[chapter] is { } pages && pages.TryGetValue(figure, out var page))
            return chapterStarts[chapter] + page;
        return position.SourceOffset >= 0 ? FindPage(chapter, position.SourceOffset) : FindPage(chapter, position.Offset, position.Fraction);
    }

    private int FindPage(int chapter, int offset, double fraction, bool useSourceOffset)
    {
        chapter = Math.Clamp(chapter, 0, chapters.Length - 1);
        var pages = chapters[chapter];
        var lower = 0;
        var upper = pages.Length - 1;
        while (lower < upper)
        {
            var middle = lower + (upper - lower + 1) / 2;
            var candidate = pages[middle];
            var beforeOrAt = useSourceOffset ? candidate.SourceOffset <= offset :
                candidate.Offset < offset || candidate.Offset == offset && candidate.Fraction <= fraction + .00001;
            if (beforeOrAt) lower = middle;
            else upper = middle - 1;
        }
        return chapterStarts[chapter] + lower;
    }

    internal TextPageStart GetPage(int globalPage, out int chapter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(globalPage);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(globalPage, Count);
        chapter = Array.BinarySearch(chapterStarts, globalPage);
        if (chapter < 0) chapter = ~chapter - 1;
        return chapters[chapter][globalPage - chapterStarts[chapter]];
    }

    internal static Task<TextPagination> BuildAsync(TextDocument current, IChapterBook? book, int currentChapter,
        TextPaginationLayout layout, CancellationToken token, Action<TextPagination>? publishPrefix = null)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<TextPagination>(token);
        var result = new TaskCompletionSource<TextPagination>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                result.TrySetResult(Build(current, book, currentChapter, layout, token, publishPrefix));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                result.TrySetCanceled(token);
            }
            catch (Exception error)
            {
                result.TrySetException(error);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "SeaReader text pagination",
            Priority = ThreadPriority.BelowNormal
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return result.Task;
    }

    private static TextPagination Build(TextDocument current, IChapterBook? book, int currentChapter,
        TextPaginationLayout layout, CancellationToken token, Action<TextPagination>? publishPrefix)
    {
        var font = new FontFamily(layout.FontFamily);
        var flow = new FlowDocument
        {
            ColumnWidth = double.PositiveInfinity,
            PagePadding = DocumentPadding
        };
        ApplyTypography(flow, layout.Size, layout.LineHeight, layout.Bold, layout.Alignment);
        var box = new RichTextBox(flow)
        {
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            FontFamily = font,
            FontSize = layout.Size,
            IsReadOnly = true,
            IsUndoEnabled = false
        };
        VisualTreeHelper.SetRootDpi(box, new DpiScale(layout.DpiX, layout.DpiY));
        TextOptions.SetTextFormattingMode(box, layout.FormattingMode);

        var chapters = new TextPageStart[book?.Chapters.Count ?? 1][];
        var characters = book != null ? new long[chapters.Length + 1] : null;
        var illustratedPages = book is EpubDocument ? new Dictionary<int, int>?[chapters.Length] : null;
        var completedPages = 0;
        long nextPrefixCount = 1;
        for (var chapter = 0; chapter < chapters.Length; chapter++)
        {
            token.ThrowIfCancellationRequested();
            var content = book == null || chapter == currentChapter ? current :
                BookFormats.ReadChapterDocumentAsync(book, chapter, token).GetAwaiter().GetResult();
            if (characters != null) characters[chapter + 1] = characters[chapter] + content.Text.Length;
            var heading = book == null ? null : ChapterHeading(content, chapter, book.Chapters[chapter].Title);
            Dictionary<int, int>? figures = null;
            if (illustratedPages != null && content.HasIllustrations) illustratedPages[chapter] = figures = new();
            var index = chapter;
            Action<List<TextPageStart>>? report = publishPrefix == null ? null :
                starts => ReportPrefix(index, starts, incomplete: true);
            chapters[chapter] = PaginateChapter(box, content, heading, layout, token, report, figures);
            if (chapter + 1 < chapters.Length && publishPrefix != null)
                ReportPrefix(chapter, chapters[chapter], incomplete: false);
            completedPages += chapters[chapter].Length;
        }
        token.ThrowIfCancellationRequested();
        return new TextPagination(chapters, illustratedPages, characters);

        void ReportPrefix(int chapter, IReadOnlyList<TextPageStart> starts, bool incomplete)
        {
            var confirmed = completedPages + starts.Count - (incomplete ? 1 : 0);
            // Publish early and exponentially as the index grows. Keep reporting the chapter currently
            // being read so a saved position there can settle before the rest of the book is measured.
            if (confirmed < 1 || confirmed < nextPrefixCount && chapter != currentChapter) return;
            token.ThrowIfCancellationRequested();
            var prefix = chapters[..(chapter + 1)];
            if (incomplete) prefix[^1] = starts.ToArray();
            var figurePrefix = illustratedPages?[..(chapter + 1)];
            if (incomplete && figurePrefix?[^1] is { } figures) figurePrefix[^1] = new Dictionary<int, int>(figures);
            publishPrefix!(new TextPagination(prefix, figurePrefix, lastPageIncomplete: incomplete));
            nextPrefixCount = Math.Max(nextPrefixCount, 2L * confirmed);
        }
    }

    internal static void ApplyTypography(FlowDocument document, double size, double lineHeight, bool bold, TextAlignment alignment)
    {
        document.FontSize = size;
        document.LineHeight = lineHeight;
        document.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        document.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        document.TextAlignment = alignment;
    }

    internal static string ChapterHeading(TextDocument document, int chapter, string title)
    {
        var number = $"第 {chapter + 1} 章";
        var firstLine = document.Lines.Take(4).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line.Text)).Text;
        return string.IsNullOrWhiteSpace(title) || title == number || title.Trim() == firstLine.Trim()
            ? number : $"{number} · {title.Trim()}";
    }

    internal static Paragraph CreateHeading(string heading, double size) => new(new Run(heading))
    {
        FontWeight = FontWeights.SemiBold,
        FontSize = Math.Clamp(size * 1.22, 16, 53),
        Margin = new Thickness(0, 0, 0, 16),
        TextAlignment = TextAlignment.Left
    };

    internal static Paragraph CreateBodyParagraph(TextDocument document, TextLine line, bool hyphenation = false,
        Size figureViewport = default, RoutedEventHandler? openIllustration = null)
    {
        var styles = document.GetTextStyles(line);
        var references = document.GetReferences(line);
        var figures = EpubIllustrationLayout.CanFit(figureViewport) ? document.GetIllustrations(line) : default;
        var breaks = hyphenation && line.Length > 0
            ? EnglishHyphenation.FindBreaks(document.LanguageTag, document.Text.AsSpan(line.Offset, line.Length)) : [];
        if (styles.IsEmpty && references.IsEmpty && figures.IsEmpty)
            return new Paragraph(ParagraphText.CreateRun(line.Length == 0 ? "\u00a0" : line.Text, breaks))
                { Margin = new Thickness(0) };

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        if (!figures.IsEmpty) paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        var position = line.Offset;
        var end = position + line.Length;
        int styleIndex = 0;
        int referenceIndex = 0;
        int figureIndex = 0;
        int activeReference = -1;
        int nextBreak = 0;
        Hyperlink? link = null;
        while (true)
        {
            while (figureIndex < figures.Length && Math.Min(figures[figureIndex].Offset, end) <= position)
            {
                if (paragraph.Inlines.LastInline != null) paragraph.Inlines.Add(new LineBreak());
                paragraph.Inlines.Add(EpubIllustrationLayout.Create(figures[figureIndex++], figureViewport, openIllustration));
                activeReference = -1;
            }
            if (position == end) break;
            if (paragraph.Inlines.LastInline is InlineUIContainer) paragraph.Inlines.Add(new LineBreak());

            while (styleIndex < styles.Length && styles[styleIndex].Offset + styles[styleIndex].Length <= position)
                styleIndex++;
            while (referenceIndex < references.Length &&
                references[referenceIndex].Offset + references[referenceIndex].Length <= position)
                referenceIndex++;

            var segmentEnd = figureIndex < figures.Length ? Math.Min(end, figures[figureIndex].Offset) : end;
            var emphasis = TextEmphasis.None;
            if (styleIndex < styles.Length)
            {
                var style = styles[styleIndex];
                if (style.Offset > position) segmentEnd = Math.Min(segmentEnd, style.Offset);
                else
                {
                    segmentEnd = Math.Min(segmentEnd, style.Offset + style.Length);
                    emphasis = style.Emphasis;
                }
            }

            var reference = referenceIndex < references.Length ? references[referenceIndex] : null;
            if (reference != null)
            {
                segmentEnd = Math.Min(segmentEnd,
                    reference.Offset > position ? reference.Offset : reference.Offset + reference.Length);
            }
            var inReference = reference != null && reference.Offset <= position;
            if (inReference && activeReference != referenceIndex)
            {
                link = new Hyperlink { Tag = reference, Focusable = true };
                KeyboardNavigation.SetIsTabStop(link, false);
                link.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(KeepVisibleReferenceInPlace));
                link.SetResourceReference(TextElement.ForegroundProperty, "ReaderLinkInk");
                AutomationProperties.SetHelpText(link, reference!.Kind switch
                {
                    EpubReferenceKind.InternalLink => "前往书内位置",
                    EpubReferenceKind.Glossary => "查看本书术语释义",
                    _ => "查看脚注"
                });
                paragraph.Inlines.Add(link);
                activeReference = referenceIndex;
            }

            var run = CreateRun(position, segmentEnd);
            if ((emphasis & TextEmphasis.Bold) != 0) run.FontWeight = FontWeights.Bold;
            if ((emphasis & TextEmphasis.Italic) != 0) run.FontStyle = FontStyles.Italic;
            if (inReference) link!.Inlines.Add(run);
            else paragraph.Inlines.Add(run);
            position = segmentEnd;
        }
        return paragraph;

        Run CreateRun(int from, int to)
        {
            var source = document.Text[from..to];
            int first = nextBreak;
            while (nextBreak < breaks.Length && breaks[nextBreak] <= to - line.Offset) nextBreak++;
            if (first == nextBreak) return new Run(source);
            Span<int> relative = stackalloc int[nextBreak - first];
            for (int i = 0; i < relative.Length; i++) relative[i] = breaks[first + i] - (from - line.Offset);
            return ParagraphText.CreateRun(source, relative);
        }
    }

    private static void KeepVisibleReferenceInPlace(object sender, RequestBringIntoViewEventArgs e)
    {
        // Focusing the visible continuation of a wrapped link must keep the current page in place.
        if (sender is Hyperlink link && KeyboardNavigation.GetIsTabStop(link)) e.Handled = true;
    }

    private static TextPageStart[] PaginateChapter(RichTextBox box, TextDocument content, string? heading,
        TextPaginationLayout layout, CancellationToken token, Action<List<TextPageStart>>? reportPrefix,
        Dictionary<int, int>? illustratedPages)
    {
        var starts = new List<TextPageStart> { heading == null ? new(0, 0, 0) : new(-1, 0, -1) };
        var pageBottom = layout.Height;
        var documentTop = 0.0;
        var nextChunkTop = 0.0;
        var size = new Size(layout.Width, layout.Height);
        long nextPrefixCount = 2;

        for (var first = 0; first < content.Lines.Count;)
        {
            token.ThrowIfCancellationRequested();
            var end = EndOfChunk(content, first);
            var title = first == 0 && heading != null ? CreateHeading(heading, layout.Size) : null;
            // One extra row gives the measured position of the next chunk without counting its text twice.
            var rows = new Paragraph[Math.Min(end + 1, content.Lines.Count) - first];
            using (box.DeclareChangeBlock())
            {
                box.Document.Blocks.Clear();
                if (title != null) box.Document.Blocks.Add(title);
                for (var index = 0; index < rows.Length; index++)
                {
                    rows[index] = CreateBodyParagraph(content, content.Lines[first + index], layout.Hyphenation, size);
                    box.Document.Blocks.Add(rows[index]);
                }
            }
            box.Measure(size);
            box.Arrange(new Rect(size));
            box.UpdateLayout();

            var firstTop = ParagraphTop(title ?? rows[0]);
            if (!double.IsFinite(firstTop)) throw new InvalidOperationException("无法测量电子书页面。");
            if (first == 0)
            {
                nextChunkTop = firstTop;
                if (illustratedPages == null) documentTop = firstTop;
            }
            var chunkTranslation = nextChunkTop - firstTop;
            if (title != null)
                ReadPageBreaks(title, rows[0], -1, 0, chunkTranslation, documentTop,
                    layout.Height, starts, ref pageBottom);

            var count = end - first;
            for (var index = 0; index < count; index++)
            {
                var following = index + 1 < rows.Length ? rows[index + 1] : null;
                var source = content.Lines[first + index];
                ReadPageBreaks(rows[index], following, source.Offset, source.Length, chunkTranslation, documentTop,
                    layout.Height, starts, ref pageBottom, illustratedPages);
            }
            if (count < rows.Length) nextChunkTop = ParagraphTop(rows[count]) + chunkTranslation;
            first = end;
            if (reportPrefix != null && first < content.Lines.Count && starts.Count >= nextPrefixCount)
            {
                reportPrefix(starts);
                // A chapter needs three starts to confirm its own first pair of pages.
                nextPrefixCount = Math.Max(3, 2L * starts.Count - 1);
            }
            box.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
        return starts.ToArray();
    }

    private static int EndOfChunk(TextDocument document, int start)
    {
        var firstOffset = document.Lines[start].Offset;
        var end = start + 1;
        while (end < document.Lines.Count && end - start < MaximumChunkRows &&
               document.Lines[end].Offset - firstOffset < MaximumChunkCharacters)
            end++;
        return end;
    }

    private static double ParagraphTop(Paragraph paragraph) =>
        paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;

    private static void ReadPageBreaks(Paragraph paragraph, Paragraph? following, int offset, int length,
        double chunkTranslation, double documentTop, double pageHeight, List<TextPageStart> starts, ref double pageBottom,
        Dictionary<int, int>? illustratedPages = null)
    {
        var end = paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        var hasFigures = illustratedPages != null && paragraph.LineStackingStrategy == LineStackingStrategy.MaxHeight;
        if (end.IsEmpty || !hasFigures && end.Bottom + chunkTranslation <= pageBottom + LayoutTolerance) return;
        var rowTop = ParagraphTop(paragraph);
        var rowHeight = (following != null ? ParagraphTop(following) : end.Bottom) - rowTop;
        var pageStart = starts[^1];
        var line = paragraph.ContentStart.GetLineStartPosition(0);
        while (line != null && line.CompareTo(paragraph.ContentEnd) < 0)
        {
            var bounds = line.GetCharacterRect(LogicalDirection.Forward);
            var figure = hasFigures ? EpubIllustrationLayout.AtLine(line) : null;
            if (!bounds.IsEmpty && bounds.Bottom + chunkTranslation > pageBottom + LayoutTolerance)
            {
                var fraction = rowHeight > 0 ? Math.Clamp((bounds.Top - rowTop) / rowHeight, 0, 1) : 0;
                var source = figure?.Offset ?? (offset < 0 ? -1 : offset + ParagraphText.OffsetOf(paragraph, line, length));
                var start = new TextPageStart(offset, fraction, source, figure?.Index);
                if (start != pageStart)
                {
                    starts.Add(start);
                    pageStart = start;
                    pageBottom = bounds.Top + chunkTranslation - documentTop + pageHeight;
                }
            }
            if (figure != null) illustratedPages!.TryAdd(figure.Index, starts.Count - 1);

            var next = line.GetLineStartPosition(1, out var moved);
            line = moved == 1 && next != null && next.CompareTo(line) > 0 ? next : null;
        }
    }
}
