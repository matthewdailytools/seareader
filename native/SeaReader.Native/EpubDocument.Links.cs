using System.Text;

namespace SeaReader.Native;

public sealed partial class EpubDocument
{
    private static readonly UTF8Encoding StrictLinkUtf8 = new(false, true);

    internal FixedEpubTarget? ResolveFixedLink(int page, string href)
    {
        if (ReadFixedLinkDestination(page, href) is not { } destination ||
            !_chaptersByPath.TryGetValue(destination.Path, out var target)) return null;
        if (destination.Path == Chapters[page].Href) target = page;
        return new FixedEpubTarget(target, destination.Fragment,
            destination.Literal == destination.Fragment ? null : destination.Literal);
    }

    internal EpubReference? ResolveFixedFootnote(int page, string href) =>
        ReadFixedLinkDestination(page, href) is { Fragment.Length: > 0 } target && _textManifestMediaTypes.ContainsKey(target.Path)
            ? new EpubReference(0, 0, target.Path, target.Fragment, EpubReferenceKind.Footnote) : null;

    private (string Path, string Fragment, string Literal)? ReadFixedLinkDestination(int page, string href)
    {
        if (FixedLayout == null || page < 0 || page >= Chapters.Count) return null;
        return ReadStrictLinkDestination(href, Chapters[page].Href);
    }

    private static (string Path, string Fragment, string Literal)? ReadStrictLinkDestination(string href, string from)
    {
        if (href.Length is 0 or > 4096 || href.Any(char.IsControl)) return null;
        href = href.Trim();
        var separator = href.IndexOf('#');
        var address = separator < 0 ? href : href[..separator];
        if ((address.Length != 0 && address.Split('/').Any(segment => segment.Length == 0)) ||
            !HasValidLinkEscapes(href) || !HasValidLinkEncoding(href)) return null;
        return ReadLinkDestination(href, from);
    }

    private static bool HasValidLinkEncoding(string href)
    {
        Span<byte> bytes = stackalloc byte[href.Length / 3];
        for (var index = 0; index < href.Length;)
        {
            if (href[index] != '%')
            {
                index++;
                continue;
            }
            var count = 0;
            do
            {
                bytes[count++] = (byte)((Uri.FromHex(href[index + 1]) << 4) | Uri.FromHex(href[index + 2]));
                index += 3;
            }
            while (index < href.Length && href[index] == '%');
            try { _ = StrictLinkUtf8.GetCharCount(bytes[..count]); }
            catch (DecoderFallbackException) { return false; }
        }
        return true;
    }

    internal async Task<(int Chapter, int? SourceOffset)?> ResolveInternalLinkAsync(EpubReference reference,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (reference.Kind != EpubReferenceKind.InternalLink || reference.Chapter is not int index ||
            index < 0 || index >= Chapters.Count || Chapters[index].Href != reference.Path) return null;
        if (reference.Fragment.Length == 0) return (index, null);

        var anchors = await ReadChapterAnchorsAsync(index, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return anchors.TryGetValue(reference.Fragment, out var offset) ? (index, offset) : null;
    }

    private static (string Path, string Fragment, int Chapter)? ReadInternalLink(string href, string chapterPath,
        int sourceChapter, IReadOnlyDictionary<string, int> chaptersByPath)
    {
        if (ReadLinkDestination(href, chapterPath) is not { } destination ||
            !chaptersByPath.TryGetValue(destination.Path, out var targetChapter)) return null;
        return (destination.Path, destination.Fragment, destination.Path == chapterPath ? sourceChapter : targetChapter);
    }

    private static (string Path, string Fragment, string Literal)? ReadLinkDestination(string href, string chapterPath)
    {
        int separator = href.IndexOf('#');
        var address = separator < 0 ? href.AsSpan() : href.AsSpan(0, separator);
        if (address.Contains('?') || !HasValidLinkEscapes(href)) return null;
        var path = ResolvePath(chapterPath, href);
        if (path == null || path.Contains(':') || path.Any(char.IsControl)) return null;

        var literal = separator < 0 ? "" : href[(separator + 1)..];
        var fragment = Uri.UnescapeDataString(literal);
        if (fragment.Length > 1024 || fragment.Any(char.IsControl)) return null;
        return (path, fragment, literal);
    }

    private static bool HasValidLinkEscapes(string href)
    {
        for (int index = 0; index < href.Length; index++)
        {
            if (href[index] != '%') continue;
            if (index + 2 >= href.Length || !Uri.IsHexDigit(href[index + 1]) || !Uri.IsHexDigit(href[index + 2]))
                return false;
            index += 2;
        }
        return true;
    }
}
