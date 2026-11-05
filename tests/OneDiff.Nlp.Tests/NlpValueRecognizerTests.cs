using System.Collections.Immutable;
using OneDiff.Documents;

namespace OneDiff.Nlp.Tests;

public class NlpValueRecognizerTests
{
    private static readonly NlpValueRecognizer Nlp = NlpValueRecognizer.Instance;
    private static readonly BuiltInValueRecognizer BuiltIn = BuiltInValueRecognizer.Instance;

    private static (ChangeType Type, string? Canonical)? Whole(IValueRecognizer r, string text, string? language = null)
    {
        ImmutableArray<RecognizedValue> v = r.Recognize(text, language);
        return v.Length == 1 && v[0].Start == 0 && v[0].Length == text.Length ? (v[0].Type, v[0].Value?.Canonical) : null;
    }

    // Our own phrasing of English contract values. The built-in recognizer reads digits; OneNLP also reads words.
    public static TheoryData<string, ChangeType, string> EnglishValues => new()
    {
        { "forty-two", ChangeType.Number, "42" },
        { "one hundred and five", ChangeType.Number, "105" },
        { "six percent", ChangeType.Percentage, "6%" },
        { "twelve and a half percent", ChangeType.Percentage, "12.5%" },
        { "thirty days", ChangeType.Duration, "30 d" },
        { "two weeks", ChangeType.Duration, "2 w" },
        { "ninety (90) days", ChangeType.Duration, "90 d" },
        { "sixty (60) business days", ChangeType.Duration, "60 bd" },
        { "ten business days", ChangeType.Duration, "10 bd" },
        { "eighteen months", ChangeType.Duration, "18 mo" },
        { "1 year and 6 months", ChangeType.Duration, "18 mo" },
        { "two years", ChangeType.Duration, "2 y" },
        { "one week and two days", ChangeType.Duration, "9 d" },
        { "two hundred thousand dollars", ChangeType.Amount, "USD 200000" },
        { "fifty cents", ChangeType.Amount, "USD 0.5" },
        { "five million euros", ChangeType.Amount, "EUR 5000000" },
        { "USD 25,000", ChangeType.Amount, "USD 25000" },
        { "$2 million", ChangeType.Amount, "USD 2000000" },
        { "£500", ChangeType.Amount, "GBP 500" },
        { "12.5%", ChangeType.Percentage, "12.5%" },
        { "30 days", ChangeType.Duration, "30 d" },
        { "May 16, 2028", ChangeType.Date, "2028-05-16" },
        { "the 16th of May, 2028", ChangeType.Date, "2028-05-16" },
        { "Sept. 3, 2029", ChangeType.Date, "2029-09-03" },
        { "March 2025", ChangeType.Date, "2025-03" },
        { "2028-05-16", ChangeType.Date, "2028-05-16" },
    };

    [Theory]
    [MemberData(nameof(EnglishValues))]
    public void ReadsEnglishValues(string text, ChangeType type, string canonical) =>
        Assert.Equal((type, canonical), Whole(Nlp, text));

    [Fact]
    public void TheEnglishSetIsAllReadByOneNlpAndFewByTheBuiltInRecognizer()
    {
        int builtIn = 0, nlp = 0, total = 0;
        var missed = new List<string>();
        foreach (var row in EnglishValues)
        {
            total++;
            var want = (row.Data.Item2, (string?)row.Data.Item3);
            bool b = Whole(BuiltIn, row.Data.Item1) == want, n = Whole(Nlp, row.Data.Item1) == want;
            builtIn += b ? 1 : 0;
            nlp += n ? 1 : 0;
            if (!b) { missed.Add(row.Data.Item1); }
        }

        // Numbers in the table: 26 of 26 against 9 of 26 (docs/nlp.md).
        Assert.Equal((26, 26, 9), (total, nlp, builtIn));
        Assert.NotEmpty(missed);
    }

    [Theory]
    [InlineData("forty-two")]
    [InlineData("six percent")]
    [InlineData("thirty days")]
    [InlineData("two hundred thousand dollars")]
    public void WrittenOutValuesAreAGainOverTheBuiltInRecognizer(string text)
    {
        Assert.NotNull(Whole(Nlp, text));
        // Not an assertion about the built-in recognizer's quality, only that the cases above are ones it does not read whole.
        Assert.Null(Whole(BuiltIn, text));
    }

    [Theory]
    [InlineData("18.000 EUR", "EUR 18000")]
    [InlineData("2 500 EUR", "EUR 2500")]
    [InlineData("人民币4,800元", "CNY 4800")]
    [InlineData("25,000 دولار", "USD 25000")]
    [InlineData("1. Juni 2029", "2029-06-01")]
    [InlineData("3 de enero de 2025", "2025-01-03")]
    [InlineData("2027年12月27日", "2027-12-27")]
    [InlineData("18 Monaten", "18 mo")]
    [InlineData("9个月", "9 mo")]
    [InlineData("6 %", "6%")]
    public void OtherLanguagesAreReadAsTheBuiltInRecognizerReadsThem(string text, string canonical)
    {
        Assert.Equal(canonical, Whole(BuiltIn, text)?.Canonical);
        Assert.Equal(canonical, Whole(Nlp, text)?.Canonical);
    }

    [Fact]
    public void ADeclaredNonEnglishLanguageIsLeftToTheFallback()
    {
        Assert.Equal(BuiltIn.Recognize("dreißig Tage", "de"), Nlp.Recognize("dreißig Tage", "de"));
        Assert.Empty(Nlp.Recognize("forty-two", "de"));
        Assert.Equal("42", Whole(Nlp, "forty-two", "en-GB")?.Canonical);
    }

    [Fact]
    public void ADateThatCouldBeReadTwoWaysStaysUnresolved()
    {
        (ChangeType, string?)? v = Whole(Nlp, "3/4/2025");
        Assert.Equal((ChangeType.Date, (string?)null), v);
        Assert.Equal((ChangeType.Date, (string?)null), Whole(Nlp, "3/4/2025", "en"));
    }

    [Fact]
    public void SeparatorsAreTheBuiltInReadingUnlessTheTextIsDeclaredEnglish()
    {
        Assert.Equal("1200", Whole(Nlp, "1.200")?.Canonical);
        Assert.Equal("1.2", Whole(Nlp, "1.200", "en")?.Canonical);
    }

    [Fact]
    public void PartiesAndYearsComeFromTheFallback()
    {
        Assert.Equal((ChangeType.Party, "Acme Inc."), Whole(Nlp, "Acme Inc."));
        Assert.Equal((ChangeType.Number, "2029"), Whole(Nlp, "2029"));
        Assert.Equal((ChangeType.Number, "2029"), Whole(Nlp, "2029", "en"));
    }

    [Fact]
    public void ValuesInsideALongerTextKeepTheirPositionsAndDoNotOverlap()
    {
        const string text = "pay six percent of USD 25,000 within thirty (30) days of May 16, 2028";
        ImmutableArray<RecognizedValue> v = Nlp.Recognize(text, null);
        Assert.Equal(["6%", "USD 25000", "30 d", "2028-05-16"], v.Select(x => x.Value?.Canonical));
        int end = 0;
        foreach (RecognizedValue x in v)
        {
            Assert.True(x.Start >= end);
            end = x.Start + x.Length;
        }

        Assert.Equal("thirty (30) days", text.Substring(v[2].Start, v[2].Length));
    }

    [Fact]
    public void EmptyAndOversizedTextsGiveNothingExtra()
    {
        Assert.Empty(Nlp.Recognize("", null));
        string big = new('x', 5000);
        Assert.Equal(BuiltIn.Recognize(big, null), Nlp.Recognize(big, null));
        Assert.Throws<ArgumentNullException>(() => Nlp.Recognize(null!, null));
    }

    [Fact]
    public void ConcurrentCallsAgree()
    {
        string[] texts = ["thirty days", "six percent", "USD 25,000", "May 16, 2028", "forty-two", "18.000 EUR"];
        string[] expected = [.. texts.Select(t => string.Join("|", Nlp.Recognize(t, null).Select(x => x.Value?.Canonical)))];
        Parallel.For(0, 400, i =>
        {
            int k = i % texts.Length;
            Assert.Equal(expected[k], string.Join("|", Nlp.Recognize(texts[k], null).Select(x => x.Value?.Canonical)));
        });
    }
}
