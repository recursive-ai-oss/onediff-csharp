using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Tests.Alignments;

/// <summary>
/// Equal and near-equal text that occurs more than once: which copy a block is paired with is decided by where it sits
/// (its row, its neighbours, its order), not by which copy the sequence match happened to reach first.
/// </summary>
public class EqualTextTieBreakTests
{
    private const int Cols = 3;

    // Node numbers of FixtureTable: section 0, table 1, then each row is a row node followed by (cell, paragraph) per column.
    private static int Row(int r) => 2 + (r * (1 + (2 * Cols)));

    private static int Cell(int r, int c) => Row(r) + 1 + (2 * c);

    private static int Text(int r, int c) => Cell(r, c) + 1;

    private static AlignmentLink? Partner(Alignment al, int oldNode) => al.LinkOfOld(oldNode);

    [Fact]
    public void DeletedRowDoesNotLendItsEqualCellToTheRowAbove()
    {
        string[] l1 = ["Support", "30 days", "USD 25,000"];
        string[] l2 = ["Hosting", "1 year", "USD 25,000"];
        string[] l3 = ["Licence", "60 days", "USD 25,000"];
        CompareDocument a = Fx.TableDoc([l1, l2, l3], Fx.Para(1));
        CompareDocument b = Fx.TableDoc([l1, l2], Fx.Para(1));
        Alignment al = Fx.Align(a, b, Strict());

        for (int r = 0; r < 2; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                AlignmentLink? l = Partner(al, Text(r, c));
                Assert.NotNull(l);
                Assert.Equal(LinkKind.Match, l.Kind);
                Assert.Equal(Text(r, c), l.NewNodes[0]);
            }
        }

        for (int c = 0; c < Cols; c++)
        {
            Assert.Null(Partner(al, Text(2, c)));
        }

        Fx.Clean(a, b, Strict());
    }

    [Fact]
    public void EditedCellsKeepTheirRowWhenAnotherRowHoldsTheNewValue()
    {
        CompareDocument a = Fx.TableDoc(
            [
                ["Item", "Term", "Fee"],
                ["Support", "term of 9 months", "EUR 18,000"],
                ["Setup", "5 years", "EUR 60,000"],
                ["Training", "1 year", "EUR 9,000"],
            ],
            Fx.Para(2));
        CompareDocument b = Fx.TableDoc(
            [
                ["Item", "Term", "Fee"],
                ["Support", "term of 5 months", "EUR 9,000"],
                ["Setup", "5 years", "EUR 60,000"],
                ["Training", "1 year", "EUR 4,800"],
            ],
            Fx.Para(2));
        Alignment al = Fx.Align(a, b, Strict());

        Assert.False(al.Links.Any(l => l.Kind == LinkKind.Move), Describe(al));
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                AlignmentLink? l = Partner(al, Text(r, c));
                Assert.NotNull(l);
                Assert.Equal(Text(r, c), l.NewNodes[0]);
            }
        }

        Fx.Clean(a, b, Strict());
    }

    [Fact]
    public void InsertedCopyOfARowIsTheLaterCopy()
    {
        string[] header = ["Item", "Term", "Fee"];
        string[] setup = ["Setup", "1 year", "EUR 60,000"];
        string[] upkeep = ["Upkeep", "15 days", "EUR 12,500"];
        CompareDocument a = Fx.TableDoc([header, setup, upkeep], Fx.Para(3));
        CompareDocument b = Fx.TableDoc([header, setup, upkeep, upkeep], Fx.Para(3));
        Alignment al = Fx.Align(a, b, Strict());

        for (int c = 0; c < Cols; c++)
        {
            AlignmentLink? l = Partner(al, Text(2, c));
            Assert.NotNull(l);
            Assert.Equal(Text(2, c), l.NewNodes[0]);
            Assert.Null(al.LinkOfNew(Text(3, c)));
        }

        Fx.Clean(a, b, Strict());
    }

    [Fact]
    public void ClauseWhoseFigureMovedUpOnePlaceIsEditsNotAMove()
    {
        // Three clauses that differ only in a figure; each new figure is the one the clause after it had.
        static string Cap(string amount) => $"The total liability under this Agreement is limited to {amount} for all claims taken together.";
        string between = Fx.Para(4, 14);
        CompareDocument a = Fx.Doc(Fx.Para(5), Cap("USD 40,000"), between, Cap("USD 60,000"), Fx.Para(6));
        CompareDocument b = Fx.Doc(Fx.Para(5), Cap("USD 60,000"), between, Cap("USD 9,000"), Fx.Para(6));
        Alignment al = Fx.Align(a, b);

        Assert.False(al.Links.Any(l => l.Kind == LinkKind.Move), Describe(al));
        Assert.Equal(Fx.P(1), Assert.IsType<AlignmentLink>(Partner(al, Fx.P(1))).NewNodes[0]);
        Assert.Equal(Fx.P(2), Assert.IsType<AlignmentLink>(Partner(al, Fx.P(2))).NewNodes[0]);
        Assert.Equal(Fx.P(3), Assert.IsType<AlignmentLink>(Partner(al, Fx.P(3))).NewNodes[0]);
        Fx.Clean(a, b);
    }

    [Fact]
    public void NearDuplicateNeighboursAreNotReadAsASplit()
    {
        static string Due(string date) => $"The Supplier shall deliver the Services on or before {date} and shall notify the Customer of any delay.";
        string other = "Neither party may assign this Agreement without the prior written consent of the other party.";
        CompareDocument a = Fx.Doc(Fx.Para(7), Due("5 May 2026") + " " + Due("11 February 2027") + " " + other, Due("17 February 2028") + " " + Due("17 April 2028"), Fx.Para(8));
        CompareDocument b = Fx.Doc(Fx.Para(7), Due("11 February 2027") + " " + other, Due("17 April 2028"), Fx.Para(8));
        Alignment al = Fx.Align(a, b);

        Assert.False(al.Links.Any(l => l.Kind is LinkKind.Split or LinkKind.Merge), Describe(al));
        Assert.Equal(Fx.P(1), Assert.IsType<AlignmentLink>(Partner(al, Fx.P(1))).NewNodes[0]);
        Assert.Equal(Fx.P(2), Assert.IsType<AlignmentLink>(Partner(al, Fx.P(2))).NewNodes[0]);
        Fx.Clean(a, b);
    }

    [Fact]
    public void OfTwoEqualParagraphsTheOneNearestItsPlaceStaysAndTheOtherMoves()
    {
        string x = "Each party shall keep the other party's confidential information secret for five years.";
        CompareDocument a = Fx.Doc(Fx.Para(9), Fx.Para(10), Fx.Para(11), x, x, Fx.Para(12));
        CompareDocument b = Fx.Doc(Fx.Para(9), x, Fx.Para(10), Fx.Para(11), x, Fx.Para(12));
        Alignment al = Fx.Align(a, b);

        // Old paragraphs 4 and 5 are the copies; new paragraph 5 is the one still in place.
        Assert.Equal(Fx.P(4), Assert.IsType<AlignmentLink>(Partner(al, Fx.P(4))).NewNodes[0]);
        AlignmentLink moved = Assert.IsType<AlignmentLink>(Partner(al, Fx.P(3)));
        Assert.Equal(Fx.P(1), moved.NewNodes[0]);
        Assert.Equal(LinkKind.Move, moved.Kind);
        Fx.Clean(a, b);
    }

    [Fact]
    public void InsertedParagraphEqualToAnExistingOneDoesNotStealItsMatch()
    {
        string x = "Notices must be given in writing and delivered to the address stated on the cover page.";
        CompareDocument a = Fx.Doc(Fx.Para(13), Fx.Para(14), x, Fx.Para(15));
        CompareDocument b = Fx.Doc(Fx.Para(13), x, Fx.Para(14), x, Fx.Para(15));
        Alignment al = Fx.Align(a, b);

        // The original stays between its own neighbours (old paragraph 2, new paragraph 3); the copy is an insertion, not a move.
        Assert.False(al.Links.Any(l => l.Kind == LinkKind.Move), Describe(al));
        Assert.Equal(Fx.P(3), Assert.IsType<AlignmentLink>(Partner(al, Fx.P(2))).NewNodes[0]);
        Assert.Null(al.LinkOfNew(Fx.P(1)));
        Fx.Clean(a, b);
    }

    private static string Describe(Alignment al) =>
        string.Join("; ", al.Links.Select(l => $"{l.Kind} [{string.Join(",", l.OldNodes)}]->[{string.Join(",", l.NewNodes)}]"));

    private static CompareSettings Strict() => CompareSettings.Strict with { ThrowOnFailure = false };
}
