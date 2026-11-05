using System.Collections.Immutable;
using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Tests.Shared;

/// <summary>
/// The short pair of documents the sample redlines (HTML and PDF, under docs/samples) are made from: a list of clauses where one is
/// edited, one inserted, one deleted, one moved and one reformatted, and a quotation mark that only differs typographically.
/// Compiled into every test project that writes samples.
/// </summary>
internal static class SamplePair
{
    private const string PaymentText = "Payment is due within sixty days of receipt of the invoice.";

    public static Comparison Create()
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

}
