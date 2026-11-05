using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Pdf.Internal;

/// <summary>
/// Folds the line breaks a PDF's layout put into running text. Lines of one paragraph are joined by LF in the raw text. A line
/// break becomes one space (or nothing between two characters of a script written without spaces); a hyphen that ends a line
/// inside a word, where the next line goes on in lower case, is dropped together with the break. Both folds keep the original
/// characters, so a setting that shows layout differences can put them back.
/// </summary>
internal sealed class LineBreakNormalizer : ITextNormalizer
{
    public static LineBreakNormalizer Instance { get; } = new();

    public bool TryFold(string raw, int index, out int length, out string replacement, out LedgerCategory category)
    {
        char c = raw[index];
        length = 1;
        replacement = "";
        category = LedgerCategory.LineBreaks;
        if (c == '\n')
        {
            bool unspaced = index > 0 && index + 1 < raw.Length && IsUnspacedScript(raw[index - 1]) && IsUnspacedScript(raw[index + 1]);
            replacement = unspaced ? "" : " ";
            return true;
        }

        if ((c is '-' or '\u2010' or '\u00AD') && index + 2 < raw.Length && raw[index + 1] == '\n')
        {
            // A hyphen at the end of a line, inside a word that goes on in lower case: the word was broken by layout.
            if (index > 0 && char.IsLetter(raw[index - 1]) && char.IsLower(raw[index + 2]))
            {
                length = 2;
                category = LedgerCategory.Hyphenation;
                return true;
            }
        }

        return false;
    }

    /// <summary>Scripts that are written without spaces between words: a line break between two such characters adds no space.</summary>
    internal static bool IsUnspacedScript(char c) =>
        (c >= '\u2E80' && c <= '\u9FFF') || (c >= '\uF900' && c <= '\uFAFF') || (c >= '\uFF00' && c <= '\uFFEF') || (c >= '\u0E00' && c <= '\u0E7F');
}

/// <summary>
/// Folds the page number in running page furniture (headers and footers) into <c>#</c>, so that "Page 3 of 10" in one
/// document and "Page 4 of 12" in another read alike and a document that gained a page does not change in every footer.
/// A number is a page number when it equals the page's number or label, or the number of pages.
/// </summary>
internal sealed class PageNumberNormalizer(IReadOnlyList<string> numbers) : ITextNormalizer
{
    public bool TryFold(string raw, int index, out int length, out string replacement, out LedgerCategory category)
    {
        replacement = "#";
        category = LedgerCategory.PageNumbers;
        length = 1;
        if (!char.IsAsciiDigit(raw[index]) || (index > 0 && char.IsAsciiDigit(raw[index - 1])))
        {
            return false;
        }

        int end = index;
        while (end < raw.Length && char.IsAsciiDigit(raw[end]))
        {
            end++;
        }

        length = end - index;
        ReadOnlySpan<char> digits = raw.AsSpan(index, length);
        foreach (string n in numbers)
        {
            if (digits.SequenceEqual(n))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Shared text predicates of the PDF source.</summary>
internal static class PdfText
{
    /// <summary>Whether text ends a sentence, so that the next block starts a new paragraph (closing quotes and brackets are looked through).</summary>
    public static bool EndsSentence(string text)
    {
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c is ')' or ']' or '"' or '\'' or '\u201D' or '\u2019' or '\u00BB' or '\u300D' or '\u300F' or '\uFF09')
            {
                continue;
            }

            return c is '.' or '!' or '?' or ':' or ';' or '\u2026' or '\u3002' or '\uFF01' or '\uFF1F' or '\u06D4' or '\u061F' or '\u0964';
        }

        return true;
    }

    /// <summary>Whether text goes on a sentence started elsewhere: its first letter is not an upper-case one.</summary>
    public static bool StartsLikeContinuation(string text)
    {
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                return !char.IsUpper(c);
            }

            if (char.IsDigit(c))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Whether a word is a list marker: a bullet, a dash, or a number or letter closed by a dot or parenthesis.</summary>
    public static bool IsListMarker(string word)
    {
        if (word.Length == 0 || word.Length > 6)
        {
            return false;
        }

        if (word.Length == 1)
        {
            return word[0] is '\u2022' or '\u25AA' or '\u25E6' or '\u25CF' or '\u25A0' or '\u2013' or '\u2014' or '-' or '*' or '\u25CB' or '\u2023' or '\u2043' or '\uF0B7';
        }

        char last = word[^1];
        if (last is not ('.' or ')'))
        {
            return false;
        }

        string core = word.Substring(0, word.Length - 1).TrimStart('(');
        if (core.Length == 0)
        {
            return false;
        }

        if (core.Length <= 3 && core.All(char.IsAsciiDigit))
        {
            return true;
        }

        if (core.Length == 1 && char.IsAsciiLetter(core[0]))
        {
            return true;
        }

        // Roman numerals, either case.
        return core.Length <= 4 && (core.All(static ch => "ivxlcdm".Contains(ch, StringComparison.Ordinal)) || core.All(static ch => "IVXLCDM".Contains(ch, StringComparison.Ordinal)));
    }

    public static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);

    public static string Number(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
