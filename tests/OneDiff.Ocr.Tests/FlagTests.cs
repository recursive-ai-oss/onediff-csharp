using System.Collections.Immutable;
using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Ocr.Tests;

/// <summary>The possible-OCR-error flag looks at the words the edits touch, not at the whole changed unit or the paragraph.</summary>
public class FlagTests
{
    /// <summary>A one-paragraph document whose words are anchored with the given confidences (the quadrilaterals are laid out on a line).</summary>
    private static CompareDocument Doc(string name, (string Word, double Confidence)[] words)
    {
        var b = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(name, name, "image/png");
        string text = string.Join(' ', words.Select(static w => w.Word));
        var segments = ImmutableArray.CreateBuilder<AnchoredSpan>();
        int at = 0;
        double x = 10;
        foreach ((string word, double confidence) in words)
        {
            segments.Add(new AnchoredSpan(at, word.Length, Anchor.ForOcrWords([new Quad(0, x, 700, x + (7 * word.Length), 700, x + (7 * word.Length), 712, x, 712)], confidence)));
            at += word.Length + 1;
            x += (7 * word.Length) + 4;
        }

        b.Begin(NodeKind.Section);
        b.Add(NodeKind.Paragraph, new NodeContent
        {
            Text = text,
            Anchor = Anchor.ForOcrWords([new Quad(0, 10, 700, x, 700, x, 712, 10, 712)], words.Min(static w => w.Confidence)),
            Segments = segments.ToImmutable(),
        });
        b.End();
        return b.Build();
    }

    [Fact]
    public void AWordNextToTheEditDoesNotFlagIt()
    {
        // "days" was read badly on the old side, but the change is thirty -> forty.
        CompareDocument o = Doc("o", [("Pay", 0.99), ("within", 0.99), ("thirty", 0.97), ("days", 0.30), ("now", 0.99)]);
        CompareDocument n = Doc("n", [("Pay", 0.99), ("within", 0.99), ("forty", 0.96), ("days", 0.98), ("now", 0.99)]);
        Comparison c = Comparison.Of(o, n, CompareSettings.Strict with { ThrowOnFailure = false });
        Change change = Assert.Single(c.Changes);
        Assert.Contains("thirty", change.Old.Text);
        Assert.False(change.PossibleOcrError);
        Assert.Equal(0.96, change.OcrConfidence);
    }

    [Fact]
    public void ALowConfidenceWordInTheEditFlagsItOnEitherSide()
    {
        CompareDocument o = Doc("o", [("Pay", 0.99), ("within", 0.99), ("thirty", 0.97), ("days", 0.98)]);
        CompareDocument n = Doc("n", [("Pay", 0.99), ("within", 0.99), ("forty", 0.41), ("days", 0.98)]);
        Change fromNew = Assert.Single(Comparison.Of(o, n, CompareSettings.Strict with { ThrowOnFailure = false }).Changes);
        Assert.True(fromNew.PossibleOcrError);
        Assert.Equal(0.41, fromNew.OcrConfidence);

        CompareDocument o2 = Doc("o2", [("Pay", 0.99), ("within", 0.99), ("thirty", 0.55), ("days", 0.98)]);
        CompareDocument n2 = Doc("n2", [("Pay", 0.99), ("within", 0.99), ("forty", 0.99), ("days", 0.98)]);
        Change fromOld = Assert.Single(Comparison.Of(o2, n2, CompareSettings.Strict with { ThrowOnFailure = false }).Changes);
        Assert.True(fromOld.PossibleOcrError);
        Assert.Equal(0.55, fromOld.OcrConfidence);

        // The change is still there with both sides: flagging never hides it.
        Assert.Equal("thirty", fromOld.Old.Text);
        Assert.Equal("forty", fromOld.New.Text);
    }

    [Fact]
    public void TheThresholdIsASetting()
    {
        CompareDocument o = Doc("o", [("Pay", 0.99), ("thirty", 0.97)]);
        CompareDocument n = Doc("n", [("Pay", 0.99), ("forty", 0.90)]);
        Assert.False(Assert.Single(Comparison.Of(o, n, CompareSettings.Strict with { ThrowOnFailure = false, OcrConfidenceThreshold = 0.5 }).Changes).PossibleOcrError);
        Assert.True(Assert.Single(Comparison.Of(o, n, CompareSettings.Strict with { ThrowOnFailure = false, OcrConfidenceThreshold = 0.95 }).Changes).PossibleOcrError);
        Assert.Throws<ArgumentException>(() => (CompareSettings.Legal with { OcrConfidenceThreshold = 0 }).Validate());
    }

    [Fact]
    public void DocumentsWithoutRecognitionAreNeverFlagged()
    {
        Comparison c = Comparison.Of(DocumentInput.FromString("Pay within thirty days."), DocumentInput.FromString("Pay within forty days."));
        Assert.All(c.Changes, static x =>
        {
            Assert.Null(x.OcrConfidence);
            Assert.False(x.PossibleOcrError);
        });
    }
}
