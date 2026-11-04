using System.Globalization;
using OneDiff.Documents.Html;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Tests.Html;

/// <summary>
/// Reads written pages back over many comparisons: random documents with random edits (the generator the invertibility tests
/// use), and, when a corpus folder is given, real and synthetic plain-text pairs. Sizes come from the environment so the same
/// tests run small locally and large on the worker.
/// </summary>
public class HtmlSweepTests
{
    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out int n) ? n : fallback;

    private static CompareSettings SettingsFor(int seed) => (seed % 3) switch
    {
        0 => CompareSettings.Legal,
        1 => CompareSettings.Strict with { ThrowOnFailure = false },
        _ => CompareSettings.TextOnly,
    };

    /// <summary>With ONEDIFF_HTML_DUMP set, keeps the first pages written so a validator can be run over them.</summary>
    private static void Dump(string name, string html, ref int dumped, int limit)
    {
        if (Environment.GetEnvironmentVariable("ONEDIFF_HTML_DUMP") is { Length: > 0 } dir && dumped < limit)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".html"), html);
            dumped++;
        }
    }

    private static void Report(string line)
    {
        if (Environment.GetEnvironmentVariable("ONEDIFF_HTML_REPORT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, line + "\n");
        }
    }

    [Fact]
    public void RandomComparisonsReadBackWithNoMismatch()
    {
        int cases = Env("ONEDIFF_HTML_CASES", 300), baseSeed = Env("ONEDIFF_PROPERTY_SEED", 0);
        bool allStyles = Environment.GetEnvironmentVariable("ONEDIFF_HTML_ALLSTYLES") == "1";
        long pages = 0, failures = 0;
        int dumped = 0;
        int withMoves = 0, withSplitMerge = 0, withFormat = 0, withAttr = 0, withLedger = 0, withChanges = 0;
        string firstFailure = "";
        for (int i = 0; i < cases; i++)
        {
            int seed = baseSeed + i;
            var r = new Random(seed);
            GNode a = DocumentGenerator.Random(r);
            GNode b = a.Clone();
            for (int k = r.Next(0, 7); k > 0; k--)
            {
                DocumentGenerator.Mutate(b, r);
            }

            CompareDocument da = DocumentGenerator.Build(a, "old-" + seed), db = DocumentGenerator.BuildWithOpaque(b, r.Next(4) == 0 ? "ff" : "00");
            Comparison c = Comparison.Of(da, db, SettingsFor(seed));
            withChanges += c.Changes.IsEmpty ? 0 : 1;
            withMoves += c.Changes.Any(x => x.Kind == ChangeKind.Move) ? 1 : 0;
            withSplitMerge += c.Changes.Any(x => x.Kind is ChangeKind.Split or ChangeKind.Merge) ? 1 : 0;
            withFormat += c.Changes.Any(x => x.Kind == ChangeKind.Format) ? 1 : 0;
            withAttr += c.Changes.Any(x => x.Kind == ChangeKind.Attribute) ? 1 : 0;
            withLedger += c.Ledger.TotalCount > 0 ? 1 : 0;
            foreach (RedlineStyle style in allStyles ? HtmlWriterTests.Styles : [HtmlWriterTests.Styles[seed % 4]])
            {
                foreach (bool sbs in new[] { false, true })
                {
                    string html = sbs ? HtmlRedline.SideBySide(c, style) : HtmlRedline.Inline(c, style);
                    pages++;
                    if (seed % 25 == 0)
                    {
                        Dump($"random-{seed}-{style.Name}-{(sbs ? "sbs" : "inline")}", html, ref dumped, 240);
                    }

                    List<string> errors = HtmlGate.Check(c, html, sbs);
                    if (errors.Count > 0)
                    {
                        failures++;
                        firstFailure = firstFailure.Length > 0 ? firstFailure : $"seed {seed} {style.Name} {(sbs ? "sbs" : "inline")}: {string.Join(" | ", errors)}";
                    }
                }
            }
        }

        Report($"random: comparisons={cases} pages={pages} mismatches={failures} withChanges={withChanges} moves={withMoves} splitMerge={withSplitMerge} format={withFormat} attribute={withAttr} ledger={withLedger} seed0={baseSeed} allStyles={allStyles}");
        Assert.True(failures == 0, firstFailure);
        if (cases >= 200)
        {
            Assert.True(withChanges > cases / 3 && withMoves > 0 && withSplitMerge > 0 && withLedger > 0, "the generator must exercise the interesting paths");
        }
    }

    [Fact]
    public void PlainTextCorpusPairsReadBackWithNoMismatch()
    {
        string? root = Environment.GetEnvironmentVariable("ONEDIFF_TEXT_CORPUS");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        int perGitCorpus = Env("ONEDIFF_HTML_GIT_PAIRS", 30);
        long maxBytes = Env("ONEDIFF_HTML_MAX_BYTES", 4_000_000);
        long pairs = 0, pages = 0, failures = 0, skipped = 0;
        int dumped = 0;
        string firstFailure = "";
        var perSet = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string set in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(set);
            IEnumerable<string> dirs = Directory.GetDirectories(set).Order(StringComparer.Ordinal);
            if (name.StartsWith("git-", StringComparison.Ordinal))
            {
                dirs = dirs.Take(perGitCorpus);
            }

            foreach (string dir in dirs)
            {
                string o = Path.Combine(dir, "old"), n = Path.Combine(dir, "new");
                if (!File.Exists(o) || !File.Exists(n) || new FileInfo(o).Length + new FileInfo(n).Length > maxBytes)
                {
                    skipped++;
                    continue;
                }

                var oi = DocumentInput.FromBytes(File.ReadAllBytes(o), name + "/" + Path.GetFileName(dir) + "/old");
                var ni = DocumentInput.FromBytes(File.ReadAllBytes(n), name + "/" + Path.GetFileName(dir) + "/new");
                if (!PlainTextSource.Instance.CanRead(oi) || !PlainTextSource.Instance.CanRead(ni))
                {
                    skipped++;
                    continue;
                }

                Comparison c = Comparison.Of(oi, ni, CompareSettings.Legal with { ThrowOnFailure = false });
                RedlineStyle style = HtmlWriterTests.Styles[(int)(pairs % 4)];
                foreach (bool sbs in new[] { false, true })
                {
                    string html = sbs ? HtmlRedline.SideBySide(c, style) : HtmlRedline.Inline(c, style);
                    pages++;
                    if (pairs % 10 == 0 && html.Length < 400_000)
                    {
                        Dump($"corpus-{name}-{Path.GetFileName(dir)}-{(sbs ? "sbs" : "inline")}", html, ref dumped, 120);
                    }

                    List<string> errors = HtmlGate.Check(c, html, sbs);
                    if (errors.Count > 0)
                    {
                        failures++;
                        firstFailure = firstFailure.Length > 0 ? firstFailure : $"{dir} {style.Name} {(sbs ? "sbs" : "inline")}: {string.Join(" | ", errors)}";
                    }
                }

                pairs++;
                perSet[name] = perSet.GetValueOrDefault(name) + 1;
            }
        }

        Report($"corpus: pairs={pairs} pages={pages} mismatches={failures} skipped={skipped} sets=" + string.Join(',', perSet.Select(kv => kv.Key + ":" + kv.Value.ToString(CultureInfo.InvariantCulture))));
        Assert.True(failures == 0, firstFailure);
    }

    /// <summary>
    /// With ONEDIFF_HTML_HASHES set to a file, writes the SHA-256 of every page for a fixed set of comparisons, so two machines
    /// can be compared line by line: seeded random documents in every preset and layout, the sample pair, and (when a corpus is
    /// given) the first plain-text pairs of the wiki and synthetic sets.
    /// </summary>
    [Fact]
    public void PageHashesForCrossMachineComparison()
    {
        if (Environment.GetEnvironmentVariable("ONEDIFF_HTML_HASHES") is not { Length: > 0 } path)
        {
            return;
        }

        var lines = new List<string>();
        void Add(string label, Comparison c)
        {
            foreach (RedlineStyle style in HtmlWriterTests.Styles)
            {
                foreach (bool sbs in new[] { false, true })
                {
                    string html = sbs ? HtmlRedline.SideBySide(c, style) : HtmlRedline.Inline(c, style);
                    lines.Add($"{label} {style.Name} {(sbs ? "sbs" : "inline")} {html.Length} {Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(html)))}");
                }
            }
        }

        Add("sample", HtmlSampleTests.SampleComparison());
        for (int seed = 0; seed < 1000; seed++)
        {
            var r = new Random(seed);
            GNode a = DocumentGenerator.Random(r);
            GNode b = a.Clone();
            for (int k = r.Next(0, 7); k > 0; k--)
            {
                DocumentGenerator.Mutate(b, r);
            }

            Add($"random-{seed}", Comparison.Of(DocumentGenerator.Build(a, "old-" + seed), DocumentGenerator.BuildWithOpaque(b, r.Next(4) == 0 ? "ff" : "00"), SettingsFor(seed)));
        }

        if (Environment.GetEnvironmentVariable("ONEDIFF_TEXT_CORPUS") is { Length: > 0 } root && Directory.Exists(root))
        {
            foreach (string set in Directory.GetDirectories(root).Order(StringComparer.Ordinal).Where(d => Path.GetFileName(d).StartsWith("wiki-", StringComparison.Ordinal) || Path.GetFileName(d) == "synthetic"))
            {
                foreach (string dir in Directory.GetDirectories(set).Order(StringComparer.Ordinal).Take(40))
                {
                    string o = Path.Combine(dir, "old"), n = Path.Combine(dir, "new");
                    if (!File.Exists(o) || !File.Exists(n) || new FileInfo(o).Length + new FileInfo(n).Length > 400_000)
                    {
                        continue;
                    }

                    var oi = DocumentInput.FromBytes(File.ReadAllBytes(o), "old");
                    var ni = DocumentInput.FromBytes(File.ReadAllBytes(n), "new");
                    if (PlainTextSource.Instance.CanRead(oi) && PlainTextSource.Instance.CanRead(ni))
                    {
                        Add($"{Path.GetFileName(set)}-{Path.GetFileName(dir)}", Comparison.Of(oi, ni, CompareSettings.Legal with { ThrowOnFailure = false }));
                    }
                }
            }
        }

        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// With ONEDIFF_HTML_COST=1, times the writers on a synthetic contract of 3,000 blocks with about 300 edits (indicative:
    /// run it alone on a quiet machine). Prints size, time and allocation per page.
    /// </summary>
    [Fact]
    public void WriteCostOfALargeContract()
    {
        if (Environment.GetEnvironmentVariable("ONEDIFF_HTML_COST") != "1")
        {
            return;
        }

        var r = new Random(7);
        GNode a = new() { Kind = NodeKind.Section };
        for (int i = 0; i < 3000; i++)
        {
            a.Children.Add(new GNode { Kind = NodeKind.Paragraph, Text = DocumentGenerator.RandomText(r, 18, 40) });
        }

        GNode b = a.Clone();
        for (int i = 0; i < 300; i++)
        {
            GNode p = b.Children[r.Next(b.Children.Count)];
            string[] words = p.Text.Split(' ');
            words[r.Next(words.Length)] = DocumentGenerator.RandomText(r, 1, 3);
            p.Text = string.Join(' ', words);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Comparison c = Comparison.Of(DocumentGenerator.Build(a, "contract-v1"), DocumentGenerator.Build(b, "contract-v2"));
        double compareMs = sw.Elapsed.TotalMilliseconds;
        foreach (bool sbs in new[] { false, true })
        {
            var times = new List<double>();
            long bytes = 0, alloc = 0;
            for (int rep = 0; rep < 7; rep++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                using var ms = new MemoryStream();
                if (sbs)
                {
                    HtmlRedline.WriteSideBySide(c, ms);
                }
                else
                {
                    HtmlRedline.WriteInline(c, ms);
                }

                times.Add(sw.Elapsed.TotalMilliseconds);
                bytes = ms.Length;
                alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            times.Sort();
            Report($"cost: layout={(sbs ? "side-by-side" : "inline")} blocks=3000 changes={c.Changes.Length} compare_ms={compareMs:F0} write_ms_median={times[times.Count / 2]:F1} write_ms_min={times[0]:F1} bytes={bytes} alloc_bytes={alloc}");
        }
    }
}
