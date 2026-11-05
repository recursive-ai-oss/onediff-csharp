using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneDocument.Documents;
using RecursiveAI.OneDocument.Documents.Notes;
using RecursiveAI.OneDocument.Documents.Styles;
using RecursiveAI.OneDocument.Documents.Numbering;
using RecursiveAI.OneDocument.Markup;
using RecursiveAI.OneDocument.Packaging;

namespace OneDiff.Word.Internal;

/// <summary>
/// Makes WordprocessingML copied from one document stand on its own in another document's package: relationships (pictures, hyperlinks)
/// are copied or reused, style and list references are mapped to the target's own definitions (copying a definition the target does not
/// have), and notes are copied with a fresh id. What the compare model does not hold (comment anchors, bookmarks) is dropped.
/// </summary>
internal sealed class TrackedImporter
{
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string RelNsStrict = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    private readonly TrackedSide from;
    private readonly TrackedSide to;
    private readonly IOpcPackage fromPkg;
    private readonly IOpcPackage toPkg;
    private readonly Dictionary<string, string> rels = [];
    private readonly Dictionary<string, string> styles = [];
    private readonly Dictionary<string, string> nums = [];
    private readonly Dictionary<string, string> notes = [];
    private readonly Dictionary<string, string> mediaByHash = [];
    private int partCounter;
    private int relCounter;

    public TrackedImporter(TrackedSide from, TrackedSide to)
    {
        this.from = from;
        this.to = to;
        fromPkg = from.Doc.Package;
        toPkg = to.Doc.Package;
    }

    /// <summary>Problems found while importing (a relationship that could not be copied, ...).</summary>
    public List<string> Problems { get; } = [];

    /// <summary>How many comment references were left out of copies (a comment cannot be added by a tracked change).</summary>
    public int CommentsDropped { get; private set; }

    private HashSet<long>? drawingIds;
    private long maxDrawingId;

    /// <summary>
    /// Gives a copied drawing an identifier no drawing of the document has (the <c>id</c> of <c>wp:docPr</c> must be unique in a
    /// document, ECMA-376 §20.4.2.5).
    /// </summary>
    private long FreshDrawingId(long id)
    {
        if (drawingIds is null)
        {
            drawingIds = [];
            foreach (string part in to.PartNames)
            {
                MarkupNode root = to.Root(part);
                if (root.IsNull)
                {
                    continue;
                }

                foreach (MarkupNode e in Wx.Descendants(root))
                {
                    if (e.LocalNameString == "docPr" && e.GetAttribute("id") is { } v && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
                    {
                        drawingIds.Add(n);
                        maxDrawingId = Math.Max(maxDrawingId, n);
                    }
                }
            }
        }

        if (id > 0 && drawingIds.Add(id))
        {
            maxDrawingId = Math.Max(maxDrawingId, id);
            return id;
        }

        drawingIds.Add(++maxDrawingId);
        return maxDrawingId;
    }

    public void Fix(MarkupNode copy, string fromPart, string toPart)
    {
        var drop = new List<MarkupNode>();
        var edits = new List<(MarkupNode El, string Ns, string Local, string Value)>();
        var all = new List<MarkupNode>(Wx.Descendants(copy)) { copy };
        foreach (MarkupNode e in all)
        {
            if (e.LocalNameString == "docPr" && e.GetAttribute("id") is not null)
            {
                e.SetAttribute("id", FreshDrawingId(0).ToString(CultureInfo.InvariantCulture));
            }

            if (Wx.InMain(e))
            {
                string ln = e.LocalNameString;
                switch (ln)
                {
                    case "commentRangeStart" or "commentRangeEnd" or "commentReference" or "bookmarkStart" or "bookmarkEnd":
                        if (ln == "commentReference")
                        {
                            CommentsDropped++;
                        }

                        drop.Add(e);
                        continue;
                    case "pStyle" or "rStyle" or "tblStyle":
                        if (Wx.Attr(e, "val") is { } sid)
                        {
                            edits.Add((e, e.NamespaceUri, "val", MapStyle(sid)));
                        }

                        break;
                    case "numId":
                        if (Wx.Attr(e, "val") is { } nid && e.Parent is { IsNull: false } np && Wx.Is(np, "numPr"))
                        {
                            edits.Add((e, e.NamespaceUri, "val", MapNum(nid)));
                        }

                        break;
                    case "footnoteReference" or "endnoteReference":
                        if (Wx.Attr(e, "id") is { } fid)
                        {
                            edits.Add((e, e.NamespaceUri, "id", MapNote(ln == "footnoteReference", fid)));
                        }

                        break;
                    default:
                        break;
                }
            }

            foreach (MarkupAttributeRef a in e.Attributes)
            {
                if (a.IsNamespaceDeclaration)
                {
                    continue;
                }

                string ns = a.NamespaceUri;
                if (ns == RelNs || ns == RelNsStrict)
                {
                    string local = Encoding.UTF8.GetString(a.LocalName);
                    string value = a.Value;
                    if (value.Length > 0)
                    {
                        edits.Add((e, ns, local, MapRelationship(fromPart, toPart, value)));
                    }
                }
            }
        }

        foreach (var (el, ns, local, value) in edits)
        {
            el.SetAttribute(ns, local, value);
        }

        foreach (MarkupNode e in drop)
        {
            e.Remove();
        }
    }

    // ---- relationships ------------------------------------------------------------------------------------------------------------

    private string MapRelationship(string fromPart, string toPart, string id)
    {
        string key = fromPart + "|" + toPart + "|" + id;
        if (rels.TryGetValue(key, out string? known))
        {
            return known;
        }

        Relationship? src = null;
        foreach (Relationship r in fromPkg.GetRelationships(fromPart))
        {
            if (r.Id == id)
            {
                src = r;
                break;
            }
        }

        if (src is null)
        {
            rels[key] = id;
            return id;
        }

        List<Relationship> existing = [.. toPkg.GetRelationships(toPart)];
        string target = src.Target;
        if (src.TargetMode == RelationshipTargetMode.Internal)
        {
            if (!PartUri.TryResolve(new PartUri(fromPart), src.Target, out PartUri uri) || fromPkg.FindPart(uri.Value) is not { } part)
            {
                Problems.Add("a relationship target is missing: " + src.Target);
                rels[key] = id;
                return id;
            }

            if (fromPkg.GetRelationships(part.Name).Count > 0)
            {
                Problems.Add("a related part has relationships of its own: " + part.Name);
            }

            byte[] bytes = part.GetBytes().ToArray();
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            string name;
            if (!mediaByHash.TryGetValue(part.ContentType + "|" + hash, out string? have))
            {
                name = FreshPartName(part.Name);
                toPkg.AddPart(name, part.ContentType, bytes);
                mediaByHash[part.ContentType + "|" + hash] = name;
            }
            else
            {
                name = have;
            }

            target = RelativeTarget(toPart, name);
        }
        else
        {
            foreach (Relationship r in existing)
            {
                if (r.Type == src.Type && r.Target == src.Target && r.TargetMode == src.TargetMode)
                {
                    rels[key] = r.Id;
                    return r.Id;
                }
            }
        }

        if (src.TargetMode == RelationshipTargetMode.Internal)
        {
            foreach (Relationship r in existing)
            {
                if (r.Type == src.Type && r.Target == target)
                {
                    rels[key] = r.Id;
                    return r.Id;
                }
            }
        }

        string newId;
        do
        {
            newId = "rIdOd" + (++relCounter).ToString(CultureInfo.InvariantCulture);
        }
        while (existing.Exists(r => r.Id == newId));
        existing.Add(new Relationship(newId, src.Type, target, src.TargetMode));
        toPkg.SetRelationships(toPart, existing);
        rels[key] = newId;
        return newId;
    }

    private string FreshPartName(string donorName)
    {
        int dot = donorName.LastIndexOf('.');
        string ext = dot < 0 ? "" : donorName[dot..];
        string dir = donorName[..(donorName.LastIndexOf('/') + 1)];
        string name;
        do
        {
            name = dir + "onediff" + (++partCounter).ToString(CultureInfo.InvariantCulture) + ext;
        }
        while (toPkg.FindPart(name) is not null);
        return name;
    }

    private static string RelativeTarget(string fromPart, string toPartName)
    {
        // Parts are siblings of /word in every case this writer meets; use an absolute target otherwise.
        string dir = fromPart[..(fromPart.LastIndexOf('/') + 1)];
        return toPartName.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ? toPartName[dir.Length..] : toPartName;
    }

    // ---- styles -------------------------------------------------------------------------------------------------------------------

    private string MapStyle(string id)
    {
        if (styles.TryGetValue(id, out string? known))
        {
            return known;
        }

        styles[id] = id;
        StyleDefinition? src = from.Doc.Styles.Find(id);
        if (src is null || src.Name is not { Length: > 0 } name)
        {
            return id;
        }

        StyleDefinition? have = to.Doc.Styles.FindByName(name);
        if (have is not null && have.Type == src.Type)
        {
            styles[id] = have.Id;
            return have.Id;
        }

        // Copy the definition (and the styles it builds on) under an id the target does not use.
        string newId = id;
        while (to.Doc.Styles.Find(newId) is not null || styles.ContainsValue(newId) && newId != id)
        {
            newId += "_od";
        }

        if (to.Doc.Styles.Find(newId) is not null)
        {
            newId = id + "_od";
        }

        styles[id] = newId;
        MarkupNode root = to.Doc.Styles.Markup.Node;
        if (root.IsNull)
        {
            Problems.Add("the target has no style sheet for " + id);
            styles[id] = id;
            return id;
        }

        string xml = src.Markup.Node.OuterXml;
        string wp = to.Prefix;
        xml = xml.Replace(wp + ":styleId=\"" + id + "\"", wp + ":styleId=\"" + newId + "\"", StringComparison.Ordinal);
        root.AppendXml(Wx.Bytes(xml));
        to.Doc.Styles.Invalidate();
        // Map the references inside the copy once it is in place.
        MarkupNode copied = root.LastChild;
        while (!copied.IsNull && copied.Kind != MarkupNodeKind.Element)
        {
            copied = copied.PreviousSibling;
        }

        if (!copied.IsNull)
        {
            var refs = new List<(MarkupNode, string, string)>();
            foreach (MarkupNode e in Wx.Descendants(copied))
            {
                if (Wx.InMain(e) && e.LocalNameString is "basedOn" or "next" or "link" && Wx.Attr(e, "val") is { } r)
                {
                    refs.Add((e, e.NamespaceUri, MapStyle(r)));
                }
            }

            foreach (var (e, ns, v) in refs)
            {
                e.SetAttribute(ns, "val", v);
            }
        }

        return newId;
    }

    // ---- numbering ----------------------------------------------------------------------------------------------------------------

    private string MapNum(string numId)
    {
        if (nums.TryGetValue(numId, out string? known))
        {
            return known;
        }

        nums[numId] = numId;
        if (!long.TryParse(numId, NumberStyles.None, CultureInfo.InvariantCulture, out long n) || n == 0)
        {
            return numId;
        }

        NumberingInstance? inst = from.Doc.Numbering.FindInstance(n);
        AbstractNumberingDefinition? abs = inst is null ? null : from.Doc.Numbering.FindAbstract(inst.AbstractNumberingId);
        if (inst is null || abs is null)
        {
            return numId;
        }

        string sig = NumberingSignature(abs.Markup.Node, inst.Markup.Node);
        foreach (NumberingInstance cand in to.Doc.Numbering.Instances)
        {
            AbstractNumberingDefinition? ca = to.Doc.Numbering.FindAbstract(cand.AbstractNumberingId);
            if (ca is not null && NumberingSignature(ca.Markup.Node, cand.Markup.Node) == sig)
            {
                nums[numId] = cand.Id.ToString(CultureInfo.InvariantCulture);
                return nums[numId];
            }
        }

        MarkupNode root = to.Doc.Numbering.Markup.Node;
        if (root.IsNull)
        {
            Problems.Add("the target has no numbering part for list " + numId);
            return numId;
        }

        long newAbs = 0, newNum = 0;
        foreach (AbstractNumberingDefinition a in to.Doc.Numbering.AbstractDefinitions)
        {
            newAbs = Math.Max(newAbs, a.Id);
        }

        foreach (NumberingInstance i in to.Doc.Numbering.Instances)
        {
            newNum = Math.Max(newNum, i.Id);
        }

        newAbs++;
        newNum++;
        string wp = to.Prefix;
        string absXml = SetAttr(abs.Markup.Node.OuterXml, wp + ":abstractNumId", abs.Id.ToString(CultureInfo.InvariantCulture), newAbs.ToString(CultureInfo.InvariantCulture));
        string numXml = SetAttr(inst.Markup.Node.OuterXml, wp + ":numId", numId, newNum.ToString(CultureInfo.InvariantCulture));
        numXml = numXml.Replace("<" + wp + ":abstractNumId " + wp + ":val=\"" + abs.Id.ToString(CultureInfo.InvariantCulture) + "\"", "<" + wp + ":abstractNumId " + wp + ":val=\"" + newAbs.ToString(CultureInfo.InvariantCulture) + "\"", StringComparison.Ordinal);
        MarkupNode firstNum = default;
        foreach (MarkupNode c in Wx.ChildElements(root))
        {
            if (Wx.Is(c, "num"))
            {
                firstNum = c;
                break;
            }
        }

        if (firstNum.IsNull)
        {
            root.AppendXml(Wx.Bytes(absXml));
        }
        else
        {
            firstNum.InsertXmlBefore(Wx.Bytes(absXml));
        }

        root.AppendXml(Wx.Bytes(numXml));
        nums[numId] = newNum.ToString(CultureInfo.InvariantCulture);
        return nums[numId];
    }

    private static string SetAttr(string xml, string attr, string oldValue, string newValue) =>
        xml.Replace(attr + "=\"" + oldValue + "\"", attr + "=\"" + newValue + "\"", StringComparison.Ordinal);

    internal static string NumberingSignature(MarkupNode abs, MarkupNode inst)
    {
        string a = Strip(abs.OuterXml, ["abstractNumId", "nsid", "tmpl"]);
        string i = Strip(inst.OuterXml, ["numId", "val"]);
        return a + "||" + i;
    }

    private static string Strip(string xml, string[] attrs)
    {
        // Compare definitions without their ids, namespace declarations or template ids.
        var sb = new StringBuilder(xml.Length);
        int pos = 0;
        while (pos < xml.Length)
        {
            int lt = xml.IndexOf('<', pos);
            if (lt < 0)
            {
                sb.Append(xml, pos, xml.Length - pos);
                break;
            }

            sb.Append(xml, pos, lt - pos);
            int gt = xml.IndexOf('>', lt);
            if (gt < 0)
            {
                sb.Append(xml, lt, xml.Length - lt);
                break;
            }

            string tag = xml[lt..(gt + 1)];
            if (!tag.StartsWith("</", StringComparison.Ordinal) && !tag.StartsWith("<?", StringComparison.Ordinal))
            {
                int sp = tag.IndexOf(' ', StringComparison.Ordinal);
                if (sp > 0)
                {
                    string name = tag[1..sp];
                    var t = new StringBuilder("<" + name);
                    foreach (string part in SplitAttrs(tag[sp..^1].TrimEnd('/')))
                    {
                        if (part.StartsWith("xmlns", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        int eq = part.IndexOf('=', StringComparison.Ordinal);
                        string an = eq < 0 ? part : part[..eq];
                        string local = an[(an.IndexOf(':', StringComparison.Ordinal) + 1)..];
                        if (name.EndsWith("abstractNum", StringComparison.Ordinal) && local == "abstractNumId")
                        {
                            continue;
                        }

                        if (name.EndsWith(":num", StringComparison.Ordinal) && local == "numId")
                        {
                            continue;
                        }

                        if (Array.IndexOf(attrs, local) >= 0 && (local is "nsid" or "tmpl"))
                        {
                            continue;
                        }

                        t.Append(' ').Append(part);
                    }

                    t.Append(tag.EndsWith("/>", StringComparison.Ordinal) ? "/>" : ">");
                    tag = t.ToString();
                }
            }

            sb.Append(tag);
            pos = gt + 1;
        }

        return sb.ToString();
    }

    private static IEnumerable<string> SplitAttrs(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && s[i] == ' ')
            {
                i++;
            }

            int eq = s.IndexOf('=', i);
            if (eq < 0)
            {
                yield break;
            }

            char q = s[eq + 1];
            int end = s.IndexOf(q, eq + 2);
            if (end < 0)
            {
                yield break;
            }

            yield return s[i..(end + 1)];
            i = end + 1;
        }
    }

    // ---- notes --------------------------------------------------------------------------------------------------------------------

    private string MapNote(bool footnote, string id)
    {
        string key = (footnote ? "f" : "e") + id;
        if (notes.TryGetValue(key, out string? known))
        {
            return known;
        }

        notes[key] = id;
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
        {
            return id;
        }

        Note? src = (footnote ? from.Doc.Footnotes : from.Doc.Endnotes).Find(n);
        if (src is null)
        {
            return id;
        }

        Note created = (footnote ? to.Doc.Footnotes : to.Doc.Endnotes).Add("");
        MarkupNode target = created.Container.Node;
        MarkupNode parent = target.Parent;

        string newId = created.Id.ToString(CultureInfo.InvariantCulture);
        string wp = to.Prefix;
        string xml = src.Container.Node.OuterXml;
        xml = SetAttr(xml, wp + ":id", id, newId);
        string partName = footnote ? "/word/footnotes.xml" : "/word/endnotes.xml";
        target.ReplaceWithXml(Wx.Bytes(xml));
        MarkupNode fresh = default;
        foreach (MarkupNode c in Wx.ChildElements(parent))
        {
            if (Wx.Is(c, footnote ? "footnote" : "endnote") && Wx.Attr(c, "id") == newId)
            {
                fresh = c;
            }
        }

        notes[key] = newId;
        if (!fresh.IsNull)
        {
            Fix(fresh, partName, partName);
        }

        return newId;
    }
}
