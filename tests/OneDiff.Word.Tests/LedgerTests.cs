using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Word.Tests;

public class LedgerTests
{
    private static DocSpec Base() => new DocSpec(
        new Para(new T("The supplier shall deliver on "), new Fld("DATE", "1 May 2026"), new T(".")) { Comment = "first thought" },
        Para.Of("Second paragraph.")) with
    {
        Title = "Draft",
        CustomXml = "<root>one</root>",
    };

    private static Comparison Compare(DocSpec a, DocSpec b, CompareSettings settings)
    {
        var source = new WordDocumentSource();
        return Comparison.Of(
            source.Read(DocumentInput.FromBytes(DocFactory.Build(a), "a.docx")),
            source.Read(DocumentInput.FromBytes(DocFactory.Build(b), "b.docx")),
            settings);
    }

    [Fact]
    public void CommentsPropertiesFieldCodesAndOpaquePartsGoToTheLedgerByDefault()
    {
        DocSpec a = Base();
        DocSpec b = a with
        {
            Title = "Final",
            CustomXml = "<root>two</root>",
            Blocks =
            [
                new Para(new T("The supplier shall deliver on "), new Fld("TIME", "1 May 2026"), new T(".")) { Comment = "second thought" },
                Para.Of("Second paragraph."),
            ],
        };

        Comparison legal = Compare(a, b, CompareSettings.Legal);
        Assert.True(legal.Verification.Passed);
        Assert.Empty(legal.Changes);
        foreach (LedgerCategory c in new[] { LedgerCategory.Comments, LedgerCategory.Properties, LedgerCategory.FieldCodes, LedgerCategory.NotItemized })
        {
            Assert.True(legal.Ledger.Get(c) is { Count: > 0 }, "ledger lacks " + c);
        }

        Comparison strict = Compare(a, b, CompareSettings.Strict);
        Assert.True(strict.Verification.Passed);
        Assert.NotEmpty(strict.Changes);
        Assert.Contains(strict.Changes, ch => ch.Old.Text.Contains("first", StringComparison.Ordinal));
        Assert.Contains(strict.Changes, ch => ch.Old.Text.Contains("Draft", StringComparison.Ordinal));
        Assert.True(strict.Ledger.Get(LedgerCategory.NotItemized) is { Count: > 0 });
    }

    [Fact]
    public void ChangedPageSetupAndFormattingAreFormattingChanges()
    {
        DocSpec a = new(new Para(new T("Keep this text.")));
        DocSpec b = new(new Para(new T("Keep this text.") { Bold = true, Color = "C00000" }) { Jc = "center" });
        Comparison legal = Compare(a, b, CompareSettings.Legal);
        Assert.True(legal.Verification.Passed);
        Assert.NotEmpty(legal.Changes);

        Comparison textOnly = Compare(a, b, CompareSettings.TextOnly);
        Assert.Empty(textOnly.Changes);
        Assert.True(textOnly.Ledger.Get(LedgerCategory.RunFormatting) is { Count: > 0 });
        Assert.True(textOnly.Ledger.Get(LedgerCategory.ParagraphFormatting) is { Count: > 0 });
    }

    [Fact]
    public void RenumberedListsAreAttributeChangesNotTextChanges()
    {
        DocSpec a = new(new Para(new T("Alpha clause.")) { List = 0 }, new Para(new T("Beta clause.")) { List = 0 }, new Para(new T("Gamma clause.")) { List = 0 }) { Lists = 1 };
        DocSpec b = new(new Para(new T("Alpha clause.")) { List = 0 }, new Para(new T("Gamma clause.")) { List = 0 }) { Lists = 1 };
        Comparison c = Compare(a, b, CompareSettings.Strict);
        Assert.True(c.Verification.Passed);
        Assert.Contains(c.Changes, ch => ch.Kind == ChangeKind.Delete && ch.Old.Text.Contains("Beta", StringComparison.Ordinal));
        Assert.Contains(c.Changes, ch => ch.AttributeEdits.Any(e => e.Key == "list.label"));
    }
}
