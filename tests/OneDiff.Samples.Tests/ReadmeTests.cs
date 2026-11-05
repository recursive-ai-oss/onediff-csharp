using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Serialization;
using OneDiff.Merge;
using OneDiff.Pdf;

namespace OneDiff.Samples.Tests;

/// <summary>The code blocks of the README, as written, so that they keep compiling (and the part that needs no files keeps running).</summary>
public class ReadmeTests
{
    // The happy-path block. It is compiled, not run: it reads files that are not here.
    private static void HappyPath(string oldText, string newText, string baseText, string ours, string theirs)
    {
        string marked = Diff.ToMarkdownInline(Diff.Words(oldText, newText));
        string patch = Diff.Unified(oldText, newText);
        TextMerge both = Diff.Merge(baseText, ours, theirs);      // both.Text, both.HasConflicts

        PdfRedline.Register();                                    // once at startup
        Redline.Create("v1.pdf", "v2.pdf", "markup.pdf");

        var cmp = Comparison.Of("v1.pdf", "v2.pdf");
        cmp.SaveAs("redline.pdf", PdfRedline.Rendered);
        cmp.SaveAs("side-by-side.html", RedlineForm.HtmlSideBySide);

        foreach (var c in cmp.Changes.Where(c => c.IsSubstantive))
            Console.WriteLine($"{c.Kind}: {c.Old.Text} -> {c.New.Text}");
        Redline.Create("v1.pdf", "v2.pdf", "changes.json");
        Comparison again = ComparisonJson.Read(File.ReadAllBytes("changes.json"));
        _ = (marked, patch, both, again);
    }

    [Fact]
    public void TheHappyPathBlockCompiles()
    {
        Action run = () => HappyPath("a", "b", "c", "d", "e");
        Assert.NotNull(run);
    }

    [Fact]
    public void TheLowLevelBlockRuns()
    {
        const string oldText = "The term is 30 days.\n\nNotices must be written.";
        const string newText = "The term is 60 days.\n\nNotices must be written.";

        CompareDocument a = CompareDocument.FromText(oldText, "old");
        CompareDocument b = CompareDocument.FromText(newText, "new");
        int oldNodeId = a.Nodes.First(n => n.Text.StartsWith("The term", StringComparison.Ordinal)).Id;
        int newNodeId = b.Nodes.First(n => n.Text.StartsWith("The term", StringComparison.Ordinal)).Id;

        Alignment alignment = Stages.Align(a, b);
        alignment = alignment.Pin(oldNodeId, newNodeId);          // a reviewer's correction

        Comparison result = Stages.Verify(Stages.Classify(Stages.Group(Stages.DiffPairs(alignment))));
        Assert.True(result.Verification.Passed);
        Assert.Single(result.Changes);
    }
}
