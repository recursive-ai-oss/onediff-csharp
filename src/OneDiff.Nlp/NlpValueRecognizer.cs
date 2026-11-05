using System.Collections.Immutable;
using System.Globalization;
using RecursiveAI.OneNlp;
using RecursiveAI.OneNlp.Recognizers;
using OneDiff.Documents;

namespace OneDiff.Nlp;

/// <summary>
/// A value recognizer that reads English through OneNLP's recognizers (written-out numbers such as <c>forty-two</c> and
/// <c>two hundred thousand dollars</c>, <c>six percent</c>, <c>thirty days</c>, <c>1 year and 6 months</c>, amounts and dates in
/// many spellings) and returns the same canonical values as <see cref="BuiltInValueRecognizer"/> (<c>USD 25000</c>, <c>2028-05-16</c>,
/// <c>30 d</c>, <c>12.5%</c>, <c>42</c>). Everything OneNLP does not cover comes from the built-in recognizer: other languages,
/// party names, dates with a missing year, a numeric date that could be read two ways, business days.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the two are combined.</b> The built-in values are the base. A OneNLP value is added where it overlaps no built-in value, and
/// it replaces the built-in values it overlaps when it covers all of them and more (<c>forty-two thousand</c> against a bare
/// <c>thousand</c>). Where both read the same text, the built-in value stays, except that for a text declared English
/// (the language tag is <c>en</c> or <c>en-*</c>) OneNLP's value is taken, unless the built-in one is a
/// date it chose not to resolve. A text declared as another language is read by the built-in recognizer alone. The comparison engine
/// passes no language, in which case the text is treated as possibly English: OneNLP only fills gaps, so the built-in reading of
/// other languages' number and date conventions (<c>18.000 EUR</c> is 18000 euros) is never overridden.
/// </para>
/// <para>
/// <b>What is mapped.</b> Money to <see cref="ChangeType.Amount"/>, percentages to <see cref="ChangeType.Percentage"/>, plain numbers
/// to <see cref="ChangeType.Number"/>, durations to <see cref="ChangeType.Duration"/> and dates of a day or a calendar month to
/// <see cref="ChangeType.Date"/>. Ordinals, measures (<c>5 km</c>), times of day, recurrences, bare years and the other periods are
/// not values of this library and are left out. Relative expressions (<c>next Friday</c>) need a reference date, which a text compared
/// by this library does not have, so they are not reported. A written number followed by the same number in brackets and a unit
/// (<c>thirty (30) days</c>, <c>sixty (60) business days</c>) is one duration.
/// </para>
/// <para>Instances are immutable and thread-safe. Texts longer than 4096 characters are not values and give no result.</para>
/// </remarks>
public sealed class NlpValueRecognizer : IValueRecognizer
{
    private const int MaxLength = 4096;

    private readonly IValueRecognizer fallback;

    /// <summary>The recognizer over OneNLP's English rules with <see cref="BuiltInValueRecognizer"/> as the fallback.</summary>
    public static NlpValueRecognizer Instance { get; } = new(BuiltInValueRecognizer.Instance);

    /// <summary>Creates a recognizer that falls back to <paramref name="fallback"/> for everything OneNLP does not cover.</summary>
    /// <param name="fallback">The recognizer for other languages and the kinds OneNLP does not know. Usually <see cref="BuiltInValueRecognizer.Instance"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fallback"/> is null.</exception>
    public NlpValueRecognizer(IValueRecognizer fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        this.fallback = fallback;
    }

    /// <inheritdoc />
    public ImmutableArray<RecognizedValue> Recognize(string text, string? language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ImmutableArray<RecognizedValue> baseline = fallback.Recognize(text, language);
        if (text.Length == 0 || text.Length > MaxLength)
        {
            return baseline;
        }

        bool declaredEnglish = false;
        if (language is { Length: > 0 })
        {
            if (!IsEnglish(language))
            {
                return baseline;
            }

            declaredEnglish = true;
        }

        List<RecognizedValue> mapped = Map(text);
        if (mapped.Count == 0)
        {
            return baseline;
        }

        return Merge(baseline, mapped, declaredEnglish);
    }

    private static bool IsEnglish(string tag) =>
        tag.Equals("en", StringComparison.OrdinalIgnoreCase)
        || (tag.Length > 2 && tag.StartsWith("en", StringComparison.OrdinalIgnoreCase) && (tag[2] == '-' || tag[2] == '_'));

    // The built-in values are the base; see the remarks on the class.
    private static ImmutableArray<RecognizedValue> Merge(ImmutableArray<RecognizedValue> baseline, List<RecognizedValue> mapped, bool declaredEnglish)
    {
        var result = new List<RecognizedValue>(baseline.Length + mapped.Count);
        var dropped = new bool[baseline.Length];
        var added = new List<RecognizedValue>();
        foreach (RecognizedValue m in mapped)
        {
            int mEnd = m.Start + m.Length;
            bool overlaps = false, coversAll = true, same = false, unresolvedBase = false;
            for (int i = 0; i < baseline.Length; i++)
            {
                RecognizedValue b = baseline[i];
                int bEnd = b.Start + b.Length;
                if (b.Start >= mEnd || bEnd <= m.Start)
                {
                    continue;
                }

                overlaps = true;
                coversAll &= m.Start <= b.Start && mEnd >= bEnd;
                if (b.Start == m.Start && bEnd == mEnd)
                {
                    same = true;
                    unresolvedBase |= b.Value is null;
                }
            }

            if (!overlaps)
            {
                added.Add(m);
            }
            else if (coversAll && !same)
            {
                MarkOverlapped(baseline, m, dropped);
                added.Add(m);
            }
            else if (same && declaredEnglish && !unresolvedBase)
            {
                MarkOverlapped(baseline, m, dropped);
                added.Add(m);
            }
        }

        for (int i = 0; i < baseline.Length; i++)
        {
            if (!dropped[i])
            {
                result.Add(baseline[i]);
            }
        }

        result.AddRange(added);
        result.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return [.. result];
    }

    private static void MarkOverlapped(ImmutableArray<RecognizedValue> baseline, RecognizedValue m, bool[] dropped)
    {
        int mEnd = m.Start + m.Length;
        for (int i = 0; i < baseline.Length; i++)
        {
            if (baseline[i].Start < mEnd && baseline[i].Start + baseline[i].Length > m.Start)
            {
                dropped[i] = true;
            }
        }
    }

    /// <summary>OneNLP's English results for the text, in this library's values; the results do not overlap.</summary>
    private static List<RecognizedValue> Map(string text)
    {
        IReadOnlyList<Recognition> found = Recognizer.English.Recognize(text, default);
        var values = new List<RecognizedValue>(found.Count);
        for (int i = 0; i < found.Count; i++)
        {
            Recognition r = found[i];
            RecognizedValue? v = null;
            switch (r)
            {
                case Quantity { Kind: QuantityKind.Cardinal } q:
                    if (TryBracketedDuration(text, found, i, q, out RecognizedValue d, out int consumed))
                    {
                        v = d;
                        i += consumed;
                    }
                    else if (TryBusinessDuration(text, q, out RecognizedValue bd))
                    {
                        v = bd;
                    }
                    else
                    {
                        v = new RecognizedValue(q.Start, q.Length, ChangeType.Number, Number(q.Value));
                    }

                    break;
                case Quantity { Kind: QuantityKind.Percent } q:
                    v = new RecognizedValue(q.Start, q.Length, ChangeType.Percentage, new ResolvedValue(Format(q.Value) + "%", q.Value, "%"));
                    break;
                case Quantity { Kind: QuantityKind.Money, Currency: { Length: > 0 } code } q:
                    // OneNLP converts cents to the currency (50 cents is 0.5); the canonical form is the currency's number.
                    v = new RecognizedValue(q.Start, q.Length, ChangeType.Amount, new ResolvedValue(code + " " + Format(q.Value), q.Value, code));
                    break;
                case TemporalMatch { Kind: TemporalKind.Duration } t:
                    v = Duration(text, t);
                    break;
                case TemporalMatch { Kind: TemporalKind.Date or TemporalKind.DateRange } t:
                    v = Date(t);
                    break;
            }

            if (v is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static ResolvedValue Number(decimal n) => new(Format(n), n);

    // Same normal form as the built-in recognizer: no trailing zeros, invariant digits.
    private static string Format(decimal value) => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    private static RecognizedValue? Date(TemporalMatch t)
    {
        // A day: YYYY-MM-DD with no time and no zone. A calendar month stated as a month: the range is [first, first of next).
        string v = t.Value;
        if (t.Kind == TemporalKind.Date && t.Granularity == Granularity.Day && IsIsoDay(v))
        {
            return new RecognizedValue(t.Start, t.Length, ChangeType.Date, new ResolvedValue(v));
        }

        if (t.Kind == TemporalKind.DateRange && t.Granularity == Granularity.Month && t.Range is { } r
            && r.Start.Day == 1 && r.End.Day == 1 && r.Start.AddMonths(1) == r.End)
        {
            return new RecognizedValue(t.Start, t.Length, ChangeType.Date,
                new ResolvedValue(r.Start.Year.ToString("0000", CultureInfo.InvariantCulture) + "-" + r.Start.Month.ToString("00", CultureInfo.InvariantCulture)));
        }

        return null;
    }

    private static bool IsIsoDay(string v) =>
        v.Length == 10 && v[4] == '-' && v[7] == '-'
        && char.IsAsciiDigit(v[0]) && char.IsAsciiDigit(v[1]) && char.IsAsciiDigit(v[2]) && char.IsAsciiDigit(v[3])
        && char.IsAsciiDigit(v[5]) && char.IsAsciiDigit(v[6]) && char.IsAsciiDigit(v[8]) && char.IsAsciiDigit(v[9]);

    // A duration is written with the unit the text used: "30 days" is 30 d although OneNLP states it in seconds, "2 weeks" is 2 w.
    private static RecognizedValue? Duration(string text, TemporalMatch t)
    {
        string v = t.Value;
        if (v.StartsWith('P') && t.Amount is null)
        {
            // ISO 8601 with years and months: P18M, P2Y, P1Y6M. Only months and years, as the library has no calendar units beyond them.
            int y = 0, m = 0, i = 1;
            while (i < v.Length && char.IsAsciiDigit(v[i]))
            {
                int j = i;
                while (j < v.Length && char.IsAsciiDigit(v[j]))
                {
                    j++;
                }

                if (j >= v.Length || !int.TryParse(v.AsSpan(i, j - i), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                {
                    return null;
                }

                switch (v[j])
                {
                    case 'Y': y = n; break;
                    case 'M': m = n; break;
                    default: return null;
                }

                i = j + 1;
            }

            if (i != v.Length || (y == 0 && m == 0))
            {
                return null;
            }

            return m == 0
                ? new RecognizedValue(t.Start, t.Length, ChangeType.Duration, new ResolvedValue(y + " y", y, "y"))
                : new RecognizedValue(t.Start, t.Length, ChangeType.Duration, new ResolvedValue((y * 12 + m) + " mo", y * 12 + m, "mo"));
        }

        if (t.Amount is not { } span || span <= TimeSpan.Zero)
        {
            return null;
        }

        string? unit = null;
        decimal unitSeconds = 0;
        foreach (string word in Words(text.Substring(t.Start, t.Length)))
        {
            if (UnitOf(word) is { Seconds: > 0 } u && (unit is null || u.Seconds < unitSeconds))
            {
                (unit, unitSeconds) = (u.Code, u.Seconds);
            }
        }

        if (unit is null)
        {
            return null;
        }

        decimal count = (decimal)span.Ticks / TimeSpan.TicksPerSecond / unitSeconds;
        return new RecognizedValue(t.Start, t.Length, ChangeType.Duration, new ResolvedValue(Format(count) + " " + unit, count, unit));
    }

    // "thirty (30) days": a written number, the same number in brackets, then a unit; or a number and "business days".
    private static bool TryBracketedDuration(string text, IReadOnlyList<Recognition> found, int index, Quantity first, out RecognizedValue result, out int consumed)
    {
        result = default;
        consumed = 0;
        int pos = first.End;
        decimal count = first.Value;
        if (index + 1 < found.Count && found[index + 1] is Quantity { Kind: QuantityKind.Cardinal } second && second.Value == first.Value)
        {
            // Between the two only " (" and the closing bracket after the second.
            ReadOnlySpan<char> gap = text.AsSpan(first.End, second.Start - first.End);
            if (!gap.SequenceEqual(" (") && !gap.SequenceEqual("(") && !gap.SequenceEqual(" ["))
            {
                return false;
            }

            if (second.End >= text.Length || (text[second.End] != ')' && text[second.End] != ']'))
            {
                return false;
            }

            pos = second.End + 1;
            consumed = 1;
        }
        else
        {
            return false;
        }

        if (UnitAfter(text, pos, out string code, out int unitEnd))
        {
            result = new RecognizedValue(first.Start, unitEnd - first.Start, ChangeType.Duration, new ResolvedValue(Format(count) + " " + code, count, code));
            return true;
        }

        consumed = 0;
        return false;
    }

    private static bool TryBusinessDuration(string text, Quantity q, out RecognizedValue result)
    {
        result = default;
        if (UnitAfter(text, q.End, out string code, out int unitEnd) && code == "bd")
        {
            result = new RecognizedValue(q.Start, unitEnd - q.Start, ChangeType.Duration, new ResolvedValue(Format(q.Value) + " bd", q.Value, "bd"));
            return true;
        }

        return false;
    }

    // After white space: [business|working|calendar] day(s) | week(s) | month(s) | year(s) | hour(s) | minute(s) | second(s).
    private static bool UnitAfter(string text, int pos, out string code, out int end)
    {
        code = "";
        end = pos;
        int i = pos;
        List<(string Word, int End)> words = [];
        while (words.Count < 2 && i < text.Length)
        {
            int s = i;
            while (s < text.Length && text[s] == ' ')
            {
                s++;
            }

            if (s == i && words.Count > 0 || s == text.Length)
            {
                break;
            }

            int e = s;
            while (e < text.Length && char.IsAsciiLetter(text[e]))
            {
                e++;
            }

            if (e == s)
            {
                break;
            }

            words.Add((text[s..e], e));
            i = e;
        }

        if (words.Count == 0)
        {
            return false;
        }

        if (words.Count == 2 && IsBusiness(words[0].Word) && UnitOf(words[1].Word) is { Code: "d" })
        {
            code = "bd";
            end = words[1].End;
            return true;
        }

        if (UnitOf(words[0].Word) is { } u)
        {
            code = u.Code;
            end = words[0].End;
            return true;
        }

        return false;
    }

    private static bool IsBusiness(string w) =>
        w.Equals("business", StringComparison.OrdinalIgnoreCase) || w.Equals("working", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Words(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && !char.IsAsciiLetter(s[i]))
            {
                i++;
            }

            int st = i;
            while (i < s.Length && char.IsAsciiLetter(s[i]))
            {
                i++;
            }

            if (i > st)
            {
                yield return s[st..i];
            }
        }
    }

    private static (string Code, decimal Seconds)? UnitOf(string word) => word.ToLowerInvariant() switch
    {
        "second" or "seconds" or "sec" or "secs" => ("s", 1m),
        "minute" or "minutes" or "min" or "mins" => ("min", 60m),
        "hour" or "hours" or "hr" or "hrs" => ("h", 3600m),
        "day" or "days" => ("d", 86400m),
        "week" or "weeks" => ("w", 604800m),
        "month" or "months" => ("mo", 0m),
        "year" or "years" => ("y", 0m),
        _ => null,
    };
}
