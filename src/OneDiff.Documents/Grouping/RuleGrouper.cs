using OneDiff.Documents.InPair;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneFont;

namespace OneDiff.Documents.Grouping;

/// <summary>
/// Rule grouping: turns the atomic edits of a link into change units that read like a redline. The edits stay as they are, in the
/// unit that holds them; a unit only decides what range each side shows and which edits belong together.
/// <list type="number">
/// <item>A value (date, amount, percentage, duration, number) that an edit touches is shown whole on both sides.</item>
/// <item>Edits never join across a sentence end (UAX #29 section 5) or a clause break (";" and ":" before white space, a line break).</item>
/// <item>Within a clause, edits join when the unchanged words between them are few, and all of a clause's edits join, widened to the
/// whole clause, when enough of it changed.</item>
/// <item>Formatting differences become format units: one per attribute label, neighbours with the same label joined across white
/// space; a formatting difference that touches a text unit is part of that unit.</item>
/// </list>
/// Every edit of the link lands in exactly one unit, so the units partition the link's edits and replay exactly as the edits do.
/// </summary>
internal static class RuleGrouper
{
    /// <summary>Edits join when no more than this many half-words (two per word) separate them.</summary>
    private const int LocalGapHalfWords = 4;

    /// <summary>A clause with at least two changed spots is one unit when this share of its words changed.</summary>
    private const double WholeClauseShare = 0.25;

    /// <summary>A clause with at least three changed spots is one unit when this share of its words changed.</summary>
    private const double ScatteredShare = 0.12;

    /// <summary>The largest gap, in characters, the local join looks at; a longer gap is never "a few words".</summary>
    private const int MaxLocalGap = 200;

    public static ChangeSet Group(PairDiffs diffs, CancellationToken ct) => ReferenceGrouper.Group(diffs, ct, Units, groupMoves: true);

    private sealed class Unit
    {
        public int OldFrom { get; set; }

        public int OldTo { get; set; }

        public int NewFrom { get; set; }

        public int NewTo { get; set; }

        public List<RegionEdit> Edits { get; set; } = [];

        public bool FormatOnly => Edits.TrueForAll(static e => e.IsFormatOnly);

        public static Unit Of(RegionEdit e) => new() { OldFrom = e.OldStart, OldTo = e.OldEnd, NewFrom = e.NewStart, NewTo = e.NewEnd, Edits = [e] };

        public void Absorb(Unit other)
        {
            Grow(other.OldFrom, other.OldTo, other.NewFrom, other.NewTo);
            Edits.AddRange(other.Edits);
            SortEdits(Edits);
        }

        public void Absorb(RegionEdit e)
        {
            Grow(e.OldStart, e.OldEnd, e.NewStart, e.NewEnd);
            Edits.Add(e);
            SortEdits(Edits);
        }

        private void Grow(int oldFrom, int oldTo, int newFrom, int newTo)
        {
            OldFrom = Math.Min(OldFrom, oldFrom);
            NewFrom = Math.Min(NewFrom, newFrom);
            OldTo = Math.Max(OldTo, oldTo);
            NewTo = Math.Max(NewTo, newTo);
        }
    }

    private static void SortEdits(List<RegionEdit> edits) =>
        edits.Sort(static (x, y) => x.OldStart != y.OldStart ? x.OldStart.CompareTo(y.OldStart) : x.NewStart.CompareTo(y.NewStart));

    private static List<(int Position, Change Change)> Units(AlignmentLink link, ProjectedGroup og, ProjectedGroup ng, LinkDiff diff, Alignment al)
    {
        var result = new List<(int, Change)>();
        if (diff.Edits.IsDefaultOrEmpty)
        {
            return result;
        }

        var units = new List<Unit>();
        var formatEdits = new List<RegionEdit>();
        foreach (RegionEdit e in diff.Edits)
        {
            if (e.IsFormatOnly)
            {
                formatEdits.Add(e);
            }
            else
            {
                units.Add(Unit.Of(e));
            }
        }

        string a = og.Text, b = ng.Text;
        foreach (Unit u in units)
        {
            WidenToValues(u, a, b);
        }

        Stabilize(units, formatEdits);
        if (units.Count > 1)
        {
            units = JoinByStructure(units, a, b);
            Stabilize(units, formatEdits);
        }

        units.AddRange(FormatUnits(units, formatEdits, b));
        units.Sort(static (x, y) => x.NewFrom != y.NewFrom ? x.NewFrom.CompareTo(y.NewFrom) : x.OldFrom.CompareTo(y.OldFrom));

        foreach (Unit u in units)
        {
            ChangeKind kind = u.FormatOnly ? ChangeKind.Format : u.OldTo == u.OldFrom ? ChangeKind.Insert : u.NewTo == u.NewFrom ? ChangeKind.Delete : ChangeKind.Replace;
            result.Add((u.NewFrom, ReferenceGrouper.Make(
                kind,
                link.Id,
                ReferenceGrouper.Side(al.Old, og, link.OldNodes, u.OldFrom, u.OldTo - u.OldFrom),
                ReferenceGrouper.Side(al.New, ng, link.NewNodes, u.NewFrom, u.NewTo - u.NewFrom),
                [.. u.Edits],
                [])));
        }

        return result;
    }

    // ---- values ----

    /// <summary>Makes each side of a unit cover every value its edits touch on that side.</summary>
    private static void WidenToValues(Unit u, string a, string b)
    {
        int oldFrom = u.OldFrom, oldTo = u.OldTo, newFrom = u.NewFrom, newTo = u.NewTo;
        List<(int Start, int End)> oldValues = ValueSpans.Overlapping(a, oldFrom, oldTo);
        List<(int Start, int End)> newValues = ValueSpans.Overlapping(b, newFrom, newTo);

        // An edit that only drops or only adds characters of a value ("15%" to "5%") is empty on one side, and that side only touches
        // the value it still has. Without it the unit would read "15%" deleted, as if the "5%" were gone. The value is taken in when
        // it is the other side's value with exactly the edit's characters left out.
        if (oldFrom == oldTo)
        {
            TakeInSurvivingValue(ValueSpans.Overlapping(a, oldFrom, oldTo, touching: true), a, newValues, b, newFrom, newTo, oldValues);
        }
        else if (newFrom == newTo)
        {
            TakeInSurvivingValue(ValueSpans.Overlapping(b, newFrom, newTo, touching: true), b, oldValues, a, oldFrom, oldTo, newValues);
        }

        foreach ((int s, int e) in oldValues)
        {
            u.OldFrom = Math.Min(u.OldFrom, s);
            u.OldTo = Math.Max(u.OldTo, e);
        }

        foreach ((int s, int e) in newValues)
        {
            u.NewFrom = Math.Min(u.NewFrom, s);
            u.NewTo = Math.Max(u.NewTo, e);
        }
    }

    /// <summary>
    /// Adds to <paramref name="emptyValues"/> the value on the empty side that touches the edit when it equals a value of the other
    /// side (<paramref name="otherText"/>, edit range [otherFrom, otherTo)) with the edit's characters removed.
    /// </summary>
    private static void TakeInSurvivingValue(List<(int Start, int End)> touching, string emptyText, List<(int Start, int End)> otherValues, string otherText, int otherFrom, int otherTo, List<(int Start, int End)> emptyValues)
    {
        foreach ((int s, int e) in touching)
        {
            foreach ((int os, int oe) in otherValues)
            {
                int keptBefore = otherFrom - os, keptAfter = oe - otherTo;
                if (os <= otherFrom && otherTo <= oe && keptBefore + keptAfter == e - s
                    && string.CompareOrdinal(otherText, os, emptyText, s, keptBefore) == 0
                    && string.CompareOrdinal(otherText, otherTo, emptyText, s + keptBefore, keptAfter) == 0)
                {
                    emptyValues.Add((s, e));
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Joins units whose ranges meet or overlap, and takes in the format edits that touch a unit, until nothing changes. Taking in
    /// a format edit can make a unit reach its neighbour, so the two steps repeat.
    /// </summary>
    private static void Stabilize(List<Unit> units, List<RegionEdit> formatEdits)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i + 1 < units.Count;)
            {
                if (units[i].OldTo >= units[i + 1].OldFrom || units[i].NewTo >= units[i + 1].NewFrom)
                {
                    units[i].Absorb(units[i + 1]);
                    units.RemoveAt(i + 1);
                    changed = true;
                }
                else
                {
                    i++;
                }
            }

            for (int f = formatEdits.Count - 1; f >= 0; f--)
            {
                RegionEdit e = formatEdits[f];
                foreach (Unit u in units)
                {
                    if ((e.OldStart < u.OldTo && e.OldEnd > u.OldFrom) || (e.NewStart < u.NewTo && e.NewEnd > u.NewFrom))
                    {
                        u.Absorb(e);
                        formatEdits.RemoveAt(f);
                        changed = true;
                        break;
                    }
                }
            }
        }
    }

    // ---- sentences, clauses, density ----

    private static List<Unit> JoinByStructure(List<Unit> units, string a, string b)
    {
        int[] oldBounds = Boundaries(a), newBounds = Boundaries(b);
        var result = new List<Unit>();
        int start = 0;
        for (int i = 1; i <= units.Count; i++)
        {
            bool split = i == units.Count
                || HasBoundary(oldBounds, a.Length, units[i - 1].OldTo, units[i].OldFrom)
                || HasBoundary(newBounds, b.Length, units[i - 1].NewTo, units[i].NewFrom);
            if (!split)
            {
                continue;
            }

            JoinClause(units, start, i, a, b, oldBounds, newBounds, result);
            start = i;
        }

        return result;
    }

    /// <summary>One clause's units: all of them when enough of it changed, otherwise the ones close to each other.</summary>
    private static void JoinClause(List<Unit> units, int from, int to, string a, string b, int[] oldBounds, int[] newBounds, List<Unit> output)
    {
        if (to - from >= 2)
        {
            int changed = 0;
            for (int i = from; i < to; i++)
            {
                foreach (RegionEdit e in units[i].Edits)
                {
                    if (!e.IsFormatOnly)
                    {
                        changed += HalfWords(e.OldText) + HalfWords(e.NewText);
                    }
                }
            }

            Unit first = units[from], last = units[to - 1];
            int floorOld = from > 0 ? units[from - 1].OldTo : 0, floorNew = from > 0 ? units[from - 1].NewTo : 0;
            int ceilOld = to < units.Count ? units[to].OldFrom : a.Length, ceilNew = to < units.Count ? units[to].NewFrom : b.Length;
            int os = ClauseStart(oldBounds, first.OldFrom, floorOld), oe = ClauseEnd(oldBounds, last.OldTo, ceilOld);
            int ns = ClauseStart(newBounds, first.NewFrom, floorNew), ne = ClauseEnd(newBounds, last.NewTo, ceilNew);
            int total = HalfWords(a.AsSpan(os, oe - os)) + HalfWords(b.AsSpan(ns, ne - ns));
            double share = total == 0 ? 0 : (double)changed / total;
            if (share >= WholeClauseShare || (to - from >= 3 && share >= ScatteredShare))
            {
                var whole = new Unit { OldFrom = os, OldTo = oe, NewFrom = ns, NewTo = ne };
                for (int i = from; i < to; i++)
                {
                    whole.Edits.AddRange(units[i].Edits);
                }

                SortEdits(whole.Edits);
                output.Add(whole);
                return;
            }
        }

        Unit cur = units[from];
        for (int i = from + 1; i < to; i++)
        {
            Unit nx = units[i];
            if (GapHalfWords(a, cur.OldTo, nx.OldFrom) <= LocalGapHalfWords || GapHalfWords(b, cur.NewTo, nx.NewFrom) <= LocalGapHalfWords)
            {
                cur.Absorb(nx);
            }
            else
            {
                output.Add(cur);
                cur = nx;
            }
        }

        output.Add(cur);
    }

    /// <summary>
    /// Positions where a sentence or clause starts, including 0 and the text length. Sentences are UAX #29 section 5; a clause also
    /// starts after ";" or ":" followed by white space, and after a line break.
    /// </summary>
    private static int[] Boundaries(string text)
    {
        var set = new SortedSet<int>(TextSegmentation.GetSentenceBoundaries(text)) { 0, text.Length };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool clause = ((c == ';' || c == ':') && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]))) || c == '\n' || c == '\u2028' || c == '\u2029';
            if (!clause)
            {
                continue;
            }

            int p = i + 1;
            while (p < text.Length && char.IsWhiteSpace(text[p]))
            {
                p++;
            }

            set.Add(p);
        }

        return [.. set];
    }

    /// <summary>Whether a sentence or clause boundary lies in [lo, hi], not counting the ends of the text.</summary>
    private static bool HasBoundary(int[] bounds, int length, int lo, int hi)
    {
        int i = LowerBound(bounds, Math.Max(lo, 1));
        return i < bounds.Length && bounds[i] <= hi && bounds[i] < length;
    }

    private static int ClauseStart(int[] bounds, int hullStart, int floor)
    {
        int i = LowerBound(bounds, hullStart + 1) - 1;
        return i >= 0 && bounds[i] >= floor ? bounds[i] : hullStart;
    }

    private static int ClauseEnd(int[] bounds, int hullEnd, int ceil)
    {
        int i = LowerBound(bounds, hullEnd);
        return i < bounds.Length && bounds[i] <= ceil ? bounds[i] : hullEnd;
    }

    private static int LowerBound(int[] sorted, int value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (sorted[mid] < value)
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

    // ---- words ----

    private static int GapHalfWords(string text, int from, int to) => to - from > MaxLocalGap ? int.MaxValue : to <= from ? 0 : HalfWords(text.AsSpan(from, to - from));

    private static int HalfWords(string s) => HalfWords(s.AsSpan());

    /// <summary>
    /// Words in half-word units: a run of letters or digits is two, and each character of a script that has no spaces between words
    /// (ideographs, kana, Thai and its neighbours) is one, so about two characters are a word. It measures density, nothing else.
    /// </summary>
    private static int HalfWords(ReadOnlySpan<char> s)
    {
        int total = 0;
        bool inRun = false;
        foreach (char c in s)
        {
            if (IsSpaceless(c))
            {
                total++;
                inRun = false;
            }
            else if (char.IsLetterOrDigit(c) || (inRun && (c == '\'' || c == '\u2019')))
            {
                if (!inRun)
                {
                    total += 2;
                    inRun = true;
                }
            }
            else
            {
                inRun = false;
            }
        }

        return total;
    }

    private static bool IsSpaceless(char c) =>
        (c >= '\u2E80' && c <= '\u9FFF') || (c >= '\uF900' && c <= '\uFAFF') || (c >= '\u0E00' && c <= '\u0EFF') || (c >= '\u1000' && c <= '\u109F') || (c >= '\u1780' && c <= '\u17FF') || char.IsSurrogate(c);

    // ---- formatting ----

    /// <summary>Format units for the edits no text unit took in: one per edit, neighbours with the same label joined across white space.</summary>
    private static List<Unit> FormatUnits(List<Unit> textUnits, List<RegionEdit> formatEdits, string newText)
    {
        var result = new List<Unit>();
        if (formatEdits.Count == 0)
        {
            return result;
        }

        formatEdits.Sort(static (x, y) => x.OldStart.CompareTo(y.OldStart));
        Unit? cur = null;
        string? curLabel = null;
        foreach (RegionEdit e in formatEdits)
        {
            int c1 = 0, c2 = 0;
            string? label = FormatIntervals.Label(FormatRuns.FormatAt(e.OldRuns, ref c1, 0), FormatRuns.FormatAt(e.NewRuns, ref c2, 0));
            if (cur is not null && string.Equals(label, curLabel, StringComparison.Ordinal) && OnlySpaceBetween(newText, cur.NewTo, e.NewStart) && !TextUnitBetween(textUnits, cur.OldTo, e.OldStart, cur.NewTo, e.NewStart))
            {
                cur.Absorb(e);
                continue;
            }

            if (cur is not null)
            {
                result.Add(cur);
            }

            cur = Unit.Of(e);
            curLabel = label;
        }

        if (cur is not null)
        {
            result.Add(cur);
        }

        return result;
    }

    private static bool OnlySpaceBetween(string text, int from, int to)
    {
        if (to < from || to - from > MaxLocalGap)
        {
            return false;
        }

        for (int i = from; i < to; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a text unit lies in, or touches, the stretch between two format edits on either side. Joining across it would
    /// interleave the two units, and the edits of a link must stay in their true order.
    /// </summary>
    private static bool TextUnitBetween(List<Unit> textUnits, int oldFrom, int oldTo, int newFrom, int newTo)
    {
        foreach (Unit u in textUnits)
        {
            if ((u.NewFrom <= newTo && u.NewTo >= newFrom) || (u.OldFrom <= oldTo && u.OldTo >= oldFrom))
            {
                return true;
            }
        }

        return false;
    }
}
