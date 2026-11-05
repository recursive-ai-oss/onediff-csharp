using System.Globalization;
using OneDiff.Documents.Model;
using RecursiveAI.OneOffice.Documents;
using RecursiveAI.OneOffice.IO;
using RecursiveAI.OneOffice.Markup;
using OneDiff.Word.Internal;

namespace OneDiff.Word.Tests;

internal static class Harness
{
    public static CompareDocument Read(byte[] docx, string name = "x.docx", WordSourceOptions? options = null) =>
        new WordDocumentSource(options ?? WordSourceOptions.Default).Read(DocumentInput.FromBytes(docx, name));

    public static IEnumerable<Node> Of(CompareDocument d, NodeKind kind) => d.Nodes.Where(n => n.Kind == kind);

    public static DocSpec Rich()
    {
        return new DocSpec(
            Para.Of("Master Agreement") with { Style = "Heading1" },
            new Para(new T("The "), new T("supplier") { Bold = true }, new T(" shall deliver within "), new T("thirty") { Italic = true, Color = "FF0000" }, new T(" days, see "), new T("the website") { Link = "https://example.com/terms" }, new T(".")),
            new Para(new T("Clause one") { Font = "Arial", Size = 14 }) { List = 0 },
            new Para(new T("Sub clause")) { List = 0, Level = 1 },
            new Para(new T("Bullet point")) { List = 1 },
            new Tbl(
                [new Cell(Para.Of("Item")), new Cell(Para.Of("Price"))],
                [new Cell(Para.Of("Widget")), new Cell(Para.Of("10"))],
                [new Cell(Para.Of("Total")) { Span = 2 }]),
            new Para(new T("Payable on "), new Fld("DATE \\@ \"d MMMM yyyy\"", "4 November 2026"), new T(" and see note"), new Foot("A footnote about payment."), new Foot("An endnote about law.", End: true)),
            new Para(new T("A picture: "), new Img(DocFactory.Png(10), "grey square"), new T(" and a box "), new Box("Inside the box")),
            new Para(new T("Controlled "), new Cc("value", "tag1"), new T(" text.")) { Comment = "Check this" } with { },
            new Sdt("block1", Para.Of("Inside a block control")),
            new Para(new T("Tab"), new TabChar(), new T("and"), new Brk(), new T("break")),
            new Para(new T("Second section")) { SectionBreak = true },
            new Para(new T("Landscape part"))) with
        {
            Lists = 1,
            Bullets = true,
            Header = "Confidential header",
            Footer = "Page footer",
            Title = "Agreement",
            Author = "Alice",
        };
    }

    public static void Resolves(byte[] docx, CompareDocument model)
    {
        using Document doc = Document.Open(ByteSource.FromMemory(docx));
        MarkupNode root = doc.MainMarkup.Root;
        var paragraphs = new Dictionary<MarkupNode, Paragraph>();
        foreach (Paragraph p in doc.Body.Paragraphs())
        {
            paragraphs[p.Markup.Node] = p;
        }

        foreach (Node n in model.Nodes)
        {
            if (n.Anchor.Kind != AnchorKind.Node || !WordAnchors.TryParse(n.Anchor.Reference, out string part, out int[] path, out _, out _))
            {
                continue;
            }

            if (part != doc.MainPartName)
            {
                continue;
            }

            MarkupNode e = WordAnchors.Follow(root, path);
            Assert.False(e.IsNull, n.Kind + " " + n.Anchor.Reference);
            string local = e.LocalNameString;
            string expected = n.Kind switch
            {
                NodeKind.Paragraph or NodeKind.ListItem => "p",
                NodeKind.Table => "tbl",
                NodeKind.TableRow => "tr",
                NodeKind.TableCell => "tc",
                _ => local,
            };
            Assert.Equal(expected, local);
            if (n.Kind is NodeKind.Paragraph or NodeKind.ListItem && n.Folds.IsDefaultOrEmpty && paragraphs.TryGetValue(e, out Paragraph? hp))
            {
                string hubText = hp.Text;
                foreach (AnchoredSpan s in n.Segments)
                {
                    Assert.True(WordAnchors.TryParse(s.Anchor.Reference, out _, out int[] runPath, out int off, out int len));
                    MarkupNode run = WordAnchors.Follow(root, runPath);
                    Assert.False(run.IsNull);
                    string segText = n.Text.Substring(s.Start, s.Length);
                    if (segText != "\uFFFC")
                    {
                        Assert.Equal(hubText.Substring(off, len), segText);
                        Assert.Equal(len, s.Length);
                    }
                }
            }
        }
    }

    public static string Digest(IEnumerable<byte[]> items)
    {
        using var h = System.Security.Cryptography.SHA256.Create();
        foreach (byte[] b in items)
        {
            h.TransformBlock(b, 0, b.Length, null, 0);
        }

        h.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(h.Hash!);
    }

    public static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
