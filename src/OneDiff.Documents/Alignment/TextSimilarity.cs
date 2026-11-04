namespace OneDiff.Documents.Pipeline;

/// <summary>
/// Text similarity used by the alignment: the Dice coefficient of two word multisets, with a character-pair Dice for texts of
/// only a few words (where one changed letter would otherwise score zero). Pure and allocation-light.
/// </summary>
internal static class TextSimilarity
{
    /// <summary>Texts with at most this many words also get the character-pair measure.</summary>
    private const int ShortWords = 3;

    /// <summary>Character pairs are taken from at most this many leading characters, so the cost stays bounded.</summary>
    private const int MaxChars = 256;

    /// <summary>Dice coefficient of two sorted multisets: twice the shared items over the total items; 0 when either is empty.</summary>
    public static double Dice(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0.0;
        }

        int i = 0, j = 0, common = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                common++;
                i++;
                j++;
            }
            else if (a[i] < b[j])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return 2.0 * common / (a.Length + b.Length);
    }

    /// <summary>The number of items two sorted multisets share (each counted as often as it occurs in both).</summary>
    public static int Common(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    {
        int i = 0, j = 0, common = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                common++;
                i++;
                j++;
            }
            else if (a[i] < b[j])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return common;
    }

    /// <summary>The same as <see cref="Dice(ReadOnlySpan{int}, ReadOnlySpan{int})"/> with the second multiset in a list.</summary>
    public static double Dice(ReadOnlySpan<int> a, List<int> b)
    {
        if (a.Length == 0 || b.Count == 0)
        {
            return 0.0;
        }

        int i = 0, j = 0, common = 0;
        while (i < a.Length && j < b.Count)
        {
            if (a[i] == b[j])
            {
                common++;
                i++;
                j++;
            }
            else if (a[i] < b[j])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return 2.0 * common / (a.Length + b.Count);
    }

    /// <summary>Dice coefficient of the overlapping character pairs of two strings.</summary>
    public static double CharDice(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1.0;
        }

        if (a.Length < 2 || b.Length < 2)
        {
            return string.Equals(a, b, StringComparison.Ordinal) ? 1.0 : 0.0;
        }

        int[] x = Pairs(a), y = Pairs(b);
        return Dice(x, y);
    }

    /// <summary>
    /// The similarity of two blocks from their sorted word ids and their text: the word Dice; for very short texts the larger of
    /// that and the character-pair Dice; for texts without words the character-pair Dice alone.
    /// </summary>
    public static double Of(ReadOnlySpan<int> wordsA, string textA, ReadOnlySpan<int> wordsB, string textB)
    {
        if (wordsA.Length == 0 && wordsB.Length == 0)
        {
            return CharDice(textA, textB);
        }

        double d = Dice(wordsA, wordsB);
        if (d < 1.0 && (wordsA.Length <= ShortWords || wordsB.Length <= ShortWords) && Math.Max(wordsA.Length, wordsB.Length) <= 2 * ShortWords)
        {
            d = Math.Max(d, CharDice(textA, textB));
        }

        return d;
    }

    private static int[] Pairs(string s)
    {
        int n = Math.Min(s.Length, MaxChars) - 1;
        var pairs = new int[n];
        for (int i = 0; i < n; i++)
        {
            pairs[i] = (s[i] << 16) | s[i + 1];
        }

        Array.Sort(pairs);
        return pairs;
    }
}
