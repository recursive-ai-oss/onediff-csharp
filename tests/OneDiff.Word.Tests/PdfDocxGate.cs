using System.Collections.Concurrent;
using System.Security.Cryptography;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Pdf;

namespace OneDiff.Word.Tests;

/// <summary>
/// The reconstructed-DOCX gate over rendered pairs. Opt-in: it does nothing unless ONEDIFF_PDFDOCX_PDF (a directory with
/// <c>pdf/ID.old.pdf</c> and <c>pdf/ID.new.pdf</c>) is set. ONEDIFF_PDFDOCX_MODE is <c>pp</c> (old PDF, new PDF; the default),
/// <c>dp</c> (old DOCX, new PDF) or <c>pd</c> (old PDF, new DOCX); the DOCX files come from ONEDIFF_PDFDOCX_DOCX (<c>docx/ID.old.docx</c>),
/// and only ids present in both sets are used. For every pair and the Legal and Strict settings the comparison must verify without a
/// fallback, and the tracked DOCX (built on the new document, and with ONEDIFF_PDFDOCX_BUILD=both on the old one as well) must pass the
/// reconstruction fidelity check, accept-all and reject-all, the schema check (ONEDIFF_PDFDOCX_SCHEMA=1) and a second run byte for byte.
/// ONEDIFF_PDFDOCX_ONLY (comma-separated ids) and ONEDIFF_PDFDOCX_DETAIL=1 (edit detail in the failures) help with one pair; ONEDIFF_PDFDOCX_FROM, _COUNT and _EVERY select pairs of the sorted ids; ONEDIFF_PDFDOCX_OUT receives one line per run (id, settings,
/// build, SHA-256 of the output) and a <c>.failures</c> file; ONEDIFF_PDFDOCX_JOBS sets the parallelism (default 4).
/// </summary>
public class PdfDocxGate
{
    private static readonly ConcurrentBag<string> Notes = [];

    private static int Int(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;

    [Fact]
    public void EveryPairWritesAReconstructedTrackedDocxThatAcceptsAndRejectsExactly()
    {
        string? pdfDir = Environment.GetEnvironmentVariable("ONEDIFF_PDFDOCX_PDF");
        if (pdfDir is null)
        {
            Assert.Skip("ONEDIFF_PDFDOCX_PDF is not set");
        }

        string mode = Environment.GetEnvironmentVariable("ONEDIFF_PDFDOCX_MODE") ?? "pp";
        string? docxDir = Environment.GetEnvironmentVariable("ONEDIFF_PDFDOCX_DOCX");
        bool schema = Environment.GetEnvironmentVariable("ONEDIFF_PDFDOCX_SCHEMA") == "1";
        bool both = Environment.GetEnvironmentVariable("ONEDIFF_PDFDOCX_BUILD") == "both";
        string outFile = Environment.GetEnvironmentVariable("ONEDIFF_PDFDOCX_OUT") ?? Path.Combine(Path.GetTempPath(), "pdfdocx-gate.txt");
        int from = Int("ONEDIFF_PDFDOCX_FROM", 0), count = Int("ONEDIFF_PDFDOCX_COUNT", int.MaxValue), every = Int("ONEDIFF_PDFDOCX_EVERY", 1), jobs = Int("ONEDIFF_PDFDOCX_JOBS", 4);
        IEnumerable<string> all = Directory.EnumerateFiles(Path.Combine(pdfDir, "pdf"), "*.old.pdf").Select(p => Path.GetFileName(p)[..^".old.pdf".Length]);
        if (mode != "pp")
        {
            Assert.NotNull(docxDir);
            all = all.Where(id => File.Exists(Path.Combine(docxDir, "docx", id + ".old.docx")));
        }

        if (Environment.GetEnvironmentVariable("ONEDIFF_PDFDOCX_ONLY") is { Length: > 0 } only)
        {
            string[] want = only.Split(',');
            all = all.Where(want.Contains);
        }

        string[] ids = [.. all.OrderBy(x => x, StringComparer.Ordinal).Skip(from).Take(count).Where((_, i) => i % every == 0)];
        PdfRedline.Register();
        var lines = new ConcurrentDictionary<string, string>();
        var failures = new ConcurrentBag<string>();
        Parallel.For(0, ids.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            string id = ids[i];
            try
            {
                lines[id] = CheckPair(pdfDir, docxDir, mode, id, schema, both, failures);
            }
            catch (Exception e)
            {
                failures.Add($"{id}: exception {e.GetType().Name}: {e.Message.Split('\n')[0]}");
                lines[id] = id + " EXCEPTION";
            }
        });

        File.WriteAllLines(outFile, ids.Select(id => lines[id]));
        File.WriteAllLines(outFile + ".notes", Notes.Order(StringComparer.Ordinal));
        File.WriteAllLines(outFile + ".failures", failures.Order(StringComparer.Ordinal));
        Assert.True(failures.IsEmpty, $"{failures.Count} of {ids.Length} pairs failed; first: {failures.Order(StringComparer.Ordinal).FirstOrDefault()}");
    }

    private static string Clip(string s) => s.Length <= 50 ? s : s[..50] + "...";

    private static CompareDocument ReadSide(string pdfDir, string? docxDir, string id, string side, bool docx)
    {
        if (docx)
        {
            return Harness.Read(File.ReadAllBytes(Path.Combine(docxDir!, "docx", id + "." + side + ".docx")), id + "." + side + ".docx");
        }

        return CompareDocument.From(DocumentInput.FromBytes(File.ReadAllBytes(Path.Combine(pdfDir, "pdf", id + "." + side + ".pdf")), id + "." + side + ".pdf"), new PdfDocumentSource());
    }

    private static string CheckPair(string pdfDir, string? docxDir, string mode, string id, bool schema, bool both, ConcurrentBag<string> failures)
    {
        CompareDocument o = ReadSide(pdfDir, docxDir, id, "old", mode == "dp"), n = ReadSide(pdfDir, docxDir, id, "new", mode == "pd");
        var baseline = new HashSet<string>(StringComparer.Ordinal);
        if (schema)
        {
            if (mode == "dp")
            {
                baseline.UnionWith(SchemaCheck.Signatures(File.ReadAllBytes(Path.Combine(docxDir!, "docx", id + ".old.docx"))));
            }

            if (mode == "pd")
            {
                baseline.UnionWith(SchemaCheck.Signatures(File.ReadAllBytes(Path.Combine(docxDir!, "docx", id + ".new.docx"))));
            }
        }

        var line = new System.Text.StringBuilder(id);
        foreach (CompareSettings settings in new[] { CompareSettings.Legal, CompareSettings.Strict })
        {
            Comparison cmp = Comparison.Of(o, n, settings);
            if (!cmp.Verification.Passed || cmp.Verification.FallbackLevel != 0)
            {
                failures.Add($"{id} {settings.Name}: verification passed={cmp.Verification.Passed} fallback={cmp.Verification.FallbackLevel}");
                continue;
            }

            foreach (WordBuildOn on in both ? new[] { WordBuildOn.New, WordBuildOn.Old } : [WordBuildOn.New])
            {
                List<string> problems = ReconstructionGate.Run(cmp, on, out byte[] bytes, out DocxRedlineResult result);
                string tag = $"{id} {settings.Name} {on}";
                if (problems.Count > 0 && Environment.GetEnvironmentVariable("ONEDIFF_PDFDOCX_DUMP") is { Length: > 0 } dump)
                {
                    Directory.CreateDirectory(dump);
                    string stem = Path.Combine(dump, $"{id}.{settings.Name}.{on}");
                    File.WriteAllBytes(stem + ".tracked.docx", bytes);
                    File.WriteAllBytes(stem + ".accepted.docx", TrackedGate.Resolve(bytes, true));
                    File.WriteAllBytes(stem + ".rejected.docx", TrackedGate.Resolve(bytes, false));
                    ReconstructionGate.Side so = ReconstructionGate.Prepare(cmp.Old, cmp.Settings, cmp.New), sn = ReconstructionGate.Prepare(cmp.New, cmp.Settings, cmp.Old);
                    File.WriteAllBytes(stem + ".old.docx", so.Bytes);
                    File.WriteAllBytes(stem + ".new.docx", sn.Bytes);
                    File.WriteAllLines(stem + ".changes.txt", cmp.Changes.Select(c =>
                        $"#{c.Id} {c.Kind} link={c.LinkId} old[{string.Join('+', c.Old.Nodes)}]='{Clip(c.Old.Text)}' new[{string.Join('+', c.New.Nodes)}]='{Clip(c.New.Text)}'"));
                }

                if (problems.Count > 0)
                {
                    failures.Add($"{tag}: {problems.Count} problems; " + string.Join(" | ", problems.Take(3)));
                }

                using var again = new MemoryStream();
                new DocxRedline { BuildOn = on }.Create(cmp, again);
                if (!again.ToArray().AsSpan().SequenceEqual(bytes))
                {
                    failures.Add($"{tag}: two runs differ");
                }

                if (schema)
                {
                    List<string> errs = SchemaCheck.NewErrors(bytes, baseline);
                    if (errs.Count > 0)
                    {
                        failures.Add($"{tag}: schema {errs.Count}; " + string.Join(" | ", errs.Take(2)));
                    }
                }

                line.Append(' ').Append(settings.Name).Append('/').Append(on).Append('=').Append(Convert.ToHexStringLower(SHA256.HashData(bytes))[..16])
                    .Append(":nc").Append(result.NotCarried.Length);
                foreach (WordNotCarried nc in result.NotCarried)
                {
                    Notes.Add($"{tag} {nc.Category}: {nc.Message} base={string.Join('+', nc.BaseNodes)} other={string.Join('+', nc.OtherNodes)}");
                }

                foreach (IGrouping<string, WordNotCarried> g in result.NotCarried.GroupBy(x => x.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    line.Append('[').Append(g.Key).Append('x').Append(g.Count()).Append(']');
                }
            }
        }

        return line.ToString();
    }
}
