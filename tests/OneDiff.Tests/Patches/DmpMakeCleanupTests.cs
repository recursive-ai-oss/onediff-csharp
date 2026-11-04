using OneDiff.Patching;

namespace OneDiff.Tests;

public class DmpMakeCleanupTests
{
    [Fact]
    public void MakeTidiesItsDiffAndStillApplies()
    {
        string a = "The quick brown fox jumps over the lazy dog near the river bank today.";
        string b = "That slow red dog leaps across the lazy cat near the river bank tonight.";
        var patcher = new DmpPatcher();
        IReadOnlyList<DmpPatch> patches = patcher.Make(a, b);
        DmpApplyResult result = patcher.Apply(patches, a);
        Assert.True(result.Success);
        Assert.Equal(b, result.Text);

        // No equality of one or two characters survives between two changes: semantic cleanup folded them into the changes.
        foreach (DmpPatch p in patches)
        {
            var diffs = p.Diffs.ToList();
            for (int i = 1; i + 1 < diffs.Count; i++)
            {
                if (diffs[i].Operation == DmpOperation.Equal)
                {
                    Assert.True(diffs[i].Text.Length > 2, $"tiny equality '{diffs[i].Text}'");
                }
            }
        }
    }

    [Fact]
    public void MakeKeepsSurrogatePairsWhole()
    {
        string a = "x\U0001F600 one \U0001F601 two \U0001F602 three";
        string b = "x\U0001F600 won \U0001F601 too \U0001F603 three";
        var patcher = new DmpPatcher();
        DmpApplyResult result = patcher.Apply(patcher.Make(a, b), a);
        Assert.True(result.Success);
        Assert.Equal(b, result.Text);
    }
}
