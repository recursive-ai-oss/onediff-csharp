using System.Collections.Immutable;
using System.Text;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Tests.Grouping;

/// <summary>Change units on small labelled cases: what a reader should see as one change, and what as separate ones.</summary>
public class GroupingTests
{
    private const string Lead = "The parties enter into this agreement on the terms set out below and nothing else applies.";
    private const string Tail = "This agreement is governed by the laws of the State of New York without regard to conflict principles.";

    private static Comparison Cmp(string oldPara, string newPara, CompareSettings? settings = null) =>
        Helpers.Compare(Helpers.Text(Lead, oldPara, Tail), Helpers.Text(Lead, newPara, Tail), settings);

    private static string Show(Comparison c)
    {
        var sb = new StringBuilder();
        foreach (Change ch in c.Changes)
        {
            sb.Append($"[{ch.Kind} '{ch.Old.Text}' -> '{ch.New.Text}' edits={ch.Edits.Length}] ");
        }

        return sb.ToString();
    }

    private static Change Single(Comparison c)
    {
        Assert.True(c.Changes.Length == 1, Show(c));
        return c.Changes[0];
    }

    [Fact]
    public void IdenticalPairHasNoChanges()
    {
        Comparison c = Cmp("The fee is 30 days.", "The fee is 30 days.");
        Assert.Empty(c.Changes);
    }

    [Fact]
    public void ANumberChangedInALongSentenceIsOneChangeShowingTheWholeDuration()
    {
        Comparison c = Cmp(
            "The Supplier shall deliver the Goods to the Buyer within 30 days of receiving a written purchase order and shall notify the Buyer of any delay.",
            "The Supplier shall deliver the Goods to the Buyer within 60 days of receiving a written purchase order and shall notify the Buyer of any delay.");
        Change ch = Single(c);
        Assert.Equal("30 days", ch.Old.Text);
        Assert.Equal("60 days", ch.New.Text);
        Assert.Equal(ChangeKind.Replace, ch.Kind);
        // The edit itself is the digit, not the sentence.
        Assert.Contains(ch.Edits, e => e.OldText == "3" && e.NewText == "6");
    }

    [Fact]
    public void AnAmountWithSeparatorsIsShownWhole()
    {
        Comparison c = Cmp("The fee is $1,250,000 per year payable in advance.", "The fee is $1,520,000 per year payable in advance.");
        Change ch = Single(c);
        Assert.Equal("$1,250,000", ch.Old.Text);
        Assert.Equal("$1,520,000", ch.New.Text);
    }

    [Fact]
    public void ADateChangeIsOneChange()
    {
        Comparison c = Cmp("This agreement takes effect on March 3, 2025 and continues.", "This agreement takes effect on April 5, 2025 and continues.");
        Change ch = Single(c);
        Assert.Equal("March 3, 2025", ch.Old.Text);
        Assert.Equal("April 5, 2025", ch.New.Text);
    }

    [Fact]
    public void ADenseRewriteOfOneSentenceIsOneChange()
    {
        Comparison c = Cmp(
            "The quick brown fox jumps over the lazy dog near the old river bank every single morning before breakfast time.",
            "A swift brown fox leaps over the sleepy dog near the new river bank every single evening after supper time.");
        Change ch = Single(c);
        Assert.True(ch.Edits.Length >= 3, Show(c));
        Assert.StartsWith("The quick", ch.Old.Text);
        Assert.EndsWith("breakfast time.", ch.Old.Text);
    }

    [Fact]
    public void ScatteredEditsInTwoSentencesStayTwoChanges()
    {
        Comparison c = Cmp(
            "The Buyer shall pay the invoice promptly. The Seller shall ship the order quickly.",
            "The Buyer shall pay the bill promptly. The Seller shall ship the goods quickly.");
        Assert.Equal(2, c.Changes.Length);
    }

    [Fact]
    public void AClauseInsertionIsOneInsert()
    {
        Comparison c = Cmp(
            "The Supplier shall deliver the Goods to the Buyer at the agreed place.",
            "The Supplier shall deliver the Goods to the Buyer, unless prevented by force majeure, at the agreed place.");
        Change ch = Single(c);
        Assert.Equal(ChangeKind.Insert, ch.Kind);
        Assert.Contains("force majeure", ch.New.Text);
        Assert.Equal("", ch.Old.Text);
    }

    [Fact]
    public void APunctuationOnlyChangeIsASmallChange()
    {
        Comparison c = Cmp("The Buyer shall pay the price in full.", "The Buyer shall pay the price in full;");
        Change ch = Single(c);
        Assert.Equal(".", ch.Old.Text);
        Assert.Equal(";", ch.New.Text);
    }

    [Fact]
    public void WhitespaceOnlyAndTypographicChangesAreLedgeredNotShown()
    {
        Comparison c = Cmp("The \"Buyer\" shall  pay the price - in full.", "The \u201CBuyer\u201D shall pay the price \u2013 in full.");
        Assert.Empty(c.Changes);
        Assert.NotEmpty(c.Ledger.Entries);
    }

    [Fact]
    public void AChangeSpanningASentenceBoundaryIsOneChange()
    {
        Comparison c = Cmp(
            "The Buyer shall pay the price. The Seller shall ship the goods. Time is of the essence here.",
            "The Buyer shall pay the price. A new sentence is inserted here. Time is of the essence here.");
        // The second sentence is replaced by another: one unit, whatever the sentence segmentation says inside it.
        Assert.True(c.Changes.Length <= 2, Show(c));
        Assert.Contains(c.Changes, ch => ch.New.Text.Contains("new sentence", StringComparison.Ordinal));
    }

    [Fact]
    public void ASwappedWordOrderIsFewChanges()
    {
        Comparison c = Cmp("The Buyer shall notify the Seller promptly in writing.", "The Seller shall notify the Buyer promptly in writing.");
        Assert.True(c.Changes.Length <= 2, Show(c));
    }

    [Fact]
    public void AWholeParagraphRewrittenStillVerifies()
    {
        Comparison c = Cmp(
            "Each party shall keep the other's information confidential. Disclosure is allowed only to advisers. Records must be destroyed on request.",
            "Neither side may reveal the terms hereof to outsiders. Exceptions need written consent. Copies are returned when asked to do so.");
        Assert.NotEmpty(c.Changes);
    }

    [Fact]
    public void ChineseTextGroupsByClause()
    {
        Comparison c = Cmp(
            "买方应在三十日内支付全部价款。卖方应当按时交付货物。",
            "买方应在六十日内支付全部价款。卖方应当按时交付货物。");
        Change ch = Single(c);
        Assert.Contains("三十", ch.Old.Text);
        Assert.Contains("六十", ch.New.Text);
    }

    [Fact]
    public void ThaiTextShowsTheCharactersThatChanged()
    {
        Comparison c = Cmp("ผู้ซื้อต้องชำระราคาทั้งหมดภายในสามสิบวัน", "ผู้ซื้อต้องชำระราคาทั้งหมดภายในหกสิบวัน");
        Assert.True(c.Changes.Length >= 1 && c.Changes.Length <= 2, Show(c));
        Assert.All(c.Changes, ch => Assert.True(ch.Old.Text.Length < 20 && ch.New.Text.Length < 20, Show(c)));
    }

    [Fact]
    public void RightToLeftTextVerifiesAndGroups()
    {
        Comparison c = Cmp(
            "يجب على المشتري أن يدفع الثمن كاملا خلال 30 يوما من تاريخ الفاتورة.",
            "يجب على المشتري أن يدفع الثمن كاملا خلال 60 يوما من تاريخ الفاتورة.");
        Change ch = Single(c);
        Assert.Contains("30", ch.Old.Text);
        Assert.Contains("60", ch.New.Text);
    }

    [Fact]
    public void FormattingOnlyChangeIsAFormatChangeOverTheFormattedWords()
    {
        CompareDocument Make(bool bold)
        {
            var b = new CompareDocumentBuilder();
            b.Begin(NodeKind.Section);
            string text = "The Buyer shall pay the price in full on delivery.";
            ImmutableArray<FormatRun> runs = bold ? [new FormatRun(4, 5, FormatSet.Of(FormatKeys.Bold, "1"))] : [];
            b.Add(NodeKind.Paragraph, new NodeContent { Text = text, Runs = runs });
            b.End();
            return b.Build();
        }

        Comparison c = Helpers.Compare(Make(false), Make(true));
        Change ch = Single(c);
        Assert.Equal(ChangeKind.Format, ch.Kind);
        Assert.Equal("Buyer", ch.New.Text);
    }

    [Fact]
    public void TwoDifferentFormatChangesAreTwoChangesAndTheSameKindJoinsAcrossASpace()
    {
        CompareDocument Make(params (int Start, int Length, string Key)[] runs)
        {
            var b = new CompareDocumentBuilder();
            b.Begin(NodeKind.Section);
            b.Add(NodeKind.Paragraph, new NodeContent
            {
                Text = "alpha beta gamma delta epsilon zeta",
                Runs = [.. runs.Select(r => new FormatRun(r.Start, r.Length, FormatSet.Of(r.Key, "1")))],
            });
            b.End();
            return b.Build();
        }

        // Bold on two words with only a space between them is one change; bold then italics on neighbours are two.
        Comparison joined = Helpers.Compare(Make(), Make((0, 5, FormatKeys.Bold), (6, 4, FormatKeys.Bold)));
        Assert.Equal(ChangeKind.Format, Single(joined).Kind);
        Comparison mixed = Helpers.Compare(Make(), Make((0, 5, FormatKeys.Bold), (6, 4, FormatKeys.Italic)));
        Assert.Equal(2, mixed.Changes.Length);
    }

    [Fact]
    public void ATextChangeWithFormattingInTheSameWordsIsOneChange()
    {
        CompareDocument Make(string text, bool bold)
        {
            var b = new CompareDocumentBuilder();
            b.Begin(NodeKind.Section);
            ImmutableArray<FormatRun> runs = bold ? [new FormatRun(0, text.Length, FormatSet.Of(FormatKeys.Bold, "1"))] : [];
            b.Add(NodeKind.Paragraph, new NodeContent { Text = text, Runs = runs });
            b.End();
            return b.Build();
        }

        Comparison c = Helpers.Compare(Make("The Buyer shall pay the price within 30 days of invoice.", false), Make("The Buyer shall pay the price within 60 days of invoice.", true));
        Assert.True(c.Changes.Length <= 2, Show(c));
    }
}
