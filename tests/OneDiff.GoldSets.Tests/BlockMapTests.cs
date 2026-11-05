using System.IO.Compression;
using System.Text;
using OneDiff.GoldSets.Docx;
using RecursiveAI.OneOffice.Documents;

namespace OneDiff.GoldSets.Tests;

/// <summary>Mapping a source's text onto gold blocks, and reading a tool's tracked-changes file into the scorer's form.</summary>
public class BlockMapTests
{
    private static BlockSide Side(params string[] texts) =>
        new([.. texts.Select((t, i) => new GoldBlock(i, "paragraph", string.Empty, t))]);

    [Fact]
    public void SquashIgnoresWhiteSpaceSoftHyphensAndDirectionMarks()
    {
        Assert.Equal("Payinthirtydays", Squash.Of("Pay in\u00AD thirty\n days\u200E"));
    }

    [Fact]
    public void NodesMapInOrderEvenWhenWhiteSpaceDiffers()
    {
        BlockSide gold = Side("The fee is due.", "Pay in thirty days.");
        NodeMap map = NodeMap.Build([(0, "The  fee is\ndue."), (1, "Pay in thirty days.")], gold, 2);
        Assert.Equal(2, map.Matched);
        Assert.Equal(0, map.Id(0));
        Assert.Equal(1, map.Id(1));
    }

    [Fact]
    public void ARepeatedTextPairsUpInOrder()
    {
        NodeMap map = NodeMap.Build([(0, "Same."), (1, "Same.")], Side("Same.", "Same."), 2);
        Assert.Equal(0, map.Id(0));
        Assert.Equal(1, map.Id(1));
    }

    [Fact]
    public void ALabelPrintedInFrontOfTheTextStillMatches()
    {
        var gold = new BlockSide([new GoldBlock(0, "item", "1.1 ", "Pay in thirty days.")]);
        NodeMap map = NodeMap.Build([(0, "1.1 Pay in thirty days.")], gold, 1);
        Assert.Equal(1, map.Matched);
        // An offset after the label lands at the start of the gold text, so intervals keep the characters they covered.
        Assert.Equal(0, map.Position(0, "1.1 ".Length, atEnd: false));
        Assert.Equal(3, map.Position(0, "1.1 Pay".Length, atEnd: true));
    }

    [Fact]
    public void AnUnmatchedSourceNodeGetsAnIdPastTheGoldNodes()
    {
        NodeMap map = NodeMap.Build([(0, "Known."), (1, "Invented by the tool.")], Side("Known."), 2);
        Assert.Equal(1, map.Matched);
        Assert.Equal(0, map.Id(0));
        Assert.True(map.Id(1) >= 1);
        Assert.Equal("Invented by the tool.", map.GoldText[map.Id(1)]);
    }

    [Fact]
    public void ToolOutputMapsIntoGoldCoordinatesWithOneGroupPerChange()
    {
        var gold = new BlockMap("t", "en", Side("Pay in 30 days."), Side("Pay in 60 days."));
        var output = new ToolOutput(ToolOutput.SchemaName, "t", "x", ["Pay in 30 days."], ["Pay in 60 days."], [new ToolLink("match", [0], [0])],
            [new ToolChange(7, "text", [new LineSpan(0, 7, 2)], [new LineSpan(0, 7, 2)])]);
        ScoreInput input = output.ToScoreInput(gold);
        SChange change = Assert.Single(input.Changes);
        Assert.Equal(7, change.Id);
        Assert.Contains(change.Extent, iv => !iv.IsNew && iv.Start == 7 && iv.End == 9);
        Assert.Contains(change.Extent, iv => iv.IsNew && iv.Start == 7 && iv.End == 9);
        Assert.Equal([[7]], input.Groups);
    }

    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

    private static byte[] Package(string body, bool withOverride)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }

            Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                (withOverride ? "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" : string.Empty) + "</Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add("word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document " + W + "><w:body>" + body + "</w:body></w:document>");
        }

        return ms.ToArray();
    }

    private static Document Tracked(string body) => Document.Open(new MemoryStream(Package(body, withOverride: true)), leaveOpen: false);

    [Fact]
    public void APackageThatTypesTheMainPartOnlyThroughTheXmlDefaultIsMadeOpenable()
    {
        // Some tools write the main part with no content-type override of its own; the reader adds the override and nothing else.
        byte[] bytes = Package("<w:p><w:r><w:t>Hello</w:t></w:r></w:p>", withOverride: false);
        using Document doc = Document.Open(new MemoryStream(OneDiff.GoldSets.Docx.Program.Openable(bytes)), leaveOpen: false);
        Assert.Contains("Hello", string.Concat(TrackedReader.ReadBody(doc).SelectMany(p => p.Segs).Select(s => s.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void AdjacentRevisionsInOneParagraphAreOneChangeAndUnchangedTextSplitsThem()
    {
        const string p = "<w:p><w:r><w:t xml:space=\"preserve\">Pay in </w:t></w:r>" +
            "<w:del w:id=\"1\" w:author=\"a\"><w:r><w:delText>30</w:delText></w:r></w:del>" +
            "<w:ins w:id=\"2\" w:author=\"a\"><w:r><w:t>60</w:t></w:r></w:ins>" +
            "<w:r><w:t xml:space=\"preserve\"> days and keep </w:t></w:r>" +
            "<w:ins w:id=\"3\" w:author=\"a\"><w:r><w:t>all</w:t></w:r></w:ins>" +
            "<w:r><w:t>.</w:t></w:r></w:p>";
        using Document doc = Tracked(p);
        ToolOutput output = TrackedReader.Read("t", "x", doc);
        Assert.Equal(["Pay in 30 days and keep ."], output.OldLines);
        Assert.Equal(["Pay in 60 days and keep all."], output.NewLines);
        Assert.Equal(2, output.Changes.Count);
        Assert.Single(output.Changes[0].Old);
        Assert.Single(output.Changes[0].New);
    }
}
