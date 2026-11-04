using OneDiff.Documents;
using OneDiff.Documents.Model;
using OnePdf;

namespace OneDiff.Pdf.Tests;

public class MarkupTests
{
    private static readonly string[] Clauses =
    [
        "The supplier shall deliver the goods within thirty days of the order and the customer shall pay each invoice within sixty days of receipt.",
        "Either party may terminate this agreement by written notice if the other party commits a material breach and fails to remedy it.",
        "All notices must be in writing and are deemed received two business days after they are posted to the address stated above.",
        "The liability of the supplier under this agreement is limited to the price paid for the goods that gave rise to the claim.",
        "This agreement is governed by the laws of the State of Delaware and the parties submit to the courts of that State.",
        "Neither party may assign its rights under this agreement without the prior written consent of the other party.",
    ];

    private static byte[] Doc(string[] paragraphs, Fixtures.Spec? template = null) =>
        Fixtures.Make((template ?? new Fixtures.Spec()) with { Paragraphs = paragraphs });

    private static void Gate(Run r)
    {
        r.AssertComplete();
        r.AssertReextraction();
    }

    [Fact]
    public void IdenticalDocumentsHaveNoChangesAndNoMarks()
    {
        byte[] a = Doc(Clauses);
        Run r = Run.Of(a, a);
        Gate(r);
        Assert.Empty(r.Comparison.Changes);
        Assert.Empty(r.Result.Marks);
    }

    [Fact]
    public void ReplacedWordsAreHighlightedAndKeepTheOldTextInThePopup()
    {
        string[] edited = [.. Clauses];
        edited[0] = edited[0].Replace("thirty days", "forty-five days", StringComparison.Ordinal);
        edited[3] = edited[3].Replace("limited to the price", "limited to twice the price", StringComparison.Ordinal);
        Run r = Run.Of(Doc(Clauses), Doc(edited));
        Gate(r);
        Assert.NotEmpty(r.Comparison.Changes);
        Assert.Contains(r.Result.Marks, m => m.Kind == PdfMarkupKind.Highlight);
        Change replace = Assert.Single(r.Of(ChangeKind.Replace), c => c.Old.Text.Contains("thirty", StringComparison.Ordinal));
        Assert.Contains("forty", replace.New.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeInsideADottedNumberMarksTheWholeNumber()
    {
        // The text core compares the number as one word, so a change that only cuts into it still marks the number on the page.
        string[] oldText = ["See exhibit 10.8 and exhibit 19852_1ex10 for the terms of the plan."];
        string[] newText = ["See exhibit 10 and exhibit 19852_ex10 for the terms of the plan."];
        Gate(Run.Of(Doc(oldText), Doc(newText)));
        Gate(Run.Of(Doc(newText), Doc(oldText)));
    }

    [Fact]
    public void InsertedSentenceAndDeletedParagraphAndMovedParagraph()
    {
        string[] edited = [Clauses[0] + " Late payment bears interest at one percent per month.", Clauses[2], Clauses[1], Clauses[3], Clauses[5]];
        Run r = Run.Of(Doc(Clauses), Doc(edited));
        Gate(r);
        Assert.NotEmpty(r.Of(ChangeKind.Insert));
        Assert.Contains(r.Result.Marks, m => m.Kind == PdfMarkupKind.Caret);
    }

    [Fact]
    public void DeletedParagraphGetsACaretWithItsTextInThePopup()
    {
        string[] edited = [Clauses[0], Clauses[1], Clauses[3], Clauses[4], Clauses[5]];
        Run r = Run.Of(Doc(Clauses), Doc(edited));
        Gate(r);
        Change del = Assert.Single(r.Of(ChangeKind.Delete));
        PdfMark mark = Assert.Single(r.Result.Marks, m => m.ChangeId == del.Id);
        Assert.Equal(PdfMarkupKind.Caret, mark.Kind);
        List<ReadAnnotation> read = Run.ReadAnnotations(r.Marked);
        ReadAnnotation caret = Assert.Single(read, a => a.Name == mark.Name);
        Assert.StartsWith("\u2212 Deleted", caret.Contents, StringComparison.Ordinal);
        Assert.Equal(Clauses[2], PdfRedline.SplitContents(caret.Contents!).Text);
    }

    [Fact]
    public void FormattingChangeGetsAnUnderlineWithAnExplanation()
    {
        byte[] plain = Doc(Clauses);
        byte[] bold = Doc(Clauses, new Fixtures.Spec { Bold = true });
        Run r = Run.Of(plain, bold);
        Gate(r);
        Assert.NotEmpty(r.Of(ChangeKind.Format));
        List<ReadAnnotation> read = Run.ReadAnnotations(r.Marked);
        Assert.All(read, a => Assert.Equal("Underline", a.Subtype));
        Assert.Contains(read, a => a.Contents!.Contains("bold none \u2192 1", StringComparison.Ordinal));
    }

    [Fact]
    public void NumberedListInsertionIsAMarkedChange()
    {
        byte[] a = Fixtures.Make(new Fixtures.Spec { Paragraphs = [Clauses[0]], NumberedList = ["first duty", "second duty", "third duty"] });
        byte[] b = Fixtures.Make(new Fixtures.Spec { Paragraphs = [Clauses[0]], NumberedList = ["first duty", "added duty", "second duty", "third duty"] });
        Run r = Run.Of(a, b);
        Gate(r);
        Assert.NotEmpty(r.Comparison.Changes);
        Assert.Contains(r.Result.Marks, m => m.Kind == PdfMarkupKind.Squiggly);
        Assert.Contains(Run.ReadAnnotations(r.Marked), a => a.Subtype == "Squiggly" && a.Contents!.Contains("list.label", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoColumnPagesGate()
    {
        string[] longer = [.. Enumerable.Range(0, 3).SelectMany(_ => Clauses)];
        string[] edited = [.. longer];
        edited[4] = edited[4].Replace("Delaware", "New York", StringComparison.Ordinal);
        edited[9] = "A new clause was added in the second column of this page.";
        var spec = new Fixtures.Spec { Columns = 2, PageHeight = 400 };
        Run r = Run.Of(Doc(longer, spec), Doc(edited, spec));
        Gate(r);
        Assert.NotEmpty(r.Comparison.Changes);
    }

    [Fact]
    public void HeadersAndPageNumbersDoNotShowAsChangesWhenPagesShift()
    {
        string[] longer = [.. Enumerable.Range(0, 5).SelectMany(_ => Clauses)];
        string[] more = [.. longer.Take(10), "An additional clause that pushes the following text onto later pages and changes the page count.", .. longer.Skip(10)];
        var spec = new Fixtures.Spec { Header = "Master Services Agreement", PageNumbers = true, PageHeight = 420 };
        Run r = Run.Of(Doc(longer, spec), Doc(more, spec));
        Gate(r);
        // The inserted clause is the only difference that is shown; page numbers and totals went to the ledger.
        Assert.DoesNotContain(r.Comparison.Changes, c => c.Old.Text.Contains("Page", StringComparison.Ordinal) || c.New.Text.Contains("Page", StringComparison.Ordinal));
    }
}
