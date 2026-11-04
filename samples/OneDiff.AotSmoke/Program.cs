using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;
using OneDiff.Pdf;
using OnePdf;
using OneDiff.Tokenization;

namespace OneDiff.AotSmoke;

/// <summary>Runs the core under NativeAOT: a diff in every algorithm and tokenizer, checked by replaying the script.</summary>
internal static class Program
{
    private static int Main()
    {
        const string oldText = "the quick brown fox\njumps over\nthe lazy dog\n";
        const string newText = "the quick red fox\njumps over\nthe very lazy dog\n";
        int failures = 0;

        foreach (DiffAlgorithm algorithm in new[] { DiffAlgorithm.Myers, DiffAlgorithm.Patience, DiffAlgorithm.Histogram })
        {
            foreach (TokenKind kind in new[] { TokenKind.Line, TokenKind.Word, TokenKind.Character, TokenKind.Grapheme })
            {
                TextDiff diff = Diff.Text(oldText, newText, kind, algorithm);
                if (diff.Script.IsIdentical || diff.Script.OldLength != diff.OldTokens.Length || diff.Script.NewLength != diff.NewTokens.Length)
                {
                    Console.Error.WriteLine($"{algorithm}/{kind}: unexpected script");
                    failures++;
                }
            }
        }

        string[] oldLines = ["a", "b", "c"];
        string[] newLines = ["a", "c", "d"];
        EditScript script = Diff.Sequences<string>(oldLines, newLines);
        if (!script.Matches<string>(oldLines, newLines) || !script.Apply<string>(oldLines, newLines).AsSpan().SequenceEqual(newLines))
        {
            Console.Error.WriteLine("sequence round trip failed");
            failures++;
        }

        failures += CheckDocuments();
        failures += CheckPdf();
        failures += CheckRedline();

        Console.Out.WriteLine(failures == 0 ? "ok" : "FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The happy path under NativeAOT: Redline.Create to HTML (inline and side by side) and JSON, and SaveAs.</summary>
    private static int CheckRedline()
    {
        int failures = 0;
        string dir = Path.Combine(Path.GetTempPath(), "onediff-aot-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.txt"), b = Path.Combine(dir, "b.txt");
            File.WriteAllText(a, "The supplier shall deliver within thirty days.\n\nPayment is due in sixty days.");
            File.WriteAllText(b, "The supplier shall deliver within forty days.\n\nPayment is due in sixty days.");
            string html = Path.Combine(dir, "out.html"), sbs = Path.Combine(dir, "sbs.dat"), json = Path.Combine(dir, "out.json");
            Redline.Create(a, b, html);
            Redline.Create(a, b, sbs, RedlineForm.HtmlSideBySide);
            Redline.Create(a, b, json);
            string page = File.ReadAllText(html);
            if (!page.Contains("<ins", StringComparison.Ordinal) || !page.Contains("<del", StringComparison.Ordinal) || !File.ReadAllText(sbs).Contains("class=\"sbs\"", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("redline: HTML output missing marks");
                failures++;
            }

            Comparison c = Comparison.Of(a, b);
            c.SaveAs(Path.Combine(dir, "again.html"), RedlineStyle.Developer);
            if (!ComparisonJson.Read(File.ReadAllBytes(json)).Check().IsEmpty || c.ToBytes(RedlineForm.Html).Length != File.ReadAllBytes(html).Length)
            {
                Console.Error.WriteLine("redline: JSON or bytes mismatch");
                failures++;
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        return failures;
    }

    /// <summary>Compares two plain-text documents through every stage and round-trips the result through JSON.</summary>
    private static int CheckDocuments()
    {
        CompareDocument a = CompareDocument.FromText("The supplier shall deliver within thirty days.\n\nPayment is due in sixty days.\n\nHe said \u201Chi\u201D.");
        CompareDocument b = CompareDocument.FromText("The supplier shall deliver within forty days.\n\nHe said \"hi\".\n\nPayment is due in sixty days.");
        int failures = 0;
        foreach (CompareSettings settings in new[] { CompareSettings.Legal, CompareSettings.Strict, CompareSettings.TextOnly })
        {
            Comparison c = Comparison.Of(a, b, settings with { ThrowOnFailure = false });
            if (c.Verification.FallbackLevel != 0 || !c.Check().IsEmpty || c.Changes.IsEmpty)
            {
                Console.Error.WriteLine($"{settings.Name}: comparison failed verification");
                failures++;
            }

            byte[] json = ComparisonJson.Write(c);
            Comparison back = ComparisonJson.Read(json);
            if (!ComparisonJson.Write(back).AsSpan().SequenceEqual(json) || !back.Check().IsEmpty)
            {
                Console.Error.WriteLine($"{settings.Name}: JSON round trip failed");
                failures++;
            }
        }

        return failures;
    }

    /// <summary>Writes two small PDFs, reads them through the PDF source, compares them, marks the new page and reads the marks back.</summary>
    private static int CheckPdf()
    {
        byte[] oldPdf = MakePdf(["The supplier shall deliver within thirty days.", "Payment is due in sixty days."]);
        byte[] newPdf = MakePdf(["The supplier shall deliver within forty days.", "Payment is due in sixty days.", "Notices must be written."]);
        var source = new PdfDocumentSource();
        CompareDocument a = source.Read(DocumentInput.FromBytes(oldPdf, "old.pdf")), b = source.Read(DocumentInput.FromBytes(newPdf, "new.pdf"));
        Comparison c = Comparison.Of(a, b);
        int failures = 0;
        if (c.Verification.FallbackLevel != 0 || !c.Check().IsEmpty || c.Changes.IsEmpty)
        {
            Console.Error.WriteLine("pdf: comparison failed verification");
            failures++;
        }

        using var marked = new MemoryStream();
        PdfMarkupResult result = PdfRedline.OnPages.MarkNewDocument(c, newPdf, marked);
        using PdfDocument back = PdfDocument.Open(marked.ToArray());
        int annotations = back.Pages[0].Annotations.Count(x => x.Subtype is "Highlight" or "Caret");
        if (result.Marks.IsEmpty || annotations != result.Marks.Length)
        {
            Console.Error.WriteLine($"pdf: {result.Marks.Length} marks written, {annotations} annotations read back");
            failures++;
        }

        // The happy path: register once, then Redline.Create writes the markup chosen by the .pdf extension.
        string dir = Path.Combine(Path.GetTempPath(), "onediff-aot-pdf-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            string pa = Path.Combine(dir, "a.pdf"), pb = Path.Combine(dir, "b.pdf"), po = Path.Combine(dir, "out.pdf");
            File.WriteAllBytes(pa, oldPdf);
            File.WriteAllBytes(pb, newPdf);
            PdfRedline.Register();
            Redline.Create(pa, pb, po);
            using PdfDocument registered = PdfDocument.Open(File.ReadAllBytes(po));
            if (registered.Pages[0].Annotations.Count(x => x.Subtype is "Highlight" or "Caret") != result.Marks.Length)
            {
                Console.Error.WriteLine("pdf: Register + Redline.Create did not write the markup");
                failures++;
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        return failures;
    }

    private static byte[] MakePdf(string[] lines)
    {
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 595, 842);
        PdfCreatedFont font = doc.AddStandardFont("Helvetica");
        using (PdfPageContentBuilder content = page.BeginContent())
        {
            content.SetFont(font, 12);
            double y = 760;
            foreach (string line in lines)
            {
                content.DrawText(line, 72, y);
                y -= 40;
            }

            content.Commit();
        }

        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16] });
        return ms.ToArray();
    }
}
