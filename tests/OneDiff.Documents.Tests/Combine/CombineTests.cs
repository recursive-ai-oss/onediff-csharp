using System.Globalization;
using System.Text.Json;
using OneDiff.Documents.Combine;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Tests.Combine;

public class CombineTests
{
    private static CompareDocument P(params string[] paragraphs) => Helpers.Text(paragraphs);

    private static readonly string[] Base =
    [
        "The Supplier shall deliver the goods within 30 days of the order.",
        "Payment is due within 45 days of the invoice date.",
        "Either party may terminate this agreement on written notice.",
        "This agreement is governed by the laws of England.",
    ];

    private static CompareDocument With(params (int Index, string Text)[] edits)
    {
        string[] p = [.. Base];
        foreach ((int i, string t) in edits)
        {
            p[i] = t;
        }

        return P([.. p.Where(x => x.Length > 0)]);
    }

    private static Combination Combine(params CompareDocument[] copies)
    {
        Combination c = Combination.Of(P(Base), copies);
        Assert.True(c.Verification.Passed);
        Assert.Equal(0, c.Verification.FallbackLevel);
        Assert.Empty(c.Check());
        return c;
    }

    [Fact]
    public void IdenticalEditsAreMergedAndAttributedToBoth()
    {
        string edited = Base[0].Replace("30 days", "60 days", StringComparison.Ordinal);
        Combination c = Combine(With((0, edited)), With((0, edited)), With((2, "Either party may terminate this agreement on ninety days written notice.")));
        CombinedChange shared = Assert.Single(c.Changes, x => x.Reviewers.Length == 2);
        Assert.Equal([0, 1], shared.Reviewers);
        Assert.Equal(-1, shared.ConflictId);
        Assert.Contains(c.Changes, x => x.Reviewers.SequenceEqual([2]));
        Assert.Empty(c.Conflicts);
        Assert.All(shared.EditReviewers, e => Assert.Equal([0, 1], e));
    }

    [Fact]
    public void OverlappingDifferentEditsAreBothShownAndFlagged()
    {
        Combination c = Combine(
            With((0, Base[0].Replace("30 days", "60 days", StringComparison.Ordinal))),
            With((0, Base[0].Replace("30 days", "10 days", StringComparison.Ordinal))));
        Conflict k = Assert.Single(c.Conflicts);
        Assert.Equal([0, 1], k.Reviewers);
        Assert.Equal(2, k.ChangeIds.Length);
        Assert.All(k.ChangeIds, id => Assert.Equal(k.Id, c.Changes[id].ConflictId));
        // Neither version was dropped.
        Assert.Contains(c.Changes, x => x.Change.Edits.Any(e => e.NewText.Contains('6')));
        Assert.Contains(c.Changes, x => x.Change.Edits.Any(e => e.NewText.Contains('1')));
    }

    [Fact]
    public void EditsInDifferentPlacesDoNotConflict()
    {
        Combination c = Combine(
            With((0, Base[0].Replace("30 days", "60 days", StringComparison.Ordinal))),
            With((0, Base[0].Replace("goods", "services", StringComparison.Ordinal))));
        Assert.Empty(c.Conflicts);
        Assert.Equal(2, c.Changes.Length);
    }

    [Fact]
    public void EditAgainstDeleteConflicts()
    {
        Combination c = Combine(With((1, "")), With((1, "Payment is due within 60 days of the invoice date.")));
        Conflict k = Assert.Single(c.Conflicts);
        Assert.Equal(ConflictKind.EditAndDelete, k.Kind);
    }

    [Fact]
    public void ProjectionOntoAReviewerIsThatReviewersChanges()
    {
        Combination c = Combine(
            With((0, Base[0].Replace("30 days", "60 days", StringComparison.Ordinal)), (3, "")),
            With((0, Base[0].Replace("30 days", "60 days", StringComparison.Ordinal))));
        for (int k = 0; k < c.Reviewers.Length; k++)
        {
            var ids = c.ChangesOf(k).SelectMany(x => x.Holders.Where(h => h.Reviewer == k).Select(h => h.ChangeId)).Order().ToArray();
            Assert.Equal(c.Comparisons[k].Changes.Select(x => x.Id).Order().ToArray(), ids);
        }
    }

    [Fact]
    public void ACorruptedAttributionFailsTheCheck()
    {
        Combination c = Combine(With((0, Base[0].Replace("30", "60", StringComparison.Ordinal))), With((2, "Either party may terminate.")));
        CombinedChange first = c.Changes[0];
        var broken = new Combination(
            c.Original, c.Reviewers, c.Comparisons, c.Changes.SetItem(0, first with { Holders = [] }), c.Conflicts, c.Diagnostics, c.Verification);
        Assert.NotEmpty(broken.Check());
    }

    [Fact]
    public void OutputsAreByteIdenticalAcrossRuns()
    {
        CompareDocument[] copies = [With((0, Base[0].Replace("30", "60", StringComparison.Ordinal))), With((0, Base[0].Replace("30", "10", StringComparison.Ordinal)), (3, "Governing law is Scots law."))];
        byte[] j1 = CombineJson.Write(Combination.Of(P(Base), copies));
        byte[] j2 = CombineJson.Write(Combination.Of(P(Base), copies));
        Assert.Equal(j1, j2);
        using JsonDocument doc = JsonDocument.Parse(j1);
        Assert.Equal("onediff.combination", doc.RootElement.GetProperty("schema").GetString());
        string h1 = ChangeReport.From(Combination.Of(P(Base), copies)).ToHtml();
        string h2 = ChangeReport.From(Combination.Of(P(Base), copies)).ToHtml();
        Assert.Equal(h1, h2);
        Assert.Contains("class=\"conflict\"", h1, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportListsResolvedValues()
    {
        Combination c = Combine(With((0, Base[0].Replace("30 days", "60 days", StringComparison.Ordinal))));
        ChangeReport r = ChangeReport.From(c);
        Assert.Contains(r.Sections[0].Items, i => i.Resolved.Contains("30 d → 60 d", StringComparison.Ordinal));
        Assert.Equal(1, r.ByReviewer[0].Count);
    }

    [Fact]
    public void AChainVerifiesEveryStepAndAttributesNetChangesToSteps()
    {
        CompareDocument v1 = P(Base);
        CompareDocument v2 = With((0, Base[0].Replace("30 days", "60 days", StringComparison.Ordinal)));
        string[] p3 = [Base[0].Replace("30 days", "60 days", StringComparison.Ordinal), Base[1], Base[2], "Governing law is Scots law."];
        CompareDocument v3 = P(p3);
        var chain = ComparisonChain.Of([v1, v2, v3]);
        Assert.Empty(chain.Check());
        Assert.Equal(2, chain.Steps.Length);
        Assert.All(chain.Net.Changes, n => Assert.NotEmpty(chain.StepsOf(n.Id)));
        Change law = chain.Net.Changes.First(n => n.New.Text.Contains("Scots", StringComparison.Ordinal) || n.Old.Text.Contains("England", StringComparison.Ordinal));
        Assert.Equal([1], chain.StepsOf(law.Id));
        Change dur = chain.Net.Changes.First(n => n.NewValue is not null);
        Assert.Equal([0], chain.StepsOf(dur.Id));
        Assert.Contains("net:", ChangeReport.From(chain).ToHtml(), StringComparison.Ordinal);
    }

    [Fact]
    public void RandomReviewersAlwaysVerify()
    {
        int cases = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_COMBINE_CASES"), CultureInfo.InvariantCulture, out int n) ? n : 150;
        int baseSeed = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_COMBINE_SEED"), CultureInfo.InvariantCulture, out int s0) ? s0 : 0;
        int shared = 0, conflicts = 0;
        for (int i = 0; i < cases; i++)
        {
            int seed = baseSeed + i;
            (Combination c, string context) = RandomCombination(seed);
            Assert.True(c.Verification.FallbackLevel == 0 && c.Verification.Passed, context + " fell back: " + string.Join(" | ", c.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            ImmutableCheck(c, context);
            shared += c.Changes.Count(x => x.Reviewers.Length > 1);
            conflicts += c.Conflicts.Length;
        }

        Assert.True(cases < 50 || (shared > 0 && conflicts > 0), $"shared {shared}, conflicts {conflicts}");
    }

    [Fact]
    public void RandomChainsAlwaysVerify()
    {
        int cases = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_CHAIN_CASES"), CultureInfo.InvariantCulture, out int n) ? n : 60;
        int baseSeed = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_CHAIN_SEED"), CultureInfo.InvariantCulture, out int s0) ? s0 : 0;
        for (int i = 0; i < cases; i++)
        {
            RandomChain(baseSeed + i);
        }
    }

    internal static (Combination, string) RandomCombination(int seed)
    {
        var r = new Random(seed);
        GNode a = DocumentGenerator.Random(r);
        int reviewers = r.Next(1, 5);
        int sharedSeed = r.Next();
        int sharedEdits = r.Next(0, 3);
        var copies = new List<CompareDocument>();
        var log = new List<string>();
        for (int k = 0; k < reviewers; k++)
        {
            GNode b = a.Clone();
            var rs = new Random(sharedSeed);
            if (k < 2 || r.Next(2) == 0)
            {
                for (int e = 0; e < sharedEdits; e++)
                {
                    log.Add(k + ":" + DocumentGenerator.Mutate(b, rs));
                }
            }

            for (int e = r.Next(0, 5); e > 0; e--)
            {
                log.Add(k + ":" + DocumentGenerator.Mutate(b, r));
            }

            copies.Add(DocumentGenerator.Build(b, "copy" + k));
        }

        CompareDocument original = DocumentGenerator.Build(a, "original");
        string context = $"seed {seed} reviewers {reviewers} edits [{string.Join(',', log)}]";
        return (Combination.Of(original, copies, seed % 3 == 1 ? CompareSettings.Strict with { ThrowOnFailure = false } : CompareSettings.Legal), context);
    }

    private static void ImmutableCheck(Combination c, string context)
    {
        Assert.True(c.Check().IsEmpty, context + " failed re-check: " + string.Join("; ", c.Check().Select(f => f.Code + " " + f.Message)));
        Assert.DoesNotContain(c.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        for (int k = 0; k < c.Reviewers.Length; k++)
        {
            int mine = c.ChangesOf(k).SelectMany(x => x.Holders.Where(h => h.Reviewer == k)).Count();
            Assert.True(mine == c.Comparisons[k].Changes.Length, context + $" projection {k}");
        }
    }

    private static string Dump(ComparisonChain chain, Change n)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"net {n.Kind} old [{string.Join(',', n.Old.Nodes)}] new [{string.Join(',', n.New.Nodes)}]");
        foreach (ChainStep s in chain.Steps)
        {
            sb.AppendLine($"step {s.Index}: links " + string.Join(' ', s.Comparison.Alignment.Links.Select(l => $"{l.Kind}[{string.Join(',', l.OldNodes)}>{string.Join(',', l.NewNodes)}]")));
            sb.AppendLine("  changes " + string.Join(' ', s.Comparison.Changes.Select(c => $"{c.Kind}[{string.Join(',', c.Old.Nodes)}>{string.Join(',', c.New.Nodes)}]")));
        }

        return sb.ToString();
    }

    internal static bool RandomChain(int seed)
    {
        var r = new Random(seed * 7 + 1);
        GNode cur = DocumentGenerator.Random(r);
        var versions = new List<CompareDocument> { DocumentGenerator.Build(cur, "v1") };
        for (int s = r.Next(3, 6); s > 0; s--)
        {
            cur = cur.Clone();
            for (int e = r.Next(0, 5); e > 0; e--)
            {
                DocumentGenerator.Mutate(cur, r);
            }

            versions.Add(DocumentGenerator.Build(cur, "v" + (versions.Count + 1).ToString(CultureInfo.InvariantCulture)));
        }

        ComparisonChain chain = ComparisonChain.Of(versions);
        Assert.True(chain.Check().IsEmpty, $"chain seed {seed}");
        Assert.All(chain.Steps, s => Assert.Equal(0, s.Comparison.Verification.FallbackLevel));
        Assert.All(chain.Net.Changes, n => Assert.True(!chain.StepsOf(n.Id).IsEmpty, $"chain seed {seed}: net change {n.Id} touched by no step\n" + Dump(chain, n)));
        return true;
    }
}
