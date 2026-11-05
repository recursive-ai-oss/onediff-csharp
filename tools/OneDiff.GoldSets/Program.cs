using System.Globalization;
using System.Text;
using System.Text.Json;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Scoring;
using OneDiff.Documents.Serialization;
using OneDiff.Nlp;

namespace OneDiff.GoldSets;

/// <summary>
/// The gold-set tool: <c>generate</c> writes the synthetic pairs with exact labels, <c>score</c> runs a pipeline over them and
/// reports the gold-set rows with bootstrap intervals, <c>check-split</c> proves two data directories share no base document.
/// </summary>
internal static class Program
{
    private const string Usage = """
        OneDiff.GoldSets generate --out DIR [--role eval|dev|train] [--seed N] [--docs-per-lang N] [--langs en,de,...] [--verify]
        OneDiff.GoldSets score --data DIR --out FILE [--settings legal] [--oracle-alignment] [--cmp DIR | --changes DIR] [--blocks DIR [--fuzzy-blocks]] [--body-only] [--dump-docs FILE] [--flag-threshold T] [--save-cmp DIR] [--pipeline NAME] [--jobs N] [--limit N] [--ids FILE] [--resamples N]
                    [--cand-scores FILE] [--clause-answers FILE] [--oracle-judge] [--oracle-clauses] [--vs-rules]
        OneDiff.GoldSets check-split --a DIR --b DIR
        OneDiff.GoldSets pair --a DUMP --b DUMP [--name-a X] [--name-b Y] [--resamples N]   (DUMP from score --dump-docs)
        OneDiff.GoldSets nlp-gate --data DIR [--jobs N] [--show N] [--digest-only --count N]
        OneDiff.GoldSets dump-model --data DIR --out FILE.jsonl [--jobs N] [--limit N]
        OneDiff.GoldSets dump-real --real DIR --out FILE.jsonl
        OneDiff.GoldSets compare-real --real DIR --out FILE [--cand-scores FILE] [--clause-answers FILE]
        OneDiff.GoldSets generate-combine --out DIR [--seed N] [--docs-per-lang N] [--langs en,de,...]
        OneDiff.GoldSets score-combine --data DIR --out FILE [--jobs N] [--limit N] [--resamples N]

          generate    writes DIR/pairs/<id>.docs.json and <id>.labels.json for the documents of the chosen role, split.json (every
                      document and its role), manifest.json and manifest.sha256 (the set hash). Same seed, same bytes.
          score       compares every pair with the reference pipeline (or reads DIR/<id>.cmp.json from --cmp, or the tracked-changes
                      readings DIR/<id>.changes.json from --changes) and writes the rows. Comparisons made from rendered files (PDF, DOCX)
                      and other tools' output are mapped to the gold nodes through DIR/<id>.blocks.json (--blocks); --changes needs it. Text recognized
                      from pixels differs from the gold's by recognition errors, so --fuzzy-blocks matches nodes to blocks by similarity and
                      carries offsets over by a character alignment.
          generate-combine writes the combine set: for each eval document 2, 3 and 4 reviewers' copies of it with exact labels (cases/<id>.json).
          score-combine runs the combination over a combine set and reports invertibility, attribution, sharing and conflict rows.
          digest-combine hashes the combination outputs of the first N cases (--data DIR --count N); time-combine times a 100-page combine.
          check-split exits 1 when the two directories contain the same base document.
        """;

    public const ulong DefaultSeed = 20261104;
    public const int DefaultDocsPerLang = 60;
    public const int EvalDocs = 30;
    public const int DevDocs = 6;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        Dictionary<string, string> opt = Options(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "generate" => Generate(opt),
                "score" => Score(opt),
                "check-split" => CheckSplit(opt),
                "generate-combine" => CombineSet.Generate(opt),
                "score-combine" => CombineSet.Score(opt),
                "digest-combine" => CombineSet.Digest(opt),
                "time-combine" => CombineSet.Time(opt),
                "explain" => Explain(opt),
                "pair" => Pairing.Compare(opt),
                "dump-model" => ModelData.Dump(opt),
                "dump-real" => ModelData.DumpReal(opt),
                "compare-real" => ModelData.CompareReal(opt),
                "real-gate" => RealGate(opt),
                "nlp-gate" => NlpGate.Run(opt),
                "time-nlp" => NlpGate.Time(opt),
                _ => Fail("unknown command " + args[0]),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("unexpected " + args[i]);
            }

            string key = args[i][2..];
            d[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        return d;
    }

    // ---- generate --------------------------------------------------------------------------------------------------

    private sealed record SplitEntry(string Lang, int Doc, string Role, string BaseHash);

    private sealed record ManifestFile(string Path, string Sha256);

    private sealed record Manifest(
        string Schema, string Name, ulong Seed, int DocsPerLang, string Role, List<string> Langs, Dictionary<string, int> RoleDocs,
        string SplitSha256, int Pairs, Dictionary<string, int> PairsByLang, Dictionary<string, int> PairsByEdit, List<ManifestFile> Files);

    private static int Generate(Dictionary<string, string> opt)
    {
        string outDir = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        string role = opt.GetValueOrDefault("role") ?? "eval";
        ulong seed = ulong.Parse(opt.GetValueOrDefault("seed") ?? DefaultSeed.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        int docsPerLang = int.Parse(opt.GetValueOrDefault("docs-per-lang") ?? DefaultDocsPerLang.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        string[] langs = opt.TryGetValue("langs", out string? l) ? l.Split(',') : Lexicon.Codes;
        bool verify = opt.ContainsKey("verify");
        if (role is not ("eval" or "dev" or "train"))
        {
            throw new ArgumentException("--role must be eval, dev or train");
        }

        if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
        {
            throw new IOException("output directory is not empty: " + outDir);
        }

        string pairsDir = Path.Combine(outDir, "pairs");
        Directory.CreateDirectory(pairsDir);
        var split = new List<SplitEntry>();
        var files = new List<ManifestFile>();
        var byLang = new Dictionary<string, int>();
        var byEdit = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int verified = 0, failed = 0;

        foreach (string lang in langs)
        {
            Lexicon lex = Lexicon.For(lang);
            // The split is a seeded shuffle of the document numbers, so it does not depend on which role is written.
            var order = Enumerable.Range(0, docsPerLang).ToList();
            new Rng(Rng.Mix(seed, "split:" + lang)).Shuffle(order);
            int evalDocs = Math.Max(1, docsPerLang * EvalDocs / DefaultDocsPerLang);
            int devDocs = Math.Max(1, docsPerLang * DevDocs / DefaultDocsPerLang);
            var roleOf = new string[docsPerLang];
            for (int i = 0; i < order.Count; i++)
            {
                roleOf[order[i]] = i < evalDocs ? "eval" : i < evalDocs + devDocs ? "dev" : "train";
            }

            int langIndex = Array.IndexOf(Lexicon.Codes, lang);
            for (int doc = 0; doc < docsPerLang; doc++)
            {
                ulong docSeed = Rng.Mix(Rng.Mix(seed, "doc:" + lang), doc);
                var db = new DocBuilder(lex);
                GDoc baseDoc = db.BuildDocument(new Rng(docSeed), "base");
                string baseHash = GoldJson.Sha256(GoldJson.Write(new PairMaker(lex, db, baseDoc, new Rng(0)).Flatten(baseDoc.Clone(), "base").File));
                split.Add(new SplitEntry(lang, doc, roleOf[doc], baseHash));
                if (roleOf[doc] != role)
                {
                    continue;
                }

                for (int k = 0; k <= 4; k++)
                {
                    string id = k == 0 ? $"{lang}-{doc:000}-m" : $"{lang}-{doc:000}-s{k}";
                    var pr = new Rng(Rng.Mix(docSeed, "pair:" + k));
                    var maker = new PairMaker(lex, db, baseDoc, pr);
                    if (k == 0)
                    {
                        MixedEdits(maker, pr);
                    }
                    else
                    {
                        string type = EditTypes.All[(doc * 4 + k - 1 + langIndex * 3) % EditTypes.All.Length];
                        int want = pr.Next(1, 3);
                        int done = 0;
                        for (int attempt = 0; attempt < 8 && done < want; attempt++)
                        {
                            if (maker.Apply(type))
                            {
                                done++;
                            }
                        }

                        if (done == 0)
                        {
                            throw new InvalidOperationException($"{id}: no place for a {type} edit");
                        }
                    }

                    (PairDocs docs, PairLabels labels) = maker.Finish(id, lang, doc, baseHash, role);
                    foreach (string e in labels.Edits)
                    {
                        byEdit[e] = byEdit.GetValueOrDefault(e) + 1;
                    }

                    byLang[lang] = byLang.GetValueOrDefault(lang) + 1;
                    files.Add(Write(outDir, "pairs/" + id + ".docs.json", GoldJson.Write(docs)));
                    files.Add(Write(outDir, "pairs/" + id + ".labels.json", GoldJson.Write(labels)));
                    if (verify)
                    {
                        Comparison cmp = Comparison.Of(GoldJson.Build(docs.Old), GoldJson.Build(docs.New), Pipeline.Settings);
                        if (cmp.Verification.Passed && cmp.Verification.FallbackLevel == 0)
                        {
                            verified++;
                        }
                        else
                        {
                            failed++;
                            Console.Error.WriteLine($"verify failed: {id} (fallback level {cmp.Verification.FallbackLevel})");
                        }
                    }
                }
            }
        }

        byte[] splitBytes = GoldJson.Write(split);
        files.Add(Write(outDir, "split.json", splitBytes));
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var roleDocs = split.Where(s => s.Role == role).GroupBy(s => s.Lang).ToDictionary(g => g.Key, g => g.Count());
        var manifest = new Manifest("onediff.goldset/1", "synth", seed, docsPerLang, role, [.. langs], roleDocs, GoldJson.Sha256(splitBytes),
            files.Count(f => f.Path.EndsWith(".docs.json", StringComparison.Ordinal)), byLang, byEdit.ToDictionary(x => x.Key, x => x.Value), files);
        byte[] manifestBytes = GoldJson.Write(manifest);
        File.WriteAllBytes(Path.Combine(outDir, "manifest.json"), manifestBytes);
        string setHash = GoldJson.Sha256(manifestBytes);
        File.WriteAllText(Path.Combine(outDir, "manifest.sha256"), setHash + "\n");
        Console.WriteLine($"role={role} pairs={manifest.Pairs} files={files.Count} set-sha256={setHash}");
        foreach ((string k, int v) in byLang.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  lang {k}: {v} pairs");
        }

        foreach ((string k, int v) in byEdit)
        {
            Console.WriteLine($"  edit {k}: {v} pairs");
        }

        if (verify)
        {
            Console.WriteLine($"verify: {verified} passed at fallback level 0, {failed} failed");
            return failed == 0 ? 0 : 1;
        }

        return 0;
    }

    private static void MixedEdits(PairMaker maker, Rng r)
    {
        if (r.Chance(12))
        {
            maker.Apply("term-rename");
        }

        int n = r.Next(4, 9);
        for (int i = 0; i < n * 3 && maker.Applied.Count < 40; i++)
        {
            string type = r.Pick(EditTypes.MixedPool);
            maker.Apply(type);
            if (--n <= 0)
            {
                break;
            }
        }

        if (maker.Applied.Count == 0)
        {
            throw new InvalidOperationException("mixed pair without edits");
        }
    }

    private static ManifestFile Write(string root, string rel, byte[] bytes)
    {
        string path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return new ManifestFile(rel, GoldJson.Sha256(bytes));
    }

    // ---- score -----------------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record PairedRow(string Scope, string Metric, double? Baseline, double? Value, double? Diff, double? Lo, double? Hi, int Docs);

    private sealed record ScoreOutput(string Set, string SetHash, string Pipeline, int Resamples, ulong Seed, List<MetricRow> Rows, List<string> Failures);

    private static int Score(Dictionary<string, string> opt)
    {
        string data = Path.GetFullPath(opt.GetValueOrDefault("data") ?? throw new ArgumentException("--data is required"));
        string outFile = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        string? cmpDir = opt.GetValueOrDefault("cmp");
        string? saveCmp = opt.GetValueOrDefault("save-cmp");
        bool bodyOnly = opt.ContainsKey("body-only");
        string? changesDir = opt.GetValueOrDefault("changes");
        string? blocksDir = opt.GetValueOrDefault("blocks");
        bool fuzzyBlocks = opt.ContainsKey("fuzzy-blocks");
        if (opt.TryGetValue("flag-threshold", out string? ft))
        {
            ScoreInput.FlagThreshold = double.Parse(ft, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (changesDir is not null && blocksDir is null)
        {
            throw new ArgumentException("--changes needs --blocks");
        }

        var mapStats = new MapStats();
        int noOutput = 0;
        Func<CompareDocument, PairLabels, IPairScorer?> judge = ModelData.Install(opt, out ModelData.LookupScorer? lookup);
        bool oracleAlignment = opt.ContainsKey("oracle-alignment");
        string pipeline = opt.GetValueOrDefault("pipeline") ?? (oracleAlignment ? "gold-alignment" : "reference");
        // Scored with nothing hidden and cascades and types on, unless --settings legal asks for the default preset.
        CompareSettings scoreSettings = opt.GetValueOrDefault("settings") == "legal" ? CompareSettings.Legal with { ThrowOnFailure = false } : Pipeline.Settings;
        // --recognizer nlp types values with OneNLP's recognizers (the .Nlp package) instead of the built-in ones.
        if (opt.GetValueOrDefault("recognizer") == "nlp")
        {
            scoreSettings = scoreSettings.UseOneNlp();
        }

        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        Bootstrap.Resamples = int.Parse(opt.GetValueOrDefault("resamples") ?? "10000", CultureInfo.InvariantCulture);
        int limit = int.Parse(opt.GetValueOrDefault("limit") ?? "0", CultureInfo.InvariantCulture);
        string setHash = File.ReadAllText(Path.Combine(data, "manifest.sha256")).Trim();
        var manifest = GoldJson.Read<Manifest>(File.ReadAllBytes(Path.Combine(data, "manifest.json")));
        if (!string.Equals(GoldJson.Sha256(File.ReadAllBytes(Path.Combine(data, "manifest.json"))), setHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("manifest.json does not match manifest.sha256");
        }

        // Every file is checked against the manifest before it is used.
        foreach (ManifestFile f in manifest.Files)
        {
            if (!string.Equals(GoldJson.Sha256(File.ReadAllBytes(Path.Combine(data, f.Path))), f.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("hash mismatch: " + f.Path);
            }
        }

        string[] ids = [.. Directory.EnumerateFiles(Path.Combine(data, "pairs"), "*.labels.json").Select(p => Path.GetFileName(p)[..^".labels.json".Length]).OrderBy(x => x, StringComparer.Ordinal)];
        if (opt.TryGetValue("ids", out string? idFile))
        {
            var want = new HashSet<string>(File.ReadAllLines(idFile), StringComparer.Ordinal);
            ids = [.. ids.Where(want.Contains)];
        }

        if (limit > 0)
        {
            ids = [.. ids.Take(limit)];
        }

        if (saveCmp is not null)
        {
            Directory.CreateDirectory(saveCmp);
        }

        bool paired = opt.ContainsKey("vs-rules");
        var results = new (PairLabels Labels, Counts Counts)[ids.Length];
        var baseline = new Counts[ids.Length];
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, ids.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            string id = ids[i];
            var labels = GoldJson.Read<PairLabels>(File.ReadAllBytes(Path.Combine(data, "pairs", id + ".labels.json")));
            var docs = GoldJson.Read<PairDocs>(File.ReadAllBytes(Path.Combine(data, "pairs", id + ".docs.json")));
            ScoreInput input;
            Comparison? semantic = null;
            BlockMap? blocks = blocksDir is null ? null : BlockMap.Read(Path.Combine(blocksDir, id + ".blocks.json"));
            if (changesDir is not null)
            {
                // A pair the tool produced nothing for is scored as an empty result: nothing aligned, nothing found.
                string file = Path.Combine(changesDir, id + ".changes.json");
                ToolOutput output = File.Exists(file) ? ToolOutput.Read(file) : new ToolOutput(ToolOutput.SchemaName, id, "none", [], [], [], []);
                if (!File.Exists(file))
                {
                    Interlocked.Increment(ref noOutput);
                }

                lock (mapStats)
                {
                    input = output.ToScoreInput(blocks!, mapStats);
                }
            }
            else
            {
                Comparison cmp;
                if (cmpDir is not null)
                {
                    cmp = ComparisonJson.Read(File.ReadAllBytes(Path.Combine(cmpDir, id + ".cmp.json")));
                }
                else
                {
                    CompareDocument o = GoldJson.Build(docs.Old);
                    CompareDocument n = GoldJson.Build(docs.New);
                    cmp = oracleAlignment ? Pipeline.OracleAlignment(o, n, labels) : Comparison.Of(o, n, scoreSettings, judge(o, labels));
                    if (saveCmp is not null)
                    {
                        File.WriteAllBytes(Path.Combine(saveCmp, id + ".cmp.json"), ComparisonJson.Write(cmp));
                    }
                }

                if (blocks is null)
                {
                    input = ScoreInput.From(cmp);
                    semantic = cmp;
                }
                else
                {
                    lock (mapStats)
                    {
                        input = ScoreInput.From(cmp, blocks, mapStats, fuzzyBlocks, bodyOnly);
                    }
                }
            }

            if (!input.Passed || input.FallbackLevel > 0)
            {
                failures.Add($"{id}: verification passed={input.Passed} fallback={input.FallbackLevel}");
            }

            bool noDiff = GoldJson.SameNodes(docs);
            Counts counts = PairScorer.Score(input, labels, noDiff);
            if (semantic is not null && !noDiff)
            {
                SemanticScorer.Add(counts, semantic, input, labels);
            }

            results[i] = (labels, counts);
            if (paired)
            {
                // The same pair under the rules alone: its documents are not registered with the hook, so it runs the engine unchanged.
                Comparison rulesCmp = Comparison.Of(GoldJson.Build(docs.Old), GoldJson.Build(docs.New), scoreSettings);
                ScoreInput rulesInput = ScoreInput.From(rulesCmp);
                Counts rulesCounts = PairScorer.Score(rulesInput, labels, noDiff);
                if (!noDiff)
                {
                    SemanticScorer.Add(rulesCounts, rulesCmp, rulesInput, labels);
                }

                baseline[i] = rulesCounts;
            }
        });

        File.WriteAllLines(outFile + ".missed.txt", results.Where(r => r.Counts.Missed.Count > 0).OrderBy(r => r.Labels.Id, StringComparer.Ordinal).Select(r => r.Labels.Id + " " + string.Join(" | ", r.Counts.Missed)));
        File.WriteAllLines(outFile + ".align.txt", results.Where(r => r.Counts.AlignErrors.Count > 0).OrderBy(r => r.Labels.Id, StringComparer.Ordinal).Select(r => r.Labels.Id + " " + string.Join(" ; ", r.Counts.AlignErrors.Order(StringComparer.Ordinal))));
        File.WriteAllLines(outFile + ".noise.txt", results.Where(r => r.Counts.Noise.Count > 0).OrderBy(r => r.Labels.Id, StringComparer.Ordinal).SelectMany(r => r.Counts.Noise.Select(n => r.Labels.Id + " " + n)));
        File.WriteAllLines(outFile + ".semantic.txt", results.Where(r => r.Counts.SemDiag.Count > 0).OrderBy(r => r.Labels.Id, StringComparer.Ordinal).SelectMany(r => r.Counts.SemDiag));
        if (opt.TryGetValue("dump-docs", out string? dumpFile))
        {
            var perDocAll = new Dictionary<(string, int), Counts>();
            foreach ((PairLabels labels, Counts c) in results)
            {
                if (!perDocAll.TryGetValue((labels.Lang, labels.BaseDoc), out Counts? sum))
                {
                    perDocAll[(labels.Lang, labels.BaseDoc)] = sum = new Counts();
                }

                sum.Add(c);
            }

            Pairing.Write(dumpFile, [.. perDocAll.OrderBy(k => k.Key.Item1, StringComparer.Ordinal).ThenBy(k => k.Key.Item2).Select(k => Pairing.ToDump(k.Key.Item1 + "/" + k.Key.Item2.ToString(CultureInfo.InvariantCulture), k.Value))]);
        }

        var rows = new List<MetricRow>();
        var pairedRows = new List<PairedRow>();
        void Scope(string name, Func<PairLabels, bool> pick)
        {
            // One entry per base document: the sum of its pairs in this scope.
            var perDoc = new Dictionary<(string, int), Counts>();
            foreach ((PairLabels labels, Counts c) in results.Where(r => pick(r.Labels)))
            {
                if (!perDoc.TryGetValue((labels.Lang, labels.BaseDoc), out Counts? sum))
                {
                    perDoc[(labels.Lang, labels.BaseDoc)] = sum = new Counts();
                }

                sum.Add(c);
            }

            if (perDoc.Count > 0)
            {
                rows.AddRange(Report.Rows("synth", setHash, name, [.. perDoc.OrderBy(k => k.Key.Item1, StringComparer.Ordinal).ThenBy(k => k.Key.Item2).Select(k => k.Value)], pipeline));
            }

            if (paired && perDoc.Count > 0)
            {
                var perDocBase = new Dictionary<(string, int), Counts>();
                for (int i = 0; i < results.Length; i++)
                {
                    PairLabels labels = results[i].Labels;
                    if (!pick(labels))
                    {
                        continue;
                    }

                    if (!perDocBase.TryGetValue((labels.Lang, labels.BaseDoc), out Counts? sum))
                    {
                        perDocBase[(labels.Lang, labels.BaseDoc)] = sum = new Counts();
                    }

                    sum.Add(baseline[i]);
                }

                var keys = perDoc.Keys.OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2).ToList();
                List<(string Name, Bootstrap.Metric M)> defs = Report.Definitions();
                var res = Bootstrap.RunPaired([.. keys.Select(k => perDocBase[k])], [.. keys.Select(k => perDoc[k])], [.. defs.Select(d => d.M)]);
                for (int m = 0; m < defs.Count; m++)
                {
                    if (!double.IsNaN(res[m].A) || !double.IsNaN(res[m].B))
                    {
                        pairedRows.Add(new PairedRow(name, defs[m].Name, Finite(res[m].A), Finite(res[m].B), Finite(res[m].Diff), Finite(res[m].Lo), Finite(res[m].Hi), keys.Count));
                    }
                }
            }
        }

        Scope("all", _ => true);
        foreach (string lang in manifest.Langs)
        {
            Scope("lang:" + lang, p => p.Lang == lang);
        }

        Scope("mixed", p => p.Id.EndsWith("-m", StringComparison.Ordinal));
        foreach (string type in EditTypes.All)
        {
            Scope("edit:" + type, p => p.Id.Contains("-s", StringComparison.Ordinal) && p.Edits.Count == 1 && p.Edits[0] == type);
        }

        Console.WriteLine(ModelData.Summary(lookup));
        if (paired)
        {
            File.WriteAllBytes(outFile + ".paired.json", JsonSerializer.SerializeToUtf8Bytes(new { Set = "synth", SetHash = setHash, Pipeline = pipeline, Baseline = "rules", Resamples = Bootstrap.Resamples, Seed = Bootstrap.Seed, Rows = pairedRows }, ReportOptions));
        }

        var output = new ScoreOutput("synth", setHash, pipeline, Bootstrap.Resamples, Bootstrap.Seed, rows, [.. failures.OrderBy(x => x, StringComparer.Ordinal)]);
        File.WriteAllBytes(outFile, JsonSerializer.SerializeToUtf8Bytes(output, ReportOptions));
        Console.WriteLine($"set {setHash}  pipeline {pipeline}  pairs {ids.Length}  verification failures {failures.Count}  bootstrap {Bootstrap.Resamples} resamples over documents, seed {Bootstrap.Seed}");
        if (blocksDir is not null)
        {
            Console.WriteLine($"block map: {mapStats.Matched}/{mapStats.Total} content nodes matched to gold blocks" + (changesDir is null ? string.Empty : $"; pairs without output: {noOutput}"));
        }

        foreach (MetricRow row in rows.Where(r => r.Scope is "all" or "mixed" || r.Scope.StartsWith("lang:", StringComparison.Ordinal)))
        {
            Console.WriteLine(Report.Line(row));
        }

        // Per-document distributions of the headline rows.
        var docVals = results.GroupBy(r => (r.Labels.Lang, r.Labels.BaseDoc)).Select(g => { var s = new Counts(); g.ToList().ForEach(x => s.Add(x.Counts)); return s; }).ToList();
        Console.WriteLine("per-document distribution (min / median / p90 / max):");
        Dist("boundary.F1", docVals, c => Bootstrap.F1(c.BTp, c.BFp, c.BFn));
        Dist("frag.sentence", docVals, c => c.SentCounted == 0 ? double.NaN : (double)c.SentChanges / c.SentCounted);
        Dist("frag.unit", docVals, c => c.UnitCounted == 0 ? double.NaN : (double)c.UnitChanges / c.UnitCounted);
        Dist("regions.sentence", docVals, c => c.SentCounted == 0 ? double.NaN : (double)c.SentRegions / c.SentCounted);
        return failures.IsEmpty ? 0 : 1;
    }

    private static double? Finite(double v) => double.IsFinite(v) ? v : null;

    private static void Dist(string name, List<Counts> docs, Func<Counts, double> f)
    {
        double[] v = [.. docs.Select(f).Where(x => !double.IsNaN(x)).OrderBy(x => x)];
        if (v.Length == 0)
        {
            return;
        }

        double Q(double q) => v[(int)Math.Round(q * (v.Length - 1))];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {name,-14} {v[0]:F3} / {Q(0.5):F3} / {Q(0.9):F3} / {v[^1]:F3}  (n={v.Length})"));
    }

    /// <summary>Lists, for one pair, the gold units and the engine's changes with their spans, to see where a score comes from.</summary>
    private static int Explain(Dictionary<string, string> opt)
    {
        string data = opt.GetValueOrDefault("data") ?? throw new ArgumentException("--data is required");
        string id = opt.GetValueOrDefault("id") ?? throw new ArgumentException("--id is required");
        var labels = GoldJson.Read<PairLabels>(File.ReadAllBytes(Path.Combine(data, "pairs", id + ".labels.json")));
        var docs = GoldJson.Read<PairDocs>(File.ReadAllBytes(Path.Combine(data, "pairs", id + ".docs.json")));
        Comparison cmp = Comparison.Of(GoldJson.Build(docs.Old), GoldJson.Build(docs.New), Pipeline.Settings);
        string Clip(string t, int s, int l) => t.Substring(Math.Min(s, t.Length), Math.Min(l, t.Length - Math.Min(s, t.Length)));
        Console.WriteLine("gold units:");
        foreach (Unit u in labels.Units)
        {
            Console.WriteLine($"  #{u.Id} {u.Type} {u.Group} old=[{string.Join("; ", u.Old.Select(s => $"{s.Node}:{s.Start}+{s.Len} '{Clip(cmp.Old[s.Node].Text, s.Start, s.Len)}'"))}] new=[{string.Join("; ", u.New.Select(s => $"{s.Node}:{s.Start}+{s.Len} '{Clip(cmp.New[s.Node].Text, s.Start, s.Len)}'"))}]");
        }

        if (opt.ContainsKey("links"))
        {
            Console.WriteLine("links (not same-position matches of unchanged text):");
            foreach (AlignmentLink l in cmp.Alignment.Links)
            {
                Console.WriteLine($"  {l.Kind} {l.Origin} [{string.Join(",", l.OldNodes)}] -> [{string.Join(",", l.NewNodes)}] {l.Score:F2}");
            }
        }

        Console.WriteLine("engine changes:");
        foreach (Change c in cmp.Changes)
        {
            Console.WriteLine($"  #{c.Id} {c.Kind} old=[{string.Join(",", c.Old.Nodes)}]{c.Old.Start}+{c.Old.Length} '{c.Old.Text}' new=[{string.Join(",", c.New.Nodes)}]{c.New.Start}+{c.New.Length} '{c.New.Text}' type={c.Type} {c.OldValue?.Canonical}->{c.NewValue?.Canonical} subst={c.IsSubstantive} attrs=[{string.Join(";", c.AttributeEdits.Select(e => e.Key + ":" + e.OldValue + ">" + e.NewValue))}]");
        }

        foreach (ChangeGroup g in cmp.Groups.Where(g => g.Kind != GroupKind.Region))
        {
            Console.WriteLine($"  group {g.Kind} '{g.Title}' [{string.Join(",", g.ChangeIds)}]");
        }

        return 0;
    }

    /// <summary>The exact gates on real pairs: every pair must pass Verify at fallback level 0 under the default settings and under Strict.</summary>
    private static int RealGate(Dictionary<string, string> opt)
    {
        string root = opt.GetValueOrDefault("real") ?? throw new ArgumentException("--real is required");
        int pairs = 0, ok = 0, changes = 0;
        var bySource = new SortedDictionary<string, (int Pairs, int Ok)>(StringComparer.Ordinal);
        foreach (string old in Directory.EnumerateFiles(root, "old.txt", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            string dir = Path.GetDirectoryName(old)!;
            string source = Path.GetFileName(Path.GetDirectoryName(dir)!);
            CompareDocument o = CompareDocument.FromText(File.ReadAllText(old), "old");
            CompareDocument n = CompareDocument.FromText(File.ReadAllText(Path.Combine(dir, "new.txt")), "new");
            bool pass = true;
            foreach (CompareSettings settings in new[] { CompareSettings.Legal, Pipeline.Settings })
            {
                Comparison cmp = Comparison.Of(o, n, settings);
                pass &= cmp.Verification.Passed && cmp.Verification.FallbackLevel == 0 && cmp.Check().IsEmpty;
                changes += cmp.Changes.Length;
            }

            pairs++;
            ok += pass ? 1 : 0;
            (int p, int k) = bySource.GetValueOrDefault(source);
            bySource[source] = (p + 1, k + (pass ? 1 : 0));
            if (!pass)
            {
                Console.WriteLine("FAILED " + dir);
            }
        }

        foreach ((string s, (int p, int k)) in bySource)
        {
            Console.WriteLine($"  {s}: {k}/{p} pairs verified at fallback level 0");
        }

        Console.WriteLine($"real-gate: {ok}/{pairs} pairs pass, {changes} changes in total");
        return ok == pairs ? 0 : 1;
    }

    // ---- check-split -----------------------------------------------------------------------------------------------

    private static int CheckSplit(Dictionary<string, string> opt)
    {
        HashSet<string> A = BaseHashes(opt.GetValueOrDefault("a") ?? throw new ArgumentException("--a is required"));
        HashSet<string> B = BaseHashes(opt.GetValueOrDefault("b") ?? throw new ArgumentException("--b is required"));
        int shared = A.Count(B.Contains);
        Console.WriteLine($"a: {A.Count} base documents, b: {B.Count} base documents, shared: {shared}");
        return shared == 0 ? 0 : 1;
    }

    private static HashSet<string> BaseHashes(string dir)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string f in Directory.EnumerateFiles(Path.Combine(dir, "pairs"), "*.labels.json"))
        {
            set.Add(GoldJson.Read<PairLabels>(File.ReadAllBytes(f)).BaseHash);
        }

        return set;
    }
}

/// <summary>The settings the gold set is scored under: nothing hidden, so every labelled difference is a visible change.</summary>
internal static class Pipeline
{
    public static CompareSettings Settings { get; } = CompareSettings.Strict with { ThrowOnFailure = false, CollapseCascades = true, TypedChanges = true };

    /// <summary>The later stages run on the gold alignment of content blocks: a check that the scorer gives 1.0 to the right answer.</summary>
    public static Comparison OracleAlignment(CompareDocument o, CompareDocument n, PairLabels labels)
    {
        var links = labels.Links.Select(l => new AlignmentLink(0, l.Kind switch { "move" => LinkKind.Move, "split" => LinkKind.Split, "merge" => LinkKind.Merge, _ => LinkKind.Match }, [.. l.Old], [.. l.New], false, 1.0, LinkOrigin.User)).ToList();
        // The containers above matched blocks (section, table, row, cell) are matched in step, so the structure stays consistent.
        var usedOld = new HashSet<int>();
        var usedNew = new HashSet<int>();
        foreach (GoldLink l in labels.Links.Where(l => l.Kind == "match"))
        {
            int po = o[l.Old[0]].Parent;
            int pn = n[l.New[0]].Parent;
            while (po >= 0 && pn >= 0 && o[po].Kind == n[pn].Kind && !usedOld.Contains(po) && !usedNew.Contains(pn))
            {
                links.Add(new AlignmentLink(0, LinkKind.Match, [po], [pn], false, 1.0, LinkOrigin.User));
                usedOld.Add(po);
                usedNew.Add(pn);
                po = o[po].Parent;
                pn = n[pn].Parent;
            }
        }

        Alignment al = Alignment.Create(o, n, Settings, links);
        return Stages.Verify(Stages.Classify(Stages.Group(Stages.DiffPairs(al))));
    }
}
