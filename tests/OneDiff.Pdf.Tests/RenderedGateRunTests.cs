using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Pdf.Tests;

/// <summary>
/// The re-extraction gate for rendered redlines over files on disk. Opt-in: each test does nothing unless its directory variable is
/// set. Every pair is compared with nothing hidden (so every difference is on the page), rendered inline with the gate style and
/// side by side, read back with OnePDF and checked by <see cref="RenderedGate"/>. ONEDIFF_RENDER_FROM and ONEDIFF_RENDER_COUNT
/// select a slice of the sorted pair list (chunks), ONEDIFF_RENDER_EVERY keeps every n-th pair, ONEDIFF_RENDER_OUT receives one
/// line per pair with the digests of the two PDFs, ONEDIFF_RENDER_JOBS sets the parallelism (default 4).
/// <list type="bullet">
/// <item><description>ONEDIFF_RENDER_TEXT_CORPUS: a corpus of <c>set/n/old</c> and <c>set/n/new</c> text files (the wiki and synthetic sets whole, the git sets up to 30 pairs each; pairs of more than ONEDIFF_RENDER_MAX_BYTES bytes in all, default 300,000, or fewer than ONEDIFF_RENDER_MIN_BYTES, default 0, are skipped and counted).</description></item>
/// <item><description>ONEDIFF_PDF_GATE_DIR: a pair set of <c>pdf/id.old.pdf</c> and <c>id.new.pdf</c>, compared PDF to PDF, with the rendered form forced.</description></item>
/// </list>
/// </summary>
public class RenderedGateRunTests
{
    private static int Int(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static string Hex(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private sealed record Pair(string Id, Func<Comparison> Make);

    [Fact]
    public void EveryTextCorpusPairReadsBack()
    {
        string? root = Environment.GetEnvironmentVariable("ONEDIFF_RENDER_TEXT_CORPUS");
        if (string.IsNullOrEmpty(root))
        {
            Assert.Skip("ONEDIFF_RENDER_TEXT_CORPUS is not set");
        }

        long maxBytes = Int("ONEDIFF_RENDER_MAX_BYTES", 300_000), minBytes = Int("ONEDIFF_RENDER_MIN_BYTES", 0);
        var pairs = new List<Pair>();
        int skipped = 0;
        foreach (string set in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(set);
            bool git = name.StartsWith("git-", StringComparison.Ordinal);
            if (!git && !name.StartsWith("wiki-", StringComparison.Ordinal) && name != "synthetic")
            {
                continue;
            }

            foreach (string dir in Directory.GetDirectories(set).Order(StringComparer.Ordinal).Take(git ? 30 : int.MaxValue))
            {
                string o = Path.Combine(dir, "old"), n = Path.Combine(dir, "new");
                if (!File.Exists(o) || !File.Exists(n))
                {
                    continue;
                }

                long size = new FileInfo(o).Length + new FileInfo(n).Length;
                if (size > maxBytes || size < minBytes)
                {
                    skipped++;
                    continue;
                }

                pairs.Add(new Pair(name + "/" + Path.GetFileName(dir), () =>
                {
                    var oi = DocumentInput.FromBytes(File.ReadAllBytes(o), "old");
                    var ni = DocumentInput.FromBytes(File.ReadAllBytes(n), "new");
                    return Comparison.Of(oi, ni, CompareSettings.Strict with { ThrowOnFailure = false });
                }));
            }
        }

        Run(pairs, skipped);
    }

    [Fact]
    public void EveryPdfPairReadsBackWhenRendered()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEDIFF_PDF_GATE_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            Assert.Skip("ONEDIFF_PDF_GATE_DIR is not set");
        }

        var pairs = new List<Pair>();
        foreach (string id in Directory.EnumerateFiles(Path.Combine(dir, "pdf"), "*.old.pdf").Select(p => Path.GetFileName(p)[..^".old.pdf".Length]).OrderBy(x => x, StringComparer.Ordinal))
        {
            pairs.Add(new Pair(id, () =>
                Comparison.Of(
                    Tests.Run.Read(File.ReadAllBytes(Path.Combine(dir, "pdf", id + ".old.pdf")), id + ".old.pdf"),
                    Tests.Run.Read(File.ReadAllBytes(Path.Combine(dir, "pdf", id + ".new.pdf")), id + ".new.pdf"),
                    CompareSettings.Strict with { ThrowOnFailure = false })));
        }

        Run(pairs, 0);
    }

    // A failure message quotes document text, which may hold a lone surrogate that the file encoding refuses.
    private static string Printable(string s)
    {
        var b = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                b.Append(s[i]).Append(s[i + 1]);
                i++;
            }
            else
            {
                b.Append(char.IsSurrogate(s[i]) ? '�' : s[i]);
            }
        }

        return b.ToString();
    }

    private static void Run(List<Pair> all, int skipped)
    {
        string outFile = Environment.GetEnvironmentVariable("ONEDIFF_RENDER_OUT") ?? Path.Combine(Path.GetTempPath(), "render-gate.txt");
        int from = Int("ONEDIFF_RENDER_FROM", 0), count = Int("ONEDIFF_RENDER_COUNT", int.MaxValue), jobs = Int("ONEDIFF_RENDER_JOBS", 4), every = Int("ONEDIFF_RENDER_EVERY", 1);
        Pair[] pairs = [.. all.Skip(from).Take(count).Where((_, i) => i % every == 0)];
        var lines = new ConcurrentDictionary<string, string>();
        var failures = new ConcurrentBag<string>();
        long pages = 0, notes = 0;
        Parallel.For(0, pairs.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            Pair p = pairs[i];
            try
            {
                Comparison c = p.Make();
                if (!c.Verification.Passed || c.Verification.FallbackLevel != 0)
                {
                    failures.Add($"{p.Id}: verification passed={c.Verification.Passed} fallback={c.Verification.FallbackLevel}");
                }

                var sb = new StringBuilder(p.Id);
                foreach ((string layout, RenderedPdfRedline form) in new[] { ("inline", PdfRedline.Rendered), ("sbs", PdfRedline.RenderedSideBySide) })
                {
                    using var ms = new MemoryStream();
                    RenderedPdfResult r = form.Render(c, ms, RenderedGate.Style);
                    byte[] pdf = ms.ToArray();
                    if (Environment.GetEnvironmentVariable("ONEDIFF_RENDER_KEEP") is { Length: > 0 } keep && p.Id.Contains(Environment.GetEnvironmentVariable("ONEDIFF_RENDER_ONLY") ?? "", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(keep);
                        File.WriteAllBytes(Path.Combine(keep, p.Id.Replace('/', '_') + "." + layout + ".pdf"), pdf);
                    }

                    Interlocked.Add(ref pages, r.PageCount);
                    Interlocked.Add(ref notes, r.Notes.Length);
                    foreach (string note in r.Notes.Where(x => x.Contains("missing glyph", StringComparison.OrdinalIgnoreCase)).Take(1))
                    {
                        failures.Add($"{p.Id} {layout}: renderer note: {note}");
                    }

                    foreach (string e in RenderedGate.Check(c, pdf, RenderedGate.Style, layout == "sbs"))
                    {
                        failures.Add($"{p.Id} {layout}: {e}");
                    }

                    sb.Append(CultureInfo.InvariantCulture, $" {layout}={Hex(pdf)} pages={r.PageCount}");
                }

                lines[p.Id] = sb.ToString();
            }
            catch (Exception e)
            {
                failures.Add($"{p.Id}: exception {e.GetType().Name}: {e.Message}");
                lines[p.Id] = p.Id + " EXCEPTION";
            }
        });

        File.WriteAllLines(outFile, pairs.Select(x => lines.GetValueOrDefault(x.Id, x.Id + " MISSING")).Append($"# pairs={pairs.Length} skipped(over size)={skipped} pages={pages} notes={notes} failures={failures.Count}"));
        File.WriteAllLines(outFile + ".failures", failures.Order(StringComparer.Ordinal).Select(Printable));
        Assert.True(failures.IsEmpty, $"{failures.Count} failures over {pairs.Length} pairs; first: {failures.Order(StringComparer.Ordinal).FirstOrDefault()}");
    }
}
