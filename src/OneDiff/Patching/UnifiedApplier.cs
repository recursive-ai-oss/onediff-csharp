namespace OneDiff.Patching;

/// <summary>How a unified patch is applied.</summary>
public sealed class UnifiedApplyOptions
{
    /// <summary>
    /// The most context lines that may be ignored at each end of a hunk when it does not match as written. Two by default, as in the
    /// GNU patch manual ("Imperfect"). Zero requires every context line to match.
    /// </summary>
    public int MaxFuzz { get; init; } = 2;

    /// <summary>The farthest, in lines, from the expected place a hunk is looked for. Unbounded by default.</summary>
    public int MaxOffset { get; init; } = int.MaxValue;

    /// <summary>Apply the patch backwards: remove its insertions and restore its deletions.</summary>
    public bool Reverse { get; init; }
}

/// <summary>What happened to one hunk.</summary>
public enum HunkOutcome
{
    /// <summary>It matched exactly where the header said.</summary>
    Applied,

    /// <summary>It matched exactly, some lines away from where the header said.</summary>
    AppliedWithOffset,

    /// <summary>It matched only after ignoring context lines at its ends (and perhaps some lines away).</summary>
    AppliedWithFuzz,

    /// <summary>It matched nowhere; the text is unchanged there.</summary>
    Rejected,
}

/// <summary>Why a hunk was rejected.</summary>
public enum HunkRejectReason
{
    /// <summary>The hunk was applied.</summary>
    None,

    /// <summary>Its old lines were not found, at any allowed offset and fuzz.</summary>
    ContextMismatch,

    /// <summary>Its new lines are already there: the change looks applied, or the patch looks reversed.</summary>
    AlreadyApplied,
}

/// <summary>The outcome of one hunk.</summary>
/// <param name="HunkIndex">Zero-based position of the hunk in the patch.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Offset">Lines from the expected place (positive is later in the file). Zero when rejected.</param>
/// <param name="Fuzz">Context lines ignored at each end. Zero unless <see cref="Outcome"/> is <see cref="HunkOutcome.AppliedWithFuzz"/>.</param>
/// <param name="Line">Zero-based line of the input text where the matched lines start (including ignored context). -1 when rejected.</param>
/// <param name="Reason">Why it was rejected, or <see cref="HunkRejectReason.None"/>.</param>
public readonly record struct HunkResult(int HunkIndex, HunkOutcome Outcome, int Offset, int Fuzz, int Line, HunkRejectReason Reason);

/// <summary>The result of applying a unified patch. Conflicts are reported here and never thrown.</summary>
public sealed class UnifiedApplyResult
{
    internal UnifiedApplyResult(string text, HunkResult[] hunks, UnifiedFilePatch? rejected)
    {
        Text = text;
        Hunks = hunks;
        Rejected = rejected;
    }

    /// <summary>The patched text. Rejected hunks leave their place unchanged.</summary>
    public string Text { get; }

    /// <summary>One entry per hunk, in order.</summary>
    public IReadOnlyList<HunkResult> Hunks { get; }

    /// <summary>The rejected hunks as a patch (what GNU patch writes to a reject file), or null when none were rejected.</summary>
    public UnifiedFilePatch? Rejected { get; }

    /// <summary>True when every hunk was applied, with or without offset or fuzz.</summary>
    public bool Success => Rejected is null;
}

/// <summary>
/// Applies hunks in the manner of the GNU patch manual ("Imperfect"): look where the header says, then farther away line by line;
/// when nothing matches, ignore one and then two context lines at each end and look again.
/// </summary>
internal static class UnifiedApplier
{
    public static UnifiedApplyResult Apply(UnifiedFilePatch patch, string text, UnifiedApplyOptions options)
    {
        UnifiedFilePatch p = options.Reverse ? patch.Reverse() : patch;
        List<string> lines = PatchLines.Split(text);
        var results = new HunkResult[p.Hunks.Count];
        var rejected = new List<UnifiedHunk>();
        var output = new List<string>(lines.Count);
        int consumed = 0;
        int hint = 0;
        int maxFuzz = Math.Max(0, options.MaxFuzz);

        for (int h = 0; h < p.Hunks.Count; h++)
        {
            UnifiedHunk hunk = p.Hunks[h];
            var oldSide = new List<string>(hunk.OldCount);
            var newSide = new List<string>(hunk.NewCount);
            int lead = 0;
            int trail = 0;
            bool seenChange = false;
            ReadOnlySpan<UnifiedLine> body = hunk.Lines;
            foreach (UnifiedLine l in body)
            {
                if (l.Kind != UnifiedLineKind.Insert)
                {
                    oldSide.Add(l.Text);
                }

                if (l.Kind != UnifiedLineKind.Delete)
                {
                    newSide.Add(l.Text);
                }

                if (l.Kind == UnifiedLineKind.Context)
                {
                    if (seenChange)
                    {
                        trail++;
                    }
                    else
                    {
                        lead++;
                    }
                }
                else
                {
                    seenChange = true;
                    trail = 0;
                }
            }

            // Observed in GNU patch 2.8: a hunk with less leading than trailing context was cut at the top of the file, so it must
            // match there; the mirror case must match at the end.
            bool anchorStart = lead < trail;
            bool anchorEnd = trail < lead;
            int nominal = hunk.OldIndex;

            int foundAt = -1;
            int usedLead = 0;
            int usedTrail = 0;
            int fuzzUsed = 0;
            int prevLead = -1;
            int prevTrail = -1;
            for (int fuzz = 0; fuzz <= maxFuzz && foundAt < 0; fuzz++)
            {
                usedLead = Math.Min(fuzz, lead);
                usedTrail = Math.Min(fuzz, trail);
                if (usedLead == prevLead && usedTrail == prevTrail)
                {
                    continue; // nothing more to ignore than at the last level
                }

                prevLead = usedLead;
                prevTrail = usedTrail;
                foundAt = Locate(lines, oldSide, usedLead, oldSide.Count - usedLead - usedTrail, nominal + usedLead + hint, consumed, anchorStart, anchorEnd, usedLead, options.MaxOffset);
                fuzzUsed = fuzz;
            }

            if (foundAt < 0)
            {
                // Tell a stale conflict from an already-applied change: look for the new lines at fuzz zero.
                int again = newSide.Count == 0 ? -1 : Locate(lines, newSide, 0, newSide.Count, nominal + hint, consumed, anchorStart, anchorEnd, 0, options.MaxOffset);
                results[h] = new HunkResult(h, HunkOutcome.Rejected, 0, 0, -1, again >= 0 ? HunkRejectReason.AlreadyApplied : HunkRejectReason.ContextMismatch);
                rejected.Add(hunk);
                continue;
            }

            int reducedLen = oldSide.Count - usedLead - usedTrail;
            for (; consumed < foundAt; consumed++)
            {
                output.Add(lines[consumed]);
            }

            for (int k = usedLead; k < newSide.Count - usedTrail; k++)
            {
                output.Add(newSide[k]);
            }

            consumed = foundAt + reducedLen;
            int offset = foundAt - (nominal + usedLead);
            hint = offset;
            HunkOutcome outcome = fuzzUsed > 0 && (usedLead > 0 || usedTrail > 0)
                ? HunkOutcome.AppliedWithFuzz
                : offset != 0 ? HunkOutcome.AppliedWithOffset : HunkOutcome.Applied;
            results[h] = new HunkResult(h, outcome, offset, outcome == HunkOutcome.AppliedWithFuzz ? fuzzUsed : 0, foundAt - usedLead, HunkRejectReason.None);
        }

        for (; consumed < lines.Count; consumed++)
        {
            output.Add(lines[consumed]);
        }

        UnifiedFilePatch? rej = rejected.Count == 0 ? null : new UnifiedFilePatch(p.OldName, p.NewName, rejected, p.OldTimestamp, p.NewTimestamp);
        return new UnifiedApplyResult(string.Concat(output), results, rej);
    }

    // Finds the start index of old[skip .. skip+length) in lines, nearest to guess, forward before backward at equal distance.
    // The match may not begin before 'floor' (lines already consumed by earlier hunks).
    private static int Locate(List<string> lines, List<string> old, int skip, int length, int guess, int floor, bool anchorStart, bool anchorEnd, int unstrippedLead, int maxOffset)
    {
        long lo = floor;
        long hi = (long)lines.Count - length;
        if (lo > hi)
        {
            return -1;
        }

        long reach = Math.Min(maxOffset, Math.Max(Math.Abs(hi - guess), Math.Abs(guess - lo)));
        for (long d = 0; d <= reach; d++)
        {
            for (int dir = 0; dir < (d == 0 ? 1 : 2); dir++)
            {
                long cand = dir == 0 ? guess + d : guess - d;
                if (cand < lo || cand > hi)
                {
                    continue;
                }

                int c = (int)cand;
                if (anchorStart && c - unstrippedLead != 0)
                {
                    continue;
                }

                if (anchorEnd && c - unstrippedLead + old.Count != lines.Count)
                {
                    continue;
                }

                if (Matches(lines, old, skip, length, c))
                {
                    return c;
                }
            }
        }

        return -1;
    }

    private static bool Matches(List<string> lines, List<string> old, int skip, int length, int at)
    {
        for (int k = 0; k < length; k++)
        {
            if (!string.Equals(lines[at + k], old[skip + k], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
