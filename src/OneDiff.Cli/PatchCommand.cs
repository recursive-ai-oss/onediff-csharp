using System.Globalization;
using OneDiff.Patching;

namespace OneDiff.Cli;

/// <summary>
/// <c>onediff patch [options] ORIGINAL [PATCH]</c>. The patch format is recognised from its content: text that starts with a
/// <c>@@ -</c> line is diff-match-patch patch text, anything else is read as a unified diff. Exit codes follow GNU patch (GNU patch
/// manual, "Exit Status"): 0 when every hunk applied, 1 when some did not, 2 when something went wrong. Hunks that do apply are
/// still applied, as GNU patch does.
/// </summary>
internal static class PatchCommand
{
    public const string Usage = """
        usage: onediff patch [options] ORIGINAL [PATCH]   (PATCH, or -i FILE, defaults to standard input; "-" reads standard input)
          -i, --patch FILE         read the patch from FILE
          -o, --output FILE        write the patched text to FILE (standard output by default)
          -R, --reverse            undo the patch (unified patches)
          -F, --fuzz N             context lines that may be ignored at each end of a hunk (2; unified patches)
              --reject-file FILE   write the hunks that did not apply, as a patch, to FILE
        The patched text goes to standard output; progress and rejected hunks are reported on standard error.
        exit status: 0 applied, 1 some hunks rejected, 2 trouble
        """;

    private static readonly OptionSet Options = new OptionSet()
        .Value("patch", "-i", "--patch")
        .Value("output", "-o", "--output")
        .Flag("reverse", "-R", "--reverse")
        .Value("fuzz", "-F", "--fuzz")
        .Value("reject", "--reject-file")
        .Flag("help", "-h", "--help");

    public static int Run(string[] args, CliIo io)
    {
        ParsedArgs p = Options.Parse(args);
        if (p.Has("help"))
        {
            io.Write(null, TextEncoding.Utf8Bytes(Usage + "\n"));
            return 0;
        }

        string? patchPath = p.Get("patch");
        if (p.Operands.Count is 0 or > 2 || (patchPath is not null && p.Operands.Count > 1))
        {
            throw new CliException("expected ORIGINAL and optionally PATCH");
        }

        patchPath ??= p.Operands.Count == 2 ? p.Operands[1] : "-";
        string originalPath = p.Operands[0];
        if (originalPath == "-" && patchPath == "-")
        {
            throw new CliException("the original and the patch cannot both be standard input");
        }

        byte[] originalBytes = io.Read(originalPath), patchBytes = io.Read(patchPath);
        (TextEncoding encoding, string[] texts) = TextEncoding.Decode(originalBytes, patchBytes);
        if (TextEncoding.IsBinary(originalBytes))
        {
            throw new CliException($"{originalPath}: binary file");
        }

        string original = texts[0], patchText = texts[1];
        string? outputPath = p.Get("output");

        if (LooksLikeDmp(patchText))
        {
            if (p.Has("reverse") || p.Has("fuzz") || p.Has("reject"))
            {
                throw new CliException("--reverse, --fuzz and --reject-file apply to unified patches only");
            }

            DmpApplyResult dmp = Diff.ApplyPatchText(original, patchText);
            io.Write(outputPath, encoding.Bytes(dmp.Text));
            for (int i = 0; i < dmp.Patches.Count; i++)
            {
                DmpPatchResult r = dmp.Patches[i];
                io.Info(r.Applied
                    ? string.Create(CultureInfo.InvariantCulture, $"Patch #{i + 1} succeeded at offset {r.Location} (drift {r.Drift})")
                    : string.Create(CultureInfo.InvariantCulture, $"Patch #{i + 1} FAILED ({r.Outcome})"));
            }

            return dmp.Success ? 0 : 1;
        }

        UnifiedPatch patch = UnifiedPatch.Parse(patchText);
        if (patch.Files.Count > 1)
        {
            throw new CliException("the patch holds more than one file; split it first");
        }

        if (patch.Files.Count == 0)
        {
            io.Write(outputPath, encoding.Bytes(original));
            return 0;
        }

        var options = new UnifiedApplyOptions { MaxFuzz = p.GetInt("fuzz", "--fuzz", 2), Reverse = p.Has("reverse") };
        UnifiedApplyResult result = patch.Files[0].Apply(original, options);
        io.Write(outputPath, encoding.Bytes(result.Text));
        foreach (HunkResult h in result.Hunks)
        {
            int n = h.HunkIndex + 1;
            io.Info(h.Outcome switch
            {
                HunkOutcome.Applied => string.Create(CultureInfo.InvariantCulture, $"Hunk #{n} succeeded at {h.Line + 1}."),
                HunkOutcome.AppliedWithOffset => string.Create(CultureInfo.InvariantCulture, $"Hunk #{n} succeeded at {h.Line + 1} (offset {h.Offset} lines)."),
                HunkOutcome.AppliedWithFuzz => string.Create(CultureInfo.InvariantCulture, $"Hunk #{n} succeeded at {h.Line + 1} with fuzz {h.Fuzz} (offset {h.Offset} lines)."),
                _ => string.Create(CultureInfo.InvariantCulture, $"Hunk #{n} FAILED ({(h.Reason == HunkRejectReason.AlreadyApplied ? "already applied or patch reversed" : "context does not match")})."),
            });
        }

        if (result.Rejected is not null)
        {
            string? rejectPath = p.Get("reject");
            if (rejectPath is not null)
            {
                io.Write(rejectPath, encoding.Bytes(result.Rejected.ToString()));
            }

            return 1;
        }

        return 0;
    }

    private static bool LooksLikeDmp(string patchText)
    {
        foreach (string line in patchText.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            return line.StartsWith("@@ -", StringComparison.Ordinal);
        }

        return false;
    }
}
