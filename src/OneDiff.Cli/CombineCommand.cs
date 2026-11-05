using OneDiff.Documents;

namespace OneDiff.Cli;

/// <summary>
/// <c>onediff combine &lt;original&gt; &lt;copy&gt;... -o &lt;output&gt;</c> and <c>onediff chain &lt;v1&gt; &lt;v2&gt;... -o &lt;output&gt;</c>:
/// thin layers over <see cref="Redline.Combine"/> and <see cref="Redline.Chain"/>. The output is the change report (HTML) or the JSON form,
/// chosen by the extension of <c>-o</c>.
/// </summary>
internal static class CombineCommand
{

    private const string Help = """
        usage: onediff combine <original> <copy>... -o <output> [options]
               onediff chain <v1> <v2>... -o <output> [options]

        combine: compares every copy with the original; each change is attributed to the reviewers who made it, identical
        changes are merged, and conflicting changes are all shown and flagged. chain: compares each version with the next
        and the first with the last, every change attributed to its step. The output is the change report (.html) or the
        JSON form (.json), by the extension of <output>. Inputs are recognized by content.

        options:
          -o, --output <file>                    where to write (required)
          --settings <legal|strict|text>         what to show and hide (default legal)
          -h, --help                             show this text
        """;

    public static int Run(string command, string[] args, CliIo io)
    {
        var positional = new List<string>();
        string? output = null, settingsName = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    io.Write(null, TextEncoding.Utf8Bytes(Help + "\n"));
                    return 0;
                case "-o" or "--output" or "--settings":
                    if (i + 1 >= args.Length)
                    {
                        return Fail(io, command, $"{a} needs a value");
                    }

                    if (a == "--settings")
                    {
                        settingsName = args[++i];
                    }
                    else
                    {
                        output = args[++i];
                    }

                    break;
                default:
                    if (a.StartsWith('-') && a.Length > 1)
                    {
                        return Fail(io, command, $"unknown option '{a}'");
                    }

                    positional.Add(a);
                    break;
            }
        }

        if (output is null)
        {
            return Fail(io, command, "-o <output> is required");
        }

        if (positional.Count < 2)
        {
            return Fail(io, command, command == "combine" ? "expected <original> and at least one <copy>" : "expected at least two versions");
        }

        CompareSettings? settings = settingsName?.ToLowerInvariant() switch
        {
            null or "legal" => CompareSettings.Legal,
            "strict" => CompareSettings.Strict with { ThrowOnFailure = false },
            "text" or "textonly" => CompareSettings.TextOnly,
            _ => null,
        };
        if (settings is null)
        {
            return Fail(io, command, $"unknown settings '{settingsName}' (legal, strict, text)");
        }

        try
        {
            if (command == "combine")
            {
                Redline.Combine(positional[0], positional.Skip(1).ToList(), output, settings);
            }
            else
            {
                Redline.Chain(positional, output, settings);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail(io, command, ex.Message);
        }

        return 0;
    }

    private static int Fail(CliIo io, string command, string message)
    {
        io.Info($"onediff {command}: {message}");
        return 2;
    }
}
