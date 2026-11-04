using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Serialization;

namespace OneDiff.Pdf.Tests;

/// <summary>
/// The PDF gate over a directory of rendered pairs (<c>pdf/&lt;id&gt;.old.pdf</c> and <c>.new.pdf</c>). Opt-in: it does nothing unless
/// ONEDIFF_PDF_GATE_DIR is set. For every pair it compares under the Legal and the Strict settings and requires verification
/// without fallback, marks the new and the old PDF, reads the new marks back against the change list, and compares a second run
/// byte for byte. ONEDIFF_PDF_GATE_FROM and ONEDIFF_PDF_GATE_COUNT select a slice of the sorted pair ids (chunks);
/// ONEDIFF_PDF_GATE_OUT receives one line per pair; ONEDIFF_PDF_GATE_JOBS sets the parallelism (default 4).
/// </summary>
public class PdfGateRunTests
{
    private static string Hex(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact]
    public void EveryPairOfTheDirectoryPassesTheGates()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEDIFF_PDF_GATE_DIR");
        if (dir is null)
        {
            Assert.Skip("ONEDIFF_PDF_GATE_DIR is not set");
        }

        string outFile = Environment.GetEnvironmentVariable("ONEDIFF_PDF_GATE_OUT") ?? Path.Combine(Path.GetTempPath(), "pdf-gate.txt");
        int from = Int("ONEDIFF_PDF_GATE_FROM", 0), count = Int("ONEDIFF_PDF_GATE_COUNT", int.MaxValue), jobs = Int("ONEDIFF_PDF_GATE_JOBS", 4);
        string[] ids = [.. Directory.EnumerateFiles(Path.Combine(dir, "pdf"), "*.old.pdf")
            .Select(p => Path.GetFileName(p)[..^".old.pdf".Length]).OrderBy(x => x, StringComparer.Ordinal).Skip(from).Take(count)];

        var lines = new ConcurrentDictionary<string, string>();
        var failures = new ConcurrentBag<string>();
        Parallel.For(0, ids.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            string id = ids[i];
            try
            {
                lines[id] = CheckPair(dir, id, failures);
            }
            catch (Exception e)
            {
                failures.Add($"{id}: exception {e.GetType().Name}: {e.Message}");
                lines[id] = id + " EXCEPTION";
            }
        });

        File.WriteAllLines(outFile, ids.Select(id => lines[id]));
        File.WriteAllLines(outFile + ".failures", failures.Order(StringComparer.Ordinal));
        Assert.True(failures.IsEmpty, $"{failures.Count} of {ids.Length} pairs failed; first: {failures.Order(StringComparer.Ordinal).FirstOrDefault()}");
    }

    [Fact]
    public void ExplainOnePair()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEDIFF_PDF_GATE_DIR"), id = Environment.GetEnvironmentVariable("ONEDIFF_PDF_GATE_EXPLAIN");
        if (dir is null || id is null)
        {
            Assert.Skip("ONEDIFF_PDF_GATE_EXPLAIN is not set");
        }

        Run run = Run.Of(File.ReadAllBytes(Path.Combine(dir, "pdf", id + ".old.pdf")), File.ReadAllBytes(Path.Combine(dir, "pdf", id + ".new.pdf")));
        var sb = new StringBuilder();
        static string Clip(string s) => s.Length <= 70 ? s : s[..70] + "...";
        foreach (Change c in run.Comparison.Changes)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"#{c.Id} {c.Kind} link={c.LinkId} old[nodes={string.Join('+', c.Old.Nodes)} {c.Old.Start}+{c.Old.Length} '{Clip(c.Old.Text)}'] new[nodes={string.Join('+', c.New.Nodes)} {c.New.Start}+{c.New.Length} '{Clip(c.New.Text)}']");
        }

        foreach (PdfMark m in run.Result.Marks)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"mark {m.Name} {m.Kind} quads={m.QuadCount}");
        }

        File.WriteAllText(Environment.GetEnvironmentVariable("ONEDIFF_PDF_GATE_OUT") ?? Path.Combine(Path.GetTempPath(), "explain.txt"), sb.ToString());
    }

    private static string CheckPair(string dir, string id, ConcurrentBag<string> failures)
    {
        byte[] oldPdf = File.ReadAllBytes(Path.Combine(dir, "pdf", id + ".old.pdf")), newPdf = File.ReadAllBytes(Path.Combine(dir, "pdf", id + ".new.pdf"));
        var sb = new StringBuilder(id);
        foreach ((string name, CompareSettings settings) in new[] { ("legal", CompareSettings.Legal), ("strict", CompareSettings.Strict) })
        {
            Run one = Run.Of(oldPdf, newPdf, settings);
            if (!one.Comparison.Verification.Passed || one.Comparison.Verification.FallbackLevel != 0 || one.Comparison.Check().Length != 0)
            {
                failures.Add($"{id} {name}: verification passed={one.Comparison.Verification.Passed} fallback={one.Comparison.Verification.FallbackLevel} recheck={one.Comparison.Check().Length}");
            }

            try
            {
                one.AssertReextraction();
            }
            catch (Exception e)
            {
                failures.Add($"{id} {name}: re-extraction: {e.Message.ReplaceLineEndings(" | ")} (kinds {string.Join(",", one.Comparison.Changes.Select(c => c.Kind).Distinct())})");
            }

            using var oldMarked = new MemoryStream();
            PdfRedline.OnPages.MarkOldDocument(one.Comparison, oldPdf, oldMarked);
            int oldAnnotations = Run.ReadAnnotations(oldMarked.ToArray()).Count;
            if (oldAnnotations == 0 && one.Comparison.Changes.Length > 0 && one.Result.Marks.Length > 0 && one.Comparison.Changes.Any(c => c.Old.Text.Length > 0 && c.Kind != ChangeKind.Insert))
            {
                failures.Add($"{id} {name}: old document has no annotations although changes remove text");
            }

            // Determinism: a second run from the bytes gives identical comparison JSON and identical marked PDFs.
            Run two = Run.Of(oldPdf, newPdf, settings);
            byte[] j1 = ComparisonJson.Write(one.Comparison), j2 = ComparisonJson.Write(two.Comparison);
            using var oldMarked2 = new MemoryStream();
            PdfRedline.OnPages.MarkOldDocument(two.Comparison, oldPdf, oldMarked2);
            if (!j1.AsSpan().SequenceEqual(j2) || !one.Marked.AsSpan().SequenceEqual(two.Marked) || !oldMarked.ToArray().AsSpan().SequenceEqual(oldMarked2.ToArray()))
            {
                failures.Add($"{id} {name}: second run differs");
            }

            sb.Append(CultureInfo.InvariantCulture, $" {name} changes={one.Comparison.Changes.Length} marks={one.Result.Marks.Length} json={Hex(j1)} new={Hex(one.Marked)} old={Hex(oldMarked.ToArray())}");
        }

        return sb.ToString();
    }

    private static int Int(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out int v) ? v : fallback;
}
