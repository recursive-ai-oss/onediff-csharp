using System.Text;

namespace OneDiff.Patching;

/// <summary>
/// Makes, writes, reads and applies diff-match-patch patches. The tuning parameters live here, as the wiki's "API" page describes
/// them; the defaults are the wiki's. The patch text format is the wiki's "Unidiff" page (a rolling context: each patch's offsets are
/// those of the text after the earlier patches were applied). Fuzzy placement uses the Bitap approximate-matching search of
/// Wu and Manber, "Fast text searching allowing errors" (1992), scored as the wiki's "API" page describes. Offsets count UTF-16 code units.
/// </summary>
public sealed partial class DmpPatcher
{
    private double matchThreshold = 0.5;
    private int matchDistance = 1000;
    private int matchMaxBits = 32;
    private double patchDeleteThreshold = 0.5;
    private int patchMargin = 4;

    /// <summary>
    /// How strict fuzzy matching is: 0.0 accepts only a perfect match at the expected place, 1.0 accepts anything. Default 0.5.
    /// Must be between 0 and 1.
    /// </summary>
    public double MatchThreshold
    {
        get => matchThreshold;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1.0);
            matchThreshold = value;
        }
    }

    /// <summary>
    /// How far from the expected place a fuzzy match may sit, in characters: a match that far away counts as completely wrong
    /// (on top of its errors). Zero means the match must be at the expected place. Default 1000.
    /// </summary>
    public int MatchDistance
    {
        get => matchDistance;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            matchDistance = value;
        }
    }

    /// <summary>The longest pattern the fuzzy search takes, in characters (1 to 64). Longer patch text is split before matching. Default 32.</summary>
    public int MatchMaxBits
    {
        get => matchMaxBits;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 64);
            matchMaxBits = value;
        }
    }

    /// <summary>
    /// For a long deletion matched fuzzily: how different the found text may be from the text the patch expects to delete, as a
    /// share of its length, before the patch is refused. 0.0 demands a perfect match. Default 0.5.
    /// </summary>
    public double PatchDeleteThreshold
    {
        get => patchDeleteThreshold;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            patchDeleteThreshold = value;
        }
    }

    /// <summary>Characters of context put around each change when making patches. Default 4.</summary>
    public int PatchMargin
    {
        get => patchMargin;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            patchMargin = value;
        }
    }

    // ---------------------------------------------------------------- make

    /// <summary>
    /// Makes patches that turn <paramref name="text1"/> into <paramref name="text2"/>. The character diff trims common ends, compares
    /// long texts line by line first, and is exact within what changed; no readability cleanup is applied to it.
    /// </summary>
    /// <exception cref="ArgumentNullException">A text is null.</exception>
    public IReadOnlyList<DmpPatch> Make(string text1, string text2, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text1);
        ArgumentNullException.ThrowIfNull(text2);
        return Make(text1, DmpDiffer.Compute(text1, text2, cancellationToken));
    }

    /// <summary>
    /// Makes patches from <paramref name="text1"/> and a diff of it (as <see cref="DmpPatch.Diffs"/> would hold). The diffs' old side must
    /// be exactly <paramref name="text1"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The diffs' old side is not <paramref name="text1"/>.</exception>
    public IReadOnlyList<DmpPatch> Make(string text1, IReadOnlyList<DmpDiff> diffs)
    {
        ArgumentNullException.ThrowIfNull(text1);
        ArgumentNullException.ThrowIfNull(diffs);

        var patches = new List<DmpPatch>();
        if (diffs.Count == 0)
        {
            return patches;
        }

        // The text after all diffs, so the context of a patch can be cut from "earlier patches applied, rest still old".
        var t2 = new StringBuilder();
        int oldLength = 0;
        foreach (DmpDiff d in diffs)
        {
            ArgumentNullException.ThrowIfNull(d.Text);
            if (d.Operation != DmpOperation.Delete)
            {
                t2.Append(d.Text);
            }

            if (d.Operation != DmpOperation.Insert)
            {
                oldLength += d.Text.Length;
            }
        }

        if (oldLength != text1.Length)
        {
            throw new ArgumentException("The diffs do not describe text1.", nameof(diffs));
        }

        var pre = new Rolling(t2.ToString(), 0, text1, 0);
        var patch = new DmpPatch();
        int count1 = 0; // offsets in the rolling text, reset to count2 when a patch closes
        int count2 = 0;
        int abs1 = 0; // how much of text1 has been passed
        int abs2 = 0; // how much of the new text has been passed
        for (int x = 0; x < diffs.Count; x++)
        {
            DmpDiff d = diffs[x];
            if (patch.MutableDiffs.Count == 0 && d.Operation != DmpOperation.Equal)
            {
                patch.Start1 = count1;
                patch.Start2 = count2;
            }

            switch (d.Operation)
            {
                case DmpOperation.Insert:
                    patch.MutableDiffs.Add(d);
                    patch.Length2 += d.Text.Length;
                    break;
                case DmpOperation.Delete:
                    patch.Length1 += d.Text.Length;
                    patch.MutableDiffs.Add(d);
                    break;
                default:
                    if (d.Text.Length <= 2 * patchMargin && patch.MutableDiffs.Count > 0 && x != diffs.Count - 1)
                    {
                        // A small equality inside a patch stays in it.
                        patch.MutableDiffs.Add(d);
                        patch.Length1 += d.Text.Length;
                        patch.Length2 += d.Text.Length;
                    }
                    else if (d.Text.Length >= 2 * patchMargin && patch.MutableDiffs.Count > 0)
                    {
                        // A long equality ends the patch. The next one is cut from the text with this one already applied.
                        AddContext(patch, pre);
                        patches.Add(patch);
                        patch = new DmpPatch();
                        pre = new Rolling(pre.Source2, abs2, text1, abs1);
                        count1 = count2;
                    }

                    break;
            }

            if (d.Operation != DmpOperation.Insert)
            {
                count1 += d.Text.Length;
                abs1 += d.Text.Length;
            }

            if (d.Operation != DmpOperation.Delete)
            {
                count2 += d.Text.Length;
                abs2 += d.Text.Length;
            }
        }

        if (patch.MutableDiffs.Count > 0)
        {
            AddContext(patch, pre);
            patches.Add(patch);
        }

        return patches;
    }

    // Grows the context until the patch's old text is unique in the text, then adds one more margin ("for good luck", wiki "API").
    private void AddContext(DmpPatch patch, Rolling text)
    {
        if (text.Length == 0)
        {
            return;
        }

        string pattern = text.Slice(patch.Start2, patch.Start2 + patch.Length1);
        int padding = 0;
        while (patchMargin > 0 && !text.IsUnique(pattern) && pattern.Length < matchMaxBits - patchMargin - patchMargin)
        {
            padding += patchMargin;
            pattern = text.Slice(patch.Start2 - padding, patch.Start2 + patch.Length1 + padding);
        }

        padding += patchMargin;
        string prefix = text.Slice(patch.Start2 - padding, patch.Start2, -1);
        if (prefix.Length > 0)
        {
            patch.MutableDiffs.Insert(0, new DmpDiff(DmpOperation.Equal, prefix));
        }

        string suffix = text.Slice(patch.Start2 + patch.Length1, patch.Start2 + patch.Length1 + padding, 1);
        if (suffix.Length > 0)
        {
            patch.MutableDiffs.Add(new DmpDiff(DmpOperation.Equal, suffix));
        }

        patch.Start1 -= prefix.Length;
        patch.Start2 -= prefix.Length;
        patch.Length1 += prefix.Length + suffix.Length;
        patch.Length2 += prefix.Length + suffix.Length;
    }

    /// <summary>
    /// The text "new text up to <c>P2</c>, then old text from <c>P1</c>": the old text with the patches made so far applied.
    /// Searching it in zones avoids building it again for every patch.
    /// </summary>
    private sealed class Rolling
    {
        private readonly int p2;
        private readonly string source1;
        private readonly int p1;

        public Rolling(string source2, int p2, string source1, int p1)
        {
            Source2 = source2;
            this.p2 = p2;
            this.source1 = source1;
            this.p1 = p1;
        }

        public string Source2 { get; }

        public int Length => p2 + source1.Length - p1;

        private char At(int i) => i < p2 ? Source2[i] : source1[p1 + i - p2];

        /// <summary>
        /// Characters [from, to) clamped to the text. With <paramref name="keepPairsOutside"/> set, the outer edge (the start for a
        /// prefix, the end for a suffix) moves outward rather than cut a surrogate pair; the edge next to the patch never moves.
        /// </summary>
        public string Slice(int from, int to, int keepPairsOutside = 0)
        {
            from = Math.Max(0, from);
            to = Math.Min(Length, to);
            if (from >= to)
            {
                return string.Empty;
            }

            if (keepPairsOutside < 0 && from > 0 && char.IsLowSurrogate(At(from)) && char.IsHighSurrogate(At(from - 1)))
            {
                from--;
            }

            if (keepPairsOutside > 0 && to < Length && char.IsHighSurrogate(At(to - 1)) && char.IsLowSurrogate(At(to)))
            {
                to++;
            }

            var sb = new StringBuilder(to - from);
            if (from < p2)
            {
                sb.Append(Source2, from, Math.Min(to, p2) - from);
            }

            if (to > p2)
            {
                int a = Math.Max(from, p2) - p2 + p1;
                sb.Append(source1, a, to - Math.Max(from, p2));
            }

            return sb.ToString();
        }

        /// <summary>True when the pattern occurs at most once (overlapping occurrences count separately).</summary>
        public bool IsUnique(string pattern)
        {
            if (pattern.Length == 0)
            {
                return Length < 2;
            }

            int found = 0;
            found += CountUpToTwo(Source2.AsSpan(0, p2), pattern);
            if (found < 2)
            {
                found += CountUpToTwo(source1.AsSpan(p1), pattern);
            }

            if (found < 2 && pattern.Length > 1 && p2 > 0 && p1 < source1.Length)
            {
                // Occurrences that straddle the seam between the two parts.
                int left = Math.Min(p2, pattern.Length - 1);
                int right = Math.Min(source1.Length - p1, pattern.Length - 1);
                string seam = string.Concat(Source2.AsSpan(p2 - left, left), source1.AsSpan(p1, right));
                for (int s = 0; s + pattern.Length <= seam.Length && found < 2; s++)
                {
                    if (s < left && s + pattern.Length > left && string.CompareOrdinal(seam, s, pattern, 0, pattern.Length) == 0)
                    {
                        found++;
                    }
                }
            }

            return found < 2;
        }

        private static int CountUpToTwo(ReadOnlySpan<char> haystack, string pattern)
        {
            int count = 0;
            int from = 0;
            while (count < 2 && from + pattern.Length <= haystack.Length)
            {
                int i = haystack[from..].IndexOf(pattern, StringComparison.Ordinal);
                if (i < 0)
                {
                    break;
                }

                count++;
                from += i + 1;
            }

            return count;
        }
    }

    // ---------------------------------------------------------------- text

    /// <summary>Writes patches as patch text (the wiki's "Unidiff" page). No patches give the empty string.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="patches"/> is null.</exception>
    public static string ToText(IEnumerable<DmpPatch> patches)
    {
        ArgumentNullException.ThrowIfNull(patches);
        var sb = new StringBuilder();
        foreach (DmpPatch p in patches)
        {
            p.AppendTo(sb);
        }

        return sb.ToString();
    }

    /// <summary>The longest patch text, in characters, that <see cref="FromText"/> accepts.</summary>
    public const int MaxTextLength = 1 << 28;

    /// <summary>
    /// Reads patch text. Starts come from the headers; lengths are recomputed from the diffs, so a header that miscounts (for example
    /// by counting code points where this library counts UTF-16 units) does no harm. Blank lines between lines are skipped.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="PatchFormatException">The text is malformed; see <see cref="PatchErrorKind"/>.</exception>
    public static IReadOnlyList<DmpPatch> FromText(string text)
    {
        if (!TryFromText(text, out IReadOnlyList<DmpPatch>? patches, out PatchParseError error))
        {
            throw new PatchFormatException(error);
        }

        return patches;
    }

    /// <summary>Reads patch text without throwing for malformed input. See <see cref="FromText"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static bool TryFromText(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<DmpPatch>? patches, out PatchParseError error)
    {
        ArgumentNullException.ThrowIfNull(text);
        patches = null;
        error = default;
        if (text.Length > MaxTextLength)
        {
            error = new PatchParseError(PatchErrorKind.TooLarge, 1, 0, "The patch text is too large.");
            return false;
        }

        var list = new List<DmpPatch>();
        DmpPatch? current = null;
        int pos = 0;
        int lineNo = 0;
        while (pos < text.Length)
        {
            lineNo++;
            int nl = text.AsSpan(pos).IndexOf('\n');
            int end = nl < 0 ? text.Length : pos + nl;
            ReadOnlySpan<char> line = text.AsSpan(pos, end - pos);
            int lineStart = pos;
            pos = nl < 0 ? text.Length : end + 1;

            if (line.Length == 0)
            {
                continue;
            }

            char sign = line[0];
            if (sign == '@')
            {
                if (!TryParseHeader(line, out int s1, out int s2))
                {
                    error = new PatchParseError(PatchErrorKind.InvalidHunkHeader, lineNo, lineStart, "The patch header is malformed.");
                    return false;
                }

                current = new DmpPatch { Start1 = s1, Start2 = s2 };
                list.Add(current);
                continue;
            }

            if (current is null || (sign != '+' && sign != '-' && sign != ' '))
            {
                error = new PatchParseError(PatchErrorKind.UnexpectedLine, lineNo, lineStart, "A patch line starts with something other than '@', '+', '-' or a space.");
                return false;
            }

            if (!DmpText.TryDecode(line[1..], out string body))
            {
                error = new PatchParseError(PatchErrorKind.InvalidEncoding, lineNo, lineStart, "A patch line has a malformed percent escape or invalid UTF-8.");
                return false;
            }

            DmpOperation op = sign == '+' ? DmpOperation.Insert : sign == '-' ? DmpOperation.Delete : DmpOperation.Equal;
            current.MutableDiffs.Add(new DmpDiff(op, body));
            if (op != DmpOperation.Insert)
            {
                current.Length1 += body.Length;
            }

            if (op != DmpOperation.Delete)
            {
                current.Length2 += body.Length;
            }
        }

        patches = list;
        return true;
    }

    // "@@ -a[,b] +c[,d] @@": the start is one-based unless its length is zero (wiki "Unidiff"); the lengths are ignored here.
    private static bool TryParseHeader(ReadOnlySpan<char> h, out int start1, out int start2)
    {
        start1 = start2 = 0;
        if (!h.StartsWith("@@ -", StringComparison.Ordinal))
        {
            return false;
        }

        int p = 4;
        if (!ReadStart(h, ref p, out start1))
        {
            return false;
        }

        if (!h[p..].StartsWith(" +", StringComparison.Ordinal))
        {
            return false;
        }

        p += 2;
        if (!ReadStart(h, ref p, out start2))
        {
            return false;
        }

        return h[p..].StartsWith(" @@", StringComparison.Ordinal);
    }

    private static bool ReadStart(ReadOnlySpan<char> h, ref int p, out int start)
    {
        start = 0;
        if (!ReadNumber(h, ref p, out int first))
        {
            return false;
        }

        int length = 1;
        if (p < h.Length && h[p] == ',')
        {
            p++;
            if (!ReadNumber(h, ref p, out length))
            {
                return false;
            }
        }

        if (length == 0)
        {
            start = first;
        }
        else
        {
            if (first == 0)
            {
                return false;
            }

            start = first - 1;
        }

        return true;
    }

    private static bool ReadNumber(ReadOnlySpan<char> h, ref int p, out int value)
    {
        value = 0;
        int begin = p;
        long acc = 0;
        while (p < h.Length && h[p] is >= '0' and <= '9')
        {
            acc = (acc * 10) + (h[p] - '0');
            if (acc > int.MaxValue / 4)
            {
                return false;
            }

            p++;
        }

        value = (int)acc;
        return p > begin;
    }

    // ---------------------------------------------------------------- match

    /// <summary>
    /// Finds the place in <paramref name="text"/> where <paramref name="pattern"/> fits best near <paramref name="loc"/>, allowing errors
    /// (the wiki's <c>match_main</c>). An exact hit at <paramref name="loc"/> wins; otherwise the Bitap search scores each candidate as
    /// errors / pattern length + distance from <paramref name="loc"/> / <see cref="MatchDistance"/> and takes the best one under
    /// <see cref="MatchThreshold"/>.
    /// </summary>
    /// <param name="text">The text to search.</param>
    /// <param name="pattern">The text to look for; at most <see cref="MatchMaxBits"/> characters.</param>
    /// <param name="loc">The offset where the pattern is expected; clamped into the text.</param>
    /// <returns>The offset of the best match, or -1 when none is good enough.</returns>
    /// <exception cref="ArgumentNullException">A text is null.</exception>
    /// <exception cref="ArgumentException">The pattern is longer than <see cref="MatchMaxBits"/>.</exception>
    public int Match(string text, string pattern, int loc)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length > matchMaxBits)
        {
            throw new ArgumentException("The pattern is longer than MatchMaxBits.", nameof(pattern));
        }

        loc = Math.Max(0, Math.Min(loc, text.Length));
        if (text.Length == pattern.Length && string.Equals(text, pattern, StringComparison.Ordinal))
        {
            return 0;
        }

        if (text.Length == 0)
        {
            return -1;
        }

        if (loc + pattern.Length <= text.Length && text.AsSpan(loc, pattern.Length).SequenceEqual(pattern))
        {
            return loc;
        }

        return Bitap(text, pattern, loc);
    }

    // Wu & Manber 1992: one bit-vector per error level; bit i of R[j] says "the first i+1 pattern characters match the text ending
    // at j with at most d errors". The binary search narrows the window around loc to where a score under the threshold is still possible.
    private int Bitap(string text, string pattern, int loc)
    {
        var alphabet = new Dictionary<char, ulong>();
        for (int i = 0; i < pattern.Length; i++)
        {
            alphabet.TryGetValue(pattern[i], out ulong m);
            alphabet[pattern[i]] = m | (1UL << (pattern.Length - i - 1));
        }

        double scoreThreshold = matchThreshold;
        int bestLoc = text.AsSpan(loc).IndexOf(pattern, StringComparison.Ordinal);
        if (bestLoc >= 0)
        {
            bestLoc += loc;
            scoreThreshold = Math.Min(Score(0, bestLoc, loc, pattern.Length), scoreThreshold);
            int lastEnd = Math.Min(text.Length, loc + (2 * pattern.Length));
            int back = text.AsSpan(0, lastEnd).LastIndexOf(pattern, StringComparison.Ordinal);
            if (back >= 0)
            {
                scoreThreshold = Math.Min(Score(0, back, loc, pattern.Length), scoreThreshold);
            }
        }

        ulong matchMask = 1UL << (pattern.Length - 1);
        bestLoc = -1;
        int binMax = pattern.Length + text.Length;
        ulong[] lastRd = [];
        for (int d = 0; d < pattern.Length; d++)
        {
            int binMin = 0;
            int binMid = binMax;
            while (binMin < binMid)
            {
                if (Score(d, loc + binMid, loc, pattern.Length) <= scoreThreshold)
                {
                    binMin = binMid;
                }
                else
                {
                    binMax = binMid;
                }

                binMid = ((binMax - binMin) / 2) + binMin;
            }

            binMax = binMid;
            int start = Math.Max(1, loc - binMid + 1);
            int finish = Math.Min(loc + binMid, text.Length) + pattern.Length;
            var rd = new ulong[finish + 2];
            rd[finish + 1] = (1UL << d) - 1;
            for (int j = finish; j >= start; j--)
            {
                ulong charMatch = text.Length <= j - 1 ? 0UL : alphabet.GetValueOrDefault(text[j - 1]);
                if (d == 0)
                {
                    rd[j] = ((rd[j + 1] << 1) | 1UL) & charMatch;
                }
                else
                {
                    rd[j] = (((rd[j + 1] << 1) | 1UL) & charMatch) | (((lastRd[j + 1] | lastRd[j]) << 1) | 1UL) | lastRd[j + 1];
                }

                if ((rd[j] & matchMask) != 0)
                {
                    double score = Score(d, j - 1, loc, pattern.Length);
                    if (score <= scoreThreshold)
                    {
                        scoreThreshold = score;
                        bestLoc = j - 1;
                        if (bestLoc > loc)
                        {
                            // Searching on the near side of loc already found a match; the far side only matters if it is as close.
                            start = Math.Max(1, (2 * loc) - bestLoc);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            if (Score(d + 1, loc, loc, pattern.Length) > scoreThreshold)
            {
                break;
            }

            lastRd = rd;
        }

        return bestLoc;
    }

    private double Score(int errors, int x, int loc, int patternLength)
    {
        double accuracy = errors / (double)patternLength;
        int proximity = Math.Abs(loc - x);
        if (matchDistance == 0)
        {
            return proximity == 0 ? accuracy : 1.0;
        }

        return accuracy + (proximity / (double)matchDistance);
    }
}
