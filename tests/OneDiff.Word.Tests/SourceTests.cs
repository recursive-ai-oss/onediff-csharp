using OneDiff.Documents.Model;

namespace OneDiff.Word.Tests;

public class SourceTests
{
    [Fact]
    public void EveryStructureIsMapped()
    {
        byte[] docx = DocFactory.Build(Harness.Rich());
        CompareDocument d = Harness.Read(docx);

        Assert.Equal(DocumentKind.Flowing, d.Kind);
        Assert.Equal(WordDocumentSource.MediaType, d.SourceFormat);
        foreach (NodeKind k in new[]
        {
            NodeKind.Section, NodeKind.Header, NodeKind.Footer, NodeKind.Paragraph, NodeKind.ListItem, NodeKind.Table, NodeKind.TableRow, NodeKind.TableCell,
            NodeKind.Footnote, NodeKind.Endnote, NodeKind.Comment, NodeKind.Field, NodeKind.Image, NodeKind.TextBox, NodeKind.ContentControl, NodeKind.Property,
        })
        {
            Assert.True(Harness.Of(d, k).Any(), "missing " + k);
        }

        Assert.Equal(2, Harness.Of(d, NodeKind.Section).Count());
        Assert.Equal(["1.", "a)", "\u2022"], Harness.Of(d, NodeKind.ListItem).Select(n => n.Attributes.Get("list.label")).ToArray());
        Assert.Equal(["0", "1", "0"], Harness.Of(d, NodeKind.ListItem).Select(n => n.Attributes.Get("list.level")).ToArray());

        Node span = Harness.Of(d, NodeKind.TableCell).Single(c => c.Attributes.Get("table.span") == "2");
        Assert.Equal("2", span.Attributes.Get("table.span"));
        Assert.Equal(3, Harness.Of(d, NodeKind.TableRow).Count());

        Node field = Harness.Of(d, NodeKind.Field).Single();
        Assert.Equal("DATE \\@ \"d MMMM yyyy\"", field.Attributes.Get("field.code"));
        Assert.Equal("4 November 2026", field.Text);

        Node image = Harness.Of(d, NodeKind.Image).Single();
        Assert.Equal(64, image.Attributes.Get("hash.content")!.Length);

        Assert.Equal("Inside the box", Harness.Of(d, NodeKind.Paragraph).Last(p => p.Text == "Inside the box").Text);
        Node title = Harness.Of(d, NodeKind.Paragraph).First(p => p.Text == "Master Agreement");
        Assert.Equal("Heading1", title.Attributes.Get("para.style"));
        Assert.Equal("Confidential header", d.Nodes.First(n => n.Parent == Harness.Of(d, NodeKind.Header).Single().Id).Text);

        Node note = Harness.Of(d, NodeKind.Footnote).Single();
        Assert.Contains("A footnote about payment.", d.Nodes.First(n => n.Kind == NodeKind.Paragraph && n.Parent == note.Id).Text, StringComparison.Ordinal);
        Assert.Contains(Harness.Of(d, NodeKind.Property), p => p.Attributes.Get("prop.name") == "title" && p.Text == "Agreement");
        Assert.Contains(Harness.Of(d, NodeKind.Property), p => p.Attributes.Get("prop.name") == "creator" && p.Text == "Alice");
        Assert.Equal("Check this", d.Nodes.First(n => n.Kind == NodeKind.Paragraph && n.Parent == Harness.Of(d, NodeKind.Comment).Single().Id).Text);

        // Objects stand in the text as U+FFFC; comments leave no mark.
        Node host = Harness.Of(d, NodeKind.Paragraph).First(p => p.Text.StartsWith("Payable on", StringComparison.Ordinal));
        Assert.Equal(3, host.Text.Count(c => c == '\uFFFC'));
        Node commented = Harness.Of(d, NodeKind.Paragraph).First(p => p.Text.StartsWith("Controlled", StringComparison.Ordinal));
        Assert.Equal("Controlled \uFFFC text.", commented.Text);
        Assert.Equal(1, commented.Text.Count(c => c == '\uFFFC'));
    }

    [Fact]
    public void RunFormattingIsTheResolvedFormatting()
    {
        CompareDocument d = Harness.Read(DocFactory.Build(Harness.Rich()));
        Node p = Harness.Of(d, NodeKind.Paragraph).First(n => n.Text.StartsWith("The supplier", StringComparison.Ordinal));
        string text = p.Text;
        FormatSet At(string word) => p.Runs.Single(r => r.Start <= text.IndexOf(word, StringComparison.Ordinal) && text.IndexOf(word, StringComparison.Ordinal) < r.End).Format;

        Assert.Equal("1", At("supplier").Get("run.bold"));
        Assert.Equal("1", At("thirty").Get("run.italic"));
        Assert.Equal("FF0000", At("thirty").Get("run.color"));
        Assert.Equal("https://example.com/terms", At("the website").Get("run.link"));
        Assert.Equal("11", At("shall").Get("run.size"));

        Node big = Harness.Of(d, NodeKind.ListItem).First();
        Assert.Equal("Arial", big.Runs[0].Format.Get("run.font"));
        Assert.Equal("14", big.Runs[0].Format.Get("run.size"));
    }

    [Fact]
    public void AnchorsResolveBackToTheElementsAndTextStretches()
    {
        byte[] docx = DocFactory.Build(Harness.Rich());
        Harness.Resolves(docx, Harness.Read(docx));
    }

    [Fact]
    public void TheModelIsWellFormedAndDeterministic()
    {
        byte[] docx = DocFactory.Build(Harness.Rich());
        CompareDocument a = Harness.Read(docx), b = Harness.Read(docx);
        Assert.Equal(a.Nodes.Length, b.Nodes.Length);
        for (int i = 0; i < a.Nodes.Length; i++)
        {
            Assert.Equal(a.Nodes[i].Text, b.Nodes[i].Text);
            Assert.Equal(a.Nodes[i].Anchor.Reference, b.Nodes[i].Anchor.Reference);
        }

        Assert.Equal(DocFactory.Build(Harness.Rich()), docx);
    }

    [Fact]
    public void ARunSplitAcrossTypographicFoldsKeepsRunsInsideTheText()
    {
        byte[] docx = DocFactory.Build(new DocSpec(new Para(new T("It\u2019s  "), new T("\u201Cquoted\u201D \u2014 text") { Bold = true })));
        CompareDocument d = Harness.Read(docx);
        Node p = Harness.Of(d, NodeKind.Paragraph).First(n => n.Text.Contains("quoted", StringComparison.Ordinal));
        Assert.Equal("It's \"quoted\" - text", p.Text);
        Assert.Equal("It\u2019s  \u201Cquoted\u201D \u2014 text", p.RawText);
        Assert.NotEmpty(p.Folds);
    }
}
