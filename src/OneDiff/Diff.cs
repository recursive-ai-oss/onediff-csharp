using OneDiff.Algorithms;
using OneDiff.Tokenization;

namespace OneDiff;

/// <summary>
/// The one-line entry points to the text core. Each is a thin wrapper: tokenize (for text), intern items to ids, run the chosen
/// algorithm. Anything finer-grained lives on the types they use.
/// </summary>
public static partial class Diff
{
    /// <summary>
    /// Compares two arbitrary sequences. Items are interned through <paramref name="comparer"/> (the default comparer when null)
    /// and the algorithm runs over the ids. The returned script's indices are positions in the given spans.
    /// </summary>
    /// <param name="oldItems">The old sequence.</param>
    /// <param name="newItems">The new sequence.</param>
    /// <param name="algorithm">Which algorithm to use. Myers when omitted.</param>
    /// <param name="comparer">Decides which items are equal. The default comparer when null.</param>
    /// <param name="cancellationToken">Observed while searching; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="algorithm"/> is not a defined value.</exception>
    public static EditScript Sequences<T>(
        ReadOnlySpan<T> oldItems,
        ReadOnlySpan<T> newItems,
        DiffAlgorithm algorithm = DiffAlgorithm.Myers,
        IEqualityComparer<T>? comparer = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        var interner = new TokenInterner<T>(comparer);
        int[] oldIds = interner.Intern(oldItems);
        int[] newIds = interner.Intern(newItems);
        return Run(oldIds, newIds, algorithm, cancellationToken);
    }

    /// <summary>
    /// Compares two strings token by token. Tokens are compared ordinally (case and Unicode normalization form matter).
    /// </summary>
    /// <param name="oldText">The old string.</param>
    /// <param name="newText">The new string.</param>
    /// <param name="tokens">How to cut the strings into tokens. Words when omitted.</param>
    /// <param name="algorithm">Which algorithm to use. Myers when omitted.</param>
    /// <param name="cancellationToken">Observed while searching; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <exception cref="ArgumentNullException">A string is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokens"/> or <paramref name="algorithm"/> is not a defined value.</exception>
    public static TextDiff Text(
        string oldText,
        string newText,
        TokenKind tokens = TokenKind.Word,
        DiffAlgorithm algorithm = DiffAlgorithm.Myers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        return TextTrimmed(oldText, newText, tokens, algorithm, cancellationToken);
    }

    /// <summary>Compares two strings line by line. Shorthand for <see cref="Text"/> with <see cref="TokenKind.Line"/>.</summary>
    public static TextDiff Lines(string oldText, string newText, DiffAlgorithm algorithm = DiffAlgorithm.Myers, CancellationToken cancellationToken = default)
        => Text(oldText, newText, TokenKind.Line, algorithm, cancellationToken);

    /// <summary>Compares two strings word by word. Shorthand for <see cref="Text"/> with <see cref="TokenKind.Word"/>.</summary>
    public static TextDiff Words(string oldText, string newText, DiffAlgorithm algorithm = DiffAlgorithm.Myers, CancellationToken cancellationToken = default)
        => Text(oldText, newText, TokenKind.Word, algorithm, cancellationToken);

    /// <summary>Compares two strings character by character. Shorthand for <see cref="Text"/> with <see cref="TokenKind.Character"/>.</summary>
    public static TextDiff Characters(string oldText, string newText, DiffAlgorithm algorithm = DiffAlgorithm.Myers, CancellationToken cancellationToken = default)
        => Text(oldText, newText, TokenKind.Character, algorithm, cancellationToken);

    /// <summary>Compares two strings grapheme cluster by grapheme cluster. Shorthand for <see cref="Text"/> with <see cref="TokenKind.Grapheme"/>.</summary>
    public static TextDiff Graphemes(string oldText, string newText, DiffAlgorithm algorithm = DiffAlgorithm.Myers, CancellationToken cancellationToken = default)
        => Text(oldText, newText, TokenKind.Grapheme, algorithm, cancellationToken);

    private static EditScript Run(int[] oldIds, int[] newIds, DiffAlgorithm algorithm, CancellationToken cancellationToken) => algorithm switch
    {
        DiffAlgorithm.Myers => MyersDiff.Compute(oldIds, newIds, cancellationToken),
        DiffAlgorithm.Patience => PatienceDiff.Compute(oldIds, newIds, cancellationToken),
        DiffAlgorithm.Histogram => HistogramDiff.Compute(oldIds, newIds, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };
}
