using System.Collections.Immutable;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Pdf.Internal;
using OnePdf;
using OnePdf.Text;

namespace OneDiff.Pdf.Tests;

/// <summary>An annotation read back from a marked-up PDF, with the text that lies under its quadrilaterals.</summary>
internal sealed record ReadAnnotation(int Page, string Subtype, string? Name, string? Contents, string? Title, int QuadCount, string TextUnder, bool HasPopup);

/// <summary>Compares two generated PDFs, marks the result, reads the marks back, and checks them against the change list.</summary>
internal sealed class Run
{
    public required byte[] OldPdf { get; init; }

    public required byte[] NewPdf { get; init; }

    public required Comparison Comparison { get; init; }

    public required byte[] Marked { get; init; }

    public required PdfMarkupResult Result { get; init; }

    public static CompareDocument Read(byte[] pdf, string name = "") =>
        new PdfDocumentSource().Read(DocumentInput.FromBytes(pdf, name));

    public static Run Of(byte[] oldPdf, byte[] newPdf, CompareSettings? settings = null, PdfRedline? redline = null)
    {
        Comparison c = Comparison.Of(Read(oldPdf, "old.pdf"), Read(newPdf, "new.pdf"), settings);
        using var ms = new MemoryStream();
        PdfMarkupResult r = (redline ?? PdfRedline.OnPages).MarkNewDocument(c, newPdf, ms);
        return new Run { OldPdf = oldPdf, NewPdf = newPdf, Comparison = c, Marked = ms.ToArray(), Result = r };
    }

    /// <summary>Verification passed on the first try, nothing failed a re-check.</summary>
    public void AssertComplete()
    {
        Assert.True(Comparison.Verification.Passed);
        Assert.Equal(0, Comparison.Verification.FallbackLevel);
        Assert.Empty(Comparison.Check());
        Assert.DoesNotContain(Comparison.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>The annotations of a PDF other than pop-ups.</summary>
    public static List<ReadAnnotation> ReadAnnotations(byte[] pdfBytes, string? password = null)
    {
        var list = new List<ReadAnnotation>();
        using PdfDocument pdf = PdfDocument.Open(pdfBytes, new PdfOpenOptions { Password = password });
        IReadOnlyList<PdfPageText> texts = pdf.ExtractText(new PdfTextOptions { RetainGlyphs = true, DetectHeadersAndFooters = false, MaxDegreeOfParallelism = 1 });
        for (int p = 0; p < pdf.PageCount; p++)
        {
            PdfPage page = pdf.Pages[p];
            PageGeometry g = PageGeometry.Of(page);
            foreach (PdfAnnotation a in page.Annotations)
            {
                if (a.Subtype is "Popup" or "Link")
                {
                    continue;
                }

                IReadOnlyList<double> qp = a.QuadPoints;
                var rects = new List<(double L, double B, double R, double T)>();
                for (int i = 0; i + 7 < qp.Count; i += 8)
                {
                    // UL, UR, LL, LR in user space, to upright space.
                    var pts = new[] { g.ToUpright(qp[i], qp[i + 1]), g.ToUpright(qp[i + 2], qp[i + 3]), g.ToUpright(qp[i + 4], qp[i + 5]), g.ToUpright(qp[i + 6], qp[i + 7]) };
                    rects.Add((pts.Min(t => t.X), pts.Min(t => t.Y), pts.Max(t => t.X), pts.Max(t => t.Y)));
                }

                list.Add(new ReadAnnotation(p, a.Subtype, a.Name, a.Contents, a.Title, rects.Count, TextUnder(texts[p], rects), a.Popup is not null));
            }
        }

        return list;
    }

    /// <summary>The words of a page, in reading order, cut to the glyphs whose centres lie inside any of the rectangles.</summary>
    private static string TextUnder(PdfPageText page, List<(double L, double B, double R, double T)> rects)
    {
        if (rects.Count == 0)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (PdfTextBlock block in page.Blocks)
        {
            foreach (PdfTextLine line in block.Lines)
            {
                foreach (PdfTextWord w in line.Words)
                {
                    var picked = new List<string>();
                    foreach (int gi in w.GlyphIndices)
                    {
                        PdfTextGlyph gl = page.Glyphs[gi];
                        PdfQuad q = gl.Quad;
                        double cx = (q.X0 + q.X2) / 2, cy = (q.Y0 + q.Y2) / 2;
                        if (rects.Any(r => cx >= r.L && cx <= r.R && cy >= r.B && cy <= r.T))
                        {
                            picked.Add(gl.Text);
                        }
                    }

                    if (picked.Count == 0)
                    {
                        continue;
                    }

                    // Glyphs come in visual order; a word that is wholly inside reads as the extraction's logical text.
                    if (picked.Count == w.GlyphIndices.Count)
                    {
                        parts.Add(w.Text);
                    }
                    else
                    {
                        parts.Add(line.Direction == PdfTextDirection.RightToLeft ? LogicalOrder(picked) : string.Concat(picked));
                    }
                }
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Visual-order glyphs of a right-to-left line back to logical order: the order reverses, but a run of digits or Latin letters
    /// inside it reads left to right in both orders and keeps its own direction.
    /// </summary>
    private static string LogicalOrder(List<string> visual)
    {
        static bool Ltr(string g) => g.Length > 0 && (char.IsAsciiLetterOrDigit(g[0]) || g[0] == '.' || g[0] == ',');
        var sb = new System.Text.StringBuilder();
        int i = visual.Count - 1;
        while (i >= 0)
        {
            if (!Ltr(visual[i]))
            {
                sb.Append(visual[i--]);
                continue;
            }

            // A left-to-right run, read from its left end (the higher index in the reversed walk) to its right end.
            int end = i;
            while (i >= 0 && Ltr(visual[i]))
            {
                i--;
            }

            for (int k = i + 1; k <= end; k++)
            {
                sb.Append(visual[k]);
            }
        }

        return sb.ToString();
    }

    /// <summary>Spaces, line-end hyphens and typographic variants do not count when text under a highlight is compared with a change's text.</summary>
    public static string Canon(string s)
    {
        string t = TextNormalization.Normalize(s, [TypographicNormalizer.Instance]).Text;
        return new string([.. t.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '\u00AD' && c != '\u001E')]);
    }

    /// <summary>The re-extraction gate: every annotation maps to one change, every shown change is marked, and the text under each highlight and each pop-up is the change's.</summary>
    public void AssertReextraction()
    {
        List<ReadAnnotation> read = ReadAnnotations(Marked);
        Assert.Equal(Result.Marks.Length, read.Count);
        var byName = read.ToDictionary(a => a.Name!);
        Assert.Equal(read.Count, byName.Count);
        var changes = Comparison.Changes.ToDictionary(c => c.Id);

        // Every change is marked or skipped, exactly once.
        var seen = new HashSet<int>(Result.Marks.Select(m => m.ChangeId));
        foreach (PdfSkippedChange s in Result.Skipped)
        {
            Assert.DoesNotContain(s.ChangeId, seen);
            Assert.True(changes.ContainsKey(s.ChangeId));
            Change sc = changes[s.ChangeId];
            Assert.True(sc.Old.Text.Length == 0 && sc.New.Text.Length == 0, $"change {sc.Id} ({sc.Kind}) was skipped: {s.Reason}");
            seen.Add(s.ChangeId);
        }

        Assert.Equal(Comparison.Changes.Select(c => c.Id).Order(), seen.Order());

        foreach (PdfMark m in Result.Marks)
        {
            ReadAnnotation a = byName[m.Name];
            Change c = changes[m.ChangeId];
            Assert.Equal(m.Page, a.Page);
            Assert.Equal(m.Kind.ToString(), a.Subtype);
            Assert.Equal("OneDiff", a.Title);
            Assert.True(a.HasPopup);
            (string label, string body) = PdfRedline.SplitContents(a.Contents!);
            Assert.False(string.IsNullOrEmpty(label));
            Assert.Equal(c.Old.Text, body);
            if (m.Kind != PdfMarkupKind.Caret)
            {
                // The text under the quadrilaterals of every annotation of the change, taken together, is the change's new text.
                string under = string.Concat(Result.Marks.Where(x => x.ChangeId == c.Id && x.Kind != PdfMarkupKind.Caret).OrderBy(x => x.Page).ThenBy(x => x.Name, StringComparer.Ordinal).Select(x => byName[x.Name].TextUnder));
                string expected = Canon(c.Kind == ChangeKind.Attribute ? AttributeMarkText(c.New, Comparison.New) : ExpectedPageText(c.New, Comparison.New, Comparison.Alignment.Settings)), got = Canon(under);
                // A change that is only a space across a line break rests on the word before it; there is no text to compare.
                bool spaceOnly = expected.Length == 0 && c.New.Text.All(ch => char.IsWhiteSpace(ch) || ch == '\u001E');
                Assert.True(spaceOnly || expected == got, $"change {c.Id} {c.Kind} mark {m.Kind} page {m.Page}: expected [{Clip(expected)}] got [{Clip(got)}]");
            }
            else
            {
                Assert.Equal(0, a.QuadCount);
            }
        }
    }

    private static string Clip(string s) => s.Length <= 90 ? s : s[..90] + "...";

    /// <summary>An attribute change marks its whole block's text (the list marker is an attribute, not text) as the source had it.</summary>
    private static string AttributeMarkText(ChangeSide side, CompareDocument doc) =>
        string.Concat(side.Nodes.Select(n => doc[n].RawText));

    /// <summary>
    /// What the page shows for a change side. The side's range is in the compared text, which is the node's text with the folds
    /// of categories that are not hidden undone; the page shows the original characters of every fold the range touches (a folded
    /// page number is the digits on the page). A whitespace-only side has no text to check, so it returns empty.
    /// </summary>
    private static string ExpectedPageText(ChangeSide side, CompareDocument doc, CompareSettings settings)
    {
        if (side.Nodes.Length != 1 || side.Length == 0)
        {
            return side.Text;
        }

        Node node = doc[side.Nodes[0]];
        // Pieces of the compared text, each with the characters the page shows for it.
        var shown = new System.Text.StringBuilder();
        int projected = 0, pos = 0, from = side.Start, to = side.Start + side.Length;
        void Piece(string compared, string page)
        {
            int a = projected, b = projected + compared.Length;
            projected = b;
            if (b <= from || a >= to)
            {
                return;
            }

            if (compared == page)
            {
                shown.Append(page, Math.Max(from, a) - a, Math.Min(to, b) - Math.Max(from, a));
            }
            else
            {
                shown.Append(page);
            }
        }

        foreach (NormalizationFold f in node.Folds)
        {
            if (f.Start > pos)
            {
                Piece(node.Text.Substring(pos, f.Start - pos), node.Text.Substring(pos, f.Start - pos));
            }

            if (settings.Hidden.Contains(f.Category))
            {
                Piece(node.Text.Substring(f.Start, f.Length), f.Original);
            }
            else
            {
                Piece(f.Original, f.Original);
            }

            pos = f.Start + f.Length;
        }

        if (pos < node.Text.Length)
        {
            Piece(node.Text.Substring(pos), node.Text.Substring(pos));
        }

        return shown.ToString();
    }

    public ImmutableArray<Change> Of(ChangeKind kind) => [.. Comparison.Changes.Where(c => c.Kind == kind)];
}
