using System.Collections.Immutable;
using OneDiff.Documents.Html;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Tests.Html;

public class HtmlWriterTests
{
    internal static readonly RedlineStyle[] Styles = [RedlineStyle.Blackline, RedlineStyle.SideBySide, RedlineStyle.Word, RedlineStyle.Developer];

    /// <summary>Writes the comparison in every preset and both layouts, reads each page back, and checks it twice for identical bytes.</summary>
    internal static void AssertPages(Comparison c, string context = "")
    {
        foreach (RedlineStyle style in Styles)
        {
            foreach (bool sbs in new[] { false, true })
            {
                string html = sbs ? HtmlRedline.SideBySide(c, style) : HtmlRedline.Inline(c, style);
                List<string> errors = HtmlGate.Check(c, html, sbs);
                Assert.True(errors.Count == 0, $"{context} {style.Name} {(sbs ? "side-by-side" : "inline")}: " + string.Join(" | ", errors));
                string again = sbs ? HtmlRedline.SideBySide(c, style) : HtmlRedline.Inline(c, style);
                Assert.Equal(html, again);
            }
        }
    }

    private static Comparison Plain(string a, string b, CompareSettings? s = null) => Helpers.CompareText(a, b, s);

    [Fact]
    public void WordEditsAndTypographyReadBackExactly()
    {
        Comparison c = Plain(
            "The supplier shall deliver within thirty days.\n\nPayment is due in sixty days.\n\nHe said \u201Chi\u201D.",
            "The supplier shall deliver within forty days of the order.\n\nPayment is due in sixty days.\n\nHe said \"hi\".");
        Assert.NotEmpty(c.Changes);
        Assert.True(c.Ledger.TotalCount > 0);
        AssertPages(c);
        string html = HtmlRedline.Inline(c);
        Assert.Contains("<ins", html, StringComparison.Ordinal);
        Assert.Contains("<del", html, StringComparison.Ordinal);
        Assert.Contains("Differences not shown", html, StringComparison.Ordinal);
        Assert.Contains("quotes", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InsertedAndDeletedParagraphsAreWholeBlocks()
    {
        Comparison c = Plain(
            "Alpha paragraph stays exactly as it is in both versions.\n\nThis paragraph about the landlord is deleted completely from the second version.\n\nOmega paragraph also stays exactly as it is.",
            "Alpha paragraph stays exactly as it is in both versions.\n\nOmega paragraph also stays exactly as it is.\n\nA brand new closing paragraph about arbitration is inserted at the end.");
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Delete && x.LinkId < 0);
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Insert && x.LinkId < 0);
        AssertPages(c);
        string html = HtmlRedline.Inline(c);
        int del = html.IndexOf("landlord", StringComparison.Ordinal), omega = html.IndexOf("Omega", StringComparison.Ordinal);
        Assert.True(del > 0 && del < omega, "the deleted paragraph shows where it used to be, before the paragraph that followed it");
    }

    [Fact]
    public void MovedBlocksShowAtBothPlacesAndLink()
    {
        string p1 = "The first clause deals with payment terms and the schedule of milestones in some detail.";
        string p2 = "The second clause deals with confidentiality of all information exchanged between the parties.";
        string p3 = "The third clause deals with termination for breach and the cure period that applies to it.";
        string p4 = "The fourth clause deals with governing law and the courts that may hear any dispute.";
        Comparison c = Plain(string.Join("\n\n", p1, p2, p3, p4), string.Join("\n\n", p1, p3, p4, p2));
        Change move = Assert.Single(c.Changes, x => x.Kind == ChangeKind.Move);
        AssertPages(c);
        string html = HtmlRedline.Inline(c);
        Assert.Contains($"id=\"mvf-{move.Id}\"", html, StringComparison.Ordinal);
        Assert.Contains($"id=\"mvt-{move.Id}\"", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"#mvt-{move.Id}\"", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"#mvf-{move.Id}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitsAndMergesStayVisible()
    {
        string a = "The tenant shall pay the rent on the first day of every month without any deduction.";
        string b = "The landlord shall keep the building insured against fire for its full replacement value.";
        Comparison merged = Plain(a + "\n\n" + b, a + " " + b);
        Comparison split = Plain(a + " " + b, a + "\n\n" + b);
        foreach (Comparison c in new[] { merged, split })
        {
            Assert.Contains(c.Changes, x => x.Kind is ChangeKind.Split or ChangeKind.Merge);
            AssertPages(c);
        }
    }

    [Fact]
    public void FormattingAndAttributeChangesAreMarked()
    {
        FormatSet bold = FormatSet.Of(FormatKeys.Bold, "true");
        CompareDocument Doc(ImmutableArray<FormatRun> runs, string label)
        {
            var b = new CompareDocumentBuilder(DocumentKind.Flowing);
            b.Begin(NodeKind.Section, new NodeContent { Anchor = Anchor.ForNode("s") });
            b.Add(NodeKind.ListItem, new NodeContent
            {
                Text = "The supplier shall deliver within thirty days.",
                Runs = runs,
                Attributes = FormatSet.Of(FormatKeys.ListLabel, label, FormatKeys.ListLevel, "1"),
                Anchor = Anchor.ForNode("p1"),
            });
            b.End();
            return b.Build();
        }

        Comparison c = Helpers.Compare(Doc([], "1.1"), Doc([new FormatRun(4, 8, bold)], "1.2"), CompareSettings.Strict with { ThrowOnFailure = false });
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Format);
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Attribute);
        AssertPages(c);
        string html = HtmlRedline.Inline(c);
        Assert.Contains("class=\"fmt\"", html, StringComparison.Ordinal);
        Assert.Contains("data-key=\"list.label\"", html, StringComparison.Ordinal);
        Assert.Contains("List number: 1.1 \u2192 1.2", html, StringComparison.Ordinal);
    }

    [Fact]
    public void HostileTextIsEscapedAndReadsBackExactly()
    {
        string evil = "<script>alert(1)</script> & \"q\" 'a' </p><img src=x onerror=alert(2)> \u0001\u001B\u007F\u0085 a\rb \uFFFE \uD83D\uDE00 ok";
        CompareDocument Doc(string text, string name)
        {
            var b = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(name);
            b.Begin(NodeKind.Section, new NodeContent { Anchor = Anchor.ForNode("s") });
            b.Add(NodeKind.Paragraph, new NodeContent { Text = text, Anchor = Anchor.ForNode("p") });
            b.Add(NodeKind.Paragraph, new NodeContent { Text = "A <b>second</b> paragraph that is unchanged and long enough to align.", Anchor = Anchor.ForNode("q") });
            b.End();
            return b.Build();
        }

        Comparison c = Helpers.Compare(Doc(evil, "<b onload=x>\"old\".docx"), Doc(evil.Replace("alert(1)", "alert(3)", StringComparison.Ordinal) + " <i>tail</i>", "new&name.docx"));
        AssertPages(c);
        string html = HtmlRedline.Inline(c, RedlineStyle.Blackline with { Title = "<script>x</script>", Language = "en\" onload=\"x" });
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("onload=\"x\"", html, StringComparison.Ordinal);
        Assert.Contains("lang=\"en\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TablesNestAndRowsCanBeInsertedAndDeleted()
    {
        CompareDocument Doc(params string[][] rows)
        {
            var b = new CompareDocumentBuilder(DocumentKind.Flowing);
            b.Begin(NodeKind.Section, new NodeContent { Anchor = Anchor.ForNode("s") });
            b.Begin(NodeKind.Table, new NodeContent { Anchor = Anchor.ForNode("t") });
            foreach (string[] row in rows)
            {
                b.Begin(NodeKind.TableRow, new NodeContent { Anchor = Anchor.ForNode("r") });
                foreach (string cell in row)
                {
                    b.Begin(NodeKind.TableCell, new NodeContent { Anchor = Anchor.ForNode("c") });
                    b.Add(NodeKind.Paragraph, new NodeContent { Text = cell, Anchor = Anchor.ForNode("p") });
                    b.End();
                }

                b.End();
            }

            b.End();
            b.End();
            return b.Build();
        }

        Comparison c = Helpers.Compare(
            Doc(["Item", "Price"], ["Widgets supplied under the agreement", "100 dollars per unit"], ["Gadgets supplied under the agreement", "250 dollars per unit"]),
            Doc(["Item", "Price"], ["Widgets supplied under the agreement", "120 dollars per unit"], ["Sprockets supplied under the agreement", "75 dollars per unit"], ["Total", "all amounts exclusive of tax"]));
        AssertPages(c);
        string html = HtmlRedline.Inline(c);
        Assert.Contains("<table", html, StringComparison.Ordinal);
        Assert.Contains("<td", html, StringComparison.Ordinal);
    }

    [Fact]
    public void IdenticalDocumentsGiveAPageWithNoChanges()
    {
        Comparison c = Plain("Nothing changes here at all.", "Nothing changes here at all.");
        AssertPages(c);
        Assert.Contains("No changes were found", HtmlRedline.Inline(c), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyDocumentsAndOneSidedPairsWork()
    {
        AssertPages(Plain("", ""));
        AssertPages(Plain("", "Everything here is new."));
        AssertPages(Plain("Everything here is old.", ""));
    }

    [Fact]
    public void FurnitureFollowsTheStyle()
    {
        Comparison c = Helpers.Compare(CompareDocument.FromText("One two three.", "old.txt"), CompareDocument.FromText("One two four.", "new.txt"));
        string full = HtmlRedline.Inline(c, RedlineStyle.Blackline with { Timestamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), TypeTags = true });
        Assert.Contains("class=\"cover\"", full, StringComparison.Ordinal);
        Assert.Contains("Compared by OneDiff", full, StringComparison.Ordinal);
        Assert.Contains("old.txt", full, StringComparison.Ordinal);
        Assert.Contains(c.Provenance.OldHash, full, StringComparison.Ordinal);
        Assert.Contains("2026-03-04 05:06:07 +00:00", full, StringComparison.Ordinal);
        Assert.Contains("class=\"tag\"", full, StringComparison.Ordinal);
        Assert.Contains(".bar{", full, StringComparison.Ordinal);

        string bare = HtmlRedline.Inline(c, RedlineStyle.Blackline with { CoverPage = false, Footer = false, ChangeBars = false });
        Assert.DoesNotContain("class=\"cover\"", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("<footer>", bare, StringComparison.Ordinal);
        Assert.DoesNotContain(".bar{", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"tag\"", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("Compared at", bare, StringComparison.Ordinal);
    }

    [Fact]
    public void PresetsDifferOnlyInStyleValuesAndGlyphsAppearOnlyWhereTheStyleHasThem()
    {
        Comparison c = Plain("One two three.", "One two four.");
        string blackline = HtmlRedline.Inline(c, RedlineStyle.Blackline);
        string sbs = HtmlRedline.Inline(c, RedlineStyle.SideBySide);
        Assert.DoesNotContain("class=\"gl\"", blackline.Split("<body")[1], StringComparison.Ordinal);
        Assert.Contains("<span class=\"gl\" aria-hidden=\"true\">+</span>", sbs, StringComparison.Ordinal);
        Assert.Contains("text-decoration:underline double #0000ff", blackline, StringComparison.Ordinal);
        Assert.Contains("background:#", sbs, StringComparison.Ordinal);
    }

    [Fact]
    public void StyleColoursCannotInjectCss()
    {
        Comparison c = Plain("One two three.", "One two four.");
        string html = HtmlRedline.Inline(c, RedlineStyle.Blackline with { Insertion = new MarkStyle("red;}</style><script>x</script>", MarkDecoration.Underline) });
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("red;}", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGateCatchesTamperedPages()
    {
        Comparison c = Plain(
            "The supplier shall deliver within thirty days.\n\nPayment is due in sixty days.\n\nA third paragraph about insurance that is deleted from the second version entirely.",
            "The supplier shall deliver within forty days.\n\nPayment is due in sixty days.\n\nA closing paragraph about arbitration that is new in the second version only.");
        foreach (bool sbs in new[] { false, true })
        {
            string html = sbs ? HtmlRedline.SideBySide(c) : HtmlRedline.Inline(c);
            Assert.Empty(HtmlGate.Check(c, html, sbs));
            Assert.NotEmpty(HtmlGate.Check(c, html.Replace("forty", "fourty", StringComparison.Ordinal), sbs));
            Assert.NotEmpty(HtmlGate.Check(c, html.Replace("thirty", "thirteen", StringComparison.Ordinal), sbs));
            Assert.NotEmpty(HtmlGate.Check(c, html.Replace("Payment is due", "Payment was due", StringComparison.Ordinal), sbs));
            Assert.NotEmpty(HtmlGate.Check(c, html.Replace("data-change=\"0\"", "data-change=\"7\"", StringComparison.Ordinal), sbs));
            Assert.NotEmpty(HtmlGate.Check(c, html.Replace("</body>", "<script>x</script></body>", StringComparison.Ordinal), sbs));
            int firstIns = html.IndexOf("<ins", StringComparison.Ordinal);
            int endIns = html.IndexOf("</ins>", firstIns, StringComparison.Ordinal) + 6;
            Assert.NotEmpty(HtmlGate.Check(c, html.Remove(firstIns, endIns - firstIns), sbs));
            Assert.NotEmpty(HtmlGate.Check(c, html.Insert(endIns, html[firstIns..endIns]), sbs));
        }
    }

    [Fact]
    public void WritersAreCancellable()
    {
        Comparison c = Plain("One two three.", "One two four.");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => HtmlRedline.Inline(c, null, cts.Token));
        Assert.Throws<OperationCanceledException>(() => HtmlRedline.SideBySide(c, null, cts.Token));
    }

    [Fact]
    public void StreamOutputIsUtf8WithoutBomAndLfLines()
    {
        Comparison c = Plain("Ünïcödé 日本語 text here.", "Ünïcödé 日本語 text there.");
        using var ms = new MemoryStream();
        HtmlRedline.WriteInline(c, ms);
        byte[] bytes = ms.ToArray();
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'<', bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(HtmlRedline.Inline(c), System.Text.Encoding.UTF8.GetString(bytes));
    }
}
