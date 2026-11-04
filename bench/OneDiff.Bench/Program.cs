using BenchmarkDotNet.Running;

namespace OneDiff.Bench;

/// <summary>
/// Entry point. With no special command it is BenchmarkDotNet's switcher (use <c>--filter</c> and <c>--artifacts</c>). Extra commands:
/// <c>info OUT.json</c>, <c>peak-all OUT.json</c> and <c>report RESULTS_DIR [--peak F] [--info F] [--env F] --out FILE</c>.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "info":
                return Workloads.Info(args[1]);
            case "peak-all":
                return Workloads.PeakAll(args[1]);
            case "peak-one":
                return Workloads.PeakOne(args[1], args[2], args[3]);
            case "report":
                {
                    string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
                    return ResultsTable.Write(args[1], Opt("--peak"), Opt("--info"), Opt("--env"), Opt("--out") ?? "results-table.md");
                }

            default:
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
                return 0;
        }
    }
}
