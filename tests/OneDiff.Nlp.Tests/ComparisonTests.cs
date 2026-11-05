using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;

namespace OneDiff.Nlp.Tests;

public class ComparisonTests
{
    private const string Old = "Payment is due within thirty days of the invoice.\nThe Supplier shall keep the goods safe at all times.\nThe cap on its liability is forty-two thousand dollars.\n";
    private const string New = "Payment is due within two months of the invoice.\nThe Supplier shall keep the goods safe at all times.\nThe cap on its liability is $50,000.\n";

    private static Comparison Compare(CompareSettings settings) =>
        Comparison.Of(CompareDocument.FromText(Old, "old"), CompareDocument.FromText(New, "new"), settings);

    [Fact]
    public void SettingsSelectTheRecognizer()
    {
        Comparison plain = Compare(CompareSettings.Legal);
        Comparison nlp = Compare(CompareSettings.Legal.UseOneNlp());
        Assert.DoesNotContain(plain.Changes, c => c.Type == ChangeType.Duration);
        Assert.DoesNotContain(plain.Changes, c => c.Type == ChangeType.Amount && c.OldValue is not null);
        Change duration = Assert.Single(nlp.Changes, c => c.Type == ChangeType.Duration);
        Assert.Equal("30 d", duration.OldValue?.Canonical);
        Assert.Equal("2 mo", duration.NewValue?.Canonical);
        Change amount = Assert.Single(nlp.Changes, c => c.Type == ChangeType.Amount);
        Assert.Equal("USD 42000", amount.OldValue?.Canonical);
        Assert.Equal("USD 50000", amount.NewValue?.Canonical);
        Assert.True(nlp.Verification.Passed);
        Assert.Equal(0, nlp.Verification.FallbackLevel);
        Assert.True(nlp.Check().IsEmpty);
    }

    [Fact]
    public void ChangesAreTheSameWhateverTheRecognizer()
    {
        // A recognizer labels changes; it never adds, removes or reshapes one.
        Comparison plain = Compare(CompareSettings.Legal), nlp = Compare(CompareSettings.Legal.UseOneNlp());
        Assert.Equal(plain.Changes.Select(c => (c.Kind, c.Old.Text, c.New.Text)), nlp.Changes.Select(c => (c.Kind, c.Old.Text, c.New.Text)));
    }

    [Fact]
    public void JsonIsIdenticalAcrossRuns()
    {
        CompareSettings s = CompareSettings.Legal.UseOneNlp();
        Assert.Equal(ComparisonJson.Write(Compare(s)), ComparisonJson.Write(Compare(s)));
    }

    [Fact]
    public void TheFallbackLevelsKeepTheRecognizer()
    {
        // The reference stages used when verification fails read the recognizer from the settings too.
        Assert.Same(NlpValueRecognizer.Instance, ValueRecognizers.For(CompareSettings.Legal.UseOneNlp()));
    }

    [Fact]
    public void RegisterMakesItTheDefaultAndNullGoesBack()
    {
        try
        {
            Assert.Same(BuiltInValueRecognizer.Instance, ValueRecognizers.For(CompareSettings.Legal));
            OneNlpRecognizers.Register();
            Assert.Same(NlpValueRecognizer.Instance, ValueRecognizers.For(CompareSettings.Legal));
            Assert.Contains(Compare(CompareSettings.Legal).Changes, c => c.Type == ChangeType.Duration);
            // Settings win over the registered recognizer.
            Assert.Same(BuiltInValueRecognizer.Instance, ValueRecognizers.For(CompareSettings.Legal with { ValueRecognizer = BuiltInValueRecognizer.Instance }));
        }
        finally
        {
            ValueRecognizers.Register(null);
        }

        Assert.Same(BuiltInValueRecognizer.Instance, ValueRecognizers.For(CompareSettings.Legal));
    }
}
