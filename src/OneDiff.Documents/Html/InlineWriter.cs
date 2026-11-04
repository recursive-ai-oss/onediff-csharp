using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Html;

/// <summary>
/// The single-document layout: the new document's blocks in order, with deleted text struck in place, inserted text marked,
/// deleted blocks and the old places of moved blocks shown where they used to be.
/// </summary>
internal sealed class InlineWriter(HtmlOut o, RedlineView v, RedlineStyle style, CancellationToken ct)
{
    private readonly PieceWriter pw = new(o, style, v);

    public void Write()
    {
        o.Raw("<main aria-label=\"Redline\">\n");
        foreach (MNode root in v.Roots)
        {
            Render(root, null);
        }

        o.Raw("</main>\n");
    }

    private void Render(MNode m, NodeKind? parentKind)
    {
        ct.ThrowIfCancellationRequested();
        if (m.Ghost)
        {
            Ghost(m, parentKind);
        }
        else
        {
            Live(m, parentKind);
        }
    }

    private void Live(MNode m, NodeKind? parentKind)
    {
        int n = m.Id;
        Node node = v.New[n];
        AlignmentLink? link = v.Alignment.LinkOfNew(n);
        LinkView? lv = null;
        if (link is not null)
        {
            v.Links.TryGetValue(link.Id, out lv);
        }

        Change? ins = link is null ? v.InsertOfNew[n] : null;
        List<Piece>? pieces = null;
        if (lv is not null)
        {
            List<Piece>[] members = lv.Inline();
            int member = Math.Max(link!.NewNodes.IndexOf(n), 0);
            pieces = members[Math.Min(member, members.Length - 1)];
        }

        bool moveTo = lv is { MoveChange: >= 0 };
        v.AttributesOfNew.TryGetValue(n, out List<(int ChangeId, AttributeEdit Edit)>? attrs);
        string plain = lv is null && ins is null ? ProjectedGroup.TextOf(v.New, node, v.Settings) : "";
        bool hasText = ins is not null || moveTo || pieces is { Count: > 0 } || plain.Length > 0;
        bool changed = ins is not null || moveTo || attrs is not null || (pieces is not null && pieces.Any(p => p.Kind != PieceKind.Equal));

        string tag = BlockShapes.TagFor(node.Kind, parentKind);
        bool wrapSelf = tag == "tr" && (hasText || attrs is not null);
        o.Raw("<");
        o.Raw(tag);
        o.Raw(" class=\"blk k-");
        o.Raw(BlockShapes.KindClass(node.Kind));
        o.Raw(changed ? " chg\"" : "\"");
        o.IntAttr("data-new", n);
        if (link is not null)
        {
            o.Attr("data-old", string.Join(' ', link.OldNodes));
            o.IntAttr("data-link", link.Id);
        }

        o.Raw(">");
        if (tag == "table")
        {
            o.Raw("<caption>");
        }
        else if (wrapSelf)
        {
            o.Raw("<td class=\"self\" colspan=\"99\">");
        }

        string? label = BlockShapes.KindLabel(node);
        if (label is not null)
        {
            o.Raw("<div class=\"kl\">");
            o.Text(label);
            o.Raw("</div>");
        }

        ListLabel(node);
        if (hasText)
        {
            o.Raw(changed && style.ChangeBars ? "<p class=\"t bar\">" : "<p class=\"t\">");
            if (moveTo)
            {
                int id = lv!.MoveChange;
                o.Raw("<span class=\"mv to\" data-role=\"to\" id=\"mvt-");
                o.Int(id);
                o.Raw("\" data-change=\"");
                o.Int(id);
                o.Raw("\">");
                pw.MoveLink(id, fromHere: false);
            }

            if (ins is not null)
            {
                pw.Inserted(ins.Id, ins.New.Text);
            }
            else if (pieces is not null)
            {
                pw.Pieces(pieces, true);
            }
            else
            {
                o.Text(plain);
            }

            if (moveTo)
            {
                o.Raw("</span>");
            }

            o.Raw("</p>");
        }

        if (attrs is not null)
        {
            pw.AttributeNotes(attrs);
        }

        if (tag == "table")
        {
            o.Raw("</caption><tbody>");
        }
        else if (wrapSelf)
        {
            o.Raw("</td>");
        }

        foreach (MNode child in m.Children)
        {
            Render(child, node.Kind);
        }

        Close(tag);
    }

    private void Ghost(MNode m, NodeKind? parentKind)
    {
        int o1 = m.Id;
        Node node = v.Old[o1];
        Change? del = m.MoveFrom ? null : v.DeleteOfOld[o1];
        AlignmentLink? link = m.MoveFrom ? v.Alignment.LinkOfOld(o1) : null;
        Change? move = link is not null && v.MoveOfLink.TryGetValue(link.Id, out Change? mc) ? mc : null;
        string text = m.MoveFrom ? move?.Old.Text ?? ProjectedGroup.TextOf(v.Old, node, v.Settings) : del?.Old.Text ?? ProjectedGroup.TextOf(v.Old, node, v.Settings);

        string tag = BlockShapes.TagFor(node.Kind, parentKind);
        o.Raw("<");
        o.Raw(tag);
        o.Raw(" class=\"blk ghost chg k-");
        o.Raw(BlockShapes.KindClass(node.Kind));
        o.Raw("\"");
        o.IntAttr("data-old", o1);
        o.Raw(">");
        if (tag == "table")
        {
            o.Raw("<caption>");
        }
        else if (tag == "tr")
        {
            o.Raw("<td class=\"self\" colspan=\"99\">");
        }

        string? label = BlockShapes.KindLabel(node);
        if (label is not null)
        {
            o.Raw("<div class=\"kl\">");
            o.Text(label);
            o.Raw("</div>");
        }

        ListLabel(node);
        o.Raw(style.ChangeBars ? "<p class=\"t bar\">" : "<p class=\"t\">");
        if (m.MoveFrom && move is not null)
        {
            o.Raw("<span class=\"mv from\" data-role=\"from\" id=\"mvf-");
            o.Int(move.Id);
            o.Raw("\" data-change=\"");
            o.Int(move.Id);
            o.Raw("\">");
            pw.MoveLink(move.Id, fromHere: true);
            o.Text(text);
            o.Raw("</span>");
        }
        else if (m.MoveFrom)
        {
            o.Text(text);
        }
        else
        {
            pw.Deleted(del?.Id ?? -1, text, false);
        }

        o.Raw("</p>");
        if (tag == "table")
        {
            o.Raw("</caption><tbody>");
        }
        else if (tag == "tr")
        {
            o.Raw("</td>");
        }

        foreach (MNode child in m.Children)
        {
            Render(child, node.Kind);
        }

        Close(tag);
    }

    private void ListLabel(Node node)
    {
        if (node.Kind == NodeKind.ListItem && node.Attributes.Get(FormatKeys.ListLabel) is { Length: > 0 } label)
        {
            o.Raw("<span class=\"lbl\">");
            o.Text(label);
            o.Raw("</span>");
        }
    }

    private void Close(string tag)
    {
        if (tag == "table")
        {
            o.Raw("</tbody></table>\n");
        }
        else
        {
            o.Raw("</");
            o.Raw(tag);
            o.Raw(">\n");
        }
    }
}
