using System.Globalization;
using System.IO;
using System.Text;

namespace SeaReader.Native;

internal static class EnglishHyphenation
{
    private const int LeftMinimum = 2;
    private const int RightMinimum = 3;
    private const int MaximumWordLength = 64;
    private static readonly Lazy<PatternTable> American = new(() => PatternTable.Load("us"));
    private static readonly Lazy<PatternTable> British = new(() => PatternTable.Load("gb"));

    internal static int[] FindBreaks(string? languageTag, ReadOnlySpan<char> text)
    {
        if (text.Length < LeftMinimum + RightMinimum || SelectTable(languageTag) is not { } table) return [];
        PatternTable? patterns = null;
        List<int>? breaks = null;
        for (int end = 0; end < text.Length;)
        {
            if (!char.IsAsciiLetter(text[end]))
            {
                end++;
                continue;
            }

            int start = end++;
            while (end < text.Length && char.IsAsciiLetter(text[end])) end++;
            int length = end - start;
            if (length < LeftMinimum + RightMinimum || length > MaximumWordLength ||
                IsWordContinuation(text, start - 1, -1) || IsWordContinuation(text, end, 1)) continue;

            patterns ??= table.Value;
            patterns.AppendBreaks(text[start..end], start, ref breaks);
        }
        return breaks?.ToArray() ?? [];
    }

    private static Lazy<PatternTable>? SelectTable(string? languageTag)
    {
        var tag = languageTag.AsSpan();
        if (tag.IsEmpty || tag.Equals("en", StringComparison.OrdinalIgnoreCase)) return American;
        if (!tag.StartsWith("en-", StringComparison.OrdinalIgnoreCase)) return null;
        tag = tag[3..];
        while (!tag.IsEmpty)
        {
            int separator = tag.IndexOf('-');
            var subtag = separator < 0 ? tag : tag[..separator];
            if (subtag.Length == 1) break; // BCP 47 extension or private-use suffix.
            if (subtag.Length == 2)
                return subtag.Equals("GB", StringComparison.OrdinalIgnoreCase) ||
                       subtag.Equals("AU", StringComparison.OrdinalIgnoreCase) ||
                       subtag.Equals("NZ", StringComparison.OrdinalIgnoreCase) ||
                       subtag.Equals("IE", StringComparison.OrdinalIgnoreCase) ||
                       subtag.Equals("ZA", StringComparison.OrdinalIgnoreCase) ||
                       subtag.Equals("IN", StringComparison.OrdinalIgnoreCase) ? British : American;
            if (separator < 0) break;
            tag = tag[(separator + 1)..];
        }
        return American;
    }

    private static bool IsWordContinuation(ReadOnlySpan<char> text, int index, int direction)
    {
        if ((uint)index >= (uint)text.Length) return false;
        char adjacent = text[index];
        if (char.IsLetterOrDigit(adjacent) || adjacent is '_' or '@' ||
            char.GetUnicodeCategory(adjacent) is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) return true;
        if (adjacent is not ('\'' or '’' or '.')) return false;
        int next = index + direction;
        return (uint)next < (uint)text.Length && char.IsLetterOrDigit(text[next]);
    }

    private sealed class PatternTable
    {
        private readonly Node root = new();
        private readonly Dictionary<string, int[]> exceptions = new(StringComparer.Ordinal);

        internal static PatternTable Load(string dialect)
        {
            using var input = typeof(EnglishHyphenation).Assembly
                .GetManifestResourceStream($"SeaReader.Hyphenation.hyph-en-{dialect}.tex")
                ?? throw new InvalidDataException("Missing English hyphenation patterns.");
            using var reader = new StreamReader(input);
            var content = new StringBuilder();
            while (reader.ReadLine() is { } line)
            {
                int comment = line.IndexOf('%');
                content.AppendLine(comment < 0 ? line : line[..comment]);
            }

            var source = content.ToString();
            var table = new PatternTable();
            var patterns = ReadBlock(source, "\\patterns");
            foreach (var range in patterns.SplitAny(" \t\r\n"))
            {
                var pattern = patterns[range];
                if (!pattern.IsEmpty) table.AddPattern(pattern);
            }
            var exceptions = ReadBlock(source, "\\hyphenation");
            foreach (var range in exceptions.SplitAny(" \t\r\n"))
            {
                var exception = exceptions[range];
                if (!exception.IsEmpty) table.AddException(exception);
            }
            if (table.root.Children == null) throw new InvalidDataException("Empty English hyphenation patterns.");
            return table;
        }

        private static ReadOnlySpan<char> ReadBlock(string source, string command)
        {
            int start = source.IndexOf(command, StringComparison.Ordinal);
            if (start < 0) return [];
            var remainder = source.AsSpan(start + command.Length).TrimStart();
            int end = remainder.IndexOf('}');
            return end > 0 && remainder[0] == '{' ? remainder[1..end] : [];
        }

        private void AddPattern(ReadOnlySpan<char> pattern)
        {
            Span<byte> weights = stackalloc byte[pattern.Length + 1];
            weights.Clear();
            var node = root;
            int letters = 0;
            foreach (char value in pattern)
            {
                if (char.IsAsciiDigit(value))
                {
                    weights[letters] = (byte)(value - '0');
                    continue;
                }
                var children = node.Children ??= new();
                if (!children.TryGetValue(value, out var child))
                {
                    child = new Node();
                    children.Add(value, child);
                }
                node = child;
                letters++;
            }
            if (node.Weights == null) node.Weights = weights[..(letters + 1)].ToArray();
            else for (int i = 0; i <= letters; i++) node.Weights[i] = Math.Max(node.Weights[i], weights[i]);
        }

        private void AddException(ReadOnlySpan<char> exception)
        {
            Span<char> word = stackalloc char[exception.Length];
            var offsets = new List<int>();
            int length = 0;
            foreach (char value in exception)
            {
                if (value == '-') offsets.Add(length);
                else word[length++] = value;
            }
            exceptions[word[..length].ToString()] = offsets.Distinct().ToArray();
        }

        internal void AppendBreaks(ReadOnlySpan<char> word, int start, ref List<int>? breaks)
        {
            Span<char> wrapped = stackalloc char[word.Length + 2];
            wrapped[0] = wrapped[^1] = '.';
            for (int i = 0; i < word.Length; i++) wrapped[i + 1] = char.ToLowerInvariant(word[i]);

            if (exceptions.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(wrapped[1..^1], out var overrides))
            {
                foreach (int offset in overrides)
                    if (offset >= LeftMinimum && offset <= word.Length - RightMinimum)
                        (breaks ??= []).Add(start + offset);
                return;
            }

            // The maximum score at a boundary wins. Odd scores allow a break; even scores veto one.
            Span<byte> scores = stackalloc byte[wrapped.Length + 1];
            scores.Clear();
            for (int first = 0; first < wrapped.Length; first++)
            {
                var node = root;
                for (int last = first; last < wrapped.Length; last++)
                {
                    if (node.Children == null || !node.Children.TryGetValue(wrapped[last], out var child)) break;
                    node = child;
                    if (node.Weights is not { } weights) continue;
                    for (int i = 0; i < weights.Length; i++)
                        scores[first + i] = Math.Max(scores[first + i], weights[i]);
                }
            }
            for (int offset = LeftMinimum; offset <= word.Length - RightMinimum; offset++)
                if ((scores[offset + 1] & 1) != 0) (breaks ??= []).Add(start + offset);
        }

        private sealed class Node
        {
            internal Dictionary<char, Node>? Children;
            internal byte[]? Weights;
        }
    }
}
