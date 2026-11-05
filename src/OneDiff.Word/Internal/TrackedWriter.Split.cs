using System.Collections.Immutable;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using RecursiveAI.OneOffice.Markup;

namespace OneDiff.Word.Internal;

internal sealed partial class TrackedWriter
{
    private const string Separator = "\u001e";

    private readonly HashSet<int> splitDone = [];
    private readonly HashSet<int> splitFailed = [];

    /// <summary>One edit of a split or merge link, placed on the paragraph it belongs to.</summary>
    private sealed record SplitEdit(RegionEdit Edit, int BasePiece, int DonorPiece, bool Boundary, int BaseLocal, int DonorLocal);

    /// <summary>
    /// A paragraph that became several (split) or several that became one (merge) is written with the paragraph marks the version that
    /// has the extra paragraphs needs: when the base has them (a split on the new document), their marks are marked inserted and the
    /// other version's text between them is added as deleted text; when the other version has them, the base paragraph is cut where the
    /// marks go and the pieces carry deleted marks (ECMA-376 §17.13.5.15, .16). Rejecting then joins what the old document had joined,
    /// and accepting gives the new paragraphs, with the text in between read the same in both views.
    /// Returns false (and leaves the document as it was) when the link is not of a shape this can write; the paragraphs are then
    /// replaced whole.
    /// </summary>
    private bool SplitMerge(AlignmentLink link)
    {
        if (splitDone.Contains(link.Id))
        {
            return true;
        }

        if (splitFailed.Contains(link.Id))
        {
            return false;
        }

        bool ok;
        try
        {
            ok = WriteSplitMerge(link);
        }
        catch (ReplaceNeeded ex)
        {
            Trace?.Invoke($"split/merge link {link.Id}: {ex.Message}");
            ok = false;
        }

        (ok ? splitDone : splitFailed).Add(link.Id);
        return ok;
    }

    private static int ProjectedLength(Node n, CompareSettings settings)
    {
        int len = n.Text.Length;
        foreach (NormalizationFold f in n.Folds)
        {
            bool stays = settings.Hidden.Contains(f.Category);
            len += (stays ? f.Length : f.Original.Length) - f.Length;
        }

        return len;
    }

    private bool WriteSplitMerge(AlignmentLink link)
    {
        ImmutableArray<int> bnodes = baseIsNew ? link.NewNodes : link.OldNodes, dnodes = baseIsNew ? link.OldNodes : link.NewNodes;
        bool multiBase = bnodes.Length > 1;
        if (!regionEdits.TryGetValue(link.Id, out List<RegionEdit>? edits) || edits.Count == 0)
        {
            throw new ReplaceNeeded("no edits");
        }

        var belements = new List<MarkupNode>();
        foreach (int id in bnodes)
        {
            MarkupNode el = b.Element(id);
            if (el.IsNull || !IsParagraph(bm[id].Kind) || bm[id].Text.Contains('\uFFFC', StringComparison.Ordinal) || !Wx.Child(Wx.Child(el, "pPr"), "sectPr").IsNull)
            {
                throw new ReplaceNeeded("a paragraph of the link is not plain text");
            }

            belements.Add(el);
        }

        var delements = new List<MarkupNode>();
        foreach (int id in dnodes)
        {
            MarkupNode el = d.Element(id);
            if (el.IsNull || !IsParagraph(dm[id].Kind) || dm[id].Text.Contains('\uFFFC', StringComparison.Ordinal) || !Wx.Child(Wx.Child(el, "pPr"), "sectPr").IsNull)
            {
                throw new ReplaceNeeded("a paragraph of the link is not plain text");
            }

            delements.Add(el);
        }

        for (int i = 0; i + 1 < belements.Count; i++)
        {
            if (Wx.NextElement(belements[i]) != belements[i + 1])
            {
                throw new ReplaceNeeded("the paragraphs are not neighbours");
            }
        }

        // Where each paragraph of the side with several starts in that side's joined text (paragraphs are joined by one separator).
        CompareDocument multiDoc = multiBase ? bm : dm;
        ImmutableArray<int> multi = multiBase ? bnodes : dnodes;
        var starts = new int[multi.Length];
        var lengths = new int[multi.Length];
        for (int i = 0, at = 0; i < multi.Length; i++)
        {
            starts[i] = at;
            lengths[i] = ProjectedLength(multiDoc[multi[i]], settings);
            at += lengths[i] + 1;
        }

        var plan = new List<SplitEdit>();
        foreach (RegionEdit e in edits)
        {
            int bs = baseIsNew ? e.NewStart : e.OldStart, bl = baseIsNew ? e.NewLength : e.OldLength;
            int ds = baseIsNew ? e.OldStart : e.NewStart, dl = baseIsNew ? e.OldLength : e.NewLength;
            string bt = baseIsNew ? e.NewText : e.OldText, dt = baseIsNew ? e.OldText : e.NewText;
            bool bSep = bt.Contains(Separator, StringComparison.Ordinal), dSep = dt.Contains(Separator, StringComparison.Ordinal);
            if (e.IsFormatOnly)
            {
                throw new ReplaceNeeded("a format edit in a split or merge");
            }

            if (multiBase)
            {
                if (dSep)
                {
                    throw new ReplaceNeeded("an edit reaches across a paragraph boundary");
                }

                if (bSep)
                {
                    int p = -1;
                    for (int i = 0; i + 1 < multi.Length; i++)
                    {
                        if (bs == starts[i] + lengths[i])
                        {
                            p = i;
                        }
                    }

                    if (bl != 1 || bt != Separator || p < 0)
                    {
                        throw new ReplaceNeeded("an edit reaches across a paragraph boundary");
                    }

                    plan.Add(new SplitEdit(e, p, 0, true, lengths[p], ds));
                }
                else
                {
                    int p = -1;
                    for (int i = 0; i < multi.Length && p < 0; i++)
                    {
                        if (bs >= starts[i] && bs + bl <= starts[i] + lengths[i])
                        {
                            p = i;
                        }
                    }

                    if (p < 0)
                    {
                        throw new ReplaceNeeded("an edit lies outside the paragraphs");
                    }

                    plan.Add(new SplitEdit(e, p, 0, false, bs - starts[p], ds));
                }
            }
            else
            {
                if (bSep)
                {
                    throw new ReplaceNeeded("an edit reaches across a paragraph boundary");
                }

                if (dSep)
                {
                    int p = -1;
                    for (int i = 0; i + 1 < multi.Length; i++)
                    {
                        if (ds == starts[i] + lengths[i])
                        {
                            p = i;
                        }
                    }

                    if (dl != 1 || dt != Separator || p < 0)
                    {
                        throw new ReplaceNeeded("an edit reaches across a paragraph boundary");
                    }

                    plan.Add(new SplitEdit(e, p, p, true, bs, ds));
                }
                else
                {
                    int p = -1;
                    for (int i = 0; i < multi.Length && p < 0; i++)
                    {
                        if (ds >= starts[i] && ds + dl <= starts[i] + lengths[i])
                        {
                            p = i;
                        }
                    }

                    if (p < 0)
                    {
                        throw new ReplaceNeeded("an edit lies outside the paragraphs");
                    }

                    plan.Add(new SplitEdit(e, p, p, false, bs, ds - starts[p]));
                }
            }
        }

        // Paragraph properties: a piece that goes (its mark is marked) must already have the properties of the paragraph it stands for.
        if (!multiBase)
        {
            for (int j = 0; j + 1 < dnodes.Length; j++)
            {
                if (Signature(Wx.Child(belements[0], "pPr"), ["rPr", "sectPr", "pPrChange"]) != Signature(Wx.Child(delements[j], "pPr"), ["rPr", "sectPr", "pPrChange"]))
                {
                    throw new ReplaceNeeded("the paragraphs differ in properties");
                }
            }
        }

        string[] snapshots = [.. belements.Select(e => e.OuterXml)];
        var created = new List<MarkupNode>();
        var scratches = new Dictionary<int, MarkupNode>();
        try
        {
            string markLocal = multiBase == baseIsNew ? "ins" : "del";
            var pieces = new List<MarkupNode>();
            if (multiBase)
            {
                pieces.AddRange(belements);
            }
            else
            {
                // Cut the base paragraph at each boundary, from the last, so the earlier hub offsets stay valid.
                MarkupNode first = belements[0];
                Node bn = bm[bnodes[0]];
                var cuts = plan.Where(x => x.Boundary).OrderByDescending(x => baseIsNew ? x.Edit.NewStart : x.Edit.OldStart).ToList();
                var cutAtPristine = new List<int>();
                foreach (SplitEdit c in cuts)
                {
                    int bs = baseIsNew ? c.Edit.NewStart : c.Edit.OldStart, bl = baseIsNew ? c.Edit.NewLength : c.Edit.OldLength;
                    if (!TextPositions.TryHubRange(bn, settings, bs, bs + bl, out int bh, out int bhl))
                    {
                        throw new ReplaceNeeded("a boundary could not be placed");
                    }

                    if (bl > 0)
                    {
                        MarkRange(first, bh, bhl);
                    }

                    // Text deleted from the base no longer counts in the paragraph's text, so the cut moves back over it.
                    MarkupNode rest = CutParagraph(first, baseIsNew ? bh + bhl : bh);
                    created.Add(rest);
                    cutAtPristine.Insert(0, bh + bhl);
                    pieces.Insert(0, rest);
                }

                pieces.Insert(0, first);
                pieceStarts = [0, .. cutAtPristine];
            }

            foreach (SplitEdit x in plan.OrderByDescending(x => x.BasePiece).ThenByDescending(x => x.Boundary ? int.MaxValue : x.BaseLocal))
            {
                if (!multiBase && x.Boundary)
                {
                    continue;
                }

                RegionEdit r = x.Edit;
                Node bn = bm[bnodes[multiBase ? x.BasePiece : 0]];
                Node dn = dm[dnodes[multiBase ? 0 : x.DonorPiece]];
                MarkupNode piece = pieces[x.BasePiece];
                int donorNodeIndex = multiBase ? 0 : x.DonorPiece;
                if (!scratches.TryGetValue(donorNodeIndex, out MarkupNode scratch))
                {
                    scratches[donorNodeIndex] = scratch = NewScratch(delements[donorNodeIndex]);
                }

                // The edit in the coordinates of the two paragraphs it joins.
                int bStart = x.BaseLocal, dStart = x.DonorLocal;
                int bLen = baseIsNew ? r.NewLength : r.OldLength, dLen = baseIsNew ? r.OldLength : r.NewLength;
                string bText = baseIsNew ? r.NewText : r.OldText, dText = baseIsNew ? r.OldText : r.NewText;
                if (x.Boundary)
                {
                    // Multiple paragraphs on the base: the other version's text between two of them goes at the end of the first.
                    if (dLen == 0)
                    {
                        continue;
                    }

                    if (!TextPositions.TryHubRange(dn, settings, dStart, dStart + dLen, out int dh, out int dhl)
                        || !TextPositions.TryHubRange(bn, settings, lengths[x.BasePiece], lengths[x.BasePiece], out int bh, out _))
                    {
                        throw new ReplaceNeeded("a boundary could not be placed");
                    }

                    string xml = Harvest(scratch, pieces[x.BasePiece], dh, dhl, donorInserted: !baseIsNew);
                    if (xml.Length > 0)
                    {
                        PlaceAt(pieces[x.BasePiece], bh, xml, false);
                    }

                    continue;
                }

                RegionEdit local = baseIsNew
                    ? new RegionEdit(dStart, dLen, bStart, bLen, dText, bText, [], [])
                    : new RegionEdit(bStart, bLen, dStart, dLen, bText, dText, [], []);
                int shift = multiBase ? 0 : pieceStarts[x.BasePiece];
                TextRegion(bn, dn, piece, scratch, local, shift);
            }

            // Marks: every paragraph but the last of the side with several carries the revision that separates it from the next.
            for (int i = 0; i + 1 < pieces.Count; i++)
            {
                MarkParagraphMark(pieces[i], markLocal);
                markedParagraphs.Add((pieces[i], markLocal == "ins", markLocal));
            }

            // The last paragraph holds the properties of the joined paragraph in the other version.
            MarkupNode lastBase = pieces[^1], lastDonor = delements[^1];
            PropertyChange(lastBase, lastDonor, "pPr", "pPrChange", ["rPr", "sectPr", "pPrChange"], insertAtStart: true);
        }
        catch (Exception ex) when (ex is ReplaceNeeded or ArgumentException or InvalidOperationException)
        {
            foreach (MarkupNode c in created)
            {
                if (!c.Parent.IsNull)
                {
                    c.Remove();
                }
            }

            for (int i = 0; i < belements.Count; i++)
            {
                MarkupNode current = b.Element(bnodes[i]);
                if (current.IsNull)
                {
                    continue;
                }

                MarkupNode restored = Restore(bnodes[i], current, snapshots[i]);
                belements[i] = restored;
            }

            throw ex is ReplaceNeeded ? ex : new ReplaceNeeded(ex.Message);
        }
        finally
        {
            foreach (MarkupNode s in scratches.Values)
            {
                if (!s.IsNull)
                {
                    s.Remove();
                }
            }
        }

        foreach (int id in dnodes)
        {
            handledDonor.Add(id);
        }

        return true;
    }

    private int[] pieceStarts = [];

    private readonly HashSet<int> handledDonor = [];

    /// <summary>
    /// Cuts a paragraph in two at a paragraph offset: what follows the offset moves to a new paragraph after it, with the same
    /// properties (without the revision marks). Returns the new paragraph. The cut has to fall between the paragraph's own children.
    /// </summary>
    private MarkupNode CutParagraph(MarkupNode p, int offset)
    {
        b.Marker.InsertText(p, offset, "x");
        MarkupNode marker = default;
        foreach (MarkupNode m in MarkerElements(p, "ins"))
        {
            marker = m;
            break;
        }

        if (marker.IsNull || marker.Parent != p)
        {
            throw new ReplaceNeeded("the paragraph cannot be cut there");
        }

        var tail = new StringBuilder();
        var move = new List<MarkupNode>();
        for (MarkupNode c = marker.NextSibling; !c.IsNull; c = c.NextSibling)
        {
            if (c.Kind == MarkupNodeKind.Element)
            {
                tail.Append(c.OuterXml);
                move.Add(c);
            }
        }

        MarkupNode pPr = Wx.Child(p, "pPr");
        string props = "";
        if (!pPr.IsNull)
        {
            string keep = Content(pPr, ["rPr", "sectPr", "pPrChange"], false);
            props = keep.Length == 0 ? "" : "<" + wp + ":pPr>" + keep + "</" + wp + ":pPr>";
        }

        p.InsertXmlAfter(Wx.Bytes("<" + wp + ":p>" + props + tail + "</" + wp + ":p>"));
        MarkupNode rest = Wx.NextElement(p);
        foreach (MarkupNode c in move)
        {
            c.Remove();
        }

        marker.Remove();
        return rest;
    }
}
