using System.Collections.Immutable;
using System.Text;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.InPair;

/// <summary>
/// Formatting differences of text that is equal on both sides, found as attribute intervals: the text is cut where either side's
/// formatting changes, each piece is labelled with exactly what changed (bold off to on, font A to B), and neighbouring pieces
/// with the same label become one edit. Bold added on one phrase and italics added on the next are two edits, not one.
/// </summary>
internal static class FormatIntervals
{
    /// <summary>Adds format-only edits for <paramref name="length"/> equal characters starting at <paramref name="oldStart"/> and <paramref name="newStart"/>.</summary>
    public static void AddEdits(ProjectedGroup og, ProjectedGroup ng, int oldStart, int newStart, int length, ImmutableArray<RegionEdit>.Builder edits)
    {
        if (length == 0 || (og.Runs.IsEmpty && ng.Runs.IsEmpty))
        {
            return;
        }

        var cuts = new List<int> { 0, length };
        AddCuts(og.Runs, oldStart, length, cuts);
        AddCuts(ng.Runs, newStart, length, cuts);
        cuts.Sort();

        int ca = FirstEndingAfter(og.Runs, oldStart), cb = FirstEndingAfter(ng.Runs, newStart);
        string? open = null;
        int openStart = 0;
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            int from = cuts[i], to = cuts[i + 1];
            if (to == from)
            {
                continue;
            }

            string? label = Label(FormatRuns.FormatAt(og.Runs, ref ca, oldStart + from), FormatRuns.FormatAt(ng.Runs, ref cb, newStart + from));
            if (!string.Equals(label, open, StringComparison.Ordinal))
            {
                if (open is not null)
                {
                    Emit(og, ng, oldStart, newStart, openStart, from - openStart, edits);
                }

                open = label;
                openStart = from;
            }
        }

        if (open is not null)
        {
            Emit(og, ng, oldStart, newStart, openStart, length - openStart, edits);
        }
    }

    /// <summary>What differs between two formats as text, or null when they are the same. Equal labels mean the same change.</summary>
    public static string? Label(FormatSet a, FormatSet b)
    {
        if (FormatSet.ContentEquals(a, b))
        {
            return null;
        }

        var keys = new List<string>();
        foreach (KeyValuePair<string, string> kv in a.Entries)
        {
            keys.Add(kv.Key);
        }

        foreach (KeyValuePair<string, string> kv in b.Entries)
        {
            if (a.Get(kv.Key) is null)
            {
                keys.Add(kv.Key);
            }
        }

        keys.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (string k in keys)
        {
            string? x = a.Get(k), y = b.Get(k);
            if (!string.Equals(x, y, StringComparison.Ordinal))
            {
                sb.Append(k).Append('=').Append(x ?? "\u0001").Append('>').Append(y ?? "\u0001").Append('\u001F');
            }
        }

        return sb.ToString();
    }

    private static void Emit(ProjectedGroup og, ProjectedGroup ng, int oldStart, int newStart, int offset, int len, ImmutableArray<RegionEdit>.Builder edits)
    {
        string text = og.Text.Substring(oldStart + offset, len);
        edits.Add(new RegionEdit(
            oldStart + offset,
            len,
            newStart + offset,
            len,
            text,
            text,
            FormatRuns.Slice(og.Runs, oldStart + offset, len),
            FormatRuns.Slice(ng.Runs, newStart + offset, len)));
    }

    /// <summary>Adds the run boundaries inside (start, start+length) as offsets from start; the runs are sorted and disjoint.</summary>
    private static void AddCuts(ImmutableArray<FormatRun> runs, int start, int length, List<int> cuts)
    {
        int lo = FirstEndingAfter(runs, start);

        for (int i = lo; i < runs.Length && runs[i].Start < start + length; i++)
        {
            int s = runs[i].Start - start, e = runs[i].End - start;
            if (s > 0 && s < length)
            {
                cuts.Add(s);
            }

            if (e > 0 && e < length)
            {
                cuts.Add(e);
            }
        }
    }

    /// <summary>Index of the first run that ends after <paramref name="start"/> (binary search; the runs are sorted and disjoint).</summary>
    private static int FirstEndingAfter(ImmutableArray<FormatRun> runs, int start)
    {
        int lo = 0, hi = runs.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (runs[mid].End <= start)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
