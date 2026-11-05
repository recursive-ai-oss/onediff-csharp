namespace OneDiff.Tokenization;

/// <summary>How white space is treated when two tokens are compared.</summary>
public enum WhiteSpaceHandling
{
    /// <summary>White space counts: tokens must match character for character.</summary>
    Significant = 0,

    /// <summary>Leading and trailing white space is ignored and every inner run of white space equals a single space (like <c>diff -b</c>).</summary>
    IgnoreChanges = 1,

    /// <summary>All white space is ignored (like <c>diff -w</c>): <c>"a b"</c> equals <c>"ab"</c>, and a token of only white space equals any other.</summary>
    IgnoreAll = 2,
}

/// <summary>
/// Decides which tokens count as the same when diffing text: the way to ignore case or white space. It is an ordinary
/// <see cref="IEqualityComparer{T}"/> of strings, so it also plugs into <see cref="Diff.Sequences{T}(ReadOnlySpan{T}, ReadOnlySpan{T}, DiffAlgorithm, IEqualityComparer{T}, CancellationToken)"/> and
/// <see cref="TokenInterner{T}"/>, and it can compare token text in place without allocating (the diff entry points use that).
/// Case is ignored with simple per-UTF-16-unit upper-casing under the invariant culture, so results never depend on the machine's locale.
/// Instances are immutable and thread safe.
/// </summary>
public sealed class TokenComparer : IEqualityComparer<string>, IAlternateEqualityComparer<ReadOnlySpan<char>, string>
{
    private readonly bool ignoreCase;
    private readonly WhiteSpaceHandling whiteSpace;

    /// <summary>Exact comparison, character for character.</summary>
    public static TokenComparer Ordinal { get; } = new(false, WhiteSpaceHandling.Significant);

    /// <summary>Ignores case.</summary>
    public static TokenComparer IgnoreCase { get; } = new(true, WhiteSpaceHandling.Significant);

    /// <summary>Ignores changes in the amount of white space (see <see cref="WhiteSpaceHandling.IgnoreChanges"/>).</summary>
    public static TokenComparer IgnoreWhiteSpaceChanges { get; } = new(false, WhiteSpaceHandling.IgnoreChanges);

    /// <summary>Ignores all white space (see <see cref="WhiteSpaceHandling.IgnoreAll"/>).</summary>
    public static TokenComparer IgnoreAllWhiteSpace { get; } = new(false, WhiteSpaceHandling.IgnoreAll);

    private TokenComparer(bool ignoreCase, WhiteSpaceHandling whiteSpace)
    {
        this.ignoreCase = ignoreCase;
        this.whiteSpace = whiteSpace;
    }

    /// <summary>A comparer with the given case and white space rules. Throws <see cref="ArgumentOutOfRangeException"/> for an undefined <paramref name="whiteSpace"/>.</summary>
    /// <param name="ignoreCase">True to treat letters that differ only in case as equal.</param>
    /// <param name="whiteSpace">How white space is treated.</param>
    public static TokenComparer Create(bool ignoreCase, WhiteSpaceHandling whiteSpace)
    {
        if (whiteSpace is < WhiteSpaceHandling.Significant or > WhiteSpaceHandling.IgnoreAll)
        {
            throw new ArgumentOutOfRangeException(nameof(whiteSpace));
        }

        return (ignoreCase, whiteSpace) switch
        {
            (false, WhiteSpaceHandling.Significant) => Ordinal,
            (true, WhiteSpaceHandling.Significant) => IgnoreCase,
            (false, WhiteSpaceHandling.IgnoreChanges) => IgnoreWhiteSpaceChanges,
            (false, WhiteSpaceHandling.IgnoreAll) => IgnoreAllWhiteSpace,
            _ => new TokenComparer(ignoreCase, whiteSpace),
        };
    }

    /// <summary>True when the two tokens are the same under this comparer's rules.</summary>
    public bool Equals(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        if (whiteSpace == WhiteSpaceHandling.Significant && !ignoreCase)
        {
            return x.SequenceEqual(y);
        }

        var a = new Normalizer(x, ignoreCase, whiteSpace);
        var b = new Normalizer(y, ignoreCase, whiteSpace);
        while (true)
        {
            bool hasA = a.TryNext(out char ca);
            bool hasB = b.TryNext(out char cb);
            if (hasA != hasB || (hasA && ca != cb))
            {
                return false;
            }

            if (!hasA)
            {
                return true;
            }
        }
    }

    /// <inheritdoc />
    public bool Equals(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        return x is not null && y is not null && Equals(x.AsSpan(), y.AsSpan());
    }

    /// <summary>A hash that agrees with <see cref="Equals(ReadOnlySpan{char}, ReadOnlySpan{char})"/>: tokens it calls equal hash alike. Stable across runs.</summary>
    public int GetHashCode(ReadOnlySpan<char> alternate)
    {
        // FNV-1a over the normalized characters; written out so the value never depends on a per-process seed.
        uint hash = 2166136261;
        var n = new Normalizer(alternate, ignoreCase, whiteSpace);
        while (n.TryNext(out char c))
        {
            hash = (hash ^ c) * 16777619;
        }

        return (int)hash;
    }

    /// <inheritdoc />
    public int GetHashCode(string obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return GetHashCode(obj.AsSpan());
    }

    /// <inheritdoc />
    public bool Equals(ReadOnlySpan<char> alternate, string other) => Equals(alternate, other.AsSpan());

    /// <inheritdoc />
    public string Create(ReadOnlySpan<char> alternate) => alternate.ToString();

    // Walks a token yielding the characters that count, in their comparison form.
    private ref struct Normalizer
    {
        private readonly ReadOnlySpan<char> text;
        private readonly bool ignoreCase;
        private readonly WhiteSpaceHandling whiteSpace;
        private int index;
        private bool started;

        public Normalizer(ReadOnlySpan<char> text, bool ignoreCase, WhiteSpaceHandling whiteSpace)
        {
            this.text = text;
            this.ignoreCase = ignoreCase;
            this.whiteSpace = whiteSpace;
        }

        public bool TryNext(out char c)
        {
            if (whiteSpace == WhiteSpaceHandling.Significant)
            {
                if (index >= text.Length)
                {
                    c = default;
                    return false;
                }

                c = Fold(text[index++]);
                return true;
            }

            if (whiteSpace == WhiteSpaceHandling.IgnoreAll)
            {
                SkipWhiteSpace();
                if (index >= text.Length)
                {
                    c = default;
                    return false;
                }

                c = Fold(text[index++]);
                return true;
            }

            if (!started)
            {
                started = true;
                SkipWhiteSpace();
            }

            if (index >= text.Length)
            {
                c = default;
                return false;
            }

            if (char.IsWhiteSpace(text[index]))
            {
                // IgnoreChanges: an inner run is one space; a trailing run (nothing after it) is dropped.
                SkipWhiteSpace();
                if (index >= text.Length)
                {
                    c = default;
                    return false;
                }

                c = ' ';
                return true;
            }

            c = Fold(text[index++]);
            return true;
        }

        private void SkipWhiteSpace()
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }
        }

        private readonly char Fold(char c) => ignoreCase ? char.ToUpperInvariant(c) : c;
    }
}
