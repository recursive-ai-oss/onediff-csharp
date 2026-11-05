using System.Collections.Immutable;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Merge;
using OneDiff.Ocr.Tests;

namespace OneDiff.Samples.Tests;

/// <summary>Runs every sample in samples/OneDiff.Samples on small documents written here and checks what each one promises.</summary>
public sealed class SampleTests : IDisposable
{
    private const string Old1 = "The supplier shall deliver the goods within thirty days of the order.";
    private const string Old2 = "Payment is due in sixty days from the invoice date.";
    private const string Old3 = "This agreement is governed by the laws of Delaware.";
    private const string New1 = "The supplier shall deliver the goods within forty days of the order.";

    private readonly string dir = Path.Combine(Path.GetTempPath(), "onediff-samples-" + Guid.NewGuid().ToString("N"));

    public SampleTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Write(string name, string text)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string P(string name) => Path.Combine(dir, name);

    private (string Old, string New) TextPair() =>
        (Write("v1.txt", $"{Old1}\n\n{Old2}\n\n{Old3}\n"), Write("v2.txt", $"{New1}\n\n{Old2}\n\n{Old3}\n"));

    private (string Old, string New) PdfPair() =>
        (Write("v1.pdf", Scans.DigitalPdf([Old1, Old2, Old3])), Write("v2.pdf", Scans.DigitalPdf([New1, Old2, Old3])));

    [Fact]
    public void DiffTwoStringsShowsTheChangedWords()
    {
        string md = Samples.DiffTwoStrings("pay within thirty days", "pay within forty days");
        Assert.Contains("thirty", md, StringComparison.Ordinal);
        Assert.Contains("forty", md, StringComparison.Ordinal);
    }

    [Fact]
    public void AUnifiedPatchTurnsTheOldTextIntoTheNew()
    {
        string oldText = "one\ntwo\nthree\nfour\n", newText = "one\n2\nthree\nfour\nfive\n";
        Assert.Equal(newText, Samples.MakeAndApplyAUnifiedPatch(oldText, newText));
    }

    [Fact]
    public void AThreeWayMergeTakesBothEditsAndReportsAConflict()
    {
        string b = "a\nb\nc\nd\ne\n";
        TextMerge clean = Samples.ThreeWayMerge(b, "A\nb\nc\nd\ne\n", "a\nb\nc\nd\nE\n");
        Assert.False(clean.HasConflicts);
        Assert.Equal("A\nb\nc\nd\nE\n", clean.Text);
        Assert.True(Samples.ThreeWayMerge(b, "a\nx\nc\nd\ne\n", "a\ny\nc\nd\ne\n").HasConflicts);
    }

    [Fact]
    public void TwoPdfsMakeARenderedRedlineAndMarkedPages()
    {
        (string a, string b) = PdfPair();
        Samples.ComparePdfsToARedlinePdf(a, b, P("redline.pdf"));
        Samples.MarkChangesOnThePages(a, b, P("marked.pdf"));
        foreach (string f in new[] { "redline.pdf", "marked.pdf" })
        {
            byte[] bytes = File.ReadAllBytes(P(f));
            Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5), StringComparison.Ordinal);
        }

        // The markup keeps the new file's bytes and appends the annotations.
        byte[] marked = File.ReadAllBytes(P("marked.pdf")), original = File.ReadAllBytes(b);
        Assert.True(marked.Length > original.Length);
        Assert.True(marked.AsSpan(0, original.Length).SequenceEqual(original));
    }

    [Fact]
    public void TheChangeListSurvivesAJsonRoundTrip()
    {
        (string a, string b) = TextPair();
        Comparison read = Samples.SaveTheChangeListAndReadItBack(a, b, P("changes.json"));
        Comparison direct = Comparison.Of(a, b);
        Assert.Equal(direct.Changes.Length, read.Changes.Length);
        Assert.NotEmpty(read.Changes);
        Assert.True(read.Verification.Passed);
    }

    [Fact]
    public void HtmlSideBySideIsOnePage()
    {
        (string a, string b) = TextPair();
        Samples.HtmlSideBySide(a, b, P("sbs.html"));
        string html = File.ReadAllText(P("sbs.html"));
        Assert.Contains("<html", html, StringComparison.Ordinal);
        Assert.Contains("forty", html, StringComparison.Ordinal);
        Assert.Contains("thirty", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoScansAreComparedAndMarked()
    {
        string a = Write("s1.png", Scans.Png([Old1, Old2, Old3])), b = Write("s2.png", Scans.Png([New1, Old2, Old3]));
        ImmutableArray<Change> flagged = Samples.CompareScans(a, b, P("scan-marked.pdf"));
        Assert.True(new FileInfo(P("scan-marked.pdf")).Length > 0);
        Assert.All(flagged, static c => Assert.True(c.PossibleOcrError));
    }

    [Fact]
    public void TheSubstantiveChangesCarryResolvedValues()
    {
        // The built-in recognizer reads numerals; written-out numbers ("thirty") need the .Nlp package.
        string a = Write("n1.txt", "Payment is due within 30 days of the invoice.\n"), b = Write("n2.txt", "Payment is due within 60 days of the invoice.\n");
        string[] lines = [.. Samples.WalkTheSubstantiveChanges(a, b)];
        Assert.Contains(lines, static l => l.Contains("30 d", StringComparison.Ordinal) && l.Contains("60 d", StringComparison.Ordinal));
    }

    [Fact]
    public void ReviewersAreCombinedAndAVersionChainIsReported()
    {
        string original = Write("o.txt", $"{Old1}\n\n{Old2}\n\n{Old3}\n");
        string alice = Write("alice.txt", $"{New1}\n\n{Old2}\n\n{Old3}\n");
        string bob = Write("bob.txt", $"{Old1}\n\n{Old2}\n\nThis agreement is governed by the laws of New York.\n");
        Samples.CombineReviewers(original, [alice, bob], P("combined.html"));
        Samples.CompareAVersionChain([original, alice, bob], P("chain.html"));
        Assert.Contains("<html", File.ReadAllText(P("combined.html")), StringComparison.Ordinal);
        Assert.Contains("<html", File.ReadAllText(P("chain.html")), StringComparison.Ordinal);
    }

    [Fact]
    public void PinningTwoParagraphsTurnsADeleteAndInsertIntoOneReplacement()
    {
        const string oldText = "Intro paragraph that stays.\n\nThe tenant pays rent on the first of each month by bank transfer.\n\nClosing paragraph that stays.\n";
        const string newText = "Intro paragraph that stays.\n\nAll sums owed by the lessee fall due monthly, in advance, through wire payment.\n\nClosing paragraph that stays.\n";
        (Comparison before, Comparison after) = Samples.PinAnAlignmentAndRerun(oldText, newText, "The tenant", "All sums");
        Assert.True(before.Verification.Passed);
        Assert.True(after.Verification.Passed);
        Assert.Contains(after.Alignment.Links, static l => l.Pinned);
        Assert.Contains(after.Changes, static c => c.Old.IsPresent && c.New.IsPresent);
    }

    [Fact]
    public void AnOfficeFileWithoutTheWordPackageNamesThePackage()
    {
        // The first bytes of a zip-based Office file are enough to say which package is missing.
        string a = Write("v1.docx", [(byte)'P', (byte)'K', 3, 4, 0, 0, 0, 0]);
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => Comparison.Of(a, a));
        Assert.Contains("RecursiveAI.OneDiff.Word", e.Message, StringComparison.Ordinal);
        Assert.Contains("Register", e.Message, StringComparison.Ordinal);

        string t = TextPair().Old;
        e = Assert.Throws<NotSupportedException>(() => Redline.Create(t, t, P("out.docx")));
        Assert.Contains("RecursiveAI.OneDiff.Word", e.Message, StringComparison.Ordinal);
    }
}
