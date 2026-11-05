using System.Globalization;
using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Word.Tests;

/// <summary>Seeded generation of documents and edited copies for the invertibility sweep.</summary>
internal sealed class Gen(int seed)
{
    private static readonly string[] Words =
    [
        "supplier", "customer", "shall", "deliver", "goods", "within", "thirty", "days", "payment", "invoice", "terminate", "notice", "party", "agreement",
        "liability", "confidential", "information", "warranty", "services", "fees", "term", "renewal", "governing", "law", "dispute", "arbitration", "damages",
        "indemnify", "assignment", "force", "majeure", "schedule", "amount", "dollars", "percent", "interest", "late", "written", "consent", "rights",
    ];

    private readonly Random rng = new(seed);

    public int Next(int n) => rng.Next(n);

    private string Word() => Words[rng.Next(Words.Length)];

    public string Sentence()
    {
        int n = rng.Next(4, 16);
        var parts = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            string w = Word();
            if (rng.Next(12) == 0)
            {
                w = (rng.Next(1, 100)).ToString(CultureInfo.InvariantCulture);
            }

            parts.Add(w);
        }

        string s = string.Join(' ', parts);
        return char.ToUpperInvariant(s[0]) + s[1..] + ".";
    }

    private Inl[] Inlines()
    {
        var list = new List<Inl>();
        int n = rng.Next(1, 5);
        for (int i = 0; i < n; i++)
        {
            string text = Sentence() + " ";
            list.Add(rng.Next(6) switch
            {
                0 => new T(text) { Bold = true },
                1 => new T(text) { Italic = true, Color = rng.Next(2) == 0 ? "1F3864" : null },
                2 => new T(text) { Link = "https://example.com/" + Word() },
                _ => new T(text),
            });
        }

        switch (rng.Next(14))
        {
            case 0:
                list.Add(new Fld("PAGE", rng.Next(1, 9).ToString(CultureInfo.InvariantCulture)));
                break;
            case 1:
                list.Add(new Foot(Sentence()));
                break;
            case 2:
                list.Add(new Img(DocFactory.Png((byte)rng.Next(256)), Word()));
                break;
            case 3:
                list.Add(new Box(Sentence()));
                break;
            case 4:
                list.Add(new Cc(Word(), "t" + rng.Next(5).ToString(CultureInfo.InvariantCulture)));
                break;
            case 5:
                list.Add(new TabChar());
                list.Add(new T(Word()));
                break;
            case 6:
                list.Add(new SimpleFld("REF clause" + rng.Next(9).ToString(CultureInfo.InvariantCulture), "Clause " + rng.Next(1, 20).ToString(CultureInfo.InvariantCulture)));
                break;
            case 7:
                list.Add(new Brk());
                list.Add(new T(Word()));
                break;
        }

        return [.. list];
    }

    private Para Paragraph()
    {
        Para p = new(Inlines());
        switch (rng.Next(8))
        {
            case 0:
                p = p with { Style = "Heading1" };
                break;
            case 1:
                p = p with { List = 0, Level = rng.Next(3) };
                break;
            case 2:
                p = p with { List = 1 };
                break;
            case 3:
                p = p with { Jc = rng.Next(2) == 0 ? "center" : "both" };
                break;
            case 4:
                p = p with { IndentLeft = 360 * rng.Next(1, 4) };
                break;
        }

        if (rng.Next(25) == 0)
        {
            p = p with { Comment = Sentence() };
        }

        return p;
    }

    private Tbl Table()
    {
        int rows = rng.Next(2, 6), cols = rng.Next(2, 5);
        var table = new Cell[rows][];
        for (int r = 0; r < rows; r++)
        {
            var row = new List<Cell>();
            int used = 0;
            while (used < cols)
            {
                int span = rng.Next(8) == 0 && cols - used > 1 ? 2 : 1;
                Cell c = new(Para.Of(rng.Next(4) == 0 ? rng.Next(10, 9999).ToString(CultureInfo.InvariantCulture) : Sentence())) { Span = span };
                if (r > 0 && span == 1 && rng.Next(15) == 0)
                {
                    c = c with { VMerge = "continue" };
                }

                row.Add(c);
                used += span;
            }

            table[r] = [.. row];
        }

        return new Tbl(table) { Style = rng.Next(5) == 0 ? "TableGrid" : null };
    }

    public DocSpec Document()
    {
        var blocks = new List<Blk>();
        int n = rng.Next(3, 24);
        for (int i = 0; i < n; i++)
        {
            switch (rng.Next(14))
            {
                case 0:
                    blocks.Add(Table());
                    break;
                case 1:
                    blocks.Add(new Sdt("blk" + rng.Next(5).ToString(CultureInfo.InvariantCulture), Paragraph(), Paragraph()));
                    break;
                case 2 when i > 1 && i < n - 1:
                    blocks.Add(Para.Of(Sentence()) with { SectionBreak = true, Landscape = rng.Next(2) == 0 });
                    break;
                default:
                    blocks.Add(Paragraph());
                    break;
            }
        }

        return new DocSpec([.. blocks])
        {
            Lists = 1,
            Bullets = true,
            Header = rng.Next(3) == 0 ? Sentence() : null,
            Footer = rng.Next(3) == 0 ? Sentence() : null,
            Title = rng.Next(2) == 0 ? Word() : null,
            Author = rng.Next(2) == 0 ? Word() : null,
        };
    }

    private Inl[] EditInlines(Inl[] inlines)
    {
        var list = inlines.ToList();
        if (list.Count == 0)
        {
            return [new T(Sentence())];
        }

        int i = rng.Next(list.Count);
        switch (rng.Next(6))
        {
            case 0 when list[i] is T t:
                {
                    string[] w = t.Text.Split(' ');
                    w[rng.Next(w.Length)] = Word();
                    list[i] = t with { Text = string.Join(' ', w) };
                    break;
                }

            case 1 when list[i] is T t:
                list[i] = t with { Bold = !t.Bold };
                break;
            case 2:
                list.Insert(i, new T(Sentence() + " "));
                break;
            case 3 when list.Count > 1:
                list.RemoveAt(i);
                break;
            case 4 when list[i] is T t:
                list[i] = t with { Text = t.Text.Replace("a", "\u2019", StringComparison.Ordinal) };
                break;
            case 5 when list[i] is Img img:
                list[i] = img with { Png = DocFactory.Png((byte)rng.Next(256)) };
                break;
            case 5 when list[i] is Foot f:
                list[i] = f with { Text = Sentence() };
                break;
            default:
                list.Add(new T(Word() + " "));
                break;
        }

        return [.. list];
    }

    private Blk EditBlock(Blk b)
    {
        switch (b)
        {
            case Para p:
                return rng.Next(6) switch
                {
                    0 => p with { Style = p.Style is null ? "Heading1" : null },
                    1 => p with { List = p.List >= 0 ? -1 : 0, Level = 0 },
                    2 => p with { Level = (p.Level + 1) % 3 },
                    3 => p with { Jc = p.Jc is null ? "right" : null },
                    _ => new Para(EditInlines(p.Inlines)) { Style = p.Style, List = p.List, Level = p.Level, Jc = p.Jc, IndentLeft = p.IndentLeft, Comment = p.Comment, SectionBreak = p.SectionBreak, Landscape = p.Landscape },
                };
            case Tbl t:
                {
                    var rows = t.Rows.Select(r => r.ToArray()).ToList();
                    switch (rng.Next(3))
                    {
                        case 0 when rows.Count > 1:
                            rows.RemoveAt(rng.Next(rows.Count));
                            break;
                        case 1:
                            rows.Insert(rng.Next(rows.Count + 1), [.. rows[0].Select(c => new Cell(Para.Of(Sentence())) { Span = c.Span })]);
                            break;
                        default:
                            {
                                int r = rng.Next(rows.Count), c = rng.Next(rows[r].Length);
                                rows[r][c] = rows[r][c] with { Blocks = [Para.Of(Sentence())] };
                                break;
                            }
                    }

                    return new Tbl([.. rows]) { Style = t.Style };
                }

            default:
                return b;
        }
    }

    public DocSpec Edit(DocSpec old)
    {
        var blocks = old.Blocks.ToList();
        int edits = rng.Next(1, 8);
        for (int e = 0; e < edits; e++)
        {
            if (blocks.Count == 0)
            {
                blocks.Add(Paragraph());
                continue;
            }

            int i = rng.Next(blocks.Count);
            switch (rng.Next(9))
            {
                case 0:
                    blocks.RemoveAt(i);
                    break;
                case 1:
                    blocks.Insert(i, Paragraph());
                    break;
                case 2:
                    {
                        Blk moved = blocks[i];
                        blocks.RemoveAt(i);
                        blocks.Insert(rng.Next(blocks.Count + 1), moved);
                        break;
                    }

                case 3 when blocks[i] is Para p && p.Inlines.Length > 0 && !p.SectionBreak:
                    {
                        // split a paragraph in two
                        int cut = rng.Next(1, Math.Max(2, p.Inlines.Length));
                        blocks[i] = new Para(p.Inlines[..Math.Min(cut, p.Inlines.Length)]) { Style = p.Style, List = p.List, Level = p.Level };
                        blocks.Insert(i + 1, new Para(p.Inlines[Math.Min(cut, p.Inlines.Length)..]));
                        break;
                    }

                case 4 when i + 1 < blocks.Count && blocks[i] is Para a && blocks[i + 1] is Para b && !a.SectionBreak:
                    blocks[i] = new Para([.. a.Inlines, .. b.Inlines]) { Style = a.Style, List = a.List, Level = a.Level };
                    blocks.RemoveAt(i + 1);
                    break;
                default:
                    blocks[i] = EditBlock(blocks[i]);
                    break;
            }
        }

        return old with
        {
            Blocks = [.. blocks],
            Header = rng.Next(6) == 0 ? Sentence() : old.Header,
            Footer = rng.Next(8) == 0 ? null : old.Footer,
            Title = rng.Next(6) == 0 ? Word() : old.Title,
        };
    }
}

public class SweepTests
{
    [Fact]
    public void SeededPairsInvertWithNoFallback()
    {
        int pairs = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_WORD_SWEEP"), out int n) ? n : 40;
        int first = int.TryParse(Environment.GetEnvironmentVariable("ONEDIFF_WORD_SWEEP_FIRST"), out int f) ? f : 0;
        var source = new WordDocumentSource();
        int failures = 0, fallbacks = 0, withChanges = 0;
        long digest = 17;
        var messages = new List<string>();
        for (int s = first; s < first + pairs; s++)
        {
            var gen = new Gen(s);
            DocSpec oldSpec = gen.Document();
            DocSpec newSpec = gen.Edit(oldSpec);
            byte[] oldBytes = DocFactory.Build(oldSpec), newBytes = DocFactory.Build(newSpec);
            CompareDocument a = source.Read(DocumentInput.FromBytes(oldBytes, "old.docx")), b = source.Read(DocumentInput.FromBytes(newBytes, "new.docx"));
            foreach (CompareSettings settings in new[] { CompareSettings.Legal, CompareSettings.Strict })
            {
                try
                {
                    Comparison c = Comparison.Of(a, b, settings);
                    if (!c.Verification.Passed || c.Check().Length > 0)
                    {
                        failures++;
                        messages.Add($"seed {s} {settings.Name}: verification failed");
                    }

                    if (c.Verification.FallbackLevel != 0)
                    {
                        fallbacks++;
                        messages.Add($"seed {s} {settings.Name}: fallback {c.Verification.FallbackLevel}");
                    }

                    if (c.Changes.Length > 0)
                    {
                        withChanges++;
                    }

                    digest = (digest * 31) + c.Changes.Length;
                }
                catch (Exception ex)
                {
                    failures++;
                    messages.Add($"seed {s} {settings.Name}: {ex.GetType().Name} {ex.Message}");
                }
            }

            // Same file against itself: no changes.
            Comparison same = Comparison.Of(a, a);
            if (same.Changes.Length != 0 || !same.Verification.Passed)
            {
                failures++;
                messages.Add($"seed {s}: identical documents differ");
            }
        }

        string summary = $"word sweep: pairs={pairs} first={first} failures={failures} fallbacks={fallbacks} withChanges={withChanges} digest={digest}";
        Console.WriteLine(summary);
        if (Environment.GetEnvironmentVariable("ONEDIFF_WORD_SWEEP_OUT") is { Length: > 0 } path)
        {
            File.WriteAllText(path, summary + "\n");
        }

        Assert.True(failures == 0 && fallbacks == 0, string.Join("\n", messages.Take(10)));
        Assert.True(withChanges > pairs, "the edits should show as changes");
    }
}
