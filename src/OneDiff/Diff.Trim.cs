using OneDiff.Tokenization;

namespace OneDiff;

public static partial class Diff
{
    // The text diff proper. The algorithms already take a common prefix and suffix of the id sequences first (and so give the same
    // script with or without it); doing it here too, on the tokens, means the shared part is never given ids or put in a dictionary,
    // and for characters it is found on the raw text before any token exists.
    private static TextDiff TextTrimmed(string oldText, string newText, TokenKind tokens, DiffAlgorithm algorithm, CancellationToken cancellationToken)
    {
        if (tokens == TokenKind.Character
            && !oldText.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF')
            && !newText.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF'))
        {
            // Without surrogates every char is a token and its own id, so the ranges are implied (see TextDiff) and trimming is
            // plain string work: vectorized prefix, blockwise suffix.
            ReadOnlySpan<char> a = oldText;
            ReadOnlySpan<char> b = newText;
            int prefix = a.CommonPrefixLength(b);
            int suffix = CommonSuffixLength(a.Slice(prefix), b.Slice(prefix));
            int[] oldCodes = Widen(a.Slice(prefix, a.Length - prefix - suffix));
            int[] newCodes = Widen(b.Slice(prefix, b.Length - prefix - suffix));
            EditScript middle = Run(oldCodes, newCodes, algorithm, cancellationToken);
            return new TextDiff(oldText, newText, tokens, null, null, Frame(middle, prefix, suffix, oldText.Length, newText.Length));
        }

        TokenRange[] oldRanges = Tokenizer.Tokenize(oldText, tokens);
        TokenRange[] newRanges = Tokenizer.Tokenize(newText, tokens);

        // Common leading and trailing tokens, compared in place. Token boundaries are the tokenizer's own, so nothing is ever split.
        int head = 0;
        int most = Math.Min(oldRanges.Length, newRanges.Length);
        while (head < most && SameToken(oldText, oldRanges[head], newText, newRanges[head]))
        {
            head++;
        }

        int tail = 0;
        while (tail < most - head && SameToken(oldText, oldRanges[oldRanges.Length - 1 - tail], newText, newRanges[newRanges.Length - 1 - tail]))
        {
            tail++;
        }

        int oldCount = oldRanges.Length - head - tail;
        int newCount = newRanges.Length - head - tail;
        int[] oldIds = new int[oldCount];
        int[] newIds = new int[newCount];
        if (tokens == TokenKind.Character)
        {
            // A character token is its own id (its code point): no dictionary needed, and equal text gets equal ids.
            CodePoints(oldText, oldRanges, head, oldIds);
            CodePoints(newText, newRanges, head, newIds);
        }
        else
        {
            // Ordinal token text -> id; the alternate lookup finds an existing token without allocating a string for it.
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var lookup = ids.GetAlternateLookup<ReadOnlySpan<char>>();
            InternTokens(oldText, oldRanges, head, oldIds, ids, lookup);
            InternTokens(newText, newRanges, head, newIds, ids, lookup);
        }

        EditScript script = Frame(Run(oldIds, newIds, algorithm, cancellationToken), head, tail, oldRanges.Length, newRanges.Length);
        return new TextDiff(oldText, newText, tokens, oldRanges, newRanges, script);
    }

    private static bool SameToken(string oldText, TokenRange o, string newText, TokenRange n)
        => o.Length == n.Length && oldText.AsSpan(o.Start, o.Length).SequenceEqual(newText.AsSpan(n.Start, n.Length));

    private static int CommonSuffixLength(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int max = Math.Min(a.Length, b.Length);
        int n = 0;
        // Blocks first, so a long shared tail costs a few vector compares rather than one test per char.
        while (n + 32 <= max && a.Slice(a.Length - n - 32, 32).SequenceEqual(b.Slice(b.Length - n - 32, 32)))
        {
            n += 32;
        }

        while (n < max && a[a.Length - 1 - n] == b[b.Length - 1 - n])
        {
            n++;
        }

        return n;
    }

    private static int[] Widen(ReadOnlySpan<char> chars)
    {
        int[] result = new int[chars.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = chars[i];
        }

        return result;
    }

    // Wraps the script of the middle part with the shared head and tail (in tokens) so it covers the whole sequences.
    private static EditScript Frame(EditScript middle, int head, int tail, int oldLength, int newLength)
    {
        if (head == 0 && tail == 0)
        {
            return middle;
        }

        var builder = new EditScriptBuilder();
        builder.Equal(head);
        foreach (Edit e in middle.Edits)
        {
            switch (e.Kind)
            {
                case EditKind.Equal: builder.Equal(e.OldLength); break;
                case EditKind.Delete: builder.Delete(e.OldLength); break;
                default: builder.Insert(e.NewLength); break;
            }
        }

        builder.Equal(tail);
        EditScript script = builder.ToEditScript();
        return script.OldLength == oldLength && script.NewLength == newLength
            ? script
            : throw new InvalidOperationException("the framed script does not cover the sequences");
    }

    // Code points of the tokens from `first` on, as ids.
    private static void CodePoints(string text, TokenRange[] ranges, int first, int[] destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            TokenRange r = ranges[first + i];
            destination[i] = r.Length == 2 ? char.ConvertToUtf32(text[r.Start], text[r.Start + 1]) : text[r.Start];
        }
    }

    private static void InternTokens(
        string text,
        TokenRange[] ranges,
        int first,
        int[] destination,
        Dictionary<string, int> ids,
        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            TokenRange r = ranges[first + i];
            ReadOnlySpan<char> token = text.AsSpan(r.Start, r.Length);
            if (!lookup.TryGetValue(token, out int id))
            {
                id = ids.Count;
                ids.Add(token.ToString(), id);
            }

            destination[i] = id;
        }
    }
}
