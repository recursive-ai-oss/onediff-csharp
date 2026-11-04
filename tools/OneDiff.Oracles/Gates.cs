using System.Text;
using OneDiff.Tokenization;

namespace OneDiff.Oracles;

internal enum Status
{
    Pass,
    Fail,
    Skip,
}

/// <summary>The verdict on one case. Skips (oracle timed out, input too large for the oracle) are counted apart from failures.</summary>
internal sealed record Outcome(Status Status, string? Detail = null)
{
    public static Outcome Pass() => new(Status.Pass);

    public static Outcome Fail(string detail) => new(Status.Fail, detail);

    public static Outcome Skip(string detail) => new(Status.Skip, detail);
}

/// <summary>What a gate needs at run time.</summary>
internal sealed class GateContext(OracleTools tools, ITextCoreSeam seam, string work, string outDir, TimeSpan oracleTimeout)
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public OracleTools Tools { get; } = tools;

    public ITextCoreSeam Seam { get; } = seam;

    public string OutDir { get; } = outDir;

    public TimeSpan OracleTimeout { get; } = oracleTimeout;

    /// <summary>Largest input (in characters) sent to the diff-match-patch package, whose Python implementation is slow on big texts.</summary>
    public int MaxDmpChars { get; init; } = 200_000;

    public static string Read(string path) => Utf8.GetString(File.ReadAllBytes(path));

    public static void Write(string path, string text) => File.WriteAllBytes(path, Utf8.GetBytes(text));

    /// <summary>A private directory for one case, removed afterwards.</summary>
    public async Task<T> InTempDirAsync<T>(string label, Func<string, Task<T>> body)
    {
        string dir = Path.Combine(work, "tmp", label + "-" + Environment.CurrentManagedThreadId + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            return await body(dir).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>One named check over a set of cases.</summary>
internal abstract class Gate(string name, bool mergeCases, Feature? needs)
{
    public string Name { get; } = name;

    /// <summary>True for gates over three-way triples, false for gates over version pairs.</summary>
    public bool MergeCases { get; } = mergeCases;

    /// <summary>The library feature the gate calls, or null when it only needs what is already on main.</summary>
    public Feature? Needs { get; } = needs;

    public abstract Task<Outcome> RunAsync(GateContext ctx, Case input);

    public static IReadOnlyList<Gate> All { get; } =
    [
        new MinEditGate(),
        new ValidityGate(),
        new UnifiedRoundTripGate(),
        new DmpRoundTripGate(),
        new ApplyGate("accept-gnu-patch"),
        new ApplyGate("accept-git-apply"),
        new DmpInteropGate(),
        new MergeFileGate(MergeStyle.Merge),
        new MergeFileGate(MergeStyle.Diff3),
        new MergeFileGate(MergeStyle.Zdiff3),
    ];
}

/// <summary>Our Myers line-level edit distance against GNU <c>diff --minimal</c>.</summary>
internal sealed class MinEditGate() : Gate("min-edit", false, null)
{
    public override async Task<Outcome> RunAsync(GateContext ctx, Case input)
    {
        string oldText = GateContext.Read(input.Files[0]);
        string newText = GateContext.Read(input.Files[1]);

        // Lines end at '\n' only, as GNU diff cuts them, and keep their terminator, so "x" and "x\n" differ as they do there.
        string[] a = SplitLines(oldText);
        string[] b = SplitLines(newText);
        int ours = Diff.Sequences<string>(a, b, DiffAlgorithm.Myers).EditDistance;

        ProcResult r = await Proc.RunAsync(ctx.Tools.Diff, ["--minimal", "--", input.Files[0], input.Files[1]], null, ctx.OracleTimeout).ConfigureAwait(false);
        if (r.TimedOut)
        {
            return Outcome.Skip("oracle timed out");
        }

        if (r.ExitCode > 1)
        {
            return Outcome.Skip($"oracle exit {r.ExitCode}: {Trim(r.StderrText)}");
        }

        int theirs = 0;
        int lineStart = 0;
        byte[] o = r.Stdout;
        for (int i = 0; i <= o.Length; i++)
        {
            if (i == o.Length || o[i] == (byte)'\n')
            {
                if (i > lineStart && (o[lineStart] == (byte)'<' || o[lineStart] == (byte)'>'))
                {
                    theirs++;
                }

                lineStart = i + 1;
            }
        }

        return ours == theirs
            ? Outcome.Pass()
            : Outcome.Fail($"ours {ours} lines edited, oracle {theirs} ({a.Length} -> {b.Length} lines)");
    }

    internal static string[] SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines.ToArray();
    }

    internal static string Trim(string s) => s.Length <= 200 ? s.Trim() : s[..200].Trim() + "...";
}

/// <summary>Every algorithm over every token kind yields a script that is well formed and transforms old into new.</summary>
internal sealed class ValidityGate() : Gate("validity", false, null)
{
    private const int MaxWordChars = 3_000_000;
    private const int MaxCharacterChars = 200_000;

    public override Task<Outcome> RunAsync(GateContext ctx, Case input)
    {
        string oldText = GateContext.Read(input.Files[0]);
        string newText = GateContext.Read(input.Files[1]);
        int size = Math.Max(oldText.Length, newText.Length);
        var problems = new List<string>();

        foreach (TokenKind kind in new[] { TokenKind.Line, TokenKind.Word, TokenKind.Character })
        {
            if ((kind == TokenKind.Word && size > MaxWordChars) || (kind == TokenKind.Character && size > MaxCharacterChars))
            {
                continue;
            }

            int myers = -1;
            foreach (DiffAlgorithm algorithm in new[] { DiffAlgorithm.Myers, DiffAlgorithm.Patience, DiffAlgorithm.Histogram })
            {
                string label = $"{kind}/{algorithm}";
                try
                {
                    TextDiff d = Diff.Text(oldText, newText, kind, algorithm);
                    string? problem = Check(d);
                    if (problem is not null)
                    {
                        problems.Add($"{label}: {problem}");
                    }
                    else if (algorithm == DiffAlgorithm.Myers)
                    {
                        myers = d.Script.EditDistance;
                    }
                    else if (myers >= 0 && d.Script.EditDistance < myers)
                    {
                        // No valid script can beat the minimum, so one that does proves the Myers result was not minimal.
                        problems.Add($"{label}: distance {d.Script.EditDistance} is below Myers' {myers}");
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    problems.Add($"{label}: {e.GetType().Name}: {MinEditGate.Trim(e.Message)}");
                }
            }
        }

        return Task.FromResult(problems.Count == 0 ? Outcome.Pass() : Outcome.Fail(string.Join("; ", problems.Take(4))));
    }

    private static string? Check(TextDiff d)
    {
        string[] oldTokens = Texts(d.OldText, d.OldTokens);
        string[] newTokens = Texts(d.NewText, d.NewTokens);
        if (!EditScript.TryValidate(d.Script.Edits, oldTokens.Length, newTokens.Length, out string? error))
        {
            return error;
        }

        if (!d.Script.Matches<string>(oldTokens, newTokens))
        {
            return "an Equal run covers tokens that differ";
        }

        if (!d.Script.Apply<string>(oldTokens, newTokens).AsSpan().SequenceEqual(newTokens))
        {
            return "applying the script to old does not give new";
        }

        if (!d.Script.Unapply<string>(oldTokens, newTokens).AsSpan().SequenceEqual(oldTokens))
        {
            return "undoing the script on new does not give old";
        }

        return null;
    }

    private static string[] Texts(string text, ReadOnlySpan<TokenRange> ranges)
    {
        var result = new string[ranges.Length];
        for (int i = 0; i < ranges.Length; i++)
        {
            result[i] = text.Substring(ranges[i].Start, ranges[i].Length);
        }

        return result;
    }
}

/// <summary>Our unified diff, applied by our own patch code, reproduces the new text.</summary>
internal sealed class UnifiedRoundTripGate() : Gate("roundtrip-unified", false, Feature.UnifiedPatch)
{
    public override Task<Outcome> RunAsync(GateContext ctx, Case input)
    {
        string oldText = GateContext.Read(input.Files[0]);
        string newText = GateContext.Read(input.Files[1]);
        string patch = ctx.Seam.MakeUnified(oldText, newText);
        if (!ctx.Seam.TryApplyUnified(patch, oldText, out string result))
        {
            return Task.FromResult(Outcome.Fail("our patch did not apply to the text it was made from"));
        }

        return Task.FromResult(result == newText ? Outcome.Pass() : Outcome.Fail("applying our patch did not give new"));
    }
}

/// <summary>Our diff-match-patch patch text, applied by our own code, reproduces the new text.</summary>
internal sealed class DmpRoundTripGate() : Gate("roundtrip-dmp", false, Feature.DmpPatch)
{
    public override Task<Outcome> RunAsync(GateContext ctx, Case input)
    {
        string oldText = GateContext.Read(input.Files[0]);
        string newText = GateContext.Read(input.Files[1]);
        string patch = ctx.Seam.MakeDmp(oldText, newText);
        if (!ctx.Seam.TryApplyDmp(patch, oldText, out string result))
        {
            return Task.FromResult(Outcome.Fail("our patch did not apply to the text it was made from"));
        }

        return Task.FromResult(result == newText ? Outcome.Pass() : Outcome.Fail("applying our patch did not give new"));
    }
}

/// <summary>Our unified output, applied by GNU patch or by git apply, reproduces the new text.</summary>
internal sealed class ApplyGate(string name) : Gate(name, false, Feature.UnifiedPatch)
{
    public override async Task<Outcome> RunAsync(GateContext ctx, Case input)
    {
        string oldText = GateContext.Read(input.Files[0]);
        string newText = GateContext.Read(input.Files[1]);
        if (oldText == newText)
        {
            return Outcome.Skip("identical inputs have no patch to apply");
        }

        string patch = ctx.Seam.MakeUnified(oldText, newText);
        return await ctx.InTempDirAsync(Name, async dir =>
        {
            GateContext.Write(Path.Combine(dir, "f"), oldText);
            GateContext.Write(Path.Combine(dir, "p.diff"), patch);
            ProcResult r = Name == "accept-gnu-patch"
                ? await Proc.RunAsync(ctx.Tools.Patch, ["-p1", "-s", "--no-backup-if-mismatch", "-i", "p.diff"], dir, ctx.OracleTimeout).ConfigureAwait(false)
                : await Proc.RunAsync(ctx.Tools.Git, ["apply", "p.diff"], dir, ctx.OracleTimeout).ConfigureAwait(false);
            if (r.TimedOut)
            {
                return Outcome.Skip("oracle timed out");
            }

            if (r.ExitCode != 0)
            {
                return Outcome.Fail($"exit {r.ExitCode}: {MinEditGate.Trim(r.StderrText + r.StdoutText)}");
            }

            string path = Path.Combine(dir, "f");
            string got = File.Exists(path) ? GateContext.Read(path) : string.Empty;
            return got == newText ? Outcome.Pass() : Outcome.Fail("the tool applied our patch but the result is not new");
        }).ConfigureAwait(false);
    }
}

/// <summary>Our diff-match-patch patch text and the Python package's, each applied by the other side.</summary>
internal sealed class DmpInteropGate() : Gate("dmp-interop", false, Feature.DmpPatch)
{
    public override async Task<Outcome> RunAsync(GateContext ctx, Case input)
    {
        string oldText = GateContext.Read(input.Files[0]);
        string newText = GateContext.Read(input.Files[1]);
        if (Math.Max(oldText.Length, newText.Length) > ctx.MaxDmpChars)
        {
            return Outcome.Skip("input larger than the package is run on");
        }

        string ours = ctx.Seam.MakeDmp(oldText, newText);
        return await ctx.InTempDirAsync(Name, async dir =>
        {
            var problems = new List<string>();

            // Ours -> the package.
            GateContext.Write(Path.Combine(dir, "ours.patch"), ours);
            GateContext.Write(Path.Combine(dir, "old"), oldText);
            ProcResult applied = await Proc.RunAsync(ctx.Tools.Python, [ctx.Tools.DmpHelper, "apply", "ours.patch", "old", "applied"], dir, ctx.OracleTimeout).ConfigureAwait(false);
            if (applied.TimedOut)
            {
                return Outcome.Skip("oracle timed out");
            }

            if (applied.ExitCode != 0)
            {
                problems.Add($"package could not apply our patch (exit {applied.ExitCode})");
            }
            else if (GateContext.Read(Path.Combine(dir, "applied")) != newText)
            {
                problems.Add("package applied our patch to a different result");
            }

            // The package -> ours.
            GateContext.Write(Path.Combine(dir, "new"), newText);
            ProcResult made = await Proc.RunAsync(ctx.Tools.Python, [ctx.Tools.DmpHelper, "make", "old", "new", "theirs.patch"], dir, ctx.OracleTimeout).ConfigureAwait(false);
            if (made.TimedOut)
            {
                return Outcome.Skip("oracle timed out");
            }

            if (made.ExitCode != 0)
            {
                return Outcome.Skip($"package could not make a patch: {MinEditGate.Trim(made.StderrText)}");
            }

            if (!ctx.Seam.TryApplyDmp(GateContext.Read(Path.Combine(dir, "theirs.patch")), oldText, out string result))
            {
                problems.Add("we could not apply the package's patch");
            }
            else if (result != newText)
            {
                problems.Add("we applied the package's patch to a different result");
            }

            return problems.Count == 0 ? Outcome.Pass() : Outcome.Fail(string.Join("; ", problems));
        }).ConfigureAwait(false);
    }
}

/// <summary>Our three-way merge against <c>git merge-file</c>, byte for byte, in one conflict style.</summary>
internal sealed class MergeFileGate(MergeStyle style) : Gate("merge-file:" + style.ToString().ToLowerInvariant(), true, Feature.Merge)
{
    /// <summary>The git arguments for a merge of files named ours, base and theirs, written to stdout with fixed labels.</summary>
    public static string[] Arguments(MergeStyle style)
    {
        var args = new List<string> { "merge-file", "-p" };
        if (style == MergeStyle.Diff3)
        {
            args.Add("--diff3");
        }
        else if (style == MergeStyle.Zdiff3)
        {
            args.Add("--zdiff3");
        }

        args.AddRange(["-L", "ours", "-L", "base", "-L", "theirs", "ours", "base", "theirs"]);
        return args.ToArray();
    }

    public override async Task<Outcome> RunAsync(GateContext ctx, Case input)
    {
        string baseText = GateContext.Read(input.Files[0]);
        string oursText = GateContext.Read(input.Files[1]);
        string theirsText = GateContext.Read(input.Files[2]);
        MergeOutcome mine = ctx.Seam.Merge(baseText, oursText, theirsText, style);

        return await ctx.InTempDirAsync("merge", async dir =>
        {
            GateContext.Write(Path.Combine(dir, "base"), baseText);
            GateContext.Write(Path.Combine(dir, "ours"), oursText);
            GateContext.Write(Path.Combine(dir, "theirs"), theirsText);
            ProcResult r = await Proc.RunAsync(ctx.Tools.Git, Arguments(style), dir, ctx.OracleTimeout).ConfigureAwait(false);
            if (r.TimedOut)
            {
                return Outcome.Skip("oracle timed out");
            }

            if (r.ExitCode is < 0 or > 127)
            {
                return Outcome.Skip($"git merge-file failed ({r.ExitCode}): {MinEditGate.Trim(r.StderrText)}");
            }

            string theirsOut = r.StdoutText;
            if (mine.Text == theirsOut && mine.Conflicts == r.ExitCode)
            {
                return Outcome.Pass();
            }

            // Keep the evidence: a unified diff from the oracle's output to ours.
            string mismatches = Path.Combine(ctx.OutDir, "merge-mismatch", style.ToString().ToLowerInvariant());
            Directory.CreateDirectory(mismatches);
            string file = Path.Combine(mismatches, input.Id.Replace('/', '_') + ".diff");
            GateContext.Write(Path.Combine(dir, "oracle.out"), theirsOut);
            GateContext.Write(Path.Combine(dir, "our.out"), mine.Text);
            ProcResult diff = await Proc.RunAsync(ctx.Tools.Diff, ["-u", "--label", "git-merge-file", "--label", "ours", "oracle.out", "our.out"], dir, ctx.OracleTimeout).ConfigureAwait(false);
            await File.WriteAllBytesAsync(file, diff.Stdout).ConfigureAwait(false);
            return Outcome.Fail($"conflicts ours {mine.Conflicts} vs git {r.ExitCode}; output differs, diff in {Path.GetRelativePath(ctx.OutDir, file)}");
        }).ConfigureAwait(false);
    }
}
