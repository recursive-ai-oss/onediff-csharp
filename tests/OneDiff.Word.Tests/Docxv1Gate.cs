using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Word.Tests;

/// <summary>
/// Runs the accept/reject gate (and the schema check) over a directory of Word pairs named <c>ID.old.docx</c> and <c>ID.new.docx</c>.
/// Environment: ONEDIFF_DOCXV1_DIR (the directory; the test does nothing without it), ONEDIFF_DOCXV1_OUT (where to write the tracked
/// files as <c>OUT/SETTINGS-BUILD/ID.docx</c>), ONEDIFF_DOCXV1_BUILD (new or old), ONEDIFF_DOCXV1_FIRST and _COUNT (a slice of the
/// sorted ids), ONEDIFF_DOCXV1_SCHEMA (1 to validate), ONEDIFF_DOCXV1_REPORT (a summary file).
/// </summary>
public class Docxv1Gate
{
    [Fact]
    public void AcceptRejectAndSchema()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEDIFF_DOCXV1_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        string? outDir = Environment.GetEnvironmentVariable("ONEDIFF_DOCXV1_OUT");
        WordBuildOn on = Environment.GetEnvironmentVariable("ONEDIFF_DOCXV1_BUILD") == "old" ? WordBuildOn.Old : WordBuildOn.New;
        bool schema = Environment.GetEnvironmentVariable("ONEDIFF_DOCXV1_SCHEMA") == "1";
        int first = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_DOCXV1_FIRST"), out int f) ? f : 0;
        int count = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_DOCXV1_COUNT"), out int c) ? c : int.MaxValue;
        string[] ids = [.. Directory.EnumerateFiles(dir, "*.old.docx").Select(p => Path.GetFileName(p)[..^".old.docx".Length]).OrderBy(x => x, StringComparer.Ordinal).Skip(first).Take(count)];
        var source = new WordDocumentSource();
        var report = new StringBuilder();
        int runs = 0, failures = 0, crashes = 0, schemaErrors = 0, schemaChecked = 0, allowed = 0, notCarriedTotal = 0;
        foreach (string id in ids)
        {
            byte[] ob = File.ReadAllBytes(Path.Combine(dir, id + ".old.docx")), nb = File.ReadAllBytes(Path.Combine(dir, id + ".new.docx"));
            CompareDocument o = source.Read(DocumentInput.FromBytes(ob, id + ".old.docx")), n = source.Read(DocumentInput.FromBytes(nb, id + ".new.docx"));
            HashSet<string>? baseline = null;
            foreach (CompareSettings settings in new[] { CompareSettings.Legal, CompareSettings.Strict })
            {
                runs++;
                try
                {
                    Comparison cmp = Comparison.Of(o, n, settings);
                    using var ms = new MemoryStream();
                    DocxRedlineResult res = new DocxRedline { BuildOn = on }.Create(cmp, ms);
                    byte[] bytes = ms.ToArray();
                    notCarriedTotal += res.NotCarried.Length;
                    var filtered = new List<string>();
                    List<string> p = TrackedGate.Problems(o, n, bytes, settings, on, res.NotCarried, detail: false, filtered: filtered);
                    allowed += filtered.Count;
                    if (p.Count > 0)
                    {
                        failures++;
                        report.AppendLine($"FAIL {id} {settings.Name}: " + string.Join(" | ", p.Take(3)));
                    }

                    if (outDir is { Length: > 0 })
                    {
                        string d = Path.Combine(outDir, settings.Name + "-" + on);
                        Directory.CreateDirectory(d);
                        File.WriteAllBytes(Path.Combine(d, id + ".docx"), bytes);
                    }

                    if (schema)
                    {
                        baseline ??= [.. SchemaCheck.Signatures(ob), .. SchemaCheck.Signatures(nb)];
                        schemaChecked++;
                        List<string> errs = SchemaCheck.NewErrors(bytes, baseline);
                        if (errs.Count > 0)
                        {
                            schemaErrors++;
                            report.AppendLine($"SCHEMA {id} {settings.Name}: {errs.Count} new; " + string.Join(" | ", errs.Take(2)));
                        }
                    }
                }
                catch (Exception ex)
                {
                    crashes++;
                    report.AppendLine($"CRASH {id} {settings.Name}: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
                }
            }
        }

        string summary = $"docx gate: dir={dir} build={on} ids={ids.Length} runs={runs} failures={failures} crashes={crashes} filteredByNotCarried={allowed} notCarriedEntries={notCarriedTotal} schemaChecked={schemaChecked} outputsWithNewSchemaErrors={schemaErrors}";
        if (Environment.GetEnvironmentVariable("ONEDIFF_DOCXV1_REPORT") is { Length: > 0 } rp)
        {
            File.WriteAllText(rp, summary + "\n" + report);
        }

        Assert.True(failures + crashes + schemaErrors == 0, summary + "\n" + string.Join("\n", report.ToString().Split('\n').Take(10)));
    }
}
