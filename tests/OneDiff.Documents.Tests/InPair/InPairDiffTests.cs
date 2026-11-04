using System.Collections.Immutable;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Tests.InPair;

/// <summary>The edits of a pair of blocks: word-level, cleaned up, refined for numbers, and formatting as attribute intervals.</summary>
public class InPairDiffTests
{
    private static ImmutableArray<RegionEdit> EditsOf(string a, string b, CompareSettings? settings = null)
    {
        Alignment al = Stages.Align(CompareDocument.FromText(a), CompareDocument.FromText(b), null, settings ?? CompareSettings.Strict);
        PairDiffs d = Stages.DiffPairs(al);
        return [.. d.Links.SelectMany(l => l.Edits)];
    }

    [Fact]
    public void AContractionIsOneWordNotThree()
    {
        ImmutableArray<RegionEdit> edits = EditsOf("The Buyer can't refuse delivery of the goods.", "The Buyer cannot refuse delivery of the goods.");
        RegionEdit e = Assert.Single(edits);
        Assert.Equal("can't", e.OldText);
        Assert.Equal("cannot", e.NewText);
    }

    [Fact]
    public void AReplacedWordStaysWholeButAReplacedNumberShowsTheDigits()
    {
        ImmutableArray<RegionEdit> words = EditsOf("The Parties agree to the terms of this deal.", "The Party agree to the terms of this deal.");
        Assert.Contains(words, e => e.OldText == "Parties" && e.NewText == "Party");
        // Only the digits that moved are edited, all inside the number.
        ImmutableArray<RegionEdit> digits = EditsOf("Pay 1,250,000 now please sir.", "Pay 1,520,000 now please sir.");
        Assert.All(digits, e => Assert.True(e.OldStart >= 4 && e.OldEnd <= 13 && e.OldLength <= 1 && e.NewLength <= 1));
    }

    [Fact]
    public void StrayCommonWordsDoNotShredARewrittenPhrase()
    {
        ImmutableArray<RegionEdit> edits = EditsOf(
            "The quick brown fox jumps over the lazy dog today.",
            "A fast red cat leaps over a sleepy hound today.");
        Assert.True(edits.Length <= 3, string.Join(" | ", edits.Select(e => $"'{e.OldText}'->'{e.NewText}'")));
    }

    [Fact]
    public void AnInsertionAmongRepeatedWordsIsOneWordWide()
    {
        RegionEdit e = Assert.Single(EditsOf("Pay the the fee now.", "Pay the the the fee now."));
        Assert.Equal(0, e.OldLength);
        Assert.Equal("the", e.NewText.Trim());
    }

    [Fact]
    public void EditsReplayForward()
    {
        string a = "Section 4.2 says the Buyer shall pay $5,000 within 30 days; the Seller shall ship within 10 days.";
        string b = "Section 4.3 says the Buyer must pay $6,000 within 45 days, and the Seller shall ship within 12 days.";
        ImmutableArray<RegionEdit> edits = EditsOf(a, b);
        var fwd = new System.Text.StringBuilder();
        int pos = 0;
        foreach (RegionEdit e in edits)
        {
            fwd.Append(a, pos, e.OldStart - pos).Append(e.NewText);
            pos = e.OldEnd;
        }

        fwd.Append(a, pos, a.Length - pos);
        Assert.Equal(b, fwd.ToString());
    }

    [Fact]
    public void FormattingIsDiffedAsIntervalsOverEqualText()
    {
        CompareDocument Make(params FormatRun[] runs)
        {
            var b = new CompareDocumentBuilder();
            b.Begin(NodeKind.Section);
            b.Add(NodeKind.Paragraph, new NodeContent { Text = "alpha beta gamma delta", Runs = [.. runs] });
            b.End();
            return b.Build();
        }

        Alignment al = Stages.Align(
            Make(new FormatRun(0, 5, FormatSet.Of(FormatKeys.Bold, "1"))),
            Make(new FormatRun(0, 5, FormatSet.Of(FormatKeys.Bold, "1", FormatKeys.Italic, "1")), new FormatRun(6, 4, FormatSet.Of(FormatKeys.Bold, "1"))),
            null,
            CompareSettings.Strict);
        ImmutableArray<RegionEdit> edits = [.. Stages.DiffPairs(al).Links.SelectMany(l => l.Edits)];
        Assert.Equal(2, edits.Length);
        Assert.All(edits, e => Assert.True(e.IsFormatOnly));
        Assert.Equal("alpha", edits[0].OldText);
        Assert.Equal("beta", edits[1].OldText);
    }
}
