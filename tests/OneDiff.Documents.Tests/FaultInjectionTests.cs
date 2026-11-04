using System.Collections.Immutable;
using OneDiff.Documents.Ledger;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Verification;
using static OneDiff.Documents.Tests.Helpers;

namespace OneDiff.Documents.Tests;

public class FaultInjectionTests
{
    private const string P1 = "The supplier shall deliver the goods within thirty days of the order date.";
    private const string P2 = "Payment is due sixty days after receipt of a correct invoice from the supplier.";
    private const string P3 = "Either party may terminate this agreement on written notice for material breach.";

    private static ChangeSet Grouped(CompareSettings? settings = null)
    {
        CompareDocument a = Text(P1, P2, P3);
        CompareDocument b = Text(P1.Replace("thirty", "forty", StringComparison.Ordinal), P3, "A brand new closing paragraph about notices.");
        Alignment al = Stages.Align(a, b, null, settings);
        return Stages.Classify(Stages.Group(Stages.DiffPairs(al)));
    }

    /// <summary>Asserts the fault was caught, recovered from at <paramref name="level"/> or later, and the recovered result is complete.</summary>
    private static Comparison Recovered(ChangeSet broken, string code, int minLevel = 1)
    {
        Comparison c = Stages.Verify(broken);
        Assert.True(c.Verification.Passed);
        Assert.True(c.Verification.FallbackLevel >= minLevel, $"expected a fallback, got level {c.Verification.FallbackLevel}");
        Assert.True(c.Verification.FailureCount > 0);
        Assert.Contains(c.Diagnostics, d => d.Code == "verify." + code && d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(c.Diagnostics, d => d.Code == "verify.fallback" && d.Severity == DiagnosticSeverity.Warning);
        Assert.Empty(c.Check());
        return c;
    }

    private static void ThrowsInStrict(ChangeSet broken, string code)
    {
        ChangeSet strict = new(
            Alignment.Create(broken.Alignment.Old, broken.Alignment.New, broken.Settings with { ThrowOnFailure = true }, broken.Alignment.Links, broken.Alignment.Diagnostics),
            broken.Changes,
            broken.Groups,
            broken.Ledger,
            broken.Diagnostics,
            true);
        ComparisonVerificationException ex = Assert.Throws<ComparisonVerificationException>(() => Stages.Verify(strict));
        Assert.Contains(ex.Failures, f => f.Code == code);
    }

    [Fact]
    public void TheUntouchedPipelineVerifiesFirstTime()
    {
        Comparison c = Stages.Verify(Grouped());
        Assert.Equal(0, c.Verification.FallbackLevel);
        Assert.NotEmpty(c.Changes);
    }

    [Fact]
    public void DroppedChangeIsCaught()
    {
        ChangeSet good = Grouped();
        Change victim = good.Changes.First(c => c.Kind == ChangeKind.Replace);
        ChangeSet broken = good
            .WithChanges(good.Changes.Where(c => c.Id != victim.Id))
            .WithGroups(good.Groups.Where(g => !g.ChangeIds.Contains(victim.Id)));
        Comparison c = Recovered(broken, "V302");
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Replace && x.Old.Text == "thirty");
        ThrowsInStrict(broken, "V302");
    }

    [Fact]
    public void DroppedNodeChangeIsCaught()
    {
        ChangeSet good = Grouped();
        Change victim = good.Changes.First(c => c.Kind == ChangeKind.Insert && c.LinkId == -1);
        ChangeSet broken = good
            .WithChanges(good.Changes.Where(c => c.Id != victim.Id))
            .WithGroups(good.Groups.Where(g => !g.ChangeIds.Contains(victim.Id)));
        Recovered(broken, "V200");
        ThrowsInStrict(broken, "V200");
    }

    [Fact]
    public void DoubleCountedEditIsCaught()
    {
        ChangeSet good = Grouped();
        Change victim = good.Changes.First(c => c.Kind == ChangeKind.Replace);
        Change twin = victim with { Id = good.Changes.Length };
        ChangeSet broken = good
            .WithChanges(good.Changes.Append(twin))
            .WithGroups(good.Groups.Append(new ChangeGroup(twin.Id, GroupKind.Region, "", [twin.Id])));
        Recovered(broken, "V302");
        ThrowsInStrict(broken, "V302");
    }

    [Fact]
    public void DoubleCountedNodeIsCaught()
    {
        ChangeSet good = Grouped();
        Change victim = good.Changes.First(c => c.Kind == ChangeKind.Insert && c.LinkId == -1);
        Change twin = victim with { Id = good.Changes.Length };
        ChangeSet broken = good
            .WithChanges(good.Changes.Append(twin))
            .WithGroups(good.Groups.Append(new ChangeGroup(twin.Id, GroupKind.Region, "", [twin.Id])));
        Recovered(broken, "V200");
    }

    [Fact]
    public void GroupingThatLosesOrRepeatsAChangeIsCaught()
    {
        ChangeSet good = Grouped();
        ChangeSet lost = good.WithGroups(good.Groups.Skip(1));
        Recovered(lost, "V103", minLevel: 1);
        ChangeSet repeated = good.WithGroups([.. good.Groups, new ChangeGroup(99, GroupKind.Custom, "again", [good.Changes[0].Id])]);
        Recovered(repeated, "V103", minLevel: 1);
        ThrowsInStrict(lost, "V103");
    }

    [Fact]
    public void GroupingThatRewritesAChangeNonInvertiblyIsCaught()
    {
        ChangeSet good = Grouped();
        Change victim = good.Changes.First(c => c.Kind == ChangeKind.Replace);
        RegionEdit e = victim.Edits[0];
        // A "merged" change that claims a different replacement than the one the documents hold.
        Change forged = victim with { Edits = [e with { NewText = "fifty", NewLength = 5 }] };
        ChangeSet broken = good.WithChanges(good.Changes.Select(c => c.Id == victim.Id ? forged : c));
        Recovered(broken, "V302");
        ThrowsInStrict(broken, "V302");
    }

    [Fact]
    public void TamperedSideTextAndAnchorsAreCaught()
    {
        ChangeSet good = Grouped();
        Change victim = good.Changes.First(c => c.Kind == ChangeKind.Replace);
        Change badText = victim with { New = victim.New with { Text = "something else" } };
        Recovered(good.WithChanges(good.Changes.Select(c => c.Id == victim.Id ? badText : c)), "V112");
        Change badAnchor = victim with { Old = victim.Old with { Anchor = Anchor.ForTextSpan("text", 1, 2) } };
        Recovered(good.WithChanges(good.Changes.Select(c => c.Id == victim.Id ? badAnchor : c)), "V113");
    }

    [Fact]
    public void LedgerThatForgetsAHiddenDifferenceIsCaught()
    {
        CompareDocument a = CompareDocument.FromText("He said \u201Chello\u201D.");
        CompareDocument b = CompareDocument.FromText("He said \"hello\".");
        ChangeSet good = Stages.Classify(Stages.Group(Stages.DiffPairs(Stages.Align(a, b))));
        Assert.Equal(2, good.Ledger.Get(LedgerCategory.Quotes)!.Count);
        Comparison c = Recovered(good.WithLedger(ChangeLedger.Empty), "V500");
        Assert.Equal(2, c.Ledger.Get(LedgerCategory.Quotes)!.Count);
    }

    [Fact]
    public void AlignmentThatBreaksOrderFallsBackToNothingAligned()
    {
        CompareDocument a = Text(P1, P2, P3), b = Text(P3, P2, P1);
        // Links that cross, left as Match: the alignment itself is not complete. Nodes: 0 is the section, 1..3 the paragraphs.
        Alignment crossed = Alignment.Create(a, b, CompareSettings.Legal, [
            new AlignmentLink(0, LinkKind.Match, [0], [0], false, 1, LinkOrigin.Exact),
            new AlignmentLink(0, LinkKind.Match, [1], [3], false, 1, LinkOrigin.Exact),
            new AlignmentLink(0, LinkKind.Match, [2], [2], false, 1, LinkOrigin.Exact),
            new AlignmentLink(0, LinkKind.Match, [3], [1], false, 1, LinkOrigin.Exact),
        ]);
        ChangeSet broken = Stages.Classify(Stages.Group(Stages.DiffPairs(crossed)));
        Comparison c = Recovered(broken, "V401", minLevel: 3);
        Assert.Equal(3, c.Verification.FallbackLevel);
        Assert.Equal(4 + 4, c.Changes.Length);
        // Normalizing the same links gives a clean result with moves instead.
        Comparison fixedUp = Clean(Stages.Verify(Stages.Classify(Stages.Group(Stages.DiffPairs(crossed.Normalize())))));
        Assert.Contains(fixedUp.Changes, x => x.Kind == ChangeKind.Move);
    }

    [Fact]
    public void ChangeWithTheWrongLinkOrMissingSideIsCaught()
    {
        ChangeSet good = Grouped();
        Change victim = good.Changes.First(c => c.Kind == ChangeKind.Replace);
        Change noSide = victim with { Old = ChangeSide.Absent };
        Recovered(good.WithChanges(good.Changes.Select(c => c.Id == victim.Id ? noSide : c)), "V108");
        Change wrongLink = victim with { LinkId = 999 };
        Recovered(good.WithChanges(good.Changes.Select(c => c.Id == victim.Id ? wrongLink : c)), "V105");
    }

    [Fact]
    public void OpaquePartsThatDifferAreLedgeredNotItemized()
    {
        GNode g = new() { Kind = NodeKind.Section, Children = [new GNode { Kind = NodeKind.Paragraph, Text = "hello world" }] };
        Comparison c = Compare(DocumentGenerator.BuildWithOpaque(g, "aa"), DocumentGenerator.BuildWithOpaque(g, "bb"));
        Assert.Empty(c.Changes);
        LedgerEntry e = c.Ledger.Get(LedgerCategory.NotItemized)!;
        Assert.Equal(1, e.Count);
        Assert.Contains("custom/data", e.Locations[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenCommentsAndFieldCodesGoToTheLedgerAndShowUnderStrict()
    {
        GNode Make(string comment, string code) => new()
        {
            Kind = NodeKind.Section,
            Children =
            [
                new GNode { Kind = NodeKind.Paragraph, Text = "See clause four" },
                new GNode { Kind = NodeKind.Comment, Children = [new GNode { Kind = NodeKind.Paragraph, Text = comment }] },
                new GNode { Kind = NodeKind.Field, Text = "4.2", Attributes = FormatSet.Of(FormatKeys.FieldCode, code) },
            ],
        };
        CompareDocument a = DocumentGenerator.Build(Make("check this", "REF a")), b = DocumentGenerator.Build(Make("checked", "REF b"));
        Comparison legal = Compare(a, b);
        Assert.Empty(legal.Changes);
        Assert.Equal(1, legal.Ledger.Get(LedgerCategory.FieldCodes)!.Count);
        Assert.True(legal.Ledger.Get(LedgerCategory.Comments)!.Count >= 1);
        Comparison strict = Compare(a, b, CompareSettings.Strict);
        Assert.Contains(strict.Changes, x => x.Kind == ChangeKind.Attribute);
        Assert.Contains(strict.Changes, x => x.Old.Text.Contains("check this", StringComparison.Ordinal));
        Assert.Empty(strict.Ledger.Entries.Where(e => e.Category != LedgerCategory.NotItemized));
    }

    [Fact]
    public void FormattingOnlyChangeIsAFormatChangeOrLedgeredByPreset()
    {
        CompareDocument Doc(bool bold)
        {
            var bld = new CompareDocumentBuilder();
            bld.Begin(NodeKind.Section);
            bld.Add(NodeKind.Paragraph, new NodeContent { Text = "pay within thirty days", Runs = bold ? [new FormatRun(11, 6, FormatSet.Of(FormatKeys.Bold, "1"))] : [] });
            bld.End();
            return bld.Build();
        }

        Comparison shown = Compare(Doc(false), Doc(true));
        Change f = Assert.Single(shown.Changes);
        Assert.Equal(ChangeKind.Format, f.Kind);
        Assert.Equal("thirty", f.New.Text);
        Comparison hidden = Compare(Doc(false), Doc(true), CompareSettings.TextOnly);
        Assert.Empty(hidden.Changes);
        Assert.Equal(1, hidden.Ledger.Get(LedgerCategory.RunFormatting)!.Count);
    }
}
