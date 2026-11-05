using OneDiff.Documents.Html;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;

namespace OneDiff.Documents.Tests;

/// <summary>
/// Writers and stages walk a document's containers recursively, so the builder limits nesting. These run in the browser suite too,
/// where the call stack is small.
/// </summary>
public class DeepNestingTests
{
    private static CompareDocument Nested(int levels, string text)
    {
        var b = new CompareDocumentBuilder(DocumentKind.Flowing);
        for (int i = 0; i < levels; i++)
        {
            b.Begin(NodeKind.Section, new NodeContent { Anchor = Anchor.ForNode("s" + i) });
        }

        NormalizedText norm = TextNormalization.Normalize(text, [TypographicNormalizer.Instance]);
        b.Add(NodeKind.Paragraph, new NodeContent { Text = norm.Text, Anchor = Anchor.ForNode("p") });
        for (int i = 0; i < levels; i++)
        {
            b.End();
        }

        return b.Build();
    }

    [Fact]
    public void TheBuilderRefusesToNestPastItsLimit()
    {
        var b = new CompareDocumentBuilder(DocumentKind.Flowing);
        for (int i = 0; i < CompareDocumentBuilder.MaxDepth; i++)
        {
            b.Begin(NodeKind.Section, new NodeContent { Anchor = Anchor.ForNode("s" + i) });
        }

        Assert.Throws<InvalidOperationException>(() => b.Begin(NodeKind.Section, new NodeContent { Anchor = Anchor.ForNode("too-deep") }));
    }

    [Fact]
    public void ADocumentAtTheLimitComparesAndWritesEveryForm()
    {
        CompareDocument a = Nested(CompareDocumentBuilder.MaxDepth, "Payment is due within thirty days.");
        CompareDocument b = Nested(CompareDocumentBuilder.MaxDepth, "Payment is due within sixty days.");
        Comparison c = Helpers.Compare(a, b);
        Assert.NotEmpty(c.Changes);
        Assert.Contains("sixty", HtmlRedline.SideBySide(c));
        Assert.Contains("sixty", HtmlRedline.Inline(c));
        Assert.NotEmpty(ComparisonJson.Write(c));
    }
}
