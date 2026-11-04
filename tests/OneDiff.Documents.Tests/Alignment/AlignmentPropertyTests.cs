using System.Globalization;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Tests.Alignments;

public class AlignmentPropertyTests
{
    private static int Cases =>
        int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_ALIGN_CASES"), CultureInfo.InvariantCulture, out int n) ? n : 120;

    private static int FirstSeed =>
        int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_ALIGN_SEED"), CultureInfo.InvariantCulture, out int n) ? n : 1;

    // Larger documents than the invertibility generator makes, with the kinds of edit the aligner is built for: moves, splits,
    // merges, near-duplicate clauses and table row and column edits. Every comparison must verify with no fallback.
    [Fact]
    public void LabelledAndTablePairsVerifyWithoutFallback()
    {
        int first = FirstSeed;
        for (int seed = first; seed < first + Cases; seed++)
        {
            var r = new Random(seed);
            CompareSettings settings = (seed % 3) switch
            {
                0 => CompareSettings.Legal,
                1 => CompareSettings.Strict with { ThrowOnFailure = false },
                _ => CompareSettings.TextOnly,
            };
            CompareDocument a, b;
            if (seed % 4 == 3)
            {
                int rows = r.Next(2, 8), cols = r.Next(1, 5);
                string[][] grid = Enumerable.Range(0, rows).Select(i => Enumerable.Range(0, cols).Select(j => Fx.Para((seed * 50) + (i * 7) + j, r.Next(0, 6))).ToArray()).ToArray();
                List<string[]> next = grid.Select(row => row.Select(c => r.Next(5) == 0 ? c + " edit" : c).ToArray()).ToList();
                if (next.Count > 1 && r.Next(2) == 0)
                {
                    next.RemoveAt(r.Next(next.Count));
                }

                next.Insert(r.Next(next.Count + 1), Enumerable.Range(0, cols).Select(j => Fx.Para((seed * 50) + 40 + j, 3)).ToArray());
                if (r.Next(2) == 0)
                {
                    int at = r.Next(cols + 1);
                    next = next.Select(row => row.Take(at).Append(Fx.Para(seed + 9000, 2)).Concat(row.Skip(at)).ToArray()).ToList();
                }

                a = Fx.TableDoc(grid, Fx.Para(seed, 6));
                b = Fx.TableDoc([.. next], Fx.Para(seed, 6));
            }
            else
            {
                (a, b, _, _) = LabelledPairs.Make(seed, r.Next(6, 70), r.Next(2) == 0, 0.2 + (r.NextDouble() * 0.8));
            }

            Comparison c = Comparison.Of(a, b, settings);
            string context = $"seed {seed} settings {settings.Name}";
            Assert.True(c.Verification.FallbackLevel == 0, context + " fell back: " + string.Join(" | ", c.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            Assert.True(c.Check().IsEmpty, context + " failed re-check");
        }
    }
}
