using System.Text.RegularExpressions;
using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.GoldSets;

/// <summary>
/// The semantic rows: typing and resolved-value accuracy per gold typed kind, cascade and rename grouping, and the substantive
/// filter. The definitions are in docs/measurement.md. A gold unit is mapped to the engine's change that overlaps its spans most
/// (a renumbering, which has no spans, to the attribute change that moved that list label).
/// </summary>
internal static partial class SemanticScorer
{
    /// <summary>The gold typed kinds the engine has a type for, in row order. Modal, adverb and jurisdiction swaps are plain text changes.</summary>
    public static readonly string[] Kinds = ["amount", "date", "duration", "percentage", "term", "reference", "number"];

    private static readonly ChangeType[] Expected =
        [ChangeType.Amount, ChangeType.Date, ChangeType.Duration, ChangeType.Percentage, ChangeType.DefinedTerm, ChangeType.Reference, ChangeType.Numbering];

    /// <summary>Gold unit types that are not substantive: list renumbering, the references it caused, formatting, and splits and merges (paragraph structure, no words). Everything else is.</summary>
    private static readonly HashSet<string> NotSubstantive = ["renumber", "cascade-reference", "format"];

    [GeneratedRegex(@"\d+(?:\.\d+)*")]
    private static partial Regex Dotted();

    public static void Add(Counts c, Comparison cmp, ScoreInput input, PairLabels labels)
    {
        var byId = new Dictionary<int, Change>();
        foreach (Change ch in cmp.Changes)
        {
            byId[ch.Id] = ch;
        }

        var extent = new Dictionary<int, SChange>();
        foreach (SChange s in input.Changes)
        {
            extent[s.Id] = s;
        }

        // A pair in which the generator renamed the term twice (the second rename overwrote the first's pieces) has gold units whose
        // spans and values describe text that is not in the documents. Its rename units are left out of every semantic row.
        bool chain = labels.Units.Where(u => u.Group == "rename" && u.Typed is not null).Select(u => (u.Typed!.Old, u.Typed.New)).Distinct().Count() > 1;
        c.RenChain += chain ? 1 : 0;
        HashSet<int> skip = chain ? [.. labels.Units.Where(u => u.Group == "rename").Select(u => u.Id)] : [];

        // unit id -> the change that shows its old side and the one that shows its new side (the same change for a replacement; a deleted
        // and an inserted cell are two)
        var mapped = new Dictionary<int, (Change? Old, Change? New)>();
        foreach (Unit u in labels.Units)
        {
            if (skip.Contains(u.Id))
            {
                continue;
            }

            (Change? o, Change? n) = Map(u, cmp, input);
            if (o is not null || n is not null)
            {
                mapped[u.Id] = (o, n);
            }
        }

        // ---- typing and resolved values ----
        foreach (Unit u in labels.Units)
        {
            int k = u.Typed is null || skip.Contains(u.Id) ? -1 : Array.IndexOf(Kinds, u.Typed.Kind);
            if (k < 0)
            {
                continue;
            }

            c.TyUnits[k]++;
            if (!mapped.TryGetValue(u.Id, out (Change? Old, Change? New) m))
            {
                c.SemDiag.Add($"{labels.Id} unmatched {u.Type} {u.Typed!.Old} -> {u.Typed.New}");
                continue;
            }

            c.TyMatched[k]++;
            string shown = $"'{m.Old?.Old.Text}' -> '{m.New?.New.Text}'";
            if ((m.Old is null || m.Old.Type == Expected[k]) && (m.New is null || m.New.Type == Expected[k]))
            {
                c.TyOk[k]++;
            }
            else
            {
                c.SemDiag.Add($"{labels.Id} type {u.Type} {u.Typed!.Old} -> {u.Typed.New}: got {m.Old?.Type}/{m.New?.Type} {shown}");
            }

            if (Canon(k, u.Typed!.Old) == m.Old?.OldValue?.Canonical && Canon(k, u.Typed.New) == m.New?.NewValue?.Canonical)
            {
                c.ValOk[k]++;
            }
            else
            {
                c.SemDiag.Add($"{labels.Id} value {u.Type} {u.Typed.Old} -> {u.Typed.New}: got {m.Old?.OldValue?.Canonical} -> {m.New?.NewValue?.Canonical} from {shown}");
            }
        }

        // ---- groups ----
        Group(c, cmp, labels, mapped, "cascade", GroupKind.Cascade, ref c.CascGoldN, ref c.CascOursN, ref c.CascFoundN);
        if (!chain)
        {
            Group(c, cmp, labels, mapped, "rename", GroupKind.Rename, ref c.RenGoldN, ref c.RenOursN, ref c.RenFoundN);
        }

        // ---- substantive ----
        foreach (Unit u in labels.Units)
        {
            if (u.Type is "split" or "merge" || skip.Contains(u.Id))
            {
                continue;
            }

            bool expected = !NotSubstantive.Contains(u.Type);
            bool predicted = mapped.TryGetValue(u.Id, out (Change? Old, Change? New) shown) && ((shown.Old?.IsSubstantive ?? false) || (shown.New?.IsSubstantive ?? false));
            if (expected != predicted)
            {
                c.SemDiag.Add($"{labels.Id} substantive {u.Type}: expected {expected} got {predicted} ({(mapped.TryGetValue(u.Id, out (Change? Old, Change? New) mc) ? (mc.Old ?? mc.New)!.Kind + " '" + (mc.Old ?? mc.New)!.Old.Text + "' -> '" + (mc.Old ?? mc.New)!.New.Text + "'" : "no change")})");
            }

            if (expected && predicted)
            {
                c.SubTp++;
            }
            else if (!expected && predicted)
            {
                c.SubFp++;
            }
            else if (expected)
            {
                c.SubFn++;
            }
        }
    }

    private static void Group(Counts c, Comparison cmp, PairLabels labels, Dictionary<int, (Change? Old, Change? New)> mapped, string goldGroup, GroupKind kind, ref long goldN, ref long oursN, ref long foundN)
    {
        var goldSet = labels.Units.Where(u => u.Group == goldGroup).Select(u => u.Id).ToHashSet();
        if (goldSet.Count > 0)
        {
            goldN++;
        }

        var unitsOfChange = new Dictionary<int, List<int>>();
        foreach (KeyValuePair<int, (Change? Old, Change? New)> kv in mapped)
        {
            foreach (Change ch in new[] { kv.Value.Old, kv.Value.New }.Where(x => x is not null).DistinctBy(x => x!.Id)!)
            {
                if (!unitsOfChange.TryGetValue(ch.Id, out List<int>? l))
                {
                    l = [];
                    unitsOfChange[ch.Id] = l;
                }

                l.Add(kv.Key);
            }
        }

        foreach (ChangeGroup g in cmp.Groups.Where(g => g.Kind == kind))
        {
            oursN++;
            var covered = new HashSet<int>();
            foreach (int id in g.ChangeIds)
            {
                if (unitsOfChange.TryGetValue(id, out List<int>? us))
                {
                    covered.UnionWith(us);
                }
            }

            if (goldSet.Count > 0 && covered.SetEquals(goldSet))
            {
                foundN++;
            }
            else
            {
                c.SemDiag.Add($"{labels.Id} {goldGroup} group: ours {g.ChangeIds.Length} changes covering [{string.Join(",", covered.Order())}] gold [{string.Join(",", goldSet.Order())}] titles '{g.Title}'");
            }
        }
    }

    /// <summary>The canonical form of a gold value, made comparable with the engine's (a reference is its number, a label has no closing full stop).</summary>
    private static string Canon(int kind, string value) => Kinds[kind] switch
    {
        "reference" => Dotted().Match(value) is { Success: true } m ? m.Value : value,
        "number" => value.Trim().TrimEnd('.'),
        _ => value,
    };

    private static (Change?, Change?) Map(Unit u, Comparison cmp, ScoreInput input)
    {
        if (u.Type == "renumber" && u.Typed is not null)
        {
            string o = u.Typed.Old.Trim().TrimEnd('.'), n = u.Typed.New.Trim().TrimEnd('.');
            foreach (Change ch in cmp.Changes)
            {
                if (ch.Kind == ChangeKind.Attribute && ch.AttributeEdits.Any(e => e.Key == FormatKeys.ListLabel && e.OldValue?.Trim().TrimEnd('.') == o && e.NewValue?.Trim().TrimEnd('.') == n))
                {
                    return (ch, ch);
                }
            }

            return (null, null);
        }

        bool isMove = u.Type == "move";
        return (Best(u.Old, false, isMove, cmp, input), Best(u.New, true, isMove, cmp, input));
    }

    /// <summary>The change whose text extent overlaps the spans most (moves are matched only by gold moves), or null.</summary>
    private static Change? Best(List<Span> spans, bool isNew, bool isMove, Comparison cmp, ScoreInput input)
    {
        if (spans.Count == 0)
        {
            return null;
        }

        Change? best = null;
        long bestOverlap = 0;
        foreach (SChange s in input.Changes)
        {
            if ((s.Kind == SKind.Move) != isMove || s.Kind == SKind.NoExtent)
            {
                continue;
            }

            long overlap = 0;
            foreach (Iv v in s.Extent)
            {
                if (v.IsNew != isNew)
                {
                    continue;
                }

                foreach (Span sp in spans)
                {
                    if (sp.Node == v.Node)
                    {
                        long lo = Math.Max(v.Start, sp.Start), hi = Math.Min(v.End, sp.Start + sp.Len);
                        if (hi > lo)
                        {
                            overlap += hi - lo;
                        }
                    }
                }
            }

            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = cmp.Changes.First(x => x.Id == s.Id);
            }
        }

        return best;
    }
}
