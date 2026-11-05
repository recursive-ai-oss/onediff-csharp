using OneDiff.Documents;
using OneDiff.Documents.Model;

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
        int failures = 0, crashes = 0;
        for (int s = first; s < first + pairs; s++)
        {
            var (o, nw, ob, nb) = Pair(s);
            foreach (CompareSettings settings in new[] { CompareSettings.Legal, CompareSettings.Strict })
            {
                try
                {
                    Comparison c = Comparison.Of(o, nw, settings);
                    using var ms = new MemoryStream();
                    new DocxRedline { BuildOn = on }.Create(c, ms);
                    List<string> p = TrackedGate.Problems(o, nw, ms.ToArray(), settings, on, detail: logDir.Length > 0);
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

        string summary = $"tracked sweep: pairs={pairs} first={first} build={on} failures={failures} crashes={crashes}";
        lines.Insert(0, summary);
        if (Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_OUT") is { Length: > 0 } path)
        {
            File.WriteAllLines(path, lines);
        }

        Assert.True(failures + crashes == 0, string.Join("\n", lines.Take(8)));
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
        var res = new DocxRedline { BuildOn = Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_BUILD") == "old" ? WordBuildOn.Old : WordBuildOn.New }.Create(c, ms);
        string dir = Environment.GetEnvironmentVariable("ONEDIFF_TRACKED_DEBUG_DIR") ?? Path.Combine(Path.GetTempPath(), "onediff-tracked-debug");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(dir + "/out.docx", ms.ToArray());
        File.WriteAllBytes(dir + "/acc.docx", TrackedGate.Resolve(ms.ToArray(), true));
        File.WriteAllBytes(dir + "/rej.docx", TrackedGate.Resolve(ms.ToArray(), false));
        File.WriteAllText(dir + "/old.txt", Dump(oldDoc));
        File.WriteAllText(dir + "/new.txt", Dump(newDoc));
        File.WriteAllText(dir + "/acc.txt", Dump(source.Read(DocumentInput.FromBytes(File.ReadAllBytes(dir + "/acc.docx"), "a.docx"))));
        File.WriteAllText(dir + "/rej.txt", Dump(source.Read(DocumentInput.FromBytes(File.ReadAllBytes(dir + "/rej.docx"), "r.docx"))));
        File.WriteAllText(dir + "/problems.txt", string.Join("\n", TrackedGate.Problems(oldDoc, newDoc, ms.ToArray(), c.Settings, WordBuildOn.New, detail: true)));
        File.WriteAllText(dir + "/notes.txt", string.Join("\n", res.NotCarried.Select(x => x.Category + ": " + x.Message)) + "\n" + string.Join("\n", c.Changes.Select(x => $"{x.Kind} link={x.LinkId} old={string.Join(",", x.Old.Nodes)} new={string.Join(",", x.New.Nodes)} edits=" + string.Join(";", x.Edits.Select(e => $"[{e.OldStart}+{e.OldLength}->{e.NewStart}+{e.NewLength} '{e.OldText.Replace("\t", "<T>")}'->'{e.NewText.Replace("\t", "<T>")}']")))));
    }
}
