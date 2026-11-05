using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Word.Internal;

namespace OneDiff.Word.Tests;

public class TrackedChangesTests
{
    private static (CompareDocument Old, CompareDocument New, byte[] OldBytes, byte[] NewBytes) Pair(int seed)
    {
        var source = new WordDocumentSource();
        var gen = new Gen(seed);
        DocSpec o = gen.Document(), n = gen.Edit(o);
        byte[] ob = DocFactory.Build(o), nb = DocFactory.Build(n);
        return (source.Read(DocumentInput.FromBytes(ob, "old.docx")), source.Read(DocumentInput.FromBytes(nb, "new.docx")), ob, nb);
    }

    [Fact]
    public void SeededPairsAcceptAndReject()
    {
        int pairs = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_SWEEP"), out int n) ? n : 20;
        int first = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_FIRST"), out int f) ? f : 0;
        WordBuildOn on = Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_BUILD") == "old" ? WordBuildOn.Old : WordBuildOn.New;
        string logDir = Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_LOG") ?? "";
        var lines = new List<string>();
        int failures = 0, crashes = 0, allowed = 0, schemaFailures = 0, markEntries = 0;
        bool schema = Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_SCHEMA") == "1";
        for (int s = first; s < first + pairs; s++)
        {
            var (o, nw, ob, nb) = Pair(s);
            HashSet<string>? baseline = null;
            foreach (CompareSettings settings in new[] { CompareSettings.Legal, CompareSettings.Strict })
            {
                try
                {
                    Comparison c = Comparison.Of(o, nw, settings);
                    using var ms = new MemoryStream();
                    var res = new DocxRedline { BuildOn = on }.Create(c, ms);
                    var filtered = new List<string>();
                    List<string> p = TrackedGate.Problems(o, nw, ms.ToArray(), settings, on, res.NotCarried, detail: logDir.Length > 0, filtered: filtered);
                    allowed += filtered.Count;
                    markEntries += res.NotCarried.Count(x => x.Category.StartsWith("paragraph-mark", StringComparison.Ordinal));
                    if (schema)
                    {
                        baseline ??= [.. SchemaCheck.Signatures(ob), .. SchemaCheck.Signatures(nb)];
                        List<string> errs = SchemaCheck.NewErrors(ms.ToArray(), baseline);
                        if (errs.Count > 0)
                        {
                            schemaFailures++;
                            lines.Add($"seed {s} {settings.Name}: SCHEMA {errs.Count} new errors; " + string.Join(" | ", errs.Take(3)));
                        }
                    }

                    if (p.Count > 0)
                    {
                        failures++;
                        lines.Add($"seed {s} {settings.Name}: {p.Count} problems; " + string.Join(" | ", p.Take(logDir.Length > 0 ? 20 : 3)));
                        if (logDir.Length > 0)
                        {
                            Directory.CreateDirectory(logDir);
                            File.WriteAllBytes(Path.Combine(logDir, $"{s}-{settings.Name}-out.docx"), ms.ToArray());
                            File.WriteAllBytes(Path.Combine(logDir, $"{s}-old.docx"), ob);
                            File.WriteAllBytes(Path.Combine(logDir, $"{s}-new.docx"), nb);
                        }
                    }
                }
                catch (Exception ex)
                {
                    crashes++;
                    lines.Add($"seed {s} {settings.Name}: CRASH {ex.GetType().Name} {ex.Message.Split('\n')[0]} @ {ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("OneDiff.Word", StringComparison.Ordinal))?.Trim()}");
                }
            }
        }

        string summary = $"tracked sweep: pairs={pairs} first={first} build={on} failures={failures} crashes={crashes} filteredAsNotCarried={allowed} schemaFailures={schemaFailures} paragraphMarkEntries={markEntries}";
        lines.Insert(0, summary);
        if (Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_OUT") is { Length: > 0 } path)
        {
            File.WriteAllLines(path, lines);
        }

        Assert.True(failures + crashes + schemaFailures == 0, string.Join("\n", lines.Take(8)));
    }
}

public class TrackedTextBoxTests
{
    private static DocSpec Doc(string box) => new(
        Para.Of("Intro paragraph that stays."),
        new Para(new T("The terms are in "), new Box(box), new T(" the box.")),
        Para.Of("Closing paragraph."));

    [Theory]
    [InlineData(WordBuildOn.New)]
    [InlineData(WordBuildOn.Old)]
    public void EditedTextBoxIsTrackedInPlace(WordBuildOn on)
    {
        byte[] ob = DocFactory.Build(Doc("Payment within thirty days")), nb = DocFactory.Build(Doc("Payment within sixty days"));
        var source = new WordDocumentSource();
        CompareDocument o = source.Read(DocumentInput.FromBytes(ob, "o.docx")), n = source.Read(DocumentInput.FromBytes(nb, "n.docx"));
        Comparison c = Comparison.Of(o, n, CompareSettings.Legal);
        var trace = new List<string>();
        TrackedWriter.Trace = trace.Add;
        try
        {
            using var ms = new MemoryStream();
            DocxRedlineResult res = new DocxRedline { BuildOn = on }.Create(c, ms);
            Assert.Empty(TrackedGate.Problems(o, n, ms.ToArray(), c.Settings, on, res.NotCarried));
        }
        finally
        {
            TrackedWriter.Trace = null;
        }

        Assert.DoesNotContain(trace, t => t.StartsWith("replace", StringComparison.Ordinal));
    }
}

public class TrackedDebug
{
    private static string Dump(CompareDocument d) => string.Join("\n", d.Nodes.Where(n => n.Kind is not (NodeKind.Property)).Select(n => $"{new string(' ', n.Depth * 2)}{n.Id} {n.Kind} '{(n.Text.Length > 40 ? n.Text[..40] : n.Text).Replace("\uFFFC", "#")}'"));

    [Fact]
    public void DebugSeed()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_SEED"), out int seed))
        {
            return;
        }

        var source = new WordDocumentSource();
        var gen = new Gen(seed);
        DocSpec o = gen.Document(), n = gen.Edit(o);
        CompareDocument oldDoc = source.Read(DocumentInput.FromBytes(DocFactory.Build(o), "o.docx")), newDoc = source.Read(DocumentInput.FromBytes(DocFactory.Build(n), "n.docx"));
        Comparison c = Comparison.Of(oldDoc, newDoc, Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_SETTINGS") == "strict" ? CompareSettings.Strict : CompareSettings.Legal);
        using var ms = new MemoryStream();
        var traceLines = new List<string>();
        TrackedWriter.Trace = traceLines.Add;
        var res = new DocxRedline { BuildOn = Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_BUILD") == "old" ? WordBuildOn.Old : WordBuildOn.New }.Create(c, ms);
        TrackedWriter.Trace = null;
        File.WriteAllLines(Path.Combine(Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_DEBUG_DIR") ?? ".", "trace.txt"), traceLines);
        string dir = Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_DEBUG_DIR") ?? Path.Combine(Path.GetTempPath(), "onediff-tracked-debug");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(dir + "/out.docx", ms.ToArray());
        File.WriteAllBytes(dir + "/o.docx", DocFactory.Build(o));
        File.WriteAllBytes(dir + "/n.docx", DocFactory.Build(n));
        File.WriteAllBytes(dir + "/acc.docx", TrackedGate.Resolve(ms.ToArray(), true));
        File.WriteAllBytes(dir + "/rej.docx", TrackedGate.Resolve(ms.ToArray(), false));
        File.WriteAllText(dir + "/segs.txt", string.Join("\n", newDoc.Nodes.Where(x => x.Kind is NodeKind.Paragraph or NodeKind.ListItem && x.Segments.Length > 0 && x.Text.EndsWith("term ", StringComparison.Ordinal)).Select(x => $"node {x.Id} '{x.Text.Replace("\t", "<T>")}' folds=" + string.Join(";", x.Folds.Select(f => f.Start + "+" + f.Length + ":" + f.Category + ":" + f.Original.Length)) + " segs=" + string.Join(";", x.Segments.Select(sg => sg.Start + "+" + sg.Length + "@" + sg.Anchor.Reference[(sg.Anchor.Reference.IndexOf('#', StringComparison.Ordinal) + 1)..])))));
        File.WriteAllText(dir + "/old.txt", Dump(oldDoc));
        File.WriteAllText(dir + "/new.txt", Dump(newDoc));
        File.WriteAllText(dir + "/acc.txt", Dump(source.Read(DocumentInput.FromBytes(File.ReadAllBytes(dir + "/acc.docx"), "a.docx"))));
        File.WriteAllText(dir + "/rej.txt", Dump(source.Read(DocumentInput.FromBytes(File.ReadAllBytes(dir + "/rej.docx"), "r.docx"))));
        File.WriteAllText(dir + "/problems.txt", string.Join("\n", TrackedGate.Problems(oldDoc, newDoc, ms.ToArray(), c.Settings, WordBuildOn.New, res.NotCarried, detail: true)));
        File.WriteAllText(dir + "/notes.txt", string.Join("\n", res.NotCarried.Select(x => x.Category + ": " + x.Message)) + "\n" + string.Join("\n", c.Changes.Select(x => $"{x.Kind} link={x.LinkId} old={string.Join(",", x.Old.Nodes)} new={string.Join(",", x.New.Nodes)} edits=" + string.Join(";", x.Edits.Select(e => $"[{e.OldStart}+{e.OldLength}->{e.NewStart}+{e.NewLength} '{e.OldText.Replace("\t", "<T>")}'->'{e.NewText.Replace("\t", "<T>")}']")))));
    }
}
