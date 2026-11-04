using BenchmarkDotNet.Running;

namespace OneDiff.Bench;

/// <summary>Entry point for the benchmark suites; run with <c>--filter</c> to pick benchmarks.</summary>
internal static class Program
{
    private static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
