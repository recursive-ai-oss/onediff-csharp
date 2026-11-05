using System.IO.Compression;
using OneDiff.Documents;

namespace OneDiff.Word.Tests;

/// <summary>The calls shown on docs/migrating-from-wmlcomparer.md.</summary>
public class MigrationPageTests
{
    private static DocSpec Old() => new(
        Para.Of("The supplier shall deliver the goods within thirty days of the order."),
        Para.Of("Payment is due within sixty days of the invoice."));

    private static DocSpec New() => new(
        Para.Of("The supplier shall deliver the goods within forty days of the order."),
        Para.Of("Payment is due within sixty days of the invoice."));

    [Fact]
    public void ACompareWithAnAuthorWritesTrackedChangesByThatAuthor()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onediff-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "v1.docx"), b = Path.Combine(dir, "v2.docx"), outPath = Path.Combine(dir, "redline.docx");
            File.WriteAllBytes(a, DocFactory.Build(Old()));
            File.WriteAllBytes(b, DocFactory.Build(New()));
            WordRedline.Register();

            Comparison cmp = Comparison.Of(a, b);
            cmp.SaveAs(outPath, new DocxRedline { Author = "Redline" });

            using var zip = ZipFile.OpenRead(outPath);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            string xml = reader.ReadToEnd();
            Assert.Contains("w:author=\"Redline\"", xml, StringComparison.Ordinal);
            Assert.Contains("<w:ins ", xml, StringComparison.Ordinal);
            Assert.Contains("<w:del ", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("w:date=", xml, StringComparison.Ordinal);

            Assert.Contains(cmp.Changes.Where(static c => c.IsSubstantive), static c => c.Kind == ChangeKind.Replace && c.Old.Text.Contains("thirty", StringComparison.Ordinal));
            Assert.Equal(WordRevisionMode.Accepted, WordSourceOptions.Default.Revisions);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
