using System.Text;
using OneOcr;

namespace OneDiff.Ocr.Internal;

/// <summary>
/// Finds the running headers and footers of a scanned document. The recognizer marks some of them (a footer with a page number),
/// but a header or footer that sits as a line in the top or bottom margin is often read as part of the body. A line in the top or
/// bottom tenth of a page whose letters resemble a line in the same margin of another page is running furniture; it is
/// moved to a block of its own with the header or footer role, so the document body reads without it. A line of digits and
/// punctuation alone in a margin is a page number. A document of one page has no other repetition to go by.
/// </summary>
internal static class PageFurniture
{
    /// <summary>The share of the page height at the top and bottom that can hold furniture.</summary>
    private const double Margin = 0.10;

    /// <summary>The least bigram similarity of two margin lines' letters at which they are taken for the same running text.</summary>
    private const double SameText = 0.65;

    /// <summary>The longest line of digits and punctuation alone that is a page number.</summary>
    private const int PageNumberLength = 12;

    public static IReadOnlyList<PageData> Detect(IReadOnlyList<PageData> pages)
    {
        // The letters of the lines in each margin, per page. Recognition errors make two copies of one header differ by a letter or two,
        // so lines are compared by similarity, not equality.
        var top = new List<(int Page, string Letters)>();
        var bottom = new List<(int Page, string Letters)>();
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
                    string letters = Letters(l);
                    if (letters.Length >= 3)
                    {
                        if (InTop(page, l))
                        {
                            top.Add((page.Index, letters));
                        }
                        else if (InBottom(page, l))
                        {
                            bottom.Add((page.Index, letters));
                        }
                    }
                }
            }
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
                    if (InTop(page, l) && IsFurniture(page, l, top))
                    {
                        role = OcrBlockRole.Header;
                    }
                    else if (InBottom(page, l) && IsFurniture(page, l, bottom))
                    {
                        role = OcrBlockRole.Footer;
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

    private static bool InTop(PageData p, LineData l) => l.Bounds.Bottom >= p.Height * (1 - Margin);

    private static bool InBottom(PageData p, LineData l) => l.Bounds.Top <= p.Height * Margin;

    /// <summary>
    /// A margin line is furniture when its letters resemble a line in the same margin of another page, or when it is a page number
    /// by itself (digits and punctuation only, as "- 3 -"), which needs no other page to be recognized.
    /// </summary>
    private static bool IsFurniture(PageData page, LineData line, List<(int Page, string Letters)> margin)
    {
        string letters = Letters(line);
        if (letters.Length == 0)
        {
            string t = line.Text.Replace(" ", string.Empty, StringComparison.Ordinal);
            return t.Length is > 0 and <= PageNumberLength && t.Any(char.IsAsciiDigit);
        }

        if (letters.Length < 3)
        {
            return false;
        }

        // "Page 2 of 9" in any of the supported languages is a page number wherever it stands in the margin, even if no other page repeats it.
        if (IsPageNumberWords(letters))
        {
            return true;
        }

        foreach ((int other, string l) in margin)
        {
            if (other != page.Index && Similarity(letters, l) >= SameText)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] PageWords = ["page", "pagina", "p\u00E1gina", "seite", "side", "sida", "strona", "sayfa"];

    private static readonly string[] OfWords = ["of", "de", "von", "sur", "di", "van", "z"];

    /// <summary>Whether a line's letters are only the word for "page", alone or with the word for "of" (as in "Page 2 of 9").</summary>
    private static bool IsPageNumberWords(string letters)
    {
        foreach (string p in PageWords)
        {
            if (letters == p)
            {
                return true;
            }

            if (letters.StartsWith(p, StringComparison.Ordinal) && OfWords.Contains(letters[p.Length..]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether two furniture texts (as letters) are one running text.</summary>
    internal static bool Alike(string a, string b) => Similarity(a, b) >= SameText;

    /// <summary>Dice coefficient over the character pairs of two strings.</summary>
    internal static double Similarity(string a, string b)
    {
        if (a == b)
        {
            return 1;
        }

        if (a.Length < 2 || b.Length < 2 || Math.Min(a.Length, b.Length) < 0.5 * Math.Max(a.Length, b.Length))
        {
            return 0;
        }

        var counts = new Dictionary<int, int>();
        for (int i = 0; i + 1 < a.Length; i++)
        {
            int key = (a[i] << 16) | a[i + 1];
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        int shared = 0;
        for (int i = 0; i + 1 < b.Length; i++)
        {
            int key = (b[i] << 16) | b[i + 1];
            if (counts.TryGetValue(key, out int c) && c > 0)
            {
                counts[key] = c - 1;
                shared++;
            }
        }

        return 2.0 * shared / ((a.Length - 1) + (b.Length - 1));
    }

    private static Box Hull(List<LineData> lines) =>
        new(lines.Min(static l => l.Bounds.Left), lines.Min(static l => l.Bounds.Bottom), lines.Max(static l => l.Bounds.Right), lines.Max(static l => l.Bounds.Top));

    /// <summary>The line's letters in lower case: digits, spaces and punctuation dropped, so "Page 3 of 9" and "Page 4/. 9" are alike.</summary>
    private static string Letters(LineData line) => LettersOf(line.Text);

    /// <summary>The letters of a text in lower case.</summary>
    internal static string LettersOf(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }
}
