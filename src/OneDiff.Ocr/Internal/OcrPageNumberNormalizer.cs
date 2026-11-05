using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Ocr.Internal;

/// <summary>
/// Folds the page numbers in running page furniture to <c>#</c>. A scan reads "Page 2 / 3" as "Page 2/.3" or "Page 2/ 3" depending
/// on the page, so every run of digits joined by the characters a page number is written with (spaces, slashes, dots, dashes)
/// folds as a whole; "Page 2 of 3" keeps its word between the numbers and folds as two.
/// </summary>
internal sealed class OcrPageNumberNormalizer : ITextNormalizer
{
    public static OcrPageNumberNormalizer Instance { get; } = new();

    public bool TryFold(string raw, int index, out int length, out string replacement, out LedgerCategory category)
    {
        replacement = "#";
        category = LedgerCategory.PageNumbers;
        length = 1;
        if (!char.IsAsciiDigit(raw[index]) || (index > 0 && char.IsAsciiDigit(raw[index - 1])))
        {
            return false;
        }

        int end = index + 1;
        for (int i = index + 1; i < raw.Length; i++)
        {
            char c = raw[i];
            if (char.IsAsciiDigit(c))
            {
                end = i + 1;
            }
            else if (c is not (' ' or '/' or '.' or '-' or '\u2013' or ',' or ':'))
            {
                break;
            }
        }

        length = end - index;
        return true;
    }
}
