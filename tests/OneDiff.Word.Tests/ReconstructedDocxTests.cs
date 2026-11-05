using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Pdf;
using OnePdf;
using OnePdf.Layout;

namespace OneDiff.Word.Tests;

public class ReconstructedDocxTests
{
    private static byte[] Pdf(params string[] paragraphs)
    {
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            d.Title("fixture");
            int per = (paragraphs.Length + 2) / 3;
            for (int pg = 0; pg < 3; pg++)
            {
                string[] mine = paragraphs[Math.Min(pg * per, paragraphs.Length)..Math.Min((pg + 1) * per, paragraphs.Length)];
                d.Page(page =>
                {
                    page.Size(595, 842);
                    page.Margin(54);
                    page.Header().PaddingBottom(24).AlignCenter().Text("Services Agreement draft").FontSize(8);
                    page.Footer().PaddingTop(24).AlignCenter().Text(t =>
                    {
                        t.FontSize(8);
                        t.Span("Page ");
                        t.CurrentPageNumber();
                    });
                    page.Content().Column(col =>
                    {
                        col.Spacing(10);
                        foreach (string p in mine)
                        {
                            if (p.StartsWith("**", StringComparison.Ordinal))
                            {
                                col.Item().Text(t => t.Span(p[2..]).Bold().FontSize(12));
                            }
                            else
                            {
                                col.Item().Text(p).FontSize(11);
                            }
                        }
                    });
                });
            }
        });
        return doc.ToBytes(new PdfDocumentWriterOptions { DocumentId = new byte[16] });
    }

    private static readonly string[] OldText =
    [
        "**Services Agreement",
        "The supplier shall deliver the goods within thirty days of the order.",
        "1. Payment is due within sixty days of the invoice.",
        "2. Either party may terminate on written notice.",
        "This paragraph is removed in the new version of the agreement entirely.",
        "Final provisions apply to every schedule attached to this agreement.",
    ];

    private static readonly string[] NewText =
    [
        "**Services Agreement",
        "The supplier shall deliver the goods within forty days of the order.",
        "1. Payment is due within sixty days of the invoice.",
        "2. Either party may terminate on written notice or on breach.",
        "A new confidentiality paragraph is inserted in this version of the agreement.",
        "Final provisions apply to every schedule attached to this agreement.",
    ];

    private static CompareDocument Read(byte[] pdf, string name)
    {
        PdfRedline.Register();
        return CompareDocument.From(DocumentInput.FromBytes(pdf, name), new PdfDocumentSource());
    }

    [Fact]
    public void PdfPairWritesAReconstructedTrackedDocxThatAcceptsAndRejectsExactly()
    {
        CompareDocument o = Read(Pdf(OldText), "old.pdf"), n = Read(Pdf(NewText), "new.pdf");
        foreach (WordBuildOn on in new[] { WordBuildOn.New, WordBuildOn.Old })
        {
            foreach (CompareSettings settings in new[] { CompareSettings.Legal, CompareSettings.Strict })
            {
                Comparison cmp = Comparison.Of(o, n, settings);
                Assert.True(cmp.Verification.Passed);
                List<string> problems = ReconstructionGate.Run(cmp, on, out byte[] bytes, out DocxRedlineResult result, detail: true);
                Assert.True(problems.Count == 0, $"{on} {settings.Name}: " + string.Join("\n", problems.Take(6)) + "\nnotcarried: " + string.Join(",", ReconstructionGate.Describe(result)));
                Assert.True(bytes.Length > 1000);
                using RecursiveAI.OneOffice.Documents.Document d = RecursiveAI.OneOffice.Documents.Document.Open(RecursiveAI.OneOffice.IO.ByteSource.FromMemory(bytes));
                Assert.True(d.Revisions.Revisions.Count >= 4, "revisions: " + d.Revisions.Revisions.Count);
            }
        }
    }

    [Fact]
    public void TheOutputIsLabelledAndRepeatable()
    {
        CompareDocument o = Read(Pdf(OldText), "old.pdf"), n = Read(Pdf(NewText), "new.pdf");
        Assert.Contains(n.Nodes, x => x.Kind == NodeKind.ListItem);
        Assert.Contains(n.Nodes, x => x.Kind == NodeKind.Header);
        Assert.Contains(n.Nodes, x => x.Kind == NodeKind.Footer);
        Assert.Contains(n.Nodes, x => x.Runs.Any(r => r.Format.Get(FormatKeys.Bold) is not null));
        Comparison cmp = Comparison.Of(o, n, CompareSettings.Legal);
        using var a = new MemoryStream();
        using var b = new MemoryStream();
        new DocxRedline().Create(cmp, a);
        new DocxRedline().Create(cmp, b);
        Assert.Equal(a.ToArray(), b.ToArray());
        CompareDocument back = Harness.Read(a.ToArray());
        Assert.Contains(back.Nodes, x => x.Kind == NodeKind.Property && x.Text.Contains("Reconstructed", StringComparison.Ordinal));
        string all = string.Join("\n", back.Nodes.Where(x => x.Kind == NodeKind.Paragraph).Select(x => x.Text));
        Assert.Contains("Reconstructed by OneDiff", all, StringComparison.Ordinal);
    }
}
