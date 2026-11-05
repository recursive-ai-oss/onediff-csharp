using System.Text;
using OneOcr;

namespace OneDiff.Ocr.Internal;

/// <summary>
/// Finds the running headers and footers of a scanned document. The recognizer marks some of them (a footer with a page number),
/// but a header or footer that sits as a line in the top or bottom margin is often read as part of the body. A line in the top or
/// bottom tenth of a page whose text, with digits folded, appears in the same margin of another page is running furniture; it is
/// moved to a block of its own with the header or footer role, so the document body reads without it. A document of one page has no
/// repetition to go by and is left as the recognizer read it.
/// </summary>
internal static class PageFurniture
{
    /// <summary>The share of the page height at the top and bottom that can hold furniture.</summary>
    private const double Margin = 0.10;

    public static IReadOnlyList<PageData> Detect(IReadOnlyList<PageData> pages)
    {
        if (pages.Count < 2)
        {
            return pages;
        }

        // The keys of the lines in each margin, per page.
        var top = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var bottom = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (PageData page in pages)
        {
            foreach (BlockData b in page.Blocks)
            {
                if (b.Role != OcrBlockRole.Body || b.IsTable)
                {
                    continue;
                }

                foreach (LineData l in b.Lines)
                {
                    Dictionary<string, HashSet<int>>? zone = ZoneOf(page, l);
                    string key = Key(l);
                    if (zone is not null && key.Length > 0)
                    {
                        if (!zone.TryGetValue(key, out HashSet<int>? set))
                        {
                            zone[key] = set = [];
                        }

                        set.Add(page.Index);
                    }
                }
            }

            Dictionary<string, HashSet<int>>? ZoneOf(PageData p, LineData l) =>
                l.Bounds.Bottom >= p.Height * (1 - Margin) ? top : l.Bounds.Top <= p.Height * Margin ? bottom : null;
        }

        var result = new List<PageData>(pages.Count);
        foreach (PageData page in pages)
        {
            var blocks = new List<BlockData>();
            foreach (BlockData b in page.Blocks)
            {
                if (b.Role != OcrBlockRole.Body || b.IsTable)
                {
                    blocks.Add(b);
                    continue;
                }

                // Runs of lines with the same role, in order.
                var run = new List<LineData>();
                OcrBlockRole runRole = OcrBlockRole.Body;
                void Emit()
                {
                    if (run.Count > 0)
                    {
                        blocks.Add(new BlockData(runRole, false, [.. run], Hull(run)));
                        run.Clear();
                    }
                }

                foreach (LineData l in b.Lines)
                {
                    OcrBlockRole role = OcrBlockRole.Body;
                    string key = Key(l);
                    if (key.Length > 0)
                    {
                        if (l.Bounds.Bottom >= page.Height * (1 - Margin) && top.TryGetValue(key, out HashSet<int>? t) && t.Count > 1)
                        {
                            role = OcrBlockRole.Header;
                        }
                        else if (l.Bounds.Top <= page.Height * Margin && bottom.TryGetValue(key, out HashSet<int>? bt) && bt.Count > 1)
                        {
                            role = OcrBlockRole.Footer;
                        }
                    }

                    if (role != runRole)
                    {
                        Emit();
                        runRole = role;
                    }

                    run.Add(l);
                }

                Emit();
            }

            result.Add(page with { Blocks = blocks });
        }

        return result;
    }

    private static Box Hull(List<LineData> lines) =>
        new(lines.Min(static l => l.Bounds.Left), lines.Min(static l => l.Bounds.Bottom), lines.Max(static l => l.Bounds.Right), lines.Max(static l => l.Bounds.Top));

    /// <summary>The text of a line in lower case, without spaces and with every digit folded to #, so "Page 3 of 9" and "Page 4 of 9" are one key.</summary>
    private static string Key(LineData line)
    {
        var sb = new StringBuilder(line.Text.Length);
        foreach (char c in line.Text)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            sb.Append(char.IsAsciiDigit(c) ? '#' : char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
