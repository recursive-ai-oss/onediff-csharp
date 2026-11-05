using System.Collections.Immutable;
using System.Globalization;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.GoldSets;

/// <summary>Counts for one pair; sums over a document's pairs are what the bootstrap resamples.</summary>
internal sealed class Counts
{
    public static readonly string[] LinkTypes = ["same", "edited", "moved", "split", "merged", "unrelated"];

    public readonly long[] Tp = new long[6];
    public readonly long[] Fp = new long[6];
    public readonly long[] Fn = new long[6];
    public long BTp, BFp, BFn;
    public long SentChanges, SentRegions, SentCounted, SentMissed;
    public long UnitChanges, UnitRegions, UnitCounted, UnitMissed;
    public long Pairs, Passed, Fallbacks, NoDiff;

    // Changes that no gold unit explains ("spurious", for text read from pixels: recognition noise) and the ones flagged as possible OCR errors.
    public long ChangesTotal, ChangesSpurious, ChangesFlagged, FlaggedSpurious, OldChars;

    // Semantic rows (SemanticScorer): per gold typed kind, the units, the units matched to a change, the right types and the exact values.
    public readonly long[] TyUnits = new long[7];
    public readonly long[] TyMatched = new long[7];
    public readonly long[] TyOk = new long[7];
    public readonly long[] ValOk = new long[7];
    public long CascGoldN, CascOursN, CascFoundN, RenGoldN, RenOursN, RenFoundN;
    public long SubTp, SubFp, SubFn, RenChain;

    /// <summary>The changed sentences no change touched, for diagnosis; not summed.</summary>
    public List<string> Missed { get; } = [];

    /// <summary>The changes no gold unit explains, for diagnosis; not summed.</summary>
    public List<string> Noise { get; } = [];

    /// <summary>Semantic misses for diagnosis (typing, values, groups, substantive); not summed.</summary>
    public List<string> SemDiag { get; } = [];

    /// <summary>The link keys that differ from the gold links ("gold" = missed, "pred" = not in the gold), for diagnosis; not summed.</summary>
    public List<string> AlignErrors { get; } = [];

    public void Add(Counts o)
    {
        for (int i = 0; i < 6; i++)
        {
            Tp[i] += o.Tp[i];
            Fp[i] += o.Fp[i];
            Fn[i] += o.Fn[i];
        }

        BTp += o.BTp;
        BFp += o.BFp;
        BFn += o.BFn;
        SentChanges += o.SentChanges;
        SentRegions += o.SentRegions;
        SentCounted += o.SentCounted;
        SentMissed += o.SentMissed;
        UnitChanges += o.UnitChanges;
        UnitRegions += o.UnitRegions;
        UnitCounted += o.UnitCounted;
        UnitMissed += o.UnitMissed;
        Pairs += o.Pairs;
        Passed += o.Passed;
        Fallbacks += o.Fallbacks;
        NoDiff += o.NoDiff;
        ChangesTotal += o.ChangesTotal;
        ChangesSpurious += o.ChangesSpurious;
        ChangesFlagged += o.ChangesFlagged;
        FlaggedSpurious += o.FlaggedSpurious;
        OldChars += o.OldChars;
        for (int i = 0; i < 7; i++)
        {
            TyUnits[i] += o.TyUnits[i];
            TyMatched[i] += o.TyMatched[i];
            TyOk[i] += o.TyOk[i];
            ValOk[i] += o.ValOk[i];
        }

        CascGoldN += o.CascGoldN;
        CascOursN += o.CascOursN;
        CascFoundN += o.CascFoundN;
        RenGoldN += o.RenGoldN;
        RenOursN += o.RenOursN;
        RenFoundN += o.RenFoundN;
        SubTp += o.SubTp;
        SubFp += o.SubFp;
        SubFn += o.SubFn;
        RenChain += o.RenChain;
    }
}

/// <summary>An interval of one node's text on one side, in node text coordinates.</summary>
internal readonly record struct Iv(bool IsNew, int Node, int Start, int End);

/// <summary>
/// Fragmentation counts. A gold target (a changed sentence, or a gold unit's spans) is touched by every change whose text extent
/// overlaps it, and by every display region that holds such a change. The counts are kept apart on purpose: a region that spans a
/// whole paragraph is one region however many changes it holds, so only the change count says how finely the edits are shown.
/// </summary>
internal static class Fragmentation
{
    /// <param name="changeExtents">The text extent of each change, as intervals.</param>
    /// <param name="groups">Each display region as the indexes of the changes it holds.</param>
    /// <param name="target">The intervals of the gold target.</param>
    /// <returns>The number of changes and the number of regions that overlap the target.</returns>
    public static (int Changes, int Regions) Touching(IReadOnlyList<List<Iv>> changeExtents, IReadOnlyList<int[]> groups, IReadOnlyList<Iv> target)
    {
        var hit = new HashSet<int>();
        for (int i = 0; i < changeExtents.Count; i++)
        {
            if (changeExtents[i].Any(v => target.Any(g => g.IsNew == v.IsNew && g.Node == v.Node && v.Start < g.End && v.End > g.Start)))
            {
                hit.Add(i);
            }
        }

        int regions = groups.Count(m => m.Any(hit.Contains));
        return (hit.Count, regions);
    }
}

/// <summary>
/// Scores a comparison against a pair's labels. See docs/measurement.md for the definitions: link F1 per type over content
/// blocks, boundary F1 over trimmed change intervals, and fragmentation as display regions per changed gold sentence.
/// </summary>
internal static class PairScorer
{
    private static bool IsContent(NodeKind k) => k is NodeKind.Paragraph or NodeKind.ListItem;

    private static int TypeIndex(string name) => Array.IndexOf(Counts.LinkTypes, name);

    private static string LinkType(string kind, string oldText, string newText) => kind switch
    {
        "move" => "moved",
        "split" => "split",
        "merge" => "merged",
        _ => oldText == newText ? "same" : "edited",
    };

    /// <param name="noDiff">Whether the two documents of the pair are identical (see <see cref="GoldJson.SameNodes"/>).</param>
    public static Counts Score(Comparison cmp, PairLabels labels, bool noDiff = false) => Score(ScoreInput.From(cmp), labels, noDiff);

    /// <param name="input">A comparison in the gold documents' coordinates, from any producer.</param>
    /// <param name="labels">The pair's labels.</param>
    /// <param name="noDiff">Whether the two documents of the pair are identical (see <see cref="GoldJson.SameNodes"/>).</param>
    public static Counts Score(ScoreInput input, PairLabels labels, bool noDiff = false)
    {
        var c = new Counts { Pairs = 1, Passed = input.Passed ? 1 : 0, Fallbacks = input.FallbackLevel > 0 ? 1 : 0 };
        string[] oldText = input.OldText;
        string[] newText = input.NewText;

        // A pair whose two documents are identical has nothing to compare, whatever its labels say. The generator can undo a defined-
        // term rename with a second one in the same pair, leaving unit labels for text that did not change; those labels describe no
        // difference, so they are left out of the boundary and fragmentation counts (and the pairs are counted and reported).
        c.NoDiff = noDiff ? 1 : 0;

        // ---- links ----
        var gold = new HashSet<string>();
        string Join(string[] d, IEnumerable<int> ids) => string.Join('\u001E', ids.Select(i => d[i]));
        foreach (GoldLink l in labels.Links)
        {
            gold.Add(Key(LinkType(l.Kind, Join(oldText, l.Old), Join(newText, l.New)), l.Old, l.New));
        }

        foreach (int d in labels.Deleted)
        {
            gold.Add(Key("unrelated", [d], []));
        }

        foreach (int i in labels.Inserted)
        {
            gold.Add(Key("unrelated", [], [i]));
        }

        var pred = new HashSet<string>();
        var linkedOld = new HashSet<int>();
        var linkedNew = new HashSet<int>();
        foreach (SLink l in input.Links)
        {
            pred.Add(Key(LinkType(l.Kind, Join(oldText, l.Old), Join(newText, l.New)), l.Old, l.New));
            l.Old.ToList().ForEach(i => linkedOld.Add(i));
            l.New.ToList().ForEach(i => linkedNew.Add(i));
        }

        for (int id = 0; id < oldText.Length; id++)
        {
            if (input.OldContent[id] && !linkedOld.Contains(id))
            {
                pred.Add(Key("unrelated", [id], []));
            }
        }

        for (int id = 0; id < newText.Length; id++)
        {
            if (input.NewContent[id] && !linkedNew.Contains(id))
            {
                pred.Add(Key("unrelated", [], [id]));
            }
        }

        foreach (string k in gold)
        {
            int t = TypeIndex(k[..k.IndexOf('|', StringComparison.Ordinal)]);
            if (pred.Contains(k))
            {
                c.Tp[t]++;
            }
            else
            {
                c.Fn[t]++;
                c.AlignErrors.Add("gold " + k);
            }
        }

        foreach (string k in pred.Where(k => !gold.Contains(k)))
        {
            c.AlignErrors.Add("pred " + k);
            c.Fp[TypeIndex(k[..k.IndexOf('|', StringComparison.Ordinal)])]++;
        }

        // ---- boundaries ----
        string TextOf(bool isNew, int node) => (isNew ? newText : oldText)[node];
        var goldBoundaries = new HashSet<(bool, int, int)>();
        var goldUnitIvs = new Dictionary<int, List<Iv>>();
        foreach (Unit u in noDiff ? [] : labels.Units)
        {
            var ivs = new List<Iv>();
            foreach (Span s in u.Old)
            {
                AddTrimmed(ivs, false, s.Node, s.Start, s.Start + s.Len, TextOf(false, s.Node));
            }

            foreach (Span s in u.New)
            {
                AddTrimmed(ivs, true, s.Node, s.Start, s.Start + s.Len, TextOf(true, s.Node));
            }

            if (ivs.Count > 0)
            {
                goldUnitIvs[u.Id] = ivs;
                foreach (Iv v in ivs)
                {
                    goldBoundaries.Add((v.IsNew, v.Node, v.Start));
                    goldBoundaries.Add((v.IsNew, v.Node, v.End));
                }
            }
        }

        // Predicted units: one list of intervals per change (boundaries use the engine's own sides).
        var perChange = new Dictionary<int, List<Iv>>();
        foreach (SChange ch in input.Changes)
        {
            if (ch.Kind == SKind.NoExtent)
            {
                continue;
            }

            var ivs = new List<Iv>();
            foreach (Iv v in ch.Extent)
            {
                string text = TextOf(v.IsNew, v.Node);
                AddTrimmed(ivs, v.IsNew, v.Node, v.Start, v.End, text);
            }

            perChange[ch.Id] = ivs;
        }

        // Spurious changes: a text change that overlaps no gold unit, a move of a block no gold link moves. Attribute, split and merge
        // changes have no text and are left out. Pairs whose documents are identical have no gold units, so every change there is spurious.
        var movedGold = new HashSet<(bool, int)>();
        foreach (GoldLink l in labels.Links.Where(l => l.Kind == "move"))
        {
            l.Old.ToList().ForEach(i => movedGold.Add((false, i)));
            l.New.ToList().ForEach(i => movedGold.Add((true, i)));
        }

        foreach (SChange ch in input.Changes)
        {
            if (ch.Kind == SKind.NoExtent)
            {
                continue;
            }

            bool explained = ch.Kind == SKind.Move
                ? ch.Extent.Any(v => movedGold.Contains((v.IsNew, v.Node)))
                : perChange.TryGetValue(ch.Id, out List<Iv>? mine) && mine.Any(v => goldUnitIvs.Values.Any(u => u.Any(g => g.IsNew == v.IsNew && g.Node == v.Node && v.Start < g.End && v.End > g.Start)));
            c.ChangesTotal++;
            c.ChangesSpurious += explained ? 0 : 1;
            if (!explained && ch.Note is not null)
            {
                c.Noise.Add(ch.Note);
            }

            c.ChangesFlagged += ch.Flagged ? 1 : 0;
            c.FlaggedSpurious += ch.Flagged && !explained ? 1 : 0;
        }

        c.OldChars = oldText.Sum(static t => (long)t.Length);

        var predBoundaries = new HashSet<(bool, int, int)>();
        foreach (List<Iv> ivs in perChange.Values)
        {
            foreach (Iv v in ivs)
            {
                predBoundaries.Add((v.IsNew, v.Node, v.Start));
                predBoundaries.Add((v.IsNew, v.Node, v.End));
            }
        }

        foreach ((bool, int, int) b in goldBoundaries)
        {
            if (predBoundaries.Contains(b))
            {
                c.BTp++;
            }
            else
            {
                c.BFn++;
            }
        }

        c.BFp = predBoundaries.Count(b => !goldBoundaries.Contains(b));

        // ---- fragmentation ----
        // Units are the changes with a text extent. A move change spans its whole node, so it is the unit of a gold move and of nothing
        // else; the edits inside a moved node are changes of their own, counted by the text targets. Regions are the display groups over
        // the same changes.
        (List<List<Iv>> Extents, List<int[]> Groups) Build(bool movesOnly)
        {
            var extents = new List<List<Iv>>();
            var indexOfChange = new Dictionary<int, int>();
            foreach (SChange ch in input.Changes)
            {
                if ((ch.Kind == SKind.Move) == movesOnly && perChange.TryGetValue(ch.Id, out List<Iv>? ivs) && ivs.Count > 0)
                {
                    indexOfChange[ch.Id] = extents.Count;
                    extents.Add(ivs);
                }
            }

            var groups = new List<int[]>();
            foreach (int[] g in input.Groups)
            {
                int[] members = [.. g.Where(indexOfChange.ContainsKey).Select(id => indexOfChange[id])];
                if (members.Length > 0)
                {
                    groups.Add(members);
                }
            }

            return (extents, groups);
        }

        (List<List<Iv>> sentExtents, List<int[]> sentGroups) = Build(movesOnly: false);
        (List<List<Iv>> unitExtents, List<int[]> unitGroups) = Build(movesOnly: true);
        foreach (GoldSentence s in noDiff ? [] : labels.Sentences)
        {
            (int changes, int regions) = Fragmentation.Touching(sentExtents, sentGroups, [new Iv(s.Side == "new", s.Node, s.Start, s.Start + s.Len)]);
            if (changes == 0)
            {
                c.SentMissed++;
                c.Missed.Add($"{s.Side}:{s.Node} units=[{string.Join(",", s.Units.Select(id => labels.Units.First(x => x.Id == id).Type))}]");
            }
            else
            {
                c.SentCounted++;
                c.SentChanges += changes;
                c.SentRegions += regions;
            }
        }

        foreach (KeyValuePair<int, List<Iv>> u in goldUnitIvs)
        {
            bool isMove = labels.Units.First(x => x.Id == u.Key).Type == "move";
            (int changes, int regions) = isMove
                ? Fragmentation.Touching(unitExtents, unitGroups, u.Value)
                : Fragmentation.Touching(sentExtents, sentGroups, u.Value);
            if (changes == 0)
            {
                c.UnitMissed++;
            }
            else
            {
                c.UnitCounted++;
                c.UnitChanges += changes;
                c.UnitRegions += regions;
            }
        }

        return c;
    }

    private static string Key(string type, IEnumerable<int> olds, IEnumerable<int> news) =>
        type + "|" + string.Join(',', olds.OrderBy(x => x)) + "|" + string.Join(',', news.OrderBy(x => x));

    private static void AddTrimmed(List<Iv> into, bool isNew, int node, int start, int end, string text)
    {
        end = Math.Min(end, text.Length);
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (end > start)
        {
            into.Add(new Iv(isNew, node, start, end));
        }
    }
}

/// <summary>A percentile bootstrap over documents with a fixed seed: documents are resampled with replacement and every metric is recomputed from the summed counts.</summary>
internal static class Bootstrap
{
    /// <summary>Bootstrap resamples; 10,000 for every reported row. 0 skips the intervals, for quick sweeps that only compare point values.</summary>
    public static int Resamples { get; set; } = 10_000;
    public const ulong Seed = 20261104;

    /// <summary>The metric's value on summed counts, or NaN when its denominator is zero.</summary>
    public delegate double Metric(Counts sum);

    public static (double Value, double Lo, double Hi)[] RunAll(IReadOnlyList<Counts> docs, IReadOnlyList<Metric> metrics)
    {
        var total = new Counts();
        foreach (Counts d in docs)
        {
            total.Add(d);
        }

        var result = new (double, double, double)[metrics.Count];
        var values = metrics.Select(_ => new List<double>(Resamples)).ToArray();
        var rng = new Rng(Seed);
        if (docs.Count > 0)
        {
            for (int b = 0; b < Resamples; b++)
            {
                var sum = new Counts();
                for (int i = 0; i < docs.Count; i++)
                {
                    sum.Add(docs[rng.Next(docs.Count)]);
                }

                for (int m = 0; m < metrics.Count; m++)
                {
                    double v = metrics[m](sum);
                    if (!double.IsNaN(v))
                    {
                        values[m].Add(v);
                    }
                }
            }
        }

        for (int m = 0; m < metrics.Count; m++)
        {
            List<double> v = values[m];
            v.Sort();
            double value = metrics[m](total);
            result[m] = Resamples == 0 || v.Count < Resamples * 0.9 ? (value, double.NaN, double.NaN) : (value, v[(int)Math.Floor(0.025 * (v.Count - 1))], v[(int)Math.Ceiling(0.975 * (v.Count - 1))]);
        }

        return result;
    }

    public static double F1(long tp, long fp, long fn) => tp + fp + fn == 0 ? double.NaN : 2.0 * tp / (2.0 * tp + fp + fn);

    public static double Precision(long tp, long fp) => tp + fp == 0 ? double.NaN : (double)tp / (tp + fp);

    public static double Recall(long tp, long fn) => tp + fn == 0 ? double.NaN : (double)tp / (tp + fn);
}

internal sealed record MetricRow(string Set, string SetHash, string Scope, string Metric, double Value, double? Lo, double? Hi, int Docs, int Pairs, string Pipeline);

internal static class Report
{
    /// <summary>All metric rows for a group of pairs (already grouped by document).</summary>
    public static IEnumerable<MetricRow> Rows(string set, string setHash, string scope, IReadOnlyList<Counts> docs, string pipeline)
    {
        var defs = new List<(string Name, Bootstrap.Metric M)>();
        for (int i = 0; i < 6; i++)
        {
            int t = i;
            string n = Counts.LinkTypes[t];
            defs.Add(($"align.F1.{n}", c => Bootstrap.F1(c.Tp[t], c.Fp[t], c.Fn[t])));
        }

        defs.Add(("align.P.moved", c => Bootstrap.Precision(c.Tp[2], c.Fp[2])));
        defs.Add(("align.R.moved", c => Bootstrap.Recall(c.Tp[2], c.Fn[2])));
        defs.Add(("boundary.F1", c => Bootstrap.F1(c.BTp, c.BFp, c.BFn)));
        defs.Add(("boundary.P", c => Bootstrap.Precision(c.BTp, c.BFp)));
        defs.Add(("boundary.R", c => Bootstrap.Recall(c.BTp, c.BFn)));
        defs.Add(("frag.sentence", c => c.SentCounted == 0 ? double.NaN : (double)c.SentChanges / c.SentCounted));
        defs.Add(("frag.unit", c => c.UnitCounted == 0 ? double.NaN : (double)c.UnitChanges / c.UnitCounted));
        defs.Add(("regions.sentence", c => c.SentCounted == 0 ? double.NaN : (double)c.SentRegions / c.SentCounted));
        defs.Add(("regions.unit", c => c.UnitCounted == 0 ? double.NaN : (double)c.UnitRegions / c.UnitCounted));
        defs.Add(("missed.sentence", c => c.SentCounted + c.SentMissed == 0 ? double.NaN : (double)c.SentMissed / (c.SentCounted + c.SentMissed)));
        for (int i = 0; i < SemanticScorer.Kinds.Length; i++)
        {
            int k = i;
            string n = SemanticScorer.Kinds[k];
            defs.Add(($"type.acc.{n}", c => c.TyMatched[k] == 0 ? double.NaN : (double)c.TyOk[k] / c.TyMatched[k]));
            defs.Add(($"value.acc.{n}", c => c.TyMatched[k] == 0 ? double.NaN : (double)c.ValOk[k] / c.TyMatched[k]));
        }

        defs.Add(("type.acc.all", c => c.TyMatched.Sum() == 0 ? double.NaN : (double)c.TyOk.Sum() / c.TyMatched.Sum()));
        defs.Add(("value.acc.all", c => c.TyMatched.Sum() == 0 ? double.NaN : (double)c.ValOk.Sum() / c.TyMatched.Sum()));
        defs.Add(("type.coverage", c => c.TyUnits.Sum() == 0 ? double.NaN : (double)c.TyMatched.Sum() / c.TyUnits.Sum()));
        defs.Add(("cascade.P", c => Bootstrap.Precision(c.CascFoundN, c.CascOursN - c.CascFoundN)));
        defs.Add(("cascade.R", c => Bootstrap.Recall(c.CascFoundN, c.CascGoldN - c.CascFoundN)));
        defs.Add(("rename.P", c => Bootstrap.Precision(c.RenFoundN, c.RenOursN - c.RenFoundN)));
        defs.Add(("rename.R", c => Bootstrap.Recall(c.RenFoundN, c.RenGoldN - c.RenFoundN)));
        defs.Add(("substantive.P", c => Bootstrap.Precision(c.SubTp, c.SubFp)));
        defs.Add(("substantive.R", c => Bootstrap.Recall(c.SubTp, c.SubFn)));
        defs.Add(("noise.changes.share", c => c.ChangesTotal == 0 ? double.NaN : (double)c.ChangesSpurious / c.ChangesTotal));
        defs.Add(("noise.changes.per10kchars", c => c.OldChars == 0 ? double.NaN : 10_000.0 * c.ChangesSpurious / c.OldChars));
        defs.Add(("ocr.flag.P", c => c.ChangesFlagged == 0 ? double.NaN : (double)c.FlaggedSpurious / c.ChangesFlagged));
        defs.Add(("ocr.flag.R", c => c.ChangesSpurious == 0 ? double.NaN : (double)c.FlaggedSpurious / c.ChangesSpurious));
        defs.Add(("ocr.flag.share", c => c.ChangesTotal == 0 ? double.NaN : (double)c.ChangesFlagged / c.ChangesTotal));
        defs.Add(("pairs.nodiff", c => c.NoDiff));
        defs.Add(("pairs.renamechain", c => c.RenChain));
        defs.Add(("verify.pass", c => c.Pairs == 0 ? double.NaN : (double)c.Passed / c.Pairs));
        defs.Add(("verify.fallback", c => c.Pairs == 0 ? double.NaN : (double)c.Fallbacks / c.Pairs));
        int pairs = (int)docs.Sum(d => d.Pairs);
        (double Value, double Lo, double Hi)[] res = Bootstrap.RunAll(docs, [.. defs.Select(d => d.M)]);
        for (int i = 0; i < defs.Count; i++)
        {
            if (!double.IsNaN(res[i].Value))
            {
                yield return new MetricRow(set, setHash, scope, defs[i].Name, res[i].Value, double.IsNaN(res[i].Lo) ? null : res[i].Lo, double.IsNaN(res[i].Hi) ? null : res[i].Hi, docs.Count, pairs, pipeline);
            }
        }
    }

    public static string Line(MetricRow r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.Scope,-22} {r.Metric,-18} {r.Value,7:F4}  [{r.Lo ?? double.NaN,7:F4}, {r.Hi ?? double.NaN,7:F4}]  docs={r.Docs} pairs={r.Pairs}");
}
