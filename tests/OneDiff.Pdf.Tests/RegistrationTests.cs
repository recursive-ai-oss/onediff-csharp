using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Pdf.Tests;

public class RegistrationTests
{
    [Fact]
    public void RegisterThenRedlineCreateWritesMarkupOnTheOriginalPages()
    {
        byte[] oldPdf = Fixtures.Make(new Fixtures.Spec { Paragraphs = ["The supplier shall deliver within thirty days.", "Payment is due in sixty days."] });
        byte[] newPdf = Fixtures.Make(new Fixtures.Spec { Paragraphs = ["The supplier shall deliver within forty days.", "Payment is due in sixty days.", "Notices must be written."] });
        string dir = Path.Combine(Path.GetTempPath(), "onediff-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.pdf"), b = Path.Combine(dir, "b.pdf"), output = Path.Combine(dir, "out.pdf");
            File.WriteAllBytes(a, oldPdf);
            File.WriteAllBytes(b, newPdf);

            PdfRedline.Register();
            PdfRedline.Register();
            Redline.Create(a, b, output);

            byte[] written = File.ReadAllBytes(output);
            Assert.True(written.AsSpan().StartsWith(newPdf), "the original bytes stay in place; the markup is an update after them");
            List<ReadAnnotation> marks = Run.ReadAnnotations(written);
            Assert.NotEmpty(marks);
            Assert.Contains(marks, m => m.Subtype == "Highlight");

            // The same bytes as marking by hand.
            Assert.Equal(Run.Of(oldPdf, newPdf).Marked, written);

            // Save/ToBytes and an explicit form agree with the registered path.
            Comparison c = Comparison.Of(a, b);
            Assert.Equal(written, c.ToBytes(PdfRedline.OnPages));
            c.SaveAs(Path.Combine(dir, "again.pdf"));
            Assert.Equal(written, File.ReadAllBytes(Path.Combine(dir, "again.pdf")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ADocumentNotReadFromAPdfSaysHowToMark()
    {
        Comparison c = Comparison.Of(CompareDocument.FromText("one"), CompareDocument.FromText("two"));
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => c.ToBytes(PdfRedline.OnPages));
        Assert.Contains("MarkNewDocument", e.Message, StringComparison.Ordinal);
    }
}
