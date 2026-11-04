using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using OneDiff.Documents.Html;
using OneDiff.Documents.Model;

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

    private const string PaymentText = "Payment is due within sixty days of receipt of the invoice.";

    internal static Comparison SampleComparison()
    {
        FormatSet bold = FormatSet.Of(FormatKeys.Bold, "true");

        CompareDocument Doc(string name, params (NodeKind Kind, string Text, string? Label, FormatRun[]? Runs)[] blocks)
        {
            var b = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(name, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", blocks.Select(x => x.Text))))), "text/plain");
            b.Begin(NodeKind.Section, new NodeContent { Anchor = Anchor.ForNode("section") });
            int i = 0;
            foreach ((NodeKind kind, string text, string? label, FormatRun[]? runs) in blocks)
            {
                NormalizedText norm = TextNormalization.Normalize(text, [TypographicNormalizer.Instance]);
                b.Add(kind, new NodeContent
                {
                    Text = norm.Text,
                    Folds = norm.Folds,
                    Runs = runs is null ? [] : [.. runs],
                    Attributes = label is null ? FormatSet.Empty : FormatSet.Of(FormatKeys.ListLabel, label, FormatKeys.ListLevel, "1"),
                    Anchor = Anchor.ForNode(name + ":" + (i++).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                });
            }

            b.End();
            return b.Build();
        }

        CompareDocument oldDoc = Doc(
            "services-v1.txt",
            (NodeKind.Paragraph, "Services Agreement", null, null),
            (NodeKind.ListItem, "The supplier shall deliver the goods within thirty days of the order.", "1.1", null),
            (NodeKind.ListItem, PaymentText, "1.2", null),
            (NodeKind.ListItem, "Either party may terminate this agreement on written notice.", "1.3", null),
            (NodeKind.ListItem, "The supplier shall keep the goods insured against fire until delivery.", "1.4", null),
            (NodeKind.ListItem, "This agreement is governed by the laws of the State of New York.", "1.5", null),
            (NodeKind.Paragraph, "The \u201CSupplier\u201D and the buyer have signed this agreement.", null, null));
        CompareDocument newDoc = Doc(
            "services-v2.txt",
            (NodeKind.Paragraph, "Services Agreement", null, null),
            (NodeKind.ListItem, "The supplier shall deliver the goods within forty-five days of the order.", "1.1", null),
            (NodeKind.ListItem, "This agreement is governed by the laws of the State of New York.", "1.2", null),
            (NodeKind.ListItem, PaymentText, "1.3", [new FormatRun(PaymentText.IndexOf("sixty days", StringComparison.Ordinal), 10, bold)]),
            (NodeKind.ListItem, "Either party may terminate this agreement on thirty days\u2019 written notice.", "1.4", null),
            (NodeKind.ListItem, "Disputes shall be referred to arbitration in London.", "1.5", null),
            (NodeKind.Paragraph, "The \"Supplier\" and the buyer have signed this agreement.", null, null));
        return Comparison.Of(oldDoc, newDoc, CompareSettings.Legal);
    }

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
