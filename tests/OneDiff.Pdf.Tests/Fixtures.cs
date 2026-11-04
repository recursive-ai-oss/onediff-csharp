using OnePdf;
using OnePdf.Hyphenation;
using OnePdf.Layout;

namespace OneDiff.Pdf.Tests;

/// <summary>Builds the PDFs the tests read, from text written here, with OnePDF's layout engine.</summary>
internal static class Fixtures
{
    /// <summary>What a fixture page looks like.</summary>
    internal sealed record Spec
    {
        public string[] Paragraphs { get; init; } = [];
        public string? Header { get; init; }
        public bool PageNumbers { get; init; }
        public int Columns { get; init; } = 1;
        public bool Justify { get; init; }
        public double Size { get; init; } = 11;
        public double PageHeight { get; init; } = 842;
        public string[]? BulletedList { get; init; }
        public string[]? NumberedList { get; init; }
        public string? FontFamily { get; init; }
        public bool Rtl { get; init; }
        public string? FontFile { get; init; }
        public bool Hyphenate { get; init; }
        public double PageWidth { get; init; } = 595;
        public bool Bold { get; init; }
        public double[]? ParagraphSizes { get; init; }
    }

    public static byte[] Make(Spec spec)
    {
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            d.Title("fixture");
            if (spec.FontFile is not null)
            {
                d.Fonts.Add(File.ReadAllBytes(spec.FontFile), "TestFace");
                d.Fonts.FallbackFamilies.Add("TestFace");
            }

            d.Page(page =>
            {
                page.Size(spec.PageWidth, spec.PageHeight);
                page.Margin(54);
                if (spec.Rtl)
                {
                    page.RightToLeft();
                }

                if (spec.Header is not null)
                {
                    page.Header().PaddingBottom(24).AlignCenter().Text(spec.Header).FontSize(9);
                }

                if (spec.PageNumbers)
                {
                    page.Footer().PaddingTop(24).AlignCenter().Text(t =>
                    {
                        t.FontSize(9);
                        t.Span("Page ");
                        t.CurrentPageNumber();
                        t.Span(" of ");
                        t.TotalPages();
                    });
                }

                Slot content = page.Content();
                if (spec.Columns > 1)
                {
                    content.MultiColumn(cols =>
                    {
                        cols.Count(spec.Columns).Gap(18);
                        foreach (string p in spec.Paragraphs)
                        {
                            Para(cols.Item(), p, spec);
                        }
                    });
                }
                else
                {
                    content.Column(col =>
                    {
                        col.Spacing(10);
                        foreach (string p in spec.Paragraphs)
                        {
                            Para(col.Item(), p, spec);
                        }

                        if (spec.BulletedList is not null)
                        {
                            col.Item().List(l =>
                            {
                                l.Bulleted();
                                foreach (string s in spec.BulletedList)
                                {
                                    Para(l.Item(), s, spec);
                                }
                            });
                        }

                        if (spec.NumberedList is not null)
                        {
                            col.Item().List(l =>
                            {
                                l.Numbered();
                                foreach (string s in spec.NumberedList)
                                {
                                    Para(l.Item(), s, spec);
                                }
                            });
                        }
                    });
                }
            });
        });
        return doc.ToBytes(new PdfDocumentWriterOptions { DocumentId = new byte[16] });
    }

    private static void Para(Slot slot, string text, Spec spec)
    {
        TextDescriptor t = slot.Text(text).FontSize(spec.Size);
        if (spec.Bold)
        {
            t.Bold();
        }

        if (spec.FontFile is not null)
        {
            t.FontFamily("TestFace");
        }

        if (spec.Hyphenate)
        {
            t.Hyphenator(Hyphenators.For("en")!);
        }

        if (spec.Justify)
        {
            t.Justify();
        }
    }
}
