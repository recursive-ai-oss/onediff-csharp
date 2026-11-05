using OneDiff.Algorithms;
using OneDiff.Tokenization;

namespace OneDiff;

public static partial class Diff
{
    /// <summary>
    /// Compares two strings token by token, with <paramref name="comparer"/> deciding which tokens are equal. This is how to ignore
    /// case or white space: pass <see cref="TokenComparer.IgnoreCase"/>, <see cref="TokenComparer.IgnoreWhiteSpaceChanges"/>,
    /// <see cref="TokenComparer.IgnoreAllWhiteSpace"/> or <see cref="TokenComparer.Create(bool, WhiteSpaceHandling)"/>. The edit script still indexes the
    /// original tokens, and <see cref="TextDiff.OldSpan"/> and <see cref="TextDiff.NewSpan"/> return the original text.
    /// </summary>
    /// <param name="oldText">The old string.</param>
    /// <param name="newText">The new string.</param>
    /// <param name="comparer">Decides which tokens are equal. Any string comparer works; a <see cref="TokenComparer"/> avoids allocating a string per token.</param>
    /// <param name="tokens">How to cut the strings into tokens. Words when omitted.</param>
    /// <param name="algorithm">Which algorithm to use. Myers when omitted.</param>
    /// <param name="cancellationToken">Observed while searching; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <exception cref="ArgumentNullException">A string or the comparer is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokens"/> or <paramref name="algorithm"/> is not a defined value.</exception>
    public static TextDiff TextUsing(
        string oldText,
        string newText,
        IEqualityComparer<string> comparer,
        TokenKind tokens = TokenKind.Word,
        DiffAlgorithm algorithm = DiffAlgorithm.Myers,
        CancellationToken cancellationToken = default)
    {
        return TextUsingCapped(oldText, newText, comparer, tokens, algorithm, Budget.DefaultCap, cancellationToken);
    }

    /// <summary>
    /// Like <see cref="TextUsing(string, string, IEqualityComparer{string}, TokenKind, DiffAlgorithm, CancellationToken)"/>, with the algorithm
    /// and the work cap taken from <paramref name="settings"/>. This is how to diff untrusted text while ignoring case or white space:
    /// when the cap is reached the script is still valid, only coarser, and <see cref="TextDiff.Diagnostic"/> says so.
    /// </summary>
    /// <param name="oldText">The old string.</param>
    /// <param name="newText">The new string.</param>
    /// <param name="comparer">Decides which tokens are equal.</param>
    /// <param name="tokens">How to cut the strings into tokens.</param>
    /// <param name="settings">Algorithm and work cap.</param>
    /// <param name="cancellationToken">Observed while searching; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <exception cref="ArgumentNullException">A string, the comparer or the settings are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A token kind or algorithm is not a defined value, or <see cref="DiffSettings.MaxWork"/> is negative.</exception>
    public static TextDiff TextUsing(string oldText, string newText, IEqualityComparer<string> comparer, TokenKind tokens, DiffSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Budget.CheckCap(settings.MaxWork);
        return TextUsingCapped(oldText, newText, comparer, tokens, settings.Algorithm, settings.MaxWork, cancellationToken);
    }

    /// <summary>Compares two strings line by line under <paramref name="comparer"/> and <paramref name="settings"/> (algorithm and work cap).</summary>
    public static TextDiff LinesUsing(string oldText, string newText, IEqualityComparer<string> comparer, DiffSettings settings, CancellationToken cancellationToken = default)
        => TextUsing(oldText, newText, comparer, TokenKind.Line, settings, cancellationToken);

    /// <summary>Compares two strings word by word under <paramref name="comparer"/> and <paramref name="settings"/> (algorithm and work cap).</summary>
    public static TextDiff WordsUsing(string oldText, string newText, IEqualityComparer<string> comparer, DiffSettings settings, CancellationToken cancellationToken = default)
        => TextUsing(oldText, newText, comparer, TokenKind.Word, settings, cancellationToken);

    private static TextDiff TextUsingCapped(string oldText, string newText, IEqualityComparer<string> comparer, TokenKind tokens, DiffAlgorithm algorithm, long maxWork, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentNullException.ThrowIfNull(comparer);

        TokenRange[] oldRanges = Tokenizer.Tokenize(oldText, tokens);
        TokenRange[] newRanges = Tokenizer.Tokenize(newText, tokens);

        var ids = new Dictionary<string, int>(comparer);
        bool spans = ids.TryGetAlternateLookup<ReadOnlySpan<char>>(out Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup);
        int[] oldIds = InternWith(oldText, oldRanges, ids, spans, lookup);
        int[] newIds = InternWith(newText, newRanges, ids, spans, lookup);

        EditScript script = Run(oldIds, newIds, algorithm, maxWork, out DiffDiagnostic diagnostic, cancellationToken);
        return new TextDiff(oldText, newText, tokens, oldRanges, newRanges, script, comparer, diagnostic);
    }

    /// <summary>Compares two strings line by line under <paramref name="comparer"/>. Shorthand for <see cref="TextUsing(string, string, IEqualityComparer{string}, TokenKind, DiffAlgorithm, CancellationToken)"/> with lines.</summary>
    public static TextDiff LinesUsing(string oldText, string newText, IEqualityComparer<string> comparer, DiffAlgorithm algorithm = DiffAlgorithm.Myers, CancellationToken cancellationToken = default)
        => TextUsing(oldText, newText, comparer, TokenKind.Line, algorithm, cancellationToken);

    /// <summary>Compares two strings word by word under <paramref name="comparer"/>. Shorthand for <see cref="TextUsing(string, string, IEqualityComparer{string}, TokenKind, DiffAlgorithm, CancellationToken)"/> with words.</summary>
    public static TextDiff WordsUsing(string oldText, string newText, IEqualityComparer<string> comparer, DiffAlgorithm algorithm = DiffAlgorithm.Myers, CancellationToken cancellationToken = default)
        => TextUsing(oldText, newText, comparer, TokenKind.Word, algorithm, cancellationToken);

    private static int[] InternWith(
        string text,
        TokenRange[] ranges,
        Dictionary<string, int> ids,
        bool spans,
        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup)
    {
        int[] result = new int[ranges.Length];
        for (int i = 0; i < ranges.Length; i++)
        {
            ReadOnlySpan<char> token = text.AsSpan(ranges[i].Start, ranges[i].Length);
            int id;
            if (spans)
            {
                if (!lookup.TryGetValue(token, out id))
                {
                    id = ids.Count;
                    ids.Add(token.ToString(), id);
                }
            }
            else
            {
                string s = token.ToString();
                if (!ids.TryGetValue(s, out id))
                {
                    id = ids.Count;
                    ids.Add(s, id);
                }
            }

            result[i] = id;
        }

        return result;
    }
}
