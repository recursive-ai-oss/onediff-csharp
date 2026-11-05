using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Pdf;
using OneDiff.Pdf.Internal;
using OneDiff.Tokenization;
using OneOcr;

namespace OneDiff.Ocr.Internal;

/// <summary>A token (a word, or one punctuation mark) of a paragraph's raw text with its page quadrilateral and recognition confidence.</summary>
internal readonly record struct OcrTokenRef(int RawStart, int RawEnd, Quad Quad, double Confidence);

/// <summary>The paragraph being collected: raw text, tokens, and what is needed to decide whether the next block continues it.</summary>
internal sealed class OcrPiece(NodeKind kind, string? label)
{
    public NodeKind Kind { get; } = kind;

    public string? Label { get; } = label;

    public StringBuilder Raw { get; } = new();

    public List<OcrTokenRef> Tokens { get; } = [];

    /// <summary>One box per line: the hull of the tokens added from that line.</summary>
    public List<(Quad Quad, double Confidence)> LineBoxes { get; } = [];

    public int LastPage { get; set; } = -1;

    public double LastBottom { get; set; }

    public string LastLineText { get; set; } = "";

    public bool IsEmpty => Raw.Length == 0;
}

/// <summary>
/// Turns OCR pages into the flowing compare model, the way the PDF source does for a text layer: blocks become paragraphs, list
/// items and tables in reading order, a paragraph that runs on at the end of a column or page is joined, and running headers and
/// footers go to a section of their own. Every token is anchored by its word's quadrilateral and the word's recognition confidence
/// (<see cref="AnchorKind.OcrWords"/>), in upright page space.
/// </summary>
internal sealed class OcrModelBuilder(OcrSourceOptions options, CompareDocumentBuilder builder, CancellationToken cancellationToken)
{
    private static readonly ITextNormalizer[] BodyNormalizers = [ControlCharacterNormalizer.Instance, LineBreakNormalizer.Instance, TypographicNormalizer.Instance];

    /// <summary>The gap between two lines of one block, in line heights, above which the second starts a new paragraph.</summary>
    private const double ParagraphGap = 0.9;

    /// <summary>A line narrower than this share of the block's widest line is short: it ended early, as a heading or the last line of a paragraph does.</summary>
    private const double ShortLine = 0.6;

    private readonly List<TokenRange> tokenScratch = [];
    private OcrPiece? open;
    private long characters;

    public void Build(IReadOnlyList<PageData> pages)
    {
        builder.Begin(NodeKind.Section);
        foreach (PageData page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (BlockData block in page.Blocks)
            {
                if (block.Role == OcrBlockRole.Body)
                {
                    AddBodyBlock(page, block);
                }
            }
        }

        Flush();
        builder.End();

        // Running page furniture goes after the body, so that a paragraph that crosses a page is never interrupted by it.
        if (!pages.Any(static p => p.Blocks.Any(static b => b.Role != OcrBlockRole.Body)))
        {
            return;
        }

        builder.Begin(NodeKind.Section);
        var lastFurniture = new Dictionary<OcrBlockRole, string>();
        foreach (PageData page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] numbers = [(page.Index + 1).ToString(CultureInfo.InvariantCulture), pages.Count.ToString(CultureInfo.InvariantCulture)];
            ITextNormalizer[] furniture = [ControlCharacterNormalizer.Instance, LineBreakNormalizer.Instance, new PageNumberNormalizer(numbers), TypographicNormalizer.Instance];
            foreach ((OcrBlockRole role, NodeKind kind) in new[] { (OcrBlockRole.Header, NodeKind.Header), (OcrBlockRole.Footer, NodeKind.Footer) })
            {
                BlockData[] blocks = [.. page.Blocks.Where(b => b.Role == role)];
                if (blocks.Length == 0)
                {
                    continue;
                }

                var prepared = new List<(OcrPiece Piece, NormalizedText Text)>();
                foreach (BlockData block in blocks)
                {
                    var piece = new OcrPiece(NodeKind.Paragraph, null);
                    foreach (LineData line in block.Lines)
                    {
                        AddLine(piece, page, line, 0, line.Words.Count);
                    }

                    prepared.Add((piece, TextNormalization.Normalize(piece.Raw.ToString(), furniture)));
                }

                // A header or footer that reads like the one on the previous page is the same running text: one node.
                string key = string.Join('\u001F', prepared.Select(static p => p.Text.Text));
                if (lastFurniture.TryGetValue(role, out string? previous) && string.Equals(previous, key, StringComparison.Ordinal))
                {
                    continue;
                }

                lastFurniture[role] = key;
                builder.Begin(kind);
                foreach ((OcrPiece piece, NormalizedText text) in prepared)
                {
                    EmitParagraph(piece, text);
                }

                builder.End();
            }
        }

        builder.End();
    }

    private void AddBodyBlock(PageData page, BlockData block)
    {
        if (block.Lines.Count == 0)
        {
            return;
        }

        if (block.IsTable && options.ReadTables)
        {
            Flush();
            AddTable(page, block);
            return;
        }

        LineData first = block.Lines[0];
        bool firstIsMarker = options.ReadListItems && IsMarkerLine(first);
        if (!(open is not null && !firstIsMarker && options.JoinAcrossColumnsAndPages && CanJoin(page, first)))
        {
            Flush();
        }

        // The gaps between the block's lines: with enough of them, a paragraph gap must also stand out from the usual one, so that
        // double-spaced running text is not cut at every line.
        double widest = block.Lines.Max(static l => l.Bounds.Width);
        double[] gaps = [.. block.Lines.Skip(1).Select((l, k) => block.Lines[k].Bounds.Bottom - l.Bounds.Top)];
        for (int i = 0; i < block.Lines.Count; i++)
        {
            LineData line = block.Lines[i];

            // The recognizer's blocks can hold several paragraphs: a gap between two lines wider than a line's own height ends one.
            if (i > 0)
            {
                LineData above = block.Lines[i - 1];
                double gap = above.Bounds.Bottom - line.Bounds.Top;
                if (gap > ParagraphGap * Math.Max(line.Bounds.Height, above.Bounds.Height) && (gaps.Length < 4 || gap > 1.6 * Median(gaps)))
                {
                    Flush();
                }
            }

            // A clause number ("2.3") at the start of a line starts a paragraph, whatever the spacing.
            if (options.ReadListItems && i > 0 && IsClauseNumberLine(line))
            {
                Flush();
            }

            // A marker starts the block, or follows a line that ended a sentence or a short line (a heading). Inside running text a
            // line that begins with a number ("11. Juni 2026") is a date that the line break put first, not a list item.
            if (options.ReadListItems && IsMarkerLine(line) && (i == 0 || (open is not null && (PdfText.EndsSentence(open.LastLineText) || block.Lines[i - 1].Bounds.Width < ShortLine * widest))))
            {
                Flush();
                open = new OcrPiece(NodeKind.ListItem, line.Words[0].Text);
                AddLine(open, page, line, 1, line.Words.Count);
                continue;
            }

            open ??= new OcrPiece(NodeKind.Paragraph, null);
            AddLine(open, page, line, 0, line.Words.Count);
        }
    }

    private static double Median(double[] values)
    {
        double[] sorted = [.. values.Order()];
        return sorted[sorted.Length / 2];
    }

    /// <summary>Whether a line starts with a multi-level number such as 1.1 or 2.3.4 and goes on with text: the number of a clause.</summary>
    private static bool IsClauseNumberLine(LineData line)
    {
        if (line.Words.Count < 2)
        {
            return false;
        }

        string w = line.Words[0].Text.TrimEnd('.');
        int dots = 0, digits = 0;
        foreach (char c in w)
        {
            if (c == '.')
            {
                dots++;
                digits = 0;
            }
            else if (char.IsAsciiDigit(c) && ++digits <= 3)
            {
                continue;
            }
            else
            {
                return false;
            }
        }

        return dots >= 1 && w.Length >= 3 && w[^1] != '.';
    }

    private static bool IsMarkerLine(LineData line) => line.Words.Count > 1 && PdfText.IsListMarker(line.Words[0].Text);

    private bool CanJoin(PageData page, LineData first)
    {
        OcrPiece p = open!;
        if (p.LastPage < 0 || PdfText.EndsSentence(p.LastLineText) || !PdfText.StartsLikeContinuation(first.Text))
        {
            return false;
        }

        if (page.Index == p.LastPage + 1)
        {
            return true;
        }

        if (page.Index != p.LastPage)
        {
            return false;
        }

        // Same page: a jump up the page (the next column) continues a paragraph. So does a block that starts right under the
        // previous line, closer than any paragraph gap.
        return first.Bounds.Top > p.LastBottom + 2 || p.LastBottom - first.Bounds.Top < 0.4 * first.Bounds.Height;
    }

    private void AddTable(PageData page, BlockData block)
    {
        builder.Begin(NodeKind.Table);
        foreach (LineData line in block.Lines)
        {
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
                var piece = new OcrPiece(NodeKind.Paragraph, null);
                AddLine(piece, page, line, from, to);
                EmitParagraph(piece, TextNormalization.Normalize(piece.Raw.ToString(), BodyNormalizers));
                builder.End();
            }

            builder.End();
        }

        builder.End();
    }

    private void Flush()
    {
        if (open is null)
        {
            return;
        }

        OcrPiece p = open;
        open = null;
        if (!p.IsEmpty)
        {
            EmitParagraph(p, TextNormalization.Normalize(p.Raw.ToString(), BodyNormalizers));
        }
    }

    private void AddLine(OcrPiece p, PageData page, LineData line, int fromWord, int toWord)
    {
        if (toWord <= fromWord)
        {
            return;
        }

        if (!p.IsEmpty)
        {
            p.Raw.Append('\n');
        }

        int firstToken = p.Tokens.Count;
        for (int w = fromWord; w < toWord; w++)
        {
            if (w > fromWord)
            {
                p.Raw.Append(' ');
            }

            AddWord(p, line.Words[w]);
        }

        characters += line.Text.Length + 1;
        if (characters > options.MaxCharacters)
        {
            throw PdfSourceErrors.Make(PdfSourceFailure.TooLarge, $"the document holds more than {options.MaxCharacters} characters of text");
        }

        if (p.Tokens.Count > firstToken)
        {
            p.LineBoxes.Add(HullOf(p.Tokens, firstToken, page.Index));
        }

        p.LastPage = page.Index;
        p.LastBottom = line.Bounds.Bottom;
        p.LastLineText = line.Text;
    }

    private void AddWord(OcrPiece p, WordData word)
    {
        string text = word.Text.Replace('\n', ' ').Replace('\r', ' ');
        int rawStart = p.Raw.Length;
        p.Raw.Append(text);
        if (text.Length == 0)
        {
            return;
        }

        tokenScratch.Clear();
        Tokenizer.Tokenize(text, TokenKind.Word, tokenScratch);
        foreach (TokenRange t in tokenScratch)
        {
            p.Tokens.Add(new OcrTokenRef(rawStart + t.Start, rawStart + t.End, Share(word.Quad, text, t.Start, t.End), word.Confidence));
        }
    }

    /// <summary>
    /// The part of a word's quadrilateral that characters [a, b) of its text cover, shared out along the writing direction in
    /// proportion to a rough width per character (narrow letters and punctuation take less than wide ones).
    /// </summary>
    internal static Quad Share(Quad q, string text, int a, int b)
    {
        if (a <= 0 && b >= text.Length)
        {
            return q;
        }

        double total = 0, before = 0, inside = 0;
        for (int i = 0; i < text.Length; i++)
        {
            double w = Weight(text[i]);
            total += w;
            if (i < a)
            {
                before += w;
            }
            else if (i < b)
            {
                inside += w;
            }
        }

        double t0 = before / total, t1 = (before + inside) / total;
        static (double, double) Lerp(double x0, double y0, double x1, double y1, double t) => (x0 + (t * (x1 - x0)), y0 + (t * (y1 - y0)));
        (double lx0, double ly0) = Lerp(q.X1, q.Y1, q.X2, q.Y2, t0);
        (double lx1, double ly1) = Lerp(q.X1, q.Y1, q.X2, q.Y2, t1);
        (double ux0, double uy0) = Lerp(q.X4, q.Y4, q.X3, q.Y3, t0);
        (double ux1, double uy1) = Lerp(q.X4, q.Y4, q.X3, q.Y3, t1);
        return new Quad(q.Page, lx0, ly0, lx1, ly1, ux1, uy1, ux0, uy0);
    }

    private static double Weight(char c) => c switch
    {
        'i' or 'j' or 'l' or 't' or 'f' or 'I' or '.' or ',' or ';' or ':' or '\'' or '!' or '|' or '`' => 0.45,
        'm' or 'w' or 'M' or 'W' or '@' or '%' => 1.5,
        ' ' => 0.5,
        _ => 1.0,
    };

    private static (Quad Quad, double Confidence) HullOf(List<OcrTokenRef> tokens, int from, int page)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, conf = 1.0;
        for (int i = from; i < tokens.Count; i++)
        {
            Box h = Box.Hull(tokens[i].Quad);
            x0 = Math.Min(x0, h.Left);
            y0 = Math.Min(y0, h.Bottom);
            x1 = Math.Max(x1, h.Right);
            y1 = Math.Max(y1, h.Top);
            conf = Math.Min(conf, tokens[i].Confidence);
        }

        return (new Quad(page, x0, y0, x1, y0, x1, y1, x0, y1), conf);
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

    /// <summary>The box between two tokens of one horizontal line, or null when they are not on one line, touch, or are far apart.</summary>
    private static Quad? GapBetween(Quad left, Quad right)
    {
        if (left.Page != right.Page)
        {
            return null;
        }

        Box l = Box.Hull(left), r = Box.Hull(right);
        double bottom = Math.Min(l.Bottom, r.Bottom), top = Math.Max(l.Top, r.Top);
        double height = Math.Max(l.Height, r.Height);
        if (!(height > 0) || Math.Abs(l.Bottom - r.Bottom) > height * 0.5 || Math.Abs(l.Top - r.Top) > height * 0.5 || !(l.Right < r.Left))
        {
            return null;
        }

        // A wider gap is not a space: something else (another cell or column) lies between the two words.
        return r.Left - l.Right > 2 * height ? null : new Quad(left.Page, l.Right, bottom, r.Left, bottom, r.Left, top, l.Right, top);
    }

    private static Anchor? NodeAnchor(OcrPiece p)
    {
        if (p.LineBoxes.Count == 0)
        {
            return null;
        }

        double conf = p.LineBoxes.Min(static b => b.Confidence);
        return Anchor.ForOcrWords(p.LineBoxes.Select(static b => b.Quad), conf);
    }

    private void EmitParagraph(OcrPiece p, NormalizedText norm)
    {
        var segments = ImmutableArray.CreateBuilder<AnchoredSpan>(p.Tokens.Count * 2);
        int previousEnd = -1;
        Quad previousQuad = default;
        foreach (OcrTokenRef t in p.Tokens)
        {
            int a = norm.MapRawIndex(t.RawStart), b = norm.MapRawIndex(t.RawEnd);
            if (b <= a || a < previousEnd)
            {
                continue;
            }

            // A space between two words on one line gets its own segment, so a change that is only a space is anchored on that
            // space and not on the whole paragraph. A space has no recognition of its own: its confidence is 1.
            if (previousEnd >= 0 && a > previousEnd && IsBlank(norm.Text, previousEnd, a) && GapBetween(previousQuad, t.Quad) is Quad gap)
            {
                segments.Add(new AnchoredSpan(previousEnd, a - previousEnd, Anchor.ForOcrWords([gap], 1.0)));
            }

            segments.Add(new AnchoredSpan(a, b - a, Anchor.ForOcrWords([t.Quad], t.Confidence)));
            previousEnd = b;
            previousQuad = t.Quad;
        }

        FormatSet? attributes = p.Label is null ? null : FormatSet.Of(FormatKeys.ListLabel, p.Label);
        builder.Add(p.Kind, new NodeContent
        {
            Text = norm.Text,
            Folds = norm.Folds,
            Attributes = attributes,
            Anchor = NodeAnchor(p),
            Segments = segments.ToImmutable(),
        });
    }
}
