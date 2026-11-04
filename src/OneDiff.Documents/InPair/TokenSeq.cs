using OneFont;

namespace OneDiff.Documents.InPair;

/// <summary>
/// A text cut into segments: UAX #29 word segments (section 4) or grapheme clusters (section 3), with the running counts the
/// cleanup needs. Segments partition the text, so a script over segment indices maps back to exact character ranges.
/// </summary>
internal sealed class TokenSeq
{
    private TokenSeq(string text, int[] starts, int[] ids, int[] nonSpaceBefore, int[] wordBefore)
    {
        Text = text;
        Starts = starts;
        Ids = ids;
        NonSpaceBefore = nonSpaceBefore;
        WordBefore = wordBefore;
    }

    public string Text { get; }

    /// <summary>Segment boundaries: <c>Count + 1</c> entries, the first 0 and the last the text length.</summary>
    public int[] Starts { get; }

    /// <summary>Equal segments have equal ids across the two sequences built with the same <see cref="Interner"/>.</summary>
    public int[] Ids { get; }

    /// <summary>Entry <c>i</c> is the number of non-white-space characters in segments before <c>i</c>.</summary>
    public int[] NonSpaceBefore { get; }

    /// <summary>Entry <c>i</c> is the number of segments before <c>i</c> that hold a letter or digit.</summary>
    public int[] WordBefore { get; }

    public int Count => Ids.Length;

    /// <summary>The ids of segment strings; shared by the old and the new sequence so equal segments get equal ids.</summary>
    public sealed class Interner
    {
        private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);

        public int Of(ReadOnlySpan<char> segment)
        {
            Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup = ids.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(segment, out int id))
            {
                return id;
            }

            id = ids.Count;
            ids[segment.ToString()] = id;
            return id;
        }
    }

    /// <summary>Cuts <paramref name="text"/> at UAX #29 word boundaries.</summary>
    public static TokenSeq Words(string text, Interner interner) => Build(text, TextSegmentation.GetWordBoundaries(text), interner);

    /// <summary>Cuts <paramref name="text"/> at grapheme cluster boundaries, so a combining mark never leaves its base.</summary>
    public static TokenSeq Graphemes(string text, Interner interner) => Build(text, TextSegmentation.GetGraphemeBoundaries(text), interner);

    private static TokenSeq Build(string text, int[] starts, Interner interner)
    {
        int n = starts.Length - 1;
        var ids = new int[n];
        var nonSpace = new int[n + 1];
        var words = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<char> seg = text.AsSpan(starts[i], starts[i + 1] - starts[i]);
            ids[i] = interner.Of(seg);
            int ns = 0;
            bool word = false;
            foreach (char c in seg)
            {
                if (!char.IsWhiteSpace(c))
                {
                    ns++;
                }

                if (char.IsLetterOrDigit(c))
                {
                    word = true;
                }
            }

            nonSpace[i + 1] = nonSpace[i] + ns;
            words[i + 1] = words[i] + (word ? 1 : 0);
        }

        return new TokenSeq(text, starts, ids, nonSpace, words);
    }

    /// <summary>Whether segment <paramref name="i"/> is white space (its first character decides; word segments never mix).</summary>
    public bool IsSpace(int i) => char.IsWhiteSpace(Text[Starts[i]]);
}
