using OneDiff.Tokenization;

namespace OneDiff;

/// <summary>
/// The outcome of comparing two strings token by token: the strings, their tokens and the <see cref="EditScript"/> over those
/// tokens. Edit indices count tokens; the methods here turn them back into text.
/// </summary>
public sealed class TextDiff
{
    private readonly TokenRange[] oldTokens;
    private readonly TokenRange[] newTokens;

    internal TextDiff(string oldText, string newText, TokenKind kind, TokenRange[] oldTokens, TokenRange[] newTokens, EditScript script, IEqualityComparer<string>? comparer = null)
    {
        Comparer = comparer;
        OldText = oldText;
        NewText = newText;
        Kind = kind;
        this.oldTokens = oldTokens;
        this.newTokens = newTokens;
        Script = script;
    }

    /// <summary>The comparer that decided which tokens are equal; null for exact (ordinal) comparison. Cleanup compares tokens the same way.</summary>
    internal IEqualityComparer<string>? Comparer { get; }

    internal TokenRange[] OldTokenArray => oldTokens;

    internal TokenRange[] NewTokenArray => newTokens;

    /// <summary>The old string, as given.</summary>
    public string OldText { get; }

    /// <summary>The new string, as given.</summary>
    public string NewText { get; }

    /// <summary>How the strings were tokenized.</summary>
    public TokenKind Kind { get; }

    /// <summary>The tokens of the old string; the edit script's old indices index this.</summary>
    public ReadOnlySpan<TokenRange> OldTokens => oldTokens;

    /// <summary>The tokens of the new string; the edit script's new indices index this.</summary>
    public ReadOnlySpan<TokenRange> NewTokens => newTokens;

    /// <summary>The alignment of the two token sequences.</summary>
    public EditScript Script { get; }

    /// <summary>The old text a run covers (empty for an insertion). Tokens partition the text, so the span is contiguous.</summary>
    public ReadOnlySpan<char> OldSpan(Edit edit) => Slice(OldText, oldTokens, edit.OldStart, edit.OldLength);

    /// <summary>The new text a run covers (empty for a deletion).</summary>
    public ReadOnlySpan<char> NewSpan(Edit edit) => Slice(NewText, newTokens, edit.NewStart, edit.NewLength);

    private static ReadOnlySpan<char> Slice(string text, TokenRange[] tokens, int start, int length)
    {
        if (length == 0)
        {
            return default;
        }

        int from = tokens[start].Start;
        int to = tokens[start + length - 1].End;
        return text.AsSpan(from, to - from);
    }
}
