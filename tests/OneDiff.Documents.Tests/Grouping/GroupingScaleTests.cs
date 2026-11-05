using System.Diagnostics;
using System.Globalization;
using System.Text;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Tests.Grouping;

/// <summary>
/// An indicative speed row: a 3,000-paragraph contract with 300 edits, compared end to end. The test asserts the result is complete
/// and writes the stage times and peak memory where <c>ONEDIFF_SCALE_OUT</c> says; it asserts no time, because timings from a
/// shared machine are not results.
/// </summary>
public class GroupingScaleTests
{
    private static readonly string[] Vocabulary =
    [
        "the", "Supplier", "Buyer", "shall", "deliver", "goods", "within", "payment", "invoice", "terminate", "notice", "breach", "of",
        "and", "or", "without", "cause", "agreement", "party", "liable", "damages", "confidential", "information", "governing", "law",
        "warranty", "remedy", "indemnify", "assign", "consent", "written", "receipt", "reasonable", "applicable", "obligation",
    ];

    /// <summary>A 3,000-paragraph contract and a copy with 300 edits.</summary>
    private static (CompareDocument Old, CompareDocument New) Contract()
    {
        var r = new Random(20261104);
        var oldParas = new List<string>();
        for (int i = 0; i < 3000; i++)
        {
            var sb = new StringBuilder();
            for (int s = 0; s < 3; s++)
            {
                int n = r.Next(8, 16);
                for (int w = 0; w < n; w++)
                {
                    sb.Append(w == 0 ? "" : " ").Append(r.Next(25) == 0 ? (r.Next(5, 90) + " days") : Vocabulary[r.Next(Vocabulary.Length)]);
                }

                sb.Append(s < 2 ? ". " : ".");
            }

            oldParas.Add($"{i + 1}. {sb}");
        }

        List<string> newParas = [.. oldParas];
        for (int k = 0; k < 300; k++)
        {
            int at = r.Next(newParas.Count);
            string p = newParas[at];
            newParas[at] = (k % 4) switch
            {
                0 => p.Replace("the", "any", StringComparison.Ordinal),
                1 => p + " A further sentence is added here by the Buyer.",
                2 => p.Replace(" days", " business days", StringComparison.Ordinal) + " Extra.",
                _ => p.Replace(".", ";", StringComparison.Ordinal) + " Also.",
            };
        }

        return (Helpers.Text([.. oldParas]), Helpers.Text([.. newParas]));
    }

    /// <summary>
    /// The contract as numbered clauses with quoted definitions, and the new version with a clause inserted near the top, so every
    /// label after it moves and the references to the moved clauses are rewritten: the worst case for the cascade pass.
    /// </summary>
    private static (CompareDocument Old, CompareDocument New) NumberedContract()
    {
        (CompareDocument plain, CompareDocument edited) = Contract();
        CompareDocument Number(CompareDocument d, bool insert)
        {
            var b = new CompareDocumentBuilder(DocumentKind.Flowing);
            int label = 0;
            foreach (Node n in d.Nodes)
            {
                if (insert && label == 10)
                {
                    label++;
                    b.Add(NodeKind.ListItem, new NodeContent { Text = "A clause added by the Buyer.", Attributes = FormatSet.Of(FormatKeys.ListLabel, "1." + label) });
                }

                label++;
                string text = label % 40 == 0 ? $"\"Term{label}\" means the thing described in Section 1.{label + (insert ? 0 : 1)}." : n.Text;
                b.Add(NodeKind.ListItem, new NodeContent { Text = text, Attributes = FormatSet.Of(FormatKeys.ListLabel, "1." + label) });
            }

            return b.Build();
        }

        return (Number(plain, false), Number(edited, true));
    }

    /// <summary>
    /// What the semantic passes (cascade and rename grouping, typing, the substantive flag) add to a whole comparison, as medians of
    /// interleaved runs with the passes off and on, on the contract above and on its renumbered variant. Written where
    /// <c>ONEDIFF_SCALE_OUT</c> says; no time is asserted.
    /// </summary>
    [Fact]
    public void SemanticPassesCostLittleOnTheScaleContract()
    {
        CompareSettings off = CompareSettings.Legal with { CollapseCascades = false, TypedChanges = false };
        CompareSettings on = CompareSettings.Legal;
        foreach ((string name, (CompareDocument a, CompareDocument b)) in new[] { ("flat", Contract()), ("numbered", NumberedContract()) })
        {
            var tOff = new List<double>();
            var tOn = new List<double>();
            int changes = 0, groups = 0;
            for (int i = 0; i < 12; i++)
            {
                foreach (bool semantic in i % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    var sw = Stopwatch.StartNew();
                    Comparison c = Comparison.Of(a, b, semantic ? on : off);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    Assert.True(c.Verification.Passed);
                    Assert.Equal(0, c.Verification.FallbackLevel);
                    if (i >= 2)
                    {
                        (semantic ? tOn : tOff).Add(ms);
                    }

                    if (semantic)
                    {
                        changes = c.Changes.Length;
                        groups = c.Groups.Count(g => g.Kind != GroupKind.Region);
                    }
                }
            }

            tOff.Sort();
            tOn.Sort();
            double mOff = tOff[tOff.Count / 2], mOn = tOn[tOn.Count / 2];
            string? outFile = Environment.GetEnvironmentVariable("ONEDIFF_SCALE_OUT");
            if (!string.IsNullOrEmpty(outFile))
            {
                File.AppendAllText(outFile, string.Create(CultureInfo.InvariantCulture, $"semantic passes {name}: changes={changes} semantic-groups={groups} off={mOff:F0}ms on={mOn:F0}ms ratio={mOn / mOff:F3} cores={Environment.ProcessorCount}\n"));
            }
        }
    }

    [Fact]
    public void ThreeThousandBlocksWithThreeHundredChangesAreComplete()
    {
        (CompareDocument a, CompareDocument b) = Contract();
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(true);
        var total = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        Alignment al = Stages.Align(a, b);
        double tAlign = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        PairDiffs d = Stages.DiffPairs(al);
        double tDiff = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        ChangeSet g = Stages.Group(d);
        double tGroup = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        ChangeSet cl = Stages.Classify(g);
        double tClassify = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        Comparison c = Stages.Verify(cl);
        double tVerify = sw.Elapsed.TotalMilliseconds;
        double tTotal = total.Elapsed.TotalMilliseconds;
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Helpers.Clean(c);
        Assert.NotEmpty(c.Changes);

        string? outFile = Environment.GetEnvironmentVariable("ONEDIFF_SCALE_OUT");
        if (!string.IsNullOrEmpty(outFile))
        {
            using Process self = Process.GetCurrentProcess();
            string line = string.Create(CultureInfo.InvariantCulture,
                $"blocks=3000 edits=300 changes={c.Changes.Length} align={tAlign:F0}ms diff={tDiff:F0}ms group={tGroup:F0}ms classify={tClassify:F0}ms verify={tVerify:F0}ms total={tTotal:F0}ms allocated={allocated / 1048576}MB peakWorkingSet={self.PeakWorkingSet64 / 1048576}MB cores={Environment.ProcessorCount}");
            File.AppendAllText(outFile, line + "\n");
        }
    }
}
