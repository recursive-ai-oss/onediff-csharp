using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using OneDiff.Documents.Html;
using OneDiff.Documents.Model;
using OneDiff.Tests.Shared;

namespace OneDiff.Documents.Tests.Html;

/// <summary>
/// The sample pages under docs/samples: one inline and one side-by-side page per preset, made from a short pair of documents
/// written for the purpose. The test compares freshly written pages with the committed ones, so a change in the look is a
/// visible diff in review; set ONEDIFF_UPDATE_SAMPLES=1 to rewrite them.
/// </summary>
public class HtmlSampleTests
{
    private static string SamplesDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "docs", "samples"));

    internal static Comparison SampleComparison() => SamplePair.Create();

    [Fact]
    public void SampleComparisonShowsEachKindOfChange()
    {
        Comparison c = Helpers.Clean(SampleComparison());
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Move);
        Assert.Contains(c.Changes, x => x.Kind is ChangeKind.Replace or ChangeKind.Insert);
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Delete);
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Format);
        Assert.True(c.Ledger.TotalCount > 0);
    }

    [Fact]
    public void CommittedSamplesMatchTheWriters()
    {
        Comparison c = SampleComparison();
        bool update = Environment.GetEnvironmentVariable("ONEDIFF_UPDATE_SAMPLES") == "1";
        string dir = SamplesDir();
        if (update)
        {
            Directory.CreateDirectory(dir);
        }

        foreach (RedlineStyle style in HtmlWriterTests.Styles)
        {
            foreach (bool sbs in new[] { false, true })
            {
                string html = sbs ? HtmlRedline.SideBySide(c, style) : HtmlRedline.Inline(c, style);
                Assert.Empty(HtmlGate.Check(c, html, sbs));
                string file = Path.Combine(dir, style.Name.ToLowerInvariant() + (sbs ? "-side-by-side.html" : "-inline.html"));
                if (update)
                {
                    File.WriteAllText(file, html);
                }
                else
                {
                    Assert.True(File.Exists(file), $"missing sample {file}; run with ONEDIFF_UPDATE_SAMPLES=1");
                    Assert.True(File.ReadAllText(file) == html, $"sample {Path.GetFileName(file)} is out of date; run with ONEDIFF_UPDATE_SAMPLES=1");
                }
            }
        }
    }
}
