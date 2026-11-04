using System.Text.RegularExpressions;

namespace OneDiff.Documents.Grouping;

/// <summary>
/// Finds the stretches of a text that read as one value: a date, an amount of money, a percentage, a duration or a plain number.
/// The patterns are simple and deterministic (English month names, ISO and slash dates, common currency marks and codes, the usual
/// duration units, a few East Asian date and duration forms); resolving what a value means is a later stage's job. Grouping uses
/// the spans so that "30 days" becoming "60 days" is one change that shows the whole duration, not the digit alone.
/// </summary>
internal static partial class ValueSpans
{
    /// <summary>How far around an edit the search looks; longer than any value the patterns accept.</summary>
    private const int Margin = 96;

    // Alternatives are tried in order at each position, longest forms first: month-name dates, numeric dates, East Asian dates,
    // amounts, percentages, durations, then bare numbers.
    [GeneratedRegex(
        @"(?:\b(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\.?\s\d{1,2}(?:st|nd|rd|th)?(?:,?\s\d{4})?\b)"
        + @"|(?:\b\d{1,2}(?:st|nd|rd|th)?\s(?:of\s)?(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\.?(?:,?\s\d{4})?\b)"
        + @"|(?:\b\d{4}-\d{2}-\d{2}\b)|(?:\b\d{1,2}[/.]\d{1,2}[/.]\d{2,4}\b)"
        + @"|(?:\d{1,4}年(?:\d{1,2}月(?:\d{1,2}日)?)?)"
        + @"|(?:(?:US\$|[$€£¥]|\b(?:USD|EUR|GBP|CAD|AUD|JPY|CHF)\s?)\d+(?:[,.]\d+)*(?:\s?(?:trillion|billion|million|thousand|[kKmMbB]\b))?)"
        + @"|(?:\d+(?:[,.]\d+)*\s?(?:dollars|euros|pounds|USD|EUR|GBP)\b)"
        + @"|(?:\d+(?:[,.]\d+)*\s?(?:%|％|percent\b|per\scent\b))"
        + @"|(?:\d+(?:[,.]\d+)*[\s-]?(?:(?:business|calendar|working)\s)?(?:seconds?|minutes?|hours?|days?|weeks?|months?|years?)\b)"
        + @"|(?:[〇零一二三四五六七八九十百千万两]+(?:日間|个月|個月|日|天|週|周|月|年))"
        + @"|(?:\d+\s?(?:日間|ヶ月|か月|ヵ月|个月|個月|개월|日|天|週|周|月|年|일|년))"
        + @"|(?:\d+(?:[,.]\d+)*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Value();

    /// <summary>
    /// The value spans that overlap [<paramref name="from"/>, <paramref name="to"/>) of <paramref name="text"/>, as (start, end)
    /// pairs. An empty range overlaps a span that contains its position strictly inside. Only the neighbourhood of the range is
    /// searched, so the cost does not depend on the size of the text.
    /// </summary>
    public static List<(int Start, int End)> Overlapping(string text, int from, int to)
    {
        var result = new List<(int, int)>();
        int w0 = Math.Max(0, from - Margin), w1 = Math.Min(text.Length, to + Margin);
        if (w1 <= w0)
        {
            return result;
        }

        ReadOnlySpan<char> window = text.AsSpan(w0, w1 - w0);
        foreach (ValueMatch m in Value().EnumerateMatches(window))
        {
            int s = w0 + m.Index, e = s + m.Length;
            // A span cut by the edge of the window may be longer than it looks; the margin makes this rare, and such a span is dropped.
            if ((w0 > 0 && s == w0) || (w1 < text.Length && e == w1))
            {
                continue;
            }

            bool overlaps = from == to ? s < from && from < e : s < to && e > from;
            if (overlaps)
            {
                result.Add((s, e));
            }
        }

        return result;
    }
}
