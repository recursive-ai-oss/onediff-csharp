using OneDiff.Documents.Model;

namespace OneDiff.Documents.Tests;

internal static class Helpers
{
    public static CompareDocument Text(params string[] paragraphs) => CompareDocument.FromText(string.Join("\n\n", paragraphs));

    /// <summary>Asserts a comparison is complete without any fallback: it passed on the first try and re-checking finds nothing.</summary>
    public static Comparison Clean(Comparison c)
    {
        Assert.True(c.Verification.Passed);
        Assert.Equal(0, c.Verification.FallbackLevel);
        Assert.Empty(c.Check());
        Assert.DoesNotContain(c.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return c;
    }

    public static Comparison Compare(CompareDocument a, CompareDocument b, CompareSettings? settings = null) => Clean(Comparison.Of(a, b, settings));

    public static Comparison CompareText(string a, string b, CompareSettings? settings = null) =>
        Compare(CompareDocument.FromText(a), CompareDocument.FromText(b), settings);
}
