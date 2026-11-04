using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
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
            foreach (PdfTextBlock block in page.Blocks)
            {
                if (block.Role == PdfTextRole.Body)
                {
                    AddBodyBlock(page, block);
                }
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

            if (options.ReadListItems && IsMarkerLine(line))
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
        p.Raw.Append(text);
        if (text.Length == 0)
        {
            return;
        }

        IReadOnlyList<int> glyphIndices = word.GlyphIndices;
        int[]? prefix = null;
        if (glyphIndices.Count > 0 && !line.IsVertical && line.Direction == PdfTextDirection.LeftToRight)
        {
            // Glyph boxes pin a token exactly when the glyphs spell the word.
            int total = 0;
            var sums = new int[glyphIndices.Count + 1];
            for (int g = 0; g < glyphIndices.Count; g++)
            {
                sums[g] = total;
                int gi = glyphIndices[g];
                total += (uint)gi < (uint)page.Glyphs.Count ? page.Glyphs[gi].Text.Length : -1_000_000;
            }

            sums[glyphIndices.Count] = total;
            if (total == word.Text.Length)
            {
                prefix = sums;
            }
        }

        foreach ((int a, int b) in TokenSpans(text))
        {
            Quad q = prefix is not null ? GlyphQuad(page, glyphIndices, prefix, a, b) : Proportional(page.PageIndex, word.Bounds, line, a, b, text.Length);
            p.Tokens.Add(new TokenRef(rawStart + a, rawStart + b, q));
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

    /// <summary>
    /// The tokens of a word, cut as the text core's word tokenizer cuts: a run of letters, digits, underscores and combining
    /// marks is one token and every other character is its own. A change found by a word diff therefore always starts and ends
    /// on a token boundary, which is what lets its anchor cover exactly the changed text.
    /// </summary>
    internal static IEnumerable<(int Start, int End)> TokenSpans(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out Rune r, out int w);
            if (!IsWordRune(r))
            {
                yield return (i, i + w);
                i += w;
                continue;
            }

            int end = i + w;
            while (end < text.Length)
            {
                Rune.DecodeFromUtf16(text.AsSpan(end), out Rune next, out int nw);
                if (!IsWordRune(next))
                {
                    break;
                }

                end += nw;
            }

            yield return (i, end);
            i = end;
        }
    }

    private static bool IsWordRune(Rune r)
    {
        if (Rune.IsLetterOrDigit(r) || r.Value == '_')
        {
            return true;
        }

        UnicodeCategory c = Rune.GetUnicodeCategory(r);
        return c is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    private void EmitParagraph(Piece p, NormalizedText norm)
    {
        var segments = ImmutableArray.CreateBuilder<AnchoredSpan>(p.Tokens.Count);
        foreach (TokenRef t in p.Tokens)
        {
            int a = norm.MapRawIndex(t.RawStart), b = norm.MapRawIndex(t.RawEnd);
            if (b > a)
            {
                segments.Add(new AnchoredSpan(a, b - a, Anchor.ForPdfQuads([t.Quad])));
            }
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
            Anchor = p.LineQuads.Count > 0 ? Anchor.ForPdfQuads(p.LineQuads) : null,
            Segments = segments.ToImmutable(),
        });
    }
}
