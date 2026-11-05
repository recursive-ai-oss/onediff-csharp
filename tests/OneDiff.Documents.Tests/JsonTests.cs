using System.Text;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;
using static OneDiff.Documents.Tests.Helpers;

namespace OneDiff.Documents.Tests;

public class JsonTests
{
    private static Comparison Sample()
    {
        var r = new Random(7);
        GNode a = DocumentGenerator.Random(r);
        while (DocumentGenerator.All(a).Count() < 8)
        {
            a = DocumentGenerator.Random(r);
        }

        GNode b = a.Clone();
        for (int i = 0; i < 6; i++)
        {
            DocumentGenerator.Mutate(b, r);
        }

        return Compare(DocumentGenerator.Build(a, "old.txt"), DocumentGenerator.BuildWithOpaque(b, "ff"));
    }

    [Fact]
    public void RoundTripIsByteIdentical()
    {
        for (int seed = 0; seed < 60; seed++)
        {
            var r = new Random(seed);
            GNode a = DocumentGenerator.Random(r), b = a.Clone();
            for (int i = r.Next(7); i > 0; i--)
            {
                DocumentGenerator.Mutate(b, r);
            }

            Comparison c = Comparison.Of(DocumentGenerator.Build(a, "a"), DocumentGenerator.BuildWithOpaque(b, "ee"), seed % 2 == 0 ? CompareSettings.Legal : CompareSettings.Strict with { ThrowOnFailure = false });
            byte[] json = ComparisonJson.Write(c);
            if (Environment.GetEnvironmentVariable("ONEDIFF_JSON_DUMP") is { Length: > 0 } dump)
            {
                // Lets the schema file be checked against real output with an external validator.
                Directory.CreateDirectory(dump);
                File.WriteAllBytes(Path.Combine(dump, $"comparison-{seed:D3}.json"), json);
            }

            Comparison back = ComparisonJson.Read(json);
            Assert.Equal(json, ComparisonJson.Write(back));
            Assert.Empty(back.Check());
            Assert.Equal(c.Changes.Length, back.Changes.Length);
            Assert.Equal(c.Ledger.TotalCount, back.Ledger.TotalCount);
        }
    }

    [Fact]
    public void WritingTwiceGivesTheSameBytes()
    {
        Comparison c = Sample();
        Assert.Equal(ComparisonJson.Write(c), ComparisonJson.Write(c));
        string text = Encoding.UTF8.GetString(ComparisonJson.Write(c));
        Assert.StartsWith("{\"schema\":\"onediff.comparison\",\"version\":1,", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfAndOcrAnchorsAndCellAnchorsSurvive()
    {
        var bld = new CompareDocumentBuilder().WithMetadata("scan.pdf", "ab12", "application/pdf");
        bld.Begin(NodeKind.Section, new NodeContent { Anchor = Anchor.ForCell("Sheet1", "B7") });
        bld.Add(NodeKind.Paragraph, new NodeContent
        {
            Text = "pay within thirty days",
            Anchor = Anchor.ForPdfQuads([new Quad(0, 10.5, 20, 30.25, 20, 30.25, 32, 10.5, 32)]),
            Segments =
            [
                new AnchoredSpan(0, 10, Anchor.ForOcrWords([new Quad(0, 1, 2, 3, 2, 3, 4, 1, 4)], 0.42)),
                new AnchoredSpan(11, 11, Anchor.ForOcrWords([new Quad(0, 5, 2, 7, 2, 7, 4, 5, 4)], 0.97)),
            ],
        });
        bld.End();
        CompareDocument a = bld.Build();
        var bld2 = new CompareDocumentBuilder().WithMetadata("scan2.pdf", "cd34", "application/pdf");
        bld2.Begin(NodeKind.Section, new NodeContent { Anchor = Anchor.ForCell("Sheet1", "B7") });
        bld2.Add(NodeKind.Paragraph, new NodeContent
        {
            Text = "pay within forty days",
            Anchor = Anchor.ForPdfQuads([new Quad(1, 10.5, 20, 30.25, 20, 30.25, 32, 10.5, 32)]),
            Segments = [new AnchoredSpan(0, 10, Anchor.ForOcrWords([new Quad(1, 1, 2, 3, 2, 3, 4, 1, 4)], 0.9)), new AnchoredSpan(11, 10, Anchor.ForOcrWords([new Quad(1, 5, 2, 7, 2, 7, 4, 5, 4)], 0.31))],
        });
        bld2.End();
        Comparison c = Compare(a, bld2.Build());
        if (Environment.GetEnvironmentVariable("ONEDIFF_JSON_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            File.WriteAllBytes(Path.Combine(dump, "comparison-ocr.json"), ComparisonJson.Write(c));
        }

        Change ch = Assert.Single(c.Changes);
        Assert.Equal(AnchorKind.OcrWords, ch.Old.Anchor.Kind);
        Assert.Equal(0.97, ch.Old.Anchor.Confidence);
        Assert.Equal(0.31, ch.New.Anchor.Confidence);
        byte[] json = ComparisonJson.Write(c);
        Comparison back = ComparisonJson.Read(json);
        Assert.Equal(json, ComparisonJson.Write(back));
        Assert.True(back.Changes[0].Old.Anchor.SameAs(ch.Old.Anchor));
        Assert.Equal("ab12", back.Provenance.OldHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"schema\":\"onediff.comparison\",\"version\":2}")]
    [InlineData("{\"schema\":\"other\",\"version\":1}")]
    [InlineData("not json")]
    [InlineData("{\"schema\":\"onediff.comparison\",\"version\":1,\"provenance\":5}")]
    public void RejectsNonComparisons(string json)
    {
        Assert.Throws<ComparisonFormatException>(() => ComparisonJson.Read(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void LimitsAreEnforced()
    {
        byte[] json = ComparisonJson.Write(Sample());
        Assert.Throws<ComparisonFormatException>(() => ComparisonJson.Read(json, new ComparisonJsonLimits(MaxInputBytes: 100)));
        Assert.Throws<ComparisonFormatException>(() => ComparisonJson.Read(json, new ComparisonJsonLimits(MaxItems: 1)));
        Assert.Throws<ComparisonFormatException>(() => ComparisonJson.Read(json, new ComparisonJsonLimits(MaxDepth: 2)));
        byte[] deep = Encoding.UTF8.GetBytes(new string('[', 100_000) + new string(']', 100_000));
        Assert.Throws<ComparisonFormatException>(() => ComparisonJson.Read(deep));
    }

    [Fact]
    public void DamagedInputFailsOnlyWithTheDocumentedException()
    {
        byte[] good = ComparisonJson.Write(Sample());
        var r = new Random(11);
        int rejected = 0, accepted = 0;
        for (int i = 0; i < 1500; i++)
        {
            byte[] bad = (byte[])good.Clone();
            switch (i % 4)
            {
                case 0:
                    bad = bad[..r.Next(bad.Length)];
                    break;
                case 1:
                    for (int k = r.Next(1, 4); k > 0; k--)
                    {
                        bad[r.Next(bad.Length)] = (byte)r.Next(256);
                    }

                    break;
                case 2:
                {
                    // Flip a digit or a quote so structure survives but values go wrong.
                    int at = r.Next(bad.Length);
                    for (int k = 0; k < bad.Length; k++)
                    {
                        int p = (at + k) % bad.Length;
                        if (bad[p] is >= (byte)'0' and <= (byte)'9')
                        {
                            bad[p] = (byte)('0' + r.Next(10));
                            break;
                        }
                    }

                    break;
                }

                default:
                {
                    int at = r.Next(bad.Length), len = r.Next(1, 40);
                    bad = [.. bad[..at], .. bad[Math.Min(bad.Length, at + len)..]];
                    break;
                }
            }

            try
            {
                Comparison c = ComparisonJson.Read(bad);
                _ = c.Check();
                accepted++;
            }
            catch (ComparisonFormatException)
            {
                rejected++;
            }
        }

        Assert.True(rejected > 100, $"only {rejected} of 1500 damaged inputs were rejected ({accepted} accepted)");
    }

    [Fact]
    [Trait("Host", "Native")] // reads the schema file from the repository
    public void SchemaFileNamesTheSameVersion()
    {
        string root = Path.GetFullPath(Path.Combine(SourceDir(), "..", ".."));
        string schema = File.ReadAllText(Path.Combine(root, "docs", "schema", "comparison-v1.json"));
        Assert.Contains("\"onediff.comparison\"", schema, StringComparison.Ordinal);
        Assert.Contains($"\"const\": {ComparisonJson.SchemaVersion}", schema, StringComparison.Ordinal);
    }

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string? file = null) => Path.GetDirectoryName(file!)!;
}
