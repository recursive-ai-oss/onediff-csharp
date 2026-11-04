using System.Globalization;
using System.Text;
using OneDiff.Documents.Model;

namespace OneDiff.GoldSets;

/// <summary>A small deterministic generator (SplitMix64) so the same seed gives the same documents on every platform.</summary>
internal sealed class Rng
{
    private ulong state;

    public Rng(ulong seed) => state = seed;

    public static ulong Mix(ulong a, string text)
    {
        ulong h = a ^ 0x9E3779B97F4A7C15UL;
        foreach (char c in text)
        {
            h = (h ^ c) * 0x100000001B3UL;
            h ^= h >> 29;
        }

        return h;
    }

    public static ulong Mix(ulong a, long b) => new Rng(a ^ (ulong)b * 0xD6E8FEB86659FD93UL).NextU();

    public ulong NextU()
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A number in [0, n).</summary>
    public int Next(int n) => n <= 1 ? 0 : (int)(NextU() % (ulong)n);

    public int Next(int lo, int hiInclusive) => lo + Next(hiInclusive - lo + 1);

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    public bool Chance(int percent) => Next(100) < percent;

    public void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

internal enum PK { Plain, Amount, Duration, Percent, Date, Term, Modal, Adverb, Ref, Jurisdiction, Role }

/// <summary>A run of a sentence's text that is either plain or one filled slot.</summary>
internal sealed class Piece
{
    public int Uid;
    public string Text = "";
    public PK Kind;
    /// <summary>Canonical, language-neutral value for typed pieces; the target block's uid for a reference.</summary>
    public string Canon = "";
    public bool Bold;
    /// <summary>The unit this piece is part of in this document, or -1.</summary>
    public int Unit = -1;

    public Piece Clone() => (Piece)MemberwiseClone();
}

internal sealed class SlotVal
{
    public string Text = "";
    public PK Kind;
    public string Canon = "";
}

internal sealed class Sentence
{
    public int Uid;
    public int Template;
    public int Variant;
    public Dictionary<char, SlotVal> Slots = [];
    public List<Piece> Pieces = [];

    public Sentence Clone() => new()
    {
        Uid = Uid, Template = Template, Variant = Variant, Slots = Slots,
        Pieces = [.. Pieces.Select(p => p.Clone())],
    };
}

internal enum BK { Title, Preamble, Recital, Heading, Clause, Definition, Closing, Cell }

internal sealed class Block
{
    public int Uid;
    public BK Role;
    public List<Sentence> Sentences = [];
    public int WholeUnit = -1;
    public bool Moved;
    public bool Protected;

    public Block Clone() => new()
    {
        Uid = Uid, Role = Role, WholeUnit = -1, Moved = false, Protected = Protected,
        Sentences = [.. Sentences.Select(s => s.Clone())],
    };
}

internal sealed class Row
{
    public int Uid;
    public List<Block> Cells = [];
}

/// <summary>A top-level item of a document: a block, or a table of rows.</summary>
internal sealed class Item
{
    public Block? Block;
    public List<Row>? Rows;
    public int TableUid;

    public Item Clone() => new()
    {
        Block = Block?.Clone(), TableUid = TableUid,
        Rows = Rows?.Select(r => new Row { Uid = r.Uid, Cells = [.. r.Cells.Select(c => c.Clone())] }).ToList(),
    };
}

internal sealed class GDoc
{
    public string Name = "";
    public string Term = "";
    public List<Item> Items = [];

    public GDoc Clone() => new() { Name = Name, Term = Term, Items = [.. Items.Select(i => i.Clone())] };

    public IEnumerable<Block> Blocks()
    {
        foreach (Item it in Items)
        {
            if (it.Block is not null)
            {
                yield return it.Block;
            }

            if (it.Rows is not null)
            {
                foreach (Row r in it.Rows)
                {
                    foreach (Block c in r.Cells)
                    {
                        yield return c;
                    }
                }
            }
        }
    }
}

/// <summary>Builds sentences and documents from a lexicon. All randomness comes from the Rng it is given.</summary>
internal sealed class DocBuilder
{
    private readonly Lexicon lex;
    private int nextUid = 1;

    public DocBuilder(Lexicon lex) => this.lex = lex;

    public int Uid() => nextUid++;

    /// <summary>Continues numbering after the uids a document already uses.</summary>
    public void ReserveAbove(int uid) => nextUid = Math.Max(nextUid, uid + 1);

    private static readonly long[] Amounts = [1200, 2500, 4800, 7500, 9000, 12500, 18000, 25000, 40000, 60000, 100000, 250000];
    private static readonly int[] Percents = [2, 3, 4, 5, 6, 8, 10, 12, 15];
    private static readonly int[][] DurationValues = [[10, 14, 15, 30, 45, 60, 90], [3, 6, 9, 12, 18, 24], [1, 2, 3, 5]];

    public SlotVal NewAmount(Rng r, string? not = null)
    {
        while (true)
        {
            long a = r.Pick(Amounts);
            string canon = $"{lex.Currency} {a}";
            if (canon != not)
            {
                return new SlotVal { Kind = PK.Amount, Text = lex.FormatAmount(a), Canon = canon };
            }
        }
    }

    public SlotVal NewPercent(Rng r, string? not = null)
    {
        while (true)
        {
            int p = r.Pick(Percents);
            string canon = $"{p}%";
            if (canon != not)
            {
                return new SlotVal { Kind = PK.Percent, Text = lex.FormatPercent(p), Canon = canon };
            }
        }
    }

    public SlotVal NewDuration(Rng r, string? not = null)
    {
        while (true)
        {
            int u = r.Next(3);
            int n = r.Pick(DurationValues[u]);
            string canon = $"{n} {"d mo y".Split(' ')[u]}";
            if (canon != not)
            {
                return new SlotVal { Kind = PK.Duration, Text = lex.FormatDuration(n, u), Canon = canon };
            }
        }
    }

    public SlotVal NewDate(Rng r, string? not = null)
    {
        while (true)
        {
            int y = r.Next(2026, 2029);
            int m = r.Next(1, 12);
            int d = r.Next(1, 28);
            string canon = $"{y:0000}-{m:00}-{d:00}";
            if (canon != not)
            {
                return new SlotVal { Kind = PK.Date, Text = lex.FormatDate(y, m, d), Canon = canon };
            }
        }
    }

    private SlotVal Make(char slot, Rng r, GDoc doc, IReadOnlyList<Block> refTargets) => slot switch
    {
        'A' => NewAmount(r),
        'P' => NewPercent(r),
        'D' => NewDuration(r),
        'T' => NewDate(r),
        'M' => new SlotVal { Kind = PK.Modal, Text = r.Pick(lex.Modal) },
        'W' => new SlotVal { Kind = PK.Adverb, Text = r.Pick(lex.Adverb) },
        'J' => new SlotVal { Kind = PK.Jurisdiction, Text = r.Pick(lex.Jurisdictions) },
        'U' => new SlotVal { Kind = PK.Role, Text = lex.Supplier },
        'C' => new SlotVal { Kind = PK.Role, Text = lex.Customer },
        'X' => new SlotVal { Kind = PK.Term, Text = doc.Term },
        'R' => new SlotVal { Kind = PK.Ref, Canon = refTargets.Count == 0 ? "0" : r.Pick(refTargets).Uid.ToString(CultureInfo.InvariantCulture) },
        _ => throw new InvalidOperationException("slot " + slot),
    };

    /// <summary>Creates a sentence for a template with freshly drawn slot values.</summary>
    public Sentence NewSentence(Rng r, GDoc doc, int template, int variant, IReadOnlyList<Block> refTargets)
    {
        (string v0, string v1) = lex.Templates[template];
        var s = new Sentence { Uid = Uid(), Template = template, Variant = variant };
        foreach (char c in (v0 + v1).Where(ch => ch is >= 'A' and <= 'Z').Distinct())
        {
            // Only characters inside braces are slots; everything else in a template is lower-case or non-Latin.
            if ((v0 + v1).Contains("{" + c + "}", StringComparison.Ordinal))
            {
                s.Slots[c] = Make(c, r, doc, refTargets);
            }
        }

        Render(s);
        return s;
    }

    /// <summary>Builds a sentence from literal text with slot letters (recitals, definitions, closing).</summary>
    public Sentence FromText(Rng r, GDoc doc, string text)
    {
        var s = new Sentence { Uid = Uid(), Template = -1, Variant = 0 };
        for (int i = 0; i + 2 < text.Length; i++)
        {
            if (text[i] == '{' && text[i + 2] == '}' && !s.Slots.ContainsKey(text[i + 1]))
            {
                s.Slots[text[i + 1]] = Make(text[i + 1], r, doc, []);
            }
        }

        RenderText(s, text);
        return s;
    }

    /// <summary>Fills in the pieces of a template sentence from its slots and variant.</summary>
    public void Render(Sentence s)
    {
        (string v0, string v1) = lex.Templates[s.Template];
        RenderText(s, s.Variant == 0 ? v0 : v1);
    }

    private void RenderText(Sentence s, string text)
    {
        s.Pieces = [];
        var plain = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '{' && i + 2 < text.Length && text[i + 2] == '}')
            {
                if (plain.Length > 0)
                {
                    s.Pieces.Add(new Piece { Uid = Uid(), Kind = PK.Plain, Text = plain.ToString() });
                    plain.Clear();
                }

                SlotVal v = s.Slots[text[i + 1]];
                s.Pieces.Add(new Piece { Uid = Uid(), Kind = v.Kind, Text = v.Text, Canon = v.Canon });
                i += 2;
            }
            else
            {
                plain.Append(text[i]);
            }
        }

        if (plain.Length > 0)
        {
            s.Pieces.Add(new Piece { Uid = Uid(), Kind = PK.Plain, Text = plain.ToString() });
        }

        if (lex.Spaced && s.Pieces.Count > 0 && s.Pieces[0].Text.Length > 0)
        {
            s.Pieces[0].Text = Lexicon.CapitalizeFirst(s.Pieces[0].Text);
        }
    }

    private Block NewBlock(BK role, params Sentence[] sentences) => new() { Uid = Uid(), Role = role, Sentences = [.. sentences] };

    /// <summary>A contract-like document: title, preamble, recitals, a definitions article, articles of clauses, a fee table and a closing line.</summary>
    public GDoc BuildDocument(Rng r, string name)
    {
        var doc = new GDoc { Name = name, Term = r.Pick(lex.Terms) };
        var refTargets = new List<Block>();
        var items = doc.Items;
        items.Add(new Item { Block = NewBlock(BK.Title, FromText(r, doc, lex.Title)) });
        items.Add(new Item { Block = NewBlock(BK.Preamble, FromText(r, doc, lex.Preamble)) });
        var recitalIdx = Enumerable.Range(0, lex.Recitals.Length).ToList();
        r.Shuffle(recitalIdx);
        int recitals = r.Next(3, 3);
        for (int i = 0; i < recitals; i++)
        {
            items.Add(new Item { Block = NewBlock(BK.Recital, FromText(r, doc, lex.Recitals[recitalIdx[2 * i % recitalIdx.Count]]), FromText(r, doc, lex.Recitals[recitalIdx[(2 * i + 1) % recitalIdx.Count]])) });
        }

        // Definitions article.
        items.Add(new Item { Block = NewBlock(BK.Heading, FromText(r, doc, lex.Headings[0])) });
        for (int i = 0; i < r.Next(2, 3); i++)
        {
            items.Add(new Item { Block = NewBlock(BK.Definition, FromText(r, doc, lex.Definitions[i])) });
        }

        // Clauses are drawn before references are resolved, so references can point at any clause.
        var articleOf = new List<(int Heading, List<Block> Clauses)>();
        int articles = r.Next(5, 8);
        var headings = Enumerable.Range(1, lex.Headings.Length - 1).ToList();
        r.Shuffle(headings);
        int[][] groups = [[0, 1, 2], [3, 4], [5, 6], [7], [8], [9, 10], [11], [0, 1, 4, 8]];
        var pending = new List<(Block B, int Template)>();
        for (int a = 0; a < articles; a++)
        {
            int g = a % groups.Length;
            items.Add(new Item { Block = NewBlock(BK.Heading, FromText(r, doc, lex.Headings[headings[a % headings.Count]])) });
            var clauses = new List<Block>();
            for (int c = r.Next(2, 4); c > 0; c--)
            {
                int sentences = r.Next(100) < 35 ? 1 : r.Next(100) < 70 ? 2 : 3;
                var b = NewBlock(BK.Clause);
                for (int k = 0; k < sentences; k++)
                {
                    int t = r.Chance(70) ? r.Pick(groups[g]) : r.Next(lex.Templates.Length);
                    b.Sentences.Add(null!);
                    pending.Add((b, t));
                }

                items.Add(new Item { Block = b });
                clauses.Add(b);
                refTargets.Add(b);
            }

            articleOf.Add((a, clauses));
            if (a == articles / 2)
            {
                items.Add(BuildTable(r, doc));
            }
        }

        // Now fill the sentences in order, with the full list of possible reference targets.
        var counters = new Dictionary<Block, int>();
        foreach ((Block b, int t) in pending)
        {
            int k = counters.GetValueOrDefault(b);
            b.Sentences[k] = NewSentence(r, doc, t, 0, refTargets);
            counters[b] = k + 1;
        }

        // Make sure the notice template (the one that carries a reference) appears at least three times.
        int refSentences = doc.Blocks().SelectMany(b => b.Sentences).Count(s => s.Pieces.Any(p => p.Kind == PK.Ref));
        var clauseBlocks = doc.Blocks().Where(b => b.Role == BK.Clause).ToList();
        for (int guard = 0; refSentences < 3 && guard < 20; guard++)
        {
            Block b = r.Pick(clauseBlocks);
            b.Sentences.Add(NewSentence(r, doc, 9, 0, refTargets));
            refSentences++;
        }

        foreach (Block t in doc.Blocks().SelectMany(b => b.Sentences).SelectMany(s => s.Pieces).Where(p => p.Kind == PK.Ref).Select(p => int.Parse(p.Canon, CultureInfo.InvariantCulture)).Distinct()
                     .Select(uid => doc.Blocks().First(b => b.Uid == uid)))
        {
            t.Protected = true;
        }

        items.Add(new Item { Block = NewBlock(BK.Closing, FromText(r, doc, lex.Closing)) });
        return doc;
    }

    private Item BuildTable(Rng r, GDoc doc)
    {
        var item = new Item { TableUid = Uid(), Rows = [] };
        var header = new Row { Uid = Uid() };
        foreach (string h in lex.TableHeader)
        {
            header.Cells.Add(NewBlock(BK.Cell, FromText(r, doc, h)));
        }

        item.Rows.Add(header);
        var items = Enumerable.Range(0, lex.TableItems.Length).ToList();
        r.Shuffle(items);
        for (int i = 0; i < r.Next(3, 5); i++)
        {
            item.Rows.Add(NewRow(r, doc, lex.TableItems[items[i]]));
        }

        return item;
    }

    public Row NewRow(Rng r, GDoc doc, string itemName)
    {
        var row = new Row { Uid = Uid() };
        row.Cells.Add(NewBlock(BK.Cell, FromText(r, doc, itemName)));
        row.Cells.Add(NewBlock(BK.Cell, FromText(r, doc, "{D}")));
        row.Cells.Add(NewBlock(BK.Cell, FromText(r, doc, "{A}")));
        return row;
    }

    public Block NewClause(Rng r, GDoc doc, IReadOnlyList<Block> refTargets)
    {
        var b = NewBlock(BK.Clause);
        for (int k = r.Next(1, 2); k > 0; k--)
        {
            b.Sentences.Add(NewSentence(r, doc, r.Next(lex.Templates.Length), 0, refTargets));
        }

        return b;
    }

    public Lexicon Lex => lex;
}
