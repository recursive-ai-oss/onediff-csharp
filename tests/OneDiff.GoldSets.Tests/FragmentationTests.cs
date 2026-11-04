using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.GoldSets.Tests;

/// <summary>The fragmentation counts: changes are the unit, display regions are reported apart and cannot hide a split.</summary>
public class FragmentationTests
{
    private static Iv Iv(int start, int end) => new(true, 3, start, end);

    [Fact]
    public void ARegionHoldingTwoChangesInOneSentenceIsTwoChangesAndOneRegion()
    {
        List<Iv>[] extents = [[Iv(5, 10)], [Iv(60, 70)]];
        (int changes, int regions) = Fragmentation.Touching(extents, [[0, 1]], [Iv(0, 100)]);
        Assert.Equal(2, changes);
        Assert.Equal(1, regions);
    }

    [Fact]
    public void AChangeOutsideTheSentenceIsNotCounted()
    {
        List<Iv>[] extents = [[Iv(5, 10)], [Iv(200, 210)]];
        (int changes, int regions) = Fragmentation.Touching(extents, [[0], [1]], [Iv(0, 100)]);
        Assert.Equal(1, changes);
        Assert.Equal(1, regions);
    }

    [Fact]
    public void NothingTouchingMeansMissed()
    {
        (int changes, int regions) = Fragmentation.Touching([[Iv(200, 210)]], [[0]], [Iv(0, 100)]);
        Assert.Equal(0, changes);
        Assert.Equal(0, regions);
    }

    [Fact]
    public void ADifferentNodeOrSideDoesNotTouch()
    {
        List<Iv>[] extents = [[new Iv(false, 3, 5, 10)], [new Iv(true, 4, 5, 10)]];
        Assert.Equal((0, 0), Fragmentation.Touching(extents, [[0, 1]], [Iv(0, 100)]));
    }

    [Fact]
    public void AMovedParagraphGroupedWithItsEditsStillCountsEachEdit()
    {
        const string Sentence = "The Seller shall deliver the Goods within 30 days to the Buyer at the address named in the purchase order, together with all the documents, certificates, manuals and spare parts that the order lists, and the Buyer shall inspect and accept or reject them in good faith and pay the invoice within 45 days of delivery.";
        const string Edited = "The Seller shall deliver the Goods within 60 days to the Buyer at the address named in the purchase order, together with all the documents, certificates, manuals and spare parts that the order lists, and the Buyer shall inspect and accept or reject them in good faith and pay the invoice within 90 days of delivery.";
        string[] filler =
        [
            "This agreement is governed by the laws of the State of New York without regard to conflict principles.",
            "Each party shall keep the other party's confidential information secret for as long as the agreement lasts.",
            "Neither party may assign this agreement to a third party without the prior written consent of the other.",
            "Notices under this agreement must be in writing and delivered by hand or by registered post to the address given.",
        ];
        Comparison cmp = Comparison.Of(
            CompareDocument.FromText(string.Join("\n\n", [filler[0], Sentence, filler[1], filler[2], filler[3]])),
            CompareDocument.FromText(string.Join("\n\n", [filler[0], filler[1], filler[2], filler[3], Edited])));
        Assert.True(cmp.Verification.Passed);
        Change move = cmp.Changes.Single(c => c.Kind == ChangeKind.Move);
        int node = move.New.Nodes[0];
        string text = cmp.New[node].Text;
        var labels = new PairLabels("t", "en", 0, "", "eval", ["move-edit"], [], [], [], [], [new GoldSentence("new", node, 0, text.Length, [])]);
        Counts c = PairScorer.Score(cmp, labels);
        Assert.Equal(1, c.SentCounted);
        Assert.True(c.SentChanges >= 2, $"changes={c.SentChanges}");
        Assert.Equal(1, c.SentRegions);
        Assert.Equal(0, c.SentMissed);
    }
}
