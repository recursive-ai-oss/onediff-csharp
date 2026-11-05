using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OneDiff.Documents;

/// <summary>
/// The recognizer <see cref="Stages.Classify(Pipeline.ChangeSet, CancellationToken)"/> uses when none is given: deterministic
/// patterns, no model, no dictionary files. It reads amounts (<c>USD 25,000</c>, <c>18.000 EUR</c>, <c>人民币4,800元</c>,
/// <c>$2 million</c>), dates (ISO, <c>16 May 2028</c>, <c>May 16, 2028</c>, <c>1. Juni 2029</c>, <c>3 de enero de 2025</c>, <c>6 أغسطس 2029</c>,
/// <c>2027年12月27日</c>, numeric day/month/year where the order is not ambiguous), durations (<c>30 days</c>, <c>10 business days</c>,
/// <c>18 Monaten</c>, <c>30 يومًا</c>, <c>三十日</c>, <c>9个月</c>), percentages and plain numbers, and the names of parties that end in a legal
/// form (<c>Contoso Inc</c>, <c>Northwind GmbH</c>, <c>北風株式会社</c>). Month, unit and currency names are written for English, German,
/// French, Spanish, Arabic, Chinese and Japanese; other languages are recognized where they use digits and symbols. A number with one
/// separator and exactly three digits after it reads as a thousands group (<c>1.200</c> is 1200, <c>0.500</c> is 0.5); otherwise the
/// separator is the decimal mark. A date whose day and month cannot be told apart (<c>3/4/2025</c>) is recognized but not resolved.
/// Stateless and thread-safe.
/// </summary>
public sealed partial class BuiltInValueRecognizer : IValueRecognizer
{
    /// <summary>The shared instance.</summary>
    public static BuiltInValueRecognizer Instance { get; } = new();

    // A number: digits with a decimal or thousands separator of the usual kinds, including the space French and others group by.
    private const string Num = @"(?:\d{1,3}(?:[\u00A0\u202F ]\d{3})+(?:[,.]\d+)?|\d+(?:[,.]\d+)*)";

    private const string LatinUnit = @"(?:seconds?|minutes?|hours?|days?|weeks?|months?|years?|Tag(?:e|en)?|Woche(?:n)?|Monat(?:e|en)?|Jahr(?:e|en)?|jours?|semaines?|mois|ans?|années?|días?|dias?|semanas?|mes(?:es)?|años?|يوم(?:ًا)?|أيام|أسبوع|أسابيع|شهر(?:ا)?|أشهر|سنة|سنوات|عام|أعوام)";

    private static readonly Dictionary<string, int> Months = BuildMonths();

    private static readonly Dictionary<string, string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["second"] = "s", ["seconds"] = "s", ["minute"] = "min", ["minutes"] = "min", ["hour"] = "h", ["hours"] = "h",
        ["day"] = "d", ["days"] = "d", ["week"] = "w", ["weeks"] = "w", ["month"] = "mo", ["months"] = "mo", ["year"] = "y", ["years"] = "y",
        ["tag"] = "d", ["tage"] = "d", ["tagen"] = "d", ["woche"] = "w", ["wochen"] = "w", ["monat"] = "mo", ["monate"] = "mo", ["monaten"] = "mo",
        ["jahr"] = "y", ["jahre"] = "y", ["jahren"] = "y",
        ["jour"] = "d", ["jours"] = "d", ["semaine"] = "w", ["semaines"] = "w", ["mois"] = "mo", ["an"] = "y", ["ans"] = "y", ["année"] = "y", ["années"] = "y",
        ["día"] = "d", ["días"] = "d", ["dia"] = "d", ["dias"] = "d", ["semana"] = "w", ["semanas"] = "w", ["mes"] = "mo", ["meses"] = "mo", ["año"] = "y", ["años"] = "y",
        ["يوم"] = "d", ["يوما"] = "d", ["أيام"] = "d", ["أسبوع"] = "w", ["أسابيع"] = "w", ["شهر"] = "mo", ["شهرا"] = "mo", ["أشهر"] = "mo",
        ["سنة"] = "y", ["سنوات"] = "y", ["عام"] = "y", ["أعوام"] = "y",
        ["日"] = "d", ["日間"] = "d", ["天"] = "d", ["일"] = "d", ["週"] = "w", ["周"] = "w", ["週間"] = "w",
        ["个月"] = "mo", ["個月"] = "mo", ["ヶ月"] = "mo", ["か月"] = "mo", ["ヵ月"] = "mo", ["개월"] = "mo", ["年"] = "y", ["년"] = "y",
    };

    // ---- patterns ----

    [GeneratedRegex(@"(?<![\p{L}\d])(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<![\p{L}\d])(?<d>\d{1,2})(?:st|nd|rd|th|\.|º|er)?\s(?:de\s)?(?<m>\p{L}+)\.?(?:,?\s(?:de\s)?(?<y>\d{4})(?!\d))?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DayMonthDate();

    [GeneratedRegex(@"(?<![\p{L}\d])(?<m>\p{L}+)\.?\s(?<d>\d{1,2})(?:st|nd|rd|th)?(?!\d)(?:,?\s(?<y>\d{4})(?!\d))?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MonthDayDate();

    [GeneratedRegex(@"(?<![\p{L}\d])(?<m>\p{L}+)\s(?<y>\d{4})(?!\d)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MonthYearDate();

    [GeneratedRegex(@"(?<![\d/.])(?<a>\d{1,2})(?<s>[/.])(?<b>\d{1,2})\k<s>(?<y>\d{4}|\d{2})(?![\d/.])", RegexOptions.CultureInvariant)]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"(?<y>\d{4})年(?:(?<m>\d{1,2})月(?:(?<d>\d{1,2})日)?)?", RegexOptions.CultureInvariant)]
    private static partial Regex CjkDate();

    [GeneratedRegex(
        @"(?:(?<c>US\$|[$€£¥]|(?<![\p{L}])(?:USD|EUR|GBP|CAD|AUD|JPY|CHF|CNY)(?![\p{L}]))\s?(?<n>" + Num + @")(?:\s?(?<k>trillion|billion|million|thousand|bn|[kKmMbB])(?![\p{L}]))?)"
        + @"|(?:人民币(?<n>" + Num + @")(?<c>元))"
        + @"|(?:(?<n>" + Num + @")\s?(?<c>dollars?|euros?|pounds?|USD|EUR|GBP|CNY|JPY|CAD|AUD|CHF|دولار|يورو|€|\$|£)(?![\p{L}]))"
        + @"|(?:(?<n>" + Num + @")\s?(?<c>元|円))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"(?:(?<n>" + Num + @")\s?(?:%|％|٪|(?:per\s?cent|percent|Prozent|pour\s?cent|pourcent|por\s?ciento)(?![\p{L}])))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PercentPattern();

    [GeneratedRegex(
        @"(?:(?<![\p{L}\d])(?<n>" + Num + @")[\s-]?(?:(?<q>business|calendar|working)\s)?(?<u>" + LatinUnit + @")(?![\p{L}]))"
        + @"|(?:(?<n>\d+)\s?(?<u>週間|日間|ヶ月|か月|ヵ月|个月|個月|개월|日|天|週|周|年|일|년))"
        + @"|(?:(?<cn>[〇零一二三四五六七八九十百千万两]+)(?<u>日間|个月|個月|日|天|週|周|年))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"(?<![\p{L}\d.,])" + Num + @"(?![\d])", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(
        @"(?:(?<![\p{L}])(?:\p{Lu}[\p{L}&'’-]*\.?\s){0,4}\p{Lu}[\p{L}&'’-]*,?\s(?:Inc|Ltd|LLC|LLP|Corp|Corporation|Co|plc|PLC|GmbH|AG|S\.A|SA|SARL|S\.L|BV|B\.V|NV|N\.V|Pty|Limited|Company|KG|AB|SE|SpA)(?![\p{L}])\.?)"
        + @"|(?:[\p{L}]{1,12}(?:株式会社|有限会社|有限公司|有限责任公司|合同会社|股份有限公司))"
        + @"|(?:株式会社[\p{L}]{1,12})",
        RegexOptions.CultureInvariant)]
    private static partial Regex PartyPattern();

    private readonly record struct Candidate(int Start, int Length, int Priority, ChangeType Type, ResolvedValue? Value);

    /// <inheritdoc />
    public ImmutableArray<RecognizedValue> Recognize(string text, string? language)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Length > 4096)
        {
            // A value is a few words; a long text is a paragraph, which is not a value. The limit also bounds the work.
            return [];
        }

        var found = new List<Candidate>();
        Dates(text, found);
        Amounts(text, found);
        Percents(text, found);
        Durations(text, found);
        foreach (Match m in PartyPattern().Matches(text))
        {
            string name = m.Value.Trim();
            found.Add(new Candidate(m.Index, m.Length, 4, ChangeType.Party, new ResolvedValue(name)));
        }

        foreach (Match m in NumberPattern().Matches(text))
        {
            ResolvedValue? v = TryParseNumber(m.Value, out decimal d) ? new ResolvedValue(Format(d), d) : null;
            found.Add(new Candidate(m.Index, m.Length, 5, ChangeType.Number, v));
        }

        if (found.Count == 0)
        {
            return [];
        }

        // Higher-priority readings win where two overlap ("2027年3月15日" is a date, not a year and a day count), then the longer one,
        // then the earlier one.
        found.Sort(static (a, b) =>
        {
            int c = a.Priority.CompareTo(b.Priority);
            if (c != 0)
            {
                return c;
            }

            c = b.Length.CompareTo(a.Length);
            return c != 0 ? c : a.Start.CompareTo(b.Start);
        });
        var chosen = new List<Candidate>();
        foreach (Candidate c in found)
        {
            bool overlaps = false;
            foreach (Candidate k in chosen)
            {
                if (c.Start < k.Start + k.Length && k.Start < c.Start + c.Length)
                {
                    overlaps = true;
                    break;
                }
            }

            if (!overlaps)
            {
                chosen.Add(c);
            }
        }

        chosen.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        ImmutableArray<RecognizedValue>.Builder result = ImmutableArray.CreateBuilder<RecognizedValue>(chosen.Count);
        foreach (Candidate c in chosen)
        {
            result.Add(new RecognizedValue(c.Start, c.Length, c.Type, c.Value));
        }

        return result.MoveToImmutable();
    }

    // ---- dates ----

    private static void Dates(string text, List<Candidate> into)
    {
        foreach (Match m in IsoDate().Matches(text))
        {
            into.Add(Date(m, int.Parse(m.Groups["y"].ValueSpan, CultureInfo.InvariantCulture), int.Parse(m.Groups["m"].ValueSpan, CultureInfo.InvariantCulture), int.Parse(m.Groups["d"].ValueSpan, CultureInfo.InvariantCulture)));
        }

        foreach (Match m in CjkDate().Matches(text))
        {
            int y = int.Parse(m.Groups["y"].ValueSpan, CultureInfo.InvariantCulture);
            int mo = m.Groups["m"].Success ? int.Parse(m.Groups["m"].ValueSpan, CultureInfo.InvariantCulture) : 0;
            int d = m.Groups["d"].Success ? int.Parse(m.Groups["d"].ValueSpan, CultureInfo.InvariantCulture) : 0;
            into.Add(Date(m, y, mo, d));
        }

        foreach (Match m in DayMonthDate().Matches(text))
        {
            if (Months.TryGetValue(m.Groups["m"].Value, out int month))
            {
                into.Add(Date(m, m.Groups["y"].Success ? int.Parse(m.Groups["y"].ValueSpan, CultureInfo.InvariantCulture) : 0, month, int.Parse(m.Groups["d"].ValueSpan, CultureInfo.InvariantCulture)));
            }
        }

        foreach (Match m in MonthDayDate().Matches(text))
        {
            if (Months.TryGetValue(m.Groups["m"].Value, out int month))
            {
                into.Add(Date(m, m.Groups["y"].Success ? int.Parse(m.Groups["y"].ValueSpan, CultureInfo.InvariantCulture) : 0, month, int.Parse(m.Groups["d"].ValueSpan, CultureInfo.InvariantCulture)));
            }
        }

        foreach (Match m in MonthYearDate().Matches(text))
        {
            if (Months.TryGetValue(m.Groups["m"].Value, out int month))
            {
                into.Add(Date(m, int.Parse(m.Groups["y"].ValueSpan, CultureInfo.InvariantCulture), month, 0));
            }
        }

        foreach (Match m in NumericDate().Matches(text))
        {
            int a = int.Parse(m.Groups["a"].ValueSpan, CultureInfo.InvariantCulture), b = int.Parse(m.Groups["b"].ValueSpan, CultureInfo.InvariantCulture);
            int y = int.Parse(m.Groups["y"].ValueSpan, CultureInfo.InvariantCulture);
            if (y < 100)
            {
                y += y < 70 ? 2000 : 1900;
            }

            // Day first when the first number cannot be a month; month first when the second cannot; a dot means day first; otherwise
            // the order is a guess and the date is left unresolved.
            (int day, int month)? order =
                a > 12 && b <= 12 ? (a, b)
                : b > 12 && a <= 12 ? (b, a)
                : m.Groups["s"].Value == "." && a <= 31 && b <= 12 ? (a, b)
                : null;
            if (order is { } o && Valid(y, o.month, o.day))
            {
                into.Add(Date(m, y, o.month, o.day));
            }
            else
            {
                into.Add(new Candidate(m.Index, m.Length, 0, ChangeType.Date, null));
            }
        }
    }

    private static Candidate Date(Match m, int year, int month, int day)
    {
        bool valid = (month == 0 || (month >= 1 && month <= 12)) && (day == 0 || (month > 0 && Valid(year == 0 ? 2000 : year, month, day)));
        if (!valid)
        {
            return new Candidate(m.Index, m.Length, 0, ChangeType.Date, null);
        }

        string iso = year > 0
            ? month == 0 ? Pad4(year) : day == 0 ? Pad4(year) + "-" + Pad2(month) : Pad4(year) + "-" + Pad2(month) + "-" + Pad2(day)
            : "--" + Pad2(month) + "-" + Pad2(day);
        return new Candidate(m.Index, m.Length, 0, ChangeType.Date, new ResolvedValue(iso));
    }

    private static bool Valid(int year, int month, int day) => month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month);

    private static string Pad2(int v) => v.ToString("00", CultureInfo.InvariantCulture);

    private static string Pad4(int v) => v.ToString("0000", CultureInfo.InvariantCulture);

    // ---- amounts, percentages, durations ----

    private static void Amounts(string text, List<Candidate> into)
    {
        foreach (Match m in AmountPattern().Matches(text))
        {
            string cur = m.Groups["c"].Value;
            string? code = CurrencyCode(cur);
            if (code is null || !TryParseNumber(m.Groups["n"].Value, out decimal n))
            {
                into.Add(new Candidate(m.Index, m.Length, 1, ChangeType.Amount, null));
                continue;
            }

            if (m.Groups["k"].Success)
            {
                n *= m.Groups["k"].Value.ToLowerInvariant() switch { "trillion" => 1_000_000_000_000m, "billion" or "bn" or "b" => 1_000_000_000m, "million" or "m" => 1_000_000m, _ => 1_000m };
            }

            into.Add(new Candidate(m.Index, m.Length, 1, ChangeType.Amount, new ResolvedValue(code + " " + Format(n), n, code)));
        }
    }

    private static string? CurrencyCode(string s) => s.ToLowerInvariant() switch
    {
        "$" or "us$" or "usd" or "dollar" or "dollars" or "دولار" => "USD",
        "€" or "eur" or "euro" or "euros" or "يورو" => "EUR",
        "£" or "gbp" or "pound" or "pounds" => "GBP",
        "¥" or "jpy" or "円" => "JPY",
        "元" or "cny" => "CNY",
        "cad" => "CAD",
        "aud" => "AUD",
        "chf" => "CHF",
        _ => null,
    };

    private static void Percents(string text, List<Candidate> into)
    {
        foreach (Match m in PercentPattern().Matches(text))
        {
            ResolvedValue? v = TryParseNumber(m.Groups["n"].Value, out decimal n) ? new ResolvedValue(Format(n) + "%", n, "%") : null;
            into.Add(new Candidate(m.Index, m.Length, 2, ChangeType.Percentage, v));
        }
    }

    private static void Durations(string text, List<Candidate> into)
    {
        foreach (Match m in DurationPattern().Matches(text))
        {
            decimal n;
            bool ok = m.Groups["cn"].Success
                ? TryParseCjkNumber(m.Groups["cn"].Value, out n)
                : TryParseNumber(m.Groups["n"].Value, out n);
            string unit = StripMarks(m.Groups["u"].Value);
            if (!ok || !Units.TryGetValue(unit, out string? code))
            {
                into.Add(new Candidate(m.Index, m.Length, 3, ChangeType.Duration, null));
                continue;
            }

            if (m.Groups["q"].Success && code == "d" && m.Groups["q"].Value is { } q && !q.Equals("calendar", StringComparison.OrdinalIgnoreCase))
            {
                code = "bd";
            }

            into.Add(new Candidate(m.Index, m.Length, 3, ChangeType.Duration, new ResolvedValue(Format(n) + " " + code, n, code)));
        }
    }

    /// <summary>Removes Arabic vowel marks (U+064B to U+0652), which are optional in writing, so "يومًا" and "يوما" are the same word.</summary>
    private static string StripMarks(string s)
    {
        if (s.AsSpan().IndexOfAnyInRange('\u064B', '\u0652') < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is < '\u064B' or > '\u0652')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    // ---- numbers ----

    internal static string Format(decimal value) => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses a number written with a decimal comma or point and optional thousands groups; see the class remarks for the rule.</summary>
    internal static bool TryParseNumber(string raw, out decimal value)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            if (char.IsDigit(ch))
            {
                sb.Append((char)('0' + (int)char.GetNumericValue(ch)));
            }
            else if (ch is ',' or '.')
            {
                sb.Append(ch);
            }
        }

        string s = sb.ToString();
        int lastComma = s.LastIndexOf(','), lastDot = s.LastIndexOf('.');
        char decimalMark = '\0';
        if (lastComma >= 0 && lastDot >= 0)
        {
            decimalMark = lastComma > lastDot ? ',' : '.';
        }
        else if (lastComma >= 0 || lastDot >= 0)
        {
            char sep = lastComma >= 0 ? ',' : '.';
            int idx = s.LastIndexOf(sep);
            int count = 0;
            foreach (char c in s)
            {
                if (c == sep)
                {
                    count++;
                }
            }

            int after = s.Length - idx - 1;
            bool group = count > 1 || (after == 3 && idx is >= 1 and <= 3 && s[0] != '0');
            decimalMark = group ? '\0' : sep;
        }

        var clean = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsDigit(c))
            {
                clean.Append(c);
            }
            else if (c == decimalMark && decimalMark != '\0')
            {
                clean.Append('.');
            }
        }

        return decimal.TryParse(clean.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Reads a Chinese or Japanese numeral up to 99,999,999 (三十, 一百二十, 二〇二五).</summary>
    private static bool TryParseCjkNumber(string s, out decimal value)
    {
        value = 0;
        long total = 0, section = 0, digit = 0;
        bool positional = s.IndexOfAny(['十', '百', '千', '万']) < 0;
        long positionalValue = 0;
        foreach (char c in s)
        {
            int d = c switch { '〇' or '零' => 0, '一' => 1, '二' or '两' => 2, '三' => 3, '四' => 4, '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => -1 };
            if (d >= 0)
            {
                digit = d;
                positionalValue = (positionalValue * 10) + d;
                continue;
            }

            long mult = c switch { '十' => 10, '百' => 100, '千' => 1000, _ => 0 };
            if (mult > 0)
            {
                section += (digit == 0 ? 1 : digit) * mult;
                digit = 0;
            }
            else if (c == '万')
            {
                total += (section + digit) * 10_000;
                section = 0;
                digit = 0;
            }
            else
            {
                return false;
            }
        }

        value = positional ? positionalValue : total + section + digit;
        return true;
    }

    // ---- months ----

    private static Dictionary<string, int> BuildMonths()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(int month, params string[] names)
        {
            foreach (string n in names)
            {
                d[n] = month;
            }
        }

        Add(1, "january", "jan", "januar", "janvier", "enero", "يناير");
        Add(2, "february", "feb", "februar", "février", "fevrier", "febrero", "فبراير");
        Add(3, "march", "mar", "märz", "maerz", "mars", "marzo", "مارس");
        Add(4, "april", "apr", "avril", "abril", "أبريل", "إبريل");
        Add(5, "may", "mai", "mayo", "مايو");
        Add(6, "june", "jun", "juni", "juin", "junio", "يونيو");
        Add(7, "july", "jul", "juli", "juillet", "julio", "يوليو");
        Add(8, "august", "aug", "août", "aout", "agosto", "أغسطس");
        Add(9, "september", "sep", "sept", "septembre", "septiembre", "setiembre", "سبتمبر");
        Add(10, "october", "oct", "oktober", "octobre", "octubre", "أكتوبر");
        Add(11, "november", "nov", "novembre", "noviembre", "نوفمبر");
        Add(12, "december", "dec", "dezember", "décembre", "decembre", "diciembre", "ديسمبر");
        return d;
    }
}
