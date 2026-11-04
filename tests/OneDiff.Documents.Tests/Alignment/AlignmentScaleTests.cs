using System.Diagnostics;
using System.Globalization;
using System.Text;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Tests.Alignments;

public class AlignmentScaleTests
{
    [Fact]
    public void ALargeContractAlignsCompletelyAndTheSeriesIsNearLinear()
    {
        bool full = Environment.GetEnvironmentVariable("ONEDIFF_ALIGN_SCALE") is not null;
        int[] sizes = full ? [750, 1500, 3000, 6000, 12000] : [300, 600];
        var report = new StringBuilder();
        report.AppendLine("blocks | nodes | align ms (best of 3) | compare ms | align alloc MB | peak working set MB | F1 edited/moved/split/merged | links");
        double firstPerBlock = 0, lastPerBlock = 0;
        foreach (int blocks in sizes)
        {
            (CompareDocument a, CompareDocument b, List<GoldLink> gold, _) = LabelledPairs.Make(7, blocks, true, 0.10);
            double best = double.MaxValue;
            Alignment al = Stages.Align(a, b);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int rep = 0; rep < (full ? 3 : 1); rep++)
            {
                var sw = Stopwatch.StartNew();
                al = Stages.Align(a, b);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }

            double allocMb = (GC.GetAllocatedBytesForCurrentThread() - before) / (full ? 3.0 : 1.0) / 1048576.0;
            var total = Stopwatch.StartNew();
            Comparison c = Comparison.Of(a, b);
            double compareMs = total.Elapsed.TotalMilliseconds;
            Assert.True(c.Verification.Passed);
            Assert.Equal(0, c.Verification.FallbackLevel);
            var tally = new Tally();
            tally.Add(gold, LabelledPairs.Predicted(al));
            double peak = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0;
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{blocks} | {a.Count} | {best:0.0} | {compareMs:0.0} | {allocMb:0.0} | {peak:0} | {tally.F1(Gold.Edited):0.000}/{tally.F1(Gold.Moved):0.000}/{tally.F1(Gold.Split):0.000}/{tally.F1(Gold.Merged):0.000} | {al.Links.Length}");
            if (firstPerBlock == 0)
            {
                firstPerBlock = best / blocks;
            }

            lastPerBlock = best / blocks;
        }

        string? path = Environment.GetEnvironmentVariable("ONEDIFF_ALIGN_REPORT");
        if (path is not null)
        {
            File.WriteAllText(path + ".scale", report.ToString());
        }

        if (full)
        {
            // Near-linear: the cost per block at 16 times the size stays within a small factor of the cost at the smallest size.
            Assert.True(lastPerBlock < firstPerBlock * 4, report.ToString());
        }
    }
}
