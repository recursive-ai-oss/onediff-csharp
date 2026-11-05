using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OneDiff.Cleanup;
using OneDiff.Documents;
using OneDiff.Documents.Grouping;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Scoring;

namespace OneDiff.GoldSets;

/// <summary>
/// Data for a learned judge of the two decisions the rules make: whether an ambiguous pair of blocks is one block edited (the
/// scorer seam), and how the changes inside a clause group into units (the clause hook). <c>dump-model</c> writes every such
/// decision the pipeline meets on a gold set, with the gold answer; <c>score</c> can be given another procedure's answers
/// (<c>--cand-scores</c>, <c>--clause-answers</c>) and runs them through the unchanged pipeline, so that its rows are the
/// engine's rows. The texts a model reads mark the engine's word diff inline.
/// </summary>
internal static class ModelData
{
    private const char EditOpen = '\u00AB', EditClose = '\u00BB', UnitOpen = '\u27E6', UnitClose = '\u27E7', Gap = '\u00A6';

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---- keys and marks ----------------------------------------------------------------------------------------------

    private static string Struct(Node n) => (n.Attributes.Get(FormatKeys.ParagraphStyle) ?? "-") + "/" + (n.Attributes.Get(FormatKeys.ListLevel) ?? "-");

    public static string CandKey(Node o, Node n) => GoldJson.Sha256("c\0" + o.Text + "\0" + n.Text + "\0" + Struct(o) + "\0" + Struct(n));

    /// <summary>Both texts with the word diff marked inline: changed words in guillemets.</summary>
    public static (string Old, string New) MarkWords(string a, string b)
    {
        OneDiff.TextDiff d = OneDiff.Diff.Words(a, b);
        d = DiffCleanup.Semantic(d);
        var so = new StringBuilder();
        var sn = new StringBuilder();
        foreach (OneDiff.Edit e in d.Script)
        {
            switch (e.Kind)
            {
                case OneDiff.EditKind.Equal:
                    so.Append(d.OldSpan(e));
                    sn.Append(d.NewSpan(e));
                    break;
                case OneDiff.EditKind.Delete:
                    so.Append(EditOpen).Append(d.OldSpan(e)).Append(EditClose);
                    break;
                default:
                    sn.Append(EditOpen).Append(d.NewSpan(e)).Append(EditClose);
                    break;
            }
        }

        return (so.ToString(), sn.ToString());
    }

    private static string ClauseKey(ClauseQuery q)
    {
        var sb = new StringBuilder("g\0").Append(q.Old).Append('\0').Append(q.New).Append('\0');
        sb.Append(q.OldClauseStart).Append(',').Append(q.OldClauseEnd).Append(',').Append(q.NewClauseStart).Append(',').Append(q.NewClauseEnd);
        foreach (ClauseUnit u in q.Units)
        {
            sb.Append(';').Append(u.OldFrom).Append(',').Append(u.OldTo).Append(',').Append(u.NewFrom).Append(',').Append(u.NewTo);
        }

        return GoldJson.Sha256(sb.ToString());
    }

    /// <summary>One side of a clause with the units in ⟦ ⟧, the atomic edits in « », and ¦ between neighbouring units.</summary>
    private static string MarkClause(ClauseQuery q, bool oldSide)
    {
        string text = oldSide ? q.Old : q.New;
        int cs = oldSide ? q.OldClauseStart : q.NewClauseStart, ce = oldSide ? q.OldClauseEnd : q.NewClauseEnd;
        var marks = new List<(int Pos, int Order, int Seq, string Token)>();
        int seq = 0;
        int Clamp(int p) => Math.Clamp(p, cs, ce);
        void Range(int from, int to, int closeOrder, int openOrder, char open, char close)
        {
            from = Clamp(from);
            to = Clamp(to);
            if (from == to)
            {
                marks.Add((from, openOrder, seq++, new string([open, close])));
            }
            else
            {
                // At one position: edit close, unit close, gap marker, unit open, edit open.
                marks.Add((from, openOrder, seq++, open.ToString()));
                marks.Add((to, closeOrder, seq++, close.ToString()));
            }
        }

        for (int i = 0; i < q.Units.Length; i++)
        {
            ClauseUnit u = q.Units[i];
            Range(oldSide ? u.OldFrom : u.NewFrom, oldSide ? u.OldTo : u.NewTo, 1, 3, UnitOpen, UnitClose);
            foreach (ClauseEdit e in u.Edits)
            {
                Range(oldSide ? e.OldStart : e.NewStart, oldSide ? e.OldEnd : e.NewEnd, 0, 4, EditOpen, EditClose);
            }

            if (i + 1 < q.Units.Length)
            {
                // The gap marker follows the unit's close on this side.
                marks.Add((Clamp(oldSide ? u.OldTo : u.NewTo), 2, seq++, Gap.ToString()));
            }
        }

        marks.Sort((x, y) => x.Pos != y.Pos ? x.Pos.CompareTo(y.Pos) : x.Order != y.Order ? x.Order.CompareTo(y.Order) : x.Seq.CompareTo(y.Seq));
        var sb = new StringBuilder();
        int at = cs;
        foreach ((int pos, _, _, string token) in marks)
        {
            sb.Append(text, at, pos - at).Append(token);
            at = pos;
        }

        sb.Append(text, at, ce - at);
        return sb.ToString();
    }

    // ---- gold answers -------------------------------------------------------------------------------------------------

    internal sealed class PairContext(string id, string lang, PairLabels labels)
    {
        public string Id { get; } = id;

        public string Lang { get; } = lang;

        public PairLabels Labels { get; } = labels;

        public Dictionary<int, int> OldLink { get; } = Index(labels, old: true);

        public Dictionary<int, int> NewLink { get; } = Index(labels, old: false);

        private static Dictionary<int, int> Index(PairLabels l, bool old)
        {
            var d = new Dictionary<int, int>();
            for (int i = 0; i < l.Links.Count; i++)
            {
                foreach (int n in old ? l.Links[i].Old : l.Links[i].New)
                {
                    d[n] = i;
                }
            }

            return d;
        }

        /// <summary>The gold link two blocks share, or -1.</summary>
        public int SharedLink(int oldId, int newId) => OldLink.TryGetValue(oldId, out int a) && NewLink.TryGetValue(newId, out int b) && a == b ? a : -1;
    }

    private static int Overlap(int from, int to, int start, int len) => Math.Max(0, Math.Min(to, start + len) - Math.Max(from, start));

    private static (int Start, int End) Trim(string t, int from, int to)
    {
        from = Math.Clamp(from, 0, t.Length);
        to = Math.Clamp(to, from, t.Length);
        while (from < to && char.IsWhiteSpace(t[from]))
        {
            from++;
        }

        while (to > from && char.IsWhiteSpace(t[to - 1]))
        {
            to--;
        }

        return (from, to);
    }

    /// <summary>The gold join/whole answer for a clause of a one-node-to-one-node link; null when the link is not that.</summary>
    private static ClauseAnswer? GoldClause(PairContext ctx, ClauseQuery q)
    {
        if (q.Link.OldNodes.Length != 1 || q.Link.NewNodes.Length != 1)
        {
            return null;
        }

        int oldNode = q.Link.OldNodes[0], newNode = q.Link.NewNodes[0];
        var which = new int[q.Units.Length];
        for (int i = 0; i < which.Length; i++)
        {
            ClauseUnit u = q.Units[i];
            int best = -1, bestOverlap = 0;
            for (int g = 0; g < ctx.Labels.Units.Count; g++)
            {
                Unit gu = ctx.Labels.Units[g];
                int ov = 0;
                foreach (Span s in gu.Old.Where(s => s.Node == oldNode))
                {
                    ov += Overlap(u.OldFrom, u.OldTo, s.Start, s.Len);
                }

                foreach (Span s in gu.New.Where(s => s.Node == newNode))
                {
                    ov += Overlap(u.NewFrom, u.NewTo, s.Start, s.Len);
                }

                if (ov > bestOverlap)
                {
                    best = g;
                    bestOverlap = ov;
                }
            }

            which[i] = best;
        }

        var join = new bool[which.Length - 1];
        for (int i = 0; i < join.Length; i++)
        {
            join[i] = which[i] >= 0 && which[i] == which[i + 1];
        }

        bool whole = false;
        if (which[0] >= 0 && which.All(w => w == which[0]))
        {
            Unit gu = ctx.Labels.Units[which[0]];
            (int cos, int coe) = Trim(q.Old, q.OldClauseStart, q.OldClauseEnd);
            (int cns, int cne) = Trim(q.New, q.NewClauseStart, q.NewClauseEnd);
            whole = true;
            foreach (Span s in gu.Old.Where(s => s.Node == oldNode))
            {
                whole &= s.Start == cos && s.Start + s.Len == coe;
            }

            foreach (Span s in gu.New.Where(s => s.Node == newNode))
            {
                whole &= s.Start == cns && s.Start + s.Len == cne;
            }

            whole &= gu.Old.Count + gu.New.Count > 0;
        }

        return new ClauseAnswer(whole, join);
    }

    // ---- dump ---------------------------------------------------------------------------------------------------------

    private sealed class Recorder(PairContext ctx, ConcurrentQueue<string> sink) : IPairScorer
    {
        public double Score(Node oldNode, Node newNode)
        {
            double rules = RulesPairScorer.Instance.Score(oldNode, newNode);
            (string mo, string mn) = MarkWords(oldNode.Text, newNode.Text);
            int shared = ctx.SharedLink(oldNode.Id, newNode.Id);
            sink.Enqueue(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["t"] = "cand",
                ["pair"] = ctx.Id,
                ["lang"] = ctx.Lang,
                ["key"] = CandKey(oldNode, newNode),
                ["old"] = mo,
                ["new"] = mn,
                ["oldStruct"] = Struct(oldNode),
                ["newStruct"] = Struct(newNode),
                ["rules"] = Math.Round(rules, 6),
                ["label"] = shared >= 0 ? 1 : 0,
                ["gold"] = shared >= 0 ? ctx.Labels.Links[shared].Kind : "none",
            }, Json));
            return rules;
        }
    }

    private static readonly ConcurrentDictionary<CompareDocument, PairContext> Contexts = new(ReferenceEqualityComparer.Instance);

    public static int Dump(Dictionary<string, string> opt)
    {
        string data = Path.GetFullPath(opt.GetValueOrDefault("data") ?? throw new ArgumentException("--data is required"));
        string outFile = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        int limit = int.Parse(opt.GetValueOrDefault("limit") ?? "0", CultureInfo.InvariantCulture);
        string[] ids = [.. Directory.EnumerateFiles(Path.Combine(data, "pairs"), "*.labels.json").Select(p => Path.GetFileName(p)[..^".labels.json".Length]).OrderBy(x => x, StringComparer.Ordinal)];
        if (limit > 0)
        {
            ids = [.. ids.Take(limit)];
        }

        var lines = new ConcurrentQueue<string>[ids.Length];
        var skipped = 0;
        ClauseProbe.Hook = q =>
        {
            if (!Contexts.TryGetValue(q.OldDocument, out PairContext? ctx))
            {
                return null;
            }

            ClauseAnswer? gold = GoldClause(ctx, q);
            if (gold is null)
            {
                Interlocked.Increment(ref skipped);
                return null;
            }

            lines[Array.IndexOf(ids, ctx.Id)].Enqueue(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["t"] = "clause",
                ["pair"] = ctx.Id,
                ["lang"] = ctx.Lang,
                ["key"] = ClauseKey(q),
                ["old"] = MarkClause(q, oldSide: true),
                ["new"] = MarkClause(q, oldSide: false),
                ["units"] = q.Units.Length,
                ["rulesWhole"] = q.RulesWhole,
                ["rulesJoin"] = q.RulesJoin,
                ["whole"] = gold.Value.Whole,
                ["join"] = gold.Value.Join,
            }, Json));
            return null;
        };

        Parallel.For(0, ids.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            lines[i] = new ConcurrentQueue<string>();
            string id = ids[i];
            var labels = GoldJson.Read<PairLabels>(File.ReadAllBytes(Path.Combine(data, "pairs", id + ".labels.json")));
            var docs = GoldJson.Read<PairDocs>(File.ReadAllBytes(Path.Combine(data, "pairs", id + ".docs.json")));
            CompareDocument o = GoldJson.Build(docs.Old), n = GoldJson.Build(docs.New);
            var ctx = new PairContext(id, labels.Lang, labels);
            Contexts[o] = ctx;
            _ = Comparison.Of(o, n, Pipeline.Settings, new Recorder(ctx, lines[i]));
            Contexts.TryRemove(o, out _);
        });
        ClauseProbe.Hook = null;

        int cands = 0, clauses = 0;
        using var w = new StreamWriter(outFile, false, new UTF8Encoding(false));
        foreach (ConcurrentQueue<string> q in lines)
        {
            foreach (string l in q)
            {
                w.Write(l);
                w.Write('\n');
                if (l.StartsWith("{\"t\":\"cand\"", StringComparison.Ordinal))
                {
                    cands++;
                }
                else
                {
                    clauses++;
                }
            }
        }

        Console.WriteLine($"pairs {ids.Length}  candidate pairs {cands}  clauses {clauses}  clauses on split/merge links skipped {skipped}");
        return 0;
    }

    // ---- real pairs ---------------------------------------------------------------------------------------------------

    private static IEnumerable<(string Id, string Old, string New)> RealPairs(string root)
    {
        foreach (string old in Directory.EnumerateFiles(root, "old.txt", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            string dir = Path.GetDirectoryName(old)!;
            yield return (Path.GetFileName(Path.GetDirectoryName(dir)!) + "/" + Path.GetFileName(dir), old, Path.Combine(dir, "new.txt"));
        }
    }

    /// <summary>The same dump as for gold pairs, over real version pairs read as plain text; the gold columns are meaningless.</summary>
    public static int DumpReal(Dictionary<string, string> opt)
    {
        string root = opt.GetValueOrDefault("real") ?? throw new ArgumentException("--real is required");
        string outFile = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        var sink = new ConcurrentQueue<string>();
        ClauseProbe.Hook = q =>
        {
            if (q.Link.OldNodes.Length != 1 || q.Link.NewNodes.Length != 1)
            {
                return null;
            }

            sink.Enqueue(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["t"] = "clause", ["pair"] = "real", ["lang"] = "en", ["key"] = ClauseKey(q), ["old"] = MarkClause(q, true), ["new"] = MarkClause(q, false),
                ["units"] = q.Units.Length, ["rulesWhole"] = q.RulesWhole, ["rulesJoin"] = q.RulesJoin, ["whole"] = false, ["join"] = new bool[q.RulesJoin.Length],
            }, Json));
            return null;
        };
        int pairs = 0;
        foreach ((string id, string o, string n) in RealPairs(root))
        {
            var ctx = new PairContext(id, "en", new PairLabels(id, "en", 0, "", "real", [], [], [], [], [], []));
            _ = Comparison.Of(CompareDocument.FromText(File.ReadAllText(o), "old"), CompareDocument.FromText(File.ReadAllText(n), "new"), Pipeline.Settings, new Recorder(ctx, sink));
            pairs++;
        }

        ClauseProbe.Hook = null;
        File.WriteAllLines(outFile, sink);
        Console.WriteLine($"real pairs {pairs}  records {sink.Count}");
        return 0;
    }

    /// <summary>
    /// Runs every real pair under the rules and under the given answers and counts what differs: alignment links (a link is its kind and
    /// its nodes) and changes (kind and ranges). Unlabelled; it says how much the answers move, not whether they are right.
    /// </summary>
    public static int CompareReal(Dictionary<string, string> opt)
    {
        string root = opt.GetValueOrDefault("real") ?? throw new ArgumentException("--real is required");
        string outFile = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        _ = Install(opt, out LookupScorer? lookup);
        var rows = new List<Dictionary<string, object?>>();
        long linksTotal = 0, linksJudged = 0, linksOnlyRules = 0, linksOnlyTeacher = 0, changesTotal = 0, changesOnlyRules = 0, changesOnlyTeacher = 0, bad = 0;
        int pairsChanged = 0, pairs = 0;
        foreach ((string id, string o, string n) in RealPairs(root))
        {
            string ot = File.ReadAllText(o), nt = File.ReadAllText(n);
            Comparison rules = Comparison.Of(CompareDocument.FromText(ot, "old"), CompareDocument.FromText(nt, "new"), Pipeline.Settings);
            CompareDocument od = CompareDocument.FromText(ot, "old");
            Contexts[od] = new PairContext(id, "en", new PairLabels(id, "en", 0, "", "real", [], [], [], [], [], []));
            Comparison teach = Comparison.Of(od, CompareDocument.FromText(nt, "new"), Pipeline.Settings, lookup);
            static string L(AlignmentLink l) => $"{l.Kind}:{string.Join(",", l.OldNodes)}>{string.Join(",", l.NewNodes)}";
            static string C(Change c) => $"{c.Kind}|{string.Join(",", c.Old.Nodes)}:{c.Old.Start}+{c.Old.Length}|{string.Join(",", c.New.Nodes)}:{c.New.Start}+{c.New.Length}";
            var lr = new HashSet<string>(rules.Alignment.Links.Select(L), StringComparer.Ordinal);
            var lt = new HashSet<string>(teach.Alignment.Links.Select(L), StringComparer.Ordinal);
            var cr = new HashSet<string>(rules.Changes.Select(C), StringComparer.Ordinal);
            var ct = new HashSet<string>(teach.Changes.Select(C), StringComparer.Ordinal);
            int onlyR = lr.Count(x => !lt.Contains(x)), onlyT = lt.Count(x => !lr.Contains(x));
            int cOnlyR = cr.Count(x => !ct.Contains(x)), cOnlyT = ct.Count(x => !cr.Contains(x));
            int judged = rules.Alignment.Links.Count(l => l.Origin is LinkOrigin.Similar or LinkOrigin.Relocated);
            linksTotal += lr.Count;
            linksJudged += judged;
            linksOnlyRules += onlyR;
            linksOnlyTeacher += onlyT;
            changesTotal += cr.Count;
            changesOnlyRules += cOnlyR;
            changesOnlyTeacher += cOnlyT;
            bad += teach.Verification.Passed && teach.Verification.FallbackLevel == 0 ? 0 : 1;
            pairs++;
            pairsChanged += onlyR + onlyT + cOnlyR + cOnlyT > 0 ? 1 : 0;
            rows.Add(new Dictionary<string, object?> { ["pair"] = id, ["links"] = lr.Count, ["linksFromScorer"] = judged, ["linksOnlyRules"] = onlyR, ["linksOnlyTeacher"] = onlyT, ["changes"] = cr.Count, ["changesOnlyRules"] = cOnlyR, ["changesOnlyTeacher"] = cOnlyT, ["teacherVerified"] = teach.Verification.Passed && teach.Verification.FallbackLevel == 0 });
        }

        File.WriteAllLines(outFile, rows.Select(r => JsonSerializer.Serialize(r, Json)));
        Console.WriteLine($"real pairs {pairs}: links {linksTotal} (of which from the scorer stages {linksJudged}); only under the rules {linksOnlyRules}, only under the answers {linksOnlyTeacher}; changes {changesTotal}, only under the rules {changesOnlyRules}, only under the answers {changesOnlyTeacher}; pairs that differ at all {pairsChanged}; answers not verified at level 0: {bad}");
        ClauseProbe.Hook = null;
        Console.WriteLine(Summary(lookup));
        return 0;
    }

    // ---- answers into the pipeline -----------------------------------------------------------------------------------

    /// <summary>Scores pairs from a table of answers, the rules otherwise; counts the lookups that missed.</summary>
    internal sealed class LookupScorer(Dictionary<string, double> table) : IPairScorer
    {
        public int Hits;
        public int Misses;

        public double Score(Node oldNode, Node newNode)
        {
            if (table.TryGetValue(CandKey(oldNode, newNode), out double s))
            {
                Interlocked.Increment(ref Hits);
                return s;
            }

            Interlocked.Increment(ref Misses);
            return RulesPairScorer.Instance.Score(oldNode, newNode);
        }
    }

    /// <summary>The gold answer to every ambiguous pair (1 for a pair of one gold link, else 0): the head-room of a perfect judge.</summary>
    internal sealed class OracleScorer(PairContext ctx) : IPairScorer
    {
        public double Score(Node oldNode, Node newNode) => ctx.SharedLink(oldNode.Id, newNode.Id) >= 0 ? 1.0 : 0.0;
    }

    private static Dictionary<string, JsonElement> ReadTable(string file)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(file))
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonElement e = JsonDocument.Parse(line).RootElement.Clone();
            d[e.GetProperty("key").GetString()!] = e;
        }

        return d;
    }

    private static int clauseHits, clauseMisses;

    /// <summary>
    /// Reads <c>--cand-scores</c>, <c>--clause-answers</c> or the <c>--oracle-judge</c> flag and installs them; the returned factory
    /// gives the scorer for one pair (null: the default rules).
    /// </summary>
    public static Func<CompareDocument, PairLabels, IPairScorer?> Install(Dictionary<string, string> opt, out LookupScorer? lookup)
    {
        lookup = null;
        LookupScorer? shared = null;
        if (opt.TryGetValue("cand-scores", out string? cf))
        {
            var table = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach ((string k, JsonElement e) in ReadTable(cf))
            {
                table[k] = e.GetProperty("score").GetDouble();
            }

            lookup = shared = new LookupScorer(table);
        }

        bool oracleJudge = opt.ContainsKey("oracle-judge");
        bool oracleClauses = opt.ContainsKey("oracle-clauses");
        Dictionary<string, JsonElement>? clauses = opt.TryGetValue("clause-answers", out string? af) ? ReadTable(af) : null;
        if (clauses is not null || oracleClauses)
        {
            ClauseProbe.Hook = q =>
            {
                // Only the documents registered for the variant under test are answered; a baseline run beside it stays on the rules.
                if (!Contexts.TryGetValue(q.OldDocument, out PairContext? c))
                {
                    return null;
                }

                if (oracleClauses)
                {
                    return GoldClause(c, q);
                }

                if (clauses!.TryGetValue(ClauseKey(q), out JsonElement e))
                {
                    Interlocked.Increment(ref clauseHits);
                    return new ClauseAnswer(e.GetProperty("whole").GetBoolean(), [.. e.GetProperty("join").EnumerateArray().Select(x => x.GetBoolean())]);
                }

                Interlocked.Increment(ref clauseMisses);
                return null;
            };
        }

        return (doc, labels) =>
        {
            var ctx = new PairContext(labels.Id, labels.Lang, labels);
            Contexts[doc] = ctx;
            return oracleJudge ? new OracleScorer(ctx) : shared;
        };
    }

    public static string Summary(LookupScorer? lookup) =>
        (lookup is null ? string.Empty : $"scorer lookups: {lookup.Hits} answered, {lookup.Misses} not in the table (rules). ")
        + (clauseHits + clauseMisses == 0 ? string.Empty : $"clause lookups: {clauseHits} answered, {clauseMisses} not in the table (rules).");
}
