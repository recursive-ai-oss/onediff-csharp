using OneDiff.Cleanup;
using OneDiff.Tokenization;

namespace OneDiff;

public static partial class Diff
{
    /// <summary>
    /// Compares two strings and cleans the result up for reading: <see cref="DiffCleanup.Semantic"/> over a diff of the given tokens.
    /// Words are the usual choice; with characters the cleanup is what turns a patchwork of one-letter matches into whole-word changes.
    /// </summary>
    /// <param name="oldText">The old string.</param>
    /// <param name="newText">The new string.</param>
    /// <param name="tokens">How to cut the strings into tokens. Words when omitted.</param>
    /// <param name="algorithm">Which algorithm to use. Myers when omitted.</param>
    /// <param name="cancellationToken">Observed while searching; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <exception cref="ArgumentNullException">A string is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokens"/> or <paramref name="algorithm"/> is not a defined value.</exception>
    public static TextDiff Semantic(
        string oldText,
        string newText,
        TokenKind tokens = TokenKind.Word,
        DiffAlgorithm algorithm = DiffAlgorithm.Myers,
        CancellationToken cancellationToken = default)
        => DiffCleanup.Semantic(Text(oldText, newText, tokens, algorithm, cancellationToken));

    /// <summary>
    /// Compares two strings and applies <see cref="DiffCleanup.Efficiency"/> to the result, for consumers that pay per edit.
    /// </summary>
    /// <param name="oldText">The old string.</param>
    /// <param name="newText">The new string.</param>
    /// <param name="editCost">The cost of one edit run in characters. 4 when omitted.</param>
    /// <param name="tokens">How to cut the strings into tokens. Characters when omitted.</param>
    /// <param name="algorithm">Which algorithm to use. Myers when omitted.</param>
    /// <param name="cancellationToken">Observed while searching; throws <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <exception cref="ArgumentNullException">A string is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="editCost"/> is negative, or <paramref name="tokens"/> or <paramref name="algorithm"/> is not a defined value.</exception>
    public static TextDiff Efficient(
        string oldText,
        string newText,
        int editCost = 4,
        TokenKind tokens = TokenKind.Character,
        DiffAlgorithm algorithm = DiffAlgorithm.Myers,
        CancellationToken cancellationToken = default)
        => DiffCleanup.Efficiency(Text(oldText, newText, tokens, algorithm, cancellationToken), editCost);

    /// <summary>
    /// Like <see cref="Semantic(string, string, TokenKind, DiffAlgorithm, CancellationToken)"/>, with the algorithm and work cap of
    /// <paramref name="settings"/> applied to the diff that is cleaned up. The cleanup passes themselves are not capped: they run once
    /// over the edits the (capped) diff produced.
    /// </summary>
    /// <param name="oldText">The old string.</param>
    /// <param name="newText">The new string.</param>
    /// <param name="tokens">How to cut the strings into tokens.</param>
    /// <param name="settings">Algorithm and work cap.</param>
    /// <param name="cancellationToken">Observed while searching.</param>
    /// <exception cref="ArgumentNullException">A string or the settings are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A token kind or algorithm is not a defined value, or <see cref="DiffSettings.MaxWork"/> is negative.</exception>
    public static TextDiff Semantic(string oldText, string newText, TokenKind tokens, DiffSettings settings, CancellationToken cancellationToken = default)
        => DiffCleanup.Semantic(Text(oldText, newText, tokens, settings, cancellationToken));

    /// <summary>
    /// Like <see cref="Efficient(string, string, int, TokenKind, DiffAlgorithm, CancellationToken)"/>, with the algorithm and work cap of
    /// <paramref name="settings"/> applied to the diff that is cleaned up.
    /// </summary>
    /// <param name="oldText">The old string.</param>
    /// <param name="newText">The new string.</param>
    /// <param name="editCost">The cost of one edit run in characters.</param>
    /// <param name="tokens">How to cut the strings into tokens.</param>
    /// <param name="settings">Algorithm and work cap.</param>
    /// <param name="cancellationToken">Observed while searching.</param>
    /// <exception cref="ArgumentNullException">A string or the settings are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="editCost"/> is negative, a token kind or algorithm is not a defined value, or <see cref="DiffSettings.MaxWork"/> is negative.</exception>
    public static TextDiff Efficient(string oldText, string newText, int editCost, TokenKind tokens, DiffSettings settings, CancellationToken cancellationToken = default)
        => DiffCleanup.Efficiency(Text(oldText, newText, tokens, settings, cancellationToken), editCost);
}
