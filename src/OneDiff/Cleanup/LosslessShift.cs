using System.Buffers;
using System.Globalization;

namespace OneDiff.Cleanup;

/// <summary>
/// Slides a lone insertion or deletion along the equal text around it, to the place where it reads best. Sliding is lossless: the
/// edit keeps its tokens' text and the script keeps its cost. Where the same change can be drawn several ways (an inserted "cat " in
/// "the cat sat" can start inside "the" or after it), the way that starts and ends on word, line or blank-line boundaries wins.
/// </summary>
internal static class LosslessShift
{
    public static EditScript Apply(TextDiff diff, TokenText oldText, TokenText newText, TokenEquality equality, bool scoreMoves)
    {
        EditScript script = diff.Script;
        int n = script.Count;
        if (n < 3)
        {
            return script;
        }

        // Mutable copy of the run lengths (kind, old length, new length), in pooled buffers: most scripts need no change at all.
        EditKind[] kinds = ArrayPool<EditKind>.Shared.Rent(n);
        int[] oldLengths = ArrayPool<int>.Shared.Rent(n);
        int[] newLengths = ArrayPool<int>.Shared.Rent(n);
        try
        {
            return Slide(script, n, kinds, oldLengths, newLengths, oldText, newText, equality, scoreMoves);
        }
        finally
        {
            ArrayPool<EditKind>.Shared.Return(kinds);
            ArrayPool<int>.Shared.Return(oldLengths);
            ArrayPool<int>.Shared.Return(newLengths);
        }
    }

    private static EditScript Slide(EditScript script, int n, EditKind[] kinds, int[] oldLengths, int[] newLengths, TokenText oldText, TokenText newText, TokenEquality equality, bool scoreMoves)
    {
        for (int i = 0; i < n; i++)
        {
            kinds[i] = script[i].Kind;
            oldLengths[i] = script[i].OldLength;
            newLengths[i] = script[i].NewLength;
        }

        bool moved = false;
        int oldPos = 0, newPos = 0;
        for (int i = 0; i < n; i++)
        {
            if (kinds[i] != EditKind.Equal && i > 0 && i + 1 < n && kinds[i - 1] == EditKind.Equal && kinds[i + 1] == EditKind.Equal
                && oldLengths[i - 1] > 0 && oldLengths[i + 1] > 0)
            {
                bool isDelete = kinds[i] == EditKind.Delete;
                TokenText side = isDelete ? oldText : newText;
                int start = isDelete ? oldPos : newPos;
                int length = isDelete ? oldLengths[i] : newLengths[i];
                // When another change lies beyond a neighbouring equal run, the edit may slide across the whole run and join that change.
                bool mergeLeft = i >= 2;
                bool mergeRight = i + 2 < n;
                int shift = BestShift(side, start, length, oldLengths[i - 1], mergeLeft, oldLengths[i + 1], mergeRight, equality, scoreMoves);
                if (shift != 0)
                {
                    // The equal run before gets shorter by `shift` and the one after longer, in both sequences alike.
                    oldLengths[i - 1] += shift;
                    newLengths[i - 1] += shift;
                    oldLengths[i + 1] -= shift;
                    newLengths[i + 1] -= shift;
                    oldPos += shift;
                    newPos += shift;
                    moved = true;
                }
            }

            oldPos += oldLengths[i];
            newPos += newLengths[i];
        }

        if (!moved)
        {
            return script;
        }

        var builder = new EditScriptBuilder();
        for (int i = 0; i < n; i++)
        {
            switch (kinds[i])
            {
                case EditKind.Equal: builder.Equal(oldLengths[i]); break;
                case EditKind.Delete: builder.Delete(oldLengths[i]); break;
                default: builder.Insert(newLengths[i]); break;
            }
        }

        return builder.ToEditScript();
    }

    // The shift (negative = left) of the tokens [start, start+length). The edit may slide left while the token before it equals the edit's
    // last token, and right while the token after it equals the edit's first. Sliding all the way across an equal run (only when another
    // change lies beyond it) makes the edit touch that change: same cost, one region fewer, so it wins. Otherwise one token of each
    // neighbouring equal run stays, and, when scoring is on, the position with the best boundaries wins; ties go to the leftmost.
    private static int BestShift(TokenText side, int start, int length, int equalLeft, bool mergeLeft, int equalRight, bool mergeRight, TokenEquality equality, bool scoreMoves)
    {
        int roomLeft = mergeLeft ? equalLeft : equalLeft - 1;
        int left = 0;
        while (left < roomLeft && equality.Equal(side.Token(start - 1 - left), side.Token(start + length - 1 - left)))
        {
            left++;
        }

        if (mergeLeft && left == equalLeft)
        {
            return -left;
        }

        int roomRight = mergeRight ? equalRight : equalRight - 1;
        int right = 0;
        while (right < roomRight && equality.Equal(side.Token(start + right), side.Token(start + length + right)))
        {
            right++;
        }

        if (mergeRight && right == equalRight)
        {
            return right;
        }

        if (!scoreMoves || (left == 0 && right == 0))
        {
            return 0;
        }

        // A partial slide must leave one token of each equal run.
        left = Math.Min(left, equalLeft - 1);
        right = Math.Min(right, equalRight - 1);
        int best = 0;
        int bestScore = int.MinValue;
        for (int shift = -left; shift <= right; shift++)
        {
            int score = BoundaryScore.At(side.Text, side.Offset(start + shift)) + BoundaryScore.At(side.Text, side.Offset(start + shift + length));
            if (score > bestScore)
            {
                bestScore = score;
                best = shift;
            }
        }

        return best;
    }
}

/// <summary>
/// How good a place to start or end a change is, scored from the characters either side of a text offset. Higher is a more natural
/// break. The scale is OneDiff's own (the dmp documentation only says edits should fall on word and line boundaries):
/// 6 at the very start or end of the text; 5 between a blank line and its neighbour; 4 at the start of a line;
/// 3 at the end of a line's text or at the start of a sentence; 2 between a space and a word; 1 inside a space run or beside punctuation;
/// 0 inside a word, between a character and its combining mark, or inside a CR LF pair.
/// </summary>
internal static class BoundaryScore
{
    public static int At(string text, int offset)
    {
        if (offset <= 0 || offset >= text.Length)
        {
            return 6;
        }

        char a = text[offset - 1];
        char b = text[offset];
        if (a == '\r' && b == '\n')
        {
            return 0;
        }

        if (char.IsLowSurrogate(b) && char.IsHighSurrogate(a))
        {
            return 0;
        }

        if (IsCombining(b) || a == '\u200D' || b == '\u200D')
        {
            return 0;
        }

        bool aBreak = a is '\n' or '\r';
        bool bBreak = b is '\n' or '\r';
        if (aBreak)
        {
            // A blank line sits on either side: the previous line is empty, or this one is.
            int terminator = a == '\n' && offset >= 2 && text[offset - 2] == '\r' ? offset - 2 : offset - 1;
            bool previousBlank = terminator >= 1 && text[terminator - 1] is '\n' or '\r';
            return bBreak || previousBlank ? 5 : 4;
        }

        if (bBreak)
        {
            return 3;
        }

        bool aSpace = char.IsWhiteSpace(a);
        bool bSpace = char.IsWhiteSpace(b);
        if (aSpace && bSpace)
        {
            return 1;
        }

        if (aSpace)
        {
            return EndsSentence(text, offset - 1) ? 3 : 2;
        }

        if (bSpace)
        {
            return 2;
        }

        return char.IsLetterOrDigit(a) && char.IsLetterOrDigit(b) ? 0 : 1;
    }

    private static bool IsCombining(char c)
    {
        if (c < 0x300)
        {
            return false;
        }

        UnicodeCategory category = char.GetUnicodeCategory(c);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    // True when the white space ending at `index` follows a full stop, exclamation or question mark.
    private static bool EndsSentence(string text, int index)
    {
        while (index >= 0 && char.IsWhiteSpace(text[index]))
        {
            index--;
        }

        return index >= 0 && text[index] is '.' or '!' or '?';
    }
}
