using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Word.Tests;

/// <summary>Small pairs for the cases the generated sweeps found, run through the accept/reject gate built on either document.</summary>
public class TrackedRegressionTests
{
    private static Para Link(string text) => new(new T(text) { Link = "https://example.com/" });

    public static TheoryData<string> Cases() => ["link-deleted", "link-inserted", "link-in-moved-chain", "tab-before-deleted-word", "text-beside-link", "object-beside-inserted-text"];

    private static (DocSpec Old, DocSpec New) Pair(string name) => name switch
    {
        "link-deleted" => (new(Para.Of("alpha beta gamma delta."), Link("link text here."), Para.Of("omega psi chi phi.")), new(Para.Of("alpha beta gamma delta."), Para.Of("omega psi chi phi."))),
        "link-inserted" => (new(Para.Of("alpha beta gamma delta."), Para.Of("omega psi chi phi.")), new(Para.Of("alpha beta gamma delta."), Link("link text here."), Para.Of("omega psi chi phi."))),
        "link-in-moved-chain" => (
            new(Para.Of("alpha beta gamma delta epsilon zeta."), Para.Of("move one two three four five six."), Link("link text here and some more text."), Para.Of("move seven eight nine ten eleven twelve."), Para.Of("omega psi chi phi omicron pi."), Para.Of("last of the paragraphs here.")),
            new(Para.Of("alpha beta gamma delta epsilon zeta."), Para.Of("omega psi chi phi omicron pi."), Para.Of("last of the paragraphs here."), Para.Of("move one two three four five six."), Link("link text here and some more text."), Para.Of("move seven eight nine ten eleven twelve."), Para.Of("brand new tail paragraph appears."))),

        // A deleted tab and word at the end of a paragraph (a fold of the space and the tab puts the tab's segment at the fold's start).
        "tab-before-deleted-word" => (
            new(Para.Of("alpha beta gamma delta epsilon zeta."), new Para(new T("Invoice services interest majeure law amount. "), new TabChar(), new T("majeure"))),
            new(Para.Of("alpha beta gamma delta epsilon zeta."), Para.Of("Invoice services interest majeure law amount. "))),

        // Text added after a hyperlink inside a paragraph: it must not become part of the link.
        "text-beside-link" => (
            new(Para.Of("alpha beta gamma delta epsilon zeta."), new Para(new T("Intro text of the paragraph. "), new T("linked words") { Link = "https://example.com/a" }, new T(" and the end."))),
            new(Para.Of("alpha beta gamma delta epsilon zeta."), new Para(new T("Intro text of the paragraph. "), new T("extra words "), new T("linked words") { Link = "https://example.com/a" }, new T(" and the end.")))),

        // Text added right after a picture: it goes after the picture's run, not before it.
        _ => (
            new(Para.Of("alpha beta gamma delta epsilon zeta."), new Para(new T("Look at the picture. "), new Img(DocFactory.Png(3), "a picture"))),
            new(Para.Of("alpha beta gamma delta epsilon zeta."), new Para(new T("Look at the picture. "), new Img(DocFactory.Png(3), "a picture"), new T(" and then read on.")))),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void AcceptAndRejectGiveTheTwoDocuments(string name)
    {
        (DocSpec o, DocSpec n) = Pair(name);
        var source = new WordDocumentSource();
        CompareDocument od = source.Read(DocumentInput.FromBytes(DocFactory.Build(o), "o.docx")), nd = source.Read(DocumentInput.FromBytes(DocFactory.Build(n), "n.docx"));
        foreach (CompareSettings settings in new[] { CompareSettings.Legal, CompareSettings.Strict })
        {
            foreach (WordBuildOn on in new[] { WordBuildOn.New, WordBuildOn.Old })
            {
                Comparison c = Comparison.Of(od, nd, settings);
                using var ms = new MemoryStream();
                DocxRedlineResult res = new DocxRedline { BuildOn = on }.Create(c, ms);
                Assert.Empty(TrackedGate.Problems(od, nd, ms.ToArray(), settings, on, res.NotCarried, detail: true));
                Assert.Empty(SchemaCheck.NewErrors(ms.ToArray(), SchemaCheck.Signatures(DocFactory.Build(o)), SchemaCheck.Signatures(DocFactory.Build(n))));
            }
        }
    }

    [Fact]
    public void OutputIsTheSameEveryTimeAndCarriesNoDateUnlessAsked()
    {
        (DocSpec o, DocSpec n) = Pair("link-in-moved-chain");
        var source = new WordDocumentSource();
        CompareDocument od = source.Read(DocumentInput.FromBytes(DocFactory.Build(o), "o.docx")), nd = source.Read(DocumentInput.FromBytes(DocFactory.Build(n), "n.docx"));
        byte[] Write(DocxRedline form)
        {
            using var ms = new MemoryStream();
            form.Create(Comparison.Of(od, nd), ms);
            return ms.ToArray();
        }

        byte[] a = Write(new DocxRedline()), b = Write(new DocxRedline());
        Assert.Equal(a, b);
        string xml = System.Text.Encoding.UTF8.GetString(ReadMain(a));
        Assert.DoesNotContain("w:author=\"OneDiff\" w:date=\"20", xml, StringComparison.Ordinal);
        Assert.NotEqual(a, Write(new DocxRedline { Author = "Reviewer", Date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) }));
    }

    private static byte[] ReadMain(byte[] docx)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(docx));
        using Stream s = zip.GetEntry("word/document.xml")!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
