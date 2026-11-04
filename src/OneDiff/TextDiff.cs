using OneDiff.Tokenization;

namespace OneDiff;

/// <summary>
/// The outcome of comparing two strings token by token: the strings, their tokens and the <see cref="EditScript"/> over those
/// tokens. Edit indices count tokens; the methods here turn them back into text.
/// </summary>
public sealed class TextDiff
{
    // A null array means "every token is exactly one char" (character tokens over text without surrogates): the ranges are then
    // implied by the index and only built if somebody asks for them, which keeps the common character diff allocation-light.
    private TokenRange[]? oldTokens;
    private TokenRange[]? newTokens;

    internal TextDiff(string oldText, string newText, TokenKind kind, TokenRange[]? oldTokens, TokenRange[]? newTokens, EditScript script, IEqualityComparer<string>? comparer = null)
    {
        Comparer = comparer;
        OldText = oldText;
        NewText = newText;
        Kind = kind;
        this.oldTokens = oldTokens;
        this.newTokens = newTokens;
        Script = script;
    }

    /// <summary>
    /// Wraps a script computed over tokens you cut yourself (for example Unicode word segments), so the cleanup methods and the
    /// text accessors work on it. Tokens are compared exactly, as in the diffs this library computes.
    /// </summary>
    /// <param name="oldText">The old string.</param>
    /// <param name="newText">The new string.</param>
    /// <param name="kind">The kind of tokens the ranges are; cleanup uses it only to describe the diff.</param>
    /// <param name="oldTokens">The old tokens: contiguous, non-empty ranges that cover <paramref name="oldText"/> exactly.</param>
    /// <param name="newTokens">The new tokens, likewise for <paramref name="newText"/>.</param>
    /// <param name="script">A script whose indices count these tokens; the equal tokens must be equal text.</param>
    /// <exception cref="ArgumentNullException">A string or the script is null.</exception>
    /// <exception cref="ArgumentException">The ranges do not partition their text, or the script does not span the token counts.</exception>
    public static TextDiff Create(string oldText, string newText, TokenKind kind, ReadOnlySpan<TokenRange> oldTokens, ReadOnlySpan<TokenRange> newTokens, EditScript script)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentNullException.ThrowIfNull(script);
        CheckPartition(oldText, oldTokens, nameof(oldTokens));
        CheckPartition(newText, newTokens, nameof(newTokens));
        if (script.OldLength != oldTokens.Length || script.NewLength != newTokens.Length)
        {
            throw new ArgumentException("the script does not span the tokens", nameof(script));
        }

        return new TextDiff(oldText, newText, kind, oldTokens.ToArray(), newTokens.ToArray(), script);
    }

    private static void CheckPartition(string text, ReadOnlySpan<TokenRange> tokens, string name)
    {
        int pos = 0;
        foreach (TokenRange t in tokens)
        {
            if (t.Start != pos || t.Length <= 0)
            {
                throw new ArgumentException("tokens must be contiguous, non-empty ranges", name);
            }

            pos = t.End;
        }

        if (pos != text.Length)
        {
            throw new ArgumentException("tokens must cover the whole text", name);
        }
    }

    /// <summary>The comparer that decided which tokens are equal; null for exact (ordinal) comparison. Cleanup compares tokens the same way.</summary>
    internal IEqualityComparer<string>? Comparer { get; }

    /// <summary>The token ranges as stored; null when each token is one char at its own index.</summary>
    internal TokenRange[]? OldTokenRanges => oldTokens;

    internal TokenRange[]? NewTokenRanges => newTokens;

    /// <summary>The old string, as given.</summary>
    public string OldText { get; }

    /// <summary>The new string, as given.</summary>
    public string NewText { get; }

    /// <summary>How the strings were tokenized.</summary>
    public TokenKind Kind { get; }

    /// <summary>The tokens of the old string; the edit script's old indices index this.</summary>
    public ReadOnlySpan<TokenRange> OldTokens => oldTokens ??= OneCharTokens(OldText.Length);

    /// <summary>The tokens of the new string; the edit script's new indices index this.</summary>
    public ReadOnlySpan<TokenRange> NewTokens => newTokens ??= OneCharTokens(NewText.Length);

    /// <summary>The alignment of the two token sequences.</summary>
    public EditScript Script { get; }

    /// <summary>The old text a run covers (empty for an insertion). Tokens partition the text, so the span is contiguous.</summary>
    public ReadOnlySpan<char> OldSpan(Edit edit) => Slice(OldText, oldTokens, edit.OldStart, edit.OldLength);

    /// <summary>The new text a run covers (empty for a deletion).</summary>
    public ReadOnlySpan<char> NewSpan(Edit edit) => Slice(NewText, newTokens, edit.NewStart, edit.NewLength);

    private static TokenRange[] OneCharTokens(int length)
    {
        var ranges = new TokenRange[length];
        for (int i = 0; i < ranges.Length; i++)
        {
            ranges[i] = new TokenRange(i, 1);
        }

        return ranges;
    }

    private static ReadOnlySpan<char> Slice(string text, TokenRange[]? tokens, int start, int length)
    {
        if (length == 0)
        {
            return default;
        }

        if (tokens is null)
        {
            return text.AsSpan(start, length);
        }

        int from = tokens[start].Start;
        int to = tokens[start + length - 1].End;
        return text.AsSpan(from, to - from);
    }
}
