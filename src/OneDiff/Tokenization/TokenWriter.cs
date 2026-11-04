using System.Buffers;

namespace OneDiff.Tokenization;

/// <summary>
/// Collects token ranges in a pooled buffer, so tokenizing allocates only the final exact-size array (or nothing, when the caller
/// copies into its own list). The initial size is a guess from the text length and kind; the buffer grows if the guess is short.
/// </summary>
internal struct TokenWriter : IDisposable
{
    private TokenRange[] buffer;
    private int count;

    public TokenWriter(int textLength, TokenKind kind)
    {
        // Lines average far more than 16 chars, words about 4-6, characters and graphemes one token per char (or fewer).
        long guess = kind switch
        {
            TokenKind.Line => textLength / 16 + 4,
            TokenKind.Word => textLength / 3 + 4,
            _ => textLength + 4,
        };
        buffer = ArrayPool<TokenRange>.Shared.Rent((int)Math.Min(guess, 1 << 26));
        count = 0;
    }

    public readonly ReadOnlySpan<TokenRange> Tokens => buffer.AsSpan(0, count);

    public void Add(int start, int length)
    {
        if (count == buffer.Length)
        {
            Grow();
        }

        buffer[count++] = new TokenRange(start, length);
    }

    private void Grow()
    {
        TokenRange[] bigger = ArrayPool<TokenRange>.Shared.Rent(buffer.Length * 2);
        buffer.AsSpan(0, count).CopyTo(bigger);
        ArrayPool<TokenRange>.Shared.Return(buffer);
        buffer = bigger;
    }

    public void Dispose()
    {
        if (buffer is not null)
        {
            ArrayPool<TokenRange>.Shared.Return(buffer);
            buffer = null!;
        }
    }
}
