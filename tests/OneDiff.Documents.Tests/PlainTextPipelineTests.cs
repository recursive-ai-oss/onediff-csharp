using OneDiff.Documents.Ledger;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using static OneDiff.Documents.Tests.Helpers;

namespace OneDiff.Documents.Tests;

public class PlainTextPipelineTests
{
    private const string P1 = "The supplier shall deliver the goods within thirty days of the order date.";
    private const string P2 = "Payment is due sixty days after receipt of a correct invoice from the supplier.";
    private const string P3 = "Either party may terminate this agreement on written notice for material breach.";
    private const string P4 = "This agreement is governed by the laws of the State of New York without regard to conflicts.";

    [Fact]
    public void IdenticalDocumentsHaveNoChanges()
    {
        Comparison c = Compare(Text(P1, P2, P3), Text(P1, P2, P3));
        Assert.Empty(c.Changes);
        Assert.Empty(c.Ledger.Entries);
        Assert.All(c.Alignment.Links, l => Assert.Equal(LinkKind.Match, l.Kind));
    }

    [Fact]
    public void EmptyDocuments()
    {
        Assert.Empty(CompareText("", "").Changes);

        Comparison ins = CompareText("", P1);
        Change insert = Assert.Single(ins.Changes);
        Assert.Equal(ChangeKind.Insert, insert.Kind);
        Assert.Equal(P1, insert.New.Text);
        Assert.False(insert.Old.IsPresent);

        Comparison del = CompareText(P1, "");
        Change delete = Assert.Single(del.Changes);
        Assert.Equal(ChangeKind.Delete, delete.Kind);
        Assert.Equal(P1, delete.Old.Text);
    }

    [Fact]
    public void InsertedParagraph()
    {
        Comparison c = Compare(Text(P1, P3), Text(P1, P2, P3));
        Change ch = Assert.Single(c.Changes);
        Assert.Equal(ChangeKind.Insert, ch.Kind);
        Assert.Equal(-1, ch.LinkId);
        Assert.Equal(P2, ch.New.Text);
        Assert.Equal(AnchorKind.TextSpan, ch.New.Anchor.Kind);
        Assert.Equal(AnchorKind.None, ch.Old.Anchor.Kind);
    }

    [Fact]
    public void DeletedParagraph()
    {
        Comparison c = Compare(Text(P1, P2, P3), Text(P1, P3));
        Change ch = Assert.Single(c.Changes);
        Assert.Equal(ChangeKind.Delete, ch.Kind);
        Assert.Equal(P2, ch.Old.Text);
    }

    [Fact]
    public void ReplacedWordsAreOneChangeWithBothSidesAndAnchors()
    {
        string newP1 = P1.Replace("thirty", "forty-five", StringComparison.Ordinal);
        Comparison c = Compare(Text(P1, P2), Text(newP1, P2));
        Change ch = Assert.Single(c.Changes);
        Assert.Equal(ChangeKind.Replace, ch.Kind);
        Assert.Equal("thirty", ch.Old.Text);
        Assert.Equal("forty-five", ch.New.Text);
        Assert.Equal(AnchorKind.TextSpan, ch.Old.Anchor.Kind);
        Assert.Equal(0, ch.Old.Anchor.Start);
        Assert.Equal(P1.Length, ch.Old.Anchor.Length);
    }

    [Fact]
    public void WordInsertedAndDeletedInsideAParagraph()
    {
        Comparison c = Compare(Text(P1), Text(P1.Replace("the goods", "the goods promptly", StringComparison.Ordinal)));
        Assert.Equal(ChangeKind.Insert, Assert.Single(c.Changes).Kind);
        Comparison d = Compare(Text(P1), Text(P1.Replace("goods ", "", StringComparison.Ordinal)));
        Assert.Equal(ChangeKind.Delete, Assert.Single(d.Changes).Kind);
    }

    [Fact]
    public void MovedParagraphIsOneMoveChange()
    {
        Comparison c = Compare(Text(P1, P2, P3, P4), Text(P1, P3, P4, P2));
        Change move = Assert.Single(c.Changes);
        Assert.Equal(ChangeKind.Move, move.Kind);
        Assert.Equal(P2, move.Old.Text);
        Assert.Equal(P2, move.New.Text);
        Assert.Equal(1, c.Alignment.Links.Count(l => l.Kind == LinkKind.Move));
    }

    [Fact]
    public void MovedAndEditedParagraphHasTheMoveAndTheEdit()
    {
        string edited = P2.Replace("sixty", "ninety", StringComparison.Ordinal);
        Comparison c = Compare(Text(P1, P2, P3, P4), Text(P1, P3, P4, edited));
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Move);
        Change r = Assert.Single(c.Changes, x => x.Kind == ChangeKind.Replace);
        Assert.Equal("sixty", r.Old.Text);
        Assert.Equal("ninety", r.New.Text);
        Assert.Equal(c.Changes.First(x => x.Kind == ChangeKind.Move).LinkId, r.LinkId);
    }

    [Fact]
    public void SplitParagraph()
    {
        const string whole = "The tenant shall pay rent monthly in advance. The landlord shall keep the building insured against fire.";
        Comparison c = Compare(Text(P1, whole, P4), Text(P1, "The tenant shall pay rent monthly in advance.", "The landlord shall keep the building insured against fire.", P4));
        Change split = Assert.Single(c.Changes);
        Assert.Equal(ChangeKind.Split, split.Kind);
        Assert.Single(split.Old.Nodes);
        Assert.Equal(2, split.New.Nodes.Length);
        Assert.Contains("\u001E", split.New.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MergedParagraphs()
    {
        Comparison c = Compare(
            Text(P1, "The tenant shall pay rent monthly in advance.", "The landlord shall keep the building insured against fire.", P4),
            Text(P1, "The tenant shall pay rent monthly in advance. The landlord shall keep the building insured against fire.", P4));
        Change merge = Assert.Single(c.Changes);
        Assert.Equal(ChangeKind.Merge, merge.Kind);
        Assert.Equal(2, merge.Old.Nodes.Length);
        Assert.Single(merge.New.Nodes);
    }

    [Fact]
    public void NormalizationOnlyDifferenceGoesToTheLedger()
    {
        Comparison c = CompareText("He said \u201Chello\u201D \u2014 and left.", "He said \"hello\" - and left.");
        Assert.Empty(c.Changes);
        LedgerEntry quotes = c.Ledger.Get(LedgerCategory.Quotes)!;
        Assert.Equal(2, quotes.Count);
        Assert.Equal(2, quotes.Locations.Length);
        Assert.Equal(1, c.Ledger.Get(LedgerCategory.Dashes)!.Count);
        Assert.Equal(3, c.Ledger.TotalCount);
        Assert.All(quotes.Locations, l => Assert.Equal(AnchorKind.TextSpan, l.OldAnchor.Kind));
    }

    [Fact]
    public void WhitespaceOnlyDifferenceGoesToTheLedger()
    {
        Comparison c = CompareText("a  b\t c\r\nd", "a b c\nd");
        Assert.Empty(c.Changes);
        Assert.True(c.Ledger.Get(LedgerCategory.Whitespace)!.Count >= 2);
    }

    [Fact]
    public void StrictShowsTheNormalizationDifference()
    {
        CompareSettings strict = CompareSettings.Strict;
        Comparison c = CompareText("He said \u201Chello\u201D.", "He said \"hello\".", strict);
        Assert.NotEmpty(c.Changes);
        Assert.All(c.Changes, x => Assert.Equal(ChangeKind.Replace, x.Kind));
        Assert.Empty(c.Ledger.Entries);
        Assert.Contains(c.Changes, x => x.Old.Text.Contains('\u201C', StringComparison.Ordinal));
    }

    [Fact]
    public void ChangedNormalizedAndUnchangedMixture()
    {
        Comparison c = CompareText("He said \u201Chello\u201D to Bob.", "He said \"hello\" to Alice.");
        Change ch = Assert.Single(c.Changes);
        Assert.Equal("Bob", ch.Old.Text);
        Assert.Equal(2, c.Ledger.Get(LedgerCategory.Quotes)!.Count);
    }

    [Fact]
    public void PinningCorrectsTheAlignmentAndTheStagesRunAgain()
    {
        CompareDocument a = Text(P1, P2), b = Text("Completely different opening text about something else entirely.", P2.Replace("sixty", "ninety", StringComparison.Ordinal));
        Alignment al = Stages.Align(a, b);
        // Section is node 0, paragraphs are 1 and 2 in both documents. Pair the first paragraphs by hand.
        Alignment pinned = al.Pin(1, 1);
        Assert.True(pinned.LinkOfOld(1)!.Pinned);
        Comparison c = Clean(Stages.Verify(Stages.Classify(Stages.Group(Stages.DiffPairs(pinned)))));
        Assert.Contains(c.Changes, x => x.LinkId == pinned.LinkOfOld(1)!.Id);
        Alignment again = Stages.Align(a, b, seed: pinned);
        Assert.True(again.LinkOfOld(1)!.Pinned);
        Alignment released = again.Unpin(again.LinkOfOld(1)!.Id);
        Assert.Null(released.LinkOfOld(1));
        Clean(Stages.Verify(Stages.Classify(Stages.Group(Stages.DiffPairs(released)))));
    }

    [Fact]
    public void ForcedSplitAndMergeVerify()
    {
        CompareDocument a = Text("alpha beta gamma delta", "unrelated tail"), b = Text("alpha beta", "gamma delta", "unrelated tail");
        Alignment al = Stages.Align(a, b).Force([1], [1, 2], LinkKind.Split);
        Comparison c = Clean(Stages.Verify(Stages.Classify(Stages.Group(Stages.DiffPairs(al)))));
        Assert.Single(c.Changes, x => x.Kind == ChangeKind.Split);
    }

    [Fact]
    public void AllStagesAreDeterministic()
    {
        CompareDocument a = Text(P1, P2, P3, P4), b = Text(P4, P1.Replace("thirty", "ten", StringComparison.Ordinal), P3);
        Comparison x = Comparison.Of(a, b), y = Comparison.Of(a, b);
        Assert.Equal(x.Changes.Length, y.Changes.Length);
        for (int i = 0; i < x.Changes.Length; i++)
        {
            Assert.Equal(x.Changes[i].Kind, y.Changes[i].Kind);
            Assert.Equal(x.Changes[i].Old.Text, y.Changes[i].Old.Text);
            Assert.Equal(x.Changes[i].New.Text, y.Changes[i].New.Text);
        }
    }

    [Fact]
    public void CostCapGivesACoarserCompleteResultWithADiagnostic()
    {
        string a = string.Join(' ', Enumerable.Range(0, 400).Select(i => "w" + i));
        string b = string.Join(' ', Enumerable.Range(0, 400).Select(i => i % 3 == 0 ? "x" + i : "w" + i));
        CompareSettings small = CompareSettings.Legal with { CostCap = 1000 };
        Comparison c = CompareText(a, b, small);
        Assert.Contains(c.Diagnostics, d => d.Code == "diffpairs.cap");
        Assert.Single(c.Changes);
    }

    [Fact]
    public void SniffingIgnoresNamesAndRejectsBinary()
    {
        CompareDocument d = CompareDocument.From(DocumentInput.FromBytes("hello\n\nworld"u8.ToArray(), "whatever.docx"));
        Assert.Equal(3, d.Count);
        Assert.Throws<NotSupportedException>(() => CompareDocument.From(DocumentInput.FromBytes(new byte[] { 1, 0, 2 }, "a.txt")));
    }
}

public class VersionTests
{
    [Fact]
    public void ProvenanceVersionMatchesTheBuiltPackageVersion()
    {
        var attribute = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
            typeof(Comparison).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        Assert.NotNull(attribute);
        Assert.StartsWith(Comparison.LibraryVersion, attribute.InformationalVersion, StringComparison.Ordinal);
    }
}
