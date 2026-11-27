namespace SeaReader.Native;

public sealed partial class EpubDocument
{
    private static readonly Dictionary<string, TextEmphasis> EmphasisElements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["strong"] = TextEmphasis.Bold,
        ["b"] = TextEmphasis.Bold,
        ["em"] = TextEmphasis.Italic,
        ["i"] = TextEmphasis.Italic,
        ["cite"] = TextEmphasis.Italic,
        ["dfn"] = TextEmphasis.Italic,
        ["var"] = TextEmphasis.Italic
    };

    internal Task<TextDocument> ReadStyledChapterAsync(int index, CancellationToken ct = default)
    {
        var chapter = GetStyledChapter(index);
        return Task.Run(() => ReadStyledChapter(index, chapter, ct), ct);
    }

    internal Task<(TextDocument Document, int? SourceOffset)> ReadChapterAtAnchorAsync(
        int index, string fragment, CancellationToken ct)
    {
        var chapter = GetStyledChapter(index);
        return Task.Run(() =>
        {
            var anchors = fragment.Length == 0 ? null : new Dictionary<string, int>(StringComparer.Ordinal);
            var document = ReadStyledChapter(index, chapter, ct, anchors: anchors);
            int? offset = anchors != null && anchors.TryGetValue(fragment, out var value) ? value : null;
            return (document, offset);
        }, ct);
    }

    internal Task<(TextDocument Document, int? SourceOffset)> ReadChapterWithLegacyPositionAsync(
        int index, string position, CancellationToken ct = default)
    {
        var chapter = GetStyledChapter(index);
        return Task.Run(() =>
        {
            var anchor = CreateLegacyAnchor(index, position);
            var document = ReadStyledChapter(index, chapter, ct, anchor);
            return (document, anchor?.SourceOffset);
        }, ct);
    }

    internal Task<int?> ResolveLegacyPositionAsync(int index, string position, CancellationToken ct = default)
    {
        var chapter = GetStyledChapter(index);
        return Task.Run(() =>
        {
            var anchor = CreateLegacyAnchor(index, position);
            if (anchor == null) return null;
            ReadChapter(chapter, ct, legacyAnchor: anchor);
            return anchor.SourceOffset;
        }, ct);
    }

    private EpubChapter GetStyledChapter(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Chapters.Count);
        return Chapters[index];
    }

    private EpubLegacyAnchor? CreateLegacyAnchor(int index, string position)
    {
        var cfi = EpubLegacyCfi.Parse(position);
        return cfi != null && FindLegacyChapter(cfi) == index ? new EpubLegacyAnchor(cfi) : null;
    }

    internal int? FindLegacyChapter(string position) => EpubLegacyCfi.Parse(position) is { } cfi ? FindLegacyChapter(cfi) : null;

    private int? FindLegacyChapter(EpubLegacyCfi cfi)
    {
        if (cfi.SpineElement.Index != _spineElement.Index ||
            cfi.SpineElement.Id is { } spineId && spineId != _spineElement.Id) return null;
        var index = Array.BinarySearch(_spineIndexes, cfi.SpineIndex);
        return index >= 0 && (cfi.SpineId == null || cfi.SpineId == _spineIds[index]) ? index : null;
    }

    private TextDocument ReadStyledChapter(int index, EpubChapter chapter, CancellationToken ct,
        EpubLegacyAnchor? legacyAnchor = null, Dictionary<string, int>? anchors = null)
    {
        var styles = new List<TextStyleSpan>();
        var references = new ReferenceCollector(chapter.Href, index, _textManifestMediaTypes, _chaptersByPath);
        var illustrations = _rasterManifestItems.Count == 0 ? null : new IllustrationCollector(chapter.Href, _rasterManifestItems);
        var (value, language) = ReadChapter(chapter, ct, anchors: anchors, styles: styles, references: references, legacyAnchor: legacyAnchor,
            illustrations: illustrations);
        return TextDocument.FromText(value, styles.ToArray(), ct, language ?? _publicationLanguage, references.ToArray(),
            illustrations: illustrations?.Images);
    }
}
