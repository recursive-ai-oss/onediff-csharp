using System.Collections.Immutable;
using OneDiff.Documents.Ledger;
using OneDiff.Documents.Model;
using OneDiff.Tokenization;

namespace OneDiff.Documents.Pipeline;

/// <summary>The reference in-pair diff: the text core at word level over each link's joined compared text, plus format and attribute differences.</summary>
internal static class ReferenceDiff
{
    /// <param name="alignment">The alignment.</param>
    /// <param name="coarse">When true, a link whose content differs gets one edit over its whole text (used by the fallback).</param>
    /// <param name="ct">Cancellation.</param>
    public static PairDiffs Run(Alignment alignment, bool coarse, CancellationToken ct)
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
            ImmutableArray<RegionEdit> edits = coarse ? CoarseEdits(og, ng) : TextEdits(og, ng, settings, link.Id, diagnostics, ref capReported, ct);
            diffs.Add(new LinkDiff(link.Id, edits, AttributeEdits(og, ng)));
        }

        ImmutableArray<LinkDiff> linkDiffs = diffs.MoveToImmutable();
        ChangeLedger ledger = LedgerBuilder.Build(alignment, id => linkDiffs[id].Edits, ct);
        return new PairDiffs(alignment, linkDiffs, ledger, [.. diagnostics]);
    }

    /// <summary>One edit over the whole joined text when anything differs in text or formatting.</summary>
    private static ImmutableArray<RegionEdit> CoarseEdits(ProjectedGroup og, ProjectedGroup ng)
    {
        if (string.Equals(og.Text, ng.Text, StringComparison.Ordinal) && FormatRuns.SameRuns(og.Runs, ng.Runs))
        {
            return [];
        }

        return [new RegionEdit(0, og.Text.Length, 0, ng.Text.Length, og.Text, ng.Text, og.Runs, ng.Runs)];
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
            FormatEditsFor(og, ng, 0, 0, a.Length, edits);
            return edits.ToImmutable();
        }

        int oldTokens = Tokenizer.Tokenize(a, TokenKind.Word).Length, newTokens = Tokenizer.Tokenize(b, TokenKind.Word).Length;
        if ((long)oldTokens * newTokens > settings.CostCap)
        {
            return CappedEdits(og, ng, linkId, diagnostics, ref capReported);
        }

        TextDiff td = Diff.Text(a, b, TokenKind.Word, settings.Algorithm, ct);
        ReadOnlySpan<TokenRange> ot = td.OldTokens, nt = td.NewTokens;
        EditScript script = td.Script;
        for (int i = 0; i < script.Count; i++)
        {
            Edit e = script[i];
            if (e.Kind == EditKind.Equal)
            {
                int os = ot[e.OldStart].Start, ns = nt[e.NewStart].Start;
                int len = ot[e.OldEnd - 1].End - os;
                FormatEditsFor(og, ng, os, ns, len, edits);
                continue;
            }

            Edit del = e, ins = e;
            if (e.Kind == EditKind.Delete && i + 1 < script.Count && script[i + 1].Kind == EditKind.Insert)
            {
                ins = script[++i];
            }
            else if (e.Kind == EditKind.Delete)
            {
                ins = Edit.Insert(e.OldEnd, e.NewStart, 0);
            }
            else
            {
                del = Edit.Delete(e.OldStart, 0, e.NewStart);
            }

            int oldFrom = del.OldLength > 0 ? ot[del.OldStart].Start : (del.OldStart < ot.Length ? ot[del.OldStart].Start : a.Length);
            int oldTo = del.OldLength > 0 ? ot[del.OldEnd - 1].End : oldFrom;
            int newFrom = ins.NewLength > 0 ? nt[ins.NewStart].Start : (ins.NewStart < nt.Length ? nt[ins.NewStart].Start : b.Length);
            int newTo = ins.NewLength > 0 ? nt[ins.NewEnd - 1].End : newFrom;
            AddRegion(og, ng, oldFrom, oldTo - oldFrom, newFrom, newTo - newFrom, edits);
        }

        return edits.ToImmutable();
    }

    /// <summary>Too big to diff word by word: keeps the common start and end and replaces the middle. Still complete.</summary>
    internal static ImmutableArray<RegionEdit> CappedEdits(ProjectedGroup og, ProjectedGroup ng, int linkId, List<Diagnostic> diagnostics, ref bool capReported)
    {
        string a = og.Text, b = ng.Text;
        var edits = ImmutableArray.CreateBuilder<RegionEdit>();
        if (!capReported)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "diffpairs.cap", $"link {linkId} is too large for a word diff; its changed middle is shown as one replacement"));
            capReported = true;
        }

        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre])
        {
            pre++;
        }

        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[^(suf + 1)] == b[^(suf + 1)])
        {
            suf++;
        }

        // Do not split a surrogate pair.
        if (pre > 0 && pre < a.Length && char.IsLowSurrogate(a[pre]))
        {
            pre--;
        }

        while (suf > 0 && char.IsLowSurrogate(a[a.Length - suf]))
        {
            suf--;
        }

        FormatEditsFor(og, ng, 0, 0, pre, edits);
        AddRegion(og, ng, pre, a.Length - pre - suf, pre, b.Length - pre - suf, edits);
        FormatEditsFor(og, ng, a.Length - suf, b.Length - suf, suf, edits);
        return edits.ToImmutable();
    }

    internal static void AddRegion(ProjectedGroup og, ProjectedGroup ng, int oldStart, int oldLength, int newStart, int newLength, ImmutableArray<RegionEdit>.Builder edits)
    {
        if (oldLength == 0 && newLength == 0)
        {
            return;
        }

        edits.Add(new RegionEdit(
            oldStart,
            oldLength,
            newStart,
            newLength,
            og.Text.Substring(oldStart, oldLength),
            ng.Text.Substring(newStart, newLength),
            FormatRuns.Slice(og.Runs, oldStart, oldLength),
            FormatRuns.Slice(ng.Runs, newStart, newLength)));
    }

    /// <summary>Format-only edits for a stretch of text that is equal on both sides.</summary>
    private static void FormatEditsFor(ProjectedGroup og, ProjectedGroup ng, int oldStart, int newStart, int length, ImmutableArray<RegionEdit>.Builder edits)
    {
        if (length == 0 || (og.Runs.IsEmpty && ng.Runs.IsEmpty))
        {
            return;
        }

        foreach ((int off, int len) in FormatRuns.Differences(og.Runs, oldStart, ng.Runs, newStart, length))
        {
            string text = og.Text.Substring(oldStart + off, len);
            edits.Add(new RegionEdit(
                oldStart + off,
                len,
                newStart + off,
                len,
                text,
                text,
                FormatRuns.Slice(og.Runs, oldStart + off, len),
                FormatRuns.Slice(ng.Runs, newStart + off, len)));
        }
    }

    internal static ImmutableArray<AttributeEdit> AttributeEdits(ProjectedGroup og, ProjectedGroup ng)
    {
        var result = ImmutableArray.CreateBuilder<AttributeEdit>();
        foreach (ProjectedGroup.Member mo in og.Members)
        {
            foreach (ProjectedGroup.Member mn in ng.Members)
            {
                var keys = new SortedSet<string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> kv in mo.Attributes.Entries)
                {
                    keys.Add(kv.Key);
                }

                foreach (KeyValuePair<string, string> kv in mn.Attributes.Entries)
                {
                    keys.Add(kv.Key);
                }

                foreach (string key in keys)
                {
                    string? x = mo.Attributes.Get(key), y = mn.Attributes.Get(key);
                    if (!string.Equals(x, y, StringComparison.Ordinal))
                    {
                        result.Add(new AttributeEdit(mo.NodeId, mn.NodeId, key, x, y));
                    }
                }
            }
        }

        return result.ToImmutable();
    }
}
