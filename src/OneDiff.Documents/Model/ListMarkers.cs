namespace OneDiff.Documents.Model;

/// <summary>The one rule for what counts as a list marker at the start of a paragraph (<c>1.</c>, <c>(a)</c>, <c>iv.</c>, <c>4.2</c>, a bullet).</summary>
public static class ListMarkers
{
    /// <summary>
    /// Whether a word is a list marker: a bullet, a dash, a number or letter closed by a dot or parenthesis, a roman numeral so
    /// closed, or a dotted-decimal clause number such as <c>1.1</c> or <c>10.2.3.</c>. Every built-in source uses this one rule, so
    /// the same clause text gets the same label whether it came from a text file or a PDF.
    /// </summary>
    public static bool IsMarker(string word)
    {
        if (IsDottedDecimal(word))
        {
            return true;
        }

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

    // Two to four groups of one to three digits joined by dots, with an optional closing dot: the clause numbers of contracts.
    // A Word source reads these as list labels, so reading them the same way keeps the two models' clauses alike. Numbers that
    // only look like them are left alone: 7.500 (thousands grouped with dots, as in German amounts) and 0.5.
    /// <summary>Whether a word is a dotted-decimal clause number: two to four groups of one to three digits joined by dots, with an optional closing dot. 7.500 and 0.5 are not.</summary>
    public static bool IsDottedDecimal(string word)
    {
        int end = word.Length > 0 && word[^1] == '.' ? word.Length - 1 : word.Length;
        int groups = 0, digits = 0;
        bool onlyThousands = true;
        for (int i = 0; i < end; i++)
        {
            char c = word[i];
            if (char.IsAsciiDigit(c) && ++digits <= 3)
            {
                continue;
            }

            if (c != '.' || digits == 0 || i == end - 1)
            {
                return false;
            }

            if (groups == 0 && digits == 1 && word[0] == '0')
            {
                return false;
            }

            if (groups > 0 && digits != 3)
            {
                onlyThousands = false;
            }

            groups++;
            digits = 0;
        }

        if (groups > 0 && digits != 3)
        {
            onlyThousands = false;
        }

        return digits > 0 && groups is >= 1 and <= 3 && !onlyThousands;
    }
}
