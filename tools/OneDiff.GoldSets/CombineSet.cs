using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OneDiff.Documents;
using OneDiff.Documents.Combine;
using OneDiff.Documents.Model;
using OneDiff.Documents.Verification;

namespace OneDiff.GoldSets;

/// <summary>One reviewer of a combine case: the copy and the labels of the original → copy pair.</summary>
internal sealed record CombineReviewer(string Name, DocFile Copy, PairLabels Labels);

/// <summary>An original and its reviewers' copies, with the exact labels of every reviewer's edits.</summary>
internal sealed record CombineCase(string Id, string Lang, int BaseDoc, string BaseHash, string Role, string Plan, DocFile Original, List<CombineReviewer> Reviewers);

/// <summary>
/// The combine set: originals of the eval documents only, each with 2 to 4 reviewers whose edits are drawn from the span-bearing
/// edit types. Shared edits are made by replaying one random stream on several makers; conflicts by replaying the stream that
/// picks the target while each maker draws its own new value. Gold attribution, sharing and conflicts are derived from the
/// units' old-side spans and new text in the labels, not from the plan, so a replay that diverged is still labelled exactly.
/// </summary>
internal static class CombineSet
{
    private static readonly string[] SpanTypes =
        ["term-swap", "value-amount", "value-date", "value-duration", "value-percentage", "clause-rewrite", "sentence-delete", "para-delete"];

    private static readonly string[] ValueTypes = SpanTypes[..5];

    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record ManifestFile(string Path, string Sha256);

    private sealed record SplitEntry(string Lang, int Doc, string Role, string BaseHash);

    private sealed record Manifest(string Schema, string Name, ulong Seed, int DocsPerLang, List<string> Langs, string SplitSha256, int Cases, Dictionary<string, int> CasesByReviewers, Dictionary<string, int> CasesByPlan, List<ManifestFile> Files);

    public static int Generate(Dictionary<string, string> opt)
    {
        string outDir = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        ulong seed = ulong.Parse(opt.GetValueOrDefault("seed") ?? Program.DefaultSeed.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int docsPerLang = int.Parse(opt.GetValueOrDefault("docs-per-lang") ?? Program.DefaultDocsPerLang.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        string[] langs = opt.TryGetValue("langs", out string? l) ? l.Split(',') : Lexicon.Codes;
        if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
        {
            throw new IOException("output directory is not empty: " + outDir);
        }

        var split = new List<SplitEntry>();
        var files = new List<ManifestFile>();
        var byReviewers = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byPlan = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string lang in langs)
        {
            Lexicon lex = Lexicon.For(lang);
            // The same seeded shuffle as the synthetic set, so the eval documents are the same documents.
            var order = Enumerable.Range(0, docsPerLang).ToList();
            new Rng(Rng.Mix(seed, "split:" + lang)).Shuffle(order);
            int evalDocs = Math.Max(1, docsPerLang * Program.EvalDocs / Program.DefaultDocsPerLang);
            int devDocs = Math.Max(1, docsPerLang * Program.DevDocs / Program.DefaultDocsPerLang);
            var roleOf = new string[docsPerLang];
            for (int i = 0; i < order.Count; i++)
            {
                roleOf[order[i]] = i < evalDocs ? "eval" : i < evalDocs + devDocs ? "dev" : "train";
            }

            for (int doc = 0; doc < docsPerLang; doc++)
            {
                ulong docSeed = Rng.Mix(Rng.Mix(seed, "doc:" + lang), doc);
                var db = new DocBuilder(lex);
                GDoc baseDoc = db.BuildDocument(new Rng(docSeed), "base");
                string baseHash = GoldJson.Sha256(GoldJson.Write(new PairMaker(lex, db, baseDoc, new Rng(0)).Flatten(baseDoc.Clone(), "base").File));
                split.Add(new SplitEntry(lang, doc, roleOf[doc], baseHash));
                if (roleOf[doc] != "eval")
                {
                    continue;
                }

                for (int n = 2; n <= 4; n++)
                {
                    CombineCase c = Make(lex, db, baseDoc, baseHash, lang, doc, docSeed, n);
                    byReviewers[n.ToString(CultureInfo.InvariantCulture)] = byReviewers.GetValueOrDefault(n.ToString(CultureInfo.InvariantCulture)) + 1;
                    byPlan[c.Plan] = byPlan.GetValueOrDefault(c.Plan) + 1;
                    files.Add(Write(outDir, "cases/" + c.Id + ".json", GoldJson.Write(c)));
                }
            }
        }

        byte[] splitBytes = GoldJson.Write(split);
        files.Add(Write(outDir, "split.json", splitBytes));
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var manifest = new Manifest("onediff.combineset/1", "combine", seed, docsPerLang, [.. langs], GoldJson.Sha256(splitBytes), files.Count(f => f.Path.StartsWith("cases/", StringComparison.Ordinal)), byReviewers.ToDictionary(x => x.Key, x => x.Value), byPlan.ToDictionary(x => x.Key, x => x.Value), files);
        byte[] manifestBytes = GoldJson.Write(manifest);
        File.WriteAllBytes(Path.Combine(outDir, "manifest.json"), manifestBytes);
        string setHash = GoldJson.Sha256(manifestBytes);
        File.WriteAllText(Path.Combine(outDir, "manifest.sha256"), setHash + "\n");
        Console.WriteLine($"cases={manifest.Cases} files={files.Count} set-sha256={setHash}");
        foreach ((string k, int v) in byReviewers)
        {
            Console.WriteLine($"  {k} reviewers: {v} cases");
        }

        foreach ((string k, int v) in byPlan)
        {
            Console.WriteLine($"  plan {k}: {v} cases");
        }

        return 0;
    }

    private static CombineCase Make(Lexicon lex, DocBuilder db, GDoc baseDoc, string baseHash, string lang, int doc, ulong docSeed, int n)
    {
        string id = $"{lang}-{doc:000}-c{n}";
        var plan = new Rng(Rng.Mix(docSeed, $"plan:{n}"));
        // Modes: 0 shared edits, 1 a conflict, 2 neither, 3 both (four reviewers: two pairs).
        int mode = plan.Next(n == 4 ? 4 : 3);
        var order = Enumerable.Range(0, n).ToList();
        plan.Shuffle(order);
        HashSet<int> shared = [], conflicting = [];
        if (mode == 0 || mode == 3)
        {
            int size = mode == 3 ? 2 : plan.Next(2, n);
            foreach (int k in order.Take(size))
            {
                shared.Add(k);
            }
        }

        if (mode == 1 || mode == 3)
        {
            int size = mode == 3 ? 2 : plan.Next(2, n);
            foreach (int k in (mode == 3 ? order.Skip(2) : order).Take(size))
            {
                conflicting.Add(k);
            }
        }

        int sharedCount = shared.Count > 0 ? plan.Next(1, 2) : 0;
        string[] sharedTypes = [.. Enumerable.Range(0, sharedCount).Select(_ => plan.Pick(SpanTypes))];
        string conflictType = plan.Pick(ValueTypes);
        string planName = mode switch { 0 => "shared", 1 => "conflict", 2 => "independent", _ => "shared+conflict" };

        DocFile? original = null;
        string originalHash = "";
        var reviewers = new List<CombineReviewer>();
        for (int k = 0; k < n; k++)
        {
            var own = new Rng(Rng.Mix(docSeed, $"own:{n}:{k}"));
            var maker = new PairMaker(lex, db, baseDoc, own);
            if (conflicting.Contains(k))
            {
                maker.UseRng(new Rng(Rng.Mix(docSeed, $"conflict:{n}")));
                maker.ValueRng = new Rng(Rng.Mix(docSeed, $"cvalue:{n}:{k}"));
                maker.Apply(conflictType);
                maker.ValueRng = null;
            }

            if (shared.Contains(k))
            {
                maker.UseRng(new Rng(Rng.Mix(docSeed, $"shared:{n}")));
                foreach (string t in sharedTypes)
                {
                    maker.Apply(t);
                }
            }

            maker.UseRng(own);
            int want = own.Next(1, 3);
            for (int attempt = 0, done = 0; attempt < 10 && done < want; attempt++)
            {
                if (maker.Apply(own.Pick(SpanTypes)))
                {
                    done++;
                }
            }

            (PairDocs docs, PairLabels labels) = maker.Finish($"{id}-r{k}", lang, doc, baseHash, "eval");
            string h = GoldJson.Sha256(GoldJson.Write(docs.Old.Nodes));
            if (original is null)
            {
                original = docs.Old with { Name = id + ".original" };
                originalHash = h;
            }
            else if (h != originalHash)
            {
                throw new InvalidOperationException($"{id}: reviewer {k} did not start from the same original");
            }

            reviewers.Add(new CombineReviewer($"{id}.r{k}", docs.New, labels));
        }

        return new CombineCase(id, lang, doc, baseHash, "eval", planName, original!, reviewers);
    }

    // ---- digest and timing -----------------------------------------------------------------------------------------

    /// <summary>Hashes the combination JSON and report page of the first N cases (twice, to show two runs agree) and prints one digest for the lot.</summary>
    public static int Digest(Dictionary<string, string> opt)
    {
        string data = Path.GetFullPath(opt.GetValueOrDefault("data") ?? throw new ArgumentException("--data is required"));
        int count = int.Parse(opt.GetValueOrDefault("count") ?? "20", CultureInfo.InvariantCulture);
        string[] paths = [.. Directory.GetFiles(Path.Combine(data, "cases"), "*.json").Order(StringComparer.Ordinal)];
        var lines = new List<string>();
        bool same = true;
        foreach (string path in paths.Take(count))
        {
            CombineCase c = GoldJson.Read<CombineCase>(File.ReadAllBytes(path));
            string[] once = new string[2];
            for (int run = 0; run < 2; run++)
            {
                Combination comb = Combination.Of(GoldJson.Build(c.Original), c.Reviewers.Select(r => GoldJson.Build(r.Copy)).ToList(), Pipeline.Settings);
                once[run] = GoldJson.Sha256(CombineJson.Write(comb)) + " " + GoldJson.Sha256(ChangeReport.From(comb).ToHtml());
            }

            same &= once[0] == once[1];
            lines.Add(c.Id + " " + once[0]);
            Console.WriteLine(lines[^1]);
        }

        Console.WriteLine($"digest {GoldJson.Sha256(string.Join("\n", lines))} cases {lines.Count} two-runs-identical {same}");
        return same ? 0 : 1;
    }

    /// <summary>
    /// Times a combination of reviewers' copies of a large synthetic contract (default 100 pages of 40 paragraphs, 4 reviewers of 75
    /// edited paragraphs each, a tenth of them shared): reading is not included, only <see cref="Combination.Of(CompareDocument, IReadOnlyList{CompareDocument}, CompareSettings?, Documents.Scoring.IPairScorer?, CancellationToken)"/>.
    /// </summary>
    public static int Time(Dictionary<string, string> opt)
    {
        int pages = int.Parse(opt.GetValueOrDefault("pages") ?? "100", CultureInfo.InvariantCulture);
        int reviewers = int.Parse(opt.GetValueOrDefault("reviewers") ?? "4", CultureInfo.InvariantCulture);
        int edits = int.Parse(opt.GetValueOrDefault("edits") ?? "75", CultureInfo.InvariantCulture);
        int reps = int.Parse(opt.GetValueOrDefault("reps") ?? "3", CultureInfo.InvariantCulture);
        string[] words = ["party", "shall", "deliver", "payment", "within", "days", "notice", "agreement", "terminate", "liability", "goods", "services", "invoice", "warranty", "confidential", "damages", "term", "renew", "supplier", "customer"];
        var rng = new Rng(20261104);
        int paragraphs = pages * 40;
        string[][] baseText = new string[paragraphs][];
        for (int i = 0; i < paragraphs; i++)
        {
            baseText[i] = [.. Enumerable.Range(0, 60).Select(_ => rng.Pick(words)), (rng.Next(10, 99)).ToString(CultureInfo.InvariantCulture) + " days"];
        }

        CompareDocument Build(string name, string[][] text)
        {
            var b = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(name);
            b.Begin(NodeKind.Section);
            foreach (string[] p in text)
            {
                b.Add(NodeKind.Paragraph, new NodeContent { Text = string.Join(' ', p) + "." });
            }

            b.End();
            return b.Build();
        }

        CompareDocument original = Build("original", baseText);
        var shared = Enumerable.Range(0, edits / 10).Select(_ => (P: rng.Next(paragraphs), W: rng.Next(60), V: rng.Pick(words) + "x")).ToList();
        var copies = new List<CompareDocument>();
        for (int r = 0; r < reviewers; r++)
        {
            string[][] text = [.. baseText.Select(p => (string[])p.Clone())];
            foreach ((int p, int w, string v) in shared)
            {
                text[p][w] = v;
            }

            var own = new Rng(Rng.Mix(20261104, "time:" + r));
            for (int e = 0; e < edits; e++)
            {
                text[own.Next(paragraphs)][own.Next(60)] = own.Pick(words) + "y" + r.ToString(CultureInfo.InvariantCulture);
            }

            copies.Add(Build("copy" + r, text));
        }

        var times = new List<double>();
        Combination? last = null;
        for (int rep = 0; rep < reps + 1; rep++)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            last = Combination.Of(original, copies, CompareSettings.Legal);
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (rep > 0)
            {
                times.Add(ms);
            }
        }

        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[] json = CombineJson.Write(last!);
        string html = ChangeReport.From(last!).ToHtml();
        double writeMs = System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
        Console.WriteLine($"pages {pages} paragraphs {paragraphs} reviewers {reviewers} cores {Environment.ProcessorCount}");
        Console.WriteLine($"combined changes {last!.Changes.Length} (reviewer changes {last.Comparisons.Sum(c => c.Changes.Length)}), shared {last.Changes.Count(c => c.Reviewers.Length > 1)}, conflicts {last.Conflicts.Length}, verification fallback {last.Verification.FallbackLevel}");
        Console.WriteLine($"combine (compare {reviewers} copies + combine + check) ms: {string.Join(", ", times.Select(t => t.ToString("F0", CultureInfo.InvariantCulture)))} (first run excluded as warm-up)");
        Console.WriteLine($"json {json.Length} bytes + report {html.Length} chars in {writeMs.ToString("F0", CultureInfo.InvariantCulture)} ms");
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        ImmutableArray<VerificationFailure> f = last.Check();
        Console.WriteLine($"Check() alone: {System.Diagnostics.Stopwatch.GetElapsedTime(t2).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms, failures {f.Length}");
        return 0;
    }

    private static ManifestFile Write(string root, string rel, byte[] bytes)
    {
        string path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return new ManifestFile(rel, GoldJson.Sha256(bytes));
    }

    // ---- score -----------------------------------------------------------------------------------------------------

    private sealed class GoldEdit
    {
        public required string Key;
        public required List<Span> Old;
        public SortedSet<int> Reviewers = [];
    }

    private sealed record Pred(int Node, int Start, int End, int[] Reviewers, int Change, int Conflict);

    // Counts per cluster (one original document): the vector below.
    private const int AttrTotal = 0, AttrOk = 1, SharedGold = 2, SharedFound = 3, SharedPred = 4, SharedPredOk = 5, ConflictGold = 6, ConflictFound = 7, ConflictPred = 8, ConflictPredOk = 9,
        Cases = 10, CasesClean = 11, Reviewers = 12, Changes = 13, Conflicts = 14, SharedChanges = 15, N = 16;

    private static bool Overlaps(Span a, int node, int start, int end) => a.Node == node && a.Start < end && start < a.Start + a.Len;

    public static int Score(Dictionary<string, string> opt)
    {
        string data = Path.GetFullPath(opt.GetValueOrDefault("data") ?? throw new ArgumentException("--data is required"));
        string outFile = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        int resamples = int.Parse(opt.GetValueOrDefault("resamples") ?? "10000", CultureInfo.InvariantCulture);
        int limit = int.Parse(opt.GetValueOrDefault("limit") ?? "0", CultureInfo.InvariantCulture);
        string setHash = File.ReadAllText(Path.Combine(data, "manifest.sha256")).Trim();
        string[] paths = [.. Directory.GetFiles(Path.Combine(data, "cases"), "*.json").Order(StringComparer.Ordinal)];
        if (limit > 0)
        {
            paths = paths[..Math.Min(limit, paths.Length)];
        }

        var results = new (string Cluster, string Lang, long[] Counts, List<string> Failures)[paths.Length];
        Parallel.For(0, paths.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            CombineCase c = GoldJson.Read<CombineCase>(File.ReadAllBytes(paths[i]));
            var failures = new List<string>();
            long[] counts = ScoreCase(c, failures);
            results[i] = ($"{c.Lang}-{c.BaseDoc:000}", c.Lang, counts, failures);
        });

        var rows = new List<object>();
        var clusters = results.GroupBy(r => r.Cluster, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (Lang: g.First().Lang, Counts: Sum(g.Select(x => x.Counts)))).ToList();
        void Emit(string scope, List<(string Lang, long[] Counts)> cs)
        {
            (string Name, Func<long[], double> M)[] metrics =
            [
                ("attribution.accuracy", c => Ratio(c[AttrOk], c[AttrTotal])),
                ("shared.precision", c => Ratio(c[SharedPredOk], c[SharedPred])),
                ("shared.recall", c => Ratio(c[SharedFound], c[SharedGold])),
                ("conflict.precision", c => Ratio(c[ConflictPredOk], c[ConflictPred])),
                ("conflict.recall", c => Ratio(c[ConflictFound], c[ConflictGold])),
                ("invertible.rate", c => Ratio(c[CasesClean], c[Cases])),
            ];
            long[] total = Sum(cs.Select(x => x.Counts));
            var rng = new Rng(20261104);
            var values = metrics.Select(_ => new List<double>()).ToArray();
            for (int b = 0; b < resamples && cs.Count > 0; b++)
            {
                long[] sum = new long[N];
                for (int k = 0; k < cs.Count; k++)
                {
                    long[] x = cs[rng.Next(cs.Count)].Counts;
                    for (int j = 0; j < N; j++)
                    {
                        sum[j] += x[j];
                    }
                }

                for (int m = 0; m < metrics.Length; m++)
                {
                    double v = metrics[m].M(sum);
                    if (!double.IsNaN(v))
                    {
                        values[m].Add(v);
                    }
                }
            }

            for (int m = 0; m < metrics.Length; m++)
            {
                values[m].Sort();
                double v = metrics[m].M(total);
                List<double> s = values[m];
                bool ci = resamples > 0 && s.Count >= resamples * 0.9;
                rows.Add(new
                {
                    scope,
                    metric = metrics[m].Name,
                    value = double.IsNaN(v) ? (double?)null : Math.Round(v, 6),
                    lo = ci ? Math.Round(s[(int)Math.Floor(0.025 * (s.Count - 1))], 6) : (double?)null,
                    hi = ci ? Math.Round(s[(int)Math.Ceiling(0.975 * (s.Count - 1))], 6) : (double?)null,
                    docs = cs.Count,
                    num = metrics[m].Name switch
                    {
                        "attribution.accuracy" => total[AttrOk],
                        "shared.precision" => total[SharedPredOk],
                        "shared.recall" => total[SharedFound],
                        "conflict.precision" => total[ConflictPredOk],
                        "conflict.recall" => total[ConflictFound],
                        _ => total[CasesClean],
                    },
                    den = metrics[m].Name switch
                    {
                        "attribution.accuracy" => total[AttrTotal],
                        "shared.precision" => total[SharedPred],
                        "shared.recall" => total[SharedGold],
                        "conflict.precision" => total[ConflictPred],
                        "conflict.recall" => total[ConflictGold],
                        _ => total[Cases],
                    },
                });
            }
        }

        Emit("all", clusters);
        foreach (string lang in clusters.Select(c => c.Lang).Distinct().Order(StringComparer.Ordinal))
        {
            Emit("lang:" + lang, [.. clusters.Where(c => c.Lang == lang)]);
        }

        long[] all = Sum(results.Select(r => r.Counts));
        string[] failuresAll = [.. results.SelectMany(r => r.Failures).Order(StringComparer.Ordinal)];
        var report = new
        {
            set = "combine",
            setHash,
            resamples,
            seed = 20261104,
            cases = results.Length,
            reviewers = all[Reviewers],
            combinedChanges = all[Changes],
            sharedChanges = all[SharedChanges],
            conflicts = all[Conflicts],
            casesInvertibleAtFallback0 = all[CasesClean],
            failures = failuresAll.Take(200).ToArray(),
            failureCount = failuresAll.Length,
            rows,
        };
        File.WriteAllText(outFile, JsonSerializer.Serialize(report, ReportOptions) + "\n");
        Console.WriteLine($"set {setHash} cases {results.Length} invertible at fallback 0: {all[CasesClean]}/{all[Cases]} failures {failuresAll.Length}");
        foreach (dynamic row in rows.Take(6))
        {
            Console.WriteLine($"  {row.metric}: {row.value} [{row.lo}, {row.hi}] ({row.num}/{row.den})");
        }

        return failuresAll.Length == 0 ? 0 : 1;
    }

    private static double Ratio(long a, long b) => b == 0 ? double.NaN : (double)a / b;

    private static long[] Sum(IEnumerable<long[]> xs)
    {
        long[] s = new long[N];
        foreach (long[] x in xs)
        {
            for (int i = 0; i < N; i++)
            {
                s[i] += x[i];
            }
        }

        return s;
    }

    private static long[] ScoreCase(CombineCase c, List<string> failures)
    {
        long[] k = new long[N];
        CompareDocument original = GoldJson.Build(c.Original);
        var copies = c.Reviewers.Select(r => GoldJson.Build(r.Copy)).ToList();
        Combination comb = Combination.Of(original, copies, Pipeline.Settings);
        k[Cases] = 1;
        k[Reviewers] = copies.Count;
        k[Changes] = comb.Changes.Length;
        k[SharedChanges] = comb.Changes.Count(x => x.Reviewers.Length > 1);
        k[Conflicts] = comb.Conflicts.Length;
        bool clean = comb.Verification.Passed && comb.Verification.FallbackLevel == 0 && comb.Comparisons.All(x => x.Verification.FallbackLevel == 0);
        ImmutableCheck(comb, c.Id, failures, ref clean);
        k[CasesClean] = clean ? 1 : 0;

        // Gold: distinct edits by (old spans, new text), with the reviewers who made exactly that edit.
        var goldByKey = new SortedDictionary<string, GoldEdit>(StringComparer.Ordinal);
        for (int r = 0; r < c.Reviewers.Count; r++)
        {
            CombineReviewer rv = c.Reviewers[r];
            foreach (Unit u in rv.Labels.Units.Where(u => u.Old.Count > 0 && Array.IndexOf(SpanTypes, u.Type) >= 0))
            {
                string newText = string.Concat(u.New.Select(s => rv.Copy.Nodes[s.Node].Text.Substring(s.Start, s.Len)));
                string key = string.Join(';', u.Old.Select(s => $"{s.Node}:{s.Start}+{s.Len}")) + "|" + newText;
                if (!goldByKey.TryGetValue(key, out GoldEdit? g))
                {
                    goldByKey[key] = g = new GoldEdit { Key = key, Old = u.Old };
                }

                g.Reviewers.Add(r);
            }
        }

        List<GoldEdit> gold = [.. goldByKey.Values];

        // Predicted atomic edits with their reviewers.
        var preds = new List<Pred>();
        foreach (CombinedChange cc in comb.Changes)
        {
            Change ch = cc.Change;
            if (ch.Old.Nodes.IsDefaultOrEmpty || ch.Old.Nodes.Length != 1)
            {
                continue;
            }

            int node = ch.Old.Nodes[0];
            if (!ch.Edits.IsDefaultOrEmpty)
            {
                for (int i = 0; i < ch.Edits.Length; i++)
                {
                    RegionEdit e = ch.Edits[i];
                    preds.Add(new Pred(node, e.OldStart, e.OldEnd, [.. cc.EditReviewers[i]], cc.Id, cc.ConflictId));
                }
            }
            else if (ch.Kind == ChangeKind.Delete && !ch.New.IsPresent)
            {
                preds.Add(new Pred(node, 0, original[node].Text.Length, [.. cc.Reviewers], cc.Id, cc.ConflictId));
            }
        }

        bool Over(GoldEdit g, Pred p) => g.Old.Any(s => Overlaps(s, p.Node, p.Start, p.End));

        // Attribution and sharing.
        var found = new HashSet<GoldEdit>();
        foreach (GoldEdit g in gold)
        {
            List<Pred> hits = [.. preds.Where(p => Over(g, p) && p.Reviewers.Any(g.Reviewers.Contains))];
            bool ok = hits.Count > 0 && hits.All(p => p.Reviewers.SequenceEqual(g.Reviewers));
            k[AttrTotal]++;
            k[AttrOk] += ok ? 1 : 0;
            if (g.Reviewers.Count >= 2)
            {
                k[SharedGold]++;
                k[SharedFound] += ok ? 1 : 0;
            }

            if (ok)
            {
                found.Add(g);
            }
        }

        foreach (Pred p in preds.Where(p => p.Reviewers.Length >= 2))
        {
            k[SharedPred]++;
            k[SharedPredOk] += gold.Any(g => g.Reviewers.Count >= 2 && g.Reviewers.SequenceEqual(p.Reviewers) && Over(g, p)) ? 1 : 0;
        }

        // Conflicts: gold components of overlapping edits of different reviewers.
        int[] comp = Enumerable.Range(0, gold.Count).ToArray();
        int Find(int x) => comp[x] == x ? x : comp[x] = Find(comp[x]);
        for (int a = 0; a < gold.Count; a++)
        {
            for (int b = a + 1; b < gold.Count; b++)
            {
                bool differentReviewers = gold[a].Reviewers.Any(r => gold[b].Reviewers.Any(q => q != r));
                if (differentReviewers && gold[a].Old.Any(sa => gold[b].Old.Any(sb => Overlaps(sa, sb.Node, sb.Start, sb.Start + sb.Len))))
                {
                    comp[Find(a)] = Find(b);
                }
            }
        }

        var components = Enumerable.Range(0, gold.Count).GroupBy(Find).Where(g => g.Count() >= 2).Select(g => g.ToHashSet()).ToList();
        k[ConflictGold] = components.Count;
        var componentFound = new bool[components.Count];
        foreach (Conflict cf in comb.Conflicts)
        {
            // The gold edits that the conflict's changes touch.
            var touched = new HashSet<int>();
            foreach (Pred p in preds.Where(p => p.Conflict == cf.Id))
            {
                for (int g = 0; g < gold.Count; g++)
                {
                    if (Over(gold[g], p))
                    {
                        touched.Add(g);
                    }
                }
            }

            bool tp = false;
            for (int ci = 0; ci < components.Count; ci++)
            {
                if (components[ci].Count(touched.Contains) >= 2)
                {
                    tp = true;
                    componentFound[ci] = true;
                }
            }

            k[ConflictPred]++;
            k[ConflictPredOk] += tp ? 1 : 0;
        }

        k[ConflictFound] = componentFound.Count(x => x);
        return k;
    }

    private static void ImmutableCheck(Combination comb, string id, List<string> failures, ref bool clean)
    {
        foreach (VerificationFailure f in comb.Check())
        {
            failures.Add($"{id}: {f.Code} {f.Message}");
            clean = false;
        }

        foreach (Diagnostic d in comb.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            failures.Add($"{id}: {d.Code} {d.Message}");
            clean = false;
        }
    }
}
