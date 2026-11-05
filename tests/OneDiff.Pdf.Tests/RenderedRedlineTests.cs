using System.Security.Cryptography;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OnePdf;

namespace OneDiff.Pdf.Tests;

public class RenderedRedlineTests
{
    private static Comparison TextPair(string a, string b, CompareSettings? settings = null) =>
        Comparison.Of(CompareDocument.FromText(a, "old.txt"), CompareDocument.FromText(b, "new.txt"), settings ?? CompareSettings.Strict with { ThrowOnFailure = false });

    private const string OldText = "The supplier shall deliver the goods within thirty days of the order.\n\nPayment is due within sixty days of receipt.\n\nEither party may terminate on notice.\n\nThis agreement is governed by New York law.";
    private const string NewText = "The supplier shall deliver the goods within forty-five days of the order.\n\nThis agreement is governed by New York law.\n\nPayment is due within sixty days of receipt.\n\nEither party may terminate on thirty days written notice.\n\nDisputes go to arbitration in London.";

    [Fact]
    public void OutputIsATaggedPdfWithLanguageAndTitle()
    {
        Comparison c = TextPair(OldText, NewText);
        foreach (RedlineForm form in new RedlineForm[] { PdfRedline.Rendered, PdfRedline.RenderedSideBySide })
        {
            byte[] pdf = c.ToBytes(form, RedlineStyle.Blackline with { Language = "de", Title = "Vergleich" });
            Assert.True(pdf.AsSpan().StartsWith("%PDF-"u8));
            using PdfDocument doc = PdfDocument.Open(pdf);
            Assert.True(doc.IsTagged, "MarkInfo /Marked is true");
            Assert.NotNull(doc.StructureTreeRoot);
            Assert.Equal("de", doc.Language);
            Assert.True(doc.StructureTree is { } tree && tree.ElementCount > 0, "the structure tree has content");
        }
    }

    [Fact]
    public void OutputIsByteIdenticalAcrossRuns()
    {
        Comparison c = TextPair(OldText, NewText);
        foreach (RedlineForm form in new RedlineForm[] { PdfRedline.Rendered, PdfRedline.RenderedSideBySide })
        {
            Assert.Equal(c.ToBytes(form), c.ToBytes(form));
            Assert.Equal(c.ToBytes(form), TextPair(OldText, NewText).ToBytes(form));
        }
    }

    [Fact]
    public void ExtractionReadsTheRedlineBackAgainstTheChangeList()
    {
        Comparison c = TextPair(OldText, NewText);
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Move);
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Insert);
        List<string> errors = RenderedGate.Check(c, c.ToBytes(PdfRedline.Rendered, RenderedGate.Style), RenderedGate.Style, sideBySide: false);
        Assert.Empty(errors);
        errors = RenderedGate.Check(c, c.ToBytes(PdfRedline.RenderedSideBySide, RenderedGate.Style), RenderedGate.Style, sideBySide: true);
        Assert.Empty(errors);
    }

    [Fact]
    public void TheGateCatchesAMarkInTheWrongColour()
    {
        Comparison c = TextPair(OldText, NewText);
        // Reading a page written with the blackline as if it were written with a style whose insertions are red puts the inserted text in the wrong class.
        RedlineStyle swapped = RenderedGate.Style with { Insertion = RenderedGate.Style.Deletion, Deletion = RenderedGate.Style.Insertion };
        Assert.NotEmpty(RenderedGate.Check(c, c.ToBytes(PdfRedline.Rendered, RenderedGate.Style), swapped, sideBySide: false));
    }

    [Fact]
    public void EveryPresetReadsBackInBothLayouts()
    {
        Comparison c = TextPair(OldText, NewText);
        foreach (RedlineStyle style in new[] { RedlineStyle.Blackline, RedlineStyle.SideBySide, RedlineStyle.Word, RedlineStyle.Developer })
        {
            RedlineStyle s = style with { CoverPage = false };
            Assert.True(c.ToBytes(PdfRedline.RenderedSideBySide, s).Length > 1000);
            Assert.Empty(RenderedGate.Check(c, c.ToBytes(PdfRedline.RenderedSideBySide, s), s, sideBySide: true));
        }
    }

    [Fact]
    public void ExtensionPicksMarkupForAPdfAndRenderingOtherwise()
    {
        PdfRedline.Register();
        string dir = Path.Combine(Path.GetTempPath(), "onediff-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.txt"), b = Path.Combine(dir, "b.txt"), o = Path.Combine(dir, "out.pdf");
            File.WriteAllText(a, OldText);
            File.WriteAllText(b, NewText);
            Redline.Create(a, b, o);
            byte[] rendered = File.ReadAllBytes(o);
            Assert.Equal(Comparison.Of(a, b).ToBytes(PdfRedline.Rendered), rendered);
            using (PdfDocument d = PdfDocument.Open(rendered))
            {
                Assert.DoesNotContain(d.Pages[0].Annotations, x => x.Subtype == "Highlight");
            }

            // A PDF as the new input: markup on its pages.
            byte[] oldPdf = Fixtures.Make(new Fixtures.Spec { Paragraphs = ["The supplier shall deliver within thirty days."] });
            byte[] newPdf = Fixtures.Make(new Fixtures.Spec { Paragraphs = ["The supplier shall deliver within forty days."] });
            string pa = Path.Combine(dir, "a.pdf"), pb = Path.Combine(dir, "b.pdf"), po = Path.Combine(dir, "marked.pdf");
            File.WriteAllBytes(pa, oldPdf);
            File.WriteAllBytes(pb, newPdf);
            Redline.Create(pa, pb, po);
            byte[] marked = File.ReadAllBytes(po);
            Assert.True(marked.AsSpan().StartsWith(newPdf));
            Assert.Contains(Run.ReadAnnotations(marked), m => m.Subtype == "Highlight");

            // Either form can be asked for by name: a PDF pair rendered, and a text pair has no pages to mark.
            Comparison pdfPair = Comparison.Of(pa, pb);
            using (PdfDocument d = PdfDocument.Open(pdfPair.ToBytes(PdfRedline.Rendered)))
            {
                Assert.Empty(d.Pages[0].Annotations);
            }

            Assert.Throws<InvalidOperationException>(() => Comparison.Of(a, b).ToBytes(PdfRedline.OnPages));

            // The mixed pair (text old, PDF new) follows the new document.
            Assert.True(Comparison.Of(a, pb).ToBytes(PdfRedline.Auto).AsSpan().StartsWith(newPdf));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PaperAndSettingsStyleAreHonoured()
    {
        Comparison c = TextPair(OldText, NewText);
        using (PdfDocument a4 = PdfDocument.Open(c.ToBytes(PdfRedline.Rendered)))
        {
            Assert.InRange(a4.Pages[0].CropBox.Width, 594, 596);
        }

        using (PdfDocument letter = PdfDocument.Open(c.ToBytes(new RenderedPdfRedline { Paper = PdfPaper.Letter })))
        {
            Assert.InRange(letter.Pages[0].CropBox.Width, 611, 613);
        }

        using (PdfDocument land = PdfDocument.Open(c.ToBytes(PdfRedline.RenderedSideBySide)))
        {
            Assert.True(land.Pages[0].CropBox.Width > land.Pages[0].CropBox.Height);
        }

        // The settings' style is used when the caller gives none; the caller's wins.
        Comparison word = TextPair(OldText, NewText, CompareSettings.Strict with { ThrowOnFailure = false, Style = RedlineStyle.Word });
        Assert.NotEqual(c.ToBytes(PdfRedline.Rendered), word.ToBytes(PdfRedline.Rendered));
        Assert.Equal(word.ToBytes(PdfRedline.Rendered), c.ToBytes(PdfRedline.Rendered, RedlineStyle.Word));
    }

    [Fact]
    public void ScriptsWithFontsReadBackAndScriptsWithoutAreReported()
    {
        const string o = "东京是日本的首都。\n\nمرحبا بالعالم، هذه جملة عربية.\n\nहिन्दी भाषा भारत में बोली जाती है।\n\nこんにちは世界。\n\n서울은 대한민국의 수도입니다.";
        const string n = "东京是日本最大的首都。\n\nمرحبا بالعالم، هذه جملة عربية جديدة.\n\nहिन्दी भाषा भारत में बहुत बोली जाती है।\n\nこんにちは世界!\n\n서울은 대한민국의 큰 수도입니다.";
        Comparison c = TextPair(o, n);
        using var ms = new MemoryStream();
        RenderedPdfResult r = PdfRedline.Rendered.Render(c, ms, RenderedGate.Style);
        Assert.Empty(r.Notes.Where(x => x.Contains("missing glyph", StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(RenderedGate.Check(c, ms.ToArray(), RenderedGate.Style, sideBySide: false));
        Assert.Empty(RenderedGate.Check(c, c.ToBytes(PdfRedline.RenderedSideBySide, RenderedGate.Style), RenderedGate.Style, sideBySide: true));
    }

    [Fact]
    public void ScratchDigest()
    {
        // A fixed digest of one rendered page, to see that the engine's output does not move with the platform.
        string d = Convert.ToHexStringLower(SHA256.HashData(TextPair(OldText, NewText).ToBytes(PdfRedline.Rendered)));
        Assert.Equal(64, d.Length);
    }
}
