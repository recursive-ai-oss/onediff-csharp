using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using RecursiveAI.OneOffice;
using RecursiveAI.OneOffice.Documents;
using RecursiveAI.OneOffice.Documents.Revisions;
using RecursiveAI.OneOffice.IO;
using RecursiveAI.OneOffice.Markup;

namespace OneDiff.Word.Internal;

/// <summary>What a write could not carry as a revision and why.</summary>
internal sealed record TrackedNote(string Category, string Message, ImmutableArray<int> BaseNodes, ImmutableArray<int> DonorNodes);

/// <summary>Settings of one tracked-changes write.</summary>
internal sealed record TrackedOptions(string Author, DateTimeOffset? Date, bool BuildOnNew);

/// <summary>
/// Writes a comparison as a DOCX with real tracked changes (ECMA-376 Part 1 §17.13.5): the <em>base</em> document (the new one, or the
/// old one when asked) is reopened from the bytes it was read from, and every difference is recorded on it as a revision so that
/// accepting all gives the new document and rejecting all gives the old one. Content only the other (<em>donor</em>) document has
/// is copied in as deleted (or inserted) content.
/// </summary>
internal sealed partial class TrackedWriter : IDisposable
{
    private readonly Comparison cmp;
    private readonly TrackedOptions options;
    private readonly bool baseIsNew;
    private readonly TrackedSide b;
    private readonly TrackedSide d;
    private readonly CompareDocument bm;
    private readonly CompareDocument dm;
    private readonly CompareSettings settings;
    private readonly string wp;
    private readonly string authorXml;
    private readonly string dateXml;
    private readonly List<TrackedNote> notes = [];
    private readonly CancellationToken ct;
    private readonly TrackedImporter importer;

    private readonly Dictionary<int, List<RegionEdit>> regionEdits = [];
    private readonly HashSet<int> changedLinks = [];
    private readonly Dictionary<int, DonorMode> donorCopy = [];
    private readonly Dictionary<int, MarkupNode> placed = [];
    private readonly Dictionary<int, string> moveNames = [];
    private readonly HashSet<int> visitedBase = [];
    private int moveCounter;
    private long idCounter = 1_000_000;

    private enum DonorMode
    {
        /// <summary>The donor's content is shown as the opposite of the base's: deleted when the base is new, inserted when it is old.</summary>
        Opposite,

        /// <summary>The donor's content is the far end of a move.</summary>
        Move,
    }

    public TrackedWriter(Comparison comparison, TrackedOptions options, TrackedSide baseSide, TrackedSide donorSide, CancellationToken cancellationToken)
    {
        cmp = comparison;
        this.options = options;
        baseIsNew = options.BuildOnNew;
        b = baseSide;
        d = donorSide;
        bm = baseSide.Model;
        dm = donorSide.Model;
        settings = comparison.Settings;
        wp = baseSide.Prefix;
        authorXml = Wx.EscapeAttribute(options.Author);
        dateXml = options.Date is { } dt ? " " + wp + ":date=\"" + dt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\"" : "";
        ct = cancellationToken;
        importer = new TrackedImporter(donorSide, baseSide);
    }

    public ImmutableArray<TrackedNote> Notes => [.. notes, .. importer.Problems.Select(p => new TrackedNote("import", p, [], []))];

    public void Dispose()
    {
        b.Dispose();
        d.Dispose();
    }

    // ------------------------------------------------------------------------------------------------------------------ entry

    public byte[] Run()
    {
        foreach (Change c in cmp.Changes)
        {
            if (c.LinkId < 0)
            {
                continue;
            }

            changedLinks.Add(c.LinkId);
            if (!c.Edits.IsDefaultOrEmpty)
            {
                if (!regionEdits.TryGetValue(c.LinkId, out List<RegionEdit>? list))
                {
                    regionEdits[c.LinkId] = list = [];
                }

                list.AddRange(c.Edits);
            }
        }

        foreach (int root in bm.Roots)
        {
            VisitBase(root);
        }

        foreach (int root in dm.Roots)
        {
            VisitDonor(root);
        }

        FixTailMarks();
        RenumberRevisions();
        foreach (string part in b.PartNames)
        {
            MarkupNode root = b.Root(part);
            if (!root.IsNull)
            {
                Wx.DeclareOnRoot(root);
            }
        }

        using var ms = new MemoryStream();
        b.Doc.SaveAs(ms, OfficeFormat.Docx);
        return ms.ToArray();
    }

    private void Note(string category, string message, int baseNode = -1, int donorNode = -1) =>
        notes.Add(new TrackedNote(category, message, baseNode >= 0 ? [baseNode] : [], donorNode >= 0 ? [donorNode] : []));

    // ------------------------------------------------------------------------------------------------------------------ orientation

    private AlignmentLink? LinkOfBase(int node) => baseIsNew ? cmp.Alignment.LinkOfNew(node) : cmp.Alignment.LinkOfOld(node);

    private AlignmentLink? LinkOfDonor(int node) => baseIsNew ? cmp.Alignment.LinkOfOld(node) : cmp.Alignment.LinkOfNew(node);

    private static bool IsPair(AlignmentLink? l) => l is { Kind: LinkKind.Match or LinkKind.Move, OldNodes.Length: 1, NewNodes.Length: 1 };

    private int DonorOf(AlignmentLink l) => baseIsNew ? l.OldNodes[0] : l.NewNodes[0];

    private int BaseOf(AlignmentLink l) => baseIsNew ? l.NewNodes[0] : l.OldNodes[0];

    private static bool IsBlockContainer(NodeKind k) =>
        k is NodeKind.Section or NodeKind.Table or NodeKind.TableRow or NodeKind.TableCell or NodeKind.ContentControl
            or NodeKind.Header or NodeKind.Footer or NodeKind.Footnote or NodeKind.Endnote or NodeKind.TextBox;

    private static bool IsParagraph(NodeKind k) => k is NodeKind.Paragraph or NodeKind.ListItem;

    private static bool IsObject(NodeKind k) =>
        k is NodeKind.Field or NodeKind.Image or NodeKind.TextBox or NodeKind.ContentControl or NodeKind.Footnote or NodeKind.Endnote;

    // ------------------------------------------------------------------------------------------------------------------ base side

    private void VisitBase(int id)
    {
        ct.ThrowIfCancellationRequested();
        Node n = bm[id];
        AlignmentLink? l = LinkOfBase(id);
        if (!IsPair(l))
        {
            BaseOnly(id);
            return;
        }

        int did = DonorOf(l!);
        bool descend = true;
        switch (n.Kind)
        {
            case NodeKind.Paragraph or NodeKind.ListItem:
                descend = ParagraphPair(id, did, l!);
                break;
            case NodeKind.Table when l!.Kind == LinkKind.Move:
            case NodeKind.TableRow when l!.Kind == LinkKind.Move:
            case NodeKind.TableCell when l!.Kind == LinkKind.Move:
                BaseOnly(id);
                return;
            case NodeKind.Table or NodeKind.TableRow or NodeKind.TableCell:
                TablePair(id, did, n.Kind);
                break;
            case NodeKind.Section:
                SectionPair(id, did);
                break;
            case NodeKind.Property or NodeKind.Comment:
                if (changedLinks.Contains(l!.Id))
                {
                    Note(n.Kind == NodeKind.Property ? "properties" : "comments", $"{n.Kind} changed; a tracked change cannot carry it", id, did);
                }

                return;
            case NodeKind.ContentControl when changedLinks.Contains(l!.Id):
                Note("content-control", "content control properties changed; a tracked change cannot carry them", id, did);
                break;
            default:
                break;
        }

        if (!descend)
        {
            return;
        }

        foreach (int c in bm.ChildrenOf(id))
        {
            NodeKind ck = bm[c].Kind;
            if (IsParagraph(n.Kind) && ck is not (NodeKind.Footnote or NodeKind.Endnote or NodeKind.TextBox))
            {
                continue;
            }

            VisitBase(c);
        }
    }

    /// <summary>Marks a node and everything under it as present only in the base document.</summary>
    private void BaseOnly(int id)
    {
        Node n = bm[id];
        MarkupNode el = b.Element(id);
        switch (n.Kind)
        {
            case NodeKind.Paragraph or NodeKind.ListItem:
                if (!el.IsNull)
                {
                    MarkParagraph(el);
                }

                break;
            case NodeKind.Table:
                if (!el.IsNull)
                {
                    MarkTable(el, b);
                }

                break;
            case NodeKind.TableRow:
                if (!el.IsNull)
                {
                    MarkRow(el, b);
                }

                break;
            case NodeKind.TableCell:
                if (!el.IsNull)
                {
                    MarkCell(el, b);
                }

                break;
            case NodeKind.ContentControl when !el.IsNull && Wx.Is(el, "sdt"):
                Note("content-control", "a block content control that exists on one side only; its content is tracked, the control itself cannot be", id);
                MarkBlocksIn(el, b);
                break;
            case NodeKind.Section:
                Note("section", "a section that exists on one side only; its blocks are tracked, the section break cannot be", id);
                foreach (int c in bm.ChildrenOf(id))
                {
                    VisitBase(c);
                }

                break;
            case NodeKind.Header or NodeKind.Footer:
                Note("header-footer", $"{n.Kind} present on one side only; a header or footer cannot be added or removed as a tracked change", id);
                break;
            case NodeKind.Property or NodeKind.Comment:
                Note(n.Kind == NodeKind.Property ? "properties" : "comments", $"{n.Kind} present on one side only; a tracked change cannot carry it", id);
                break;
            default:
                break;
        }
    }

    private void MarkBlocksIn(MarkupNode container, TrackedSide side)
    {
        foreach (MarkupNode e in Wx.Descendants(container))
        {
            if (Wx.Is(e, "p") && !InsideTextBox(e, container))
            {
                // Descendants lists nested paragraphs too, so a cell's paragraphs are reached from the table.
                MarkParagraphOn(e, side, record: false);
            }
        }
    }

    private static bool InsideTextBox(MarkupNode p, MarkupNode stop)
    {
        for (MarkupNode a = p.Parent; !a.IsNull && a != stop; a = a.Parent)
        {
            if (Wx.Is(a, "txbxContent"))
            {
                return true;
            }
        }

        return false;
    }

    private void MarkParagraph(MarkupNode p) => MarkParagraphOn(p, b);

    /// <summary>
    /// Marks a paragraph (its content and its mark) as inserted or deleted so that the base reads as it should after accept-all: the
    /// authoring call writes a deletion by a marker author, and the result is renamed to what the base needs.
    /// </summary>
    private void MarkParagraphOn(MarkupNode p, TrackedSide side, bool record = true)
    {
        bool toInserted = side == b ? baseIsNew : !baseIsNew;
        SimpleFieldsToComplex(p);
        side.Marker.DeleteParagraph(p);
        ConvertMarkers(p, toInserted);
        WrapInlineControls(p, toInserted ? "ins" : "del");
        if (record && side == b)
        {
            markedParagraphs.Add((p, toInserted, toInserted ? "ins" : "del"));
        }
    }

    private readonly List<(MarkupNode Paragraph, bool Inserted, string Local)> markedParagraphs = [];

    /// <summary>
    /// Rewrites <c>w:fldSimple</c> as the equivalent run-level field (ECMA-376 §17.16.18 and §17.16.5): a simple field is not allowed
    /// inside a revision wrapper, so it would outlive the deletion of its text, while the runs of a complex field are all wrapped.
    /// </summary>
    private void SimpleFieldsToComplex(MarkupNode p)
    {
        foreach (MarkupNode f in Wx.Descendants(p).Where(e => Wx.Is(e, "fldSimple")).ToList())
        {
            string instr = Wx.EscapeAttribute(Wx.Attr(f, "instr") ?? "").Replace("&quot;", "\"", StringComparison.Ordinal);
            var sb = new StringBuilder();
            sb.Append('<').Append(wp).Append(":r><").Append(wp).Append(":fldChar ").Append(wp).Append(":fldCharType=\"begin\"/></").Append(wp).Append(":r>");
            sb.Append('<').Append(wp).Append(":r><").Append(wp).Append(":instrText xml:space=\"preserve\"> ").Append(instr).Append(" </").Append(wp).Append(":instrText></").Append(wp).Append(":r>");
            sb.Append('<').Append(wp).Append(":r><").Append(wp).Append(":fldChar ").Append(wp).Append(":fldCharType=\"separate\"/></").Append(wp).Append(":r>");
            for (MarkupNode c = f.FirstChild; !c.IsNull; c = c.NextSibling)
            {
                if (c.Kind == MarkupNodeKind.Element)
                {
                    sb.Append(c.OuterXml);
                }
            }

            sb.Append('<').Append(wp).Append(":r><").Append(wp).Append(":fldChar ").Append(wp).Append(":fldCharType=\"end\"/></").Append(wp).Append(":r>");
            f.ReplaceWithXml(Wx.Bytes(sb.ToString()));
        }
    }

    /// <summary>
    /// An inline content control keeps its wrapper when only the runs inside it are marked, which would leave an empty control behind
    /// after the content is removed; the control is wrapped as a whole instead (<c>w:ins</c> and <c>w:del</c> may hold a <c>w:sdt</c>).
    /// </summary>
    private void WrapInlineControls(MarkupNode p, string wrapper)
    {
        foreach (MarkupNode c in Wx.ChildElements(p).Where(e => Wx.Is(e, "sdt")).ToList())
        {
            string xml = c.OuterXml;
            xml = Wx.Unwrap(xml, wp, "ins");
            xml = Wx.Unwrap(xml, wp, "del");
            string attrs = " " + wp + ":id=\"" + NextId().ToString(CultureInfo.InvariantCulture) + "\" " + wp + ":author=\"" + authorXml + "\"" + dateXml;
            c.ReplaceWithXml(Wx.Bytes("<" + wp + ":" + wrapper + attrs + ">" + xml + "</" + wp + ":" + wrapper + ">"));
        }
    }

    private void MarkTable(MarkupNode tbl, TrackedSide side)
    {
        foreach (MarkupNode e in Wx.Descendants(tbl))
        {
            if (Wx.Is(e, "tr") && e.Parent == tbl)
            {
                MarkRow(e, side);
            }
        }
    }

    private void MarkRow(MarkupNode tr, TrackedSide side)
    {
        side.Marker.MarkRowDeleted(tr);
        ConvertMarkers(tr, toInserted: side == b ? baseIsNew : !baseIsNew);
    }

    private void MarkCell(MarkupNode tc, TrackedSide side)
    {
        side.Marker.MarkCellDeleted(tc);
        ConvertMarkers(tc, toInserted: side == b ? baseIsNew : !baseIsNew);
    }

    // ------------------------------------------------------------------------------------------------------------------ markers

    /// <summary>
    /// Renames the revision elements the marker author created in a subtree: deletions become insertions when the content is
    /// inserted (with their text as ordinary text), and the author and date become the real ones.
    /// </summary>
    private void ConvertMarkers(MarkupNode scope, bool toInserted)
    {
        var targets = new List<MarkupNode>();
        foreach (MarkupNode e in Wx.Descendants(scope))
        {
            if (Wx.InMain(e) && Wx.Attr(e, "author") == Wx.Marker)
            {
                string ln = e.LocalNameString;
                if (ln is "del" or "ins" or "cellDel" or "cellIns")
                {
                    targets.Add(e);
                }
            }
        }

        foreach (MarkupNode e in targets)
        {
            Retag(e, toInserted);
        }
    }

    private void Retag(MarkupNode e, bool toInserted)
    {
        string xml = e.OuterXml;
        if (toInserted)
        {
            xml = Wx.Rename(xml, wp, "del", "ins");
            xml = Wx.Rename(xml, wp, "cellDel", "cellIns");
            xml = Wx.Rename(xml, wp, "delText", "t");
            xml = Wx.Rename(xml, wp, "delInstrText", "instrText");
        }

        e.ReplaceWithXml(Wx.Bytes(WithAuthor(xml)));
    }

    /// <summary>Replaces the marker author in revision XML by the real author and date.</summary>
    private string WithAuthor(string xml)
    {
        string with = Wx.EscapeAttribute(Wx.Marker);
        string at = wp + ":author=\"" + with + "\"";
        return xml.Replace(at, wp + ":author=\"" + authorXml + "\"" + dateXml, StringComparison.Ordinal);
    }

    private static IEnumerable<MarkupNode> MarkerElements(MarkupNode scope, params string[] locals)
    {
        foreach (MarkupNode e in Wx.Descendants(scope))
        {
            if (Wx.InMain(e) && Wx.Attr(e, "author") == Wx.Marker && Array.IndexOf(locals, e.LocalNameString) >= 0)
            {
                yield return e;
            }
        }
    }

    private long NextId() => ++idCounter;

    // ------------------------------------------------------------------------------------------------------------------ paragraphs

    private sealed class ReplaceNeeded(string reason) : Exception(reason);

    /// <summary>Records the differences of two paragraphs that correspond. Returns whether the nodes under the base paragraph still need a visit.</summary>
    private bool ParagraphPair(int bid, int did, AlignmentLink link)
    {
        MarkupNode bp = b.Element(bid), dp = d.Element(did);
        bool replace = bp.IsNull || dp.IsNull || !ObjectsStable(bid, did);
        bool move = link.Kind == LinkKind.Move;
        if (!replace)
        {
            string snapshot = bp.OuterXml;
            try
            {
                FineEdits(bid, did, bp, dp, link);
                if (!move)
                {
                    // A moved paragraph exists twice (moved from, moved to), each with its own properties, so it needs no property
                    // change; and rejecting one next to a paragraph-mark revision would not resolve the mark (a gap in OneOffice).
                    PropertyChange(bp, dp, "pPr", "pPrChange", ["rPr", "sectPr", "pPrChange"], insertAtStart: true);
                }

            }
            catch (Exception ex) when (ex is ReplaceNeeded or ArgumentException or InvalidOperationException)
            {
                bp = Restore(bid, bp, snapshot);
                replace = true;
            }
        }

        if (replace)
        {
            if (!bp.IsNull)
            {
                MarkParagraph(bp);
            }

            donorCopy[did] = DonorMode.Opposite;
            return false;
        }

        if (move)
        {
            string name = MoveName(link.Id);
            WrapMove(bp, baseIsNew ? "moveTo" : "moveFrom", name);
            markedParagraphs.Add((bp, baseIsNew, baseIsNew ? "moveTo" : "moveFrom"));
            donorCopy[did] = DonorMode.Move;
        }

        return true;
    }

    /// <summary>Puts a paragraph back as it was before a failed edit; the restored paragraph is a new node, so the model node is rebound to it.</summary>
    private MarkupNode Restore(int bid, MarkupNode bp, string snapshot)
    {
        MarkupNode prev = Wx.PreviousElement(bp), parent = bp.Parent;
        bp.ReplaceWithXml(Wx.Bytes(snapshot));
        MarkupNode restored = prev.IsNull ? FirstElement(parent) : Wx.NextElement(prev);
        b.Rebind(bid, restored);
        return restored;
    }

    private static MarkupNode FirstElement(MarkupNode parent)
    {
        MarkupNode c = parent.FirstChild;
        while (!c.IsNull && c.Kind != MarkupNodeKind.Element)
        {
            c = c.NextSibling;
        }

        return c;
    }

    private string MoveName(int linkId)
    {
        if (!moveNames.TryGetValue(linkId, out string? n))
        {
            n = "move" + (++moveCounter).ToString(CultureInfo.InvariantCulture);
            moveNames[linkId] = n;
        }

        return n;
    }

    /// <summary>
    /// True when the objects inside the two paragraphs (fields, pictures, notes, text boxes, content controls) are the same ones in the
    /// same order and none of them changed, so the text edits alone describe the difference.
    /// </summary>
    private bool ObjectsStable(int bid, int did)
    {
        List<int> bo = ObjectChildren(bm, bid), dobj = ObjectChildren(dm, did);
        if (bo.Count != dobj.Count)
        {
            return false;
        }

        for (int i = 0; i < bo.Count; i++)
        {
            AlignmentLink? l = LinkOfBase(bo[i]);
            if (l is not { Kind: LinkKind.Match } || DonorOf(l) != dobj[i] || BaseOf(l) != bo[i] || changedLinks.Contains(l.Id))
            {
                return false;
            }
        }

        return true;
    }

    private static List<int> ObjectChildren(CompareDocument m, int id)
    {
        var list = new List<int>();
        foreach (int c in m.ChildrenOf(id))
        {
            if (IsObject(m[c].Kind))
            {
                list.Add(c);
            }
        }

        return list;
    }

    // ------------------------------------------------------------------------------------------------------------------ text regions

    private void FineEdits(int bid, int did, MarkupNode bp, MarkupNode dp, AlignmentLink link)
    {
        if (!regionEdits.TryGetValue(link.Id, out List<RegionEdit>? list) || list.Count == 0)
        {
            return;
        }

        Node bn = bm[bid], dn = dm[did];
        var regions = new List<RegionEdit>(list);
        regions.Sort((x, y) => baseIsNew ? x.NewStart.CompareTo(y.NewStart) : x.OldStart.CompareTo(y.OldStart));
        MarkupNode scratch = default;
        try
        {
            // Work from the end of the paragraph so earlier positions stay valid.
            for (int i = regions.Count - 1; i >= 0; i--)
            {
                RegionEdit r = regions[i];
                if (r.OldText.Contains('\uFFFC', StringComparison.Ordinal) || r.NewText.Contains('\uFFFC', StringComparison.Ordinal))
                {
                    throw new ReplaceNeeded("an edit reaches an object");
                }

                if (r.IsFormatOnly)
                {
                    throw new ReplaceNeeded("format edit");
                }

                if (scratch.IsNull)
                {
                    scratch = NewScratch(dp);
                }

                TextRegion(bn, dn, bp, scratch, r);
            }
        }
        finally
        {
            if (!scratch.IsNull)
            {
                scratch.Remove();
            }
        }
    }

    private static MarkupNode NewScratch(MarkupNode donorParagraph)
    {
        donorParagraph.InsertXmlAfter(Wx.Bytes(donorParagraph.OuterXml));
        return Wx.NextElement(donorParagraph);
    }

    private void TextRegion(Node bn, Node dn, MarkupNode bp, MarkupNode scratch, RegionEdit r)
    {
        int bs = baseIsNew ? r.NewStart : r.OldStart, bl = baseIsNew ? r.NewLength : r.OldLength;
        int ds = baseIsNew ? r.OldStart : r.NewStart, dl = baseIsNew ? r.OldLength : r.NewLength;
        if (!TextPositions.TryHubRange(bn, settings, bs, bs + bl, out int bh, out int bhl)
            || !TextPositions.TryHubRange(dn, settings, ds, ds + dl, out int dh, out int dhl))
        {
            throw new ReplaceNeeded("a position of the edit could not be placed");
        }

        string donorXml = "";
        if (dl > 0)
        {
            donorXml = Harvest(scratch, bp, dh, dhl, donorInserted: !baseIsNew);
        }

        if (baseIsNew)
        {
            if (donorXml.Length > 0)
            {
                PlaceAt(bp, bh, donorXml, leadingObjects);
            }

            if (bl > 0)
            {
                MarkRange(bp, bh, bhl);
            }
        }
        else
        {
            if (donorXml.Length > 0)
            {
                PlaceAt(bp, bh + bhl, donorXml, leadingObjects);
            }

            if (bl > 0)
            {
                MarkRange(bp, bh, bhl);
            }
        }
    }

    /// <summary>The donor's runs for a stretch of its paragraph, as deleted (or inserted) content with the real author.</summary>
    private string Harvest(MarkupNode scratch, MarkupNode basePara, int offset, int length, bool donorInserted)
    {
        d.Marker.DeleteText(scratch, offset, length);
        leadingObjects = false;
        var sb = new StringBuilder();
        var found = new List<MarkupNode>(MarkerElements(scratch, "del"));
        foreach (MarkupNode w in found)
        {
            if (harvested.Contains(w))
            {
                continue;
            }

            harvested.Add(w);
            leadingObjects |= StripEdgeObjects(w, keep: false);
            // The runs of a hyperlink (or smart tag) sit inside that element, and their link is part of what reject-all must give back, so
            // the harvested revision keeps a shallow copy of each wrapper; relationships are mapped once per wrapper.
            MarkupNode top = w;
            var wrappers = new List<MarkupNode>();
            while (!top.Parent.IsNull && !Wx.Is(top.Parent, "p"))
            {
                top = top.Parent;
                wrappers.Add(top);
            }

            if (fixedTops.Add(top))
            {
                importer.Fix(top, d.PartNameOf(scratch), b.PartNameOf(basePara));
            }

            string xml = w.OuterXml;
            if (donorInserted)
            {
                xml = Wx.Rename(xml, wp, "del", "ins");
                xml = Wx.Rename(xml, wp, "delText", "t");
                xml = Wx.Rename(xml, wp, "delInstrText", "instrText");
            }

            xml = WithAuthor(xml);
            foreach (MarkupNode wrap in wrappers)
            {
                string open = wrap.OuterXml;
                open = open[..(open.IndexOf('>', StringComparison.Ordinal) + 1)];
                xml = open + xml + "</" + wrap.Name + ">";
            }

            sb.Append(xml);
        }

        return sb.ToString();
    }

    private readonly HashSet<MarkupNode> harvested = [];
    private readonly HashSet<MarkupNode> fixedTops = [];
    private bool leadingObjects;

    /// <summary>Puts revision XML into a base paragraph at a paragraph offset.</summary>
    private void PlaceAt(MarkupNode bp, int offset, string xml, bool afterObjects)
    {
        b.Marker.InsertText(bp, offset, "x");
        MarkupNode marker = default;
        foreach (MarkupNode m in MarkerElements(bp, "ins"))
        {
            marker = m;
            break;
        }

        if (marker.IsNull)
        {
            throw new ReplaceNeeded("the insertion point was not found");
        }

        // Text of the paragraph cannot lie inside an inline content control (its text is a separate node), so a point at the edge of
        // one belongs outside it.
        MarkupNode content = marker.Parent;
        if (!content.IsNull && Wx.Is(content, "sdtContent") && !content.Parent.IsNull && Wx.Is(content.Parent.Parent, "p"))
        {
            MarkupNode sdt = content.Parent;
            if (Wx.NextElement(marker).IsNull)
            {
                sdt.InsertXmlAfter(Wx.Bytes(xml));
                marker.Remove();
                return;
            }

            if (Wx.PreviousElement(marker).IsNull)
            {
                sdt.InsertXmlBefore(Wx.Bytes(xml));
                marker.Remove();
                return;
            }
        }

        // The same holds for a simple field, whose runs are its result.
        if (!marker.Parent.IsNull && Wx.Is(marker.Parent, "fldSimple"))
        {
            MarkupNode field = marker.Parent;
            if (Wx.PreviousElement(marker).IsNull)
            {
                field.InsertXmlBefore(Wx.Bytes(xml));
            }
            else
            {
                field.InsertXmlAfter(Wx.Bytes(xml));
            }

            marker.Remove();
            return;
        }

        // A field's result is part of the field object, so a point at its edge belongs before its start or after its end.
        if (FieldEdge(marker, out MarkupNode beforeRun, out bool atStart))
        {
            if (atStart)
            {
                beforeRun.InsertXmlBefore(Wx.Bytes(xml));
            }
            else
            {
                beforeRun.InsertXmlAfter(Wx.Bytes(xml));
            }

            marker.Remove();
            return;
        }

        if (afterObjects)
        {
            // The other document has an object just before this text, and the matching object here sits at the same point.
            MarkupNode last = marker;
            while (!Wx.NextElement(last).IsNull && IsObjectRun(Wx.NextElement(last)))
            {
                last = Wx.NextElement(last);
            }

            if (last != marker)
            {
                last.InsertXmlAfter(Wx.Bytes(xml));
                marker.Remove();
                return;
            }
        }

        MarkupNode parent = marker.Parent;
        if (!parent.IsNull && (Wx.Is(parent, "hyperlink") || Wx.Is(parent, "smartTag")))
        {
            // The other document's runs are not part of this hyperlink, so it is split around them.
            var before = new StringBuilder();
            var after = new StringBuilder();
            bool seen = false;
            for (MarkupNode c = parent.FirstChild; !c.IsNull; c = c.NextSibling)
            {
                if (c == marker)
                {
                    seen = true;
                    continue;
                }

                (seen ? after : before).Append(c.OuterXml);
            }

            string whole = parent.OuterXml;
            string open = whole[..(whole.IndexOf('>', StringComparison.Ordinal) + 1)], close = "</" + parent.Name + ">";
            parent.ReplaceWithXml(Wx.Bytes((before.Length > 0 ? open + before + close : "") + xml + (after.Length > 0 ? open + after + close : "")));
            return;
        }

        marker.ReplaceWithXml(Wx.Bytes(xml));
    }

    /// <summary>
    /// Finds whether a marker sits inside a complex field (ECMA-376 §17.16.18: begin, instruction, separate, result, end) of the same
    /// paragraph level. When it does, <paramref name="run"/> is the run to put the new content before (the field's begin, when the
    /// marker is at the start of the result) or after (the field's end, when it is at the end).
    /// </summary>
    private static bool FieldEdge(MarkupNode marker, out MarkupNode run, out bool atStart)
    {
        run = default;
        atStart = true;
        var siblings = new List<MarkupNode>(Wx.ChildElements(marker.Parent));
        int at = siblings.IndexOf(marker);
        int depth = 0, outerBegin = -1;
        for (int i = 0; i < at; i++)
        {
            string? t = FieldCharType(siblings[i]);
            if (t == "begin")
            {
                if (depth++ == 0)
                {
                    outerBegin = i;
                }
            }
            else if (t == "end" && depth > 0)
            {
                depth--;
            }
        }

        if (depth == 0 || outerBegin < 0)
        {
            return false;
        }

        // At the end of the result when the next run closes the field; otherwise at its start.
        if (at + 1 < siblings.Count && FieldCharType(siblings[at + 1]) == "end" && depth == 1 && FieldCharType(siblings[at - 1]) != "separate")
        {
            run = siblings[at + 1];
            atStart = false;
            return true;
        }

        run = siblings[outerBegin];
        return true;
    }

    private static string? FieldCharType(MarkupNode e)
    {
        if (!Wx.Is(e, "r"))
        {
            return null;
        }

        foreach (MarkupNode c in Wx.ChildElements(e))
        {
            if (Wx.Is(c, "fldChar"))
            {
                return Wx.Attr(c, "fldCharType");
            }
        }

        return null;
    }

    /// <summary>Marks a stretch of base text as inserted (base is new) or deleted (base is old).</summary>
    private void MarkRange(MarkupNode bp, int offset, int length)
    {
        b.Marker.DeleteText(bp, offset, length);
        foreach (MarkupNode w in new List<MarkupNode>(MarkerElements(bp, "del")))
        {
            StripEdgeObjects(w, keep: true);
        }

        ConvertMarkers(bp, toInserted: baseIsNew);
    }

    /// <summary>
    /// Objects (pictures, text boxes, note references) have no text, so an edit that starts or ends next to one takes its run into the
    /// revision. Edits never include an object (those paragraphs are replaced as a whole), so the object runs at either end of a
    /// revision are put back outside it (<paramref name="keep"/>) or left out of the copy (not <paramref name="keep"/>).
    /// </summary>
    private static bool StripEdgeObjects(MarkupNode wrapper, bool keep)
    {
        bool leading = false;
        while (true)
        {
            MarkupNode first = FirstElement(wrapper);
            if (first.IsNull || !IsObjectRun(first))
            {
                break;
            }

            leading = true;
            if (keep)
            {
                wrapper.InsertXmlBefore(Wx.Bytes(first.OuterXml));
            }

            first.Remove();
        }

        while (true)
        {
            MarkupNode last = wrapper.LastChild;
            while (!last.IsNull && last.Kind != MarkupNodeKind.Element)
            {
                last = last.PreviousSibling;
            }

            if (last.IsNull || !IsObjectRun(last))
            {
                break;
            }

            if (keep)
            {
                wrapper.InsertXmlAfter(Wx.Bytes(last.OuterXml));
            }

            last.Remove();
        }

        return leading;
    }

    private static bool IsObjectRun(MarkupNode e)
    {
        if (!Wx.Is(e, "r"))
        {
            return false;
        }

        bool obj = false;
        foreach (MarkupNode d in Wx.Descendants(e))
        {
            if (!Wx.InMain(d))
            {
                continue;
            }

            switch (d.LocalNameString)
            {
                case "drawing" or "pict" or "object" or "footnoteReference" or "endnoteReference":
                    obj = true;
                    break;
                case "t" or "delText" or "tab" or "br" or "cr" or "noBreakHyphen" or "sym" or "fldChar" or "instrText" or "delInstrText":
                    // Text inside a drawing or text box belongs to the object, not to the run.
                    bool inside = false;
                    for (MarkupNode a = d.Parent; !a.IsNull && a != e; a = a.Parent)
                    {
                        inside |= Wx.Is(a, "txbxContent") || Wx.Is(a, "drawing") || Wx.Is(a, "pict") || Wx.Is(a, "object");
                    }

                    if (!inside)
                    {
                        return false;
                    }

                    break;
                default:
                    break;
            }
        }

        return obj;
    }

    // ------------------------------------------------------------------------------------------------------------------ properties

    private static string StripNamespaces(string xml)
    {
        var sb = new StringBuilder(xml.Length);
        int i = 0;
        while (i < xml.Length)
        {
            int at = xml.IndexOf(" xmlns", i, StringComparison.Ordinal);
            if (at < 0)
            {
                sb.Append(xml, i, xml.Length - i);
                break;
            }

            sb.Append(xml, i, at - i);
            int eq = xml.IndexOf('=', at);
            int q = eq < 0 ? -1 : xml.IndexOf(xml[eq + 1], eq + 2);
            if (q < 0)
            {
                sb.Append(xml, at, xml.Length - at);
                break;
            }

            i = q + 1;
        }

        return sb.ToString();
    }

    private static string Signature(MarkupNode props, string[] keep)
    {
        if (props.IsNull)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (MarkupNode c in Wx.ChildElements(props))
        {
            if (Wx.InMain(c) && Array.IndexOf(keep, c.LocalNameString) >= 0)
            {
                continue;
            }

            sb.Append(StripNamespaces(c.OuterXml));
        }

        return sb.ToString();
    }

    private static string Content(MarkupNode props, string[] keep, bool keepOnly)
    {
        if (props.IsNull)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (MarkupNode c in Wx.ChildElements(props))
        {
            bool isKeep = Wx.InMain(c) && Array.IndexOf(keep, c.LocalNameString) >= 0;
            if (isKeep == keepOnly)
            {
                sb.Append(c.OuterXml);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Records a change of a property container (paragraph, table, row, cell or section properties): the base container keeps (or gets)
    /// the properties of the version it stands for after accept-all, and the other version's properties are written as the
    /// <c>…Change</c> record, which reject-all restores (ECMA-376 §17.13.5.29, .32, .34, .36, .37).
    /// </summary>
    private void PropertyChange(MarkupNode bEl, MarkupNode dEl, string propsLocal, string changeLocal, string[] keep, bool insertAtStart)
    {
        MarkupNode bProps = Wx.Child(bEl, propsLocal), dProps = Wx.Child(dEl, propsLocal);
        if (Signature(bProps, keep) == Signature(dProps, keep))
        {
            return;
        }

        string donorContent = Content(dProps, keep, false);
        string baseContent = Content(bProps, keep, false);
        string baseKeep = Content(bProps, keep, true);
        string id = NextId().ToString(CultureInfo.InvariantCulture);
        string attrs = " " + wp + ":id=\"" + id + "\" " + wp + ":author=\"" + authorXml + "\"" + dateXml;
        // The change record holds the version that reject-all must give back; the container holds the one accept-all keeps.
        string record = baseIsNew ? donorContent : baseContent;
        string current = baseIsNew ? baseContent : donorContent;
        string change = "<" + wp + ":" + changeLocal + attrs + "><" + wp + ":" + propsLocal + ">" + record + "</" + wp + ":" + propsLocal + "></" + wp + ":" + changeLocal + ">";
        string props = "<" + wp + ":" + propsLocal + ">" + current + baseKeep + change + "</" + wp + ":" + propsLocal + ">";
        if (bProps.IsNull)
        {
            if (insertAtStart && !bEl.FirstChild.IsNull)
            {
                bEl.PrependXml(Wx.Bytes(props));
            }
            else
            {
                bEl.AppendXml(Wx.Bytes(props));
            }

            ImportFix(Wx.Child(bEl, propsLocal), dEl);
        }
        else
        {
            bProps.ReplaceWithXml(Wx.Bytes(props));
            ImportFix(Wx.Child(bEl, propsLocal), dEl);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ moves

    /// <summary>
    /// Wraps the content of a paragraph in a move range (ECMA-376 §17.13.5.22/.25): range start and end markers that share an id and a
    /// name, the runs inside <c>w:moveFrom</c> or <c>w:moveTo</c>, and the paragraph mark marked the same way.
    /// </summary>
    private void WrapMove(MarkupNode p, string wrapper, string name)
    {
        string rangeId = NextId().ToString(CultureInfo.InvariantCulture);
        SimpleFieldsToComplex(p);
        WrapRunGroups(p, wrapper);
        string at = " " + wp + ":id=\"" + rangeId + "\" " + wp + ":author=\"" + authorXml + "\"" + dateXml;
        string start = "<" + wp + ":" + wrapper + "RangeStart" + at + " " + wp + ":name=\"" + name + "\"/>";
        string end = "<" + wp + ":" + wrapper + "RangeEnd " + wp + ":id=\"" + rangeId + "\"/>";
        MarkupNode pPr = Wx.Child(p, "pPr");
        if (pPr.IsNull)
        {
            p.PrependXml(Wx.Bytes(start));
        }
        else
        {
            pPr.InsertXmlAfter(Wx.Bytes(start));
        }

        p.AppendXml(Wx.Bytes(end));
        MarkParagraphMark(p, wrapper);
    }

    /// <summary>Wraps each stretch of runs under a container in the move wrapper, looking inside hyperlinks, whose runs cannot be wrapped from outside.</summary>
    private void WrapRunGroups(MarkupNode container, string wrapper)
    {
        var children = new List<MarkupNode>(Wx.ChildElements(container));
        int i = 0;
        while (i < children.Count)
        {
            if (Wx.Is(children[i], "hyperlink") || Wx.Is(children[i], "smartTag"))
            {
                WrapRunGroups(children[i], wrapper);
                i++;
                continue;
            }

            if (!IsWrappable(children[i]))
            {
                i++;
                continue;
            }

            var g = new List<MarkupNode>();
            while (i < children.Count && IsWrappable(children[i]))
            {
                g.Add(children[i++]);
            }

            var sb = new StringBuilder();
            sb.Append('<').Append(wp).Append(':').Append(wrapper);
            sb.Append(' ').Append(wp).Append(":id=\"").Append(NextId().ToString(CultureInfo.InvariantCulture)).Append("\" ").Append(wp).Append(":author=\"").Append(authorXml).Append('"').Append(dateXml).Append('>');
            foreach (MarkupNode n in g)
            {
                sb.Append(n.OuterXml);
            }

            sb.Append("</").Append(wp).Append(':').Append(wrapper).Append('>');
            g[0].ReplaceWithXml(Wx.Bytes(sb.ToString()));
            for (int k = 1; k < g.Count; k++)
            {
                g[k].Remove();
            }
        }
    }

    private static bool IsWrappable(MarkupNode e) => Wx.Is(e, "r") || Wx.Is(e, "ins") || Wx.Is(e, "del") || Wx.Is(e, "sdt");

    private void MarkParagraphMark(MarkupNode p, string local)
    {
        string mark = "<" + wp + ":" + local + " " + wp + ":id=\"" + NextId().ToString(CultureInfo.InvariantCulture) + "\" " + wp + ":author=\"" + authorXml + "\"" + dateXml + "/>";
        MarkupNode pPr = Wx.Child(p, "pPr");
        if (pPr.IsNull)
        {
            p.PrependXml(Wx.Bytes("<" + wp + ":pPr><" + wp + ":rPr>" + mark + "</" + wp + ":rPr></" + wp + ":pPr>"));
            return;
        }

        MarkupNode rPr = Wx.Child(pPr, "rPr");
        if (rPr.IsNull)
        {
            string xml = "<" + wp + ":rPr>" + mark + "</" + wp + ":rPr>";
            MarkupNode before = default;
            foreach (MarkupNode c in Wx.ChildElements(pPr))
            {
                if (Wx.Is(c, "sectPr") || Wx.Is(c, "pPrChange"))
                {
                    before = c;
                    break;
                }
            }

            if (before.IsNull)
            {
                pPr.AppendXml(Wx.Bytes(xml));
            }
            else
            {
                before.InsertXmlBefore(Wx.Bytes(xml));
            }
        }
        else
        {
            rPr.PrependXml(Wx.Bytes(mark));
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ tables and sections

    private void TablePair(int bid, int did, NodeKind kind)
    {
        MarkupNode be = b.Element(bid), de = d.Element(did);
        if (be.IsNull || de.IsNull)
        {
            return;
        }

        switch (kind)
        {
            case NodeKind.Table:
                PropertyChange(be, de, "tblPr", "tblPrChange", ["tblPrChange"], insertAtStart: true);
                break;
            case NodeKind.TableRow:
                PropertyChange(be, de, "trPr", "trPrChange", ["ins", "del", "trPrChange"], insertAtStart: false);
                break;
            default:
                PropertyChange(be, de, "tcPr", "tcPrChange", ["cellIns", "cellDel", "cellMerge", "tcPrChange"], insertAtStart: true);
                break;
        }
    }

    private void SectionPair(int bid, int did)
    {
        MarkupNode be = b.Element(bid), de = d.Element(did);
        if (be.IsNull || de.IsNull || !Wx.Is(be, "sectPr") || !Wx.Is(de, "sectPr"))
        {
            return;
        }

        if (Signature(be, ["headerReference", "footerReference", "sectPrChange"]) == Signature(de, ["headerReference", "footerReference", "sectPrChange"]))
        {
            return;
        }

        string donorContent = Content(de, ["headerReference", "footerReference", "sectPrChange"], false);
        string baseContent = Content(be, ["headerReference", "footerReference", "sectPrChange"], false);
        string refs = Content(be, ["headerReference", "footerReference"], true);
        string attrs = " " + wp + ":id=\"" + NextId().ToString(CultureInfo.InvariantCulture) + "\" " + wp + ":author=\"" + authorXml + "\"" + dateXml;
        string record = baseIsNew ? donorContent : baseContent;
        string current = baseIsNew ? baseContent : donorContent;
        string xml = "<" + wp + ":sectPr>" + refs + current + "<" + wp + ":sectPrChange" + attrs + "><" + wp + ":sectPr>" + record + "</" + wp + ":sectPr></" + wp + ":sectPrChange></" + wp + ":sectPr>";
        be.ReplaceWithXml(Wx.Bytes(xml));
    }

    // ------------------------------------------------------------------------------------------------------------------ ids

    /// <summary>
    /// Numbers every revision and move-range element of the base document 1, 2, … in document order, so that identifiers depend on
    /// the document and not on the order the edits were made in. A range end takes the number of the range start it shares an id with.
    /// </summary>
    private void RenumberRevisions()
    {
        string[] revision =
        [
            "ins", "del", "moveFrom", "moveTo", "rPrChange", "pPrChange", "sectPrChange", "tblPrChange", "tblPrExChange", "tblGridChange",
            "trPrChange", "tcPrChange", "cellIns", "cellDel", "cellMerge", "numberingChange",
        ];
        int next = 1;
        var starts = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>(b.PartNames);
        names.Sort(StringComparer.Ordinal);
        foreach (string part in names)
        {
            MarkupNode root = b.Root(part);
            if (root.IsNull)
            {
                continue;
            }

            foreach (MarkupNode e in Wx.Descendants(root))
            {
                if (!Wx.InMain(e))
                {
                    continue;
                }

                string ln = e.LocalNameString;
                bool isStart = ln is "moveFromRangeStart" or "moveToRangeStart";
                bool isEnd = ln is "moveFromRangeEnd" or "moveToRangeEnd";
                if (!isStart && !isEnd && Array.IndexOf(revision, ln) < 0)
                {
                    continue;
                }

                string? old = Wx.Attr(e, "id");
                if (old is null)
                {
                    continue;
                }

                string key = (isStart ? ln[..^"Start".Length] : isEnd ? ln[..^"End".Length] : ln) + ":" + old;
                int value;
                if (isEnd)
                {
                    if (!starts.TryGetValue(key, out value))
                    {
                        value = next++;
                    }
                }
                else
                {
                    value = next++;
                    if (isStart)
                    {
                        starts[key] = value;
                    }
                }

                e.SetAttribute(e.NamespaceUri, "id", value.ToString(CultureInfo.InvariantCulture), wp);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ paragraph marks that cannot go

    /// <summary>
    /// A paragraph mark joins its paragraph to the next one when accepted (deleted) or rejected (inserted), which is impossible when
    /// the next block is not a paragraph (a table, a content control) or there is none (the end of a cell). Word keeps such a mark and
    /// so does the hub, which would leave an empty paragraph behind. As Word does when Enter is pressed at the end of a paragraph, the
    /// mark is carried by the paragraph before instead, and the properties the joined paragraph must end up with are exchanged
    /// through a paragraph-property change (ECMA-376 §17.13.5.29).
    /// </summary>
    private void FixTailMarks()
    {
        var marked = new Dictionary<MarkupNode, bool>();
        foreach ((MarkupNode p, bool inserted, _) in markedParagraphs)
        {
            marked[p] = inserted;
        }

        foreach ((MarkupNode p, bool inserted, string local) in markedParagraphs)
        {
            if (p.Parent.IsNull)
            {
                continue;
            }

            MarkupNode pPr = Wx.Child(p, "pPr");
            if (!Wx.Child(pPr, "sectPr").IsNull)
            {
                TailNote(inserted, "the paragraph holds a section break, which keeps its mark");
                continue;
            }

            MarkupNode next = Wx.NextElement(p);
            if (!next.IsNull && Wx.Is(next, "p"))
            {
                continue;
            }

            if (local is "ins" or "del" && merged.Add(p))
            {
                MarkupNode o = Wx.PreviousElement(p);
                while (!o.IsNull && Wx.Is(o, "p") && marked.TryGetValue(o, out bool oi) && oi == inserted)
                {
                    o = Wx.PreviousElement(o);
                }

                if (!o.IsNull && Wx.Is(o, "p") && marked.TryGetValue(o, out bool opposite) && opposite != inserted && IsPlainMark(o, !inserted) && Wx.Child(Wx.Child(o, "pPr"), "sectPr").IsNull)
                {
                    MergeTail(p, o, inserted);
                    marked.Remove(o);
                    marked.Remove(p);
                    continue;
                }
            }

            MarkupNode q = Wx.PreviousElement(p);
            while (!q.IsNull && Wx.Is(q, "p") && marked.TryGetValue(q, out bool qi) && qi == inserted)
            {
                q = Wx.PreviousElement(q);
            }

            if (q.IsNull || !Wx.Is(q, "p") || marked.ContainsKey(q) || HasMarkRevision(q) || !Wx.Child(Wx.Child(q, "pPr"), "sectPr").IsNull || !Wx.Child(Wx.Child(q, "pPr"), "pPrChange").IsNull)
            {
                TailNote(inserted, q.IsNull || !Wx.Is(q, "p") ? "no untracked paragraph before it in the same container to take its mark" : "the paragraph before it cannot take its mark (it is tracked or holds a section break)");
                continue;
            }

            TransferMark(p, q, inserted, local);
        }
    }

    private readonly HashSet<MarkupNode> merged = [];

    /// <summary>
    /// Records a paragraph mark that has to go on one resolution but cannot: the paragraph before a table or content control, at the end
    /// of a container, or holding a section break keeps its mark, so an empty paragraph is left behind (see <see cref="FixTailMarks"/>).
    /// </summary>
    private void TailNote(bool inserted, string why) =>
        Note(inserted ? "paragraph-mark-reject" : "paragraph-mark-accept", "an emptied paragraph is left after " + (inserted ? "reject-all" : "accept-all") + ": " + why);

    /// <summary>True when the paragraph's mark carries a plain insertion (<paramref name="inserted"/>) or deletion mark.</summary>
    private static bool IsPlainMark(MarkupNode p, bool inserted)
    {
        MarkupNode rPr = Wx.Child(Wx.Child(p, "pPr"), "rPr");
        return !rPr.IsNull && !Wx.Child(rPr, inserted ? "ins" : "del").IsNull;
    }

    /// <summary>
    /// At the end of a container the last paragraph cannot lose its mark, and what survives is different after accept-all (the inserted
    /// paragraph) and reject-all (the deleted one). So the two become one paragraph: the deleted text, then the inserted text, with the
    /// inserted paragraph's properties and a property change that restores the deleted paragraph's.
    /// </summary>
    private void MergeTail(MarkupNode p, MarkupNode o, bool inserted)
    {
        MarkupNode insP = inserted ? p : o, delP = inserted ? o : p;
        string[] keep = ["rPr", "sectPr", "pPrChange"];
        string insProps = Content(Wx.Child(insP, "pPr"), keep, false);
        string delProps = Content(Wx.Child(delP, "pPr"), keep, false);
        MarkupNode rPr = Wx.Child(Wx.Child(insP, "pPr"), "rPr");
        foreach (MarkupNode c in Wx.ChildElements(rPr).Where(c => Wx.InMain(c) && c.LocalNameString == "ins").ToList())
        {
            c.Remove();
        }

        string rPrXml = !rPr.IsNull && Wx.ChildElements(rPr).Any() ? rPr.OuterXml : "";
        var content = new StringBuilder();
        foreach (MarkupNode para in new[] { delP, insP })
        {
            for (MarkupNode c = para.FirstChild; !c.IsNull; c = c.NextSibling)
            {
                if (c.Kind == MarkupNodeKind.Element && !Wx.Is(c, "pPr"))
                {
                    content.Append(c.OuterXml);
                }
            }
        }

        string change = "";
        if (StripNamespaces(insProps) != StripNamespaces(delProps))
        {
            string attrs = " " + wp + ":id=\"" + NextId().ToString(CultureInfo.InvariantCulture) + "\" " + wp + ":author=\"" + authorXml + "\"" + dateXml;
            change = "<" + wp + ":pPrChange" + attrs + "><" + wp + ":pPr>" + delProps + "</" + wp + ":pPr></" + wp + ":pPrChange>";
        }

        string pPrXml = insProps.Length + rPrXml.Length + change.Length == 0 ? "" : "<" + wp + ":pPr>" + insProps + rPrXml + change + "</" + wp + ":pPr>";
        p.ReplaceWithXml(Wx.Bytes("<" + wp + ":p>" + pPrXml + content + "</" + wp + ":p>"));
        o.Remove();
    }

    private static bool HasMarkRevision(MarkupNode p)
    {
        MarkupNode rPr = Wx.Child(Wx.Child(p, "pPr"), "rPr");
        if (rPr.IsNull)
        {
            return false;
        }

        foreach (MarkupNode c in Wx.ChildElements(rPr))
        {
            if (Wx.InMain(c) && c.LocalNameString is "ins" or "del" or "moveFrom" or "moveTo")
            {
                return true;
            }
        }

        return false;
    }

    private void TransferMark(MarkupNode p, MarkupNode q, bool inserted, string local)
    {
        string[] keep = ["rPr", "sectPr", "pPrChange"];
        string qProps = Content(Wx.Child(q, "pPr"), keep, false);
        MarkupNode pPr = Wx.Child(p, "pPr");
        string pProps = Content(pPr, keep, false);
        // take the revision mark off p ...
        MarkupNode rPr = Wx.Child(pPr, "rPr");
        foreach (MarkupNode c in Wx.ChildElements(rPr).Where(c => Wx.InMain(c) && c.LocalNameString == local).ToList())
        {
            c.Remove();
        }

        // ... and put it on q.
        MarkParagraphMark(q, local);
        if (StripNamespaces(qProps) == StripNamespaces(pProps))
        {
            return;
        }

        string attrs = " " + wp + ":id=\"" + NextId().ToString(CultureInfo.InvariantCulture) + "\" " + wp + ":author=\"" + authorXml + "\"" + dateXml;
        string record = inserted ? qProps : pProps;
        string current = inserted ? pProps : qProps;
        string change = "<" + wp + ":pPrChange" + attrs + "><" + wp + ":pPr>" + record + "</" + wp + ":pPr></" + wp + ":pPrChange>";
        string rPrXml = Wx.Child(Wx.Child(p, "pPr"), "rPr") is { IsNull: false } r ? r.OuterXml : "";
        string xml = "<" + wp + ":pPr>" + current + rPrXml + change + "</" + wp + ":pPr>";
        Wx.Child(p, "pPr").ReplaceWithXml(Wx.Bytes(xml));
    }
}
