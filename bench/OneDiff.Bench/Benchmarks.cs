using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using DiffMatchPatch;
using DmpDiff = DiffMatchPatch.Diff;
using OneDiff.Tokenization;

namespace OneDiff.Bench;

/// <summary>
/// One job for every row: workstation non-concurrent GC and a single thread of work. <c>ONEDIFF_BENCH_MODE=smoke</c> trims
/// iterations to prove the pipeline end to end (the numbers are indicative); anything else uses BenchmarkDotNet's defaults.
/// <c>ONEDIFF_BENCH_TOOLCHAIN=inprocess</c> runs inside this process instead of a freshly built child per benchmark.
/// </summary>
public sealed class BenchConfig : ManualConfig
{
    public BenchConfig()
    {
        Job job = Job.Default.WithGcServer(false).WithGcConcurrent(false).WithGcForce(false);
        if (Environment.GetEnvironmentVariable("ONEDIFF_BENCH_MODE") == "smoke")
        {
            job = job.WithLaunchCount(1).WithWarmupCount(1).WithIterationCount(3).WithId("smoke");
        }

        if (Environment.GetEnvironmentVariable("ONEDIFF_BENCH_TOOLCHAIN") == "inprocess")
        {
            job = job.WithToolchain(InProcessNoEmitToolchain.Instance);
        }

        AddJob(job);
        AddDiagnoser(MemoryDiagnoser.Default);
        AddExporter(JsonExporter.Full);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddFilter(new AvailabilityFilter());
        WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }
}

/// <summary>Line diffs of code: ours (per algorithm) against DiffPlex and diff-match-patch's line mode.</summary>
[Config(typeof(BenchConfig))]
[BenchmarkCategory("lines")]
[MemoryDiagnoser]
public class LineDiffBench
{
    private string a = "";
    private string b = "";
    private diff_match_patch dmp = Impl.NewDmp();

    [Params(Sizes.Small, Sizes.Medium, Sizes.Large)]
    public string Size { get; set; } = Sizes.Small;

    [GlobalSetup]
    public void Setup() => (a, b) = LoadPair(Inputs.SourceFor("lines"), Size);

    internal static (string, string) LoadPair(string source, string size)
    {
        Pair p = Inputs.Pick(source, size);
        return (p.Old, p.New);
    }

    [Benchmark] public int OursMyers() => Impl.OursText(a, b, TokenKind.Line, DiffAlgorithm.Myers);

    [Benchmark] public int OursPatience() => Impl.OursText(a, b, TokenKind.Line, DiffAlgorithm.Patience);

    [Benchmark] public int OursHistogram() => Impl.OursText(a, b, TokenKind.Line, DiffAlgorithm.Histogram);

    [Benchmark] public int DiffPlexLines() => Impl.DiffPlexLines(a, b);

    [Benchmark] public int DmpLines() => Impl.DmpLines(dmp, a, b);
}

/// <summary>Word diffs of prose: ours against DiffPlex's word differ. diff-match-patch has no word mode.</summary>
[Config(typeof(BenchConfig))]
[BenchmarkCategory("words")]
[MemoryDiagnoser]
public class WordDiffBench
{
    private string a = "";
    private string b = "";

    [Params(Sizes.Small, Sizes.Medium, Sizes.Large)]
    public string Size { get; set; } = Sizes.Small;

    [GlobalSetup]
    public void Setup() => (a, b) = LineDiffBench.LoadPair(Inputs.SourceFor("words"), Size);

    [Benchmark] public int OursMyers() => Impl.OursText(a, b, TokenKind.Word, DiffAlgorithm.Myers);

    [Benchmark] public int OursPatience() => Impl.OursText(a, b, TokenKind.Word, DiffAlgorithm.Patience);

    [Benchmark] public int OursHistogram() => Impl.OursText(a, b, TokenKind.Word, DiffAlgorithm.Histogram);

    [Benchmark] public int DiffPlexWords() => Impl.DiffPlexWords(a, b);
}

/// <summary>
/// Character diffs of prose: ours against DiffPlex and diff-match-patch. Large inputs are left out because a character-level edit
/// graph of that size is not a realistic use and the comparators' memory grows with the number of edits.
/// </summary>
[Config(typeof(BenchConfig))]
[BenchmarkCategory("chars")]
[MemoryDiagnoser]
public class CharDiffBench
{
    private string a = "";
    private string b = "";
    private diff_match_patch dmp = Impl.NewDmp();

    [Params(Sizes.Small, Sizes.Medium)]
    public string Size { get; set; } = Sizes.Small;

    [GlobalSetup]
    public void Setup() => (a, b) = LineDiffBench.LoadPair(Inputs.SourceFor("chars"), Size);

    [Benchmark] public int OursMyers() => Impl.OursText(a, b, TokenKind.Character, DiffAlgorithm.Myers);

    [Benchmark] public int OursPatience() => Impl.OursText(a, b, TokenKind.Character, DiffAlgorithm.Patience);

    [Benchmark] public int OursHistogram() => Impl.OursText(a, b, TokenKind.Character, DiffAlgorithm.Histogram);

    [Benchmark] public int DiffPlexChars() => Impl.DiffPlexChars(a, b);

    [Benchmark] public int DmpChars() => Impl.DmpChars(dmp, a, b);
}

/// <summary>
/// Semantic and efficiency cleanup of a character diff, against diff-match-patch's. Each call starts from a fresh copy of the same
/// diff (cleanup edits in place), so the copy is part of both sides' timed work.
/// </summary>
[Config(typeof(BenchConfig))]
[BenchmarkCategory("cleanup")]
[MemoryDiagnoser]
public class CleanupBench
{
    private List<DmpDiff> dmpDiffs = [];
    private diff_match_patch dmp = Impl.NewDmp();

    [Params(Sizes.Small, Sizes.Medium)]
    public string Size { get; set; } = Sizes.Small;

    [GlobalSetup]
    public void Setup()
    {
        (string a, string b) = LineDiffBench.LoadPair(Inputs.SourceFor("chars"), Size);
        dmpDiffs = dmp.diff_main(a, b, false);

        // Library side, once cleanup exists: Diff.Characters(a, b) is the input and each call cleans a copy of it.
    }

    [Benchmark] public int DmpSemantic()
    {
        List<DmpDiff> copy = Impl.Clone(dmpDiffs);
        dmp.diff_cleanupSemantic(copy);
        return copy.Count;
    }

    [Benchmark] public int DmpEfficiency()
    {
        List<DmpDiff> copy = Impl.Clone(dmpDiffs);
        dmp.diff_cleanupEfficiency(copy);
        return copy.Count;
    }

    [Benchmark, Needs(Feature.Cleanup)] public int OursSemantic() => throw new NotSupportedException("the library's cleanup is not wired into this row yet");

    [Benchmark, Needs(Feature.Cleanup)] public int OursEfficiency() => throw new NotSupportedException("the library's cleanup is not wired into this row yet");
}

/// <summary>Patch make, toText, fromText and apply against diff-match-patch's.</summary>
[Config(typeof(BenchConfig))]
[BenchmarkCategory("patch")]
[MemoryDiagnoser]
public class PatchBench
{
    private string a = "";
    private string b = "";
    private string patchText = "";
    private List<Patch> patches = [];
    private diff_match_patch dmp = Impl.NewDmp();

    [Params(Sizes.Small, Sizes.Medium)]
    public string Size { get; set; } = Sizes.Small;

    [GlobalSetup]
    public void Setup()
    {
        (a, b) = LineDiffBench.LoadPair(Inputs.SourceFor("patch"), Size);
        patches = dmp.patch_make(a, b);
        patchText = dmp.patch_toText(patches);

        // Library side, once patches exist: build the same four inputs with the library here.
    }

    [Benchmark] public int DmpMake() => dmp.patch_make(a, b).Count;

    [Benchmark] public int DmpToText() => dmp.patch_toText(patches).Length;

    [Benchmark] public int DmpFromText() => dmp.patch_fromText(patchText).Count;

    [Benchmark] public int DmpApply() => ((string)dmp.patch_apply(patches, a)[0]).Length;

    [Benchmark, Needs(Feature.Patches)] public int OursMake() => throw new NotSupportedException("the library's patches are not wired into this row yet");

    [Benchmark, Needs(Feature.Patches)] public int OursToText() => throw new NotSupportedException("the library's patches are not wired into this row yet");

    [Benchmark, Needs(Feature.Patches)] public int OursFromText() => throw new NotSupportedException("the library's patches are not wired into this row yet");

    [Benchmark, Needs(Feature.Patches)] public int OursApply() => throw new NotSupportedException("the library's patches are not wired into this row yet");
}
