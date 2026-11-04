using OneDiff.Tokenization;
using OneFont;

namespace OneDiff.Documents.InPair;

/// <summary>
/// A text cut at UAX #29 word boundaries (section 4): letters and digits with the marks that join them stay together, white space
/// and other symbols stand alone. Segments partition the text, so a script over segment indices maps back to exact character ranges.
/// </summary>
internal sealed class TokenSeq
{
    private TokenSeq(string text, int[] starts, int[] ids)
    {
        Text = text;
        Starts = starts;
        Ids = ids;
        var ranges = new TokenRange[ids.Length];
        for (int i = 0; i < ranges.Length; i++)
        {
            ranges[i] = new TokenRange(starts[i], starts[i + 1] - starts[i]);
        }

        Ranges = ranges;
    }

    public string Text { get; }

    /// <summary>Segment boundaries: <c>Count + 1</c> entries, the first 0 and the last the text length.</summary>
    public int[] Starts { get; }

    /// <summary>Equal segments have equal ids across sequences built with the same <see cref="Interner"/>.</summary>
    public int[] Ids { get; }

    public TokenRange[] Ranges { get; }

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
    public static TokenSeq Words(string text, Interner interner)
    {
        int[] starts = TextSegmentation.GetWordBoundaries(text);
        var ids = new int[starts.Length - 1];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = interner.Of(text.AsSpan(starts[i], starts[i + 1] - starts[i]));
        }

        return new TokenSeq(text, starts, ids);
    }
}
