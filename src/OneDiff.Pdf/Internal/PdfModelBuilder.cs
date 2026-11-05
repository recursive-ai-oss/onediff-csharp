using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Tokenization;
using OnePdf;
using OnePdf.Text;

namespace OneDiff.Pdf.Internal;

/// <summary>A token of a paragraph's raw text (a word, or one punctuation mark) and the page quadrilateral it covers.</summary>
internal readonly record struct TokenRef(int RawStart, int RawEnd, Quad Quad);

/// <summary>The character formatting of a word, over its range of the paragraph's raw text.</summary>
internal readonly record struct WordFormat(int RawStart, int RawEnd, FormatSet Format);

/// <summary>
/// A paragraph being collected: raw text with its lines joined by LF, a token with a quadrilateral for each word and
/// punctuation mark, the formatting of each word, and one quadrilateral per line for the paragraph as a whole.
/// </summary>
internal sealed class Piece(NodeKind kind, string? label)
{
    public NodeKind Kind { get; } = kind;

    public string? Label { get; } = label;

    public StringBuilder Raw { get; } = new();

    public List<TokenRef> Tokens { get; } = [];

    public List<WordFormat> Formats { get; } = [];

    public List<Quad> LineQuads { get; } = [];

    public int LastPage { get; set; } = -1;

    public double LastBottom { get; set; }

    public string LastLineText { get; set; } = "";

    public bool IsEmpty => Raw.Length == 0;
}

/// <summary>
/// Turns OnePDF's text extraction into the flowing compare model. Blocks become paragraphs, list items and tables in reading order;
/// a paragraph that runs on at the end of a column or page is joined to its continuation; running headers and footers go into a
/// section of their own, one header and one footer node per page, so the body flows without them. Every word is anchored by
/// glyph quadrilaterals in the extraction's upright page space (corner order: lower left, lower right, upper right, upper left).
/// </summary>
internal sealed class PdfModelBuilder
{
    private readonly PdfSourceOptions options;
    private readonly CompareDocumentBuilder builder;
    private readonly CancellationToken cancellationToken;
    private Piece? open;
    private PdfTextLine? pendingMarker;
    private PdfPageText? pendingMarkerPage;
    private long characters;

    public PdfModelBuilder(PdfSourceOptions options, CompareDocumentBuilder builder, CancellationToken cancellationToken)
    {
        this.options = options;
        this.builder = builder;
        this.cancellationToken = cancellationToken;
    }

    private static readonly ITextNormalizer[] BodyNormalizers = [ControlCharacterNormalizer.Instance, LineBreakNormalizer.Instance, TypographicNormalizer.Instance];

    public void Build(IReadOnlyList<PdfPageText> pages, IReadOnlyList<string?> labels)
    {
        builder.Begin(NodeKind.Section);
        foreach (PdfPageText page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfTextBlock[] body = [.. page.Blocks.Where(static b => b.Role == PdfTextRole.Body)];
            for (int i = 0; i < body.Length; i++)
            {
                if (options.ReadTables && TryGrid(body, i, out List<List<List<PdfTextBlock>>>? rows))
                {
                    FlushMarker();
                    Flush();
                    AddGrid(page, rows);
                    i += rows.Sum(static r => r.Sum(static c => c.Count)) - 1;
                    continue;
                }

                AddBodyBlock(page, body[i]);
            }
        }

        FlushMarker();
        Flush();
        builder.End();

        // Running page furniture: after the body, so that a paragraph that crosses a page is never interrupted by it.
        bool any = pages.Any(static p => p.Blocks.Any(static b => b.Role != PdfTextRole.Body));
        if (!any)
        {
            return;
        }

        builder.Begin(NodeKind.Section);
        var lastFurniture = new Dictionary<PdfTextRole, string>();
        foreach (PdfPageText page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var numbers = new List<string> { (page.PageIndex + 1).ToString(CultureInfo.InvariantCulture), pages.Count.ToString(CultureInfo.InvariantCulture) };
            if (labels[page.PageIndex] is { Length: > 0 } label)
            {
                numbers.Add(label);
            }

            ITextNormalizer[] furniture = [ControlCharacterNormalizer.Instance, LineBreakNormalizer.Instance, new PageNumberNormalizer(numbers), TypographicNormalizer.Instance];
            foreach ((PdfTextRole role, NodeKind kind) in new[] { (PdfTextRole.Header, NodeKind.Header), (PdfTextRole.Footer, NodeKind.Footer) })
            {
                PdfTextBlock[] blocks = [.. page.Blocks.Where(b => b.Role == role)];
                if (blocks.Length == 0)
                {
                    continue;
                }

                var prepared = new List<(Piece Piece, NormalizedText Text)>();
                foreach (PdfTextBlock block in blocks)
                {
                    var piece = new Piece(NodeKind.Paragraph, null);
                    foreach (PdfTextLine line in block.Lines)
                    {
                        AddLine(piece, page, line, 0, line.Words.Count);
                    }

                    prepared.Add((piece, TextNormalization.Normalize(piece.Raw.ToString(), furniture)));
                }

                // A header or footer that reads the same as the one before it on the previous page is the same running text:
                // it is one node, so that a document with more pages does not gain a header per page.
                string key = string.Join('', prepared.Select(static p => p.Text.Text));
                if (lastFurniture.TryGetValue(role, out string? previous) && string.Equals(previous, key, StringComparison.Ordinal))
                {
                    continue;
                }

                lastFurniture[role] = key;
                builder.Begin(kind);
                foreach ((Piece piece, NormalizedText text) in prepared)
                {
                    EmitParagraph(piece, text);
                }

                builder.End();
            }
        }

        builder.End();
    }

    private void AddBodyBlock(PdfPageText page, PdfTextBlock block)
    {
        if (block.Lines.Count == 0 || block.Lines.All(static l => l.Words.Count == 0))
        {
            return;
        }

        // A list marker that the text analysis made a block of its own belongs to the block beside it on the same baseline.
        PdfTextLine? marker = pendingMarker;
        PdfPageText? markerPage = pendingMarkerPage;
        pendingMarker = null;
        pendingMarkerPage = null;
        PdfTextLine first = block.Lines[0];
        Piece? labelled = null;
        if (marker is not null)
        {
            if (markerPage!.PageIndex == page.PageIndex && !block.IsTable && SameBaseline(marker.Bounds, first.Bounds))
            {
                Flush();
                labelled = open = new Piece(NodeKind.ListItem, marker.Words[0].Text);
                open.LineQuads.Add(ToQuad(page.PageIndex, marker.Bounds));
            }
            else
            {
                Flush();
                var lone = new Piece(NodeKind.Paragraph, null);
                AddLine(lone, markerPage, marker, 0, 1);
                EmitParagraph(lone, TextNormalization.Normalize(lone.Raw.ToString(), BodyNormalizers));
            }
        }

        if (options.ReadListItems && labelled is null && block.Lines.Count == 1 && first.Words.Count == 1 && PdfText.IsListMarker(first.Words[0].Text))
        {
            pendingMarker = first;
            pendingMarkerPage = page;
            return;
        }

        if (block.IsTable && options.ReadTables)
        {
            Flush();
            AddTable(page, block);
            return;
        }

        bool firstIsMarker = options.ReadListItems && IsMarkerLine(first);
        if (labelled is null && !(open is not null && !firstIsMarker && options.JoinAcrossColumnsAndPages && first.Words.Count > 0 && CanJoin(page, first)))
        {
            Flush();
        }

        for (int i = 0; i < block.Lines.Count; i++)
        {
            PdfTextLine line = block.Lines[i];
            if (line.Words.Count == 0)
            {
                continue;
            }

            // A clause number such as 2.2 that opens a wrapped line is usually a reference that fell there ("see clause\n2.2."), so
            // within a block it starts an item only after a line that closed one.
            if (options.ReadListItems && IsMarkerLine(line) && (i == 0 || !PdfText.IsDottedDecimal(line.Words[0].Text) || EndsItem(block.Lines[i - 1])))
            {
                Flush();
                open = new Piece(NodeKind.ListItem, line.Words[0].Text);
                AddLine(open, page, line, 1, line.Words.Count);
                continue;
            }

            open ??= new Piece(NodeKind.Paragraph, null);
            AddLine(open, page, line, 0, line.Words.Count);
        }
    }

    private static bool SameBaseline(PdfRectangle a, PdfRectangle b)
    {
        double overlap = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        return overlap > 0.5 * Math.Min(a.Height, b.Height);
    }

    // A line closes an item when it ends a sentence or is a heading-sized fragment.
    private static bool EndsItem(PdfTextLine line) => line.Words.Count <= 3 || PdfText.EndsSentence(line.Text);

    private static bool IsMarkerLine(PdfTextLine line) => line.Words.Count > 1 && PdfText.IsListMarker(line.Words[0].Text);

    private bool CanJoin(PdfPageText page, PdfTextLine first)
    {
        Piece p = open!;
        if (p.LastPage < 0 || PdfText.EndsSentence(p.LastLineText) || !PdfText.StartsLikeContinuation(first.Text))
        {
            return false;
        }

        if (page.PageIndex == p.LastPage + 1)
        {
            return true;
        }

        if (page.PageIndex != p.LastPage)
        {
            return false;
        }

        // Same page: a jump up the page (the next column) continues a paragraph. So does a block that starts right under the
        // previous line, closer than any paragraph gap: the text analysis split a line off (a justified first line, say).
        return first.Bounds.Top > p.LastBottom + 2 || p.LastBottom - first.Bounds.Top < 0.4 * first.Bounds.Height;
    }

    // A ruled or aligned grid whose cells the text analysis returned as one block each, row by row ("Item", "Period", "Fee", then
    // "Support", "24 months", "USD 25,000"): consecutive short blocks that fall into rows of the same number of cells (two or more),
    // each cell sitting under the one above it, for at least two rows. A block that sits right under a cell of the row and
    // overlaps it is that cell's next line (a narrow cell wraps "1 200 EUR" into two blocks). A Word source has the table, so
    // reading the grid back as a table keeps the two models alike; a block that is longer than a cell or a row that breaks the
    // pattern ends the grid. A cell is its blocks, a row its cells.
    private static bool TryGrid(PdfTextBlock[] body, int start, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out List<List<List<PdfTextBlock>>>? rows)
    {
        rows = null;
        var done = new List<List<List<PdfTextBlock>>>();
        var row = new List<List<PdfTextBlock>>();
        int columns = -1;
        for (int j = start; j < body.Length; j++)
        {
            PdfTextBlock b = body[j];
            if (!IsCell(b))
            {
                break;
            }

            if (row.Count > 0 && !SameBaseline(row[0][0].Lines[0].Bounds, b.Lines[0].Bounds))
            {
                if (row.Count >= 2 && ContinuesCell(row, b))
                {
                    continue;
                }

                // The row is complete: it must have as many cells as the rows before it, each under the one above.
                if (row.Count < 2 || (columns >= 0 && row.Count != columns) || (done.Count > 0 && !Stacks(done[^1], row)))
                {
                    row = [];
                    break;
                }

                columns = row.Count;
                done.Add(row);
                row = [];
            }

            row.Add([b]);
        }

        if (row.Count >= 2 && (columns < 0 || row.Count == columns) && (done.Count == 0 || Stacks(done[^1], row)))
        {
            done.Add(row);
        }

        if (done.Count < 2)
        {
            return false;
        }

        rows = done;
        return true;
    }

    private static bool ContinuesCell(List<List<PdfTextBlock>> row, PdfTextBlock b)
    {
        PdfRectangle next = b.Lines[0].Bounds;
        foreach (List<PdfTextBlock> cell in row)
        {
            PdfRectangle last = cell[^1].Lines[^1].Bounds;
            bool overlaps = Math.Min(last.Right, next.Right) > Math.Max(last.Left, next.Left);
            if (overlaps && last.Bottom - next.Top < 0.5 * next.Height && next.Top <= last.Bottom + 0.5 * next.Height)
            {
                cell.Add(b);
                return true;
            }
        }

        return false;
    }

    private static bool IsCell(PdfTextBlock b)
    {
        if (b.IsTable || b.Lines.Count is 0 or > 3 || b.Lines.Any(static l => l.Words.Count == 0))
        {
            return false;
        }

        return b.Lines.Sum(static l => l.Words.Count) <= 8 && !PdfText.IsListMarker(b.Lines[0].Words[0].Text);
    }

    private static bool Stacks(List<List<PdfTextBlock>> above, List<List<PdfTextBlock>> below)
    {
        for (int c = 0; c < above.Count; c++)
        {
            PdfRectangle a = above[c][0].Lines[0].Bounds, b = below[c][0].Lines[0].Bounds;
            if (Math.Min(a.Right, b.Right) <= Math.Max(a.Left, b.Left))
            {
                return false;
            }
        }

        return true;
    }

    private void AddGrid(PdfPageText page, List<List<List<PdfTextBlock>>> rows)
    {
        builder.Begin(NodeKind.Table);
        foreach (List<List<PdfTextBlock>> row in rows)
        {
            builder.Begin(NodeKind.TableRow);
            foreach (List<PdfTextBlock> cell in row)
            {
                builder.Begin(NodeKind.TableCell);
                var piece = new Piece(NodeKind.Paragraph, null);
                foreach (PdfTextLine line in cell.SelectMany(static b => b.Lines))
                {
                    AddLine(piece, page, line, 0, line.Words.Count);
                }

                EmitParagraph(piece, TextNormalization.Normalize(piece.Raw.ToString(), BodyNormalizers));
                builder.End();
            }

            builder.End();
        }

        builder.End();
    }

    private void AddTable(PdfPageText page, PdfTextBlock block)
    {
        builder.Begin(NodeKind.Table);
        foreach (PdfTextLine line in block.Lines)
        {
            if (line.Words.Count == 0)
            {
                continue;
            }

            builder.Begin(NodeKind.TableRow);
            IReadOnlyList<int> starts = line.CellStarts.Count == 0 ? [0] : line.CellStarts;
            for (int c = 0; c < starts.Count; c++)
            {
                int from = Math.Clamp(starts[c], 0, line.Words.Count);
                int to = c + 1 < starts.Count ? Math.Clamp(starts[c + 1], from, line.Words.Count) : line.Words.Count;
                if (to <= from)
                {
                    continue;
                }

                builder.Begin(NodeKind.TableCell);
                var piece = new Piece(NodeKind.Paragraph, null);
                AddLine(piece, page, line, from, to);
                EmitParagraph(piece, TextNormalization.Normalize(piece.Raw.ToString(), BodyNormalizers));
                builder.End();
            }

            builder.End();
        }

        builder.End();
    }

    private void FlushMarker()
    {
        if (pendingMarker is null)
        {
            return;
        }

        Flush();
        var lone = new Piece(NodeKind.Paragraph, null);
        AddLine(lone, pendingMarkerPage!, pendingMarker, 0, 1);
        pendingMarker = null;
        pendingMarkerPage = null;
        EmitParagraph(lone, TextNormalization.Normalize(lone.Raw.ToString(), BodyNormalizers));
    }

    private void Flush()
    {
        if (open is null)
        {
            return;
        }

        Piece p = open;
        open = null;
        if (!p.IsEmpty)
        {
            EmitParagraph(p, TextNormalization.Normalize(p.Raw.ToString(), BodyNormalizers));
        }
    }

    private void AddLine(Piece p, PdfPageText page, PdfTextLine line, int fromWord, int toWord)
    {
        if (toWord <= fromWord)
        {
            return;
        }

        if (!p.IsEmpty)
        {
            p.Raw.Append('\n');
        }

        for (int w = fromWord; w < toWord; w++)
        {
            if (w > fromWord)
            {
                p.Raw.Append(' ');
            }

            AddWord(p, page, line, line.Words[w]);
        }

        characters += line.Text.Length + 1;
        if (characters > options.MaxCharacters)
        {
            throw PdfSourceErrors.Make(PdfSourceFailure.TooLarge, $"the document holds more than {options.MaxCharacters} characters of text");
        }

        p.LineQuads.Add(ToQuad(page.PageIndex, line.Bounds));
        p.LastPage = page.PageIndex;
        p.LastBottom = line.Bounds.Bottom;
        p.LastLineText = line.Text;
    }

    private void AddWord(Piece p, PdfPageText page, PdfTextLine line, PdfTextWord word)
    {
        string text = word.Text.Replace('\n', ' ').Replace('\r', ' ');
        int rawStart = p.Raw.Length;
        IReadOnlyList<int>? glyphForTokens = null;
        p.Raw.Append(text);
        if (text.Length == 0)
        {
            return;
        }

        IReadOnlyList<int> glyphIndices = word.GlyphIndices;
        int[]? prefix = null;
        bool rtl = line.Direction == PdfTextDirection.RightToLeft;
        if (glyphIndices.Count > 0 && !line.IsVertical && line.Direction is PdfTextDirection.LeftToRight or PdfTextDirection.RightToLeft)
        {
            // In a right-to-left line the glyph order is not stated precisely enough to rely on, so the order whose glyph texts
            // spell the word's text is found by trying the plausible ones; when none spells it, the word's box is shared out.
            IReadOnlyList<int> logical = rtl ? SpellingOrder(page, glyphIndices, word.Text) ?? glyphIndices : glyphIndices;
            // Glyph boxes pin a token exactly when the glyphs spell the word.
            int total = 0;
            var sums = new int[logical.Count + 1];
            for (int g = 0; g < logical.Count; g++)
            {
                sums[g] = total;
                int gi = logical[g];
                total += (uint)gi < (uint)page.Glyphs.Count ? page.Glyphs[gi].Text.Length : -1_000_000;
            }

            sums[logical.Count] = total;
            if (total == word.Text.Length && (!rtl || Spells(page, logical, word.Text)))
            {
                prefix = sums;
                glyphForTokens = logical;
            }
        }

        foreach ((int a, int b) in TokenSpans(text))
        {
            Quad q = prefix is not null && glyphForTokens is not null
                ? (rtl ? GlyphBox(page, glyphForTokens, prefix, a, b) : GlyphQuad(page, glyphForTokens, prefix, a, b))
                : Proportional(page.PageIndex, word.Bounds, line, a, b, text.Length);
            IReadOnlyList<int> ownGlyphs = glyphIndices;
            if (prefix is not null && glyphForTokens is not null)
            {
                // Only the glyphs of the token itself are its own; the rest of the word may overlap it.
                int first = 0;
                while (first + 1 < glyphForTokens.Count && prefix[first + 1] <= a)
                {
                    first++;
                }

                int last = first;
                while (last + 1 < glyphForTokens.Count && prefix[last + 1] < b)
                {
                    last++;
                }

                ownGlyphs = [.. glyphForTokens.Skip(first).Take(last - first + 1)];
            }

            p.Tokens.Add(new TokenRef(rawStart + a, rawStart + b, ClipForeignGlyphs(page, ownGlyphs, q)));
        }

        if (options.ReadFormatting && glyphIndices.Count > 0)
        {
            FormatSet? f = FormatOf(page, glyphIndices);
            if (f is not null)
            {
                p.Formats.Add(new WordFormat(rawStart, rawStart + text.Length, f));
            }
        }
    }

    private PdfPageText? centresPage;
    private double[] centreX = [];
    private double[] centreY = [];
    private int[] centreGlyph = [];

    /// <summary>
    /// Glyph boxes of neighbouring words can overlap (a line of underscores running into the next word), so a highlight over a
    /// token's box would also take the neighbour's glyphs. A box is cut back, midway between the token's own outermost glyph centre
    /// and the foreign glyph's centre, until no glyph of another word has its centre inside it. Vertical lines are left alone.
    /// </summary>
    private Quad ClipForeignGlyphs(PdfPageText page, IReadOnlyList<int> own, Quad q)
    {
        if (own.Count == 0 || page.Glyphs.Count == 0 || Math.Abs(q.X1 - q.X4) > 0.01 || Math.Abs(q.Y1 - q.Y2) > 0.01)
        {
            return q;
        }

        if (!ReferenceEquals(centresPage, page))
        {
            int n = page.Glyphs.Count;
            var order = new int[n];
            var keys = new double[n];
            for (int i = 0; i < n; i++)
            {
                PdfQuad g = page.Glyphs[i].Quad;
                order[i] = i;
                keys[i] = (g.X0 + g.X2) / 2;
            }

            Array.Sort(keys, order);
            centreX = keys;
            centreGlyph = order;
            centreY = new double[n];
            for (int i = 0; i < n; i++)
            {
                PdfQuad g = page.Glyphs[order[i]].Quad;
                centreY[i] = (g.Y0 + g.Y2) / 2;
            }

            centresPage = page;
        }

        double x0 = Math.Min(Math.Min(q.X1, q.X2), Math.Min(q.X3, q.X4)), x1 = Math.Max(Math.Max(q.X1, q.X2), Math.Max(q.X3, q.X4));
        double y0 = Math.Min(Math.Min(q.Y1, q.Y2), Math.Min(q.Y3, q.Y4)), y1 = Math.Max(Math.Max(q.Y1, q.Y2), Math.Max(q.Y3, q.Y4));
        double lo = double.MaxValue, hi = double.MinValue, ownY = 0;
        int ownCount = 0;
        foreach (int gi in own)
        {
            if ((uint)gi >= (uint)page.Glyphs.Count)
            {
                continue;
            }

            PdfQuad g = page.Glyphs[gi].Quad;
            double c = (g.X0 + g.X2) / 2, cy = (g.Y0 + g.Y2) / 2;
            if (c >= x0 && c <= x1 && cy >= y0 && cy <= y1)
            {
                lo = Math.Min(lo, c);
                hi = Math.Max(hi, c);
                ownY += cy;
                ownCount++;
            }
        }

        if (lo > hi)
        {
            return q;
        }

        bool changed = false;
        int at = Array.BinarySearch(centreX, x0);
        for (int i = at < 0 ? ~at : at; i < centreX.Length && centreX[i] <= x1; i++)
        {
            double cx = centreX[i], cy = centreY[i];
            if (cx < x0 || cy < y0 || cy > y1 || own.Contains(centreGlyph[i]))
            {
                continue;
            }

            // A glyph from the line above or below (the boxes of tall lines touch) is cut off vertically; one on the same line,
            // horizontally.
            double h = y1 - y0, mid = ownY / ownCount;
            if (cy > mid + 0.25 * h)
            {
                y1 = Math.Min(y1, (mid + cy) / 2);
                changed = true;
            }
            else if (cy < mid - 0.25 * h)
            {
                y0 = Math.Max(y0, (cy + mid) / 2);
                changed = true;
            }
            else if (cx > hi)
            {
                x1 = Math.Min(x1, (hi + cx) / 2);
                changed = true;
            }
            else if (cx < lo)
            {
                x0 = Math.Max(x0, (cx + lo) / 2);
                changed = true;
            }
        }

        return changed ? new Quad(q.Page, x0, y0, x1, y0, x1, y1, x0, y1) : q;
    }

    private static FormatSet? FormatOf(PdfPageText page, IReadOnlyList<int> glyphIndices)
    {
        foreach (int gi in glyphIndices)
        {
            if ((uint)gi >= (uint)page.Glyphs.Count)
            {
                continue;
            }

            PdfTextGlyph g = page.Glyphs[gi];
            if (g.Text.Length == 0)
            {
                continue;
            }

            string name = g.FontName ?? "";
            var kv = new List<KeyValuePair<string, string>>();
            if (Has(name, "bold") || Has(name, "black") || Has(name, "heavy") || Has(name, "semibold") || Has(name, "demi"))
            {
                kv.Add(new(FormatKeys.Bold, "1"));
            }

            if (Has(name, "italic") || Has(name, "oblique"))
            {
                kv.Add(new(FormatKeys.Italic, "1"));
            }

            if (name.Length > 0)
            {
                int cut = name.AsSpan().IndexOfAny('-', ',');
                kv.Add(new(FormatKeys.Font, cut > 0 ? name[..cut] : name));
            }

            double size = Math.Round(g.Size * 2, MidpointRounding.AwayFromZero) / 2;
            if (size > 0 && double.IsFinite(size))
            {
                kv.Add(new(FormatKeys.Size, PdfText.Number(size)));
            }

            if (g.FillColor >= 0 && (g.FillColor & 0xFFFFFF) != 0)
            {
                kv.Add(new(FormatKeys.Color, PdfText.Hex(g.FillColor)));
            }

            return kv.Count == 0 ? null : FormatSet.Create(kv);
        }

        return null;
    }

    private static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static bool Spells(PdfPageText page, IReadOnlyList<int> order, string text)
    {
        int at = 0;
        foreach (int gi in order)
        {
            if ((uint)gi >= (uint)page.Glyphs.Count)
            {
                return false;
            }

            string t = page.Glyphs[gi].Text;
            if (at + t.Length > text.Length || string.CompareOrdinal(text, at, t, 0, t.Length) != 0)
            {
                return false;
            }

            at += t.Length;
        }

        return at == text.Length;
    }

    /// <summary>The order of a right-to-left word's glyphs in which their texts spell the word: as given, reversed, or with numbers and Latin runs kept left to right; null when none does.</summary>
    private static IReadOnlyList<int>? SpellingOrder(PdfPageText page, IReadOnlyList<int> given, string text)
    {
        if (Spells(page, given, text))
        {
            return given;
        }

        var reversed = new List<int>(given);
        reversed.Reverse();
        if (Spells(page, reversed, text))
        {
            return reversed;
        }

        List<int> a = LogicalGlyphOrder(page, given);
        if (Spells(page, a, text))
        {
            return a;
        }

        List<int> b = LogicalGlyphOrder(page, reversed);
        return Spells(page, b, text) ? b : null;
    }

    /// <summary>
    /// The glyphs of a right-to-left word in logical order. They arrive in visual order, so the order reverses, except that a run
    /// of digits and Latin letters (with the signs inside and beside it) reads left to right in both orders.
    /// </summary>
    private static List<int> LogicalGlyphOrder(PdfPageText page, IReadOnlyList<int> visual)
    {
        static bool Strong(string t) => t.Length > 0 && char.IsAsciiLetterOrDigit(t[0]);
        static bool Sign(string t) => t.Length > 0 && t[0] is '.' or ',' or ':' or '/' or '%' or '-' or '+';
        int n = visual.Count;
        var ltr = new bool[n];
        for (int i = 0; i < n; i++)
        {
            string t = (uint)visual[i] < (uint)page.Glyphs.Count ? page.Glyphs[visual[i]].Text : "";
            ltr[i] = Strong(t);
        }

        for (int i = 0; i < n; i++)
        {
            string t = (uint)visual[i] < (uint)page.Glyphs.Count ? page.Glyphs[visual[i]].Text : "";
            if (Sign(t) && ((i > 0 && ltr[i - 1]) || (i + 1 < n && ltr[i + 1])))
            {
                ltr[i] = true;
            }
        }

        var logical = new List<int>(n);
        int k = n - 1;
        while (k >= 0)
        {
            if (!ltr[k])
            {
                logical.Add(visual[k--]);
                continue;
            }

            int end = k;
            while (k >= 0 && ltr[k])
            {
                k--;
            }

            for (int j = k + 1; j <= end; j++)
            {
                logical.Add(visual[j]);
            }
        }

        return logical;
    }

    /// <summary>The box around the glyphs that spell characters [a, b) of a word whose glyphs are in logical order (right-to-left text).</summary>
    private static Quad GlyphBox(PdfPageText page, IReadOnlyList<int> logical, int[] prefix, int a, int b)
    {
        int first = 0;
        while (first + 1 < logical.Count && prefix[first + 1] <= a)
        {
            first++;
        }

        int last = first;
        while (last + 1 < logical.Count && prefix[last + 1] < b)
        {
            last++;
        }

        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        for (int g = first; g <= last; g++)
        {
            PdfQuad q = page.Glyphs[logical[g]].Quad;
            x0 = Math.Min(x0, Math.Min(Math.Min(q.X0, q.X1), Math.Min(q.X2, q.X3)));
            x1 = Math.Max(x1, Math.Max(Math.Max(q.X0, q.X1), Math.Max(q.X2, q.X3)));
            y0 = Math.Min(y0, Math.Min(Math.Min(q.Y0, q.Y1), Math.Min(q.Y2, q.Y3)));
            y1 = Math.Max(y1, Math.Max(Math.Max(q.Y0, q.Y1), Math.Max(q.Y2, q.Y3)));
        }

        return new Quad(page.PageIndex, x0, y0, x1, y0, x1, y1, x0, y1);
    }

    private static Quad GlyphQuad(PdfPageText page, IReadOnlyList<int> glyphIndices, int[] prefix, int a, int b)
    {
        int first = 0;
        while (first + 1 < glyphIndices.Count && prefix[first + 1] <= a)
        {
            first++;
        }

        int last = first;
        while (last + 1 < glyphIndices.Count && prefix[last + 1] < b)
        {
            last++;
        }

        PdfQuad g0 = page.Glyphs[glyphIndices[first]].Quad, g1 = page.Glyphs[glyphIndices[last]].Quad;
        // Lower left and upper left of the first glyph, lower right and upper right of the last.
        return new Quad(page.PageIndex, g0.X0, g0.Y0, g1.X1, g1.Y1, g1.X2, g1.Y2, g0.X3, g0.Y3);
    }

    /// <summary>The box of characters [a, b) of an n-character word, by sharing the word's box out evenly along the writing direction.</summary>
    private static Quad Proportional(int pageIndex, PdfRectangle r, PdfTextLine line, int a, int b, int n)
    {
        double t0 = (double)a / n, t1 = (double)b / n;
        if (line.IsVertical)
        {
            double y0 = r.Top - t0 * r.Height, y1 = r.Top - t1 * r.Height;
            return new Quad(pageIndex, r.Left, y1, r.Right, y1, r.Right, y0, r.Left, y0);
        }

        double x0, x1;
        if (line.Direction == PdfTextDirection.RightToLeft)
        {
            x0 = r.Right - t1 * r.Width;
            x1 = r.Right - t0 * r.Width;
        }
        else
        {
            x0 = r.Left + t0 * r.Width;
            x1 = r.Left + t1 * r.Width;
        }

        return new Quad(pageIndex, x0, r.Bottom, x1, r.Bottom, x1, r.Top, x0, r.Top);
    }

    private static Quad ToQuad(int pageIndex, PdfRectangle r) => new(pageIndex, r.Left, r.Bottom, r.Right, r.Bottom, r.Right, r.Top, r.Left, r.Top);

    private readonly List<TokenRange> tokenScratch = [];

    /// <summary>
    /// The tokens of a word, cut by the text core's own word tokenizer (not a copy of it), so a change found by a word diff
    /// always starts and ends on a token boundary and its anchor covers exactly the changed text.
    /// </summary>
    internal IEnumerable<(int Start, int End)> TokenSpans(string text)
    {
        tokenScratch.Clear();
        Tokenizer.Tokenize(text, TokenKind.Word, tokenScratch);
        foreach (TokenRange t in tokenScratch)
        {
            yield return (t.Start, t.End);
        }
    }

    private static bool IsBlank(string text, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The box between two words of one horizontal line (either direction), or null when they are not on one line or touch.</summary>
    private static Quad? GapBetween(Quad left, Quad right)
    {
        if (left.Page != right.Page)
        {
            return null;
        }

        double lMin = Math.Min(Math.Min(left.X1, left.X2), Math.Min(left.X3, left.X4)), lMax = Math.Max(Math.Max(left.X1, left.X2), Math.Max(left.X3, left.X4));
        double rMin = Math.Min(Math.Min(right.X1, right.X2), Math.Min(right.X3, right.X4)), rMax = Math.Max(Math.Max(right.X1, right.X2), Math.Max(right.X3, right.X4));
        double bottom = Math.Min(left.Y1, right.Y1), top = Math.Max(left.Y4, right.Y4);
        double height = Math.Max(left.Y4 - left.Y1, right.Y4 - right.Y1);
        if (!(height > 0) || Math.Abs(left.Y1 - right.Y1) > height * 0.5 || Math.Abs(left.Y4 - right.Y4) > height * 0.5)
        {
            return null;
        }

        double x0, x1;
        if (lMax < rMin)
        {
            x0 = lMax;
            x1 = rMin;
        }
        else if (rMax < lMin)
        {
            x0 = rMax;
            x1 = lMin;
        }
        else
        {
            return null;
        }

        // A wider gap is not a space: something else (another cell or column) lies between the two words.
        if (x1 - x0 > 2 * height)
        {
            return null;
        }

        return new Quad(left.Page, x0, bottom, x1, bottom, x1, top, x0, top);
    }

    /// <summary>
    /// The anchor of a block: its line boxes (a list item's include the marker). The extraction's line box can reach across columns
    /// or table cells that are other blocks, so for a block without a marker the boxes are the hulls of its own words, line by line.
    /// </summary>
    private static Anchor? NodeAnchor(Piece p)
    {
        if (p.Label is not null || p.Tokens.Count == 0)
        {
            return p.LineQuads.Count > 0 ? Anchor.ForPdfQuads(p.LineQuads) : null;
        }

        // One box per run of neighbouring tokens: the run ends where the line changes, the reading direction reverses or a gap wider
        // than two line heights opens, because whatever lies in such a gap (another table cell, another column) is not this block's.
        var lines = new List<Quad>();
        int page = -1;
        double x0 = 0, x1 = 0, y0 = 0, y1 = 0, prevLeft = 0;
        int dir = 0;
        foreach (TokenRef t in p.Tokens)
        {
            Quad q = t.Quad;
            double qx0 = Math.Min(Math.Min(q.X1, q.X2), Math.Min(q.X3, q.X4)), qx1 = Math.Max(Math.Max(q.X1, q.X2), Math.Max(q.X3, q.X4));
            double qy0 = Math.Min(Math.Min(q.Y1, q.Y2), Math.Min(q.Y3, q.Y4)), qy1 = Math.Max(Math.Max(q.Y1, q.Y2), Math.Max(q.Y3, q.Y4));
            double centre = (qy0 + qy1) / 2, height = Math.Max(1, y1 - y0);
            if (page == q.Page && Math.Abs(centre - (y0 + y1) / 2) <= 0.25 * height)
            {
                int step = Math.Sign(qx0 - prevLeft);
                double gap = step >= 0 ? qx0 - x1 : x0 - qx1;
                if ((dir == 0 || step == 0 || step == dir) && gap <= 2 * height)
                {
                    if (step != 0)
                    {
                        dir = step;
                    }

                    prevLeft = qx0;
                    x0 = Math.Min(x0, qx0);
                    x1 = Math.Max(x1, qx1);
                    y0 = Math.Min(y0, qy0);
                    y1 = Math.Max(y1, qy1);
                    continue;
                }
            }

            if (page >= 0)
            {
                lines.Add(new Quad(page, x0, y0, x1, y0, x1, y1, x0, y1));
            }

            page = q.Page;
            x0 = qx0;
            x1 = qx1;
            y0 = qy0;
            y1 = qy1;
            prevLeft = qx0;
            dir = 0;
        }

        lines.Add(new Quad(page, x0, y0, x1, y0, x1, y1, x0, y1));
        return Anchor.ForPdfQuads(lines);
    }

    private void EmitParagraph(Piece p, NormalizedText norm)
    {
        var segments = ImmutableArray.CreateBuilder<AnchoredSpan>(p.Tokens.Count * 2);
        int previousEnd = -1;
        Quad previousQuad = default;
        foreach (TokenRef t in p.Tokens)
        {
            int a = norm.MapRawIndex(t.RawStart), b = norm.MapRawIndex(t.RawEnd);
            if (b <= a || a < previousEnd)
            {
                continue;
            }

            // A space between two words on one line gets its own segment, so a change that is only a space is anchored on that
            // space and not on the whole paragraph. A space at a line or page break has no box and no segment.
            if (previousEnd >= 0 && a > previousEnd && IsBlank(norm.Text, previousEnd, a) && GapBetween(previousQuad, t.Quad) is Quad gap)
            {
                segments.Add(new AnchoredSpan(previousEnd, a - previousEnd, Anchor.ForPdfQuads([gap])));
            }

            segments.Add(new AnchoredSpan(a, b - a, Anchor.ForPdfQuads([t.Quad])));
            previousEnd = b;
            previousQuad = t.Quad;
        }

        var runs = new List<FormatRun>();
        for (int i = 0; i < p.Formats.Count; i++)
        {
            WordFormat f = p.Formats[i];
            int a = norm.MapRawIndex(f.RawStart), b = norm.MapRawIndex(f.RawEnd);
            // A run goes on over the space to the next word when that word is formatted alike.
            if (i + 1 < p.Formats.Count && FormatSet.ContentEquals(f.Format, p.Formats[i + 1].Format))
            {
                b = norm.MapRawIndex(p.Formats[i + 1].RawStart);
            }

            if (b > a)
            {
                runs.Add(new FormatRun(a, b - a, f.Format));
            }
        }

        FormatSet? attributes = p.Label is null ? null : FormatSet.Of(FormatKeys.ListLabel, p.Label);
        builder.Add(p.Kind, new NodeContent
        {
            Text = norm.Text,
            Folds = norm.Folds,
            Runs = [.. runs],
            Attributes = attributes,
            Anchor = NodeAnchor(p),
            Segments = segments.ToImmutable(),
        });
    }
}
