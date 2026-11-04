using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using OneDiff.Patching;

namespace OneDiff.Tests.Patches;

/// <summary>
/// Corpus and oracle gates for the patch code. They need a corpus of version pairs and the released GNU patch, git and diff-match-patch
/// binaries, so they run only when the environment names them:
/// ONEDIFF_CORPUS (directory of source/number/{old,new}), ONEDIFF_ORACLES (directory holding patch and git),
/// ONEDIFF_DMP_PYTHON (python with the diff-match-patch package), ONEDIFF_GATE_WORK (scratch directory), and optionally
/// ONEDIFF_GATE_STRIDE (use every n-th pair) and ONEDIFF_GATE_LOG (summary file). Oracles are only executed, never read.
/// </summary>
public class CorpusGateTests
{
    private static string? Corpus => Environment.GetEnvironmentVariable("ONEDIFF_CORPUS");

    private static string? OracleBin => Environment.GetEnvironmentVariable("ONEDIFF_ORACLES");

    private static string? Python => Environment.GetEnvironmentVariable("ONEDIFF_DMP_PYTHON");

    private static string Work => Environment.GetEnvironmentVariable("ONEDIFF_GATE_WORK") ?? Path.Combine(Path.GetTempPath(), "onediff-gates");

    private static int Stride => int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_GATE_STRIDE"), out int s) && s > 0 ? s : 1;

    // Size bounds, in characters per side: the C# round trips take everything up to the first; the Python interop run takes the second
    // (its patch_make is slow on very large inputs).
    private const int DmpRoundTripBound = 2_000_000;
    private const int DmpInteropBound = 200_000;

    private static readonly int[] Contexts = [3, 0, 1, 10];
    private static readonly string[] Tools = ["patch", "git"];
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static List<(string Id, string Dir)> Pairs()
    {
        var list = new List<(string, string)>();
        int i = 0;
        foreach (string source in Directory.EnumerateDirectories(Corpus!).Order(StringComparer.Ordinal))
        {
            foreach (string pair in Directory.EnumerateDirectories(source).Order(StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(pair, "old")) && File.Exists(Path.Combine(pair, "new")) && i++ % Stride == 0)
                {
                    list.Add((Path.GetFileName(source) + "_" + Path.GetFileName(pair), pair));
                }
            }
        }

        return list;
    }

    private static void Log(string line)
    {
        Console.WriteLine(line);
        string? path = Environment.GetEnvironmentVariable("ONEDIFF_GATE_LOG");
        if (path is not null)
        {
            lock (typeof(CorpusGateTests))
            {
                File.AppendAllText(path, line + "\n");
            }
        }
    }

    private static (int Exit, string Output) Run(string exe, string[] args, string cwd, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["LC_ALL"] = "C";
        psi.Environment["TZ"] = "UTC";
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(timeoutMs))
        {
            p.Kill(true);
            return (-1, "timeout");
        }

        return (p.ExitCode, output + err.Result);
    }

    // ------------------------------------------------------------------ unified: round trip, GNU patch, git apply

    [Fact]
    public void UnifiedRoundTripsAndIsAcceptedByGnuPatchAndGitApply()
    {
        Assert.SkipUnless(Corpus is not null && OracleBin is not null, "corpus and oracles not configured");
        string patchExe = Path.Combine(OracleBin!, "patch");
        string gitExe = Path.Combine(OracleBin!, "git");
        string work = Path.Combine(Work, "unified");
        Directory.CreateDirectory(work);
        var pairs = Pairs();
        var failures = new ConcurrentBag<string>();
        int identical = 0;
        int total = 0;
        int hunks = 0;
        int emptyOld = 0;
        int emptyNew = 0;
        int ext = 0;

        string patchVersion = Run(patchExe, ["--version"], work).Output.Split('\n')[0];
        string gitVersion = Run(gitExe, ["--version"], work).Output.Trim();
        Log($"oracles: {patchVersion}; {gitVersion}");

        Parallel.ForEach(pairs, new ParallelOptions { MaxDegreeOfParallelism = 8 }, pair =>
        {
            try
            {
                Interlocked.Increment(ref total);
                byte[] oldBytes = File.ReadAllBytes(Path.Combine(pair.Dir, "old"));
                byte[] newBytes = File.ReadAllBytes(Path.Combine(pair.Dir, "new"));
                string o = Latin1.GetString(oldBytes);
                string n = Latin1.GetString(newBytes);
                List<string> ol = PatchLines.Split(o);
                List<string> nl = PatchLines.Split(n);
                EditScript script = ReferenceDiff.Compute(ol, nl);

                // Round trips at several context lengths: make -> text -> parse -> text, apply forward and in reverse.
                foreach (int context in Contexts)
                {
                    if (context is 1 or 10 && pair.Id.Sum(c => c) % 7 != 0)
                    {
                        continue;
                    }

                    UnifiedFilePatch fp = UnifiedFilePatch.Create(ol, nl, script, "a/f", "b/f", context);
                    string text = fp.ToString();
                    if (text.Length == 0)
                    {
                        if (o != n)
                        {
                            failures.Add($"{pair.Id}: empty patch for different texts");
                        }

                        continue;
                    }

                    UnifiedPatch parsed = UnifiedPatch.Parse(text);
                    if (parsed.ToString() != text)
                    {
                        failures.Add($"{pair.Id} U{context}: parse/format changed the patch");
                    }

                    UnifiedApplyResult fwd = parsed.Files[0].Apply(o);
                    if (!fwd.Success || fwd.Text != n || fwd.Hunks.Any(h => h.Outcome != HunkOutcome.Applied))
                    {
                        failures.Add($"{pair.Id} U{context}: forward apply differs");
                    }

                    UnifiedApplyResult rev = parsed.Files[0].Apply(n, new UnifiedApplyOptions { Reverse = true });
                    if (!rev.Success || rev.Text != o)
                    {
                        failures.Add($"{pair.Id} U{context}: reverse apply differs");
                    }

                    if (context == 3)
                    {
                        Interlocked.Add(ref hunks, parsed.Files[0].Hunks.Count);
                    }
                }

                if (o == n)
                {
                    Interlocked.Increment(ref identical);
                    return;
                }

                // External acceptance: the context-3 patch, as bytes, applied by GNU patch and by git apply.
                bool createsFile = o.Length == 0;
                bool removesFile = n.Length == 0;
                if (createsFile)
                {
                    Interlocked.Increment(ref emptyOld);
                }

                if (removesFile)
                {
                    Interlocked.Increment(ref emptyNew);
                }

                string patchText = UnifiedFilePatch.Create(ol, nl, script, createsFile ? "/dev/null" : "a/f", removesFile ? "/dev/null" : "b/f", 3).ToString();
                byte[] patchBytes = Latin1.GetBytes(patchText);
                foreach (string tool in Tools)
                {
                    string dir = Path.Combine(work, pair.Id + "." + tool);
                    Directory.CreateDirectory(dir);
                    string target = Path.Combine(dir, "f");
                    if (!createsFile)
                    {
                        File.WriteAllBytes(target, oldBytes);
                    }

                    File.WriteAllBytes(Path.Combine(dir, "p.diff"), patchBytes);
                    (int Exit, string Output) r;
                    if (tool == "patch")
                    {
                        r = Run(patchExe, ["-p1", "--no-backup-if-mismatch", "--dry-run", "-i", "p.diff"], dir);
                        if (r.Exit != 0)
                        {
                            failures.Add($"{pair.Id} patch --dry-run exit {r.Exit}: {Trim(r.Output)}");
                            continue;
                        }

                        r = Run(patchExe, ["-p1", "--no-backup-if-mismatch", "-i", "p.diff"], dir);
                    }
                    else
                    {
                        r = Run(gitExe, ["apply", "--check", "p.diff"], dir);
                        if (r.Exit != 0)
                        {
                            failures.Add($"{pair.Id} git apply --check exit {r.Exit}: {Trim(r.Output)}");
                            continue;
                        }

                        r = Run(gitExe, ["apply", "p.diff"], dir);
                    }

                    if (r.Exit != 0)
                    {
                        failures.Add($"{pair.Id} {tool} exit {r.Exit}: {Trim(r.Output)}");
                        continue;
                    }

                    byte[] got = File.Exists(target) ? File.ReadAllBytes(target) : [];
                    if (!got.AsSpan().SequenceEqual(newBytes))
                    {
                        failures.Add($"{pair.Id} {tool}: result differs from new ({got.Length} vs {newBytes.Length} bytes)");
                    }
                    else
                    {
                        Interlocked.Increment(ref ext);
                    }

                    Directory.Delete(dir, true);
                }
            }
            catch (Exception e)
            {
                failures.Add($"{pair.Id}: {e.GetType().Name} {e.Message}");
            }
        });

        Log($"unified gate: pairs={total} identical={identical} hunks(U3)={hunks} createsFile={emptyOld} emptiesFile={emptyNew} oracleApplications_ok={ext} failures={failures.Count}");
        foreach (string f in failures.Take(25))
        {
            Log("  " + f);
        }

        Assert.Empty(failures);
    }

    private static string ScriptPath([System.Runtime.CompilerServices.CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(source)!, "dmp_interop.py");

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;

    // ------------------------------------------------------------------ dmp: round trip and interop with the released package

    [Fact]
    public void DmpRoundTripsAndInteroperatesWithTheReleasedPackage()
    {
        Assert.SkipUnless(Corpus is not null && Python is not null, "corpus and python oracle not configured");
        string work = Path.Combine(Work, "dmp");
        Directory.CreateDirectory(work);
        var pairs = Pairs();
        var failures = new ConcurrentBag<string>();
        var interop = new ConcurrentBag<string>();
        var astral = new ConcurrentBag<string>(); // pairs with characters outside the BMP
        int total = 0;
        int invalidUtf8 = 0;
        int tooBigRoundTrip = 0;
        int roundTripped = 0;
        int refRoundTripped = 0;
        int identical = 0;
        var strict = new UTF8Encoding(false, true);

        Parallel.ForEach(pairs, new ParallelOptions { MaxDegreeOfParallelism = 8 }, pair =>
        {
            try
            {
                Interlocked.Increment(ref total);
                string o;
                string n;
                try
                {
                    o = strict.GetString(File.ReadAllBytes(Path.Combine(pair.Dir, "old")));
                    n = strict.GetString(File.ReadAllBytes(Path.Combine(pair.Dir, "new")));
                }
                catch (DecoderFallbackException)
                {
                    Interlocked.Increment(ref invalidUtf8);
                    return;
                }

                if (o == n)
                {
                    Interlocked.Increment(ref identical);
                    return;
                }

                if (o.Length > DmpRoundTripBound || n.Length > DmpRoundTripBound)
                {
                    Interlocked.Increment(ref tooBigRoundTrip);
                    return;
                }

                var patcher = new DmpPatcher();
                string text = DmpPatcher.ToText(patcher.Make(o, n));
                DmpApplyResult r = patcher.Apply(DmpPatcher.FromText(text), o);
                if (!r.Success || r.Text != n)
                {
                    failures.Add($"{pair.Id}: default make/apply round trip failed");
                }
                else
                {
                    Interlocked.Increment(ref roundTripped);
                }

                if (o.Length <= 100_000 && n.Length <= 100_000)
                {
                    // Many small patches: the reference character diff exercises rolling context, splitting and drift.
                    string text2 = DmpPatcher.ToText(patcher.Make(o, ReferenceDmpLines.Diffs(o, n)));
                    DmpApplyResult r2 = patcher.Apply(DmpPatcher.FromText(text2), o);
                    if (!r2.Success || r2.Text != n)
                    {
                        failures.Add($"{pair.Id}: fine-grained make/apply round trip failed");
                    }
                    else
                    {
                        Interlocked.Increment(ref refRoundTripped);
                    }

                    if (o.Length <= DmpInteropBound && n.Length <= DmpInteropBound)
                    {
                        string dir = Path.Combine(work, pair.Id);
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(Path.Combine(dir, "old.txt"), Encoding.UTF8.GetBytes(o));
                        File.WriteAllBytes(Path.Combine(dir, "new.txt"), Encoding.UTF8.GetBytes(n));
                        File.WriteAllBytes(Path.Combine(dir, "ours.patch"), Encoding.UTF8.GetBytes(text2));
                        File.WriteAllBytes(Path.Combine(dir, "ours_default.patch"), Encoding.UTF8.GetBytes(text));
                        interop.Add(pair.Id);
                        if (o.Any(char.IsSurrogate) || n.Any(char.IsSurrogate))
                        {
                            astral.Add(pair.Id);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                failures.Add($"{pair.Id}: {e.GetType().Name} {e.Message}");
            }
        });

        Log($"dmp round trip: pairs={total} identical={identical} invalidUtf8Skipped={invalidUtf8} overBound({DmpRoundTripBound})Skipped={tooBigRoundTrip} defaultMakeOk={roundTripped} fineGrainedMakeOk={refRoundTripped} failures={failures.Count}");

        // Interop with the released package: it applies our patches, and we apply the patches it writes.
        string ids = Path.Combine(work, "ids.txt");
        File.WriteAllLines(ids, interop.Order(StringComparer.Ordinal));
        string script = ScriptPath();
        (int exit, string output) = Run(Python!, [script, work, ids], work, 3_000_000);
        string version = output.Split('\n').FirstOrDefault(l => l.StartsWith("version", StringComparison.Ordinal)) ?? "version ?";
        Log($"dmp interop oracle: {version}");
        if (exit != 0)
        {
            failures.Add($"python oracle exited {exit}: {Trim(output)}");
        }

        // The Python package counts offsets in code points and this library in UTF-16 units (as the JavaScript, Java and C# ports do),
        // so patch offsets differ after the first character outside the BMP. Those pairs are reported, not gated.
        var astralSet = new HashSet<string>(astral, StringComparer.Ordinal);
        int astralOursOk = 0;
        int astralTheirsOk = 0;
        int theirsApplied = 0;
        int oursApplied = 0;
        int oursDefaultApplied = 0;
        foreach (string id in interop.Where(i => !astralSet.Contains(i)).Concat(interop.Where(astralSet.Contains)))
        {
            bool isAstral = astralSet.Contains(id);
            string dir = Path.Combine(work, id);
            string? status = File.Exists(Path.Combine(dir, "status.txt")) ? File.ReadAllText(Path.Combine(dir, "status.txt")).Trim() : null;
            bool oursOk = status is not null && status.StartsWith("ours_ok ours_default_ok", StringComparison.Ordinal);
            if (isAstral)
            {
                astralOursOk += oursOk ? 1 : 0;
            }
            else if (!oursOk)
            {
                failures.Add($"{id}: oracle applying our patches: {status ?? "no status"}");
            }
            else
            {
                oursApplied++;
                oursDefaultApplied++;
            }

            string o = strict.GetString(File.ReadAllBytes(Path.Combine(dir, "old.txt")));
            string n = strict.GetString(File.ReadAllBytes(Path.Combine(dir, "new.txt")));
            string theirs = strict.GetString(File.ReadAllBytes(Path.Combine(dir, "theirs.patch")));
            if (!DmpPatcher.TryFromText(theirs, out IReadOnlyList<DmpPatch>? parsed, out PatchParseError err))
            {
                if (isAstral)
                {
                    continue;
                }

                failures.Add($"{id}: we cannot parse the package's patch text: {err.Message} line {err.Line}");
                continue;
            }

            DmpApplyResult res = new DmpPatcher().Apply(parsed, o);
            bool theirsOk = res.Success && res.Text == n;
            if (isAstral)
            {
                astralTheirsOk += theirsOk ? 1 : 0;
            }
            else if (!theirsOk)
            {
                failures.Add($"{id}: we apply the package's patch to a different result (success={res.Success})");
            }
            else
            {
                theirsApplied++;
            }
        }

        Log($"dmp interop: astralPairsReportedOnly={astralSet.Count} (oracle applies ours: {astralOursOk}, we apply theirs: {astralTheirsOk})");
        Log($"dmp interop: gatedPairs={interop.Count - astralSet.Count} oracleAppliesOursFine={oursApplied} oracleAppliesOursDefault={oursDefaultApplied} weApplyTheirs={theirsApplied} failures={failures.Count}");
        foreach (string f in failures.Take(25))
        {
            Log("  " + f);
        }

        Assert.Empty(failures);
    }
}

/// <summary>Line-then-character reference diffs for large texts (the reference diff alone is quadratic in the edit distance).</summary>
internal static class ReferenceDmpLines
{
    public static List<DmpDiff> Diffs(string a, string b)
    {
        List<string> la = PatchLines.Split(a);
        List<string> lb = PatchLines.Split(b);
        EditScript script = ReferenceDiff.Compute(la, lb);
        var result = new List<DmpDiff>();
        ReadOnlySpan<Edit> edits = script.Edits;
        for (int i = 0; i < edits.Length; i++)
        {
            Edit e = edits[i];
            switch (e.Kind)
            {
                case EditKind.Equal:
                    Add(result, DmpOperation.Equal, string.Concat(la.GetRange(e.OldStart, e.OldLength)));
                    break;
                case EditKind.Delete when i + 1 < edits.Length && edits[i + 1].Kind == EditKind.Insert:
                    string da = string.Concat(la.GetRange(e.OldStart, e.OldLength));
                    string db = string.Concat(lb.GetRange(edits[i + 1].NewStart, edits[i + 1].NewLength));
                    if (da.Length * (long)db.Length > 4_000_000)
                    {
                        Add(result, DmpOperation.Delete, da);
                        Add(result, DmpOperation.Insert, db);
                    }
                    else
                    {
                        foreach (DmpDiff d in ReferenceDmp.Diffs(da, db))
                        {
                            Add(result, d.Operation, d.Text);
                        }
                    }

                    i++;
                    break;
                case EditKind.Delete:
                    Add(result, DmpOperation.Delete, string.Concat(la.GetRange(e.OldStart, e.OldLength)));
                    break;
                default:
                    Add(result, DmpOperation.Insert, string.Concat(lb.GetRange(e.NewStart, e.NewLength)));
                    break;
            }
        }

        return result;
    }

    private static void Add(List<DmpDiff> list, DmpOperation op, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (list.Count > 0 && list[^1].Operation == op)
        {
            list[^1] = new DmpDiff(op, list[^1].Text + text);
        }
        else
        {
            list.Add(new DmpDiff(op, text));
        }
    }
}
