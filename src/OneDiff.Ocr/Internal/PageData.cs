using OneDiff.Documents.Model;
using OneOcr;

namespace OneDiff.Ocr.Internal;

/// <summary>A rectangle in upright page space: points, origin at the lower left of the page as displayed, y up.</summary>
internal readonly record struct Box(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public static Box Hull(Quad q) =>
        new(Math.Min(Math.Min(q.X1, q.X2), Math.Min(q.X3, q.X4)), Math.Min(Math.Min(q.Y1, q.Y2), Math.Min(q.Y3, q.Y4)),
            Math.Max(Math.Max(q.X1, q.X2), Math.Max(q.X3, q.X4)), Math.Max(Math.Max(q.Y1, q.Y2), Math.Max(q.Y3, q.Y4)));
}

/// <summary>A recognized word with its quadrilateral (lower left, lower right, upper right, upper left of the text) and confidence.</summary>
internal sealed record WordData(string Text, Quad Quad, double Confidence);

internal sealed record LineData(IReadOnlyList<WordData> Words, Box Bounds, IReadOnlyList<int> CellStarts)
{
    public string Text { get; } = string.Join(' ', Words.Select(static w => w.Text));
}

internal sealed record BlockData(OcrBlockRole Role, bool IsTable, IReadOnlyList<LineData> Lines, Box Bounds);

internal sealed record PageData(int Index, double Width, double Height, IReadOnlyList<BlockData> Blocks);

/// <summary>
/// Converts the OCR page model (image or page units, origin top left, y down) to upright page space in points (origin at the lower
/// left, y up), which is the space every page anchor of the compare model and the PDF markup writer use. Corner order follows the
/// OCR page format: top left, top right, bottom right, bottom left of the text's own frame.
/// </summary>
internal static class PageConversion
{
    public static PageData Convert(OcrPage page)
    {
        double s = 72.0 / page.UnitsPerInch;
        double heightPoints = page.Height * s;
        var blocks = new List<BlockData>(page.Blocks.Count);
        foreach (OcrBlock b in page.Blocks)
        {
            var lines = new List<LineData>(b.Lines.Count);
            foreach (OcrLine l in b.Lines)
            {
                var words = new List<WordData>(l.Words.Count);
                foreach (OcrWord w in l.Words)
                {
                    if (w.Text.Length == 0)
                    {
                        continue;
                    }

                    words.Add(new WordData(w.Text, ToQuad(page.Index, w.Quad, w.Box, s, heightPoints), Math.Clamp((double)w.Confidence, 0.0, 1.0)));
                }

                if (words.Count > 0)
                {
                    // Cell starts index the line's words; empty words were dropped above, so the indices are recomputed against what is left.
                    lines.Add(new LineData(words, Box.Hull(ToQuad(page.Index, l.Quad, l.Box, s, heightPoints)), CellStarts(l, words.Count)));
                }
            }

            if (lines.Count > 0)
            {
                blocks.Add(new BlockData(b.Role, b.IsTable, lines, Box.Hull(ToQuad(page.Index, b.Quad, b.Box, s, heightPoints))));
            }
        }

        return new PageData(page.Index, page.Width * s, heightPoints, blocks);
    }

    private static IReadOnlyList<int> CellStarts(OcrLine line, int wordCount)
    {
        if (line.Words.Count == wordCount)
        {
            return line.Cells;
        }

        // Some words were empty: map each cell start to the count of kept words before it.
        var kept = new List<int>();
        int before = 0, next = 0;
        for (int i = 0; i < line.Words.Count; i++)
        {
            while (next < line.Cells.Count && line.Cells[next] == i)
            {
                int start = before;
                if (kept.Count == 0 || kept[^1] != start)
                {
                    kept.Add(start);
                }

                next++;
            }

            if (line.Words[i].Text.Length > 0)
            {
                before++;
            }
        }

        return kept;
    }

    private static Quad ToQuad(int page, OcrQuad? quad, OcrBox box, double s, double heightPoints)
    {
        OcrQuad q = quad ?? new OcrQuad(new OcrPoint(box.Left, box.Top), new OcrPoint(box.Right, box.Top), new OcrPoint(box.Right, box.Bottom), new OcrPoint(box.Left, box.Bottom));
        (double X, double Y) P(OcrPoint p) => (p.X * s, heightPoints - (p.Y * s));
        (double x0, double y0) = P(q.P3);
        (double x1, double y1) = P(q.P2);
        (double x2, double y2) = P(q.P1);
        (double x3, double y3) = P(q.P0);
        return new Quad(page, x0, y0, x1, y1, x2, y2, x3, y3);
    }
}
