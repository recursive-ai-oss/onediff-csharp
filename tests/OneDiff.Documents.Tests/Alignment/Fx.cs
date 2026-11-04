using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Scoring;

namespace OneDiff.Documents.Tests.Alignments;

/// <summary>Small hand-built documents for alignment tests.</summary>
internal static class Fx
{
    private static readonly string[] Syllables = ["ka", "lo", "mi", "ren", "tu", "sa", "vel", "dor", "pi", "an", "or", "ex", "qua", "zim", "bel", "nu"];

    /// <summary>A pronounceable made-up word from a number, so unrelated paragraphs share almost nothing.</summary>
    public static string Word(int n)
    {
        var s = new System.Text.StringBuilder();
        n += 17;
        while (n > 0)
        {
            s.Append(Syllables[n % Syllables.Length]);
            n /= Syllables.Length;
        }

        return s.ToString();
    }

    /// <summary>A paragraph of <paramref name="count"/> distinct words starting at word number <paramref name="seed"/> * 100.</summary>
    public static string Para(int seed, int count = 24) => string.Join(' ', Enumerable.Range(0, count).Select(i => Word((seed * 100) + i)));

    public static CompareDocument Doc(params string[] paragraphs)
    {
        var b = new CompareDocumentBuilder();
        b.Begin(NodeKind.Section);
        foreach (string p in paragraphs)
        {
            b.Add(NodeKind.Paragraph, new NodeContent { Text = p });
        }

        b.End();
        return b.Build();
    }

    /// <summary>A section holding a table of rows of cells, each cell one paragraph.</summary>
    public static CompareDocument TableDoc(string[][] rows, params string[] after)
    {
        var b = new CompareDocumentBuilder();
        b.Begin(NodeKind.Section);
        b.Begin(NodeKind.Table);
        foreach (string[] row in rows)
        {
            b.Begin(NodeKind.TableRow);
            foreach (string cell in row)
            {
                b.Begin(NodeKind.TableCell);
                b.Add(NodeKind.Paragraph, new NodeContent { Text = cell });
                b.End();
            }

            b.End();
        }

        b.End();
        foreach (string p in after)
        {
            b.Add(NodeKind.Paragraph, new NodeContent { Text = p });
        }

        b.End();
        return b.Build();
    }

    public static Alignment Align(CompareDocument a, CompareDocument b, CompareSettings? settings = null, IPairScorer? scorer = null) =>
        Stages.Align(a, b, scorer, settings ?? CompareSettings.Legal);

    /// <summary>The link of an old node, or null.</summary>
    public static AlignmentLink? Of(Alignment al, int oldNode) => al.LinkOfOld(oldNode);

    /// <summary>Every document must verify cleanly with no fallback, whatever the aligner did.</summary>
    public static Comparison Clean(CompareDocument a, CompareDocument b, CompareSettings? settings = null) =>
        Helpers.Clean(Comparison.Of(a, b, settings));

    /// <summary>The old node of the n-th paragraph (documents here are one section of paragraphs).</summary>
    public static int P(int index) => index + 1;
}
