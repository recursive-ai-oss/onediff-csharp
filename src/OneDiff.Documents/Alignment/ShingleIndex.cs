namespace OneDiff.Documents.Pipeline;

/// <summary>
/// An inverted index from shingles to the blocks that contain them: the candidate generator. Looking up the shingles of one
/// block and counting votes finds the blocks that share most of its words in order, without comparing every pair of blocks.
/// A shingle that occurs in more than <see cref="PostingCap"/> blocks is too common to say anything and is ignored, which keeps
/// the work per lookup bounded and the total near-linear.
/// </summary>
internal sealed class ShingleIndex
{
    /// <summary>The most blocks one shingle may point at before it is ignored.</summary>
    public const int PostingCap = 24;

    private readonly Dictionary<int, int> slotOf = [];
    private readonly int[] start;
    private readonly int[] stored;
    private readonly int[] total;
    private readonly int[] postings;

    /// <summary>Indexes the given blocks (ascending ids) of a document.</summary>
    public ShingleIndex(DocIndex doc, IReadOnlyList<int> blocks)
    {
        var counts = new List<int>();
        foreach (int b in blocks)
        {
            foreach (int s in doc.Shingles[b])
            {
                if (!slotOf.TryGetValue(s, out int slot))
                {
                    slot = counts.Count;
                    slotOf[s] = slot;
                    counts.Add(0);
                }

                counts[slot]++;
            }
        }

        total = [.. counts];
        start = new int[counts.Count + 1];
        stored = new int[counts.Count];
        for (int k = 0; k < counts.Count; k++)
        {
            start[k + 1] = start[k] + Math.Min(counts[k], PostingCap);
        }

        postings = new int[start[counts.Count]];
        foreach (int b in blocks)
        {
            foreach (int s in doc.Shingles[b])
            {
                int slot = slotOf[s];
                if (stored[slot] < PostingCap)
                {
                    postings[start[slot] + stored[slot]++] = b;
                }
            }
        }
    }

    /// <summary>
    /// Adds one vote to every indexed block per shingle it shares with <paramref name="shingles"/>. Returns the number of
    /// postings visited, the work done. <paramref name="touched"/> receives each block the first time it gets a vote.
    /// </summary>
    public long Vote(int[] shingles, int[] votes, List<int> touched)
    {
        long visited = 0;
        foreach (int s in shingles)
        {
            if (!slotOf.TryGetValue(s, out int slot) || total[slot] > PostingCap)
            {
                continue;
            }

            int from = start[slot], to = from + stored[slot];
            visited += to - from;
            for (int k = from; k < to; k++)
            {
                int b = postings[k];
                if (votes[b]++ == 0)
                {
                    touched.Add(b);
                }
            }
        }

        return visited;
    }
}
