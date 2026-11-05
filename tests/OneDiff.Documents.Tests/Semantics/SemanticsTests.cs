using System.Collections.Immutable;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Serialization;
using static OneDiff.Documents.Tests.Helpers;

namespace OneDiff.Documents.Tests;

public class SemanticsTests
{
    private static CompareDocument Items(params (string Label, string Text)[] items)
    {
        var b = new CompareDocumentBuilder(DocumentKind.Flowing);
        foreach ((string label, string text) in items)
        {
            b.Add(NodeKind.ListItem, new NodeContent { Text = text, Attributes = FormatSet.Of(FormatKeys.ListLabel, label) });
        }

        return b.Build();
    }

    [Theory]
    [InlineData("USD 25,000", ChangeType.Amount, "USD 25000")]
    [InlineData("18.000 EUR", ChangeType.Amount, "EUR 18000")]
    [InlineData("2 500 EUR", ChangeType.Amount, "EUR 2500")]
    [InlineData("人民币4,800元", ChangeType.Amount, "CNY 4800")]
    [InlineData("$2 million", ChangeType.Amount, "USD 2000000")]
    [InlineData("25,000 دولار", ChangeType.Amount, "USD 25000")]
    [InlineData("16 May 2028", ChangeType.Date, "2028-05-16")]
    [InlineData("May 16, 2028", ChangeType.Date, "2028-05-16")]
    [InlineData("1. Juni 2029", ChangeType.Date, "2029-06-01")]
    [InlineData("3 de enero de 2025", ChangeType.Date, "2025-01-03")]
    [InlineData("6 أغسطس 2029", ChangeType.Date, "2029-08-06")]
    [InlineData("2027年12月27日", ChangeType.Date, "2027-12-27")]
    [InlineData("2027-03-01", ChangeType.Date, "2027-03-01")]
    [InlineData("31/12/2025", ChangeType.Date, "2025-12-31")]
    [InlineData("30 days", ChangeType.Duration, "30 d")]
    [InlineData("10 business days", ChangeType.Duration, "10 bd")]
    [InlineData("18 Monaten", ChangeType.Duration, "18 mo")]
    [InlineData("30 يومًا", ChangeType.Duration, "30 d")]
    [InlineData("9个月", ChangeType.Duration, "9 mo")]
    [InlineData("1年", ChangeType.Duration, "1 y")]
    [InlineData("三十日", ChangeType.Duration, "30 d")]
    [InlineData("12.5%", ChangeType.Percentage, "12.5%")]
    [InlineData("6 %", ChangeType.Percentage, "6%")]
    [InlineData("42", ChangeType.Number, "42")]
    [InlineData("Contoso Inc", ChangeType.Party, "Contoso Inc")]
    public void BuiltInRecognizerResolvesValues(string text, ChangeType type, string canonical)
    {
        ImmutableArray<RecognizedValue> found = BuiltInValueRecognizer.Instance.Recognize(text, null);
        RecognizedValue v = Assert.Single(found);
        Assert.Equal(0, v.Start);
        Assert.Equal(text.Length, v.Length);
        Assert.Equal(type, v.Type);
        Assert.Equal(canonical, v.Value?.Canonical);
    }

    [Fact]
    public void AnAmbiguousDateIsRecognizedButNotResolved()
    {
        RecognizedValue v = Assert.Single(BuiltInValueRecognizer.Instance.Recognize("3/4/2025", null));
        Assert.Equal(ChangeType.Date, v.Type);
        Assert.Null(v.Value);
    }

    [Fact]
    public void ADurationChangeIsTypedWithResolvedValues()
    {
        Comparison c = CompareText("The notice period is 30 days from receipt.", "The notice period is 60 days from receipt.");
        Change ch = Assert.Single(c.Changes);
        Assert.Equal(ChangeType.Duration, ch.Type);
        Assert.Equal("30 d", ch.OldValue?.Canonical);
        Assert.Equal("60 d", ch.NewValue?.Canonical);
        Assert.Equal(30m, ch.OldValue?.Number);
        Assert.Equal("d", ch.OldValue?.Unit);
        Assert.True(ch.IsSubstantive);
        Assert.Equal(DecidedBy.Rule, ch.Decision.By);

        byte[] json = ComparisonJson.Write(c);
        Comparison back = ComparisonJson.Read(json);
        Assert.Equal(json, ComparisonJson.Write(back));
        Change r = Assert.Single(back.Changes);
        Assert.Equal(ChangeType.Duration, r.Type);
        Assert.Equal(c.Changes[0].NewValue, r.NewValue);
    }

    [Fact]
    public void WithoutTypedChangesNothingIsTypedButSubstantiveIsStillDecided()
    {
        Comparison c = CompareText("Pay within 30 days.", "Pay within 60 days.", CompareSettings.Strict with { ThrowOnFailure = false });
        Change ch = Assert.Single(c.Changes);
        Assert.Equal(ChangeType.None, ch.Type);
        Assert.Null(ch.NewValue);
        Assert.True(ch.IsSubstantive);
    }

    [Fact]
    public void FormattingAndTypographyAreNotSubstantive()
    {
        Comparison c = CompareText("He said \"yes\" and left.", "He said \u201Cyes\u201D and left.", CompareSettings.Strict with { ThrowOnFailure = false, TypedChanges = true });
        Assert.All(c.Changes, ch => Assert.False(ch.IsSubstantive));
        Assert.Contains(c.Changes, ch => ch.Type == ChangeType.Typography);
    }

    [Fact]
    public void ARenumberingAndItsReferencesAreOneCascadeGroupAndStayThreeChanges()
    {
        CompareDocument a = Items(("1.1", "Scope of the services."), ("1.2", "Fees are set out in Section 1.3."), ("1.3", "Payment terms apply."));
        CompareDocument b = Items(("1.1", "Scope of the services."), ("1.2", "A new clause is added here."), ("1.3", "Fees are set out in Section 1.4."), ("1.4", "Payment terms apply."));
        Comparison c = Compare(a, b, CompareSettings.Legal);

        ChangeGroup cascade = Assert.Single(c.Groups, g => g.Kind == GroupKind.Cascade);
        Assert.True(cascade.ChangeIds.Length >= 3);
        var members = cascade.ChangeIds.Select(id => c.Changes.First(x => x.Id == id)).ToList();
        Assert.Contains(members, m => m.Type == ChangeType.Numbering && m.OldValue?.Canonical == "1.3" && m.NewValue?.Canonical == "1.4");
        Change reference = Assert.Single(members, m => m.Type == ChangeType.Reference);
        Assert.Equal("1.3", reference.OldValue?.Canonical);
        Assert.Equal("1.4", reference.NewValue?.Canonical);
        Assert.All(members, m => Assert.False(m.IsSubstantive));

        // Grouping is display only: each member is still its own change with both sides, and every change is in exactly one group.
        Assert.Equal(c.Changes.Length, c.Groups.Sum(g => g.ChangeIds.Length));
        Assert.Contains(c.Changes, ch => ch.Kind == ChangeKind.Insert && ch.IsSubstantive);

        // Strict does not collapse.
        Comparison strict = Compare(a, b, CompareSettings.Strict with { ThrowOnFailure = false });
        Assert.DoesNotContain(strict.Groups, g => g.Kind == GroupKind.Cascade);
        Assert.Equal(c.Changes.Length, strict.Changes.Length);
    }

    [Fact]
    public void ARenamedDefinedTermIsOneRenameGroupAndLeavesOtherEditsAlone()
    {
        CompareDocument a = Items(
            ("1.1", "\"Software\" means the programs listed in Schedule 1."),
            ("1.2", "The Supplier shall deliver the Software within 30 days."),
            ("1.3", "The Customer shall not copy the Software."));
        CompareDocument b = Items(
            ("1.1", "\"Platform\" means the programs listed in Schedule 1."),
            ("1.2", "The Supplier shall deliver the Platform within 60 days."),
            ("1.3", "The Customer shall not copy the Platform."));
        Comparison c = Compare(a, b, CompareSettings.Legal);

        ChangeGroup rename = Assert.Single(c.Groups, g => g.Kind == GroupKind.Rename);
        Assert.Equal(3, rename.ChangeIds.Length);
        Assert.All(rename.ChangeIds.Select(id => c.Changes.First(x => x.Id == id)), m =>
        {
            Assert.Equal(ChangeType.DefinedTerm, m.Type);
            Assert.Equal("Software", m.OldValue?.Canonical);
            Assert.Equal("Platform", m.NewValue?.Canonical);
        });

        // The duration edit in the same sentence as one occurrence is its own change, outside the group.
        Change duration = Assert.Single(c.Changes, ch => ch.Type == ChangeType.Duration);
        Assert.DoesNotContain(duration.Id, rename.ChangeIds);
        Assert.Equal("60 d", duration.NewValue?.Canonical);
    }

    private sealed class FixedAmount : IValueRecognizer
    {
        public ImmutableArray<RecognizedValue> Recognize(string text, string? language) =>
            [new RecognizedValue(0, text.Length, ChangeType.Amount, new ResolvedValue("XTS " + text.Length))];
    }

    [Fact]
    public void ACustomRecognizerPlugsIntoClassify()
    {
        CompareDocument a = CompareDocument.FromText("Total: alpha beta.");
        CompareDocument b = CompareDocument.FromText("Total: gamma delta epsilon.");
        Alignment al = Stages.Align(a, b);
        ChangeSet set = Stages.Group(Stages.DiffPairs(al));
        Comparison c = Stages.Verify(Stages.Classify(set, new FixedAmount()));
        Assert.Contains(c.Changes, ch => ch.Type == ChangeType.Amount && ch.NewValue!.Canonical.StartsWith("XTS", StringComparison.Ordinal));
    }
}
