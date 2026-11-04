using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Tests.Grouping;

/// <summary>
/// Random pairs of realistic paragraphs (sentences, numbers, dates, amounts, clause punctuation, CJK, Thai, Arabic, emoji,
/// formatting runs) with random edits of every kind the grouping handles. Every comparison must verify with no fallback, under
/// every preset, and changes must stay few for few edits.
/// </summary>
public class GroupingPropertyTests
{
    private static int Cases =>
        int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_GROUP_CASES"), CultureInfo.InvariantCulture, out int n) ? n : 200;

    private static int FirstSeed =>
        int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_GROUP_SEED"), CultureInfo.InvariantCulture, out int n) ? n : 1;

    private static readonly string[] Words =
    [
        "the", "Supplier", "Buyer", "shall", "deliver", "goods", "within", "payment", "invoice", "terminate", "notice", "breach", "of",
        "and", "or", "without", "cause", "agreement", "party", "parties", "liable", "damages", "confidential", "information", "law",
        "30 days", "60 days", "$1,250,000", "12.5%", "March 3, 2025", "2026-01-31", "three (3) months", "Section 4.2", "(a)", "e.g.",
        "Mr. Smith", "U.S.A.", "\u201Cquoted\u201D", "it\u2019s", "\u2014", "caf\u00E9", "na\u00EFve", "\uD83D\uDE00", "\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67",
    ];

    private static readonly string[] Foreign =
    [
        "买方应在三十日内支付全部价款。", "卖方应当按时交付货物。", "ผู้ซื้อต้องชำระราคาทั้งหมดภายในสามสิบวัน", "ผู้ขายต้องส่งมอบสินค้าตรงเวลา",
        "يجب على المشتري أن يدفع الثمن كاملا خلال 30 يوما.", "הקונה ישלם את המחיר המלא בתוך 30 ימים.", "購入者は30日以内に代金を支払う。",
    ];

    // Seeds that once produced a format unit joined across a deletion, which put the link's edits out of order.
    [Theory]
    [InlineData(29281)]
    [InlineData(1033107)]
    public void FormatUnitsNeverInterleaveWithTextUnits(int seed) => Assert.True(Check(seed).Changes.Length >= 0);

    [Fact]
    public void RealisticPairsVerifyWithoutFallbackUnderEveryPreset()
    {
        int first = FirstSeed, withChanges = 0, units = 0, atoms = 0;
        for (int seed = first; seed < first + Cases; seed++)
        {
            Comparison c = Check(seed);
            withChanges += c.Changes.IsEmpty ? 0 : 1;
            units += c.Changes.Length;
            atoms += c.Changes.Sum(x => Math.Max(1, x.Edits.Length));
        }

        if (Cases >= 100)
        {
            Assert.True(withChanges > Cases / 2, $"only {withChanges} of {Cases} cases had changes");
            Assert.True(units < atoms, "grouping never joined anything");
        }
    }

    private static Comparison Check(int seed)
    {
        var r = new Random(seed);
        CompareSettings settings = (seed % 3) switch
        {
            0 => CompareSettings.Legal,
            1 => CompareSettings.Strict with { ThrowOnFailure = false },
            _ => CompareSettings.TextOnly,
        };
        int count = r.Next(1, 7);
        var oldParas = new List<Para>();
        for (int i = 0; i < count; i++)
        {
            oldParas.Add(RandomPara(r));
        }

        var newParas = oldParas.Select(p => Mutate(p, r)).ToList();
        if (count > 2 && r.Next(5) == 0)
        {
            (newParas[0], newParas[^1]) = (newParas[^1], newParas[0]);
        }

        Comparison c = Comparison.Of(Build(oldParas), Build(newParas), settings);
        string context = $"seed {seed} settings {settings.Name}";
        Assert.True(c.Verification.FallbackLevel == 0, context + " fell back: " + string.Join(" | ", c.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.True(c.Check().IsEmpty, context + " failed re-check");
        Assert.True(c.Groups.Length <= c.Changes.Length);
        return c;
    }

    private sealed record Para(string Text, List<FormatRun> Runs);

    private static Para RandomPara(Random r)
    {
        var sb = new StringBuilder();
        int sentences = r.Next(1, 5);
        for (int s = 0; s < sentences; s++)
        {
            if (s > 0)
            {
                sb.Append(' ');
            }

            if (r.Next(8) == 0)
            {
                sb.Append(Foreign[r.Next(Foreign.Length)]);
                continue;
            }

            int n = r.Next(5, 22);
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    sb.Append(r.Next(20) == 0 ? ", " : r.Next(40) == 0 ? "; " : " ");
                }

                sb.Append(Words[r.Next(Words.Length)]);
            }

            sb.Append(r.Next(10) == 0 ? "?" : ".");
        }

        string text = sb.ToString();
        var runs = new List<FormatRun>();
        int pos = 0;
        while (r.Next(3) == 0 && pos < text.Length - 6)
        {
            int start = r.Next(pos, text.Length - 4), len = r.Next(1, Math.Min(20, text.Length - start));
            runs.Add(new FormatRun(start, len, FormatSet.Of(r.Next(2) == 0 ? FormatKeys.Bold : FormatKeys.Italic, "1")));
            pos = start + len + 1;
        }

        return new Para(text, runs);
    }

    private static Para Mutate(Para p, Random r)
    {
        string t = p.Text;
        List<FormatRun> runs = [.. p.Runs];
        int edits = r.Next(0, 6);
        for (int e = 0; e < edits; e++)
        {
            // Mutations that move characters invalidate the format runs; they are kept only for the pure-formatting edits.
            int kind = r.Next(10);
            int at = t.Length == 0 ? 0 : r.Next(t.Length);
            while (at > 0 && at < t.Length && char.IsLowSurrogate(t[at]))
            {
                at--;
            }

            switch (kind)
            {
                case 0:
                case 1:
                    t = ReplaceWordAt(t, at, Words[r.Next(Words.Length)]);
                    runs.Clear();
                    break;
                case 2:
                    t = t.Insert(WordStart(t, at), Words[r.Next(Words.Length)] + " ");
                    runs.Clear();
                    break;
                case 3:
                    t = ReplaceWordAt(t, at, "");
                    runs.Clear();
                    break;
                case 4:
                    t = t.Replace("30", "90", StringComparison.Ordinal).Replace("三十", "六十", StringComparison.Ordinal);
                    runs.Clear();
                    break;
                case 5:
                    t = t.Replace(".", "!", StringComparison.Ordinal);
                    runs.Clear();
                    break;
                case 6:
                    t = t.Replace(" ", "  ", StringComparison.Ordinal);
                    runs.Clear();
                    break;
                case 7:
                    t = t.Replace("\u201C", "\"", StringComparison.Ordinal).Replace("\u2019", "'", StringComparison.Ordinal);
                    runs.Clear();
                    break;
                case 8:
                    runs.Add(new FormatRun(t.Length / 2, Math.Min(6, t.Length - (t.Length / 2)), FormatSet.Of(FormatKeys.Color, "#ff0000")));
                    break;
                default:
                    t += " " + Words[r.Next(Words.Length)] + " " + Words[r.Next(Words.Length)] + ".";
                    break;
            }
        }

        return new Para(t, Canonical(runs, t.Length));
    }

    private static List<FormatRun> Canonical(List<FormatRun> runs, int length)
    {
        // Drop runs that fall outside the text or overlap an earlier run; the builder wants them sorted and disjoint.
        var result = new List<FormatRun>();
        int end = 0;
        foreach (FormatRun run in runs.OrderBy(x => x.Start))
        {
            if (run.Start >= end && run.End <= length && run.Length > 0)
            {
                result.Add(run);
                end = run.End;
            }
        }

        return result;
    }

    private static int WordStart(string t, int at)
    {
        while (at > 0 && !char.IsWhiteSpace(t[at - 1]))
        {
            at--;
        }

        return at;
    }

    private static string ReplaceWordAt(string t, int at, string replacement)
    {
        if (t.Length == 0)
        {
            return replacement;
        }

        int s = WordStart(t, at), e = at;
        while (e < t.Length && !char.IsWhiteSpace(t[e]))
        {
            e++;
        }

        return t[..s] + replacement + t[e..];
    }

    private static CompareDocument Build(List<Para> paras)
    {
        var b = new CompareDocumentBuilder();
        b.Begin(NodeKind.Section);
        foreach (Para p in paras)
        {
            ImmutableArray<FormatRun> runs = FormatRunsOf(p);
            b.Add(NodeKind.Paragraph, new NodeContent { Text = p.Text, Runs = runs });
        }

        b.End();
        return b.Build();
    }

    private static ImmutableArray<FormatRun> FormatRunsOf(Para p) => [.. p.Runs];
}
