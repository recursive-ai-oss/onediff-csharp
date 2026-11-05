using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Word.Internal;

/// <summary>
/// Maps a position in a node's compared text back to the paragraph's current-text offset that RevisionAuthor and the segment anchors use
/// (docs/word.md, "Anchors"). A change's offsets are in the text the comparison projected: the node's text with every fold whose ledger
/// category is not hidden undone. The node text is that text with all folds applied, and the segment anchors name stretches of the
/// original (raw) characters, so a position goes projected → raw → segment → paragraph offset.
/// </summary>
internal static class TextPositions
{
    /// <summary>
    /// The raw index of a position of the projected text. the settings' hidden categories are the folds that stay applied in
    /// the projected text; <paramref name="projected"/> false means the position is already in node text (every fold applied).
    /// A position strictly inside a fold snaps to its start for a range start and to its end for a range end.
    /// </summary>
    public static int ToRaw(Node node, CompareSettings settings, bool projected, int position, bool isStart)
    {
        int nodePos = 0, projPos = 0, rawPos = 0;
        foreach (NormalizationFold f in node.Folds)
        {
            int gap = f.Start - nodePos;
            if (position <= projPos + gap)
            {
                return rawPos + (position - projPos);
            }

            projPos += gap;
            rawPos += gap;
            bool stays = !projected || settings.Hidden.Contains(f.Category);
            int projLen = stays ? f.Length : f.Original.Length;
            if (position < projPos + projLen || (position == projPos + projLen && projLen == 0))
            {
                if (!stays)
                {
                    return rawPos + (position - projPos);
                }

                return isStart ? rawPos : rawPos + f.Original.Length;
            }

            projPos += projLen;
            rawPos += f.Original.Length;
            nodePos = f.Start + f.Length;
        }

        return rawPos + (position - projPos);
    }

    /// <summary>
    /// The paragraph offset of a raw position of the node's text. Each segment gives a piece: where its text starts in the raw text
    /// and where it starts in the paragraph. Raw characters that a fold swallowed from a neighbouring run (a tab after a space, say)
    /// have no segment of their own, and run on from the piece before them, one paragraph character for each raw character.
    /// Returns false when the position falls inside an object placeholder or nothing holds it.
    /// </summary>
    public static bool TryHubOffset(Node node, CompareSettings settings, int rawPosition, bool isStart, out int hub)
    {
        hub = 0;
        if (node.Segments.IsDefaultOrEmpty)
        {
            return node.Text.Length == 0 && rawPosition == 0;
        }

        int chosen = -1, chosenRaw = 0, chosenOffset = 0, chosenLength = 0;
        bool chosenPlaceholder = false;
        int previousEnd = 0;
        for (int i = 0; i < node.Segments.Length; i++)
        {
            AnchoredSpan s = node.Segments[i];
            int rs = ToRaw(node, settings, false, s.Start, true);
            if (i > 0 && rs < previousEnd)
            {
                // A fold that merged characters of two runs (a space and the tab after it) puts the later run's segment at the start
                // of the fold, but the earlier run's anchor already covers the characters up to there.
                rs = previousEnd;
            }

            if (isStart ? rs > rawPosition : rs >= rawPosition)
            {
                break;
            }

            if (!WordAnchors.TryParse(s.Anchor.Reference, out _, out _, out int offset, out int length) || offset < 0)
            {
                return false;
            }

            // An object's placeholder is one character of the node text however much text the object holds.
            previousEnd = rs + (s.Length == 1 && node.Text[s.Start] == '\uFFFC' ? 1 : length);

            chosen = i;
            chosenRaw = rs;
            chosenOffset = offset;
            chosenLength = length;
            chosenPlaceholder = s.Length == 1 && node.Text[s.Start] == '\uFFFC';
        }

        if (chosen < 0)
        {
            return false;
        }

        int delta = rawPosition - chosenRaw;
        if (chosenPlaceholder)
        {
            if (delta > 1)
            {
                return false;
            }

            hub = chosenOffset + (delta == 0 ? 0 : chosenLength);
            return true;
        }

        hub = chosenOffset + delta;
        return true;
    }

    /// <summary>The paragraph range of a stretch [start, end) of the projected text.</summary>
    public static bool TryHubRange(Node node, CompareSettings settings, int start, int end, out int hubStart, out int hubLength)
    {
        hubStart = 0;
        hubLength = 0;
        if (end < start)
        {
            return false;
        }

        int rs = ToRaw(node, settings, true, start, true);
        int re = ToRaw(node, settings, true, end, false);
        if (start == end)
        {
            // An insertion point: take it at the end of the text before it when there is some, else at the start of the text after it.
            if (TryHubOffset(node, settings, rs, false, out int p) || TryHubOffset(node, settings, rs, true, out p))
            {
                hubStart = p;
                return true;
            }

            return false;
        }

        if (!TryHubOffset(node, settings, rs, true, out int a) || !TryHubOffset(node, settings, re, false, out int b) || b < a)
        {
            return false;
        }

        hubStart = a;
        hubLength = b - a;
        return true;
    }
}
