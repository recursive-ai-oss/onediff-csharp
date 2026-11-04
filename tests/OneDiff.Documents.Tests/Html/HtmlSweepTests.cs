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
}
