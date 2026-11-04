namespace OneDiff.Cli;

/// <summary>The <c>onediff</c> command line. The commands exist so scripts can be written against them; they do nothing yet.</summary>
internal static class Program
{
    private const int NotImplemented = 3;
    private const int Usage = 2;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.Out.WriteLine("usage: onediff <diff|patch|merge> [arguments]");
            return args.Length == 0 ? Usage : 0;
        }

        if (args[0] is "diff" or "patch" or "merge")
        {
            Console.Error.WriteLine($"onediff {args[0]}: not implemented yet");
            return NotImplemented;
        }

        Console.Error.WriteLine($"onediff: unknown command '{args[0]}'");
        return Usage;
    }
}
