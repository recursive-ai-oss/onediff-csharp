using DiffPlex;
using DiffMatchPatch;
using DmpDiff = DiffMatchPatch.Diff;
using OneDiff.Tokenization;

namespace OneDiff.Bench;

/// <summary>
/// One method per implementation per row, shared by the BenchmarkDotNet classes and the peak-memory runner so both time the
/// same work. Each returns the edit distance it found (inserted plus deleted tokens) so the result is used and the workloads can be
/// compared for like-for-like output.
/// </summary>
/// <remarks>
/// Settings, chosen from the comparators' published documentation:
/// <list type="bullet">
/// <item>No cleanup anywhere in the diff rows: every implementation returns its raw edit script.</item>
/// <item>diff-match-patch: <c>Diff_Timeout = 0</c> ("0 for infinity" in its API description), so it computes the full diff
/// instead of giving up after a second; <c>checklines</c> false so the character rows are a plain character diff. Its line mode
/// is the documented line-to-characters approach: map each distinct line to one character, diff those, map back.</item>
/// <item>DiffPlex: its line, word and character differs with whitespace and case significant (<c>ignoreWhitespace: false</c>), and
/// the word differ is given an explicit separator set (spaces, line ends and common punctuation). Its words are not ours, so the
/// word rows do not see identical token streams;
/// the edit distances printed by <c>info</c> show how far apart the workloads are.</item>
/// </list>
/// Everything runs on one thread. A new comparator or option goes here, in one place.
/// </remarks>
internal static class Impl
{
    public static int OursText(string a, string b, TokenKind kind, DiffAlgorithm algorithm)
        => Diff.Text(a, b, kind, algorithm).Script.EditDistance;

    public static int DiffPlexLines(string a, string b) => Distance(new Differ().CreateLineDiffs(a, b, false));

    public static int DiffPlexWords(string a, string b) => Distance(new Differ().CreateWordDiffs(a, b, false, WordSeparators));

    public static int DiffPlexChars(string a, string b) => Distance(new Differ().CreateCharacterDiffs(a, b, false));

    /// <summary>The separators handed to DiffPlex's word differ: space, line ends and common punctuation.</summary>
    private static readonly char[] WordSeparators = [' ', '\r', '\n', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '!', '?'];

    private static int Distance(DiffPlex.Model.DiffResult result)
    {
        int distance = 0;
        foreach (DiffPlex.Model.DiffBlock block in result.DiffBlocks)
        {
            distance += block.DeleteCountA + block.InsertCountB;
        }

        return distance;
    }

    public static diff_match_patch NewDmp() => new() { Diff_Timeout = 0 };

    public static int DmpChars(diff_match_patch dmp, string a, string b) => Distance(dmp.diff_main(a, b, false));

    /// <summary>
    /// Line mode as the diff-match-patch documentation describes it: give every distinct line one character, diff the two
    /// character strings, and read the result in lines. The package keeps its own line mapping non-public, so the mapping is done here,
    /// inside the timed work like the comparator's own; mapping the diff back to lines is left out of both sides.
    /// </summary>
    public static int DmpLines(diff_match_patch dmp, string a, string b)
    {
        var ids = new Dictionary<string, char>(StringComparer.Ordinal);
        string x = LinesToChars(a, ids);
        string y = LinesToChars(b, ids);
        return Distance(dmp.diff_main(x, y, false));
    }

    private static string LinesToChars(string text, Dictionary<string, char> ids)
    {
        var chars = new char[CountLines(text)];
        int n = 0;
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            int end = nl < 0 ? text.Length : nl + 1;
            string line = text[start..end];
            if (!ids.TryGetValue(line, out char c))
            {
                int next = ids.Count + 1;
                if (next >= 0xD800)
                {
                    next += 0x800; // skip the surrogate block so no character is half a pair
                }

                if (next > 0xFFFF)
                {
                    throw new InvalidOperationException("more distinct lines than one UTF-16 character each can name");
                }

                c = (char)next;
                ids[line] = c;
            }

            chars[n++] = c;
            start = end;
        }

        return new string(chars, 0, n);
    }

    private static int CountLines(string text)
    {
        int n = 0;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                n++;
            }
        }

        return n + 1;
    }

    public static int Distance(List<DmpDiff> diffs)
    {
        int distance = 0;
        foreach (DmpDiff d in diffs)
        {
            if (d.operation != Operation.EQUAL)
            {
                distance += d.text.Length;
            }
        }

        return distance;
    }

    public static List<DmpDiff> Clone(List<DmpDiff> diffs)
    {
        var copy = new List<DmpDiff>(diffs.Count);
        foreach (DmpDiff d in diffs)
        {
            copy.Add(new DmpDiff(d.operation, d.text));
        }

        return copy;
    }
}
