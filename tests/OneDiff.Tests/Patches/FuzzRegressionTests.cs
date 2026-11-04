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

    [Fact]
    public void HeaderLinesWithSeveralCarriageReturnsReadBackStable()
    {
        // Found by the parser fuzz run: the name used to keep one of two trailing carriage returns, then lose it on re-reading.
        UnifiedPatch p = UnifiedPatch.Parse("--- \r\r\n+++ b\r\n@@ -1 +1 @@\r\n-x\r\n+y\r\n");
        Assert.False(UnifiedPatch.TryParse("--- a\n+++ b\n@@ ,1 +1 @@\n-x\n+y\n", out _, out _)); // a header with no "-" before the old range
        Assert.False(UnifiedPatch.TryParse("--- a\n+++ b\n@@ -1 +1 @@\n-x\n+y\n@@ ,1 +1 @@\n", out _, out _));
        string text = p.ToString();
        Assert.Equal(text, UnifiedPatch.Parse(text).ToString());
        UnifiedPatch q = UnifiedPatch.Parse("--- a\n+++ b\n@@ -1 +1 @@ s\r\r\n-x\n+y\n");
        Assert.Equal(q.ToString(), UnifiedPatch.Parse(q.ToString()).ToString());
    }
}
