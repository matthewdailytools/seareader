using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace SeaReader.Native;

internal sealed record PdfTextHit(int Page, int Offset, int Length, string Excerpt, bool FromOcr);

internal sealed class PdfTextPage(string text, IReadOnlyList<PdfTextPage.Glyph> glyphs)
{
    internal readonly record struct Glyph(int Offset, int Length, int Line, Rect Bounds, TextOrientation Orientation);

    private static readonly NearestNeighbourWordExtractor WordExtractor = new(new()
    {
        GroupByOrientation = true,
        MaxDegreeOfParallelism = 1
    });

    internal string Text { get; } = text;
    internal bool HasText => glyphs.Count != 0;

    internal static PdfTextPage Read(Page page, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var glyphs = new List<Glyph>();
        if (page.Width <= 0 || page.Height <= 0) return new PdfTextPage("", glyphs);
        Rect previous = Rect.Empty;
        var previousOrientation = TextOrientation.Other;
        var line = 0;
        var contentOrder = new Dictionary<Letter, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < page.Letters.Count; index++) contentOrder[page.Letters[index]] = index;
        foreach (var word in page.GetWords(WordExtractor).OrderBy(word => word.Letters.Min(letter => contentOrder[letter])))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(word.Text)) continue;
            var screenBounds = ScreenBounds(word.BoundingBox, page.Width, page.Height);
            if (screenBounds.IsEmpty) continue;
            var orientation = word.Letters[0].TextOrientation;
            var wordBounds = ReadingBounds(screenBounds, orientation);

            bool previousWord = !previous.IsEmpty && builder.Length > 0;
            bool sameLine = previousWord && orientation == previousOrientation && SameLine(previous, wordBounds);
            if (previousWord && !sameLine)
            {
                builder.Append('\n');
                line++;
            }
            else if (sameLine && NeedsSpace(builder[^1], word.Text, previous, wordBounds)) builder.Append(' ');

            foreach (var letter in word.Letters)
            {
                var bounds = ScreenBounds(letter.GlyphRectangleLoose, page.Width, page.Height);
                if (bounds.IsEmpty || string.IsNullOrWhiteSpace(letter.Value)) continue;
                glyphs.Add(new Glyph(builder.Length, letter.Value.Length, line, bounds, letter.TextOrientation));
                builder.Append(letter.Value);
            }
            previous = wordBounds;
            previousOrientation = orientation;
        }
        return new PdfTextPage(builder.ToString(), glyphs);
    }

    private static Rect ReadingBounds(Rect bounds, TextOrientation orientation) => orientation switch
    {
        TextOrientation.Rotate90 => new Rect(bounds.Y, 1 - bounds.Right, bounds.Height, bounds.Width),
        TextOrientation.Rotate180 => new Rect(1 - bounds.Right, 1 - bounds.Bottom, bounds.Width, bounds.Height),
        TextOrientation.Rotate270 => new Rect(1 - bounds.Bottom, bounds.X, bounds.Height, bounds.Width),
        _ => bounds
    };

    private static bool SameLine(Rect previous, Rect next)
    {
        var overlap = Math.Min(previous.Bottom, next.Bottom) - Math.Max(previous.Top, next.Top);
        return overlap >= 0.4 * Math.Min(previous.Height, next.Height) &&
               next.Left >= previous.Left - Math.Max(previous.Height, next.Height);
    }

    private static bool NeedsSpace(char previous, string next, Rect previousBounds, Rect nextBounds) =>
        next.Length != 0 && (!IsUnspacedScript(previous) || !IsUnspacedScript(next[0]) ||
                           nextBounds.Left - previousBounds.Right > previousBounds.Height / 2);

    private static bool IsUnspacedScript(char ch) => ch is >= '\u3000' and <= '\u9fff' or >= '\uf900' and <= '\ufaff' or >= '\uff00' and <= '\uffef';

    private static Rect ScreenBounds(PdfRectangle source, double width, double height)
    {
        var left = Math.Max(0, Math.Min(Math.Min(source.TopLeft.X, source.BottomLeft.X), Math.Min(source.TopRight.X, source.BottomRight.X)) / width);
        var right = Math.Min(1, Math.Max(Math.Max(source.TopLeft.X, source.BottomLeft.X), Math.Max(source.TopRight.X, source.BottomRight.X)) / width);
        var top = Math.Max(0, 1 - Math.Max(Math.Max(source.TopLeft.Y, source.BottomLeft.Y), Math.Max(source.TopRight.Y, source.BottomRight.Y)) / height);
        var bottom = Math.Min(1, 1 - Math.Min(Math.Min(source.TopLeft.Y, source.BottomLeft.Y), Math.Min(source.TopRight.Y, source.BottomRight.Y)) / height);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : Rect.Empty;
    }

    internal bool Contains(Point point) => glyphs.Any(glyph => glyph.Bounds.Contains(point));

    internal int StepInsertion(int offset, bool forward)
    {
        if (forward)
        {
            foreach (var glyph in glyphs)
                if (glyph.Offset + glyph.Length > offset) return glyph.Offset + glyph.Length;
            return Text.Length;
        }
        for (var index = glyphs.Count - 1; index >= 0; index--)
            if (glyphs[index].Offset < offset) return glyphs[index].Offset;
        return 0;
    }

    internal int NearestInsertion(Point point)
    {
        var nearest = default(Glyph);
        var closest = double.PositiveInfinity;
        foreach (var glyph in glyphs)
        {
            var box = glyph.Bounds;
            var dx = Math.Max(Math.Max(box.Left - point.X, point.X - box.Right), 0);
            var dy = Math.Max(Math.Max(box.Top - point.Y, point.Y - box.Bottom), 0);
            bool vertical = glyph.Orientation is TextOrientation.Rotate90 or TextOrientation.Rotate270;
            var distance = vertical ? 16 * dx * dx + dy * dy : dx * dx + 16 * dy * dy;
            if (distance >= closest) continue;
            closest = distance;
            nearest = glyph;
            if (distance == 0) break;
        }
        bool before = nearest.Orientation switch
        {
            TextOrientation.Rotate90 => point.Y < nearest.Bounds.Y + nearest.Bounds.Height / 2,
            TextOrientation.Rotate180 => point.X > nearest.Bounds.X + nearest.Bounds.Width / 2,
            TextOrientation.Rotate270 => point.Y > nearest.Bounds.Y + nearest.Bounds.Height / 2,
            _ => point.X < nearest.Bounds.X + nearest.Bounds.Width / 2
        };
        return before ? nearest.Offset : nearest.Offset + nearest.Length;
    }

    internal IReadOnlyList<Rect> Bounds(int start, int end)
    {
        var bounds = new List<Rect>();
        if (start >= end) return bounds;
        var previousLine = -1;
        foreach (var glyph in glyphs)
        {
            if (glyph.Offset >= end) break;
            if (glyph.Offset + glyph.Length <= start) continue;
            if (glyph.Line == previousLine)
            {
                var previous = bounds[^1];
                previous.Union(glyph.Bounds);
                bounds[^1] = previous;
            }
            else bounds.Add(glyph.Bounds);
            previousLine = glyph.Line;
        }
        return bounds;
    }

    internal static IReadOnlyList<PdfTextHit> Find(string text, string query, int page, int limit, bool fromOcr = false)
    {
        var term = Regex.Replace(query.Trim(), @"\s+", " ");
        if (term.Length == 0) return [];
        var searchable = fromOcr ? Regex.Replace(text, @"\s+", " ") : text.Replace('\n', ' ');
        var results = new List<PdfTextHit>();
        var start = 0;
        while (start <= searchable.Length - term.Length && results.Count < limit)
        {
            var offset = searchable.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
            if (offset < 0) break;
            var left = Math.Max(0, offset - 32);
            var right = Math.Min(searchable.Length, offset + Math.Min(term.Length, 75) + 45);
            var excerpt = $"{(left > 0 ? "…" : "")}{searchable[left..right].Trim()}{(right < searchable.Length ? "…" : "")}";
            results.Add(new PdfTextHit(page, offset, term.Length, excerpt, fromOcr));
            start = offset + term.Length;
        }
        return results;
    }
}
