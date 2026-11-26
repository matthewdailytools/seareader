using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SeaReader.Native;

/// <summary>A decoded text document with stable UTF-16 offsets and indexable display rows.</summary>
public sealed class TextDocument
{
    private const int MaximumRowLength = 1024;
    private const int ReadBufferSize = 64 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly int[] rowStarts;
    private readonly Dictionary<int, int> continuedParagraphs = new();

    static TextDocument() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private TextDocument(string text, string encodingName, CancellationToken cancellationToken)
    {
        Text = text;
        EncodingName = encodingName;
        var starts = new List<int>();
        var paragraphStart = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            starts.Add(paragraphStart);
            var newline = text.AsSpan(paragraphStart).IndexOfAny('\r', '\n');
            var paragraphEnd = newline < 0 ? text.Length : paragraphStart + newline;

            for (var rowStart = paragraphStart; paragraphEnd - rowStart > MaximumRowLength;)
            {
                rowStart = FindSplit(text, rowStart);
                continuedParagraphs.Add(starts.Count, paragraphStart);
                starts.Add(rowStart);
            }

            if (newline < 0)
                break;

            paragraphStart = paragraphEnd + 1;
            if (text[paragraphEnd] == '\r' && paragraphStart < text.Length && text[paragraphStart] == '\n')
                paragraphStart++;
        } while (true);

        rowStarts = starts.ToArray();
        Lines = new LineCollection(this);
    }

    public string Text { get; }
    public string EncodingName { get; }
    public IReadOnlyList<TextLine> Lines { get; }

    public static TextDocument FromText(string value) => new(value, "utf-8", CancellationToken.None);

    public static Task<TextDocument> LoadAsync(string path, string? encoding = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            if (encoding is not null)
                return await ReadAsync(path, ResolveEncoding(encoding), detectBom: false, cancellationToken).ConfigureAwait(false);

            try
            {
                return await ReadAsync(path, StrictUtf8, detectBom: true, cancellationToken).ConfigureAwait(false);
            }
            catch (DecoderFallbackException)
            {
                return await ReadAsync(path, ResolveEncoding("gb18030"), detectBom: false, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    public int FindLine(int offset)
    {
        var index = Array.BinarySearch(rowStarts, Math.Clamp(offset, 0, Text.Length));
        return index >= 0 ? index : ~index - 1;
    }

    public Task<IReadOnlyList<TextSearchHit>> SearchAsync(string query, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var needle = query.Trim();
        if (needle.Length == 0 || limit == 0)
            return Task.FromResult<IReadOnlyList<TextSearchHit>>([]);

        return Task.Run<IReadOnlyList<TextSearchHit>>(() =>
        {
            var results = new List<TextSearchHit>(Math.Min(limit, 100));
            var position = 0;
            while (position <= Text.Length - needle.Length && results.Count < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = Text.IndexOf(needle, position, StringComparison.OrdinalIgnoreCase);
                if (match < 0)
                    break;

                var line = FindLine(match);
                results.Add(new TextSearchHit(match, line, Excerpt(match, needle.Length)));
                position = match + needle.Length;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return results;
        }, cancellationToken);
    }

    private static async Task<TextDocument> ReadAsync(string path, Encoding encoding, bool detectBom, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, encoding, detectBom, ReadBufferSize, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var name = reader.CurrentEncoding.CodePage switch
        {
            1200 => "utf-16le",
            1201 => "utf-16be",
            _ => reader.CurrentEncoding.WebName,
        };
        return new TextDocument(text, name, cancellationToken);
    }

    private static Encoding ResolveEncoding(string name) => name.ToLowerInvariant() switch
    {
        "utf-16le" => Encoding.Unicode,
        "utf-16be" => Encoding.BigEndianUnicode,
        _ => Encoding.GetEncoding(name),
    };

    private static int FindSplit(string text, int start)
    {
        var split = start + MaximumRowLength;
        for (var probe = split - 1; probe >= split - 64; probe--)
        {
            if (char.IsWhiteSpace(text[probe]))
                return probe + 1;
        }
        return char.IsHighSurrogate(text[split - 1]) && char.IsLowSurrogate(text[split]) ? split - 1 : split;
    }

    private TextLine GetLine(int index)
    {
        var start = rowStarts[index];
        var end = index + 1 < rowStarts.Length ? rowStarts[index + 1] : Text.Length;
        if (end > start && Text[end - 1] == '\n')
            end--;
        if (end > start && Text[end - 1] == '\r')
            end--;
        var paragraph = continuedParagraphs.TryGetValue(index, out var paragraphStart) ? paragraphStart : start;
        return new TextLine(Text, start, end - start, paragraph);
    }

    private string Excerpt(int offset, int length)
    {
        var start = Math.Max(0, offset - 28);
        var end = (int)Math.Min(Text.Length, (long)offset + length + 45);
        if (start > 0 && char.IsLowSurrogate(Text[start]))
            start--;
        if (end < Text.Length && char.IsLowSurrogate(Text[end]))
            end++;
        return (start > 0 ? "…" : "") + Text[start..end].Replace('\r', ' ').Replace('\n', ' ') +
            (end < Text.Length ? "…" : "");
    }

    // WPF binds directly to IList and only retrieves rows realized by the virtualizing panel.
    private sealed class LineCollection(TextDocument owner) : IReadOnlyList<TextLine>, IList
    {
        public int Count => owner.rowStarts.Length;
        public TextLine this[int index] => owner.GetLine(index);
        public IEnumerator<TextLine> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        bool IList.IsReadOnly => true;
        bool IList.IsFixedSize => true;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;

        object? IList.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException();
        }

        int IList.IndexOf(object? value)
        {
            if (value is not TextLine line)
                return -1;
            var index = owner.FindLine(line.Offset);
            return this[index].Equals(line) ? index : -1;
        }

        bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

        void ICollection.CopyTo(Array array, int index)
        {
            for (var row = 0; row < Count; row++)
                array.SetValue(this[row], index + row);
        }

        int IList.Add(object? value) => throw new NotSupportedException();
        void IList.Clear() => throw new NotSupportedException();
        void IList.Insert(int index, object? value) => throw new NotSupportedException();
        void IList.Remove(object? value) => throw new NotSupportedException();
        void IList.RemoveAt(int index) => throw new NotSupportedException();
    }
}

public readonly record struct TextLine
{
    private readonly string source;

    internal TextLine(string source, int offset, int length, int paragraphOffset)
    {
        this.source = source;
        Offset = offset;
        Length = length;
        ParagraphOffset = paragraphOffset;
    }

    public int Offset { get; }
    public int Length { get; }
    public int ParagraphOffset { get; }
    public string Text => source is null ? "" : source.Substring(Offset, Length);
}

public readonly record struct TextSearchHit(int Offset, int LineIndex, string Excerpt);
