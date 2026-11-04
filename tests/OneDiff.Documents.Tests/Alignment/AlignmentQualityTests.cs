using System.Globalization;
using System.Text;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Tests.Alignments;

public class AlignmentQualityTests
{
    private static int Pairs =>
        int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_ALIGN_PAIRS"), CultureInfo.InvariantCulture, out int n) ? n : 150;

    [Fact]
    public void LabelledPairsScoreAtLeastAsWellAsTheReferenceAligner() => Run(false);

    [Fact]
    public void HardLabelledPairsScoreAtLeastAsWellAsTheReferenceAligner() => Run(true);

    private static void Run(bool hard)
    {
        var mine = new Tally();
        var reference = new Tally();
        var lenient = new Tally();
        int pairs = Pairs;
        for (int seed = 1; seed <= pairs; seed++)
        {
            (CompareDocument a, CompareDocument b, List<GoldLink> gold, _) = LabelledPairs.Make(seed, 40, hard);
            Alignment m = Stages.Align(a, b);
            Alignment r = ReferenceAligner.Run(a, b, null, CompareSettings.Default, null, CancellationToken.None);
            mine.Add(gold, LabelledPairs.Predicted(m));
            lenient.Add(gold, LabelledPairs.Predicted(Stages.Align(a, b, null, CompareSettings.Default with { MoveThreshold = 0.5 })));
            reference.Add(gold, LabelledPairs.Predicted(r));
            Helpers.Clean(Comparison.Of(a, b));
        }

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"set={(hard ? "hard" : "easy")} pairs={pairs} seeds=1..{pairs} paragraphs=40");
        report.AppendLine("type        | block aligner P / R / F1       | reference aligner P / R / F1 | block aligner with MoveThreshold 0.5 P / R / F1");
        foreach (Gold k in Enum.GetValues<Gold>())
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{k,-11} | {mine.Precision(k):0.000} / {mine.Recall(k):0.000} / {mine.F1(k):0.000} (n={mine.Tp[(int)k] + mine.Fn[(int)k]}) | {reference.Precision(k):0.000} / {reference.Recall(k):0.000} / {reference.F1(k):0.000} | {lenient.Precision(k):0.000} / {lenient.Recall(k):0.000} / {lenient.F1(k):0.000}");
        }

        string? path = Environment.GetEnvironmentVariable("ONEDIFF_ALIGN_REPORT");
        if (path is not null)
        {
            File.WriteAllText(path + (hard ? ".hard" : ".easy"), report.ToString());
        }

        foreach (Gold k in Enum.GetValues<Gold>())
        {
            Assert.True(mine.F1(k) >= reference.F1(k) - 0.01, $"{k}: block aligner {mine.F1(k):0.000} below reference {reference.F1(k):0.000}\n{report}");
        }

        if (!hard)
        {
            Assert.True(mine.F1(Gold.Edited) >= 0.97, report.ToString());
            Assert.True(mine.F1(Gold.Moved) >= 0.85, report.ToString());
            Assert.True(mine.F1(Gold.Split) >= 0.80, report.ToString());
            Assert.True(mine.F1(Gold.Merged) >= 0.80, report.ToString());
        }
    }
}
