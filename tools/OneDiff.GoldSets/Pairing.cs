using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OneDiff.GoldSets;

/// <summary>The counts of one base document in a form that can be written down and read back (see <see cref="Pairing"/>).</summary>
internal sealed record CountsDump(
    string Key, long[] Tp, long[] Fp, long[] Fn, long BTp, long BFp, long BFn, long SentChanges, long SentRegions, long SentCounted, long SentMissed,
    long UnitChanges, long UnitRegions, long UnitCounted, long UnitMissed, long Pairs, long Passed, long Fallbacks, long NoDiff);

/// <summary>
/// Paired comparison of two tools on the same documents. <c>score --dump-docs FILE</c> writes the per-document counts of the all-pairs
/// scope; <c>pair</c> resamples the documents once (the same draws for both tools) and reports, for each row, both values with their
/// own intervals and the interval of the difference. A row is "better" when the interval of the difference excludes zero in the
/// good direction, and "ceiling" when both tools' own intervals contain the best possible value.
/// </summary>
internal static class Pairing
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly string[] Rows =
    [
        "align.F1.same", "align.F1.edited", "align.F1.moved", "align.F1.split", "align.F1.merged", "align.F1.unrelated",
        "boundary.F1", "boundary.P", "boundary.R", "frag.sentence", "frag.unit", "missed.sentence",
    ];

    public static CountsDump ToDump(string key, Counts c) => new(
        key, c.Tp, c.Fp, c.Fn, c.BTp, c.BFp, c.BFn, c.SentChanges, c.SentRegions, c.SentCounted, c.SentMissed, c.UnitChanges, c.UnitRegions,
        c.UnitCounted, c.UnitMissed, c.Pairs, c.Passed, c.Fallbacks, c.NoDiff);

    public static void Write(string path, List<CountsDump> docs) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(docs, Options));

    private static Counts FromDump(CountsDump d)
    {
        var c = new Counts { BTp = d.BTp, BFp = d.BFp, BFn = d.BFn, SentChanges = d.SentChanges, SentRegions = d.SentRegions, SentCounted = d.SentCounted, SentMissed = d.SentMissed, UnitChanges = d.UnitChanges, UnitRegions = d.UnitRegions, UnitCounted = d.UnitCounted, UnitMissed = d.UnitMissed, Pairs = d.Pairs, Passed = d.Passed, Fallbacks = d.Fallbacks, NoDiff = d.NoDiff };
        Array.Copy(d.Tp, c.Tp, 6);
        Array.Copy(d.Fp, c.Fp, 6);
        Array.Copy(d.Fn, c.Fn, 6);
        return c;
    }

    /// <summary>Which direction is better, and the best possible value, for a row.</summary>
    private static (bool HigherIsBetter, double Ideal) Direction(string row) => row switch
    {
        "missed.sentence" => (false, 0.0),
        "frag.sentence" or "frag.unit" => (false, 1.0),
        _ => (true, 1.0),
    };

    public static int Compare(Dictionary<string, string> opt)
    {
        List<CountsDump> a = JsonSerializer.Deserialize<List<CountsDump>>(File.ReadAllBytes(opt["a"]), Options)!;
        List<CountsDump> b = JsonSerializer.Deserialize<List<CountsDump>>(File.ReadAllBytes(opt["b"]), Options)!;
        if (a.Count != b.Count || !a.Select(x => x.Key).SequenceEqual(b.Select(x => x.Key)))
        {
            throw new InvalidDataException("the two dumps are not over the same documents");
        }

        Bootstrap.Resamples = int.Parse(opt.GetValueOrDefault("resamples") ?? "10000", CultureInfo.InvariantCulture);
        var ca = a.Select(FromDump).ToList();
        var cb = b.Select(FromDump).ToList();
        Dictionary<string, Bootstrap.Metric> defs = Report.Definitions().ToDictionary(d => d.Name, d => d.M);
        string nameA = opt.GetValueOrDefault("name-a") ?? "a", nameB = opt.GetValueOrDefault("name-b") ?? "b";
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Row | {nameA} | {nameB} | {nameA} − {nameB} | Verdict |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (string row in Rows)
        {
            Bootstrap.Metric m = defs[row];
            (double Value, double Lo, double Hi) ra = Bootstrap.RunAll(ca, [m])[0], rb = Bootstrap.RunAll(cb, [m])[0];
            (double d, double lo, double hi) = Difference(ca, cb, m);
            (bool higher, double ideal) = Direction(row);
            bool better = higher ? lo > 0 : hi < 0;
            bool worse = higher ? hi < 0 : lo > 0;
            bool aIdeal = Reaches(ra, ideal), bIdeal = Reaches(rb, ideal);
            string verdict = better ? "strictly better" : worse ? "WORSE" : aIdeal && bIdeal ? "ceiling (both at the best value within noise)" : "tie (interval includes 0)";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row} | {ra.Value:F3} [{ra.Lo:F3}, {ra.Hi:F3}] | {rb.Value:F3} [{rb.Lo:F3}, {rb.Hi:F3}] | {d:+0.000;-0.000} [{lo:+0.000;-0.000}, {hi:+0.000;-0.000}] | {verdict} |");
        }

        Console.Write(sb);
        return 0;
    }

    /// <summary>Whether the interval reaches the best possible value (to the third decimal, as the tables are printed).</summary>
    private static bool Reaches((double Value, double Lo, double Hi) r, double ideal) => r.Lo <= ideal + 0.0005 && r.Hi >= ideal - 0.0005;

    private static (double Mean, double Lo, double Hi) Difference(List<Counts> a, List<Counts> b, Bootstrap.Metric m)
    {
        var rng = new Rng(Bootstrap.Seed);
        var diffs = new List<double>(Bootstrap.Resamples);
        for (int r = 0; r < Bootstrap.Resamples; r++)
        {
            var sa = new Counts();
            var sbb = new Counts();
            for (int i = 0; i < a.Count; i++)
            {
                int k = rng.Next(a.Count);
                sa.Add(a[k]);
                sbb.Add(b[k]);
            }

            double va = m(sa), vb = m(sbb);
            if (!double.IsNaN(va) && !double.IsNaN(vb))
            {
                diffs.Add(va - vb);
            }
        }

        diffs.Sort();
        var ta = new Counts();
        var tb = new Counts();
        a.ForEach(ta.Add);
        b.ForEach(tb.Add);
        return (m(ta) - m(tb), diffs[(int)Math.Floor(0.025 * (diffs.Count - 1))], diffs[(int)Math.Ceiling(0.975 * (diffs.Count - 1))]);
    }
}
