using System.Collections.Immutable;
using System.Text;

namespace OneDiff.Documents.Model;

/// <summary>
/// One piece of normalization: a source text range that a normalizer replaced. The compare model holds the normalized text; the
/// folds say what was there before, so whatever a normalizer hid can be put in the ledger or, when a setting asks for it, shown.
/// </summary>
/// <param name="Category">The ledger category the difference belongs to.</param>
/// <param name="Start">Where the replacement starts in the normalized <see cref="Node.Text"/>.</param>
/// <param name="Length">The length of the replacement in the normalized text (0 when the original was dropped).</param>
/// <param name="Original">The source text the replacement stands for; never empty.</param>
public readonly record struct NormalizationFold(LedgerCategory Category, int Start, int Length, string Original);

/// <summary>
/// The seam for text normalizers. A normalizer looks at one position of the raw text and may replace the characters starting
/// there. It must not look backwards and must be a pure function of its arguments. Normalizers are tried in order; the first
/// that matches at a position wins.
/// </summary>
public interface ITextNormalizer
{
    /// <summary>Tries to fold the raw text starting at <paramref name="index"/>.</summary>
    /// <param name="raw">The whole raw text.</param>
    /// <param name="index">Where to look.</param>
    /// <param name="length">How many raw characters (at least 1) the fold consumes.</param>
    /// <param name="replacement">What they become (possibly empty).</param>
    /// <param name="category">What kind of difference this hides.</param>
    /// <returns>True when a fold applies.</returns>
    bool TryFold(string raw, int index, out int length, out string replacement, out LedgerCategory category);
}

/// <summary>The outcome of normalizing a raw text.</summary>
public sealed class NormalizedText
{
    internal NormalizedText(string text, ImmutableArray<NormalizationFold> folds, int[] rawToNormalized)
    {
        Text = text;
        Folds = folds;
        RawToNormalized = rawToNormalized;
    }

    /// <summary>The normalized text.</summary>
    public string Text { get; }

    /// <summary>The folds, in text order.</summary>
    public ImmutableArray<NormalizationFold> Folds { get; }

    internal int[] RawToNormalized { get; }

    /// <summary>Maps a position in the raw text (0 to its length) to the position in <see cref="Text"/>; positions inside a folded range map to the fold's start.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rawIndex"/> is outside the raw text.</exception>
    public int MapRawIndex(int rawIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rawIndex, RawToNormalized.Length);
        return RawToNormalized[rawIndex];
    }
}

/// <summary>Runs normalizers over raw text.</summary>
public static class TextNormalization
{
    /// <summary>Normalizes <paramref name="raw"/> with <paramref name="normalizers"/> (none: the text is unchanged).</summary>
    /// <exception cref="InvalidOperationException">A normalizer consumed no characters or more than remain.</exception>
    public static NormalizedText Normalize(string raw, IReadOnlyList<ITextNormalizer>? normalizers)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var map = new int[raw.Length + 1];
        if (normalizers is null || normalizers.Count == 0)
        {
            for (int k = 0; k <= raw.Length; k++)
            {
                map[k] = k;
            }

            return new NormalizedText(raw, [], map);
        }

        var sb = new StringBuilder(raw.Length);
        ImmutableArray<NormalizationFold>.Builder folds = ImmutableArray.CreateBuilder<NormalizationFold>();
        int i = 0;
        while (i < raw.Length)
        {
            bool matched = false;
            foreach (ITextNormalizer n in normalizers)
            {
                if (!n.TryFold(raw, i, out int len, out string replacement, out LedgerCategory category))
                {
                    continue;
                }

                if (len < 1 || len > raw.Length - i || replacement is null)
                {
                    throw new InvalidOperationException("a normalizer returned an invalid fold");
                }

                string original = raw.Substring(i, len);
                for (int k = 0; k < len; k++)
                {
                    map[i + k] = sb.Length;
                }

                if (!string.Equals(original, replacement, StringComparison.Ordinal))
                {
                    folds.Add(new NormalizationFold(category, sb.Length, replacement.Length, original));
                }

                sb.Append(replacement);
                i += len;
                matched = true;
                break;
            }

            if (!matched)
            {
                map[i] = sb.Length;
                sb.Append(raw[i]);
                i++;
            }
        }

        map[raw.Length] = sb.Length;
        return new NormalizedText(sb.ToString(), folds.ToImmutable(), map);
    }
}

/// <summary>
/// The reference typographic normalizer: curly quotes become straight quotes, the dash family becomes a hyphen-minus,
/// runs of horizontal whitespace (including non-breaking and exotic spaces) become one space, and CR LF and lone CR become LF.
/// A single plain space is left alone.
/// </summary>
public sealed class TypographicNormalizer : ITextNormalizer
{
    /// <summary>The shared instance; the normalizer has no state.</summary>
    public static TypographicNormalizer Instance { get; } = new();

    /// <inheritdoc />
    public bool TryFold(string raw, int index, out int length, out string replacement, out LedgerCategory category)
    {
        char c = raw[index];
        length = 1;
        switch (c)
        {
            case '\u2018' or '\u2019' or '\u201A' or '\u201B':
                replacement = "'";
                category = LedgerCategory.Quotes;
                return true;
            case '\u201C' or '\u201D' or '\u201E' or '\u201F':
                replacement = "\"";
                category = LedgerCategory.Quotes;
                return true;
            case (>= '\u2010' and <= '\u2015') or '\u2212':
                replacement = "-";
                category = LedgerCategory.Dashes;
                return true;
            case '\r':
                if (index + 1 < raw.Length && raw[index + 1] == '\n')
                {
                    length = 2;
                }

                replacement = "\n";
                category = LedgerCategory.Whitespace;
                return true;
        }

        if (IsHorizontalSpace(c))
        {
            int end = index + 1;
            while (end < raw.Length && IsHorizontalSpace(raw[end]))
            {
                end++;
            }

            length = end - index;
            replacement = " ";
            category = LedgerCategory.Whitespace;
            return !(length == 1 && c == ' ');
        }

        replacement = "";
        category = default;
        return false;
    }

    private static bool IsHorizontalSpace(char c) =>
        c is ' ' or '\t' or '\u00A0' or (>= '\u2000' and <= '\u200A') or '\u202F' or '\u205F' or '\u3000';
}

/// <summary>
/// Replaces the information-separator character U+001E, which the engine reserves for joining blocks, with U+FFFD.
/// Sources include it so that no input text can collide with the separator.
/// </summary>
public sealed class ControlCharacterNormalizer : ITextNormalizer
{
    /// <summary>The shared instance; the normalizer has no state.</summary>
    public static ControlCharacterNormalizer Instance { get; } = new();

    /// <inheritdoc />
    public bool TryFold(string raw, int index, out int length, out string replacement, out LedgerCategory category)
    {
        length = 1;
        replacement = "\uFFFD";
        category = LedgerCategory.Whitespace;
        return raw[index] == '\u001E';
    }
}
