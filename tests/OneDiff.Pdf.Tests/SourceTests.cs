using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;
using OnePdf;

namespace OneDiff.Pdf.Tests;

public class SourceTests
{
    private static readonly string Sentence = "The supplier shall deliver the goods within thirty days of the order, and the customer shall pay the invoice. ";

    private static string Long(int repeats) => string.Concat(Enumerable.Repeat(Sentence, repeats)).TrimEnd();

    private static byte[] Doc(Fixtures.Spec spec) => Fixtures.Make(spec);

    [Fact]
    public void SniffsByContentNotByName()
    {
        byte[] pdf = Doc(new Fixtures.Spec { Paragraphs = ["Hello world."] });
        var source = new PdfDocumentSource();
        Assert.True(source.CanRead(DocumentInput.FromBytes(pdf, "notes.txt")));
        Assert.False(source.CanRead(DocumentInput.FromString("%PDF is mentioned late in a text file", "a.pdf")));
        Assert.Same(source, DocumentSources.Sniff(DocumentInput.FromBytes(pdf, "x"), [source]));
        CompareDocument doc = CompareDocument.From(DocumentInput.FromBytes(pdf, "x"), source);
        Assert.Equal("application/pdf", doc.SourceFormat);
        Assert.Equal(DocumentInput.FromBytes(pdf).Sha256Hex(), doc.SourceHash);
    }

    [Fact]
    public void HeadersFootersAndPageNumbersAreFurnitureAndLedgered()
    {
        var spec = new Fixtures.Spec { Header = "Master Services Agreement", PageNumbers = true, PageHeight = 300, Paragraphs = [Long(30)] };
        CompareDocument doc = Run.Read(Doc(spec));
        int headers = doc.Nodes.Count(n => n.Kind == NodeKind.Header), footers = doc.Nodes.Count(n => n.Kind == NodeKind.Footer);
        Assert.Equal(1, headers);
        Assert.Equal(1, footers);
        Assert.All(doc.Nodes.Where(n => n.Kind == NodeKind.Footer).Select(n => doc[doc.ChildrenOf(n.Id)[0]]), p =>
        {
            Assert.Equal("Page # of #", p.Text);
            Assert.Contains(p.Folds, f => f.Category == LedgerCategory.PageNumbers);
        });

        // The body is one paragraph even though it crosses all those pages.
        Assert.Single(doc.Nodes, n => n.Kind == NodeKind.Paragraph && n.Text.StartsWith("The supplier", StringComparison.Ordinal));

        // A longer document shifts every page number and the total; by default none of that is a change.
        var longer = spec with { Paragraphs = [Long(45)] };
        Comparison legal = Comparison.Of(doc, Run.Read(Doc(longer)));
        Assert.Empty(legal.Check());
        Assert.Single(legal.Changes);
        Assert.Contains(legal.Ledger.Entries, e => e.Category == LedgerCategory.PageNumbers);

        // Showing page numbers shows the difference.
        Comparison shown = Comparison.Of(doc, Run.Read(Doc(longer)), CompareSettings.Legal with { Hidden = CompareSettings.Legal.Hidden.Without(LedgerCategory.PageNumbers) });
        Assert.Empty(shown.Check());
        Assert.Contains(shown.Changes, c => c.Kind == ChangeKind.Replace && c.Old.Text.Length > 0 && c.Old.Text.All(char.IsAsciiDigit) && c.New.Text.All(char.IsAsciiDigit));
    }

    [Fact]
    public void LayoutDifferencesAreInTheLedgerNotInTheChanges()
    {
        string text = Long(6);
        byte[] wide = Doc(new Fixtures.Spec { Paragraphs = [text], PageWidth = 595 });
        byte[] narrow = Doc(new Fixtures.Spec { Paragraphs = [text], PageWidth = 420 });
        Comparison c = Comparison.Of(Run.Read(wide), Run.Read(narrow));
        Assert.Empty(c.Check());
        Assert.Empty(c.Changes);
        Assert.Contains(c.Ledger.Entries, e => e.Category == LedgerCategory.LineBreaks);

        // With line breaks shown, the layout difference is a change and is still complete.
        Comparison strict = Comparison.Of(Run.Read(wide), Run.Read(narrow), CompareSettings.Legal with { Hidden = CompareSettings.Legal.Hidden.Without(LedgerCategory.LineBreaks).Without(LedgerCategory.Hyphenation) });
        Assert.Empty(strict.Check());
        Assert.NotEmpty(strict.Changes);
    }

    [Fact]
    public void HyphenatedLineEndsFoldAway()
    {
        string text = "Internationalization considerations notwithstanding, administrative responsibilities characteristically " +
                      "extraordinarily compartmentalized telecommunications infrastructure misunderstandings.";
        string[] paragraphs = [.. Enumerable.Repeat(text, 12)];
        byte[] hyphenated = Doc(new Fixtures.Spec { Paragraphs = paragraphs, Hyphenate = true, Justify = true, PageWidth = 300 });
        byte[] plain = Doc(new Fixtures.Spec { Paragraphs = paragraphs, PageWidth = 450 });
        CompareDocument h = Run.Read(hyphenated);
        Assert.Contains(h.Nodes, n => n.Folds.Any(f => f.Category == LedgerCategory.Hyphenation));
        Assert.All(h.Nodes.Where(n => n.Kind == NodeKind.Paragraph), n => Assert.Equal(text, n.Text));
        Comparison c = Comparison.Of(h, Run.Read(plain));
        Assert.Empty(c.Check());
        Assert.Empty(c.Changes);
        Assert.Contains(c.Ledger.Entries, e => e.Category == LedgerCategory.Hyphenation);
    }

    [Fact]
    public void PageSpanningParagraphChangeIsOneMarkOnTheRightPage()
    {
        string a = Long(40), b = ReplaceFirst(a, "thirty days", "ninety days");
        var spec = new Fixtures.Spec { PageHeight = 250, Paragraphs = ["Opening paragraph.", a, "Closing paragraph."] };
        Run r = Run.Of(Doc(spec), Doc(spec with { Paragraphs = ["Opening paragraph.", b, "Closing paragraph."] }));
        r.AssertComplete();
        r.AssertReextraction();
        Change change = Assert.Single(r.Comparison.Changes);
        Assert.Equal(ChangeKind.Replace, change.Kind);
        Assert.Equal("thirty", change.Old.Text);
        Assert.Single(r.Result.Marks);
    }

    [Fact]
    public void ListsKeepTheirLabelsAsAttributes()
    {
        CompareDocument doc = Run.Read(Doc(new Fixtures.Spec { Paragraphs = ["Intro text."], BulletedList = ["alpha item", "beta item"], NumberedList = ["one", "two"] }));
        Node[] items = [.. doc.Nodes.Where(n => n.Kind == NodeKind.ListItem)];
        Assert.Equal(4, items.Length);
        Assert.Equal(["\u2022", "\u2022", "1.", "2."], items.Select(i => i.Attributes.Get(FormatKeys.ListLabel)));
        Assert.Equal(["alpha item", "beta item", "one", "two"], items.Select(i => i.Text));

        // Renumbering is an attribute change on the item, not a text change.
        CompareDocument other = Run.Read(Doc(new Fixtures.Spec { Paragraphs = ["Intro text."], BulletedList = ["alpha item", "beta item"], NumberedList = ["zero", "one", "two"] }));
        Comparison c = Comparison.Of(doc, other);
        Assert.Empty(c.Check());
        Assert.Contains(c.Changes, ch => ch.Kind == ChangeKind.Attribute);
    }

    [Fact]
    public void AnchorsAreGlyphQuadsInsideThePage()
    {
        CompareDocument doc = Run.Read(Doc(new Fixtures.Spec { Paragraphs = ["Plain words (a) and 3.5% here."] }));
        Node p = doc.Nodes.Single(n => n.Kind == NodeKind.Paragraph);
        Assert.Equal(AnchorKind.PdfQuads, p.Anchor.Kind);
        Assert.NotEmpty(p.Segments);
        foreach (AnchoredSpan s in p.Segments)
        {
            Assert.Equal(AnchorKind.PdfQuads, s.Anchor.Kind);
            Quad q = Assert.Single(s.Anchor.Quads);
            Assert.Equal(0, q.Page);
            Assert.True(q.X2 > q.X1 && q.Y4 > q.Y1, $"segment '{p.Text.Substring(s.Start, s.Length)}' has an empty box");
            Assert.InRange(q.X1, 0, 595);
            Assert.InRange(q.Y1, 0, 842);
        }

        // One segment per word and per punctuation mark, so a change's anchor can start and end inside a phrase.
        Assert.Equal(["Plain", "words", "(", "a", ")", "and", "3.5", "%", "here", "."], p.Segments.Select(s => p.Text.Substring(s.Start, s.Length)).Where(t => t.Trim().Length > 0));

        // A space between two words on a line is a segment of its own, between the two word boxes.
        AnchoredSpan space = p.Segments.First(s => p.Text.Substring(s.Start, s.Length) == " ");
        Quad gap = Assert.Single(space.Anchor.Quads);
        Assert.True(gap.X2 > gap.X1);
    }

    [Fact]
    public void FormattingIsReadFromTheGlyphs()
    {
        CompareDocument bold = Run.Read(Doc(new Fixtures.Spec { Paragraphs = ["Heavy words here."], Bold = true, Size = 14 }));
        Node p = bold.Nodes.Single(n => n.Kind == NodeKind.Paragraph);
        FormatSet f = Assert.Single(p.Runs).Format;
        Assert.Equal("1", f.Get(FormatKeys.Bold));
        Assert.Equal("14", f.Get(FormatKeys.Size));
        Assert.NotNull(f.Get(FormatKeys.Font));
    }

    [Fact]
    public void SourceAndMarkupAreDeterministic()
    {
        byte[] a = Doc(new Fixtures.Spec { Paragraphs = [Long(3), "Second."] });
        byte[] b = Doc(new Fixtures.Spec { Paragraphs = [Long(3).Replace("thirty", "forty", StringComparison.Ordinal), "Second.", "Third."] });
        Run one = Run.Of(a, b), two = Run.Of(a, b);
        Assert.Equal(one.Marked, two.Marked);
        Assert.Equal(ComparisonJson.Write(one.Comparison), ComparisonJson.Write(two.Comparison));
    }

    [Fact]
    public void IncrementalAndFullSavesBothPassAndIncrementalKeepsTheOriginalBytes()
    {
        byte[] a = Doc(new Fixtures.Spec { Paragraphs = [Long(3)] });
        byte[] b = Doc(new Fixtures.Spec { Paragraphs = [Long(3).Replace("thirty", "forty", StringComparison.Ordinal)] });
        Run inc = Run.Of(a, b);
        Run full = Run.Of(a, b, redline: PdfRedline.OnPages with { Incremental = false });
        inc.AssertReextraction();
        full.AssertReextraction();
        Assert.True(inc.Result.SavedIncrementally);
        Assert.False(full.Result.SavedIncrementally);
        Assert.True(inc.Marked.AsSpan(0, b.Length).SequenceEqual(b));
        Assert.NotEqual(inc.Marked, full.Marked);
    }

    [Fact]
    public void ModifiedDateAndAuthorComeFromTheCaller()
    {
        byte[] a = Doc(new Fixtures.Spec { Paragraphs = ["Alpha beta gamma."] });
        byte[] b = Doc(new Fixtures.Spec { Paragraphs = ["Alpha delta gamma."] });
        var stamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Run r = Run.Of(a, b, redline: PdfRedline.OnPages with { Author = "Reviewer", ModifiedDate = stamp });
        using PdfDocument pdf = PdfDocument.Open(r.Marked);
        PdfAnnotation ann = pdf.Pages[0].Annotations.First(x => x.Subtype == "Highlight");
        Assert.Equal("Reviewer", ann.Title);
        Assert.Equal(stamp, ann.ModifiedDate);
        Assert.Equal("onediff-c" + r.Comparison.Changes[0].Id + "-p0", ann.Name);
    }

    [Fact]
    public void OldDocumentMarkupStrikesDeletedText()
    {
        byte[] a = Doc(new Fixtures.Spec { Paragraphs = ["Alpha beta gamma.", "Dropped paragraph here."] });
        byte[] b = Doc(new Fixtures.Spec { Paragraphs = ["Alpha delta gamma."] });
        Run r = Run.Of(a, b);
        using var ms = new MemoryStream();
        PdfMarkupResult res = PdfRedline.OnPages.MarkOldDocument(r.Comparison, a, ms);
        List<ReadAnnotation> read = Run.ReadAnnotations(ms.ToArray());
        Assert.Equal(res.Marks.Length, read.Count);
        Assert.All(read, x => Assert.Equal("StrikeOut", x.Subtype));
        Assert.Contains(read, x => Run.Canon(x.TextUnder) == "beta");
        Assert.Contains(read, x => Run.Canon(x.TextUnder) == Run.Canon("Dropped paragraph here."));
        Assert.Contains(read, x => PdfRedline.SplitContents(x.Contents!).Text == "delta");
    }

    [Fact]
    public void MarkingRefusesAPdfThatIsNotTheComparisonsInput()
    {
        byte[] a = Doc(new Fixtures.Spec { Paragraphs = ["Alpha."] });
        byte[] b = Doc(new Fixtures.Spec { Paragraphs = ["Beta."] });
        Run r = Run.Of(a, b);
        Assert.Throws<ArgumentException>(() => PdfRedline.OnPages.MarkNewDocument(r.Comparison, a, new MemoryStream()));
        Assert.Throws<ArgumentException>(() => PdfRedline.OnPages.MarkOldDocument(r.Comparison, b, new MemoryStream()));
    }

    [Fact]
    public void RotatedPagesAreMarkedWhereTheTextIs()
    {
        string[] paras = ["Alpha beta gamma delta.", "Second paragraph stays."];
        foreach (int degrees in new[] { 90, 180, 270 })
        {
            byte[] a = Rotate(Doc(new Fixtures.Spec { Paragraphs = paras }), degrees);
            byte[] b = Rotate(Doc(new Fixtures.Spec { Paragraphs = ["Alpha omega gamma delta.", paras[1]] }), degrees);
            Run r = Run.Of(a, b);
            r.AssertComplete();
            r.AssertReextraction();
            Assert.Contains(r.Comparison.Changes, c => c.New.Text == "omega");
        }
    }

    private static string ReplaceFirst(string text, string find, string with)
    {
        int i = text.IndexOf(find, StringComparison.Ordinal);
        return text[..i] + with + text[(i + find.Length)..];
    }

    private static byte[] Rotate(byte[] pdf, int degrees)
    {
        using PdfDocument doc = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });
        doc.SetPageRotation(0, degrees);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }
}
