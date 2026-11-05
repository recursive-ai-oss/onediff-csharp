using OneDiff.Documents.Model;
using RecursiveAI.OneOffice;
using RecursiveAI.OneOffice.Documents;
using RecursiveAI.OneOffice.Documents.Revisions;
using RecursiveAI.OneOffice.IO;

namespace OneDiff.Word.Tests;

public class RevisionModeTests
{
    private static byte[] WithRevisions()
    {
        byte[] plain = DocFactory.Build(new DocSpec(Para.Of("The supplier shall deliver within thirty days."), Para.Of("Payment is due in sixty days.")));
        using Document doc = Document.Open(ByteSource.FromMemory(plain));
        RevisionAuthor alice = doc.Revisions.CreateAuthor("Alice", null);
        Paragraph first = doc.Body.Paragraphs().First(p => p.Text.StartsWith("The supplier", StringComparison.Ordinal));
        int at = first.Text.IndexOf("thirty", StringComparison.Ordinal);
        alice.ReplaceText(first.Markup.Node, at, "thirty".Length, "forty");
        Paragraph second = doc.Body.Paragraphs().First(p => p.Text.StartsWith("Payment", StringComparison.Ordinal));
        alice.InsertText(second.Markup.Node, second.Text.Length, " Late payment bears interest.");
        using var ms = new MemoryStream();
        doc.SaveAs(ms, OfficeFormat.Docx);
        return ms.ToArray();
    }

    private static string[] BodyTexts(CompareDocument d) => d.Nodes.Where(n => n.Kind == NodeKind.Paragraph).Select(n => n.Text).ToArray();

    [Fact]
    public void AcceptedIsTheDefault()
    {
        string[] texts = BodyTexts(Harness.Read(WithRevisions()));
        Assert.Equal(["The supplier shall deliver within forty days.", "Payment is due in sixty days. Late payment bears interest."], texts);
    }

    [Fact]
    public void AsOriginalReadsTheDocumentBeforeTheChanges()
    {
        string[] texts = BodyTexts(Harness.Read(WithRevisions(), options: new WordSourceOptions { Revisions = WordRevisionMode.AsOriginal }));
        Assert.Equal(["The supplier shall deliver within thirty days.", "Payment is due in sixty days."], texts);
    }

    [Fact]
    public void RefuseRejectsADocumentWithRevisionsAndReadsOneWithout()
    {
        var refuse = new WordSourceOptions { Revisions = WordRevisionMode.Refuse };
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => Harness.Read(WithRevisions(), options: refuse));
        Assert.Equal(WordSourceFailure.HasRevisions, WordSourceErrors.FailureOf(e));
        Assert.NotEmpty(Harness.Read(DocFactory.Build(new DocSpec(Para.Of("No changes here."))), options: refuse).Nodes);
    }

    [Fact]
    public void AcceptedAndAsOriginalOfTheSameFileCompareAsTheEditsThatWereTracked()
    {
        byte[] tracked = WithRevisions();
        var original = new WordDocumentSource(new WordSourceOptions { Revisions = WordRevisionMode.AsOriginal });
        var accepted = new WordDocumentSource();
        OneDiff.Documents.Comparison c = OneDiff.Documents.Comparison.Of(
            original.Read(DocumentInput.FromBytes(tracked, "o.docx")),
            accepted.Read(DocumentInput.FromBytes(tracked, "a.docx")));
        Assert.True(c.Verification.Passed);
        Assert.Equal(0, c.Verification.FallbackLevel);
        Assert.NotEmpty(c.Changes);
        Assert.Contains(c.Changes, ch => ch.Old.Text.Contains("thirty", StringComparison.Ordinal) && ch.New.Text.Contains("forty", StringComparison.Ordinal));
    }
}
