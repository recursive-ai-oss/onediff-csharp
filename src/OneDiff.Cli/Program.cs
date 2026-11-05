using OneDiff.Documents;
using OneDiff.Ocr;
using OneDiff.Patching;

namespace OneDiff.Cli;

/// <summary>The <c>onediff</c> command line: dispatch, error reporting and the process streams.</summary>
internal static class Program
{
    private const string Usage = """
        usage: onediff <command> [arguments]
          diff    compare two texts (unified, terminal, Markdown or diff-match-patch output)
          patch   apply a unified or diff-match-patch patch
          merge   three-way merge with conflict markers
          compare compare two documents (text, PDF or scan) and write a redline (HTML, JSON or PDF)
          combine combine reviewers' copies of one original (change report in HTML, or JSON)
          chain   compare a chain of versions step by step (change report in HTML, or JSON)
          --version   print the version
        "onediff <command> --help" lists a command's options.
        """;

    private static int Main(string[] args)
    {
        using Stream stdin = Console.OpenStandardInput();
        using Stream stdout = Console.OpenStandardOutput();
        using Stream stderr = Console.OpenStandardError();
        return Run(args, new CliIo(stdin, stdout, stderr));
    }

    /// <summary>Runs one command line against the given streams and returns the process exit status.</summary>
    internal static int Run(string[] args, CliIo io)
    {
        if (args.Length == 0)
        {
            io.Info(Usage);
            return 2;
        }

        // PDF inputs (digital by their text layer, scanned by OCR) are read, and .pdf output is written, once the sources and forms are
        // registered (idempotent). The scan source is asked before the PDF source whatever the order.
        OcrRedline.Register();

        string command = args[0];
        if (command is "--version" or "version")
        {
            io.Write(null, TextEncoding.Utf8Bytes("onediff " + Comparison.LibraryVersion + "\n"));
            return 0;
        }

        if (command is "-h" or "--help" or "help")
        {
            io.Write(null, TextEncoding.Utf8Bytes(Usage + "\n"));
            return 0;
        }

        // Each command has its own "trouble" status: 2 for diff and patch, 255 for merge (git merge-file's negative value).
        int trouble = command == "merge" ? MergeCommand.ErrorStatus : 2;
        try
        {
            string[] rest = args[1..];
            return command switch
            {
                "diff" => DiffCommand.Run(rest, io),
                "patch" => PatchCommand.Run(rest, io),
                "merge" => MergeCommand.Run(rest, io),
                "compare" => CompareCommand.Run(rest, io),
                "combine" or "chain" => CombineCommand.Run(command, rest, io),
                _ => throw new CliException($"unknown command '{command}'"),
            };
        }
        catch (CliException e)
        {
            io.Info($"onediff {command}: {e.Message}");
            return trouble;
        }
        catch (PatchFormatException e)
        {
            io.Info($"onediff {command}: malformed patch: {e.Message}");
            return trouble;
        }
    }
}
