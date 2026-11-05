using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Pdf;
using OnePdf;
using OnePdf.Layout;

namespace OneDiff.Word.Tests;

public class HappyPathTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onediff-word-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static DocSpec Old() => new(
        Para.Of("Services Agreement") with { Style = "Heading1" },
        Para.Of("The supplier shall deliver the goods within thirty days of the order."),
        Para.Of("Payment is due within sixty days of the invoice."),
        Para.Of("Either party may terminate on written notice."));

    private static DocSpec New() => new(
        Para.Of("Services Agreement") with { Style = "Heading1" },
        Para.Of("The supplier shall deliver the goods within forty days of the order."),
        Para.Of("Payment is due within sixty days of the invoice."),
        Para.Of("Confidentiality survives termination."),
        Para.Of("Either party may terminate on written notice."));

    [Fact]
    public void RegisterThenRedlineCreateWritesHtmlAndJson()
    {
        string dir = NewDir();
        try
        {
            string a = Path.Combine(dir, "a.docx"), b = Path.Combine(dir, "b.docx");
            File.WriteAllBytes(a, DocFactory.Build(Old()));
            File.WriteAllBytes(b, DocFactory.Build(New()));
            WordRedline.Register();
            WordRedline.Register();

            string html = Path.Combine(dir, "out.html"), json = Path.Combine(dir, "out.json");
            Redline.Create(a, b, html);
            Redline.Create(a, b, json);
            string page = File.ReadAllText(html);
            Assert.Contains("<ins", page, StringComparison.Ordinal);
            Assert.Contains("forty", page, StringComparison.Ordinal);
            string text = File.ReadAllText(json);
            Assert.Contains("onediff.comparison", text, StringComparison.Ordinal);

            // Twice, byte for byte.
            Redline.Create(a, b, Path.Combine(dir, "again.json"));
            Assert.Equal(File.ReadAllBytes(json), File.ReadAllBytes(Path.Combine(dir, "again.json")));

            Comparison c = Comparison.Of(a, b);
            Assert.True(c.Verification.Passed);
            Assert.Equal(0, c.Verification.FallbackLevel);
            Assert.Empty(c.Check());
            Assert.Contains(c.Changes, ch => ch.Old.Text.Contains("thirty", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static byte[] Pdf(params string[] paragraphs)
    {
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            d.Title("fixture");
            d.Page(page =>
            {
                page.Size(595, 842);
                page.Margin(54);
                page.Content().Column(col =>
                {
                    col.Spacing(10);
                    foreach (string p in paragraphs)
                    {
                        col.Item().Text(p).FontSize(11);
                    }
                });
            });
        });
        return doc.ToBytes(new PdfDocumentWriterOptions { DocumentId = new byte[16] });
    }

    [Fact]
    public void ADocxComparesWithAPdfInTheFlowingKind()
    {
        string dir = NewDir();
        try
        {
            string a = Path.Combine(dir, "a.docx"), b = Path.Combine(dir, "b.pdf");
            File.WriteAllBytes(a, DocFactory.Build(Old()));
            File.WriteAllBytes(b, Pdf(
                "Services Agreement",
                "The supplier shall deliver the goods within forty days of the order.",
                "Payment is due within sixty days of the invoice.",
                "Either party may terminate on written notice."));
            WordRedline.Register();
            PdfRedline.Register();

            Comparison c = Comparison.Of(a, b);
            Assert.Equal(DocumentKind.Flowing, c.Old.Kind);
            Assert.Equal("application/pdf", c.New.SourceFormat);
            Assert.True(c.Verification.Passed);
            Assert.Equal(0, c.Verification.FallbackLevel);
            Assert.Contains(c.Changes, ch => ch.Old.Text.Contains("thirty", StringComparison.Ordinal) && ch.New.Text.Contains("forty", StringComparison.Ordinal));

            string json = Path.Combine(dir, "x.json");
            Redline.Create(a, b, json);
            Assert.True(new FileInfo(json).Length > 100);

            // And the other way round.
            Comparison back = Comparison.Of(b, a);
            Assert.True(back.Verification.Passed);
            Assert.Equal(0, back.Verification.FallbackLevel);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
