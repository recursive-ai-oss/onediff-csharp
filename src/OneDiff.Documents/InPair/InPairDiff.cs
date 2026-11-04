using System.Collections.Immutable;
using OneDiff.Documents.Ledger;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Tokenization;

namespace OneDiff.Documents.InPair;

/// <summary>
/// The in-pair diff. For every aligned link it compares the joined compared text word by word (UAX #29 word segments), cleans
/// the script up so a change reads as a phrase rather than a patchwork, refines a replaced number (or a replaced run of a script
/// without spaces) to the characters that differ, and finds formatting differences of the equal text as attribute intervals.
/// Every edit carries its text and formatting on both sides, so replaying the edits forward gives the new text and backward the old.
/// </summary>
internal static class InPairDiff
{
    /// <summary>Runs the in-pair diff over every link of the alignment.</summary>
    public static PairDiffs Run(Alignment alignment, CancellationToken ct)
    {
        CompareSettings settings = alignment.Settings;
        LedgerCategory?[] hiddenOld = HiddenNodes.Compute(alignment.Old, settings);
        LedgerCategory?[] hiddenNew = HiddenNodes.Compute(alignment.New, settings);
        var diagnostics = new List<Diagnostic>(alignment.Diagnostics);
        ImmutableArray<LinkDiff>.Builder diffs = ImmutableArray.CreateBuilder<LinkDiff>(alignment.Links.Length);
        bool capReported = false;
        foreach (AlignmentLink link in alignment.Links)
        {
            ct.ThrowIfCancellationRequested();
            if (hiddenOld[link.OldNodes[0]] is not null || hiddenNew[link.NewNodes[0]] is not null)
            {
                diffs.Add(new LinkDiff(link.Id, [], []));
                continue;
            }

            ProjectedGroup og = ProjectedGroup.Create(alignment.Old, link.OldNodes, settings);
            ProjectedGroup ng = ProjectedGroup.Create(alignment.New, link.NewNodes, settings);
            ImmutableArray<RegionEdit> edits = TextEdits(og, ng, settings, link.Id, diagnostics, ref capReported, ct);
            diffs.Add(new LinkDiff(link.Id, edits, ReferenceDiff.AttributeEdits(og, ng)));
        }

        ImmutableArray<LinkDiff> linkDiffs = diffs.MoveToImmutable();
        ChangeLedger ledger = LedgerBuilder.Build(alignment, id => linkDiffs[id].Edits, ct);
        return new PairDiffs(alignment, linkDiffs, ledger, [.. diagnostics]);
    }

    private static ImmutableArray<RegionEdit> TextEdits(
        ProjectedGroup og,
        ProjectedGroup ng,
        CompareSettings settings,
        int linkId,
        List<Diagnostic> diagnostics,
        ref bool capReported,
        CancellationToken ct)
    {
        string a = og.Text, b = ng.Text;
        var edits = ImmutableArray.CreateBuilder<RegionEdit>();
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            FormatIntervals.AddEdits(og, ng, 0, 0, a.Length, edits);
            return edits.ToImmutable();
        }

        var interner = new TokenSeq.Interner();
        TokenSeq o = TokenSeq.Words(a, interner), n = TokenSeq.Words(b, interner);
        if ((long)o.Count * n.Count > settings.CostCap)
        {
            return ReferenceDiff.CappedEdits(og, ng, linkId, diagnostics, ref capReported);
        }

        int po = 0, pn = 0;
        foreach (TokenRegion r in TokenRegions.Compute(o, n, settings.Algorithm, ct))
        {
            ct.ThrowIfCancellationRequested();
            int of = o.Starts[r.OldFrom], ot = o.Starts[r.OldTo], nf = n.Starts[r.NewFrom], nt = n.Starts[r.NewTo];
            FormatIntervals.AddEdits(og, ng, po, pn, of - po, edits);
            if (!TryRefine(og, ng, of, ot, nf, nt, edits, ct))
            {
                ReferenceDiff.AddRegion(og, ng, of, ot - of, nf, nt - nf, edits);
            }

            po = ot;
            pn = nt;
        }

        FormatIntervals.AddEdits(og, ng, po, pn, a.Length - po, edits);
        return edits.ToImmutable();
    }

    /// <summary>
    /// A replaced number shows which digits changed, and a replaced run in a script that has no spaces between words (Thai, Lao,
    /// Khmer, Myanmar: UAX #29 cannot cut it without a dictionary) shows which characters changed. Other replaced words stay whole,
    /// because a reader wants "Parties" replaced by "Party", not "ies" by "y".
    /// </summary>
    private static bool TryRefine(ProjectedGroup og, ProjectedGroup ng, int of, int ot, int nf, int nt, ImmutableArray<RegionEdit>.Builder edits, CancellationToken ct)
    {
        if (ot == of || nt == nf)
        {
            return false;
        }

        string x = og.Text.Substring(of, ot - of), y = ng.Text.Substring(nf, nt - nf);
        bool digits = ContainsDigit(x) && ContainsDigit(y) && IsSingleSegment(x) && IsSingleSegment(y);
        if (!digits && !(IsSpacelessScript(x) && IsSpacelessScript(y)))
        {
            return false;
        }

        TextDiff d = Diff.Semantic(x, y, TokenKind.Grapheme, DiffAlgorithm.Myers, ct);
        List<TokenRegion> parts = TokenRegions.FromScript(d.Script);
        ReadOnlySpan<TokenRange> dt = d.OldTokens, nr = d.NewTokens;
        // Keep the refinement only when something is shared and it does not shatter into many pieces.
        if (parts.Count == 0 || parts.Count > 3 || (parts.Count == 1 && parts[0].OldFrom == 0 && parts[0].OldTo == dt.Length && parts[0].NewFrom == 0 && parts[0].NewTo == nr.Length))
        {
            return false;
        }

        int po = 0, pn = 0;
        foreach (TokenRegion r in parts)
        {
            int ro = r.OldFrom < dt.Length ? dt[r.OldFrom].Start : x.Length, rt = r.OldTo > r.OldFrom ? dt[r.OldTo - 1].End : ro;
            int rn = r.NewFrom < nr.Length ? nr[r.NewFrom].Start : y.Length, ru = r.NewTo > r.NewFrom ? nr[r.NewTo - 1].End : rn;
            FormatIntervals.AddEdits(og, ng, of + po, nf + pn, ro - po, edits);
            ReferenceDiff.AddRegion(og, ng, of + ro, rt - ro, nf + rn, ru - rn, edits);
            po = rt;
            pn = ru;
        }

        FormatIntervals.AddEdits(og, ng, of + po, nf + pn, x.Length - po, edits);
        return true;
    }

    private static bool ContainsDigit(string s)
    {
        foreach (char c in s)
        {
            if (char.IsDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One UAX #29 segment: no white space inside.</summary>
    private static bool IsSingleSegment(string s)
    {
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether every letter is Thai, Lao, Khmer or Myanmar, and there is at least one.</summary>
    private static bool IsSpacelessScript(string s)
    {
        bool any = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsDigit(c))
            {
                continue;
            }

            bool spaceless = (c >= '\u0E00' && c <= '\u0EFF') || (c >= '\u1000' && c <= '\u109F') || (c >= '\u1780' && c <= '\u17FF') || (c >= '\uAA60' && c <= '\uAA7F') || (c >= '\u19E0' && c <= '\u19FF');
            if (!spaceless)
            {
                return false;
            }

            any = true;
        }

        return any;
    }
}
