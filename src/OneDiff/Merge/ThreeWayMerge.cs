using System.Text;

namespace OneDiff.Merge;

/// <summary>The structured outcome of a three-way merge over any sequence of items.</summary>
public sealed class ItemMerge
{
    internal ItemMerge(MergeRegion[] regions, int conflictCount)
    {
        Regions = regions;
        ConflictCount = conflictCount;
    }

    /// <summary>The stretches of the merge in order; they tile the base, ours and theirs exactly.</summary>
    public IReadOnlyList<MergeRegion> Regions { get; }

    /// <summary>Number of conflicts the merge leaves open; always zero when the settings resolve conflicts automatically.</summary>
    public int ConflictCount { get; }

    /// <summary>True when <see cref="ConflictCount"/> is above zero.</summary>
    public bool HasConflicts => ConflictCount > 0;
}

/// <summary>The outcome of a three-way merge of text, line by line.</summary>
public sealed class TextMerge
{
    private readonly string baseText;
    private readonly string oursText;
    private readonly string theirsText;
    private readonly int[] baseStarts;
    private readonly int[] oursStarts;
    private readonly int[] theirsStarts;

    internal TextMerge(string text, MergeRegion[] regions, int conflictCount, string baseText, string oursText, string theirsText, int[] baseStarts, int[] oursStarts, int[] theirsStarts)
    {
        Text = text;
        Regions = regions;
        ConflictCount = conflictCount;
        this.baseText = baseText;
        this.oursText = oursText;
        this.theirsText = theirsText;
        this.baseStarts = baseStarts;
        this.oursStarts = oursStarts;
        this.theirsStarts = theirsStarts;
    }

    /// <summary>
    /// The merged text. Conflicts are marked in the requested style, or settled when the settings say so. A final line without a
    /// line break stays without one, except inside a conflict, where a break is added so the markers start on their own line.
    /// </summary>
    public string Text { get; }

    /// <summary>The stretches of the merge in order; the line ranges tile the base, ours and theirs exactly.</summary>
    public IReadOnlyList<MergeRegion> Regions { get; }

    /// <summary>Number of conflicts the merge leaves open (and marks in <see cref="Text"/>); zero when conflicts were resolved automatically.</summary>
    public int ConflictCount { get; }

    /// <summary>True when <see cref="ConflictCount"/> is above zero.</summary>
    public bool HasConflicts => ConflictCount > 0;

    /// <summary>Number of lines in one of the inputs. Lines end at <c>\n</c> (a <c>\r</c> before it stays part of the line); the last one may have no ending.</summary>
    /// <param name="side">Which input.</param>
    public int LineCount(MergeSide side) => Starts(side).Length - 1;

    /// <summary>The text of a range of lines of one input, line endings included.</summary>
    /// <param name="side">Which input.</param>
    /// <param name="lines">A line range, as found in <see cref="Regions"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range falls outside the input.</exception>
    public ReadOnlySpan<char> Slice(MergeSide side, ItemRange lines)
    {
        int[] starts = Starts(side);
        if (lines.Start < 0 || lines.Length < 0 || lines.End > starts.Length - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lines));
        }

        string text = side switch { MergeSide.Base => baseText, MergeSide.Ours => oursText, _ => theirsText };
        return text.AsSpan(starts[lines.Start], starts[lines.End] - starts[lines.Start]);
    }

    private int[] Starts(MergeSide side) => side switch
    {
        MergeSide.Base => baseStarts,
        MergeSide.Ours => oursStarts,
        MergeSide.Theirs => theirsStarts,
        _ => throw new ArgumentOutOfRangeException(nameof(side)),
    };
}

/// <summary>
/// Three-way merge (diff3): given a common ancestor and two descendants, applies both descendants' changes, and reports the
/// stretches where they disagree as conflicts.
/// </summary>
/// <remarks>
/// <para>
/// The merge follows the diff3 formalisation of Khanna, Kuber and Pierce, "A Formal Investigation of Diff3" (2007). Each side is
/// aligned with the base; stretches only one side changed are taken from that side, stretches both changed to the same items are
/// taken once (a false conflict), and the rest are conflicts. Changes that touch, without a shared item between them, conflict.
/// </para>
/// <para>
/// Conflict layouts follow git's merge-file and merge documentation (conflict styles). See <c>docs/merge.md</c> for the
/// behaviours that documentation leaves open and how they were settled.
/// </para>
/// </remarks>
public static class ThreeWayMerge
{
    /// <summary>Merges three sequences of any item type and reports the result as regions.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="baseItems">The common ancestor.</param>
    /// <param name="ours">Our version.</param>
    /// <param name="theirs">Their version.</param>
    /// <param name="settings">Style, resolution and algorithm; <see cref="MergeSettings.Default"/> when null. Labels and marker size are ignored here.</param>
    /// <param name="comparer">Decides which items are equal. The default comparer when null.</param>
    /// <param name="cancellationToken">Observed while aligning; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <returns>The regions in order. Narrowing a <see cref="ConflictStyle.Merge"/> conflict joins conflicts that are at most three items apart.</returns>
    public static ItemMerge Items<T>(
        ReadOnlySpan<T> baseItems,
        ReadOnlySpan<T> ours,
        ReadOnlySpan<T> theirs,
        MergeSettings? settings = null,
        IEqualityComparer<T>? comparer = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        settings ??= MergeSettings.Default;
        Validate(settings);
        var interner = new TokenInterner<T>(comparer);
        int[] baseIds = interner.Intern(baseItems);
        int[] oursIds = interner.Intern(ours);
        int[] theirsIds = interner.Intern(theirs);
        List<Segment> segments = Diff3Core.Merge(baseIds, oursIds, theirsIds, settings, null, cancellationToken);
        return new ItemMerge(ToRegions(segments), CountOpen(segments, settings));
    }

    /// <summary>
    /// Merges three texts line by line. Lines end at <c>\n</c>; a line is equal to another only if its line ending is too, so
    /// <c>"a"</c> and <c>"a\n"</c> differ, as do <c>"a\r\n"</c> and <c>"a\n"</c>.
    /// </summary>
    /// <param name="baseText">The common ancestor.</param>
    /// <param name="ours">Our version.</param>
    /// <param name="theirs">Their version.</param>
    /// <param name="settings">Style, resolution, labels and marker size; <see cref="MergeSettings.Default"/> when null.</param>
    /// <param name="cancellationToken">Observed while aligning; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <returns>The merged text, the conflict count and the regions.</returns>
    /// <exception cref="ArgumentNullException">A text is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings hold an undefined value or a marker size below 1.</exception>
    public static TextMerge Lines(string baseText, string ours, string theirs, MergeSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseText);
        ArgumentNullException.ThrowIfNull(ours);
        ArgumentNullException.ThrowIfNull(theirs);
        settings ??= MergeSettings.Default;
        Validate(settings);

        int[] baseStarts = LineStarts(baseText);
        int[] oursStarts = LineStarts(ours);
        int[] theirsStarts = LineStarts(theirs);

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var lookup = ids.GetAlternateLookup<ReadOnlySpan<char>>();
        var alnum = new List<bool>();
        int[] baseIds = InternLines(baseText, baseStarts, ids, lookup, alnum);
        int[] oursIds = InternLines(ours, oursStarts, ids, lookup, alnum);
        int[] theirsIds = InternLines(theirs, theirsStarts, ids, lookup, alnum);

        List<Segment> segments = Diff3Core.Merge(baseIds, oursIds, theirsIds, settings, alnum.ToArray(), cancellationToken);
        string text = new TextRenderer(baseText, ours, theirs, baseStarts, oursStarts, theirsStarts, settings).Render(segments);
        return new TextMerge(text, ToRegions(segments), CountOpen(segments, settings), baseText, ours, theirs, baseStarts, oursStarts, theirsStarts);
    }

    private static void Validate(MergeSettings settings)
    {
        if (settings.Style is < ConflictStyle.Merge or > ConflictStyle.ZDiff3)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "undefined conflict style");
        }

        if (settings.Resolution is < ConflictResolution.None or > ConflictResolution.Union)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "undefined conflict resolution");
        }

        if (settings.MarkerSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "the marker size must be at least 1");
        }
    }

    private static int CountOpen(List<Segment> segments, MergeSettings settings)
    {
        if (settings.Resolution != ConflictResolution.None)
        {
            return 0;
        }

        int count = 0;
        foreach (Segment s in segments)
        {
            if (s.Kind == MergeRegionKind.Conflict)
            {
                count++;
            }
        }

        return count;
    }

    private static MergeRegion[] ToRegions(List<Segment> segments)
    {
        var regions = new MergeRegion[segments.Count];
        for (int k = 0; k < regions.Length; k++)
        {
            Segment s = segments[k];
            regions[k] = new MergeRegion(
                s.Kind,
                new ItemRange(s.BaseStart, s.BaseEnd - s.BaseStart),
                new ItemRange(s.OursStart, s.OursLength),
                new ItemRange(s.TheirsStart, s.TheirsLength));
        }

        return regions;
    }

    // Start offset of every line, plus the text length as a final entry. Only '\n' ends a line.
    private static int[] LineStarts(string text)
    {
        var starts = new List<int>();
        int at = 0;
        while (at < text.Length)
        {
            starts.Add(at);
            int newline = text.IndexOf('\n', at);
            at = newline < 0 ? text.Length : newline + 1;
        }

        starts.Add(text.Length);
        return starts.ToArray();
    }

    private static int[] InternLines(
        string text,
        int[] starts,
        Dictionary<string, int> ids,
        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup,
        List<bool> alnum)
    {
        int[] result = new int[starts.Length - 1];
        for (int k = 0; k < result.Length; k++)
        {
            ReadOnlySpan<char> line = text.AsSpan(starts[k], starts[k + 1] - starts[k]);
            if (!lookup.TryGetValue(line, out int id))
            {
                id = ids.Count;
                ids.Add(line.ToString(), id);
                alnum.Add(HasAsciiLetterOrDigit(line));
            }

            result[k] = id;
        }

        return result;
    }

    // Observed in output of git merge-file 2.51.2: only ASCII letters and digits count; accented letters, underscores and
    // punctuation do not.
    private static bool HasAsciiLetterOrDigit(ReadOnlySpan<char> line)
    {
        foreach (char c in line)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class TextRenderer
    {
        private readonly string baseText;
        private readonly string oursText;
        private readonly string theirsText;
        private readonly int[] baseStarts;
        private readonly int[] oursStarts;
        private readonly int[] theirsStarts;
        private readonly MergeSettings settings;
        private readonly StringBuilder output = new();

        public TextRenderer(string baseText, string oursText, string theirsText, int[] baseStarts, int[] oursStarts, int[] theirsStarts, MergeSettings settings)
        {
            this.baseText = baseText;
            this.oursText = oursText;
            this.theirsText = theirsText;
            this.baseStarts = baseStarts;
            this.oursStarts = oursStarts;
            this.theirsStarts = theirsStarts;
            this.settings = settings;
        }

        public string Render(List<Segment> segments)
        {
            foreach (Segment s in segments)
            {
                switch (s.Kind)
                {
                    case MergeRegionKind.TheirsOnly:
                        output.Append(theirsText, theirsStarts[s.TheirsStart], theirsStarts[s.TheirsEnd] - theirsStarts[s.TheirsStart]);
                        break;
                    case MergeRegionKind.Conflict:
                        RenderConflict(s);
                        break;
                    default:
                        output.Append(oursText, oursStarts[s.OursStart], oursStarts[s.OursEnd] - oursStarts[s.OursStart]);
                        break;
                }
            }

            return output.ToString();
        }

        private void RenderConflict(Segment s)
        {
            bool cr = NeedsCarriageReturn(s);
            string eol = cr ? "\r\n" : "\n";

            switch (settings.Resolution)
            {
                case ConflictResolution.Ours:
                    AppendOurs(s, false, eol);
                    return;
                case ConflictResolution.Theirs:
                    AppendTheirs(s, false, eol);
                    return;
                case ConflictResolution.Union:
                    AppendOurs(s, true, eol);
                    AppendTheirs(s, false, eol);
                    return;
            }

            AppendMarker('<', settings.OursLabel, eol);
            AppendOurs(s, true, eol);
            if (settings.Style != ConflictStyle.Merge)
            {
                AppendMarker('|', settings.BaseLabel, eol);
                AppendSection(baseText, baseStarts, s.BaseStart, s.BaseEnd, true, eol);
            }

            AppendMarker('=', null, eol);
            AppendTheirs(s, true, eol);
            AppendMarker('>', settings.TheirsLabel, eol);
        }

        private void AppendOurs(Segment s, bool ensureEnding, string eol) => AppendSection(oursText, oursStarts, s.OursStart, s.OursEnd, ensureEnding, eol);

        private void AppendTheirs(Segment s, bool ensureEnding, string eol) => AppendSection(theirsText, theirsStarts, s.TheirsStart, s.TheirsEnd, ensureEnding, eol);

        private void AppendSection(string text, int[] starts, int from, int to, bool ensureEnding, string eol)
        {
            int length = starts[to] - starts[from];
            if (length == 0)
            {
                return;
            }

            output.Append(text, starts[from], length);
            if (ensureEnding && text[starts[to] - 1] != '\n')
            {
                output.Append(eol);
            }
        }

        private void AppendMarker(char marker, string? label, string eol)
        {
            output.Append(marker, settings.MarkerSize);
            if (!string.IsNullOrEmpty(label))
            {
                output.Append(' ').Append(label);
            }

            output.Append(eol);
        }

        // Observed in output of git merge-file 2.51.2: marker lines (and a line ending added to a last line that has none) use CR LF
        // exactly when the line before the conflict, or the first line of the file when the conflict starts it, ends in CR LF in
        // the base and in each of ours and theirs that has such a line. The base line is the one before where the whole conflict
        // began, also for the pieces a conflict was narrowed into. An empty base never asks for CR LF.
        private bool NeedsCarriageReturn(Segment s)
        {
            if (!EndsWithCrLf(baseText, baseStarts, s.Anchor > 0 ? s.Anchor - 1 : 0, false))
            {
                return false;
            }

            return EndsWithCrLf(oursText, oursStarts, s.OursStart > 0 ? s.OursStart - 1 : 0, true)
                && EndsWithCrLf(theirsText, theirsStarts, s.TheirsStart > 0 ? s.TheirsStart - 1 : 0, true);
        }

        private static bool EndsWithCrLf(string text, int[] starts, int line, bool missingMeansCr)
        {
            if (line >= starts.Length - 1)
            {
                return missingMeansCr;
            }

            int end = starts[line + 1];
            return end - starts[line] >= 2 && text[end - 1] == '\n' && text[end - 2] == '\r';
        }
    }
}
