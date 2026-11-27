using System.Globalization;
using System.Text;

namespace SeaReader.Native;

internal readonly record struct EpubLegacyCfiStep(int Index, string? Id);

internal sealed record EpubLegacyCfi(
    int SpineIndex,
    string? SpineId,
    EpubLegacyCfiStep[] Steps,
    int? TextNodeIndex,
    int? CharacterOffset)
{
    private const string Prefix = "epubcfi(";
    private const int MaxLength = 4096;
    private const int MaxSteps = 128;

    internal EpubLegacyCfiStep SpineElement { get; init; }

    internal static EpubLegacyCfi? Parse(string? position)
    {
        if (position is null || position.Length > MaxLength ||
            !position.StartsWith(Prefix, StringComparison.Ordinal) || position[^1] != ')')
            return null;

        var input = position.AsSpan(Prefix.Length, position.Length - Prefix.Length - 1);
        int cursor = 0;
        var steps = new List<EpubLegacyCfiStep>();
        if (!TryReadPath(input, ref cursor, steps, null, out int? textIndex, out _) ||
            steps.Count != 2 || textIndex.HasValue || !TryTake(input, ref cursor, '!'))
            return null;

        var spineElement = steps[0];
        var item = steps[1];
        steps.Clear();
        int pathStart = cursor;
        if (!TryReadPath(input, ref cursor, steps, null, out textIndex, out int? offset) ||
            cursor == pathStart)
            return null;

        if (TryTake(input, ref cursor, ','))
        {
            // A range shares a prefix. Resolve its start, but check both endpoints.
            if (offset.HasValue) return null;
            var endSteps = new List<EpubLegacyCfiStep>(steps);
            int? sharedTextIndex = textIndex;
            if (!TryReadPath(input, ref cursor, steps, sharedTextIndex, out textIndex, out offset) ||
                !TryTake(input, ref cursor, ',') ||
                !TryReadPath(input, ref cursor, endSteps, sharedTextIndex, out _, out _))
                return null;
        }

        return cursor == input.Length
            ? new EpubLegacyCfi(item.Index, item.Id, [.. steps], textIndex, offset) { SpineElement = spineElement }
            : null;
    }

    private static bool TryReadPath(ReadOnlySpan<char> input, ref int cursor,
        List<EpubLegacyCfiStep> steps, int? sharedTextIndex, out int? textIndex, out int? offset)
    {
        textIndex = sharedTextIndex;
        offset = null;

        // epub.js serializes every component with a slash, even if it has no steps.
        if (cursor < input.Length && input[cursor] == '/' &&
            (cursor + 1 == input.Length || input[cursor + 1] is ':' or ','))
            cursor++;

        while (TryTake(input, ref cursor, '/'))
        {
            if (textIndex.HasValue || steps.Count == MaxSteps ||
                !TryReadInteger(input, ref cursor, out int number) || number == 0)
                return false;

            bool isTextNode = number % 2 != 0;
            if (!TryReadAssertion(input, ref cursor, isTextAssertion: false, out var id)) return false;
            if (isTextNode)
            {
                if (id is not null) return false;
                // epub.js numbers text-only children separately from element children.
                textIndex = number / 2;
            }
            else
            {
                steps.Add(new EpubLegacyCfiStep(number / 2 - 1, id));
            }
        }

        if (!TryTake(input, ref cursor, ':')) return true;
        if (!textIndex.HasValue || !TryReadInteger(input, ref cursor, out int characterOffset) ||
            !TryReadAssertion(input, ref cursor, isTextAssertion: true, out _))
            return false;

        offset = characterOffset;
        return true;
    }

    private static bool TryReadInteger(ReadOnlySpan<char> input, ref int cursor, out int value)
    {
        int start = cursor;
        while (cursor < input.Length && char.IsAsciiDigit(input[cursor])) cursor++;
        return int.TryParse(input[start..cursor], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadAssertion(ReadOnlySpan<char> input, ref int cursor,
        bool isTextAssertion, out string? id)
    {
        id = null;
        if (!TryTake(input, ref cursor, '[')) return true;

        int valueStart = cursor;
        if (!TryReadAssertionValue(input, ref cursor)) return false;
        var value = input[valueStart..cursor];
        if (isTextAssertion && TryTake(input, ref cursor, ',') &&
            !TryReadAssertionValue(input, ref cursor))
            return false;

        // The stored offset is sufficient for epub.js positions. Consume side bias
        // without treating it as part of the ID or a range separator.
        if (TryTake(input, ref cursor, ';'))
        {
            if (!TryTake(input, ref cursor, 's') || !TryTake(input, ref cursor, '=') ||
                (!TryTake(input, ref cursor, 'a') && !TryTake(input, ref cursor, 'b')))
                return false;
        }

        if (!TryTake(input, ref cursor, ']')) return false;
        if (!isTextAssertion && !value.IsEmpty) id = DecodeAssertion(value);
        return true;
    }

    private static bool TryReadAssertionValue(ReadOnlySpan<char> input, ref int cursor)
    {
        while (cursor < input.Length)
        {
            char character = input[cursor];
            if (character is ']' or ',' or ';') return true;
            if (character is '[' or '(' or ')' or '=' || char.IsControl(character)) return false;
            cursor++;
            if (character != '^') continue;
            if (cursor == input.Length || input[cursor] is not ('^' or '[' or ']' or '(' or ')' or ',' or ';' or '='))
                return false;
            cursor++;
        }

        return false;
    }

    private static string DecodeAssertion(ReadOnlySpan<char> value)
    {
        if (!value.Contains('^')) return value.ToString();
        var decoded = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '^') i++;
            decoded.Append(value[i]);
        }

        return decoded.ToString();
    }

    private static bool TryTake(ReadOnlySpan<char> input, ref int cursor, char expected)
    {
        if (cursor == input.Length || input[cursor] != expected) return false;
        cursor++;
        return true;
    }
}
