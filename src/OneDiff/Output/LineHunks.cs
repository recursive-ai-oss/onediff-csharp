using System.Globalization;
using OneDiff.Tokenization;

namespace OneDiff.Output;

/// <summary>One line of a rendered hunk: how it changed and where it sits in each text.</summary>
/// <param name="Kind">Equal (context), Delete (old only) or Insert (new only).</param>
/// <param name="OldIndex">The line's index in the old text; for an insertion, the index of the old line it precedes.</param>
/// <param name="NewIndex">The line's index in the new text; for a deletion, the index of the new line it precedes.</param>
internal readonly record struct HunkLine(EditKind Kind, int OldIndex, int NewIndex);

/// <summary>A run of changed lines with its surrounding context, as the hunks of a unified diff are cut.</summary>
internal sealed class LineHunk
{
    public LineHunk(List<HunkLine> lines)
    {
        Lines = lines;
        foreach (HunkLine l in lines)
        {
            if (l.Kind != EditKind.Insert)
            {
                OldCount++;
            }

            if (l.Kind != EditKind.Delete)
            {
                NewCount++;
            }
        }

        OldStart = lines[0].OldIndex;
        NewStart = lines[0].NewIndex;
    }

    public List<HunkLine> Lines { get; }

    /// <summary>Zero-based index of the first old line (or the line the hunk sits before when it has no old lines).</summary>
    public int OldStart { get; }

    public int OldCount { get; }

    public int NewStart { get; }

    public int NewCount { get; }

    /// <summary>Formats the range as <c>@@ -a,b +c,d @@</c>; a count of one is left out and an empty range names the line before it.</summary>
    public string Header()
        => "@@ -" + Range(OldStart, OldCount) + " +" + Range(NewStart, NewCount) + " @@";

    private static string Range(int start, int count)
    {
        int first = count == 0 ? start : start + 1;
        string s = first.ToString(CultureInfo.InvariantCulture);
        return count == 1 ? s : s + "," + count.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Cuts a line-token diff into hunks and reads line text back out of it.</summary>
internal static class LineHunks
{
    public static void RequireLines(TextDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (diff.Kind != TokenKind.Line)
        {
            throw new ArgumentException("A line rendering needs a diff made with line tokens (Diff.Lines).", nameof(diff));
        }
    }

    /// <summary>
    /// Groups changes with <paramref name="context"/> equal lines around them. Two changes whose gap is at most twice the context
    /// share a hunk, so no line is shown twice.
    /// </summary>
    public static List<LineHunk> Build(TextDiff diff, int context)
    {
        var ops = new List<HunkLine>();
        foreach (Edit e in diff.Script.Edits)
        {
            switch (e.Kind)
            {
                case EditKind.Equal:
                    for (int k = 0; k < e.OldLength; k++)
                    {
                        ops.Add(new HunkLine(EditKind.Equal, e.OldStart + k, e.NewStart + k));
                    }

                    break;
                case EditKind.Delete:
                    for (int k = 0; k < e.OldLength; k++)
                    {
                        ops.Add(new HunkLine(EditKind.Delete, e.OldStart + k, e.NewStart));
                    }

                    break;
                default:
                    for (int k = 0; k < e.NewLength; k++)
                    {
                        ops.Add(new HunkLine(EditKind.Insert, e.OldStart, e.NewStart + k));
                    }

                    break;
            }
        }

        var hunks = new List<LineHunk>();
        int i = 0;
        while (true)
        {
            int change = NextChange(ops, i);
            if (change < 0)
            {
                break;
            }

            int start = (int)Math.Max((long)change - context, i);
            int end;
            while (true)
            {
                int runEnd = change;
                while (runEnd < ops.Count && ops[runEnd].Kind != EditKind.Equal)
                {
                    runEnd++;
                }

                int next = NextChange(ops, runEnd);
                if (next < 0 || next - runEnd > 2L * context)
                {
                    end = (int)Math.Min((long)runEnd + context, ops.Count);
                    break;
                }

                change = next;
            }

            hunks.Add(new LineHunk(ops.GetRange(start, end - start)));
            i = end;
        }

        return hunks;
    }

    private static int NextChange(List<HunkLine> ops, int from)
    {
        for (int i = from; i < ops.Count; i++)
        {
            if (ops[i].Kind != EditKind.Equal)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The text of old line <paramref name="index"/> without its terminator; <paramref name="terminated"/> says whether it had one.</summary>
    public static ReadOnlySpan<char> OldLine(TextDiff diff, int index, out bool terminated)
        => Content(diff.OldText, diff.OldTokens[index], out terminated);

    /// <summary>The text of new line <paramref name="index"/> without its terminator.</summary>
    public static ReadOnlySpan<char> NewLine(TextDiff diff, int index, out bool terminated)
        => Content(diff.NewText, diff.NewTokens[index], out terminated);

    private static ReadOnlySpan<char> Content(string text, TokenRange token, out bool terminated)
    {
        ReadOnlySpan<char> line = text.AsSpan(token.Start, token.Length);
        int cut = 0;
        if (line.EndsWith("\r\n", StringComparison.Ordinal))
        {
            cut = 2;
        }
        else if (line.Length > 0 && (line[^1] == '\n' || line[^1] == '\r'))
        {
            cut = 1;
        }

        terminated = cut > 0;
        return line.Slice(0, line.Length - cut);
    }
}
