using System.Globalization;
using OneDiff.Documents.Model;

namespace OneDiff.GoldSets;

/// <summary>The kinds of edit the generator applies. Each produces labelled change units.</summary>
internal static class EditTypes
{
    public static readonly string[] All =
    [
        "term-swap", "value-amount", "value-date", "value-duration", "value-percentage", "clause-rewrite", "sentence-insert",
        "sentence-delete", "para-insert", "para-delete", "move", "move-edit", "cascade", "term-rename", "split", "merge", "format",
        "table-cell", "table-row-insert", "table-row-delete",
    ];

    /// <summary>The edits mixed pairs draw from, most common first by repetition.</summary>
    public static readonly string[] MixedPool =
    [
        "term-swap", "value-amount", "value-amount", "value-date", "value-duration", "value-duration", "value-percentage",
        "clause-rewrite", "clause-rewrite", "sentence-insert", "sentence-delete", "para-insert", "para-delete", "move", "move-edit",
        "cascade", "split", "merge", "format", "format", "table-cell", "table-row-insert", "table-row-delete",
    ];
}

/// <summary>What a node looks like in a flattened document.</summary>
internal sealed class FlatBlock
{
    public required Block Block;
    public int Node;
    public string Text = "";
    public string Label = "";
    public List<(Piece Piece, int Start, int Len)> Pieces = [];
    public List<(Sentence Sentence, int Start, int Len)> Sentences = [];
}

internal sealed class Flat
{
    public required DocFile File;
    public List<FlatBlock> Blocks = [];
    public Dictionary<int, FlatBlock> ByUid = [];
}

internal sealed record UnitDraft(int Id, string Type, string Group, Typed? Typed);

/// <summary>Makes one old/new pair from a base document by applying labelled edits, and works out the labels.</summary>
internal sealed class PairMaker
{
    private readonly Lexicon lex;
    private readonly DocBuilder db;
    private readonly Rng r;
    private readonly GDoc oldDoc;
    private readonly GDoc newDoc;
    private readonly Dictionary<int, Piece> oldPieces = [];
    private readonly Dictionary<int, Sentence> oldSentences = [];
    private readonly Dictionary<int, Block> oldBlocks = [];
    private readonly HashSet<int> touched = [];
    private readonly List<UnitDraft> units = [];
    private readonly List<(int Old, List<int> New)> splits = [];
    private readonly List<(List<int> Old, int New)> merges = [];
    private readonly List<string> applied = [];

    public PairMaker(Lexicon lex, DocBuilder db, GDoc baseDoc, Rng r)
    {
        this.lex = lex;
        this.db = db;
        this.r = r;
        oldDoc = baseDoc.Clone();
        newDoc = baseDoc.Clone();
        foreach (Block b in oldDoc.Blocks())
        {
            oldBlocks[b.Uid] = b;
            foreach (Sentence s in b.Sentences)
            {
                oldSentences[s.Uid] = s;
                foreach (Piece p in s.Pieces)
                {
                    oldPieces[p.Uid] = p;
                }
            }
        }
    }

    public IReadOnlyList<string> Applied => applied;

    private int NewUnit(string type, string group = "", Typed? typed = null)
    {
        units.Add(new UnitDraft(units.Count, type, group, typed));
        return units.Count - 1;
    }

    private IEnumerable<Block> Fresh(Func<Block, bool> where) => newDoc.Blocks().Where(b => !touched.Contains(b.Uid) && where(b));

    private List<Block> RefTargets() => [.. newDoc.Blocks().Where(b => b.Role == BK.Clause)];

    private static bool IsProse(Block b) => b.Role is BK.Clause or BK.Definition or BK.Recital or BK.Preamble;

    private void MarkOld(Piece p, int unit)
    {
        if (oldPieces.TryGetValue(p.Uid, out Piece? o))
        {
            o.Unit = unit;
        }
    }

    private void Protect(Sentence s)
    {
        foreach (Piece p in s.Pieces.Where(p => p.Kind == PK.Ref))
        {
            int uid = int.Parse(p.Canon, CultureInfo.InvariantCulture);
            foreach (Block t in newDoc.Blocks().Where(b => b.Uid == uid))
            {
                t.Protected = true;
            }
        }
    }

    /// <summary>Applies one edit of the named type. Returns false when the document has no place for it.</summary>
    public bool Apply(string type)
    {
        bool ok = type switch
        {
            "term-swap" => TermSwap(null),
            "value-amount" => ValueEdit(PK.Amount, "value-amount", false, null),
            "value-date" => ValueEdit(PK.Date, "value-date", false, null),
            "value-duration" => ValueEdit(PK.Duration, "value-duration", false, null),
            "value-percentage" => ValueEdit(PK.Percent, "value-percentage", false, null),
            "clause-rewrite" => Rewrite(),
            "sentence-insert" => SentenceInsert(),
            "sentence-delete" => SentenceDelete(),
            "para-insert" => ParaInsert(false),
            "para-delete" => ParaDelete(),
            "move" => Move(false),
            "move-edit" => Move(true),
            "cascade" => ParaInsert(true),
            "term-rename" => Rename(),
            "split" => Split(),
            "merge" => Merge(),
            "format" => Format(),
            "table-cell" => ValueEdit(r.Chance(50) ? PK.Amount : PK.Duration, "table-cell", true, null),
            "table-row-insert" => RowInsert(),
            "table-row-delete" => RowDelete(),
            _ => throw new ArgumentException("unknown edit " + type),
        };
        if (ok && !applied.Contains(type))
        {
            applied.Add(type);
        }

        return ok;
    }

    private bool TermSwap(Block? only)
    {
        var cands = new List<(Block B, Piece P)>();
        foreach (Block b in only is null ? Fresh(IsProse) : [only])
        {
            foreach (Piece p in b.Sentences.SelectMany(s => s.Pieces).Where(p => p.Unit < 0 && p.Kind is PK.Modal or PK.Adverb or PK.Jurisdiction))
            {
                cands.Add((b, p));
            }
        }

        if (cands.Count == 0)
        {
            return false;
        }

        (Block blk, Piece piece) = r.Pick(cands);
        string[] pool = piece.Kind switch { PK.Modal => lex.Modal, PK.Adverb => lex.Adverb, _ => lex.Jurisdictions };
        string[] others = [.. pool.Where(x => x != piece.Text)];
        string old = piece.Text;
        piece.Text = r.Pick(others);
        int u = NewUnit("term-swap", "", new Typed(piece.Kind.ToString().ToLowerInvariant(), old, piece.Text));
        piece.Unit = u;
        MarkOld(piece, u);
        touched.Add(blk.Uid);
        return true;
    }

    private bool ValueEdit(PK kind, string type, bool table, Block? only)
    {
        var cands = new List<(Block B, Piece P)>();
        IEnumerable<Block> pool = only is not null ? [only] : Fresh(b => table ? b.Role == BK.Cell : IsProse(b));
        foreach (Block b in pool)
        {
            foreach (Piece p in b.Sentences.SelectMany(s => s.Pieces).Where(p => p.Unit < 0 && p.Kind == kind))
            {
                cands.Add((b, p));
            }
        }

        if (cands.Count == 0)
        {
            return false;
        }

        (Block blk, Piece piece) = r.Pick(cands);
        SlotVal v = kind switch
        {
            PK.Amount => db.NewAmount(r, piece.Canon),
            PK.Date => db.NewDate(r, piece.Canon),
            PK.Duration => db.NewDuration(r, piece.Canon),
            _ => db.NewPercent(r, piece.Canon),
        };
        string typedKind = kind switch { PK.Amount => "amount", PK.Date => "date", PK.Duration => "duration", _ => "percentage" };
        var typed = new Typed(typedKind, piece.Canon, v.Canon);
        piece.Text = v.Text;
        piece.Canon = v.Canon;
        int u = NewUnit(type, "", typed);
        piece.Unit = u;
        MarkOld(piece, u);
        touched.Add(blk.Uid);
        return true;
    }

    private bool Rewrite()
    {
        var cands = Fresh(b => b.Role == BK.Clause).SelectMany(b => b.Sentences.Select(s => (B: b, S: s))).Where(x => x.S.Template >= 0).ToList();
        if (cands.Count == 0)
        {
            return false;
        }

        (Block blk, Sentence s) = r.Pick(cands);
        var fresh = new Sentence { Uid = db.Uid(), Template = s.Template, Variant = 1 - s.Variant, Slots = new Dictionary<char, SlotVal>(s.Slots) };
        if (fresh.Slots.ContainsKey('X'))
        {
            fresh.Slots['X'] = new SlotVal { Kind = PK.Term, Text = newDoc.Term };
        }

        db.Render(fresh);
        int u = NewUnit("clause-rewrite");
        foreach (Piece p in s.Pieces)
        {
            MarkOld(p, u);
        }

        foreach (Piece p in fresh.Pieces)
        {
            p.Unit = u;
        }

        blk.Sentences[blk.Sentences.IndexOf(s)] = fresh;
        Protect(fresh);
        touched.Add(blk.Uid);
        return true;
    }

    private bool SentenceInsert()
    {
        var cands = Fresh(b => b.Role == BK.Clause).ToList();
        if (cands.Count == 0)
        {
            return false;
        }

        Block b = r.Pick(cands);
        Sentence s = db.NewSentence(r, newDoc, r.Next(lex.Templates.Length), 0, RefTargets());
        int u = NewUnit("sentence-insert");
        foreach (Piece p in s.Pieces)
        {
            p.Unit = u;
        }

        b.Sentences.Insert(r.Next(b.Sentences.Count + 1), s);
        Protect(s);
        touched.Add(b.Uid);
        return true;
    }

    private bool SentenceDelete()
    {
        var cands = Fresh(b => b.Role == BK.Clause && b.Sentences.Count >= 2).ToList();
        if (cands.Count == 0)
        {
            return false;
        }

        Block b = r.Pick(cands);
        int i = r.Next(b.Sentences.Count);
        int u = NewUnit("sentence-delete");
        foreach (Piece p in b.Sentences[i].Pieces)
        {
            MarkOld(p, u);
        }

        b.Sentences.RemoveAt(i);
        touched.Add(b.Uid);
        return true;
    }

    private int ItemIndexOf(Block b) => newDoc.Items.FindIndex(it => it.Block == b);

    private bool ParaInsert(bool cascade)
    {
        int at;
        if (cascade)
        {
            // Insert directly before a clause that other text refers to, so it and everything after it is renumbered.
            var targets = newDoc.Blocks().Where(b => b.Role == BK.Clause && b.Protected).ToList();
            if (targets.Count == 0)
            {
                return false;
            }

            at = ItemIndexOf(r.Pick(targets));
        }
        else
        {
            var anchors = Enumerable.Range(0, newDoc.Items.Count).Where(i => newDoc.Items[i].Block is { Role: BK.Clause or BK.Heading or BK.Definition }).ToList();
            at = r.Pick(anchors) + 1;
        }

        Block nb = db.NewClause(r, newDoc, RefTargets());
        nb.WholeUnit = NewUnit(cascade ? "cascade" : "para-insert");
        foreach (Sentence s in nb.Sentences)
        {
            Protect(s);
        }

        newDoc.Items.Insert(at, new Item { Block = nb });
        return true;
    }

    private bool ParaDelete()
    {
        var cands = Fresh(b => b.Role == BK.Clause && !b.Protected && !b.Moved).ToList();
        if (cands.Count == 0)
        {
            return false;
        }

        Block b = r.Pick(cands);
        oldBlocks[b.Uid].WholeUnit = NewUnit("para-delete");
        newDoc.Items.RemoveAt(ItemIndexOf(b));
        touched.Add(b.Uid);
        return true;
    }

    private bool Move(bool withEdit)
    {
        var cands = Fresh(b => b.Role == BK.Clause && !b.Moved).ToList();
        if (withEdit)
        {
            cands = [.. cands.Where(b => b.Sentences.SelectMany(s => s.Pieces).Any(p => p.Kind is PK.Amount or PK.Duration or PK.Percent or PK.Date or PK.Modal))];
        }

        if (cands.Count == 0)
        {
            return false;
        }

        Block b = r.Pick(cands);
        int from = ItemIndexOf(b);
        var anchors = Enumerable.Range(0, newDoc.Items.Count)
            .Where(i => newDoc.Items[i].Block is { Role: BK.Clause or BK.Heading } && Math.Abs(i - from) >= 4 && newDoc.Items[i].Block != b)
            .ToList();
        if (anchors.Count == 0)
        {
            return false;
        }

        int to = r.Pick(anchors);
        Item item = newDoc.Items[from];
        newDoc.Items.RemoveAt(from);
        if (to > from)
        {
            to--;
        }

        newDoc.Items.Insert(to + 1, item);
        int u = NewUnit("move");
        b.WholeUnit = u;
        oldBlocks[b.Uid].WholeUnit = u;
        b.Moved = true;
        touched.Add(b.Uid);
        if (withEdit)
        {
            Piece p = r.Pick([.. b.Sentences.SelectMany(s => s.Pieces).Where(p => p.Kind is PK.Amount or PK.Duration or PK.Percent or PK.Date or PK.Modal)]);
            if (p.Kind == PK.Modal)
            {
                TermSwap(b);
            }
            else
            {
                ValueEdit(p.Kind, p.Kind switch { PK.Amount => "value-amount", PK.Date => "value-date", PK.Duration => "value-duration", _ => "value-percentage" }, false, b);
            }
        }

        return true;
    }

    private bool Rename()
    {
        var occurrences = newDoc.Blocks().SelectMany(b => b.Sentences.SelectMany(s => s.Pieces.Where(p => p.Kind == PK.Term).Select(p => (B: b, P: p)))).ToList();
        if (occurrences.Count < 2)
        {
            return false;
        }

        string old = newDoc.Term;
        string next = r.Pick([.. lex.Terms.Where(t => t != old)]);
        newDoc.Term = next;
        foreach ((Block b, Piece p) in occurrences)
        {
            int u = NewUnit("term-rename", "rename", new Typed("term", old, next));
            p.Text = next;
            p.Unit = u;
            MarkOld(p, u);
            touched.Add(b.Uid);
        }

        return true;
    }

    private bool Split()
    {
        var cands = Fresh(b => b.Role == BK.Recital && b.Sentences.Count >= 2).ToList();
        if (cands.Count == 0)
        {
            return false;
        }

        Block b = r.Pick(cands);
        int k = r.Next(1, b.Sentences.Count - 1);
        var second = new Block { Uid = db.Uid(), Role = BK.Recital, Sentences = b.Sentences.GetRange(k, b.Sentences.Count - k) };
        b.Sentences.RemoveRange(k, b.Sentences.Count - k);
        newDoc.Items.Insert(ItemIndexOf(b) + 1, new Item { Block = second });
        NewUnit("split");
        splits.Add((b.Uid, [b.Uid, second.Uid]));
        touched.Add(b.Uid);
        touched.Add(second.Uid);
        return true;
    }

    private bool Merge()
    {
        var cands = new List<int>();
        for (int i = 0; i + 1 < newDoc.Items.Count; i++)
        {
            Block? a = newDoc.Items[i].Block;
            Block? c = newDoc.Items[i + 1].Block;
            if (a is { Role: BK.Recital } && c is { Role: BK.Recital } && !touched.Contains(a.Uid) && !touched.Contains(c.Uid) && oldBlocks.ContainsKey(c.Uid))
            {
                cands.Add(i);
            }
        }

        if (cands.Count == 0)
        {
            return false;
        }

        int at = r.Pick(cands);
        Block first = newDoc.Items[at].Block!;
        Block next = newDoc.Items[at + 1].Block!;
        first.Sentences.AddRange(next.Sentences);
        newDoc.Items.RemoveAt(at + 1);
        NewUnit("merge");
        merges.Add(([first.Uid, next.Uid], first.Uid));
        touched.Add(first.Uid);
        touched.Add(next.Uid);
        return true;
    }

    private bool Format()
    {
        var cands = new List<(Block B, Sentence S, int Index, int Start, int Len)>();
        foreach (Block b in Fresh(x => x.Role is BK.Clause or BK.Recital))
        {
            foreach (Sentence s in b.Sentences.Where(s => oldSentences.ContainsKey(s.Uid)))
            {
                for (int i = 0; i < s.Pieces.Count; i++)
                {
                    Piece p = s.Pieces[i];
                    if (p.Kind != PK.Plain || p.Unit >= 0 || p.Bold)
                    {
                        continue;
                    }

                    if (lex.Spaced)
                    {
                        for (int a = 0; a < p.Text.Length;)
                        {
                            if (!char.IsLetter(p.Text[a]))
                            {
                                a++;
                                continue;
                            }

                            int e = a;
                            while (e < p.Text.Length && char.IsLetter(p.Text[e]))
                            {
                                e++;
                            }

                            if (e - a >= 4)
                            {
                                cands.Add((b, s, i, a, e - a));
                            }

                            a = e;
                        }
                    }
                    else
                    {
                        for (int a = 0; a + 3 <= p.Text.Length; a += 4)
                        {
                            if (p.Text.AsSpan(a, 3).ToString().All(char.IsLetter))
                            {
                                cands.Add((b, s, i, a, 3));
                            }
                        }
                    }
                }
            }
        }

        if (cands.Count == 0)
        {
            return false;
        }

        (Block blk, Sentence ns, int idx, int start, int len) = r.Pick(cands);
        Sentence os = oldSentences[ns.Uid];
        int idxOld = os.Pieces.FindIndex(p => p.Uid == ns.Pieces[idx].Uid);
        if (idxOld < 0)
        {
            return false;
        }

        int u = NewUnit("format");
        int[] uids = [db.Uid(), db.Uid(), db.Uid()];
        SplitPiece(ns, idx, start, len, uids, u, true);
        SplitPiece(os, idxOld, start, len, uids, u, false);
        foreach (Piece p in os.Pieces)
        {
            oldPieces[p.Uid] = p;
        }

        touched.Add(blk.Uid);
        return true;
    }

    private static void SplitPiece(Sentence s, int idx, int start, int len, int[] uids, int unit, bool bold)
    {
        Piece p = s.Pieces[idx];
        var parts = new List<Piece>();
        if (start > 0)
        {
            parts.Add(new Piece { Uid = uids[0], Kind = PK.Plain, Text = p.Text[..start] });
        }

        parts.Add(new Piece { Uid = uids[1], Kind = PK.Plain, Text = p.Text.Substring(start, len), Bold = bold, Unit = unit });
        if (start + len < p.Text.Length)
        {
            parts.Add(new Piece { Uid = uids[2], Kind = PK.Plain, Text = p.Text[(start + len)..] });
        }

        s.Pieces.RemoveAt(idx);
        s.Pieces.InsertRange(idx, parts);
    }

    private bool RowInsert()
    {
        Item? t = newDoc.Items.FirstOrDefault(i => i.Rows is not null);
        if (t?.Rows is null)
        {
            return false;
        }

        Row row = db.NewRow(r, newDoc, r.Pick(lex.TableItems));
        int u = NewUnit("table-row-insert");
        foreach (Block c in row.Cells)
        {
            c.WholeUnit = u;
        }

        t.Rows.Insert(r.Next(1, t.Rows.Count), row);
        return true;
    }

    private bool RowDelete()
    {
        Item? t = newDoc.Items.FirstOrDefault(i => i.Rows is not null);
        if (t?.Rows is null || t.Rows.Count < 3)
        {
            return false;
        }

        int i = r.Next(1, t.Rows.Count - 1);
        Row row = t.Rows[i];
        int u = NewUnit("table-row-delete");
        foreach (Block c in row.Cells)
        {
            oldBlocks[c.Uid].WholeUnit = u;
        }

        t.Rows.RemoveAt(i);
        return true;
    }

    // ---- flattening and labels -------------------------------------------------------------------------------------

    /// <summary>Turns a document into compare-model nodes (stored form) and remembers where each block, sentence and piece went.</summary>
    public Flat Flatten(GDoc doc, string name)
    {
        var labels = new Dictionary<int, string>();
        int h = 0;
        int c = 0;
        foreach (Item it in doc.Items)
        {
            if (it.Block is null)
            {
                continue;
            }

            switch (it.Block.Role)
            {
                case BK.Heading:
                    h++;
                    c = 0;
                    labels[it.Block.Uid] = h.ToString(CultureInfo.InvariantCulture) + ".";
                    break;
                case BK.Clause or BK.Definition:
                    c++;
                    labels[it.Block.Uid] = $"{h}.{c}";
                    break;
                default:
                    break;
            }
        }

        var nodes = new List<DocNode> { new("Section", -1, "", [], []) };
        var flat = new Flat { File = new DocFile(name, nodes) };

        int AddBlock(Block b, int parent)
        {
            var fb = new FlatBlock { Block = b, Label = labels.GetValueOrDefault(b.Uid, "") };
            var text = new System.Text.StringBuilder();
            foreach (Sentence s in b.Sentences)
            {
                if (text.Length > 0 && lex.Spaced)
                {
                    text.Append(' ');
                }

                int sStart = text.Length;
                foreach (Piece p in s.Pieces)
                {
                    if (p.Kind == PK.Ref)
                    {
                        p.Text = lex.Ref(labels.GetValueOrDefault(int.Parse(p.Canon, CultureInfo.InvariantCulture), "?"));
                    }

                    fb.Pieces.Add((p, text.Length, p.Text.Length));
                    text.Append(p.Text);
                }

                fb.Sentences.Add((s, sStart, text.Length - sStart));
            }

            fb.Text = text.ToString();
            var attrs = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var runs = new List<DocRun>();
            bool wholeBold = b.Role is BK.Title or BK.Heading;
            if (wholeBold && fb.Text.Length > 0)
            {
                runs.Add(new DocRun(0, fb.Text.Length, new SortedDictionary<string, string>(StringComparer.Ordinal) { [FormatKeys.Bold] = "1" }));
            }
            else
            {
                foreach ((Piece p, int st, int ln) in fb.Pieces.Where(x => x.Piece.Bold && x.Len > 0))
                {
                    runs.Add(new DocRun(st, ln, new SortedDictionary<string, string>(StringComparer.Ordinal) { [FormatKeys.Bold] = "1" }));
                }
            }

            string kind = "Paragraph";
            switch (b.Role)
            {
                case BK.Title:
                    attrs[FormatKeys.ParagraphStyle] = "Title";
                    break;
                case BK.Heading:
                    kind = "ListItem";
                    attrs[FormatKeys.ParagraphStyle] = "Heading1";
                    attrs[FormatKeys.ListLabel] = fb.Label;
                    attrs[FormatKeys.ListLevel] = "1";
                    break;
                case BK.Clause or BK.Definition:
                    kind = "ListItem";
                    attrs[FormatKeys.ListLabel] = fb.Label;
                    attrs[FormatKeys.ListLevel] = "2";
                    break;
                default:
                    break;
            }

            fb.Node = nodes.Count;
            nodes.Add(new DocNode(kind, parent, fb.Text, attrs, runs));
            flat.Blocks.Add(fb);
            flat.ByUid[b.Uid] = fb;
            return fb.Node;
        }

        foreach (Item it in doc.Items)
        {
            if (it.Block is not null)
            {
                AddBlock(it.Block, 0);
            }
            else
            {
                int t = nodes.Count;
                nodes.Add(new DocNode("Table", 0, "", [], []));
                foreach (Row row in it.Rows!)
                {
                    int rn = nodes.Count;
                    nodes.Add(new DocNode("TableRow", t, "", [], []));
                    foreach (Block cell in row.Cells)
                    {
                        int cn = nodes.Count;
                        nodes.Add(new DocNode("TableCell", rn, "", [], []));
                        AddBlock(cell, cn);
                    }
                }
            }
        }

        return flat;
    }

    /// <summary>Flattens both documents, checks that every difference is labelled, and returns the stored pair and its labels.</summary>
    public (PairDocs Docs, PairLabels Labels) Finish(string id, string lang, int baseDoc, string baseHash, string role)
    {
        Flat o = Flatten(oldDoc, id + ".old");
        Flat n = Flatten(newDoc, id + ".new");
        var allUnits = units.Select(u => new Unit(u.Id, u.Type, u.Group, u.Typed, [], [])).ToList();

        void Collect(Flat f, bool isNew)
        {
            foreach (FlatBlock fb in f.Blocks)
            {
                foreach (Unit u in allUnits)
                {
                    List<Span> target = isNew ? u.New : u.Old;
                    if (fb.Block.WholeUnit == u.Id)
                    {
                        if (fb.Text.Length > 0)
                        {
                            target.Add(new Span(fb.Node, 0, fb.Text.Length));
                        }

                        continue;
                    }

                    Span? open = null;
                    foreach ((Piece p, int st, int ln) in fb.Pieces)
                    {
                        if (p.Unit == u.Id && ln > 0)
                        {
                            open = open is null ? new Span(fb.Node, st, ln) : open.Start + open.Len == st ? open with { Len = open.Len + ln } : Flush(target, open, new Span(fb.Node, st, ln));
                        }
                        else if (open is not null && p.Unit != u.Id)
                        {
                            target.Add(open);
                            open = null;
                        }
                    }

                    if (open is not null)
                    {
                        target.Add(open);
                    }
                }
            }
        }

        static Span Flush(List<Span> target, Span open, Span next)
        {
            target.Add(open);
            return next;
        }

        Collect(o, false);
        Collect(n, true);

        // Differences caused by renumbering: a reference whose text moved with its target, and a label that changed.
        int nextUnit = allUnits.Count;
        foreach (FlatBlock nb in n.Blocks)
        {
            if (!o.ByUid.TryGetValue(nb.Block.Uid, out FlatBlock? ob))
            {
                continue;
            }

            if (ob.Label != nb.Label)
            {
                allUnits.Add(new Unit(nextUnit++, "renumber", "cascade", new Typed("number", ob.Label, nb.Label), [], []));
            }

            foreach ((Piece np, int st, int ln) in nb.Pieces.Where(x => x.Piece.Kind == PK.Ref && x.Piece.Unit < 0))
            {
                (Piece op, int ost, int oln) = ob.Pieces.FirstOrDefault(x => x.Piece.Uid == np.Uid);
                if (op is not null && op.Unit < 0 && op.Text != np.Text)
                {
                    allUnits.Add(new Unit(nextUnit++, "cascade-reference", "cascade", new Typed("reference", op.Text, np.Text), [new Span(ob.Node, ost, oln)], [new Span(nb.Node, st, ln)]));
                }
            }
        }

        CheckComplete(o, n, [.. splits.Select(x => x.Old), .. merges.Select(x => x.New)]);

        // Links.
        var links = new List<GoldLink>();
        var deleted = new List<int>();
        var inserted = new List<int>();
        var consumedOld = new HashSet<int>();
        var consumedNew = new HashSet<int>();
        foreach ((int oldUid, List<int> news) in splits)
        {
            links.Add(new GoldLink("split", [o.ByUid[oldUid].Node], [.. news.Select(x => n.ByUid[x].Node).OrderBy(x => x)]));
            consumedOld.Add(oldUid);
            news.ForEach(x => consumedNew.Add(x));
        }

        foreach ((List<int> olds, int newUid) in merges)
        {
            links.Add(new GoldLink("merge", [.. olds.Select(x => o.ByUid[x].Node).OrderBy(x => x)], [n.ByUid[newUid].Node]));
            olds.ForEach(x => consumedOld.Add(x));
            consumedNew.Add(newUid);
        }

        foreach (FlatBlock ob in o.Blocks.Where(b => !consumedOld.Contains(b.Block.Uid)))
        {
            if (n.ByUid.TryGetValue(ob.Block.Uid, out FlatBlock? nb) && !consumedNew.Contains(ob.Block.Uid))
            {
                links.Add(new GoldLink(nb.Block.Moved ? "move" : "match", [ob.Node], [nb.Node]));
            }
            else
            {
                deleted.Add(ob.Node);
            }
        }

        foreach (FlatBlock nb in n.Blocks.Where(b => !consumedNew.Contains(b.Block.Uid) && !o.ByUid.ContainsKey(b.Block.Uid)))
        {
            inserted.Add(nb.Node);
        }

        // Changed sentences: on the new side, and on the old side for text that was only deleted.
        var kindOf = allUnits.ToDictionary(u => u.Id, u => u.Type);
        bool Textual(int unit) => unit >= 0 && kindOf[unit] is not ("move" or "split" or "merge");
        var sentences = new List<GoldSentence>();
        foreach (FlatBlock nb in n.Blocks)
        {
            foreach ((Sentence s, int st, int ln) in nb.Sentences)
            {
                var us = new SortedSet<int>(nb.Pieces.Where(x => s.Pieces.Contains(x.Piece) && Textual(x.Piece.Unit)).Select(x => x.Piece.Unit));
                if (Textual(nb.Block.WholeUnit))
                {
                    us.Add(nb.Block.WholeUnit);
                }

                if (us.Count > 0 && ln > 0)
                {
                    sentences.Add(new GoldSentence("new", nb.Node, st, ln, [.. us]));
                }
            }
        }

        foreach (FlatBlock ob in o.Blocks)
        {
            foreach ((Sentence s, int st, int ln) in ob.Sentences)
            {
                var us = new SortedSet<int>(ob.Pieces.Where(x => s.Pieces.Contains(x.Piece) && Textual(x.Piece.Unit) && allUnits[x.Piece.Unit].New.Count == 0).Select(x => x.Piece.Unit));
                if (Textual(ob.Block.WholeUnit) && allUnits[ob.Block.WholeUnit].New.Count == 0)
                {
                    us.Add(ob.Block.WholeUnit);
                }

                if (us.Count > 0 && ln > 0)
                {
                    sentences.Add(new GoldSentence("old", ob.Node, st, ln, [.. us]));
                }
            }
        }

        var docs = new PairDocs(id, lang, o.File, n.File);
        var labels = new PairLabels(id, lang, baseDoc, baseHash, role, applied, links, deleted, inserted, allUnits, sentences);
        return (docs, labels);
    }

    /// <summary>The generator's own sanity gate: nothing differs between the two documents without a unit that covers it.</summary>
    private static void CheckComplete(Flat o, Flat n, HashSet<int> skip)
    {
        foreach (FlatBlock nb in n.Blocks)
        {
            if (skip.Contains(nb.Block.Uid) || nb.Block.WholeUnit >= 0 || !o.ByUid.TryGetValue(nb.Block.Uid, out FlatBlock? ob) || ob.Block.WholeUnit >= 0)
            {
                continue;
            }

            var oldPieces = ob.Pieces.ToDictionary(x => x.Piece.Uid, x => x.Piece);
            foreach ((Piece p, _, _) in nb.Pieces)
            {
                bool had = oldPieces.TryGetValue(p.Uid, out Piece? q);
                bool changed = !had || q!.Text != p.Text || q.Bold != p.Bold;
                bool refCascade = had && p.Kind == PK.Ref;
                if (changed && p.Unit < 0 && !refCascade)
                {
                    throw new InvalidOperationException($"unlabelled change in block {nb.Block.Uid}: '{q?.Text}' -> '{p.Text}'");
                }
            }

            var newUids = nb.Pieces.Select(x => x.Piece.Uid).ToHashSet();
            foreach ((Piece p, _, _) in ob.Pieces)
            {
                if (!newUids.Contains(p.Uid) && p.Unit < 0)
                {
                    throw new InvalidOperationException($"unlabelled removal in block {ob.Block.Uid}: '{p.Text}'");
                }
            }
        }
    }
}
