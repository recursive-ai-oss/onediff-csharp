using System.Diagnostics;
using System.Globalization;
using System.Text;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Tests.Grouping;

/// <summary>
/// An indicative speed row: a 3,000-paragraph contract with 300 edits, compared end to end. The test asserts the result is complete
/// and writes the stage times and peak memory where <c>ONEDIFF_SCALE_OUT</c> says; it asserts no time, because timings from a
/// shared machine are not results.
/// </summary>
public class GroupingScaleTests
{
    private static readonly string[] Vocabulary =
    [
        "the", "Supplier", "Buyer", "shall", "deliver", "goods", "within", "payment", "invoice", "terminate", "notice", "breach", "of",
        "and", "or", "without", "cause", "agreement", "party", "liable", "damages", "confidential", "information", "governing", "law",
        "warranty", "remedy", "indemnify", "assign", "consent", "written", "receipt", "reasonable", "applicable", "obligation",
    ];

    /// <summary>A 3,000-paragraph contract and a copy with 300 edits.</summary>
    private static (CompareDocument Old, CompareDocument New) Contract()
    {
        var r = new Random(20261104);
        var oldParas = new List<string>();
        for (int i = 0; i < 3000; i++)
        {
            var sb = new StringBuilder();
            for (int s = 0; s < 3; s++)
            {
                int n = r.Next(8, 16);
                for (int w = 0; w < n; w++)
                {
                    sb.Append(w == 0 ? "" : " ").Append(r.Next(25) == 0 ? (r.Next(5, 90) + " days") : Vocabulary[r.Next(Vocabulary.Length)]);
                }

                sb.Append(s < 2 ? ". " : ".");
            }

            oldParas.Add($"{i + 1}. {sb}");
        }

        List<string> newParas = [.. oldParas];
        for (int k = 0; k < 300; k++)
        {
            int at = r.Next(newParas.Count);
            string p = newParas[at];
            newParas[at] = (k % 4) switch
            {
                0 => p.Replace("the", "any", StringComparison.Ordinal),
                1 => p + " A further sentence is added here by the Buyer.",
                2 => p.Replace(" days", " business days", StringComparison.Ordinal) + " Extra.",
                _ => p.Replace(".", ";", StringComparison.Ordinal) + " Also.",
            };
        }

        return (Helpers.Text([.. oldParas]), Helpers.Text([.. newParas]));
    }

    /// <summary>
    /// What the semantic passes (cascade and rename grouping, typing, the substantive flag) add to a whole comparison of the contract
    /// above, as medians of five runs with the passes off and on. Written where <c>ONEDIFF_SCALE_OUT</c> says; no time is asserted.
    /// </summary>
    [Fact]
    public void SemanticPassesCostLittleOnTheScaleContract()
    {
        (CompareDocument a, CompareDocument b) = Contract();
        CompareSettings off = CompareSettings.Legal with { CollapseCascades = false, TypedChanges = false };
        CompareSettings on = CompareSettings.Legal;
        double Median(CompareSettings settings)
        {
            var times = new List<double>();
            for (int i = 0; i < 6; i++)
            {
                var sw = Stopwatch.StartNew();
                Comparison c = Comparison.Of(a, b, settings);
                times.Add(sw.Elapsed.TotalMilliseconds);
                Assert.True(c.Verification.Passed);
                Assert.Equal(0, c.Verification.FallbackLevel);
            }

            times.RemoveAt(0);
            times.Sort();
            return times[2];
        }

        double tOff = Median(off), tOn = Median(on);
        string? outFile = Environment.GetEnvironmentVariable("ONEDIFF_SCALE_OUT");
        if (!string.IsNullOrEmpty(outFile))
        {
            File.AppendAllText(outFile, string.Create(CultureInfo.InvariantCulture, $"semantic passes: off={tOff:F0}ms on={tOn:F0}ms ratio={tOn / tOff:F3} cores={Environment.ProcessorCount}\n"));
        }
    }

    [Fact]
    public void ThreeThousandBlocksWithThreeHundredChangesAreComplete()
    {
        (CompareDocument a, CompareDocument b) = Contract();
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(true);
        var total = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        Alignment al = Stages.Align(a, b);
        double tAlign = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        PairDiffs d = Stages.DiffPairs(al);
        double tDiff = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        ChangeSet g = Stages.Group(d);
        double tGroup = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        ChangeSet cl = Stages.Classify(g);
        double tClassify = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        Comparison c = Stages.Verify(cl);
        double tVerify = sw.Elapsed.TotalMilliseconds;
        double tTotal = total.Elapsed.TotalMilliseconds;
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Helpers.Clean(c);
        Assert.NotEmpty(c.Changes);

        string? outFile = Environment.GetEnvironmentVariable("ONEDIFF_SCALE_OUT");
        if (!string.IsNullOrEmpty(outFile))
        {
            using Process self = Process.GetCurrentProcess();
            string line = string.Create(CultureInfo.InvariantCulture,
                $"blocks=3000 edits=300 changes={c.Changes.Length} align={tAlign:F0}ms diff={tDiff:F0}ms group={tGroup:F0}ms classify={tClassify:F0}ms verify={tVerify:F0}ms total={tTotal:F0}ms allocated={allocated / 1048576}MB peakWorkingSet={self.PeakWorkingSet64 / 1048576}MB cores={Environment.ProcessorCount}");
            File.AppendAllText(outFile, line + "\n");
        }
    }
}
