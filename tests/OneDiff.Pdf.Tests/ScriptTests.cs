using OneDiff.Documents.Model;

namespace OneDiff.Pdf.Tests;

/// <summary>Arabic and CJK pages. They need a font that covers the script; the tests look for one on the machine (or in the folder named by ONEDIFF_TEST_FONTS) and say so when they cannot find it.</summary>
public class ScriptTests
{
    private static string? Find(params string[] names)
    {
        var roots = new List<string>();
        if (Environment.GetEnvironmentVariable("ONEDIFF_TEST_FONTS") is { Length: > 0 } custom)
        {
            roots.Add(custom);
        }

        roots.AddRange(["/usr/share/fonts", "/usr/local/share/fonts", "/Library/Fonts", "/System/Library/Fonts/Supplemental"]);
        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string name in names)
            {
                string? hit = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).Order(StringComparer.Ordinal).FirstOrDefault();
                if (hit is not null)
                {
                    return hit;
                }
            }
        }

        return null;
    }

    [Fact]
    public void ChineseParagraphsAreMarkedExactly()
    {
        string? font = Find("DroidSansFallbackFull.ttf", "NotoSansCJK*.otf", "NotoSansSC*.ttf", "PingFang*.ttf");
        if (font is null)
        {
            Assert.Skip("no CJK font on this machine");
        }

        string[] a = ["供应商应当在订单日期后三十日内交付货物，客户应当在收到发票后六十日内付款。", "任何一方在对方发生重大违约且未补救时，可以书面通知终止本协议。", "本协议受特拉华州法律管辖。"];
        string[] b = ["供应商应当在订单日期后四十五日内交付货物，客户应当在收到发票后六十日内付款。", "任何一方在对方发生重大违约且未补救时，可以书面通知终止本协议。", "本协议受纽约州法律管辖。", "新增条款：保密义务。"];
        var spec = new Fixtures.Spec { FontFile = font, PageWidth = 400 };
        Run r = Run.Of(Fixtures.Make(spec with { Paragraphs = a }), Fixtures.Make(spec with { Paragraphs = b }));
        r.AssertComplete();
        r.AssertReextraction();
        Assert.NotEmpty(r.Comparison.Changes);
        Assert.Contains(r.Comparison.Changes, c => c.New.Text.Contains('四', StringComparison.Ordinal));
    }

    [Fact]
    public void ArabicParagraphsAreMarkedExactly()
    {
        string? font = Find("DejaVuSans.ttf", "NotoNaskhArabic-Regular.ttf", "NotoSansArabic-Regular.ttf", "GeezaPro.ttc");
        if (font is null)
        {
            Assert.Skip("no Arabic font on this machine");
        }

        string[] a = ["يجب على المورد تسليم البضائع خلال ثلاثين يوما من تاريخ الطلب.", "يجوز لأي طرف إنهاء هذه الاتفاقية بإشعار كتابي.", "تخضع هذه الاتفاقية لقوانين ولاية ديلاوير."];
        string[] b = ["يجب على المورد تسليم البضائع خلال ستين يوما من تاريخ الطلب.", "يجوز لأي طرف إنهاء هذه الاتفاقية بإشعار كتابي.", "تخضع هذه الاتفاقية لقوانين ولاية نيويورك."];
        var spec = new Fixtures.Spec { FontFile = font, Rtl = true, PageWidth = 420 };
        Run r = Run.Of(Fixtures.Make(spec with { Paragraphs = a }), Fixtures.Make(spec with { Paragraphs = b }));
        r.AssertComplete();
        r.AssertReextraction();
        Assert.NotEmpty(r.Comparison.Changes);
    }
}
