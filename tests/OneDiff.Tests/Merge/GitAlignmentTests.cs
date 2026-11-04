using OneDiff.Merge;

namespace OneDiff.Tests.Merge;

/// <summary>
/// Triples over a handful of repeated lines, where the merge depends on which of several equally short alignments of each side
/// with the base is used (ties between equal-length scripts, sliding of changed runs, lines that repeat too often to match).
/// Each expected text is what git merge-file 2.51.2 printed with -L ours -L base -L theirs. The lines are synthetic.
/// </summary>
public class GitAlignmentTests
{
    private const ConflictStyle M = ConflictStyle.Merge;
    private const ConflictStyle D3 = ConflictStyle.Diff3;
    private const ConflictStyle Z3 = ConflictStyle.ZDiff3;

    public static TheoryData<string, string, string, ConflictStyle, ConflictResolution, string, int> Cases => new()
    {
        { "c\nb\nb\nc\nc\na\na\n", "c\nb\nb\nb\na\nc\nc\na\na\n", "b\nb\na\nc\nc\na\na\n", M, ConflictResolution.None, "b\nb\n<<<<<<< ours\nb\n=======\n>>>>>>> theirs\na\nc\nc\na\na\n", 1 },
        { "c\nb\nb\nc\nc\na\na\n", "c\nb\nb\nb\na\nc\nc\na\na\n", "b\nb\na\nc\nc\na\na\n", D3, ConflictResolution.None, "b\nb\n<<<<<<< ours\nb\na\n||||||| base\n=======\na\n>>>>>>> theirs\nc\nc\na\na\n", 1 },
        { "c\nb\nb\nc\nc\na\na\n", "c\nb\nb\nb\na\nc\nc\na\na\n", "b\nb\na\nc\nc\na\na\n", Z3, ConflictResolution.None, "b\nb\n<<<<<<< ours\nb\n||||||| base\n=======\n>>>>>>> theirs\na\nc\nc\na\na\n", 1 },
        { "c\nc\nc\na\nc\nc\n", "c\nc\na\nc\nb\nc\n", "c\nc\nc\na\nb\nc\n", M, ConflictResolution.None, "c\nc\na\n<<<<<<< ours\nc\n=======\n>>>>>>> theirs\nb\nc\n", 1 },
        { "c\nc\nc\na\nc\nc\n", "c\nc\na\nc\nb\nc\n", "c\nc\nc\na\nb\nc\n", D3, ConflictResolution.None, "c\nc\na\n<<<<<<< ours\nc\nb\n||||||| base\nc\n=======\nb\n>>>>>>> theirs\nc\n", 1 },
        { "c\nc\nc\na\nc\nc\n", "c\nc\na\nc\nb\nc\n", "c\nc\nc\na\nb\nc\n", Z3, ConflictResolution.None, "c\nc\na\n<<<<<<< ours\nc\n||||||| base\nc\n=======\n>>>>>>> theirs\nb\nc\n", 1 },
        { "c\na\na\nc\nb\na\na\n", "c\na\nc\nc\na\na\n", "c\nc\na\nc\nb\na\nc\n", M, ConflictResolution.None, "c\n<<<<<<< ours\n=======\nc\n>>>>>>> theirs\na\nc\nc\na\nc\n", 1 },
        { "c\na\na\nc\nb\na\na\n", "c\na\nc\nc\na\na\n", "c\nc\na\nc\nb\na\nc\n", D3, ConflictResolution.None, "c\n<<<<<<< ours\na\n||||||| base\na\na\n=======\nc\na\n>>>>>>> theirs\nc\nc\na\nc\n", 1 },
        { "c\na\na\nc\nb\na\na\n", "c\na\nc\nc\na\na\n", "c\nc\na\nc\nb\na\nc\n", Z3, ConflictResolution.None, "c\n<<<<<<< ours\n||||||| base\na\na\n=======\nc\n>>>>>>> theirs\na\nc\nc\na\nc\n", 1 },
        { "c\nb\na\nc\n", "c\nb\nc\na\nc\nb\n", "c\nb\nc\nc\nc\n", M, ConflictResolution.None, "c\nb\nc\n<<<<<<< ours\na\n=======\nc\n>>>>>>> theirs\nc\nb\n", 1 },
        { "c\nb\na\nc\n", "c\nb\nc\na\nc\nb\n", "c\nb\nc\nc\nc\n", D3, ConflictResolution.None, "c\nb\n<<<<<<< ours\nc\na\n||||||| base\na\n=======\nc\nc\n>>>>>>> theirs\nc\nb\n", 1 },
        { "c\nb\na\nc\n", "c\nb\nc\na\nc\nb\n", "c\nb\nc\nc\nc\n", Z3, ConflictResolution.None, "c\nb\nc\n<<<<<<< ours\na\n||||||| base\na\n=======\nc\n>>>>>>> theirs\nc\nb\n", 1 },
        { "c\na\nc\nb\n", "c\na\nb\na\nb\n", "c\nc\na\nc\na\nb\n", M, ConflictResolution.None, "c\nc\na\n<<<<<<< ours\nb\n=======\nc\n>>>>>>> theirs\na\nb\n", 1 },
        { "c\na\nc\nb\n", "c\na\nb\na\nb\n", "c\nc\na\nc\na\nb\n", D3, ConflictResolution.None, "c\nc\na\n<<<<<<< ours\nb\na\n||||||| base\nc\n=======\nc\na\n>>>>>>> theirs\nb\n", 1 },
        { "c\na\nc\nb\n", "c\na\nb\na\nb\n", "c\nc\na\nc\na\nb\n", Z3, ConflictResolution.None, "c\nc\na\n<<<<<<< ours\nb\n||||||| base\nc\n=======\nc\n>>>>>>> theirs\na\nb\n", 1 },
        { "c\nb\na\nb\nc\nc\na\n", "c\nb\na\nb\nb\nc\nc\na\n", "c\nb\na\nb\nb\nc\na\n", M, ConflictResolution.None, "c\nb\na\nb\nb\n<<<<<<< ours\nc\n=======\n>>>>>>> theirs\nc\na\n", 1 },
        { "c\nb\na\nb\nc\nc\na\n", "c\nb\na\nb\nb\nc\nc\na\n", "c\nb\na\nb\nb\nc\na\n", D3, ConflictResolution.None, "c\nb\na\nb\n<<<<<<< ours\nb\nc\n||||||| base\nc\n=======\nb\n>>>>>>> theirs\nc\na\n", 1 },
        { "c\nb\na\nb\nc\nc\na\n", "c\nb\na\nb\nb\nc\nc\na\n", "c\nb\na\nb\nb\nc\na\n", Z3, ConflictResolution.None, "c\nb\na\nb\nb\n<<<<<<< ours\nc\n||||||| base\nc\n=======\n>>>>>>> theirs\nc\na\n", 1 },
        { "a\nb\nb\nb\nb\nb\na\na\nb\n", "a\nb\nb\na\na\nb\na\na\nb\n", "a\nb\nb\na\nb\nb\nb\na\na\nb\n", M, ConflictResolution.None, "a\nb\nb\na\n<<<<<<< ours\na\n=======\nb\nb\n>>>>>>> theirs\nb\na\na\nb\n", 1 },
        { "a\nb\nb\nb\nb\nb\na\na\nb\n", "a\nb\nb\na\na\nb\na\na\nb\n", "a\nb\nb\na\nb\nb\nb\na\na\nb\n", D3, ConflictResolution.None, "a\nb\nb\n<<<<<<< ours\na\na\n||||||| base\nb\nb\n=======\na\nb\nb\n>>>>>>> theirs\nb\na\na\nb\n", 1 },
        { "a\nb\nb\nb\nb\nb\na\na\nb\n", "a\nb\nb\na\na\nb\na\na\nb\n", "a\nb\nb\na\nb\nb\nb\na\na\nb\n", Z3, ConflictResolution.None, "a\nb\nb\na\n<<<<<<< ours\na\n||||||| base\nb\nb\n=======\nb\nb\n>>>>>>> theirs\nb\na\na\nb\n", 1 },
        { "b\nb\nb\nb\nb\nb\nb\na\nb\na\na\nb\nb\nb\na\n", "b\nb\nb\nb\nb\nb\na\nb\na\na\na\nb\nb\nb\nb\n", "b\nb\nb\nb\nb\nb\nb\na\nb\na\na\nb\nb\na\nb\n", M, ConflictResolution.None, "b\nb\nb\nb\nb\nb\na\nb\na\na\na\nb\nb\nb\n<<<<<<< ours\n=======\na\n>>>>>>> theirs\nb\n", 1 },
        { "b\nb\nb\nb\nb\nb\nb\na\nb\na\na\nb\nb\nb\na\n", "b\nb\nb\nb\nb\nb\na\nb\na\na\na\nb\nb\nb\nb\n", "b\nb\nb\nb\nb\nb\nb\na\nb\na\na\nb\nb\na\nb\n", D3, ConflictResolution.None, "b\nb\nb\nb\nb\nb\na\nb\na\na\na\nb\nb\nb\n<<<<<<< ours\nb\n||||||| base\nb\na\n=======\na\nb\n>>>>>>> theirs\n", 1 },
        { "b\nb\nb\nb\nb\nb\nb\na\nb\na\na\nb\nb\nb\na\n", "b\nb\nb\nb\nb\nb\na\nb\na\na\na\nb\nb\nb\nb\n", "b\nb\nb\nb\nb\nb\nb\na\nb\na\na\nb\nb\na\nb\n", Z3, ConflictResolution.None, "b\nb\nb\nb\nb\nb\na\nb\na\na\na\nb\nb\nb\n<<<<<<< ours\n||||||| base\nb\na\n=======\na\n>>>>>>> theirs\nb\n", 1 },
        { "b\nb\nb\nb\nb\nb\nb\n", "a\nb\na\nb\nb\nb\nb\nb\n", "a\nb\nb\nb\nb\nb\nb\n", M, ConflictResolution.None, "a\nb\n<<<<<<< ours\na\n=======\n>>>>>>> theirs\nb\nb\nb\nb\nb\n", 1 },
        { "b\nb\nb\nb\nb\nb\nb\n", "a\nb\na\nb\nb\nb\nb\nb\n", "a\nb\nb\nb\nb\nb\nb\n", D3, ConflictResolution.None, "<<<<<<< ours\na\nb\na\n||||||| base\nb\nb\n=======\na\nb\n>>>>>>> theirs\nb\nb\nb\nb\nb\n", 1 },
        { "b\nb\nb\nb\nb\nb\nb\n", "a\nb\na\nb\nb\nb\nb\nb\n", "a\nb\nb\nb\nb\nb\nb\n", Z3, ConflictResolution.None, "a\nb\n<<<<<<< ours\na\n||||||| base\nb\nb\n=======\n>>>>>>> theirs\nb\nb\nb\nb\nb\n", 1 },
        { "b\nb\na\na\na\na\na\na\na\nb\na\n", "b\na\na\na\na\na\nb\na\nb\na\n", "b\nb\na\na\na\na\na\na\nb\nb\na\n", M, ConflictResolution.None, "b\na\na\na\na\na\nb\n<<<<<<< ours\na\n=======\n>>>>>>> theirs\nb\na\n", 1 },
        { "b\nb\na\na\na\na\na\na\na\nb\na\n", "b\na\na\na\na\na\nb\na\nb\na\n", "b\nb\na\na\na\na\na\na\nb\nb\na\n", D3, ConflictResolution.None, "b\na\na\na\na\na\n<<<<<<< ours\nb\na\n||||||| base\na\n=======\nb\n>>>>>>> theirs\nb\na\n", 1 },
        { "b\nb\na\na\na\na\na\na\na\nb\na\n", "b\na\na\na\na\na\nb\na\nb\na\n", "b\nb\na\na\na\na\na\na\nb\nb\na\n", Z3, ConflictResolution.None, "b\na\na\na\na\na\nb\n<<<<<<< ours\na\n||||||| base\na\n=======\n>>>>>>> theirs\nb\na\n", 1 },
        { "b\na\nb\na\nb\nb\nb\nb\nb\na\nb\na\na\na\na\n", "b\na\nb\na\nb\nb\na\nb\na\nb\na\na\na\na\na\n", "b\na\nb\na\nb\nb\nb\na\nb\na\nb\na\na\na\nb\n", M, ConflictResolution.None, "b\na\nb\na\nb\nb\n<<<<<<< ours\na\n=======\n>>>>>>> theirs\nb\na\nb\na\nb\na\na\na\n<<<<<<< ours\na\n=======\nb\n>>>>>>> theirs\n", 2 },
        { "b\na\nb\na\nb\nb\nb\nb\nb\na\nb\na\na\na\na\n", "b\na\nb\na\nb\nb\na\nb\na\nb\na\na\na\na\na\n", "b\na\nb\na\nb\nb\nb\na\nb\na\nb\na\na\na\nb\n", D3, ConflictResolution.None, "b\na\nb\na\nb\nb\n<<<<<<< ours\na\nb\n||||||| base\nb\nb\nb\n=======\nb\n>>>>>>> theirs\na\nb\na\nb\na\na\na\n<<<<<<< ours\na\n||||||| base\n=======\nb\n>>>>>>> theirs\n", 2 },
        { "b\na\nb\na\nb\nb\nb\nb\nb\na\nb\na\na\na\na\n", "b\na\nb\na\nb\nb\na\nb\na\nb\na\na\na\na\na\n", "b\na\nb\na\nb\nb\nb\na\nb\na\nb\na\na\na\nb\n", Z3, ConflictResolution.None, "b\na\nb\na\nb\nb\n<<<<<<< ours\na\n||||||| base\nb\nb\nb\n=======\n>>>>>>> theirs\nb\na\nb\na\nb\na\na\na\n<<<<<<< ours\na\n||||||| base\n=======\nb\n>>>>>>> theirs\n", 2 },
        { "a\na\na\na\na\na\nb\na\nb\nb\nb\nb\n", "a\na\na\na\na\na\na\na\nb\nb\nb\nb\n", "a\na\na\na\na\na\na\nb\na\nb\nb\nb\nb\n", M, ConflictResolution.None, "a\na\na\na\na\na\na\n<<<<<<< ours\n=======\nb\n>>>>>>> theirs\na\nb\nb\nb\nb\n", 1 },
        { "a\na\na\na\na\na\nb\na\nb\nb\nb\nb\n", "a\na\na\na\na\na\na\na\nb\nb\nb\nb\n", "a\na\na\na\na\na\na\nb\na\nb\nb\nb\nb\n", D3, ConflictResolution.None, "a\na\na\na\na\na\n<<<<<<< ours\na\n||||||| base\nb\n=======\na\nb\n>>>>>>> theirs\na\nb\nb\nb\nb\n", 1 },
        { "a\na\na\na\na\na\nb\na\nb\nb\nb\nb\n", "a\na\na\na\na\na\na\na\nb\nb\nb\nb\n", "a\na\na\na\na\na\na\nb\na\nb\nb\nb\nb\n", Z3, ConflictResolution.None, "a\na\na\na\na\na\na\n<<<<<<< ours\n||||||| base\nb\n=======\nb\n>>>>>>> theirs\na\nb\nb\nb\nb\n", 1 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesGitMergeFile(string baseText, string ours, string theirs, ConflictStyle style, ConflictResolution resolution, string expected, int conflicts)
    {
        var settings = new MergeSettings { Style = style, Resolution = resolution };
        TextMerge merge = ThreeWayMerge.Lines(baseText, ours, theirs, settings);

        Assert.Equal(expected, merge.Text);
        Assert.Equal(conflicts, merge.ConflictCount);
    }
}
