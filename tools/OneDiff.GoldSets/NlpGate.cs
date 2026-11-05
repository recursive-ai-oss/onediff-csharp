using System.Globalization;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Serialization;
using OneDiff.Nlp;

namespace OneDiff.GoldSets;

/// <summary>
/// The exact gates for the OneNLP value recognizer on a gold set: under the Legal preset with the recognizer selected every pair
/// verifies at fallback level 0 and passes Check, two runs give identical JSON, and the typed output is compared with the
/// built-in recognizer's, per language. Also prints a digest of the first N pairs' JSON for comparing machines.
/// </summary>
internal static class NlpGate
{
    public static int Run(Dictionary<string, string> opt)
    {
        string data = Path.GetFullPath(opt.GetValueOrDefault("data") ?? throw new ArgumentException("--data is required"));
        int count = int.Parse(opt.GetValueOrDefault("count") ?? "20", CultureInfo.InvariantCulture);
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        bool digestOnly = opt.ContainsKey("digest-only");
        string[] ids = [.. Directory.EnumerateFiles(Path.Combine(data, "pairs"), "*.labels.json").Select(p => Path.GetFileName(p)[..^".labels.json".Length]).OrderBy(x => x, StringComparer.Ordinal)];
        if (digestOnly)
        {
            // A spread across the set (every language), not the first N.
            int step = Math.Max(1, ids.Length / Math.Max(1, count));
            ids = [.. ids.Where((_, k) => k % step == 0).Take(count)];
        }

        CompareSettings legal = CompareSettings.Legal with { ThrowOnFailure = false };
        CompareSettings nlp = legal.UseOneNlp();
        var rows = new (string Lang, bool Ok, bool TwoRunsSame, bool Differs, string Digest, string Diff)[ids.Length];
        Parallel.For(0, ids.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            var docs = GoldJson.Read<PairDocs>(File.ReadAllBytes(Path.Combine(data, "pairs", ids[i] + ".docs.json")));
            Comparison a = Comparison.Of(GoldJson.Build(docs.Old), GoldJson.Build(docs.New), nlp);
            Comparison b = Comparison.Of(GoldJson.Build(docs.Old), GoldJson.Build(docs.New), nlp);
            Comparison builtIn = Comparison.Of(GoldJson.Build(docs.Old), GoldJson.Build(docs.New), legal);
            byte[] ja = ComparisonJson.Write(a), jb = ComparisonJson.Write(b), jc = ComparisonJson.Write(builtIn);
            bool ok = a.Verification.Passed && a.Verification.FallbackLevel == 0 && a.Check().IsEmpty;
            string diff = "";
            if (!ja.AsSpan().SequenceEqual(jc))
            {
                var sb = new StringBuilder();
                for (int k = 0; k < a.Changes.Length && k < builtIn.Changes.Length; k++)
                {
                    Change x = a.Changes[k], y = builtIn.Changes[k];
                    if (x.Type != y.Type || x.OldValue != y.OldValue || x.NewValue != y.NewValue)
                    {
                        sb.Append($"  {ids[i]} #{x.Id} '{x.Old.Text}' -> '{x.New.Text}': nlp {x.Type} {x.OldValue?.Canonical}/{x.NewValue?.Canonical}; built-in {y.Type} {y.OldValue?.Canonical}/{y.NewValue?.Canonical}\n");
                    }
                }

                diff = sb.Length > 0 ? sb.ToString() : $"  {ids[i]} differs outside the typed members\n";
            }

            rows[i] = (docs.Lang, ok, ja.AsSpan().SequenceEqual(jb), diff.Length > 0, GoldJson.Sha256(ja), diff);
        });

        if (digestOnly)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                Console.WriteLine(ids[i] + " " + rows[i].Digest);
            }

            Console.WriteLine("digest " + GoldJson.Sha256(string.Join("\n", rows.Select((r, i) => ids[i] + " " + r.Digest))) + " pairs " + ids.Length + " two-runs-identical " + rows.All(r => r.TwoRunsSame));
            return rows.All(r => r.TwoRunsSame) ? 0 : 1;
        }

        foreach (IGrouping<string, (string Lang, bool Ok, bool TwoRunsSame, bool Differs, string Digest, string Diff)> g in rows.GroupBy(r => r.Lang).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{g.Key}: pairs {g.Count()} verify-ok {g.Count(r => r.Ok)} two-runs-identical {g.Count(r => r.TwoRunsSame)} differs-from-built-in {g.Count(r => r.Differs)}");
        }

        foreach (var r in rows.Where(r => r.Differs).Take(int.Parse(opt.GetValueOrDefault("show") ?? "40", CultureInfo.InvariantCulture)))
        {
            Console.Write(r.Diff);
        }

        bool pass = rows.All(r => r.Ok && r.TwoRunsSame);
        Console.WriteLine($"nlp-gate: pairs {rows.Length} verify-ok {rows.Count(r => r.Ok)} two-runs-identical {rows.Count(r => r.TwoRunsSame)} differs-from-built-in {rows.Count(r => r.Differs)} {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    /// <summary>
    /// Indicative cost of recognition on a large synthetic contract (default 100 pages of 40 paragraphs, 300 edited values): the same
    /// comparison under Legal with the built-in recognizer and with the OneNLP one, the Classify stage alone for each, and the
    /// recognizer's first call (which loads OneNLP's rules).
    /// </summary>
    public static int Time(Dictionary<string, string> opt)
    {
        int pages = int.Parse(opt.GetValueOrDefault("pages") ?? "100", CultureInfo.InvariantCulture);
        int edits = int.Parse(opt.GetValueOrDefault("edits") ?? "300", CultureInfo.InvariantCulture);
        int reps = int.Parse(opt.GetValueOrDefault("reps") ?? "5", CultureInfo.InvariantCulture);
        string[] words = ["party", "shall", "deliver", "payment", "within", "notice", "agreement", "terminate", "liability", "goods", "services", "invoice", "warranty", "confidential", "damages", "term", "renew", "supplier", "customer"];
        var rng = new Rng(20261104);
        int paragraphs = pages * 40;
        static string Value(int k, int n) => (k % 4) switch
        {
            0 => n + " days",
            1 => "USD " + (n * 1000).ToString("N0", CultureInfo.InvariantCulture),
            2 => n + "%",
            _ => "May " + (n % 28 + 1) + ", 20" + (20 + n % 10),
        };

        string[] before = new string[paragraphs], after = new string[paragraphs];
        var edited = new HashSet<int>();
        while (edited.Count < edits)
        {
            edited.Add(rng.Next(paragraphs));
        }

        for (int i = 0; i < paragraphs; i++)
        {
            string head = string.Join(' ', Enumerable.Range(0, 50).Select(_ => rng.Pick(words)));
            string tail = string.Join(' ', Enumerable.Range(0, 10).Select(_ => rng.Pick(words))) + ".";
            int n = rng.Next(10, 90);
            before[i] = head + " " + Value(i, n) + " " + tail;
            after[i] = head + " " + Value(i, edited.Contains(i) ? n + 7 : n) + " " + tail;
        }

        static CompareDocument Build(string name, string[] text)
        {
            var b = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(name);
            b.Begin(NodeKind.Section);
            foreach (string p in text)
            {
                b.Add(NodeKind.Paragraph, new NodeContent { Text = p });
            }

            b.End();
            return b.Build();
        }

        CompareDocument o = Build("old", before), n2 = Build("new", after);
        CompareSettings legal = CompareSettings.Legal, nlp = legal.UseOneNlp();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _ = NlpValueRecognizer.Instance.Recognize("thirty days", null);
        Console.WriteLine($"first OneNLP call (loads the English rules): {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} ms");
        Console.WriteLine($"pages {pages} paragraphs {paragraphs} edited values {edits} cores {Environment.ProcessorCount}");
        foreach ((string name, CompareSettings s) in new[] { ("built-in", legal), ("onenlp", nlp) })
        {
            var full = new List<double>();
            var classify = new List<double>();
            Comparison? last = null;
            for (int rep = 0; rep <= reps; rep++)
            {
                Alignment a = Stages.Align(o, n2, null, s, null, default);
                var grouped = Stages.Group(Stages.DiffPairs(a, default), default);
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var classified = Stages.Classify(grouped, default);
                double cm = System.Diagnostics.Stopwatch.GetElapsedTime(c0).TotalMilliseconds;
                long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
                last = Comparison.Of(o, n2, s);
                double fm = System.Diagnostics.Stopwatch.GetElapsedTime(f0).TotalMilliseconds;
                if (rep > 0)
                {
                    classify.Add(cm);
                    full.Add(fm);
                }

                _ = classified;
            }

            Console.WriteLine($"{name,-9} changes {last!.Changes.Length} typed {last.Changes.Count(c => c.Type != ChangeType.None)} fallback {last.Verification.FallbackLevel}; Comparison.Of ms {string.Join(", ", full.Select(t => t.ToString("F0", CultureInfo.InvariantCulture)))}; Classify alone ms {string.Join(", ", classify.Select(t => t.ToString("F1", CultureInfo.InvariantCulture)))} (first run excluded)");
        }

        return 0;
    }
}
