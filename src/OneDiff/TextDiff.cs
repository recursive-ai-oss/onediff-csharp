using OneDiff.Algorithms;
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

    internal TextDiff(string oldText, string newText, TokenKind kind, TokenRange[]? oldTokens, TokenRange[]? newTokens, EditScript script, IEqualityComparer<string>? comparer = null, DiffDiagnostic diagnostic = default)
    {
        Comparer = comparer;
        Diagnostic = diagnostic;
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

    /// <summary>
    /// Whether the work cap ended the search early (the script is then valid but coarser than the algorithm would give) and how much work
    /// was spent. All zero for a diff you built yourself with <see cref="Create"/>.
    /// </summary>
    public DiffDiagnostic Diagnostic { get; }

    /// <summary>The old text a run covers (empty for an insertion). Tokens partition the text, so the span is contiguous.</summary>
    public ReadOnlySpan<char> OldSpan(Edit edit) => Slice(OldText, oldTokens, edit.OldStart, edit.OldLength);

    /// <summary>The new text a run covers (empty for a deletion).</summary>
    public ReadOnlySpan<char> NewSpan(Edit edit) => Slice(NewText, newTokens, edit.NewStart, edit.NewLength);

    private DiffRun[]? pieces;
    private DiffRun[]? lines;

    /// <summary>
    /// The diff as typed runs of text, in order: one piece per edit, with the line each run starts on in each text. Works for every
    /// token kind; for a line diff each piece is a block of whole lines, for a word diff a run of words.
    /// </summary>
    public IReadOnlyList<DiffRun> Pieces => pieces ??= BuildPieces(split: false);

    /// <summary>
    /// The diff cut into lines for reading: every piece of <see cref="Pieces"/> is split at line breaks (which are dropped from the
    /// text), so each entry lies on one line of one or both texts. For a line diff that is exactly one entry per line, like the
    /// lines of an inline diff view; for a word diff a changed line shows as several entries sharing a line number.
    /// </summary>
    public IReadOnlyList<DiffRun> Lines => lines ??= BuildPieces(split: true);

    private DiffRun[] BuildPieces(bool split)
    {
        var result = new List<DiffRun>(Script.Count);
        // A side's line is "open" when the text so far ended without a line break, so the next text continues it.
        int oldLine = 0, newLine = 0;
        bool oldOpen = false, newOpen = false;
        foreach (Edit e in Script.Edits)
        {
            ReadOnlySpan<char> text = e.Kind == EditKind.Insert ? NewSpan(e) : OldSpan(e);
            bool inOld = e.Kind != EditKind.Insert;
            bool inNew = e.Kind != EditKind.Delete;
            int firstOld = oldLine + (oldOpen ? 0 : 1);
            int firstNew = newLine + (newOpen ? 0 : 1);
            if (!split)
            {
                result.Add(new DiffRun(e.Kind, inOld ? firstOld : null, inNew ? firstNew : null, text.ToString()));
            }

            while (text.Length > 0)
            {
                int len = LineLength(text, out int content);
                bool ended = content < len;
                if (inOld && !oldOpen)
                {
                    oldLine++;
                }

                if (inNew && !newOpen)
                {
                    newLine++;
                }

                if (inOld)
                {
                    oldOpen = !ended;
                }

                if (inNew)
                {
                    newOpen = !ended;
                }

                if (split)
                {
                    result.Add(new DiffRun(e.Kind, inOld ? oldLine : null, inNew ? newLine : null, text.Slice(0, content).ToString()));
                }

                text = text.Slice(len);
            }
        }

        return result.ToArray();
    }

    // Length of the first line of text including its break, and the length without the break.
    private static int LineLength(ReadOnlySpan<char> text, out int content)
    {
        int i = text.IndexOfAny('\r', '\n');
        if (i < 0)
        {
            content = text.Length;
            return text.Length;
        }

        content = i;
        return text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? i + 2 : i + 1;
    }

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
