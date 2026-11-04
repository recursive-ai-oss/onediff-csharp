using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using OneDiff.Patching;

namespace OneDiff.Tests.Patches;

/// <summary>
/// Compares applying unified patches to drifted text with the released GNU patch, run as a black box. Needs ONEDIFF_ORACLES (the
/// directory holding patch) and ONEDIFF_GATE_WORK. Behaviour the GNU patch manual leaves open is settled by what patch prints.
/// </summary>
public partial class GnuPatchAgreementTests
{
    private static string? OracleBin => Environment.GetEnvironmentVariable("ONEDIFF_ORACLES");

    private static string Work => Environment.GetEnvironmentVariable("ONEDIFF_GATE_WORK") ?? Path.Combine(Path.GetTempPath(), "onediff-gates");

    [GeneratedRegex(@"^Hunk #(\d+) (?:succeeded at (\d+)(?: with fuzz (\d+))?(?: \((?:offset (-?\d+) lines?)\))?|FAILED at (\d+))", RegexOptions.Multiline)]
    private static partial Regex HunkLine();

    private sealed record Observed(string Kind, int Offset, int Fuzz);

    [Fact]
    public void DriftedPatchesAgreeWithGnuPatch()
    {
        Assert.SkipUnless(OracleBin is not null, "oracles not configured");
        string patchExe = Path.Combine(OracleBin!, "patch");
        string work = Path.Combine(Work, "drift");
        Directory.CreateDirectory(work);
        var random = new Random(20261104);
        int cases = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_DRIFT_CASES"), out int c) ? c : 800;
        int sameText = 0;
        int sameHunks = 0;
        int hunkTotal = 0;
        int hunkSame = 0;
        int sameExit = 0;
        var kinds = new Dictionary<string, int>();
        var disagreements = new List<string>();

        for (int i = 0; i < cases; i++)
        {
            (string baseText, string drifted, string patchText) = Generate(random);
            UnifiedFilePatch fp = UnifiedPatch.Parse(patchText).Files[0];

            string dir = Path.Combine(work, "c" + i);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "f"), Encoding.Latin1.GetBytes(drifted));
            File.WriteAllBytes(Path.Combine(dir, "drifted"), Encoding.Latin1.GetBytes(drifted));
            File.WriteAllBytes(Path.Combine(dir, "p.diff"), Encoding.Latin1.GetBytes(patchText));
            (int exit, string output) = Run(patchExe, ["-p1", "-f", "--no-backup-if-mismatch", "-i", "p.diff"], dir); // -f: never ask, never assume the patch is reversed
            File.WriteAllText(Path.Combine(dir, "patch.out"), output);
            string theirText = Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(dir, "f")));
            var theirs = new Observed[fp.Hunks.Count];
            for (int h = 0; h < theirs.Length; h++)
            {
                theirs[h] = new Observed("applied", 0, 0);
            }

            foreach (Match m in HunkLine().Matches(output))
            {
                int idx = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
                if (m.Groups[5].Success)
                {
                    theirs[idx] = new Observed("rejected", 0, 0);
                }
                else
                {
                    int fuzz = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                    int offset = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                    theirs[idx] = new Observed(fuzz > 0 ? "fuzz" : offset != 0 ? "offset" : "applied", offset, fuzz);
                }
            }

            UnifiedApplyResult ours = fp.Apply(drifted);
            bool textSame = ours.Text == theirText;
            bool exitSame = (exit == 0) == ours.Success;
            bool hunksSame = true;
            for (int h = 0; h < theirs.Length; h++)
            {
                string mine = ours.Hunks[h].Outcome switch
                {
                    HunkOutcome.Applied => "applied",
                    HunkOutcome.AppliedWithOffset => "offset",
                    HunkOutcome.AppliedWithFuzz => "fuzz",
                    _ => "rejected",
                };
                hunkTotal++;
                kinds[theirs[h].Kind] = kinds.GetValueOrDefault(theirs[h].Kind) + 1;
                bool same = mine == theirs[h].Kind && (mine == "rejected" || (ours.Hunks[h].Offset == theirs[h].Offset && ours.Hunks[h].Fuzz == theirs[h].Fuzz));
                if (same)
                {
                    hunkSame++;
                }
                else
                {
                    hunksSame = false;
                    if (disagreements.Count < 30)
                    {
                        disagreements.Add($"case {i} hunk {h + 1}: ours {mine} off={ours.Hunks[h].Offset} fuzz={ours.Hunks[h].Fuzz}; patch {theirs[h].Kind} off={theirs[h].Offset} fuzz={theirs[h].Fuzz}");
                    }
                }
            }

            sameText += textSame ? 1 : 0;
            sameExit += exitSame ? 1 : 0;
            sameHunks += hunksSame ? 1 : 0;
            if (!textSame && disagreements.Count < 30)
            {
                disagreements.Add($"case {i}: texts differ (exit {exit}, ours success {ours.Success})");
            }

            if (textSame && hunksSame)
            {
                Directory.Delete(dir, true);
            }
        }

        string kindText = string.Join(", ", kinds.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"));
        Log($"drift agreement with {Run(patchExe, ["--version"], work).Output.Split('\n')[0]}: cases={cases} sameResultText={sameText} sameExitStatus={sameExit} allHunksSame={sameHunks} hunks={hunkTotal} hunkSame={hunkSame} (patch's hunk outcomes: {kindText})");
        foreach (string d in disagreements)
        {
            Log("  " + d);
        }

        string? min = Environment.GetEnvironmentVariable("ONEDIFF_DRIFT_MIN_TEXT_AGREEMENT");
        if (min is not null)
        {
            Assert.True(sameText >= double.Parse(min, System.Globalization.CultureInfo.InvariantCulture) * cases, $"text agreement {sameText}/{cases}");
        }
    }

    private static (string Base, string Drifted, string Patch) Generate(Random random)
    {
        string[] vocab = ["alpha", "beta", "gamma", "delta", "eps", "zeta", "eta", "theta"];
        int n = random.Next(40, 120);
        var lines = new List<string>();
        for (int k = 0; k < n; k++)
        {
            // Mostly distinct lines, with some repeats so that a hunk can match in more than one place.
            lines.Add(random.Next(8) == 0 ? "repeat " + vocab[random.Next(vocab.Length)] + "\n" : $"line {k} {vocab[random.Next(vocab.Length)]}\n");
        }

        string baseText = string.Concat(lines);
        var changed = new List<string>(lines);
        int clusters = random.Next(1, 5);
        var positions = Enumerable.Range(0, clusters).Select(_ => random.Next(n)).Distinct().OrderByDescending(x => x).ToList();
        foreach (int p in positions)
        {
            switch (random.Next(3))
            {
                case 0:
                    changed[p] = "changed " + p + "\n";
                    break;
                case 1:
                    changed.Insert(p, "inserted " + p + "\n");
                    break;
                default:
                    changed.RemoveAt(p);
                    break;
            }
        }

        string newText = string.Concat(changed);
        string patch = UnifiedPatchTests.Make(baseText, newText, random.Next(1, 4), "a/f", "b/f");

        // Drift the base: shift lines, then perhaps touch context near the changes, and rarely the changed lines themselves.
        var drift = new List<string>(lines);
        for (int k = random.Next(0, 4); k > 0; k--)
        {
            int at = random.Next(drift.Count + 1);
            if (random.Next(2) == 0)
            {
                drift.Insert(at, "drift " + k + "\n");
            }
            else if (drift.Count > 0 && at < drift.Count)
            {
                drift.RemoveAt(at);
            }
        }

        int touch = random.Next(0, 4);
        for (int k = 0; k < touch && drift.Count > 0; k++)
        {
            int at = positions.Count > 0 ? Math.Clamp(positions[random.Next(positions.Count)] + random.Next(-4, 5), 0, drift.Count - 1) : random.Next(drift.Count);
            drift[at] = "touched " + k + "\n";
        }

        return (baseText, string.Concat(drift), patch);
    }

    private static (int Exit, string Output) Run(string exe, string[] args, string cwd)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, output + err.Result);
    }

    private static void Log(string line)
    {
        Console.WriteLine(line);
        string? path = Environment.GetEnvironmentVariable("ONEDIFF_GATE_LOG");
        if (path is not null)
        {
            File.AppendAllText(path, line + "\n");
        }
    }
}
