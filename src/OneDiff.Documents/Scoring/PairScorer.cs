using OneDiff.Documents.Model;
using OneDiff.Tokenization;

namespace OneDiff.Documents.Scoring;

/// <summary>A candidate pairing of an old node with a new node.</summary>
/// <param name="Old">The old-document node.</param>
/// <param name="New">The new-document node.</param>
public readonly record struct NodePair(Node Old, Node New);

/// <summary>
/// The scorer seam: says how likely two nodes are one block edited, as a number in [0, 1]. Rules, a model and user code all
/// plug in here. A scorer is consulted only for candidates the exact stages could not settle; it can reorder and reweigh
/// pairings but never hides a difference, because every pairing is verified afterwards. Implementations must be pure and
/// thread-safe.
/// </summary>
public interface IPairScorer
{
    /// <summary>Scores one pair.</summary>
    /// <returns>A number in [0, 1]; 1 means the same block, 0 unrelated.</returns>
    double Score(Node oldNode, Node newNode);

    /// <summary>
    /// Scores many pairs at once so a model can batch them. The default scores them one by one.
    /// </summary>
    /// <param name="pairs">The candidates.</param>
    /// <param name="scores">Receives one score per pair; at least as long as <paramref name="pairs"/>.</param>
    void ScoreBatch(ReadOnlySpan<NodePair> pairs, Span<double> scores)
    {
        for (int i = 0; i < pairs.Length; i++)
        {
            scores[i] = Score(pairs[i].Old, pairs[i].New);
        }
    }
}

/// <summary>
/// The reference scorer: the Dice coefficient of the two nodes' word multisets (twice the shared words over the total
/// words). Two nodes with no words score 1, one with and one without words score 0.
/// </summary>
public sealed class WordOverlapScorer : IPairScorer
{
    /// <summary>The shared instance; the scorer has no state.</summary>
    public static WordOverlapScorer Instance { get; } = new();

    /// <inheritdoc />
    public double Score(Node oldNode, Node newNode)
    {
        ArgumentNullException.ThrowIfNull(oldNode);
        ArgumentNullException.ThrowIfNull(newNode);
        return WordBag.Of(oldNode.Text).Dice(WordBag.Of(newNode.Text));
    }
}

/// <summary>A multiset of the words of a text, for similarity.</summary>
internal sealed class WordBag
{
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);

    public int Total { get; private set; }

    public static WordBag Of(string text)
    {
        var bag = new WordBag();
        bag.Add(text);
        return bag;
    }

    public void Add(string text)
    {
        foreach (TokenRange t in Tokenizer.Tokenize(text, TokenKind.Word))
        {
            if (!char.IsLetterOrDigit(text[t.Start]))
            {
                continue;
            }

            string w = text.Substring(t.Start, t.Length);
            counts[w] = counts.TryGetValue(w, out int c) ? c + 1 : 1;
            Total++;
        }
    }

    public void Add(WordBag other)
    {
        foreach (KeyValuePair<string, int> kv in other.counts)
        {
            counts[kv.Key] = counts.TryGetValue(kv.Key, out int c) ? c + kv.Value : kv.Value;
        }

        Total += other.Total;
    }

    public WordBag Clone()
    {
        var b = new WordBag();
        b.Add(this);
        return b;
    }

    /// <summary>Dice coefficient; the work is proportional to the smaller bag.</summary>
    public double Dice(WordBag other)
    {
        if (Total == 0 && other.Total == 0)
        {
            return 1.0;
        }

        if (Total == 0 || other.Total == 0)
        {
            return 0.0;
        }

        WordBag small = counts.Count <= other.counts.Count ? this : other;
        WordBag large = ReferenceEquals(small, this) ? other : this;
        int common = 0;
        foreach (KeyValuePair<string, int> kv in small.counts)
        {
            if (large.counts.TryGetValue(kv.Key, out int c))
            {
                common += Math.Min(kv.Value, c);
            }
        }

        return 2.0 * common / (Total + other.Total);
    }
}
