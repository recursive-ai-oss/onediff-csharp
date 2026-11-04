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
    public long SentRegions, SentCounted, SentMissed;
    public long UnitRegions, UnitCounted, UnitMissed;
    public long Pairs, Passed, Fallbacks;

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
        SentRegions += o.SentRegions;
        SentCounted += o.SentCounted;
        SentMissed += o.SentMissed;
        UnitRegions += o.UnitRegions;
        UnitCounted += o.UnitCounted;
        UnitMissed += o.UnitMissed;
        Pairs += o.Pairs;
        Passed += o.Passed;
        Fallbacks += o.Fallbacks;
    }
}

/// <summary>An interval of one node's text on one side, in node text coordinates.</summary>
internal readonly record struct Iv(bool IsNew, int Node, int Start, int End);

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

    public static Counts Score(Comparison cmp, PairLabels labels)
    {
        var c = new Counts { Pairs = 1, Passed = cmp.Verification.Passed ? 1 : 0, Fallbacks = cmp.Verification.FallbackLevel > 0 ? 1 : 0 };
        CompareDocument od = cmp.Old;
        CompareDocument nd = cmp.New;

        // ---- links ----
        var gold = new HashSet<string>();
        string Join(CompareDocument d, IEnumerable<int> ids) => string.Join('\u001E', ids.Select(i => d[i].Text));
        foreach (GoldLink l in labels.Links)
        {
            gold.Add(Key(LinkType(l.Kind, Join(od, l.Old), Join(nd, l.New)), l.Old, l.New));
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
        foreach (AlignmentLink l in cmp.Alignment.Links)
        {
            if (!l.OldNodes.All(i => IsContent(od[i].Kind)) || !l.NewNodes.All(i => IsContent(nd[i].Kind)))
            {
                continue;
            }

            string kind = l.Kind switch { LinkKind.Move => "move", LinkKind.Split => "split", LinkKind.Merge => "merge", _ => "match" };
            pred.Add(Key(LinkType(kind, Join(od, l.OldNodes), Join(nd, l.NewNodes)), l.OldNodes, l.NewNodes));
            l.OldNodes.ToList().ForEach(i => linkedOld.Add(i));
            l.NewNodes.ToList().ForEach(i => linkedNew.Add(i));
        }

        foreach (Node n in od.Nodes.Where(n => IsContent(n.Kind) && !linkedOld.Contains(n.Id)))
        {
            pred.Add(Key("unrelated", [n.Id], []));
        }

        foreach (Node n in nd.Nodes.Where(n => IsContent(n.Kind) && !linkedNew.Contains(n.Id)))
        {
            pred.Add(Key("unrelated", [], [n.Id]));
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
            }
        }

        foreach (string k in pred.Where(k => !gold.Contains(k)))
        {
            c.Fp[TypeIndex(k[..k.IndexOf('|', StringComparison.Ordinal)])]++;
        }

        // ---- boundaries ----
        string TextOf(bool isNew, int node) => (isNew ? nd : od)[node].Text;
        var goldBoundaries = new HashSet<(bool, int, int)>();
        var goldUnitIvs = new Dictionary<int, List<Iv>>();
        foreach (Unit u in labels.Units)
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

        // Predicted regions: one list of intervals per change, and per display group.
        var perChange = new Dictionary<int, List<Iv>>();
        foreach (Change ch in cmp.Changes)
        {
            if (ch.Kind is ChangeKind.Attribute or ChangeKind.Split or ChangeKind.Merge)
            {
                continue;
            }

            var ivs = new List<Iv>();
            Sides(ch.Old, false, od, ivs);
            Sides(ch.New, true, nd, ivs);
            perChange[ch.Id] = ivs;
        }

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
        var regions = new List<List<Iv>>();
        foreach (ChangeGroup g in cmp.Groups)
        {
            var all = new List<Iv>();
            foreach (int id in g.ChangeIds)
            {
                if (perChange.TryGetValue(id, out List<Iv>? ivs))
                {
                    all.AddRange(ivs);
                }
            }

            if (all.Count > 0)
            {
                regions.Add(all);
            }
        }

        foreach (GoldSentence s in labels.Sentences)
        {
            bool isNew = s.Side == "new";
            int touching = regions.Count(r => r.Any(v => v.IsNew == isNew && v.Node == s.Node && v.Start < s.Start + s.Len && v.End > s.Start));
            if (touching == 0)
            {
                c.SentMissed++;
            }
            else
            {
                c.SentCounted++;
                c.SentRegions += touching;
            }
        }

        foreach (KeyValuePair<int, List<Iv>> u in goldUnitIvs)
        {
            int touching = regions.Count(r => r.Any(v => u.Value.Any(g => g.IsNew == v.IsNew && g.Node == v.Node && v.Start < g.End && v.End > g.Start)));
            if (touching == 0)
            {
                c.UnitMissed++;
            }
            else
            {
                c.UnitCounted++;
                c.UnitRegions += touching;
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

    /// <summary>Maps a change side (a range of the nodes' joined text) back to intervals of single nodes.</summary>
    private static void Sides(ChangeSide side, bool isNew, CompareDocument doc, List<Iv> into)
    {
        if (!side.IsPresent)
        {
            return;
        }

        int offset = 0;
        int from = side.Start;
        int to = side.Start + side.Length;
        foreach (int id in side.Nodes)
        {
            string text = doc[id].Text;
            int lo = Math.Max(from, offset);
            int hi = Math.Min(to, offset + text.Length);
            if (hi > lo)
            {
                AddTrimmed(into, isNew, id, lo - offset, hi - offset, text);
            }

            offset += text.Length + 1;
        }
    }
}

/// <summary>A percentile bootstrap over documents with a fixed seed: documents are resampled with replacement and every metric is recomputed from the summed counts.</summary>
internal static class Bootstrap
{
    public const int Resamples = 10_000;
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
            result[m] = v.Count < Resamples * 0.9 ? (value, double.NaN, double.NaN) : (value, v[(int)Math.Floor(0.025 * (v.Count - 1))], v[(int)Math.Ceiling(0.975 * (v.Count - 1))]);
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
        defs.Add(("frag.sentence", c => c.SentCounted == 0 ? double.NaN : (double)c.SentRegions / c.SentCounted));
        defs.Add(("frag.unit", c => c.UnitCounted == 0 ? double.NaN : (double)c.UnitRegions / c.UnitCounted));
        defs.Add(("missed.sentence", c => c.SentCounted + c.SentMissed == 0 ? double.NaN : (double)c.SentMissed / (c.SentCounted + c.SentMissed)));
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
