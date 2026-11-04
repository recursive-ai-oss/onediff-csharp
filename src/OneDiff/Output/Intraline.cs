using System.Text;
using OneDiff.Tokenization;

namespace OneDiff.Output;

/// <summary>
/// Finds which characters inside a block of changed lines changed, by diffing the block's old and new lines word by word.
/// Used to highlight within a line; it never changes which lines are shown.
/// </summary>
internal static class Intraline
{
    // Beyond this many chars per side the word diff costs more than a highlight is worth; the lines are shown whole instead.
    private const int MaxChars = 8192;

    // When less than this share of the longer side survives, the lines are rewrites and marking everything would be noise.
    private const double MinSharedShare = 0.3;

    /// <summary>Marked char ranges per line (line-relative, ascending, non-overlapping), or false when nothing should be marked.</summary>
    public static bool TryMark(List<string> oldLines, List<string> newLines, out List<(int Start, int End)>[] oldMarks, out List<(int Start, int End)>[] newMarks)
    {
        oldMarks = [];
        newMarks = [];
        string oldJoined = string.Join('\n', oldLines);
        string newJoined = string.Join('\n', newLines);
        if (oldJoined.Length > MaxChars || newJoined.Length > MaxChars)
        {
            return false;
        }

        TextDiff words = Diff.Words(oldJoined, newJoined);
        int shared = 0;
        foreach (Edit e in words.Script.Edits)
        {
            if (e.Kind == EditKind.Equal)
            {
                foreach (char c in words.OldSpan(e))
                {
                    if (!char.IsWhiteSpace(c))
                    {
                        shared++;
                    }
                }
            }
        }

        int longer = Math.Max(oldJoined.Length, newJoined.Length);
        if (shared == 0 || shared < MinSharedShare * longer)
        {
            return false;
        }

        oldMarks = Distribute(oldLines, words, isOld: true);
        newMarks = Distribute(newLines, words, isOld: false);
        return true;
    }

    private static List<(int Start, int End)>[] Distribute(List<string> lines, TextDiff words, bool isOld)
    {
        var result = new List<(int Start, int End)>[lines.Count];
        var starts = new int[lines.Count];
        int offset = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            result[i] = [];
            starts[i] = offset;
            offset += lines[i].Length + 1;
        }

        int line = 0;
        foreach (Edit e in words.Script.Edits)
        {
            if (e.Kind == (isOld ? EditKind.Insert : EditKind.Delete) || e.Kind == EditKind.Equal)
            {
                continue;
            }

            ReadOnlySpan<TokenRange> tokens = isOld ? words.OldTokens : words.NewTokens;
            int from = tokens[isOld ? e.OldStart : e.NewStart].Start;
            int to = tokens[(isOld ? e.OldEnd : e.NewEnd) - 1].End;
            while (line > 0 && starts[line] > from)
            {
                line--;
            }

            for (int l = line; l < lines.Count && starts[l] < to; l++)
            {
                int s = Math.Max(from, starts[l]) - starts[l];
                int t = Math.Min(to, starts[l] + lines[l].Length) - starts[l];
                if (t > s)
                {
                    List<(int Start, int End)> marks = result[l];
                    if (marks.Count > 0 && marks[^1].End >= s)
                    {
                        marks[^1] = (marks[^1].Start, Math.Max(marks[^1].End, t));
                    }
                    else
                    {
                        marks.Add((s, t));
                    }
                }

                line = l;
            }
        }

        return result;
    }

    /// <summary>Appends text with control characters shown as their visible pictures so diffed content cannot drive the terminal.</summary>
    public static void AppendSafe(StringBuilder sb, ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c < ' ' && c != '\t')
            {
                sb.Append((char)(0x2400 + c));
            }
            else if (c == '\u007f')
            {
                sb.Append('\u2421');
            }
            else if (c >= '\u0080' && c <= '\u009f')
            {
                sb.Append('\uFFFD');
            }
            else
            {
                sb.Append(c);
            }
        }
    }
}
