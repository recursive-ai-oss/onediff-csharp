using System.Text;
using RecursiveAI.OneDocument;
using RecursiveAI.OneDocument.Documents;
using RecursiveAI.OneDocument.Markup;

namespace OneDiff.GoldSets.Docx;

/// <summary>What a revision does to a stretch of text.</summary>
internal enum SegKind { Equal, Delete, Insert, MoveFrom, MoveTo, Gone }

internal sealed record Seg(SegKind Kind, string Text, string? Move, bool Fmt = false);

/// <summary>One paragraph of a tracked-changes file: its text in pieces, and whether its paragraph mark exists with changes rejected and accepted.</summary>
internal sealed class ParaRec
{
    public List<Seg> Segs { get; } = [];

    /// <summary>The mark exists in the document with every revision rejected.</summary>
    public bool OldMark { get; set; } = true;

    /// <summary>The mark exists in the document with every revision accepted.</summary>
    public bool NewMark { get; set; } = true;
}

/// <summary>
/// Reads the output of a comparison tool — a DOCX with tracked changes — through OneDocument's markup and revision model into the
/// scorer's form (<see cref="ToolOutput"/>). Nothing about the tool's own API is used: only the file it wrote.
/// <para>
/// The two views are the document with every revision rejected and with every revision accepted, one line per non-empty paragraph;
/// a paragraph whose mark is removed in a view runs on into the next one (ECMA-376 §17.13.5). A <em>change</em> is a maximal run of
/// revisions inside a paragraph with no unchanged text between them, and a move's two halves (paired by the name on their range
/// markers) are one change. A run whose formatting was changed (<c>w:rPrChange</c>) is a revision of its text: the same text in both
/// views, and it counts as a change with the other revisions around it. Paragraph-level property revisions carry no text and are not changes.
/// </para>
/// </summary>
internal static class TrackedReader
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static List<ParaRec> ReadBody(Document doc)
    {
        var paras = new List<ParaRec>();
        var moveStack = new Stack<string>();
        Walk(doc.MainMarkup.Root, paras, moveStack, null);
        return paras;
    }

    private static bool Is(MarkupNode n, string local) => n.Kind == MarkupNodeKind.Element && n.IsElement(W, local);

    private static void Walk(MarkupNode node, List<ParaRec> paras, Stack<string> moves, SegKind? forced)
    {
        foreach (MarkupNode child in node.ChildElements)
        {
            if (Is(child, "p"))
            {
                paras.Add(ReadParagraph(child, moves, forced));
            }
            else if (Is(child, "tr"))
            {
                SegKind? rowForced = forced;
                foreach (MarkupNode pr in child.ChildElements)
                {
                    if (Is(pr, "trPr"))
                    {
                        foreach (MarkupNode x in pr.ChildElements)
                        {
                            if (Is(x, "ins"))
                            {
                                rowForced = SegKind.Insert;
                            }
                            else if (Is(x, "del"))
                            {
                                rowForced = SegKind.Delete;
                            }
                        }
                    }
                }

                Walk(child, paras, moves, rowForced);
            }
            else if (Is(child, "tc"))
            {
                SegKind? cellForced = forced;
                foreach (MarkupNode pr in child.ChildElements)
                {
                    if (Is(pr, "tcPr"))
                    {
                        foreach (MarkupNode x in pr.ChildElements)
                        {
                            if (Is(x, "cellIns"))
                            {
                                cellForced = SegKind.Insert;
                            }
                            else if (Is(x, "cellDel"))
                            {
                                cellForced = SegKind.Delete;
                            }
                        }
                    }
                }

                Walk(child, paras, moves, cellForced);
            }
            else if (Is(child, "moveFromRangeStart") || Is(child, "moveToRangeStart"))
            {
                moves.Push(child.GetAttribute(W, "name") ?? string.Empty);
            }
            else if (Is(child, "moveFromRangeEnd") || Is(child, "moveToRangeEnd"))
            {
                if (moves.Count > 0)
                {
                    moves.Pop();
                }
            }
            else if (Is(child, "tbl") || Is(child, "sdt") || Is(child, "sdtContent") || Is(child, "body") || Is(child, "document") || Is(child, "customXml") || Is(child, "ins") || Is(child, "del"))
            {
                // Block-level wrappers: a revision wrapper around whole paragraphs or rows applies to everything inside.
                SegKind? f = forced;
                if (Is(child, "ins"))
                {
                    f = SegKind.Insert;
                }
                else if (Is(child, "del"))
                {
                    f = SegKind.Delete;
                }

                Walk(child, paras, moves, f);
            }
        }
    }

    private static ParaRec ReadParagraph(MarkupNode p, Stack<string> moves, SegKind? forced)
    {
        var rec = new ParaRec();
        foreach (MarkupNode c in p.ChildElements)
        {
            if (Is(c, "pPr"))
            {
                foreach (MarkupNode rpr in c.ChildElements)
                {
                    if (!Is(rpr, "rPr"))
                    {
                        continue;
                    }

                    foreach (MarkupNode mark in rpr.ChildElements)
                    {
                        if (Is(mark, "ins") || Is(mark, "moveTo"))
                        {
                            rec.OldMark = false;
                        }
                        else if (Is(mark, "del") || Is(mark, "moveFrom"))
                        {
                            rec.NewMark = false;
                        }
                    }
                }
            }
        }

        if (forced == SegKind.Insert)
        {
            rec.OldMark = false;
        }
        else if (forced == SegKind.Delete)
        {
            rec.NewMark = false;
        }

        ReadInline(p, rec, moves, forced ?? SegKind.Equal, null);
        return rec;
    }

    private static void ReadInline(MarkupNode node, ParaRec rec, Stack<string> moves, SegKind ctx, string? move)
    {
        foreach (MarkupNode c in node.ChildElements)
        {
            if (Is(c, "r"))
            {
                var sb = new StringBuilder();
                foreach (MarkupNode t in c.ChildElements)
                {
                    if (Is(t, "t") || Is(t, "delText"))
                    {
                        sb.Append(t.InnerText);
                    }
                    else if (Is(t, "tab"))
                    {
                        sb.Append('\t');
                    }
                    else if (Is(t, "br") || Is(t, "cr"))
                    {
                        sb.Append('\n');
                    }
                }

                if (sb.Length > 0)
                {
                    bool fmt = ctx == SegKind.Equal && c.ChildElements.Any(x => Is(x, "rPr") && x.ChildElements.Any(y => Is(y, "rPrChange")));
                    Add(rec, ctx, sb.ToString(), move, fmt);
                }
            }
            else if (Is(c, "ins"))
            {
                // Accepting or rejecting everything resolves the outer revision first: an insertion inside moved-away text is
                // removed with it (accept) and restored text never contains it (reject), so it is in neither view.
                ReadInline(c, rec, moves, ctx == SegKind.Delete ? SegKind.Delete : ctx is SegKind.MoveFrom or SegKind.Gone ? SegKind.Gone : SegKind.Insert, move);
            }
            else if (Is(c, "del"))
            {
                // A deletion inside inserted or moved-here text goes with it on reject-all and on accept-all.
                ReadInline(c, rec, moves, ctx is SegKind.Insert or SegKind.MoveTo or SegKind.Gone ? SegKind.Gone : SegKind.Delete, move);
            }
            else if (Is(c, "moveFrom"))
            {
                ReadInline(c, rec, moves, SegKind.MoveFrom, moves.Count > 0 ? moves.Peek() : null);
            }
            else if (Is(c, "moveTo"))
            {
                ReadInline(c, rec, moves, SegKind.MoveTo, moves.Count > 0 ? moves.Peek() : null);
            }
            else if (Is(c, "moveFromRangeStart") || Is(c, "moveToRangeStart"))
            {
                moves.Push(c.GetAttribute(W, "name") ?? string.Empty);
            }
            else if (Is(c, "moveFromRangeEnd") || Is(c, "moveToRangeEnd"))
            {
                if (moves.Count > 0)
                {
                    moves.Pop();
                }
            }
            else if (Is(c, "pPr") || Is(c, "rPr"))
            {
                // properties carry no text
            }
            else
            {
                // hyperlinks, smart tags, inline content controls, simple fields, bookmarks and the like
                ReadInline(c, rec, moves, ctx, move);
            }
        }
    }

    private static void Add(ParaRec rec, SegKind kind, string text, string? move, bool fmt = false)
    {
        if (rec.Segs.Count > 0 && rec.Segs[^1].Kind == kind && rec.Segs[^1].Move == move && rec.Segs[^1].Fmt == fmt)
        {
            rec.Segs[^1] = rec.Segs[^1] with { Text = rec.Segs[^1].Text + text };
        }
        else
        {
            rec.Segs.Add(new Seg(kind, text, move, fmt));
        }
    }

    // ---- views, links and changes ---------------------------------------------------------------------------------------

    /// <summary>Builds the scorer's reading of a tracked-changes file.</summary>
    public static ToolOutput Read(string id, string tool, Document doc)
    {
        List<ParaRec> paras = ReadBody(doc);
        var oldLines = new List<string>();
        var newLines = new List<string>();
        var oldSb = new StringBuilder();
        var newSb = new StringBuilder();

        // Per paragraph: the line each of its views falls on and where its text starts in it.
        var oldAt = new (int Line, int Start)[paras.Count];
        var newAt = new (int Line, int Start)[paras.Count];
        for (int i = 0; i < paras.Count; i++)
        {
            ParaRec p = paras[i];
            oldAt[i] = (oldLines.Count, oldSb.Length);
            newAt[i] = (newLines.Count, newSb.Length);
            foreach (Seg s in p.Segs)
            {
                if (s.Kind is SegKind.Equal or SegKind.Delete or SegKind.MoveFrom)
                {
                    oldSb.Append(s.Text);
                }

                if (s.Kind is SegKind.Equal or SegKind.Insert or SegKind.MoveTo)
                {
                    newSb.Append(s.Text);
                }
            }

            if (p.OldMark)
            {
                Flush(oldSb, oldLines);
            }

            if (p.NewMark)
            {
                Flush(newSb, newLines);
            }
        }

        Flush(oldSb, oldLines);
        Flush(newSb, newLines);

        // Lines that hold only white space are dropped (Flush); the offsets above refer to lines before dropping, so renumber.
        var oldMap = LineMap(paras, forNew: false);
        var newMap = LineMap(paras, forNew: true);

        var changes = new List<ToolChange>();
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x)
        {
            while (parent.TryGetValue(x, out string? px) && px != x)
            {
                x = px;
            }

            return x;
        }

        void Union(string a, string b)
        {
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            parent[Find(a)] = Find(b);
        }

        var moveFrom = new Dictionary<string, List<LineSpan>>(StringComparer.Ordinal);
        var moveTo = new Dictionary<string, List<LineSpan>>(StringComparer.Ordinal);
        int nextId = 0;
        for (int i = 0; i < paras.Count; i++)
        {
            ParaRec p = paras[i];
            int oldPos = oldAt[i].Start, newPos = newAt[i].Start;
            int oldLine = oldMap[i], newLine = newMap[i];
            List<LineSpan>? oldSpans = null, newSpans = null;
            List<LineSpan>? moveOld = null, moveNew = null;
            string? moveName = null;

            // A move is the moved text; insertions, deletions and formatting beside it or inside it are changes of their own.
            void Close()
            {
                if (moveName is not null || moveOld is not null || moveNew is not null)
                {
                    string name = moveName ?? string.Empty;
                    if (moveOld is not null)
                    {
                        AddTo(moveFrom, name, moveOld);
                    }

                    if (moveNew is not null)
                    {
                        AddTo(moveTo, name, moveNew);
                    }
                }

                if (oldSpans is not null || newSpans is not null)
                {
                    changes.Add(new ToolChange(nextId++, "text", oldSpans ?? [], newSpans ?? []));
                }

                oldSpans = null;
                newSpans = null;
                moveOld = null;
                moveNew = null;
                moveName = null;
            }

            foreach (Seg s in p.Segs)
            {
                switch (s.Kind)
                {
                    case SegKind.Gone:
                        break;
                    case SegKind.Equal when s.Fmt:
                        // A formatting revision: the same text in both views, changed.
                        if (oldLine >= 0)
                        {
                            (oldSpans ??= []).Add(new LineSpan(oldLine, oldPos, s.Text.Length));
                        }

                        if (newLine >= 0)
                        {
                            (newSpans ??= []).Add(new LineSpan(newLine, newPos, s.Text.Length));
                        }

                        oldPos += s.Text.Length;
                        newPos += s.Text.Length;
                        break;
                    case SegKind.Equal:
                        if (s.Text.Trim().Length > 0)
                        {
                            Close();
                        }

                        oldPos += s.Text.Length;
                        newPos += s.Text.Length;
                        break;
                    case SegKind.MoveFrom:
                        if (oldLine >= 0)
                        {
                            (moveOld ??= []).Add(new LineSpan(oldLine, oldPos, s.Text.Length));
                        }

                        moveName ??= s.Move;
                        oldPos += s.Text.Length;
                        break;
                    case SegKind.MoveTo:
                        if (newLine >= 0)
                        {
                            (moveNew ??= []).Add(new LineSpan(newLine, newPos, s.Text.Length));
                        }

                        moveName ??= s.Move;
                        newPos += s.Text.Length;
                        break;
                    case SegKind.Delete:
                        if (oldLine >= 0)
                        {
                            (oldSpans ??= []).Add(new LineSpan(oldLine, oldPos, s.Text.Length));
                        }

                        oldPos += s.Text.Length;
                        break;
                    default:
                        if (newLine >= 0)
                        {
                            (newSpans ??= []).Add(new LineSpan(newLine, newPos, s.Text.Length));
                        }

                        newPos += s.Text.Length;
                        break;
                }
            }

            Close();
        }

        // The two halves of a move are one change.
        foreach (string name in moveFrom.Keys.Union(moveTo.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            List<LineSpan> from = moveFrom.GetValueOrDefault(name) ?? [];
            List<LineSpan> to = moveTo.GetValueOrDefault(name) ?? [];
            changes.Add(new ToolChange(nextId++, "move", from, to));
            foreach (LineSpan f in from)
            {
                foreach (LineSpan t in to)
                {
                    Union("o" + f.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), "n" + t.Line.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        // Links: lines joined through paragraphs that hold text in both views, plus the halves of moves.
        for (int i = 0; i < paras.Count; i++)
        {
            if (oldMap[i] >= 0 && newMap[i] >= 0)
            {
                Union("o" + oldMap[i].ToString(System.Globalization.CultureInfo.InvariantCulture), "n" + newMap[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var compOld = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var compNew = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        for (int i = 0; i < paras.Count; i++)
        {
            if (oldMap[i] >= 0)
            {
                string root = Find("o" + oldMap[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!compOld.TryGetValue(root, out SortedSet<int>? set))
                {
                    compOld[root] = set = [];
                }

                set.Add(oldMap[i]);
            }

            if (newMap[i] >= 0)
            {
                string root = Find("n" + newMap[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!compNew.TryGetValue(root, out SortedSet<int>? set))
                {
                    compNew[root] = set = [];
                }

                set.Add(newMap[i]);
            }
        }

        var movedLines = new HashSet<string>(StringComparer.Ordinal);
        foreach (List<LineSpan> from in moveFrom.Values)
        {
            foreach (LineSpan f in from)
            {
                movedLines.Add(Find("o" + f.Line.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        var links = new List<ToolLink>();
        foreach (string root in compOld.Keys.Where(compNew.ContainsKey).OrderBy(r => compOld[r].Min()).ThenBy(r => r, StringComparer.Ordinal))
        {
            SortedSet<int> o = compOld[root];
            SortedSet<int> n = compNew[root];
            if (o.Count > 1 && n.Count > 1)
            {
                continue;
            }

            string kind = o.Count == 1 && n.Count == 1 ? (movedLines.Contains(root) ? "move" : "match") : o.Count == 1 ? "split" : "merge";
            links.Add(new ToolLink(kind, [.. o], [.. n]));
        }

        return new ToolOutput(ToolOutput.SchemaName, id, tool, oldLines, newLines, links, changes);
    }

    private static void AddTo(Dictionary<string, List<LineSpan>> into, string name, List<LineSpan> spans)
    {
        if (into.TryGetValue(name, out List<LineSpan>? existing))
        {
            existing.AddRange(spans);
        }
        else
        {
            into[name] = [.. spans];
        }
    }

    private static void Flush(StringBuilder sb, List<string> lines)
    {
        if (sb.Length > 0)
        {
            string s = sb.ToString();
            sb.Clear();
            if (s.Trim().Length > 0)
            {
                lines.Add(s);
            }
        }
    }

    /// <summary>For each paragraph, the index of the (non-empty) line of the chosen view that holds its text, or -1 when it holds none.</summary>
    private static int[] LineMap(List<ParaRec> paras, bool forNew)
    {
        var map = new int[paras.Count];
        int line = 0;
        bool open = false;
        for (int i = 0; i < paras.Count; i++)
        {
            ParaRec p = paras[i];
            bool hasText = p.Segs.Any(s => forNew ? s.Kind is SegKind.Equal or SegKind.Insert or SegKind.MoveTo : s.Kind is SegKind.Equal or SegKind.Delete or SegKind.MoveFrom);
            bool text = p.Segs.Any(s => (forNew ? s.Kind is SegKind.Equal or SegKind.Insert or SegKind.MoveTo : s.Kind is SegKind.Equal or SegKind.Delete or SegKind.MoveFrom) && s.Text.Trim().Length > 0);
            map[i] = hasText && text ? line : -1;
            open |= hasText && text;
            if (forNew ? p.NewMark : p.OldMark)
            {
                if (open)
                {
                    line++;
                }

                open = false;
            }
        }

        return map;
    }
}
