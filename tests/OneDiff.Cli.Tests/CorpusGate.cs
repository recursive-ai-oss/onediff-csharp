using System.Diagnostics;
using System.Globalization;
using System.Text;
using OneDiff.Merge;

namespace OneDiff.Cli.Tests;

/// <summary>
/// The corpus gate for the command line, run on a machine that has the corpora and the reference tools. It is skipped unless
/// ONEDIFF_CLI_CORPUS names the corpus root (holding <c>text/&lt;source&gt;/&lt;n&gt;/{old,new}</c> and
/// <c>merge/&lt;repo&gt;/&lt;n&gt;/{base,ours,theirs}</c>) and ONEDIFF_ORACLE_BIN names the folder with GNU <c>patch</c> and <c>git</c>.
/// Every check runs the built command line as a separate process.
/// </summary>
public sealed class CorpusGate
{
    private static readonly string? Corpus = Environment.GetEnvironmentVariable("ONEDIFF_CLI_CORPUS");
    private static readonly string? OracleBin = Environment.GetEnvironmentVariable("ONEDIFF_ORACLE_BIN");

    private static int Setting(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : fallback;

    private static void RequireCorpus()
    {
        Assert.SkipUnless(Corpus is not null && OracleBin is not null && Directory.Exists(Corpus), "set ONEDIFF_CLI_CORPUS and ONEDIFF_ORACLE_BIN to run the corpus gate");
    }

    private static List<string> Sample(string root, int wanted, params string[] files)
    {
        var all = new List<string>();
        foreach (string source in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            foreach (string pair in Directory.GetDirectories(source).Order(StringComparer.Ordinal))
            {
                if (files.All(f => File.Exists(Path.Combine(pair, f))))
                {
                    all.Add(pair);
                }
            }
        }

        // An even stride over the sorted list: the same sample every run.
        var picked = new List<string>();
        for (int i = 0; i < wanted && all.Count > 0; i++)
        {
            picked.Add(all[(int)((long)i * all.Count / wanted)]);
        }

        return picked.Distinct().ToList();
    }

    private static (int Code, byte[] Out, string Err) Exec(string file, IEnumerable<string> args, string? cwd = null, byte[]? stdin = null)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        foreach (string a in args)
        {
            start.ArgumentList.Add(a);
        }

        using Process process = Process.Start(start)!;
        Task<string> err = process.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            process.StandardInput.BaseStream.Write(stdin);
            process.StandardInput.Close();
        }

        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        return (process.ExitCode, output.ToArray(), err.Result);
    }

    // The built command line's own executable, which sits beside its assembly (the test host's process is not `dotnet`).
    private static (int Code, byte[] Out, string Err) Cli(params string[] args)
    {
        string dll = typeof(Program).Assembly.Location;
        string exe = Path.Combine(Path.GetDirectoryName(dll)!, Path.GetFileNameWithoutExtension(dll) + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        return Exec(exe, args);
    }

    [Fact]
    public void UnifiedDiffsApplyWithGnuPatchAndGitApply()
    {
        RequireCorpus();
        string patchTool = Path.Combine(OracleBin!, "patch"), gitTool = Path.Combine(OracleBin!, "git");
        string work = Path.Combine(Path.GetTempPath(), "onediff-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            List<string> pairs = Sample(Path.Combine(Corpus!, "text"), Setting("ONEDIFF_GATE_PAIRS", 500), "old", "new");
            int identical = 0, checkedPairs = 0;
            var failures = new List<string>();
            foreach (string pair in pairs)
            {
                string old = Path.Combine(pair, "old"), @new = Path.Combine(pair, "new");
                byte[] newBytes = File.ReadAllBytes(@new);
                (int code, byte[] patch, string err) = Cli("diff", "-u", "--label", "a/f", "--label", "b/f", old, @new);
                if (code == 0)
                {
                    identical++;
                    continue;
                }

                checkedPairs++;
                string patchFile = Path.Combine(work, "p.diff");
                File.WriteAllBytes(patchFile, patch);
                if (code != 1)
                {
                    failures.Add($"{pair}: onediff diff exited {code}: {err.Trim()}");
                    continue;
                }

                // GNU patch 2.8, original file named on the command line, result to a separate file.
                string patched = Path.Combine(work, "patched");
                var gnu = Exec(patchTool, ["--no-backup-if-mismatch", "-s", "-o", patched, old, patchFile]);
                if (gnu.Code != 0 || !File.ReadAllBytes(patched).AsSpan().SequenceEqual(newBytes))
                {
                    failures.Add($"{pair}: GNU patch exit {gnu.Code}, {Encoding.UTF8.GetString(gnu.Out).Trim()} {gnu.Err.Trim()}");
                }

                // git apply, in a directory holding the old file as f.
                string gitDir = Path.Combine(work, "g");
                Directory.CreateDirectory(gitDir);
                File.Copy(old, Path.Combine(gitDir, "f"), overwrite: true);
                var git = Exec(gitTool, ["apply", patchFile], gitDir);
                string result = Path.Combine(gitDir, "f");
                byte[] got = File.Exists(result) ? File.ReadAllBytes(result) : [];
                if (git.Code != 0 || !got.AsSpan().SequenceEqual(newBytes))
                {
                    failures.Add($"{pair}: git apply exit {git.Code}, {git.Err.Trim()}");
                }
            }

            string summary = $"unified: {pairs.Count} pairs sampled, {identical} identical, {checkedPairs} diffs checked with GNU patch and git apply, {failures.Count} failures";
            TestContext.Current.SendDiagnosticMessage(summary);
            Assert.True(failures.Count == 0, summary + "\n" + string.Join("\n", failures.Take(20)));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void MergeOutputIsByteIdenticalToTheLibrary()
    {
        RequireCorpus();
        List<string> triples = Sample(Path.Combine(Corpus!, "merge"), Setting("ONEDIFF_GATE_TRIPLES", 200), "base", "ours", "theirs");
        var failures = new List<string>();
        int runs = 0;
        foreach (string triple in triples)
        {
            string b = Path.Combine(triple, "base"), o = Path.Combine(triple, "ours"), t = Path.Combine(triple, "theirs");
            (TextEncoding encoding, string[] texts) = TextEncoding.Decode(File.ReadAllBytes(b), File.ReadAllBytes(o), File.ReadAllBytes(t));
            foreach ((string name, ConflictStyle style) in new[] { ("merge", ConflictStyle.Merge), ("diff3", ConflictStyle.Diff3), ("zdiff3", ConflictStyle.ZDiff3) })
            {
                runs++;
                TextMerge expected = Diff.Merge(texts[0], texts[1], texts[2], new MergeSettings { Style = style, OursLabel = o, BaseLabel = b, TheirsLabel = t });
                (int code, byte[] output, string err) = Cli("merge", "--style", name, o, b, t);
                byte[] expectedBytes = encoding.Bytes(expected.Text);
                int expectedCode = Math.Min(expected.ConflictCount, 127);
                if (code != expectedCode || !output.AsSpan().SequenceEqual(expectedBytes))
                {
                    failures.Add($"{triple} {name}: exit {code} (expected {expectedCode}), output {(output.AsSpan().SequenceEqual(expectedBytes) ? "same" : "differs")} {err.Trim()}");
                }
            }
        }

        string summary = $"merge: {triples.Count} triples x 3 styles = {runs} runs, {failures.Count} failures";
        TestContext.Current.SendDiagnosticMessage(summary);
        Assert.True(failures.Count == 0, summary + "\n" + string.Join("\n", failures.Take(20)));
    }
}
