using OneDiff.Output;
using OneDiff.Tokenization;

namespace OneDiff.Cli;

/// <summary>
/// <c>onediff diff [options] OLD NEW</c>. Each form is one call to the library; this file only reads inputs and picks the call.
/// Exit codes are those of GNU diff (GNU diffutils manual, "Invoking diff"): 0 when the inputs are the same, 1 when they differ,
/// 2 when something went wrong.
/// </summary>
internal static class DiffCommand
{
    public const string Usage = """
        usage: onediff diff [options] OLD NEW        ("-" reads standard input)
          -u, --unified            unified diff (the default)
              --format FORMAT      unified | terminal | markdown | dmp (diff-match-patch patch text)
              --terminal           same as --format terminal (ANSI colour; see --no-color)
              --markdown           same as --format markdown
              --dmp                same as --format dmp
          -U, --context N          lines of context around each change (3)
          -A, --algorithm NAME     myers | patience | histogram (myers)
          -g, --granularity NAME   line | word | char (line); word and char need --markdown and give an inline <del>/<ins> text
              --no-color           terminal format without colour
              --label NAME         name for the old, then (given again) the new side; the file names by default
        exit status: 0 same, 1 different, 2 trouble
        """;

    private static readonly OptionSet Options = new OptionSet()
        .Flag("unified", "-u", "--unified")
        .Value("format", "--format")
        .Flag("terminal", "--terminal")
        .Flag("markdown", "--markdown")
        .Flag("dmp", "--dmp")
        .Value("context", "-U", "--context")
        .Value("algorithm", "-A", "--algorithm")
        .Value("granularity", "-g", "--granularity")
        .Flag("no-color", "--no-color")
        .Value("label", "--label")
        .Flag("help", "-h", "--help");

    public static int Run(string[] args, CliIo io)
    {
        ParsedArgs p = Options.Parse(args);
        if (p.Has("help"))
        {
            io.Write(null, TextEncoding.Utf8Bytes(Usage + "\n"));
            return 0;
        }

        if (p.Operands.Count != 2)
        {
            throw new CliException("expected two inputs: OLD NEW");
        }

        string oldPath = p.Operands[0], newPath = p.Operands[1];
        if (oldPath == "-" && newPath == "-")
        {
            throw new CliException("only one input can be standard input");
        }

        string format = p.GetChoice("format", "--format", "unified",
            ("unified", "unified"), ("terminal", "terminal"), ("markdown", "markdown"), ("dmp", "dmp"));
        foreach (string flag in new[] { "terminal", "markdown", "dmp" })
        {
            if (p.Has(flag))
            {
                format = flag;
            }
        }

        DiffAlgorithm algorithm = p.GetChoice("algorithm", "--algorithm", DiffAlgorithm.Myers,
            ("myers", DiffAlgorithm.Myers), ("patience", DiffAlgorithm.Patience), ("histogram", DiffAlgorithm.Histogram));
        TokenKind kind = p.GetChoice("granularity", "--granularity", TokenKind.Line,
            ("line", TokenKind.Line), ("word", TokenKind.Word), ("char", TokenKind.Character));
        int context = p.GetInt("context", "--context", 3);
        IReadOnlyList<string> labels = p.All("label");
        if (labels.Count > 2)
        {
            throw new CliException("--label may be given at most twice");
        }

        if (kind != TokenKind.Line && format != "markdown")
        {
            throw new CliException("--granularity word and char are available with --markdown only");
        }

        if (format == "dmp" && (p.Has("algorithm") || p.Has("context")))
        {
            throw new CliException("--algorithm and --context do not apply to --format dmp");
        }

        if (format == "markdown" && kind != TokenKind.Line && p.Has("context"))
        {
            throw new CliException("--context applies to line diffs only");
        }

        byte[] oldBytes = io.Read(oldPath), newBytes = io.Read(newPath);
        if (oldBytes.AsSpan().SequenceEqual(newBytes))
        {
            return 0;
        }

        (TextEncoding encoding, string[] texts) = TextEncoding.Decode(oldBytes, newBytes);
        if (TextEncoding.IsBinary(oldBytes) || TextEncoding.IsBinary(newBytes))
        {
            io.Write(null, encoding.Bytes($"Binary files {oldPath} and {newPath} differ\n"));
            return 1;
        }

        string oldName = labels.Count > 0 ? labels[0] : oldPath;
        string newName = labels.Count > 1 ? labels[1] : newPath;
        string oldText = texts[0], newText = texts[1];

        string output;
        switch (format)
        {
            case "unified":
                output = Diff.Unified(oldText, newText, oldName, newName, context, algorithm);
                break;
            case "terminal":
                output = new TerminalRenderer { Color = !p.Has("no-color"), ContextLines = context, OldName = oldName, NewName = newName }
                    .Render(Diff.Lines(oldText, newText, algorithm));
                break;
            case "markdown" when kind == TokenKind.Line:
                output = new MarkdownRenderer { ContextLines = context, OldName = oldName, NewName = newName }
                    .RenderLines(Diff.Lines(oldText, newText, algorithm));
                break;
            case "markdown":
                output = Diff.ToMarkdownInline(Diff.Text(oldText, newText, kind, algorithm));
                if (!output.EndsWith('\n'))
                {
                    output += "\n";
                }

                break;
            default:
                output = Diff.PatchText(oldText, newText);
                break;
        }

        io.Write(null, encoding.Bytes(output));
        return 1;
    }
}
