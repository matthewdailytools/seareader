using System.Xml;

namespace SeaReader.Native;

internal sealed class EpubLegacyAnchor(EpubLegacyCfi cfi)
{
    // Only the ancestors on the requested path need child counts.
    private readonly ChildCounts[] children = new ChildCounts[cfi.Steps.Length + 1];
    private int matchedDepth = -1;
    private bool atTargetElement;
    private bool atTargetText;
    private bool foundBoundary;

    internal int? SourceOffset { get; private set; }

    internal void Observe(XmlReader reader)
    {
        if (foundBoundary) return;
        atTargetElement = false;
        atTargetText = false;
        int depth = reader.Depth;
        if (reader.NodeType == XmlNodeType.Element)
        {
            if (depth == 0)
            {
                matchedDepth = 0;
                children[0] = default;
                return;
            }
            if (depth != matchedDepth + 1 || depth > cfi.Steps.Length) return;

            ref var parent = ref children[matchedDepth];
            var step = cfi.Steps[depth - 1];
            if (parent.Elements++ != step.Index ||
                step.Id is { } id && reader.GetAttribute("id") != id &&
                reader.GetAttribute("id", "http://www.w3.org/XML/1998/namespace") != id)
                return;

            atTargetElement = depth == cfi.Steps.Length;
            if (reader.IsEmptyElement) return;
            matchedDepth = depth;
            children[depth] = default;
        }
        else if (reader.NodeType == XmlNodeType.EndElement && depth == matchedDepth)
        {
            matchedDepth--;
        }
        else if (cfi.TextNodeIndex is { } target && matchedDepth == cfi.Steps.Length &&
            depth == matchedDepth + 1 &&
            reader.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
        {
            // epub.js indexes Node.TEXT_NODE children separately from elements and CDATA.
            atTargetText = children[matchedDepth].Texts++ == target;
        }
    }

    internal int? TakeCharacterBoundary(string value)
    {
        if (foundBoundary) return null;
        int offset;
        if (cfi.TextNodeIndex.HasValue)
        {
            if (!atTargetText) return null;
            offset = cfi.CharacterOffset.GetValueOrDefault();
            if (offset > value.Length) return null;
            if (offset > 0 && offset < value.Length &&
                char.IsLowSurrogate(value[offset]) && char.IsHighSurrogate(value[offset - 1]))
                offset--;
        }
        else
        {
            if (!atTargetElement && matchedDepth != cfi.Steps.Length || value.AsSpan().IsWhiteSpace()) return null;
            offset = 0;
        }
        foundBoundary = true;
        return offset;
    }

    internal void Resolve(int offset) => SourceOffset = offset;

    internal void Reset()
    {
        matchedDepth = -1;
        atTargetElement = false;
        atTargetText = false;
        foundBoundary = false;
        SourceOffset = null;
    }

    private struct ChildCounts
    {
        public int Elements;
        public int Texts;
    }
}
