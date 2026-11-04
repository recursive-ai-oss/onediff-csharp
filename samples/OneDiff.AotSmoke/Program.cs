using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;
using OneDiff.Tokenization;

namespace OneDiff.AotSmoke;

/// <summary>Runs the core under NativeAOT: a diff in every algorithm and tokenizer, checked by replaying the script.</summary>
internal static class Program
{
    private static int Main()
    {
        const string oldText = "the quick brown fox\njumps over\nthe lazy dog\n";
        const string newText = "the quick red fox\njumps over\nthe very lazy dog\n";
        int failures = 0;

        foreach (DiffAlgorithm algorithm in new[] { DiffAlgorithm.Myers, DiffAlgorithm.Patience, DiffAlgorithm.Histogram })
        {
            foreach (TokenKind kind in new[] { TokenKind.Line, TokenKind.Word, TokenKind.Character, TokenKind.Grapheme })
            {
                TextDiff diff = Diff.Text(oldText, newText, kind, algorithm);
                if (diff.Script.IsIdentical || diff.Script.OldLength != diff.OldTokens.Length || diff.Script.NewLength != diff.NewTokens.Length)
                {
                    Console.Error.WriteLine($"{algorithm}/{kind}: unexpected script");
                    failures++;
                }
            }
        }

        string[] oldLines = ["a", "b", "c"];
        string[] newLines = ["a", "c", "d"];
        EditScript script = Diff.Sequences<string>(oldLines, newLines);
        if (!script.Matches<string>(oldLines, newLines) || !script.Apply<string>(oldLines, newLines).AsSpan().SequenceEqual(newLines))
        {
            Console.Error.WriteLine("sequence round trip failed");
            failures++;
        }

        failures += CheckDocuments();

        Console.Out.WriteLine(failures == 0 ? "ok" : "FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Compares two plain-text documents through every stage and round-trips the result through JSON.</summary>
    private static int CheckDocuments()
    {
        CompareDocument a = CompareDocument.FromText("The supplier shall deliver within thirty days.\n\nPayment is due in sixty days.\n\nHe said \u201Chi\u201D.");
        CompareDocument b = CompareDocument.FromText("The supplier shall deliver within forty days.\n\nHe said \"hi\".\n\nPayment is due in sixty days.");
        int failures = 0;
        foreach (CompareSettings settings in new[] { CompareSettings.Legal, CompareSettings.Strict, CompareSettings.TextOnly })
        {
            Comparison c = Comparison.Of(a, b, settings with { ThrowOnFailure = false });
            if (c.Verification.FallbackLevel != 0 || !c.Check().IsEmpty || c.Changes.IsEmpty)
            {
                Console.Error.WriteLine($"{settings.Name}: comparison failed verification");
                failures++;
            }

            byte[] json = ComparisonJson.Write(c);
            Comparison back = ComparisonJson.Read(json);
            if (!ComparisonJson.Write(back).AsSpan().SequenceEqual(json) || !back.Check().IsEmpty)
            {
                Console.Error.WriteLine($"{settings.Name}: JSON round trip failed");
                failures++;
            }
        }

        return failures;
    }
}
