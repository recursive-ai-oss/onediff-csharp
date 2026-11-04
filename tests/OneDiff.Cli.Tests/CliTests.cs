using System.Text;
using OneDiff.Merge;

namespace OneDiff.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "onediff-cli-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string File_(string name, string content)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));
        return path;
    }

    private static (int Code, string Out, string Err) Run(string stdin, params string[] args)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(stdin));
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = Program.Run(args, new CliIo(input, output, error));
        return (code, Encoding.UTF8.GetString(output.ToArray()), Encoding.UTF8.GetString(error.ToArray()));
    }

    // ---- diff

    [Fact]
    public void DiffExitCodesFollowGnuDiff()
    {
        string a = File_("a", "one\ntwo\n"), b = File_("b", "one\n2\n"), same = File_("same", "one\ntwo\n");
        Assert.Equal(0, Run("", "diff", a, same).Code);
        (int code, string output, _) = Run("", "diff", a, b);
        Assert.Equal(1, code);
        Assert.Equal($"--- {a}\n+++ {b}\n@@ -1,2 +1,2 @@\n one\n-two\n+2\n", output);
        Assert.Equal(2, Run("", "diff", a).Code);
        Assert.Equal(2, Run("", "diff", a, Path.Combine(dir, "missing")).Code);
        Assert.Equal(2, Run("", "diff", "-", "-").Code);
        Assert.Equal(2, Run("", "diff", "--bogus", a, b).Code);
        Assert.Equal(2, Run("", "bogus").Code);
        Assert.Equal(2, Run("").Code);
    }

    [Fact]
    public void DiffMatchesTheLibrary()
    {
        const string Old = "a\nb\nc\nd\ne\nf\ng\n", New = "a\nB\nc\nd\ne\nf\nG\n";
        string a = File_("a", Old), b = File_("b", New);
        Assert.Equal(Diff.Unified(Old, New, a, b, 1, DiffAlgorithm.Patience), Run("", "diff", "-u", "-U", "1", "-A", "patience", a, b).Out);
        Assert.Equal(Diff.Unified(Old, New, "x", "y"), Run("", "diff", "--label", "x", "--label=y", a, b).Out);
        Assert.Equal(Diff.PatchText(Old, New), Run("", "diff", "--dmp", a, b).Out);
        Assert.Equal(Diff.ToMarkdown(Diff.Lines(Old, New)).Replace("```diff\n", "```diff\n--- " + a + "\n+++ " + b + "\n"), Run("", "diff", "--markdown", a, b).Out);
        Assert.Equal(Diff.ToMarkdownInline(Diff.Words("a b c", "a x c")) + "\n", Run("", "diff", "--markdown", "-g", "word", File_("w1", "a b c"), File_("w2", "a x c")).Out);
        string plain = Run("", "diff", "--terminal", "--no-color", "-U1", a, b).Out;
        Assert.DoesNotContain('\u001b', plain);
        Assert.Contains('\u001b', Run("", "diff", "--terminal", a, b).Out);
    }

    [Fact]
    public void DiffReadsStandardInputAndRejectsBadCombinations()
    {
        string b = File_("b", "x\ny\n");
        (int code, string output, _) = Run("x\nz\n", "diff", "-", b);
        Assert.Equal(1, code);
        Assert.StartsWith("--- -\n+++ " + b + "\n", output, StringComparison.Ordinal);
        Assert.Equal(2, Run("", "diff", "-g", "word", b, b).Code);
        Assert.Equal(2, Run("", "diff", "--dmp", "-A", "myers", b, b).Code);
        Assert.Equal(2, Run("", "diff", "-A", "fastest", b, b).Code);
        Assert.Equal(2, Run("", "diff", "-U", "x", b, b).Code);
    }

    [Fact]
    public void DiffTreatsNulBytesAsBinary()
    {
        string a = Path.Combine(dir, "a.bin"), b = Path.Combine(dir, "b.bin");
        File.WriteAllBytes(a, [1, 0, 2]);
        File.WriteAllBytes(b, [1, 0, 3]);
        (int code, string output, _) = Run("", "diff", a, b);
        Assert.Equal(1, code);
        Assert.Equal($"Binary files {a} and {b} differ\n", output);
    }

    [Fact]
    public void DiffPassesLegacyEncodedLinesThroughUnchanged()
    {
        string a = Path.Combine(dir, "a"), b = Path.Combine(dir, "b");
        File.WriteAllBytes(a, [0x63, 0x61, 0x66, 0xE9, 0x0A, 0x31, 0x0A]);   // "café" in Latin-1, not valid UTF-8
        File.WriteAllBytes(b, [0x63, 0x61, 0x66, 0xE9, 0x0A, 0x32, 0x0A]);
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        Assert.Equal(1, Program.Run(["diff", a, b], new CliIo(input, output, error)));
        byte[] bytes = output.ToArray();
        Assert.Contains((byte)0xE9, bytes);
        Assert.DoesNotContain((byte)0xC3, bytes);
    }

    // ---- patch

    [Fact]
    public void PatchAppliesUnifiedAndDmpPatches()
    {
        const string Old = "one\ntwo\nthree\nfour\nfive\n", New = "one\n2\nthree\nfour\nfive\nsix\n";
        string original = File_("o", Old);

        string unified = File_("u.patch", Diff.Unified(Old, New));
        Assert.Equal((0, New), Run("", "patch", original, unified).Take2());
        Assert.Equal((0, New), Run(Diff.Unified(Old, New), "patch", original).Take2());
        Assert.Equal((0, New), Run("", "patch", "-i", unified, original).Take2());
        Assert.Equal((0, Old), Run("", "patch", "-R", File_("n", New), unified).Take2());

        string dmp = File_("d.patch", Diff.PatchText(Old, New));
        Assert.Equal((0, New), Run("", "patch", original, dmp).Take2());

        string target = Path.Combine(dir, "out");
        Assert.Equal(0, Run("", "patch", "-o", target, original, unified).Code);
        Assert.Equal(New, File.ReadAllText(target));
    }

    [Fact]
    public void PatchReportsOffsetFuzzAndRejects()
    {
        string patch = Diff.Unified("a\nb\nc\nd\ne\n", "a\nb\nC\nd\ne\n");
        string shifted = File_("s", "x\ny\na\nb\nc\nd\ne\n");
        (int code, string output, string err) = Run("", "patch", shifted, File_("p", patch));
        Assert.Equal(0, code);
        Assert.Equal("x\ny\na\nb\nC\nd\ne\n", output);
        Assert.Contains("Hunk #1 succeeded at 3 (offset 2 lines).", err, StringComparison.Ordinal);

        string drifted = File_("f", "a\nb\nc\nD\nE\n");
        (code, output, err) = Run("", "patch", drifted, File_("p2", patch));
        Assert.Equal(0, code);
        Assert.Equal("a\nb\nC\nD\nE\n", output);
        Assert.Contains("with fuzz", err, StringComparison.Ordinal);
        Assert.Equal(1, Run("", "patch", "--fuzz", "0", drifted, File_("p3", patch)).Code);

        string unrelated = File_("u", "completely\ndifferent\n");
        string rej = Path.Combine(dir, "rej");
        (code, output, err) = Run("", "patch", "--reject-file", rej, unrelated, File_("p4", patch));
        Assert.Equal(1, code);
        Assert.Equal("completely\ndifferent\n", output);
        Assert.Contains("Hunk #1 FAILED", err, StringComparison.Ordinal);
        Assert.Contains("@@ ", File.ReadAllText(rej), StringComparison.Ordinal);
    }

    [Fact]
    public void PatchReportsDmpFailures()
    {
        string dmp = File_("d", Diff.PatchText("The quick brown fox jumps over the lazy dog.", "The quick red fox jumps over the lazy dog."));
        (int code, string output, string err) = Run("", "patch", File_("o", "The quick brown fox jumps over a lazy dog."), dmp);
        Assert.Equal(0, code);
        Assert.Equal("The quick red fox jumps over a lazy dog.", output);
        Assert.Contains("Patch #1 succeeded", err, StringComparison.Ordinal);

        (code, _, err) = Run("", "patch", File_("x", "nothing alike here at all, truly"), dmp);
        Assert.Equal(1, code);
        Assert.Contains("Patch #1 FAILED", err, StringComparison.Ordinal);
    }

    [Fact]
    public void PatchTroubleExits2()
    {
        string o = File_("o", "a\n");
        Assert.Equal(2, Run("", "patch", o, File_("bad", "--- a\n+++ b\n@@ -1 +1 @@\n")).Code);
        Assert.Equal(2, Run("", "patch", o, File_("bad2", "@@ -1 +1 @@\n%zz\n")).Code);
        Assert.Equal(2, Run("", "patch").Code);
        Assert.Equal(2, Run("", "patch", "-F", "x", o, o).Code);
        Assert.Equal(2, Run("", "patch", "-R", o, File_("d", Diff.PatchText("a\n", "b\n"))).Code);
        string two = Diff.Unified("a\n", "b\n", "f1", "f1") + Diff.Unified("c\n", "d\n", "f2", "f2");
        Assert.Equal(2, Run("", "patch", o, File_("two", two)).Code);
        Assert.Equal((0, "a\n"), Run("", "patch", o, File_("empty", "")).Take2());
    }

    // ---- merge

    private const string Base = "1\n2\n3\n4\n5\n6\n7\n8\n";
    private const string Ours = "1\nours\n3\n4\n5\n6\n7\n8\n";
    private const string Theirs = "1\ntheirs\n3\n4\n5\n6\n7\n8\n";

    [Theory]
    [InlineData("merge", ConflictStyle.Merge)]
    [InlineData("diff3", ConflictStyle.Diff3)]
    [InlineData("zdiff3", ConflictStyle.ZDiff3)]
    public void MergeIsByteIdenticalToTheLibrary(string style, ConflictStyle expectedStyle)
    {
        string o = File_("o", Ours), b = File_("b", Base), t = File_("t", Theirs);
        string expected = Diff.Merge(Base, Ours, Theirs, new MergeSettings { Style = expectedStyle, OursLabel = "L1", BaseLabel = "L2", TheirsLabel = "L3" }).Text;
        (int code, string output, _) = Run("", "merge", "--style", style, "-L", "L1", "-L", "L2", "-L", "L3", o, b, t);
        Assert.Equal(1, code);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void MergeExitStatusIsTheConflictCount()
    {
        string b = File_("b", Base);
        Assert.Equal(0, Run("", "merge", File_("o", Ours), b, File_("same", Base)).Code);
        Assert.Equal(1, Run("", "merge", File_("o2", Ours), b, File_("t", Theirs)).Code);

        string[] baseLines = Enumerable.Range(0, 2000).Select(i => "l" + i).ToArray();
        string many = string.Join("\n", baseLines) + "\n";
        string[] a = (string[])baseLines.Clone(), c = (string[])baseLines.Clone();
        for (int i = 0; i < 2000; i += 10)
        {
            a[i] = "ours" + i;
            c[i] = "theirs" + i;
        }

        Assert.Equal(127, Run("", "merge", "--diff3", File_("mo", string.Join("\n", a) + "\n"), File_("mb", many), File_("mt", string.Join("\n", c) + "\n")).Code);
    }

    [Fact]
    public void MergeResolutionsLabelsAndErrors()
    {
        string o = File_("o", Ours), b = File_("b", Base), t = File_("t", Theirs);
        (int code, string output, _) = Run("", "merge", "--ours", o, b, t);
        Assert.Equal((0, Ours), (code, output));
        Assert.Equal((0, Theirs), Run("", "merge", "--theirs", o, b, t).Take2());
        Assert.Equal((0, "1\nours\ntheirs\n3\n4\n5\n6\n7\n8\n"), Run("", "merge", "--union", o, b, t).Take2());

        string marked = Run("", "merge", "--marker-size", "9", o, b, t).Out;
        Assert.Contains("<<<<<<<<< " + o + "\n", marked, StringComparison.Ordinal);

        Assert.Equal(255, Run("", "merge", o, b).Code);
        Assert.Equal(255, Run("", "merge", "--ours", "--theirs", o, b, t).Code);
        Assert.Equal(255, Run("", "merge", o, b, Path.Combine(dir, "missing")).Code);
        Assert.Equal(255, Run("", "merge", "--style", "nope", o, b, t).Code);

        // Standard input stands in for one file.
        Assert.Equal(1, Run(Theirs, "merge", o, b, "-").Code);
    }

}

internal static class ResultExtensions
{
    public static (int, string) Take2(this (int Code, string Out, string Err) r) => (r.Code, r.Out);
}
