using System.Collections.Immutable;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Html;

/// <summary>
/// The side-by-side layout: one table row per block, old on the left and new on the right. Unchanged blocks sit on both sides,
/// deleted blocks only on the left, inserted blocks only on the right, and a moved block shows twice, at its old place on the
/// left and at its new place on the right. Splits and merges put all their blocks in the one row.
/// </summary>
internal sealed class SideBySideWriter : IDisposable
{
    private readonly HtmlOut o;
    private readonly RedlineView v;
    private readonly RedlineStyle style;
    private readonly CancellationToken ct;
    private readonly StringWriter buffer = new();
    private readonly HtmlOut cell;
    private readonly PieceWriter pw;

    public SideBySideWriter(HtmlOut o, RedlineView v, RedlineStyle style, CancellationToken ct)
    {
        this.o = o;
        this.v = v;
        this.style = style;
        this.ct = ct;
        cell = new HtmlOut(buffer);
        pw = new PieceWriter(cell, style, v);
    }

    public void Dispose() => buffer.Dispose();

    public void Write()
    {
        Provenance p = v.Comparison.Provenance;
        o.Raw("<main aria-label=\"Redline\">\n<table class=\"sbs\">\n<caption class=\"sr\">Old and new text side by side</caption>\n<thead><tr><th scope=\"col\">Old");
        Heading(p.OldName);
        o.Raw("</th><th scope=\"col\">New");
        Heading(p.NewName);
        o.Raw("</th></tr></thead>\n<tbody>\n");
        foreach (MNode root in v.Roots)
        {
            Walk(root, null, 0);
        }

        o.Raw("</tbody>\n</table>\n</main>\n");
    }

    private void Heading(string name)
    {
        if (name.Length > 0)
        {
            o.Raw(": ");
            o.Text(name);
        }
    }

    private void Walk(MNode m, NodeKind? parentKind, int depth)
    {
        ct.ThrowIfCancellationRequested();
        NodeKind kind = m.Ghost ? v.Old[m.Id].Kind : v.New[m.Id].Kind;
        if (m.Ghost)
        {
            GhostRow(m, depth);
        }
        else
        {
            LiveRow(m, depth);
        }

        foreach (MNode child in m.Children)
        {
            Walk(child, kind, depth + 1);
        }
    }

    private void LiveRow(MNode m, int depth)
    {
        int n = m.Id;
        AlignmentLink? link = v.Alignment.LinkOfNew(n);
        LinkView? lv = null;
        if (link is not null)
        {
            v.Links.TryGetValue(link.Id, out lv);
        }

        string left = "", right;
        if (lv is null)
        {
            right = Cell(() => NewBlock(n, link, depth, null, v.InsertOfNew[n], -1));
        }
        else if (lv.MoveChange >= 0)
        {
            right = Cell(() => NewBlock(n, link, depth, lv.NewSide()[0], null, lv.MoveChange));
        }
        else if (link!.Kind is LinkKind.Split or LinkKind.Merge)
        {
            if (link.NewNodes[0] != n)
            {
                return;
            }

            LinkView view = lv;
            AlignmentLink l = link;
            left = Cell(() =>
            {
                List<Piece>[] sides = view.OldSide();
                for (int i = 0; i < l.OldNodes.Length; i++)
                {
                    OldBlock(l.OldNodes[i], l, depth, sides[Math.Min(i, sides.Length - 1)], null, -1);
                }
            });
            right = Cell(() =>
            {
                List<Piece>[] sides = view.NewSide();
                for (int i = 0; i < l.NewNodes.Length; i++)
                {
                    NewBlock(l.NewNodes[i], l, depth, sides[Math.Min(i, sides.Length - 1)], null, -1);
                }
            });
        }
        else
        {
            LinkView view = lv;
            AlignmentLink l = link;
            left = Cell(() => OldBlock(l.OldNodes[0], l, depth, view.OldSide()[0], null, -1));
            right = Cell(() => NewBlock(n, l, depth, view.NewSide()[0], null, -1));
        }

        Row(left, right);
    }

    private void GhostRow(MNode m, int depth)
    {
        int id = m.Id;
        string left;
        if (m.MoveFrom && v.Alignment.LinkOfOld(id) is { } link && v.Links.TryGetValue(link.Id, out LinkView? lv))
        {
            left = Cell(() => OldBlock(id, link, depth, lv.OldSide()[0], null, lv.MoveChange));
        }
        else
        {
            left = Cell(() => OldBlock(id, null, depth, null, v.DeleteOfOld[id], -1));
        }

        Row(left, "");
    }

    private void Row(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return;
        }

        o.Raw("<tr>");
        CellOut(left);
        CellOut(right);
        o.Raw("</tr>\n");
    }

    private void CellOut(string html)
    {
        if (html.Length == 0)
        {
            o.Raw("<td class=\"empty\"></td>");
            return;
        }

        o.Raw("<td>");
        o.Raw(html);
        o.Raw("</td>");
    }

    /// <summary>Runs <paramref name="write"/> against the cell buffer and returns what it wrote.</summary>
    private string Cell(Action write)
    {
        buffer.GetStringBuilder().Clear();
        pw.NewCell();
        write();
        return buffer.ToString();
    }

    private static string DepthClass(int depth) => "d" + Math.Min(depth, 5).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A block of the new document. <paramref name="moveTo"/> is the Move change's id when this is where the block moved to, else -1.</summary>
    private void NewBlock(int n, AlignmentLink? link, int depth, List<Piece>? pieces, Change? ins, int moveTo)
    {
        Node node = v.New[n];
        v.AttributesOfNew.TryGetValue(n, out List<(int ChangeId, AttributeEdit Edit)>? attrs);
        string plain = pieces is null && ins is null ? ProjectedGroup.TextOf(v.New, node, v.Settings) : "";
        bool hasText = ins is not null || moveTo >= 0 || pieces is { Count: > 0 } || plain.Length > 0;
        if (!hasText && attrs is null)
        {
            return;
        }

        bool changed = ins is not null || moveTo >= 0 || attrs is not null || (pieces is not null && pieces.Any(p => p.Kind != PieceKind.Equal));
        cell.Raw("<div class=\"blk k-");
        cell.Raw(BlockShapes.KindClass(node.Kind));
        cell.Raw(" ");
        cell.Raw(DepthClass(depth));
        cell.Raw(changed ? " chg\"" : "\"");
        cell.IntAttr("data-new", n);
        if (link is not null)
        {
            cell.Attr("data-old", string.Join(' ', link.OldNodes));
            cell.IntAttr("data-link", link.Id);
        }

        cell.Raw(">");
        Label(node);
        if (hasText)
        {
            cell.Raw(changed && style.ChangeBars ? "<p class=\"t bar\">" : "<p class=\"t\">");
            if (moveTo >= 0)
            {
                cell.Raw("<span class=\"mv to\" data-role=\"to\" id=\"mvt-");
                cell.Int(moveTo);
                cell.Raw("\" data-change=\"");
                cell.Int(moveTo);
                cell.Raw("\">");
                pw.MoveLink(moveTo, fromHere: false);
            }

            if (ins is not null)
            {
                pw.Inserted(ins.Id, ins.New.Text);
            }
            else if (pieces is not null)
            {
                pw.Pieces(pieces, false);
            }
            else
            {
                cell.Text(plain);
            }

            if (moveTo >= 0)
            {
                cell.Raw("</span>");
            }

            cell.Raw("</p>");
        }

        if (attrs is not null)
        {
            pw.AttributeNotes(attrs);
        }

        cell.Raw("</div>");
    }

    /// <summary>A block of the old document. <paramref name="moveFrom"/> is the Move change's id when this is where the block moved away from, else -1.</summary>
    private void OldBlock(int o1, AlignmentLink? link, int depth, List<Piece>? pieces, Change? del, int moveFrom)
    {
        Node node = v.Old[o1];
        string plain = pieces is null && del is null ? ProjectedGroup.TextOf(v.Old, node, v.Settings) : "";
        bool hasText = del is not null || moveFrom >= 0 || pieces is { Count: > 0 } || plain.Length > 0 || link is null;
        if (!hasText)
        {
            return;
        }

        bool changed = del is not null || moveFrom >= 0 || link is null || (pieces is not null && pieces.Any(p => p.Kind != PieceKind.Equal));
        cell.Raw("<div class=\"blk k-");
        cell.Raw(BlockShapes.KindClass(node.Kind));
        cell.Raw(" ");
        cell.Raw(DepthClass(depth));
        cell.Raw(changed ? " chg\"" : "\"");
        cell.IntAttr("data-old", o1);
        if (link is not null)
        {
            cell.IntAttr("data-link", link.Id);
        }

        cell.Raw(">");
        Label(node);
        cell.Raw(changed && style.ChangeBars ? "<p class=\"t bar\">" : "<p class=\"t\">");
        if (moveFrom >= 0)
        {
            cell.Raw("<span class=\"mv from\" data-role=\"from\" id=\"mvf-");
            cell.Int(moveFrom);
            cell.Raw("\" data-change=\"");
            cell.Int(moveFrom);
            cell.Raw("\">");
            pw.MoveLink(moveFrom, fromHere: true);
        }

        if (pieces is not null)
        {
            pw.Pieces(pieces, false);
        }
        else if (del is not null)
        {
            pw.Deleted(del.Id, del.Old.Text, false);
        }
        else
        {
            pw.Deleted(-1, plain, false);
        }

        if (moveFrom >= 0)
        {
            cell.Raw("</span>");
        }

        cell.Raw("</p></div>");
    }

    private void Label(Node node)
    {
        string? label = BlockShapes.KindLabel(node);
        if (label is not null)
        {
            cell.Raw("<div class=\"kl\">");
            cell.Text(label);
            cell.Raw("</div>");
        }

        if (node.Kind == NodeKind.ListItem && node.Attributes.Get(FormatKeys.ListLabel) is { Length: > 0 } l)
        {
            cell.Raw("<span class=\"lbl\">");
            cell.Text(l);
            cell.Raw("</span>");
        }
    }
}
