using System.Globalization;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Tests;

public class InvertibilityPropertyTests
{
    private static int Cases =>
        int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_PROPERTY_CASES"), CultureInfo.InvariantCulture, out int n) ? n : 400;

    private static CompareSettings SettingsFor(int seed) => (seed % 3) switch
    {
        0 => CompareSettings.Legal,
        1 => CompareSettings.Strict with { ThrowOnFailure = false },
        _ => CompareSettings.TextOnly,
    };

    [Fact]
    public void RandomEditSequencesAlwaysVerifyWithoutFallback()
    {
        int cases = Cases, withChanges = 0, withMoves = 0, withSplitMerge = 0, withLedger = 0;
        int baseSeed = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_PROPERTY_SEED"), CultureInfo.InvariantCulture, out int s0) ? s0 : 0;
        for (int i = 0; i < cases; i++)
        {
            int seed = baseSeed + i;
            var r = new Random(seed);
            GNode a = DocumentGenerator.Random(r);
            GNode b = a.Clone();
            int edits = r.Next(0, 7);
            var log = new List<string>();
            for (int k = 0; k < edits; k++)
            {
                log.Add(DocumentGenerator.Mutate(b, r));
            }

            CompareDocument da = DocumentGenerator.Build(a), db = DocumentGenerator.BuildWithOpaque(b, r.Next(4) == 0 ? "ff" : "00");
            Comparison c = Comparison.Of(da, db, SettingsFor(seed));
            string context = $"seed {seed} edits [{string.Join(',', log)}] settings {SettingsFor(seed).Name}";
            Assert.True(c.Verification.FallbackLevel == 0, context + " fell back: " + string.Join(" | ", c.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            Assert.True(c.Check().IsEmpty, context + " failed re-check");
            if (!c.Changes.IsEmpty)
            {
                withChanges++;
            }

            if (c.Changes.Any(x => x.Kind == ChangeKind.Move))
            {
                withMoves++;
            }

            if (c.Changes.Any(x => x.Kind is ChangeKind.Split or ChangeKind.Merge))
            {
                withSplitMerge++;
            }

            if (c.Ledger.TotalCount > 0)
            {
                withLedger++;
            }
        }

        // The generator has to exercise the interesting paths, or the property proves little.
        if (cases >= 200)
        {
            Assert.True(withChanges > cases / 3, $"only {withChanges} of {cases} cases had changes");
            Assert.True(withMoves > 0, "no case produced a move");
            Assert.True(withSplitMerge > 0, "no case produced a split or merge");
            Assert.True(withLedger > 0, "no case produced a ledger entry");
        }
    }
}
