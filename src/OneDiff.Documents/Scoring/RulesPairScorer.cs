using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Tokenization;

namespace OneDiff.Documents.Scoring;

/// <summary>
/// The default scorer of the alignment: rules over what two blocks say and how they are built. The score is the text
/// similarity (the Dice coefficient of the two word multisets; for texts of only a few words also of their character pairs)
/// nudged by structure: the same paragraph style or list level raises it slightly, a different one lowers it. Position and
/// neighbours are not visible to a scorer that sees two nodes; the alignment adds them when it weighs candidates, so a custom
/// scorer replaces only this judgement of the pair itself. Pure and thread-safe.
/// </summary>
public sealed class RulesPairScorer : IPairScorer
{
    private const double SameStructure = 0.02;
    private const double DifferentStructure = -0.05;

    /// <summary>The shared instance; the scorer has no state.</summary>
    public static RulesPairScorer Instance { get; } = new();

    /// <summary>Scores one pair: text similarity plus the structure adjustment, clamped to [0, 1].</summary>
    /// <param name="oldNode">The old-document node.</param>
    /// <param name="newNode">The new-document node.</param>
    /// <returns>A number in [0, 1].</returns>
    /// <exception cref="ArgumentNullException">A node is null.</exception>
    public double Score(Node oldNode, Node newNode)
    {
        ArgumentNullException.ThrowIfNull(oldNode);
        ArgumentNullException.ThrowIfNull(newNode);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] a = WordIds(oldNode.Text, ids), b = WordIds(newNode.Text, ids);
        double text = TextSimilarity.Of(a, oldNode.Text, b, newNode.Text);
        return Math.Clamp(text + Adjustment(oldNode, newNode), 0.0, 1.0);
    }

    /// <summary>The structure adjustment: small, so structure breaks near-ties and never outweighs the words.</summary>
    internal static double Adjustment(Node a, Node b)
    {
        double adjustment = 0;
        foreach (string key in StructureKeys)
        {
            string? x = a.Attributes.Get(key), y = b.Attributes.Get(key);
            if (x is null || y is null)
            {
                continue;
            }

            adjustment += string.Equals(x, y, StringComparison.Ordinal) ? SameStructure : DifferentStructure;
        }

        return adjustment;
    }

    private static readonly string[] StructureKeys = [FormatKeys.ParagraphStyle, FormatKeys.ListLevel];

    private static int[] WordIds(string text, Dictionary<string, int> ids)
    {
        var ranges = new List<TokenRange>();
        Tokenizer.Tokenize(text, TokenKind.Word, ranges);
        var list = new List<int>(ranges.Count);
        foreach (TokenRange t in ranges)
        {
            if (!char.IsLetterOrDigit(text[t.Start]))
            {
                continue;
            }

            string w = text.Substring(t.Start, t.Length);
            if (!ids.TryGetValue(w, out int id))
            {
                id = ids.Count;
                ids[w] = id;
            }

            list.Add(id);
        }

        int[] result = [.. list];
        Array.Sort(result);
        return result;
    }
}
