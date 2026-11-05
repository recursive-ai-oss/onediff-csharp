using System.Globalization;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using RecursiveAI.OneOffice.Markup;

namespace OneDiff.Word.Internal;

internal sealed partial class TrackedWriter
{
    private readonly Dictionary<MarkupNode, MarkupNode> placedByElement = [];

    // ------------------------------------------------------------------------------------------------------------------ donor side

    private void VisitDonor(int id)
    {
        ct.ThrowIfCancellationRequested();
        Node n = dm[id];
        AlignmentLink? l = LinkOfDonor(id);
        if (handledDonor.Contains(id))
        {
            return;
        }

        if (donorCopy.TryGetValue(id, out DonorMode mode))
        {
            Transplant(id, mode, l);
            return;
        }

        if (!IsPair(l))
        {
            DonorOnly(id);
            return;
        }

        if (l!.Kind == LinkKind.Move)
        {
            if (IsParagraph(n.Kind))
            {
                Transplant(id, DonorMode.Move, l);
            }
            else
            {
                DonorOnly(id);
            }

            return;
        }

        if (n.Kind is NodeKind.Property or NodeKind.Comment)
        {
            return;
        }

        foreach (int c in dm.ChildrenOf(id))
        {
            NodeKind ck = dm[c].Kind;
            if (IsParagraph(n.Kind) && ck is not (NodeKind.Footnote or NodeKind.Endnote or NodeKind.TextBox))
            {
                continue;
            }

            VisitDonor(c);
        }
    }

    /// <summary>A donor node with no counterpart in the base: its content is copied in as deleted (base is new) or inserted (base is old).</summary>
    private void DonorOnly(int id)
    {
        Node n = dm[id];
        switch (n.Kind)
        {
            case NodeKind.Paragraph or NodeKind.ListItem or NodeKind.Table or NodeKind.TableRow or NodeKind.TableCell:
                Transplant(id, DonorMode.Opposite, null);
                break;
            case NodeKind.ContentControl:
                if (Wx.Is(d.Element(id), "sdt"))
                {
                    Note("content-control", "a block content control that exists on one side only; its content is tracked, the control itself cannot be", donorNode: id);
                    foreach (int c in dm.ChildrenOf(id))
                    {
                        VisitDonor(c);
                    }
                }

                break;
            case NodeKind.Section:
                Note("section", "a section that exists on one side only; its blocks are tracked, the section break cannot be", donorNode: id);
                foreach (int c in dm.ChildrenOf(id))
                {
                    VisitDonor(c);
                }

                break;
            case NodeKind.Header or NodeKind.Footer:
                Note("header-footer", $"{n.Kind} present on one side only; a header or footer cannot be added or removed as a tracked change", donorNode: id);
                break;
            case NodeKind.Property or NodeKind.Comment:
                Note(n.Kind == NodeKind.Property ? "properties" : "comments", $"{n.Kind} present on one side only; a tracked change cannot carry it", donorNode: id);
                break;
            default:
                break;
        }
    }

    private void Transplant(int id, DonorMode mode, AlignmentLink? link)
    {
        MarkupNode de = d.Element(id);
        Node n = dm[id];
        if (de.IsNull)
        {
            Note("unplaced", $"{n.Kind} has no element to copy", donorNode: id);
            return;
        }

        de.InsertXmlAfter(Wx.Bytes(de.OuterXml));
        MarkupNode clone = Wx.NextElement(de);
        string xml;
        try
        {
            if (mode == DonorMode.Move && link is not null)
            {
                WrapMove(clone, baseIsNew ? "moveFrom" : "moveTo", MoveName(link.Id));
            }
            else
            {
                switch (n.Kind)
                {
                    case NodeKind.Paragraph or NodeKind.ListItem:
                        MarkParagraphOn(clone, d);
                        break;
                    case NodeKind.Table:
                        MarkTable(clone, d);
                        break;
                    case NodeKind.TableRow:
                        MarkRow(clone, d);
                        break;
                    case NodeKind.TableCell:
                        MarkCell(clone, d);
                        break;
                    default:
                        MarkBlocksIn(clone, d);
                        break;
                }
            }

            if (baseIsNew)
            {
                // A section break cannot be tracked, so a copy of an old section break would outlive accept-all.
                DropSectionBreak(clone);
            }

            xml = clone.OuterXml;
        }
        finally
        {
            clone.Remove();
        }

        if (!Splice(id, de, xml, out MarkupNode copy))
        {
            Note("unplaced", $"{n.Kind} could not be placed in the output", donorNode: id);
            return;
        }

        placed[id] = copy;
        if (IsParagraph(n.Kind))
        {
            if (mode == DonorMode.Opposite)
            {
                markedParagraphs.Add((copy, !baseIsNew, baseIsNew ? "del" : "ins"));
            }
            else
            {
                markedParagraphs.Add((copy, !baseIsNew, baseIsNew ? "moveFrom" : "moveTo"));
            }
        }

        placedByElement[de] = copy;
        ImportFix(copy, de);
    }

    private static void DropSectionBreak(MarkupNode paragraph)
    {
        foreach (MarkupNode e in Wx.Descendants(paragraph).Where(e => Wx.Is(e, "sectPr")).ToList())
        {
            e.Remove();
        }
    }

    /// <summary>Inserts XML copied from the donor at the place that corresponds to the donor element <paramref name="de"/>.</summary>
    private bool Splice(int id, MarkupNode de, string xml, out MarkupNode copy)
    {
        copy = default;
        MarkupNode cursor = de;
        while (true)
        {
            MarkupNode container = BaseContainer(cursor.Parent);
            for (MarkupNode prev = Wx.PreviousElement(cursor); !prev.IsNull; prev = Wx.PreviousElement(prev))
            {
                MarkupNode anchor = default;
                if (placedByElement.TryGetValue(prev, out MarkupNode pe))
                {
                    anchor = pe;
                }
                else
                {
                    int pid = d.NodeOf(prev);
                    AlignmentLink? pl = pid >= 0 ? LinkOfDonor(pid) : null;
                    if (pid >= 0 && pl is { Kind: LinkKind.Match, OldNodes.Length: 1, NewNodes.Length: 1 })
                    {
                        anchor = b.Element(BaseOf(pl));
                    }
                    else if (pl is { Kind: LinkKind.Split or LinkKind.Merge } && splitTail.TryGetValue(pl.Id, out MarkupNode tail))
                    {
                        // The last of the paragraphs a split or merge was written as, when the previous donor paragraph went into one.
                        int last = baseIsNew ? pl.OldNodes[^1] : pl.NewNodes[^1];
                        if (last == pid)
                        {
                            anchor = tail;
                        }
                    }
                }

                if (!anchor.IsNull && (container.IsNull || anchor.Parent == container))
                {
                    anchor.InsertXmlAfter(Wx.Bytes(xml));
                    copy = Wx.NextElement(anchor);
                    return !copy.IsNull;
                }
            }

            // A block content control that exists only in the donor is not copied, so its blocks sit where it sat.
            MarkupNode parent = cursor.Parent;
            if (!parent.IsNull && Wx.Is(parent, "sdtContent") && !parent.Parent.IsNull && !DonorSdtMatched(parent.Parent))
            {
                cursor = parent.Parent;
                continue;
            }

            if (container.IsNull)
            {
                return false;
            }

            copy = InsertFirst(container, xml);
            return !copy.IsNull;
        }
    }

    private bool DonorSdtMatched(MarkupNode sdt)
    {
        int sid = d.NodeOf(sdt);
        return sid >= 0 && IsPair(LinkOfDonor(sid));
    }

    private MarkupNode BaseContainer(MarkupNode donorParent)
    {
        if (donorParent.IsNull)
        {
            return default;
        }

        if (Wx.Is(donorParent, "body"))
        {
            return Wx.Child(b.Doc.MainMarkup.Root, "body");
        }

        MarkupNode lookup = Wx.Is(donorParent, "sdtContent") ? donorParent.Parent : donorParent;
        int pid = d.NodeOf(lookup);
        AlignmentLink? l = pid >= 0 ? LinkOfDonor(pid) : null;
        if (l is not { OldNodes.Length: 1, NewNodes.Length: 1 })
        {
            return default;
        }

        MarkupNode be = b.Element(BaseOf(l));
        return Wx.Is(donorParent, "sdtContent") && !be.IsNull ? Wx.Child(be, "sdtContent") : be;
    }

    private static bool IsContentChild(MarkupNode container, MarkupNode c)
    {
        string ln = c.LocalNameString;
        if (Wx.Is(container, "tbl"))
        {
            return ln == "tr";
        }

        if (Wx.Is(container, "tr"))
        {
            return ln == "tc";
        }

        if (Wx.Is(container, "tc"))
        {
            return ln is "p" or "tbl" or "sdt";
        }

        return ln is "p" or "tbl" or "sdt" or "customXml";
    }

    private static MarkupNode InsertFirst(MarkupNode container, string xml)
    {
        foreach (MarkupNode c in Wx.ChildElements(container))
        {
            if (Wx.InMain(c) && IsContentChild(container, c))
            {
                c.InsertXmlBefore(Wx.Bytes(xml));
                return Wx.PreviousElement(c);
            }
        }

        // No content yet: before the section properties of a body, otherwise at the end.
        foreach (MarkupNode c in Wx.ChildElements(container))
        {
            if (Wx.Is(c, "sectPr"))
            {
                c.InsertXmlBefore(Wx.Bytes(xml));
                return Wx.PreviousElement(c);
            }
        }

        container.AppendXml(Wx.Bytes(xml));
        MarkupNode last = container.LastChild;
        while (!last.IsNull && last.Kind != MarkupNodeKind.Element)
        {
            last = last.PreviousSibling;
        }

        return last;
    }

    // ------------------------------------------------------------------------------------------------------------------ import

    /// <summary>Makes a copy from the donor document stand on its own in the base package (see <see cref="TrackedImporter"/>).</summary>
    private void ImportFix(MarkupNode copy, MarkupNode donorElement)
    {
        if (copy.IsNull)
        {
            return;
        }

        int before = importer.CommentsDropped;
        importer.Fix(copy, d.PartNameOf(donorElement), b.PartNameOf(copy));
        if (importer.CommentsDropped != before)
        {
            Note("comments", "a comment anchored in content that exists on one side only is not carried");
        }
    }
}
