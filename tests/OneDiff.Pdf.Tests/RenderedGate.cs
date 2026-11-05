using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OnePdf;
using OnePdf.Text;

namespace OneDiff.Pdf.Tests;

/// <summary>
/// Reads a rendered redline back with OnePDF and checks it against the comparison it was made from.
/// <para>
/// Inline pages: every glyph is put in a class by its fill colour (inserted, deleted, moved from, moved to, formatting, plain), which
/// needs a style whose colours differ (<see cref="Style"/>). Taking out the classes that are not in the new document leaves the new
/// document's text, and taking out those not in the old one leaves the old text: each block's text must be found, in order, in the
/// body of the page (after the key). Each edit's inserted text must be in the inserted class and its deleted text in the deleted
/// class. Side-by-side pages: the left half of the page must hold the old blocks in order and the right half the new blocks.
/// </para>
/// <para>
/// What the extraction cannot see: underlines, strike-throughs and highlights are drawn as shapes, not as text, so a style whose marks
/// differ only by decoration (the Word preset's insertions and deletions are both red) cannot be told apart here; those pages are
/// checked for text only.
/// </para>
/// </summary>
internal static class RenderedGate
{
    internal enum Cls { Plain, Ins, Del, MoveFrom, MoveTo, Fmt }

    /// <summary>The blackline with a colour of its own for each kind of mark (the preset's two moves share one).</summary>
    public static RedlineStyle Style { get; } = RedlineStyle.Blackline with
    {
        Name = "Gate",
        MoveFrom = new MarkStyle("#FF8000", MarkDecoration.DoubleStrikeThrough),
        CoverPage = false,
    };

    private const double BottomMargin = 56;

    internal sealed record Piece(char Ch, Cls Class);

    /// <summary>The characters of the document in reading order, each with its class; whitespace is kept (as plain) between words and lines.</summary>
    internal sealed record Extracted(List<Piece> Pieces, List<(double X, char Ch)> Columns, int Pages, int InvisibleGlyphs, int Glyphs);

    private static Cls ClassOf(int color, RedlineStyle s)
    {
        string hex = "#" + (color & 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);
        if (Eq(hex, s.Insertion.Color))
        {
            return Cls.Ins;
        }

        if (Eq(hex, s.Deletion.Color))
        {
            return Cls.Del;
        }

        if (Eq(hex, s.MoveFrom.Color))
        {
            return Cls.MoveFrom;
        }

        if (Eq(hex, s.MoveTo.Color))
        {
            return Cls.MoveTo;
        }

        return Eq(hex, s.Formatting.Color) ? Cls.Fmt : Cls.Plain;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static Extracted Extract(byte[] pdfBytes, RedlineStyle style)
    {
        var pieces = new List<Piece>();
        var columns = new List<(double X, char Ch)>();
        int invisible = 0, glyphs = 0;
        using PdfDocument pdf = PdfDocument.Open(pdfBytes);
        IReadOnlyList<PdfPageText> texts = pdf.ExtractText(new PdfTextOptions { RetainGlyphs = true, DetectHeadersAndFooters = false, UseStructureTree = false, MaxDegreeOfParallelism = 1 });
        foreach (PdfPageText page in texts)
        {
            // The order the extraction gives its blocks follows the structure tree or its own layout analysis; the gate wants the
            // order a reader sees, so lines are put in rows from the top of the page, each row left to right.
            var lines = new List<PdfTextLine>();
            foreach (PdfTextBlock block in page.Blocks)
            {
                lines.AddRange(block.Lines);
            }

            foreach (PdfTextLine line in InRows(lines))
            {
                foreach (PdfTextWord w in line.Words)
                {
                    // The running page number sits in the bottom margin (20 mm = 56.7 pt), outside the page's content.
                    if (w.Bounds.Top < BottomMargin)
                    {
                        continue;
                    }

                    var gl = new List<PdfTextGlyph>(w.GlyphIndices.Count);
                    foreach (int gi in w.GlyphIndices)
                    {
                        gl.Add(page.Glyphs[gi]);
                    }

                    foreach (PdfTextGlyph g in gl)
                    {
                        glyphs++;
                        if (!g.IsVisible)
                        {
                            invisible++;
                        }
                    }

                    string wordText = w.Text;
                    var classes = gl.Select(g => ClassOf(g.FillColor, style)).Distinct().ToList();
                    if (classes.Count <= 1)
                    {
                        Cls c = classes.Count == 1 ? classes[0] : Cls.Plain;
                        foreach (char ch in wordText)
                        {
                            pieces.Add(new Piece(ch, c));
                        }
                    }
                    else
                    {
                        // A word whose glyphs differ in colour (an edit inside a word): glyph by glyph, in logical order.
                        IEnumerable<PdfTextGlyph> ordered = line.Direction == PdfTextDirection.RightToLeft ? gl.AsEnumerable().Reverse() : gl;
                        foreach (PdfTextGlyph g in ordered)
                        {
                            Cls c = ClassOf(g.FillColor, style);
                            foreach (char ch in g.Text)
                            {
                                pieces.Add(new Piece(ch, c));
                            }
                        }
                    }

                    // For two-column pages the column is decided glyph by glyph: a word of unspaced script can run across the gutter.
                    foreach (PdfTextGlyph g in gl)
                    {
                        double gx = (g.Bounds.Left + g.Bounds.Right) / 2 / page.Width;
                        foreach (char ch in g.Text)
                        {
                            columns.Add((gx + page.PageIndex * 10, ch));
                        }
                    }

                    pieces.Add(new Piece(' ', Cls.Plain));
                    columns.Add(((w.Bounds.Left + w.Bounds.Right) / 2 / page.Width + page.PageIndex * 10, ' '));
                }

                pieces.Add(new Piece(' ', Cls.Plain));
            }
        }

        return new Extracted(pieces, columns, texts.Count, invisible, glyphs);
    }

    // The characters a style draws beside its marks (+, −, arrows). The page text and the expected text both lose them, so a
    // plus sign in the document is not told apart from a mark; the colour and column checks are what tell marks apart.
    // A carriage return is folded away in the compared text (line endings are normalized) but drawn, as its control picture, when the
    // original has one, so the control picture of a carriage return is left out of both sides. Joiners (U+200C, U+200D) are left out too: the extraction
    // loses the joiner of an emoji sequence and turns a Persian half-space into a space.
    [ThreadStatic]
    private static HashSet<char>? drop;

    /// <summary>Lines top to bottom; lines whose middles are within 4 pt of the first of their row form a row and are ordered left to right.</summary>
    private static List<PdfTextLine> InRows(List<PdfTextLine> lines)
    {
        var byTop = lines.OrderByDescending(l => (l.Bounds.Top + l.Bounds.Bottom) / 2).ToList();
        var result = new List<PdfTextLine>(lines.Count);
        int i = 0;
        while (i < byTop.Count)
        {
            double y = (byTop[i].Bounds.Top + byTop[i].Bounds.Bottom) / 2;
            int j = i;
            var row = new List<PdfTextLine>();
            while (j < byTop.Count && y - (byTop[j].Bounds.Top + byTop[j].Bounds.Bottom) / 2 <= 4)
            {
                row.Add(byTop[j++]);
            }

            result.AddRange(row.OrderBy(l => l.Bounds.Left));
            i = j;
        }

        return result;
    }

    private static string Strip(IEnumerable<char> chars)
    {
        var sb = new StringBuilder();
        foreach (char c in chars)
        {
            if (!char.IsWhiteSpace(c) && c != '\u200B' && c != '\u00AD' && c != '\u240D' && c != '\u200C' && c != '\u200D' && drop?.Contains(c) != true)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string Project(List<Piece> body, params Cls[] keep) =>
        Strip(body.Where(p => keep.Contains(p.Class)).Select(p => p.Ch));

    /// <summary>The checks; the errors found (empty when the page reads back as the comparison).</summary>
    public static List<string> Check(Comparison c, byte[] pdf, RedlineStyle style, bool sideBySide)
    {
        var errors = new List<string>();
        void Fail(string m)
        {
            if (errors.Count < 12)
            {
                errors.Add(m);
            }
        }

        drop = [.. new[] { style.Insertion, style.Deletion, style.MoveFrom, style.MoveTo, style.Formatting }.SelectMany(m => m.Glyph)];
        Extracted x = Extract(pdf, style);
        if (x.Pages == 0)
        {
            Fail("no pages");
        }

        if (x.InvisibleGlyphs > 0)
        {
            Fail($"{x.InvisibleGlyphs} of {x.Glyphs} glyphs are invisible text");
        }

        if (sideBySide)
        {
            CheckColumns(c, x, Fail);
            return errors;
        }

        // The body starts after the key.
        string all = Strip(x.Pieces.Select(p => p.Ch));
        const string Sentinel = "formattingchanged";
        int at = all.IndexOf(Sentinel, StringComparison.Ordinal);
        if (at < 0)
        {
            Fail("the key is missing");
            return errors;
        }

        int skip = 0, seen = 0;
        while (skip < x.Pieces.Count && seen < at + Sentinel.Length)
        {
            if (Strip([x.Pieces[skip].Ch]).Length > 0)
            {
                seen++;
            }

            skip++;
        }

        List<Piece> body = x.Pieces.GetRange(skip, x.Pieces.Count - skip);
        string newProj = Project(body, Cls.Plain, Cls.Ins, Cls.MoveTo, Cls.Fmt);
        string oldProj = Project(body, Cls.Plain, Cls.Del, Cls.MoveFrom, Cls.Fmt);
        InOrder(c.New, c.Settings, newProj, "new", Fail);
        InOrder(c.Old, c.Settings, oldProj, "old", Fail);

        string ins = Project(body, Cls.Ins), del = Project(body, Cls.Del);
        foreach (Change ch in c.Changes)
        {
            if (ch.Edits.IsDefaultOrEmpty)
            {
                switch (ch.Kind)
                {
                    case ChangeKind.Insert:
                        Contains(ins, ch.New.Text, $"change {ch.Id} inserted text", Fail);
                        break;
                    case ChangeKind.Delete:
                        Contains(del, ch.Old.Text, $"change {ch.Id} deleted text", Fail);
                        break;
                    case ChangeKind.Move:
                        // A block moved and edited carries its edits in the colours of insertions and deletions.
                        Contains(Project(body, Cls.MoveFrom, Cls.Del), ch.Old.Text, $"change {ch.Id} moved-from text", Fail);
                        Contains(Project(body, Cls.MoveTo, Cls.Ins), ch.New.Text, $"change {ch.Id} moved-to text", Fail);
                        break;
                }

                continue;
            }

            foreach (RegionEdit e in ch.Edits)
            {
                if (e.IsFormatOnly)
                {
                    continue;
                }

                Contains(del, e.OldText, $"change {ch.Id} deleted text", Fail);
                Contains(ins, e.NewText, $"change {ch.Id} inserted text", Fail);
            }
        }

        return errors;
    }

    private static void Contains(string stream, string text, string what, Action<string> fail)
    {
        string t = Strip(Display(text));
        if (t.Length > 0 && !(HasRtl(text) ? BagEnd(stream, 0, t) >= 0 : stream.Contains(t, StringComparison.Ordinal)))
        {
            fail($"{what} '{Clip(text)}' is not drawn in its mark's colour");
        }
    }

    /// <summary>
    /// The text as the page shows it: the page writer cannot carry control characters, so each is drawn as its control picture
    /// (U+2400 + code), DEL as U+2421, and the other characters it cannot carry (C1 controls, noncharacters, unpaired surrogates) as
    /// U+FFFD; a node boundary inside joined text is drawn as a pilcrow.
    /// </summary>
    internal static string Display(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[++i]);
            }
            else if (c == '\u001E')
            {
                sb.Append('\u00B6');
            }
            else if (c < 0x20 && c is not ('\t' or '\n'))
            {
                sb.Append((char)(0x2400 + c));
            }
            else if (c == '\u007F')
            {
                sb.Append('\u2421');
            }
            else if (c is (>= '\u0080' and <= '\u009F') or (>= '\uD800' and <= '\uDFFF') or (>= '\uFDD0' and <= '\uFDEF') or '\uFFFE' or '\uFFFF')
            {
                sb.Append('\uFFFD');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string Clip(string s) => s.Length <= 40 ? s : s[..40] + "...";

    private static bool HasRtl(string s) => s.Any(c => c is (>= '\u0590' and <= '\u08FF') or (>= '\uFB1D' and <= '\uFDFF') or (>= '\uFE70' and <= '\uFEFF'));

    /// <summary>How many nodes were matched by their characters alone because they hold right-to-left text (see <see cref="InOrder"/>).</summary>
    [ThreadStatic]
    internal static int RelaxedNodes;

    /// <summary>
    /// Each visible node's text (as the page shows it: the compared text with the folds of categories that are not hidden undone),
    /// in node order, must appear in the stream in that order; text between nodes is the page's own furniture. Two weaker checks
    /// stand in where order cannot be read back. A node with right-to-left text is not matched in sequence, because the extraction
    /// puts the words of a right-to-left run inside a left-to-right line in visual order; instead, over all nodes, the page must hold
    /// every character of every node (a bag of characters). The side-by-side columns are not read in sequence either, because a
    /// moved block sits in its new row on both sides.
    /// </summary>
    private static void InOrder(CompareDocument doc, CompareSettings settings, string stream, string side, Action<string> fail, bool ordered = true)
    {
        // A document with right-to-left text anywhere is checked as a bag of characters throughout: its neutral characters (braces,
        // angle brackets, hyphen runs) are placed by the bidirectional algorithm among the right-to-left runs, and the extraction does
        // not read them back in sequence.
        bool bagOnly = doc.Nodes.Any(n => HasRtl(n.Text));
        LedgerCategory?[] hidden = HiddenNodes.Compute(doc, settings);
        int pos = 0;
        var need = new Dictionary<char, int>();
        foreach (Node n in doc.Nodes)
        {
            if (hidden[n.Id] is not null)
            {
                continue;
            }

            string shown = ProjectedGroup.TextOf(doc, n, settings);
            string t = Strip(Display(shown));
            if (t.Length == 0)
            {
                continue;
            }

            foreach (char c in t)
            {
                need[c] = need.GetValueOrDefault(c) + 1;
            }

            if (bagOnly || HasRtl(shown))
            {
                RelaxedNodes++;
                continue;
            }

            int i = stream.IndexOf(t, ordered ? pos : 0, StringComparison.Ordinal);
            if (i < 0)
            {
                fail($"{side} node {n.Id}: '{Clip(shown)}' is not on the page after the previous block; {Divergence(stream, ordered ? pos : 0, t)}");
                return;
            }

            if (ordered)
            {
                pos = i + t.Length;
            }
        }

        var have = new Dictionary<char, int>();
        foreach (char c in stream)
        {
            have[c] = have.GetValueOrDefault(c) + 1;
        }

        var lost = need.Where(kv => have.GetValueOrDefault(kv.Key) < kv.Value).Take(8).Select(kv => ((int)kv.Key).ToString("X4", System.Globalization.CultureInfo.InvariantCulture) + "x" + (kv.Value - have.GetValueOrDefault(kv.Key))).ToList();
        if (lost.Count > 0)
        {
            fail($"{side}: the page lacks characters of the text: {string.Join(' ', lost)}");
        }
    }

    private static string Missing(string stream, int from, string t)
    {
        var have = new Dictionary<char, int>();
        foreach (char c in stream.Skip(from).Take(t.Length + 100))
        {
            have[c] = have.GetValueOrDefault(c) + 1;
        }

        var lost = new List<string>();
        foreach (char c in t)
        {
            if (have.TryGetValue(c, out int k) && k > 0)
            {
                have[c] = k - 1;
            }
            else if (lost.Count < 8)
            {
                lost.Add(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return lost.Count == 0 ? "nothing (order or noise)" : string.Join(' ', lost);
    }

    /// <summary>The position just after the earliest run of the stream, from <paramref name="from"/>, that holds every character of <paramref name="t"/> (as a bag), or -1.</summary>
    private static int BagEnd(string stream, int from, string t)
    {
        var need = new Dictionary<char, int>();
        foreach (char c in t)
        {
            need[c] = need.GetValueOrDefault(c) + 1;
        }

        int left = t.Length;
        for (int i = from; i < stream.Length && left > 0; i++)
        {
            if (need.TryGetValue(stream[i], out int k) && k > 0)
            {
                need[stream[i]] = k - 1;
                if (--left == 0)
                {
                    return i + 1;
                }
            }
        }

        return -1;
    }

    /// <summary>Where the page text first departs from the node's text, as code points: the longest agreement found from the previous block on.</summary>
    private static string Divergence(string stream, int from, string t)
    {
        int bestAt = -1, best = 0;
        string head = t[..Math.Min(4, t.Length)];
        for (int i = stream.IndexOf(head, from, StringComparison.Ordinal); i >= 0; i = stream.IndexOf(head, i + 1, StringComparison.Ordinal))
        {
            int k = 0;
            while (k < t.Length && i + k < stream.Length && stream[i + k] == t[k])
            {
                k++;
            }

            if (k > best)
            {
                best = k;
                bestAt = i;
            }
        }

        if (bestAt < 0)
        {
            return "the start of the text is not on the page";
        }

        static string Cp(string x, int a, int n) => string.Join(' ', x.Skip(Math.Max(a, 0)).Take(n).Select(ch => ((int)ch).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
        return $"agrees for {best} of {t.Length} characters; expected {Cp(t, best - 2, 8)} page has {Cp(stream, bestAt + best - 2, 8)}";
    }

    private static void CheckColumns(Comparison c, Extracted x, Action<string> fail)
    {
        string left = Strip(x.Columns.Where(t => t.X % 10 < 0.5).Select(t => t.Ch)), right = Strip(x.Columns.Where(t => t.X % 10 >= 0.5).Select(t => t.Ch));
        // The column headings repeat at the top of each page, which can fall inside a long block.
        left = left.Replace(Strip("Old: " + c.Provenance.OldName), "", StringComparison.Ordinal);
        right = right.Replace(Strip("New: " + c.Provenance.NewName), "", StringComparison.Ordinal);
        InOrder(c.Old, c.Settings, left, "old (left)", fail, ordered: false);
        InOrder(c.New, c.Settings, right, "new (right)", fail, ordered: false);
    }
}
