using OneDiff.Patching;

namespace OneDiff.Tests.Patches;

/// <summary>Minimized inputs the fuzz runs found.</summary>
public class FuzzRegressionTests
{
    [Fact]
    public void HeaderNamingALineFarOutsideTheTextDoesNotStepThroughTheDistance()
    {
        // Reversed, the hunk's old start is 999999991; the search used to walk a billion empty offsets.
        UnifiedPatch p = UnifiedPatch.Parse("--- a\n+++ b\n@@ -1 +999999991 @@\n-1\n+");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        UnifiedApplyResult r = p.Files[0].Apply("x\ny\n", new UnifiedApplyOptions { Reverse = true });
        Assert.True(watch.Elapsed.TotalSeconds < 2);
        Assert.False(r.Success);
    }

    [Fact]
    public void APatchThatDeletesThePaddingDoesNotThrow()
    {
        IReadOnlyList<DmpPatch> p = DmpPatcher.FromText("@@ -1 +1,80 @@\n-%0136A\n +");
        _ = new DmpPatcher().Apply(p, string.Empty);
        _ = new DmpPatcher().Apply(p, "x");
    }
}
