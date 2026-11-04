using OneDiff.Merge;

namespace OneDiff.Oracles;

/// <summary>Which merge conflict style to produce, named as git names them.</summary>
internal enum MergeStyle
{
    Merge,
    Diff3,
    Zdiff3,
}

/// <summary>The parts of the text core that exist behind a gate.</summary>
internal enum Feature
{
    UnifiedPatch,
    DmpPatch,
    Merge,
}

/// <summary>A merge result: the merged text with conflict markers, and how many conflicts it holds.</summary>
internal sealed record MergeOutcome(string Text, int Conflicts);

/// <summary>
/// The only place the harness touches the library's patch and merge API. The gates are written against this interface;
/// <see cref="OneDiffSeam"/> is the one file to edit when the library gains each feature.
/// </summary>
internal interface ITextCoreSeam
{
    /// <summary>Whether the library side of <paramref name="feature"/> is wired up. Gates for unwired features report "pending".</summary>
    bool Has(Feature feature);

    /// <summary>A unified diff (three lines of context) whose file headers are <c>a/f</c> and <c>b/f</c>.</summary>
    string MakeUnified(string oldText, string newText);

    /// <summary>Applies a unified diff to <paramref name="text"/>; false when any hunk does not apply.</summary>
    bool TryApplyUnified(string patch, string text, out string result);

    /// <summary>Patch text in diff-match-patch's format that turns old into new.</summary>
    string MakeDmp(string oldText, string newText);

    /// <summary>Applies diff-match-patch patch text; false unless every hunk applies.</summary>
    bool TryApplyDmp(string patchText, string text, out string result);

    /// <summary>A three-way merge using git's marker layout with the labels <c>ours</c>, <c>base</c> and <c>theirs</c>.</summary>
    MergeOutcome Merge(string baseText, string ours, string theirs, MergeStyle style);
}

/// <summary>
/// The library under test. Nothing here is wired yet: each member is a one-line call into the public API once it ships.
/// To switch a gate on, implement its members and return true from <see cref="Has"/> for its feature.
/// </summary>
internal sealed class OneDiffSeam : ITextCoreSeam
{
    public bool Has(Feature feature) => feature == Feature.Merge;

    public string MakeUnified(string oldText, string newText) => throw Pending();

    public bool TryApplyUnified(string patch, string text, out string result) => throw Pending();

    public string MakeDmp(string oldText, string newText) => throw Pending();

    public bool TryApplyDmp(string patchText, string text, out string result) => throw Pending();

    public MergeOutcome Merge(string baseText, string ours, string theirs, MergeStyle style)
    {
        var settings = new MergeSettings
        {
            Style = style switch
            {
                MergeStyle.Diff3 => ConflictStyle.Diff3,
                MergeStyle.Zdiff3 => ConflictStyle.ZDiff3,
                _ => ConflictStyle.Merge,
            },
        };
        TextMerge merge = Diff.Merge(baseText, ours, theirs, settings);
        return new MergeOutcome(merge.Text, merge.ConflictCount);
    }

    private static NotSupportedException Pending() => new("the library does not expose this yet");
}

/// <summary>
/// A stand-in that answers every question with the oracles themselves (GNU diff -u, GNU patch, the diff-match-patch package,
/// git merge-file). Every gate must pass against it; that proves the gate plumbing before the library has the features.
/// </summary>
internal sealed class OracleSelfSeam(OracleTools tools, string scratch) : ITextCoreSeam
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    public bool Has(Feature feature) => true;

    public string MakeUnified(string oldText, string newText)
    {
        string dir = Fresh();
        try
        {
            GateContext.Write(Path.Combine(dir, "old"), oldText);
            GateContext.Write(Path.Combine(dir, "new"), newText);
            ProcResult r = Proc.Run(tools.Diff, ["-u", "--label", "a/f", "--label", "b/f", "old", "new"], dir, Limit);
            return r.StdoutText;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    public bool TryApplyUnified(string patch, string text, out string result)
    {
        string dir = Fresh();
        try
        {
            GateContext.Write(Path.Combine(dir, "f"), text);
            GateContext.Write(Path.Combine(dir, "p.diff"), patch);
            ProcResult r = Proc.Run(tools.Patch, ["-p1", "-s", "--no-backup-if-mismatch", "-i", "p.diff"], dir, Limit);
            result = File.Exists(Path.Combine(dir, "f")) ? GateContext.Read(Path.Combine(dir, "f")) : string.Empty;
            return r.ExitCode == 0;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    public string MakeDmp(string oldText, string newText)
    {
        string dir = Fresh();
        try
        {
            GateContext.Write(Path.Combine(dir, "old"), oldText);
            GateContext.Write(Path.Combine(dir, "new"), newText);
            Proc.Run(tools.Python, [tools.DmpHelper, "make", "old", "new", "out"], dir, Limit);
            return GateContext.Read(Path.Combine(dir, "out"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    public bool TryApplyDmp(string patchText, string text, out string result)
    {
        string dir = Fresh();
        try
        {
            GateContext.Write(Path.Combine(dir, "patch"), patchText);
            GateContext.Write(Path.Combine(dir, "text"), text);
            ProcResult r = Proc.Run(tools.Python, [tools.DmpHelper, "apply", "patch", "text", "out"], dir, Limit);
            result = File.Exists(Path.Combine(dir, "out")) ? GateContext.Read(Path.Combine(dir, "out")) : string.Empty;
            return r.ExitCode == 0;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    public MergeOutcome Merge(string baseText, string ours, string theirs, MergeStyle style)
    {
        string dir = Fresh();
        try
        {
            GateContext.Write(Path.Combine(dir, "base"), baseText);
            GateContext.Write(Path.Combine(dir, "ours"), ours);
            GateContext.Write(Path.Combine(dir, "theirs"), theirs);
            ProcResult r = Proc.Run(tools.Git, MergeFileGate.Arguments(style), dir, Limit);
            return new MergeOutcome(r.StdoutText, r.ExitCode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private string Fresh()
    {
        string dir = Path.Combine(scratch, "self-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
