using System.Text;
using OnePdf;
using OnePdf.Layout;

namespace OneDiff.Cli.Tests;

/// <summary>The tool reads PDFs and writes PDFs: on-page markup when the new document is a PDF, a rendered redline otherwise.</summary>
public sealed class PdfTests : IDisposable
{
    private static readonly string[] OldClauses =
    [
        "The supplier shall deliver the goods within thirty days of the order.",
        "Payment is due within sixty days of receipt of the invoice.",
        "This agreement is governed by the laws of England.",
    ];

    private static readonly string[] NewClauses =
    [
        "The supplier shall deliver the goods within forty-five days of the order.",
        "Payment is due within sixty days of receipt of the invoice.",
        "This agreement is governed by the laws of Scotland.",
    ];

    private readonly string dir = Path.Combine(Path.GetTempPath(), "onediff-cli-pdf-" + Guid.NewGuid().ToString("N"));

    public PdfTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Pdf(string name, string[] clauses)
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
                    foreach (string c in clauses)
                    {
                        col.Item().Text(c).FontSize(11);
                    }
                });
            });
        });
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, doc.ToBytes(new PdfDocumentWriterOptions { DocumentId = new byte[16] }));
        return path;
    }

    private static (int Code, string Err) Run(params string[] args)
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = Program.Run(args, new CliIo(input, output, error));
        return (code, Encoding.UTF8.GetString(error.ToArray()));
    }

    [Fact]
    public void ComparingTwoPdfsMarksTheNewPagesWithAnnotations()
    {
        string a = Pdf("a.pdf", OldClauses), b = Pdf("b.pdf", NewClauses), result = Path.Combine(dir, "markup.pdf");
        (int code, string err) = Run("compare", a, b, "-o", result);
        Assert.True(code == 0, err);

        using PdfDocument marked = PdfDocument.Open(File.ReadAllBytes(result));
        Assert.Single(marked.Pages);
        Assert.Contains(marked.Pages[0].Annotations, x => x.Subtype is "Highlight" or "Caret" or "StrikeOut");
        // The pages themselves are the new document's.
        using PdfDocument original = PdfDocument.Open(File.ReadAllBytes(b));
        Assert.Equal(original.ExtractText().Select(p => p.Text), marked.ExtractText().Select(p => p.Text));
    }

    [Fact]
    public void ComparingTextFilesToAPdfWritesARenderedRedline()
    {
        string a = Path.Combine(dir, "a.txt"), b = Path.Combine(dir, "b.txt"), result = Path.Combine(dir, "redline.pdf");
        File.WriteAllText(a, string.Join("\n\n", OldClauses));
        File.WriteAllText(b, string.Join("\n\n", NewClauses));
        (int code, string err) = Run("compare", a, b, result);
        Assert.True(code == 0, err);

        using PdfDocument redline = PdfDocument.Open(File.ReadAllBytes(result));
        string text = string.Join("\n", redline.ExtractText().Select(p => p.Text));
        Assert.Contains("Scotland", text, StringComparison.Ordinal);
        Assert.Contains("England", text, StringComparison.Ordinal);
    }
}
