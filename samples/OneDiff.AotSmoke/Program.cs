using OneDiff.Documents;
using OneDiff.Documents.Combine;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;
using OneDiff.Nlp;
using OneDiff.Ocr;
using OneDiff.Pdf;
using OneDiff.Word;
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
        failures += CheckSemantics();
        failures += CheckNlp();
        failures += CheckScan();
        failures += CheckPdf();
        failures += CheckWord();
        failures += CheckRedline();
        failures += CheckCombine();

        Console.Out.WriteLine(failures == 0 ? "ok" : "FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The OneNLP value recognizer under NativeAOT: written-out values typed in a comparison, the built-in fallback, and a stable result.</summary>
    private static int CheckNlp()
    {
        int failures = 0;
        CompareSettings settings = CompareSettings.Legal.UseOneNlp();
        CompareDocument a = CompareDocument.FromText("Payment is due within thirty days of the invoice.\nThe cap on its liability is forty-two thousand dollars.\nThe fee is 18.000 EUR.", "old");
        CompareDocument b = CompareDocument.FromText("Payment is due within two months of the invoice.\nThe cap on its liability is $50,000.\nThe fee is 20.000 EUR.", "new");
        Comparison c = Comparison.Of(a, b, settings);
        string[] values = [.. c.Changes.Select(x => x.Type + " " + x.OldValue?.Canonical + ">" + x.NewValue?.Canonical)];
        string[] want = ["Duration 30 d>2 mo", "Amount USD 42000>USD 50000", "Amount EUR 18000>EUR 20000"];
        if (!values.AsSpan().SequenceEqual(want) || !c.Verification.Passed || c.Verification.FallbackLevel != 0 || !c.Check().IsEmpty)
        {
            Console.Error.WriteLine("nlp: unexpected values " + string.Join("; ", values));
            failures++;
        }

        if (!ComparisonJson.Write(c).AsSpan().SequenceEqual(ComparisonJson.Write(Comparison.Of(a, b, settings))))
        {
            Console.Error.WriteLine("nlp: JSON differs between runs");
            failures++;
        }

        return failures;
    }

    /// <summary>
    /// Scans under NativeAOT: two pages are drawn to pixels, read through the OCR engine from a PNG and from a PDF made of the same
    /// pixels, compared, flagged and marked on the page.
    /// </summary>
    private static int CheckScan()
    {
        int failures = 0;
        byte[] oldPng = Draw(MakePdf(["The supplier shall deliver within thirty days.", "Payment is due in sixty days."]));
        byte[] newPng = Draw(MakePdf(["The supplier shall deliver within forty days.", "Payment is due in sixty days."]));
        var source = new OcrDocumentSource();
        CompareDocument a = source.Read(DocumentInput.FromBytes(oldPng, "old.png")), b = source.Read(DocumentInput.FromBytes(newPng, "new.png"));
        Comparison c = Comparison.Of(a, b, CompareSettings.Legal with { OcrConfidenceThreshold = 1.0 });
        if (c.Verification.FallbackLevel != 0 || !c.Check().IsEmpty || c.Changes.IsEmpty || c.Changes.Any(x => x.OcrConfidence is null || !x.PossibleOcrError))
        {
            Console.Error.WriteLine("scan: comparison failed verification or carries no confidence");
            failures++;
        }

        if (ComparisonJson.Read(ComparisonJson.Write(c)).Changes.Count(x => x.PossibleOcrError) != c.Changes.Length)
        {
            Console.Error.WriteLine("scan: the JSON lost the OCR flags");
            failures++;
        }

        using var marked = new MemoryStream();
        PdfRedline.OnPages.Write(c, marked, RedlineStyle.SideBySide);
        using PdfDocument back = PdfDocument.Open(marked.ToArray());
        if (!back.Pages[0].Annotations.Any(x => x.Subtype is "Highlight" or "Caret"))
        {
            Console.Error.WriteLine("scan: no mark on the page");
            failures++;
        }

        return failures;
    }

    /// <summary>The first page of a PDF drawn at 200 dpi, as PNG bytes.</summary>
    private static byte[] Draw(byte[] pdf)
    {
        var load = new OneImage.LoadOptions { Configuration = new OneImage.ImageConfiguration().Add(new OneImage.Pdf.PdfFormat(new OneImage.Pdf.PdfRenderOptions { Dpi = 200 })) };
        OneImage.Image doc = OneImage.Image.Load(pdf, load);
        using var ms = new MemoryStream();
        doc.Frames[0].ToImage().Save(ms, OneImage.PngFormat.Instance);
        return ms.ToArray();
    }

    /// <summary>Combine and chain under NativeAOT: attribution, a conflict, the report page, both JSON forms and the checks.</summary>
    private static int CheckCombine()
    {
        CompareDocument original = CompareDocument.FromText("Pay within 30 days.\n\nDeliver the goods.\n\nEnglish law governs.", "original");
        CompareDocument alice = CompareDocument.FromText("Pay within 60 days.\n\nDeliver the goods.\n\nEnglish law governs.", "alice");
        CompareDocument bob = CompareDocument.FromText("Pay within 10 days.\n\nDeliver the goods.\n\nEnglish law governs.", "bob");
        CompareDocument carol = CompareDocument.FromText("Pay within 60 days.\n\nDeliver the goods.\n\nScots law governs.", "carol");
        int failures = 0;
        Combination c = Combination.Of(original, [alice, bob, carol]);
        if (!c.Check().IsEmpty || c.Conflicts.Length != 1 || !c.Changes.Any(x => x.Reviewers.Length == 2))
        {
            Console.Error.WriteLine("combine: unexpected result");
            failures++;
        }

        string html = ChangeReport.From(c).ToHtml();
        if (!html.Contains("class=\"conflict\"", StringComparison.Ordinal) || CombineJson.Write(c).Length == 0)
        {
            Console.Error.WriteLine("combine: report or JSON missing");
            failures++;
        }

        ComparisonChain chain = ComparisonChain.Of([original, alice, carol]);
        if (!chain.Check().IsEmpty || chain.Net.Changes.Any(n => chain.StepsOf(n.Id).IsEmpty) || CombineJson.Write(chain).Length == 0 || !ChangeReport.From(chain).ToHtml().Contains("net:", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("chain: unexpected result");
            failures++;
        }

        return failures;
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

    /// <summary>Writes two small DOCX files with OneOffice, reads them through the Word source, compares them and writes the redline through the registered happy path.</summary>
    private static int CheckWord()
    {
        int failures = 0;
        byte[] oldDocx = MakeDocx(["The supplier shall deliver within thirty days.", "Payment is due in sixty days."]);
        byte[] newDocx = MakeDocx(["The supplier shall deliver within forty days.", "Payment is due in sixty days.", "Notices must be written."]);
        var source = new WordDocumentSource();
        CompareDocument a = source.Read(DocumentInput.FromBytes(oldDocx, "old.docx")), b = source.Read(DocumentInput.FromBytes(newDocx, "new.docx"));
        Comparison c = Comparison.Of(a, b);
        if (c.Verification.FallbackLevel != 0 || !c.Verification.Passed || !c.Check().IsEmpty || c.Changes.IsEmpty)
        {
            Console.Error.WriteLine("word: comparison failed verification");
            failures++;
        }

        string dir = Path.Combine(Path.GetTempPath(), "onediff-aot-word-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        try
        {
            WordRedline.Register();
            string x = Path.Combine(dir, "a.docx"), y = Path.Combine(dir, "b.docx"), html = Path.Combine(dir, "out.html");
            File.WriteAllBytes(x, oldDocx);
            File.WriteAllBytes(y, newDocx);
            Redline.Create(x, y, html);
            if (!File.ReadAllText(html).Contains("<ins", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("word: HTML output missing marks");
                failures++;
            }

            // The tracked-changes DOCX: accepting every revision gives the new file, rejecting every one gives the old.
            string tracked = Path.Combine(dir, "tracked.docx");
            Redline.Create(x, y, tracked);
            byte[] trackedBytes = File.ReadAllBytes(tracked);
            foreach (bool accept in new[] { true, false })
            {
                using RecursiveAI.OneOffice.Documents.Document doc = RecursiveAI.OneOffice.Documents.Document.Open(RecursiveAI.OneOffice.IO.ByteSource.FromMemory(trackedBytes));
                if (doc.Revisions.Revisions.Count == 0)
                {
                    Console.Error.WriteLine("word: tracked DOCX has no revisions");
                    failures++;
                }

                if (accept)
                {
                    doc.Revisions.AcceptAll();
                }
                else
                {
                    doc.Revisions.RejectAll();
                }

                using var resolved = new MemoryStream();
                doc.SaveAs(resolved, RecursiveAI.OneOffice.OfficeFormat.Docx);
                CompareDocument got = source.Read(DocumentInput.FromBytes(resolved.ToArray(), "resolved.docx"));
                if (!Comparison.Of(got, accept ? b : a).Changes.IsEmpty)
                {
                    Console.Error.WriteLine("word: tracked DOCX " + (accept ? "accept-all" : "reject-all") + " differs");
                    failures++;
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        return failures;
    }

    private static byte[] MakeDocx(string[] paragraphs)
    {
        using RecursiveAI.OneOffice.Documents.Document doc = RecursiveAI.OneOffice.Documents.Document.Create();
        foreach (string p in paragraphs)
        {
            doc.Body.AddParagraph(p);
        }

        using var ms = new MemoryStream();
        doc.SaveAs(ms, RecursiveAI.OneOffice.OfficeFormat.Docx);
        return ms.ToArray();
    }

    /// <summary>Compares two plain-text documents through every stage and round-trips the result through JSON.</summary>
    private static int CheckDocuments()
    {
        CompareDocument a = CompareDocument.FromText("The supplier shall deliver within 30 days of 3 March 2025; 买方应在三十日内支付。ผู้ซื้อชำระราคา.\n\nPayment is due in sixty days.\n\nHe said \u201Chi\u201D.");
        CompareDocument b = CompareDocument.FromText("The supplier shall deliver within 45 days of 4 March 2025; 买方应在六十日内支付。ผู้ซื้อชำระราคาทั้งหมด.\n\nHe said \"hi\".\n\nPayment is due in sixty days.");
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

    /// <summary>Compares numbered clauses and prints the typed changes, the substantive filter, the cascade and rename groups, and the resolved values.</summary>
    private static int CheckSemantics()
    {
        static CompareDocument Clauses(params (string Label, string Text)[] items)
        {
            var b = new CompareDocumentBuilder(DocumentKind.Flowing);
            foreach ((string label, string text) in items)
            {
                b.Add(NodeKind.ListItem, new NodeContent { Text = text, Attributes = FormatSet.Of(FormatKeys.ListLabel, label) });
            }

            return b.Build();
        }

        CompareDocument a = Clauses(
            ("1.1", "\"Software\" means the programs in Schedule 1."),
            ("1.2", "The Supplier shall deliver the Software within 30 days of 3 March 2025 for USD 5,000."),
            ("1.3", "Fees are set out in Section 1.4."),
            ("1.4", "The Customer shall not copy the Software."));
        CompareDocument b = Clauses(
            ("1.1", "\"Platform\" means the programs in Schedule 1."),
            ("1.2", "The Supplier shall deliver the Platform within 60 days of 3 March 2025 for USD 6,500."),
            ("1.3", "A new clause is added."),
            ("1.4", "Fees are set out in Section 1.5."),
            ("1.5", "The Customer shall not copy the Platform."));
        Comparison c = Comparison.Of(a, b, CompareSettings.Legal);
        int failures = 0;
        foreach (Change ch in c.Changes.Where(x => x.IsSubstantive && x.Type != ChangeType.None))
        {
            Console.WriteLine($"typed {ch.Type}: {ch.OldValue?.Canonical} -> {ch.NewValue?.Canonical}");
        }

        foreach (ChangeGroup g in c.Groups.Where(x => x.Kind != GroupKind.Region))
        {
            Console.WriteLine($"group {g.Kind}: {g.Title} ({g.ChangeIds.Length} changes)");
        }

        bool Has(ChangeType t, string o, string n) => c.Changes.Any(x => x.Type == t && x.OldValue?.Canonical == o && x.NewValue?.Canonical == n);
        if (!Has(ChangeType.Duration, "30 d", "60 d") || !Has(ChangeType.Amount, "USD 5000", "USD 6500") || !Has(ChangeType.DefinedTerm, "Software", "Platform") || !Has(ChangeType.Reference, "1.4", "1.5"))
        {
            Console.Error.WriteLine("semantics: expected typed values are missing");
            failures++;
        }

        if (!c.Groups.Any(x => x.Kind == GroupKind.Cascade) || !c.Groups.Any(x => x.Kind == GroupKind.Rename) || c.Changes.Any(x => x.Type == ChangeType.Numbering && x.IsSubstantive))
        {
            Console.Error.WriteLine("semantics: expected cascade and rename groups are missing");
            failures++;
        }

        byte[] json = ComparisonJson.Write(c);
        if (!ComparisonJson.Write(ComparisonJson.Read(json)).AsSpan().SequenceEqual(json) || c.Verification.FallbackLevel != 0 || !c.Check().IsEmpty)
        {
            Console.Error.WriteLine("semantics: JSON round trip or verification failed");
            failures++;
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

            // The rendered redline: text files in, a tagged PDF out, by the .pdf extension; and a PDF pair rendered by naming the form.
            string ta = Path.Combine(dir, "a.txt"), tb = Path.Combine(dir, "b.txt"), tr = Path.Combine(dir, "rendered.pdf");
            File.WriteAllText(ta, "The supplier shall deliver within thirty days.\n\nPayment is due in sixty days.");
            File.WriteAllText(tb, "The supplier shall deliver within forty days.\n\nPayment is due in sixty days.");
            Redline.Create(ta, tb, tr);
            byte[] rendered = File.ReadAllBytes(tr);
            using PdfDocument renderedDoc = PdfDocument.Open(rendered);
            string text = renderedDoc.ExtractText().Select(p => p.Text).Aggregate("", (x, y) => x + y);
            if (!renderedDoc.IsTagged || renderedDoc.Language != "en" || !text.Contains("forty", StringComparison.Ordinal) || !text.Contains("thirty", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("pdf: the rendered redline is not a tagged PDF holding both texts");
                failures++;
            }

            Comparison pdfPair = Comparison.Of(pa, pb);
            if (!pdfPair.ToBytes(PdfRedline.Rendered).AsSpan().StartsWith("%PDF-"u8) || !pdfPair.ToBytes(PdfRedline.RenderedSideBySide).AsSpan().StartsWith("%PDF-"u8))
            {
                Console.Error.WriteLine("pdf: a PDF pair did not render");
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
