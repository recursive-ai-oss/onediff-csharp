namespace OneDiff.Fuzz;

/// <summary>Fuzz harness entry point. Targets are added with the parsers they exercise.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "comparison-json")
        {
            return ComparisonJsonFuzz.Campaign(args[1..]);
        }

        Console.Error.WriteLine("OneDiff.Fuzz targets: comparison-json [--seconds N] [--seed N] [--crashes DIR]");
        return 0;
    }
}
