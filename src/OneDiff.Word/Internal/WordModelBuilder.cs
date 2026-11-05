using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using RecursiveAI.OneOffice.Documents;
using RecursiveAI.OneOffice.Documents.Comments;
using RecursiveAI.OneOffice.Documents.ContentControls;
using RecursiveAI.OneOffice.Documents.Notes;
using RecursiveAI.OneOffice.Documents.Numbering;
using RecursiveAI.OneOffice.Documents.Styles;
using RecursiveAI.OneOffice.Markup;
using RecursiveAI.OneOffice.Packaging;
using RecursiveAI.OneOffice.Schema;

namespace OneDiff.Word.Internal;

/// <summary>A piece of text being collected for one node: the raw text, its formatting runs and its anchored segments, all in raw coordinates.</summary>
internal sealed class TextBuf
{
    internal readonly record struct RawRun(int Start, int Length, FormatSet Format);

    /// <summary>A stretch of raw text that came from one element. <c>Ref</c> is set for stretches that do not come from a run of the hub.</summary>
    internal readonly record struct RawSeg(int Start, int Length, MarkupNode Element, int HubOffset, int HubLength, string? Ref);

    public readonly StringBuilder Raw = new();
    public readonly List<RawRun> Runs = [];
    public readonly List<RawSeg> Segs = [];

    public void Append(string text, FormatSet? format, MarkupNode element, int hubOffset, int hubLength)
    {
        if (text.Length == 0 && hubLength == 0)
        {
            return;
        }

        int start = Raw.Length;
        Raw.Append(text);
        if (format is { IsEmpty: false } && text.Length > 0)
        {
            Runs.Add(new RawRun(start, text.Length, format));
        }

        if (Segs.Count > 0)
        {
            RawSeg last = Segs[^1];
            if (last.Ref is null && last.Element == element && last.Start + last.Length == start && last.HubOffset + last.HubLength == hubOffset && text.Length > 0 && last.Length > 0)
            {
                Segs[^1] = last with { Length = last.Length + text.Length, HubLength = last.HubLength + hubLength };
                return;
            }
        }

        Segs.Add(new RawSeg(start, text.Length, element, hubOffset, hubLength, null));
    }

    public void AppendWhole(string text, string reference)
    {
        int start = Raw.Length;
        Raw.Append(text);
        Segs.Add(new RawSeg(start, text.Length, default, 0, text.Length, reference));
    }

    /// <summary>Appends another buffer's content (a nested field's result) at the end of this one.</summary>
    public void AppendBuffer(TextBuf other)
    {
        int shift = Raw.Length;
        Raw.Append(other.Raw);
        foreach (RawRun r in other.Runs)
        {
            Runs.Add(r with { Start = r.Start + shift });
        }

        foreach (RawSeg s in other.Segs)
        {
            Segs.Add(s with { Start = s.Start + shift });
        }
    }
}

/// <summary>Reads a Word document through the OneOffice hub into the flowing compare model (docs/word.md).</summary>
internal sealed class WordModelBuilder
{
    private const string McNamespace = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string Placeholder = "\uFFFC";
    private static readonly ITextNormalizer[] Normalizers = [ControlCharacterNormalizer.Instance, TypographicNormalizer.Instance];

    private readonly Document doc;
    private readonly CompareDocumentBuilder builder;
    private readonly WordSourceOptions options;
    private readonly CancellationToken cancellation;
    private readonly string mainPart;
    private readonly Dictionary<MarkupNode, ListLabel> labels = [];
    private readonly Dictionary<MarkupNode, ContentControl> controls = [];
    private readonly Dictionary<string, Dictionary<string, Relationship>> relationships = new(StringComparer.Ordinal);
    private string? footnotesPart;
    private string? endnotesPart;
    private string? commentsPart;
    private long characters;
    private int nodes;

    public WordModelBuilder(Document doc, CompareDocumentBuilder builder, WordSourceOptions options, CancellationToken cancellation)
    {
        this.doc = doc;
        this.builder = builder;
        this.options = options;
        this.cancellation = cancellation;
        mainPart = doc.MainPartName;
    }

    public void Build()
    {
        foreach (ListLabel l in doc.Numbering.LabelBody())
        {
            labels.TryAdd(l.Paragraph.Markup.Node, l);
        }

        foreach (ContentControl c in doc.ContentControls())
        {
            controls.TryAdd(c.Markup.Node, c);
        }

        footnotesPart = StoryPart("/footnotes");
        endnotesPart = StoryPart("/endnotes");
        commentsPart = StoryPart("/comments");

        EmitBody();
        EmitProperties();
        EmitOpaque();
    }

    // ---- structure ----------------------------------------------------------------------------------------------------------

    private void EmitBody()
    {
        List<BlockElement> blocks = [.. doc.Body.Blocks()];
        IReadOnlyList<Section> sections = doc.Sections;
        int index = 0;
        OpenSection(SectionAt(sections, index));
        foreach (BlockElement block in blocks)
        {
            EmitBlock(block, mainPart);
            if (block is Paragraph p && HasSectionBreak(p))
            {
                End();
                index++;
                OpenSection(SectionAt(sections, index));
            }
        }

        End();
    }

    private static Section SectionAt(IReadOnlyList<Section> sections, int index) => sections[Math.Min(index, sections.Count - 1)];

    private static bool HasSectionBreak(Paragraph p)
    {
        for (MarkupNode n = p.Markup.Node.FirstChild; !n.IsNull; n = n.NextSibling)
        {
            if (n.Kind == MarkupNodeKind.Element && IsW(n, "pPr"u8))
            {
                for (MarkupNode c = n.FirstChild; !c.IsNull; c = c.NextSibling)
                {
                    if (c.Kind == MarkupNodeKind.Element && IsW(c, "sectPr"u8))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        return false;
    }

    private void OpenSection(Section s)
    {
        MarkupNode where = s.PropertiesElement.IsValid ? s.PropertiesElement.Node : doc.Body.Container.Node;
        Begin(NodeKind.Section, new NodeContent { Attributes = FormatSet.Of(FormatKeys.SectionPageSetup, PageSetup(s)), Anchor = Anchor.ForNode(WordAnchors.Of(mainPart, where)) });
        foreach (HeaderFooterReferenceInfo r in s.HeaderFooterReferences())
        {
            HeaderFooter? story = s.GetOwn(r.Kind, r.Type);
            if (story is null)
            {
                continue;
            }

            string type = r.Type switch { HeaderFooterType.First => "first", HeaderFooterType.Even => "even", _ => "default" };
            Begin(
                r.Kind == HeaderFooterKind.Header ? NodeKind.Header : NodeKind.Footer,
                new NodeContent { Attributes = FormatSet.Of("hf.type", type), Anchor = Anchor.ForNode(WordAnchors.Of(story.PartName, story.Container.Node)) });
            EmitBlocks(story.Blocks(), story.PartName);
            End();
        }
    }

    private static string PageSetup(Section s)
    {
        PageSize size = s.PageSize;
        PageMargins m = s.Margins;
        SectionColumns c = s.Columns;
        PageNumbering n = s.PageNumbering;
        var sb = new StringBuilder();
        sb.Append("w=").Append(size.Width.ToString(CultureInfo.InvariantCulture));
        sb.Append(";h=").Append(size.Height.ToString(CultureInfo.InvariantCulture));
        sb.Append(";orient=").Append(size.Orientation == PageOrientation.Landscape ? "landscape" : "portrait");
        sb.Append(";margins=").Append(m.Top.ToString(CultureInfo.InvariantCulture)).Append(',').Append(m.Right.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(m.Bottom.ToString(CultureInfo.InvariantCulture)).Append(',').Append(m.Left.ToString(CultureInfo.InvariantCulture));
        sb.Append(";hf=").Append(m.Header.ToString(CultureInfo.InvariantCulture)).Append(',').Append(m.Footer.ToString(CultureInfo.InvariantCulture));
        sb.Append(";gutter=").Append(m.Gutter.ToString(CultureInfo.InvariantCulture));
        sb.Append(";cols=").Append(c.Count.ToString(CultureInfo.InvariantCulture)).Append('/').Append(c.Space.ToString(CultureInfo.InvariantCulture));
        sb.Append(";break=").Append(BreakName(s.BreakType));
        if (s.TitlePage)
        {
            sb.Append(";titlepage");
        }

        if (n.Start is { } start)
        {
            sb.Append(";pgnum=").Append(start.ToString(CultureInfo.InvariantCulture));
        }

        if (n.Format is { } nf)
        {
            sb.Append(";pgfmt=").Append(nf);
        }

        if (s.IsRightToLeft)
        {
            sb.Append(";rtl");
        }

        return sb.ToString();
    }

    private static string BreakName(SectionBreakType t) => t switch
    {
        SectionBreakType.Continuous => "continuous",
        SectionBreakType.NextColumn => "nextColumn",
        SectionBreakType.EvenPage => "evenPage",
        SectionBreakType.OddPage => "oddPage",
        _ => "nextPage",
    };

    private void EmitBlocks(IEnumerable<BlockElement> blocks, string part)
    {
        foreach (BlockElement block in blocks)
        {
            EmitBlock(block, part);
        }
    }

    private void EmitBlock(BlockElement block, string part)
    {
        cancellation.ThrowIfCancellationRequested();
        switch (block)
        {
            case Paragraph p:
                EmitParagraph(p, part);
                break;
            case Table t:
                EmitTable(t, part);
                break;
            case BlockContentControl sdt:
                Begin(NodeKind.ContentControl, new NodeContent { Attributes = ControlAttributes(sdt.Markup.Node), Anchor = Anchor.ForNode(WordAnchors.Of(part, sdt.Markup.Node)) });
                EmitBlocks(sdt.Content.Blocks(), part);
                End();
                break;
            default:
                // Bookmark and range markers, wrappers and unknown markup between blocks: not part of the model.
                break;
        }
    }

    private FormatSet ControlAttributes(MarkupNode sdt)
    {
        var items = new List<KeyValuePair<string, string>>();
        if (controls.TryGetValue(sdt, out ContentControl? c))
        {
            if (!string.IsNullOrEmpty(c.Tag))
            {
                items.Add(new("sdt.tag", c.Tag));
            }

            if (!string.IsNullOrEmpty(c.Alias))
            {
                items.Add(new("sdt.alias", c.Alias));
            }

            items.Add(new("sdt.type", c.Type.ToString().ToLowerInvariant()));
            if (c.IsShowingPlaceholder)
            {
                items.Add(new("sdt.placeholder", "1"));
            }
        }

        return FormatSet.Create(items);
    }

    private void EmitTable(Table t, string part)
    {
        var items = new List<KeyValuePair<string, string>>();
        if (t.StyleId is { Length: > 0 } style)
        {
            items.Add(new("table.style", StyleName(style)));
        }

        Begin(NodeKind.Table, new NodeContent { Attributes = FormatSet.Create(items), Anchor = Anchor.ForNode(WordAnchors.Of(part, t.Markup.Node)) });
        foreach (TableRow row in t.Rows())
        {
            var rowItems = new List<KeyValuePair<string, string>>();
            if (row.IsHeader)
            {
                rowItems.Add(new("table.header", "1"));
            }

            if (row.GridBefore > 0)
            {
                rowItems.Add(new("table.gridbefore", row.GridBefore.ToString(CultureInfo.InvariantCulture)));
            }

            if (row.GridAfter > 0)
            {
                rowItems.Add(new("table.gridafter", row.GridAfter.ToString(CultureInfo.InvariantCulture)));
            }

            Begin(NodeKind.TableRow, new NodeContent { Attributes = FormatSet.Create(rowItems), Anchor = Anchor.ForNode(WordAnchors.Of(part, row.Markup.Node)) });
            foreach (TableCell cell in row.Cells())
            {
                var cellItems = new List<KeyValuePair<string, string>>();
                if (cell.GridSpan > 1)
                {
                    cellItems.Add(new("table.span", cell.GridSpan.ToString(CultureInfo.InvariantCulture)));
                }

                if (cell.VerticalMerge != CellMerge.None)
                {
                    cellItems.Add(new("table.vmerge", cell.VerticalMerge == CellMerge.Restart ? "restart" : "continue"));
                }

                if (cell.HorizontalMerge != CellMerge.None)
                {
                    cellItems.Add(new("table.hmerge", cell.HorizontalMerge == CellMerge.Restart ? "restart" : "continue"));
                }

                Begin(NodeKind.TableCell, new NodeContent { Attributes = FormatSet.Create(cellItems), Anchor = Anchor.ForNode(WordAnchors.Of(part, cell.Container.Node)) });
                EmitBlocks(cell.Blocks(), part);
                End();
            }

            End();
        }

        End();
    }

    // ---- paragraphs -----------------------------------------------------------------------------------------------------------

    private sealed class FieldFrame(string kind, MarkupNode beginRun, int hubStart)
    {
        public readonly string Kind = kind; // "field" or "control"
        public readonly MarkupNode BeginRun = beginRun;
        public readonly int HubStart = hubStart;
        public readonly StringBuilder Code = new();
        public readonly TextBuf Result = new();
        public bool InResult;
        public FormatSet? ControlAttributes;
    }

    private sealed class ParaState(string part)
    {
        public readonly string Part = part;
        public readonly TextBuf Main = new();
        public readonly List<Action> Children = [];
        public readonly Stack<FieldFrame> Frames = new();
        public int Hub;
    }

    private void EmitParagraph(Paragraph p, string part)
    {
        var st = new ParaState(part);
        WalkInlines(p.Inlines(), st, null);
        while (st.Frames.Count > 0)
        {
            // An unbalanced field: close it where the paragraph ends, the way the hub reads it.
            FinishFrame(st, st.Frames.Pop());
        }

        var items = new List<KeyValuePair<string, string>>();
        ParagraphAttributes(p, items);
        NodeKind kind = NodeKind.Paragraph;
        if (labels.TryGetValue(p.Markup.Node, out ListLabel? label))
        {
            kind = NodeKind.ListItem;
            items.Add(new(FormatKeys.ListLabel, ListText(label.Text)));
            items.Add(new(FormatKeys.ListLevel, label.Level.ToString(CultureInfo.InvariantCulture)));
            items.Add(new(FormatKeys.ListFormat, label.Definition.Format));
        }

        NodeContent content = Finish(st.Main, part, FormatSet.Create(items), Anchor.ForNode(WordAnchors.Of(part, p.Markup.Node)));
        Begin(kind, content);
        foreach (Action child in st.Children)
        {
            child();
        }

        End();
    }

    private static string ListText(string label)
    {
        // Symbol-font bullets are private-use characters that mean nothing outside the font.
        var sb = new StringBuilder(label.Length);
        foreach (char c in label)
        {
            sb.Append(c is >= '\uF000' and <= '\uF0FF' ? '\u2022' : c);
        }

        return sb.ToString();
    }

    private string StyleName(string styleId) => doc.Styles.Find(styleId)?.Name ?? styleId;

    private void ParagraphAttributes(Paragraph p, List<KeyValuePair<string, string>> items)
    {
        ResolvedParagraphProperties rp = p.ResolvedProperties;
        if (rp.StyleId is { Length: > 0 } style)
        {
            items.Add(new(FormatKeys.ParagraphStyle, StyleName(style)));
        }

        if (rp.Justification is { } jc && jc is not ("left" or "start"))
        {
            items.Add(new(FormatKeys.Alignment, jc));
        }

        string indent = Join(
            ("l", rp.LeftIndent),
            ("r", rp.RightIndent),
            ("f", rp.FirstLineIndent),
            ("h", rp.HangingIndent));
        if (indent.Length > 0)
        {
            items.Add(new(FormatKeys.Indent, indent));
        }

        string spacing = Join(("b", rp.SpaceBefore), ("a", rp.SpaceAfter), ("l", rp.LineSpacing));
        if (spacing.Length > 0)
        {
            spacing += rp.LineRule is { } rule ? ";rule=" + rule : string.Empty;
            items.Add(new(FormatKeys.Spacing, spacing));
        }

        var keep = new List<string>(3);
        if (rp.KeepNext == true)
        {
            keep.Add("next");
        }

        if (rp.KeepLines == true)
        {
            keep.Add("lines");
        }

        if (rp.PageBreakBefore == true)
        {
            keep.Add("pagebreak");
        }

        if (keep.Count > 0)
        {
            items.Add(new(FormatKeys.Keep, string.Join(',', keep)));
        }
    }

    private static string Join(params ReadOnlySpan<(string Name, long? Value)> values)
    {
        var sb = new StringBuilder();
        foreach ((string name, long? value) in values)
        {
            if (value is { } v && v != 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append(name).Append('=').Append(v.ToString(CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private void WalkInlines(IEnumerable<InlineElement> inlines, ParaState st, string? link)
    {
        foreach (InlineElement e in inlines)
        {
            switch (e)
            {
                case Run r:
                    AppendRun(r, st, link);
                    break;
                case Hyperlink h:
                    WalkInlines(h.Inlines(), st, LinkTarget(h, st.Part) ?? link);
                    break;
                case SimpleField sf:
                    {
                        var frame = new FieldFrame("field", sf.Markup.Node, st.Hub) { InResult = true };
                        frame.Code.Append(sf.Instruction ?? string.Empty);
                        st.Frames.Push(frame);
                        WalkInlines(sf.Inlines(), st, link);
                        FinishFrame(st, st.Frames.Pop());
                        break;
                    }

                case InlineContentControl cc:
                    {
                        var frame = new FieldFrame("control", cc.Markup.Node, st.Hub) { InResult = true, ControlAttributes = ControlAttributes(cc.Markup.Node) };
                        st.Frames.Push(frame);
                        WalkInlines(cc.Inlines(), st, link);
                        FinishFrame(st, st.Frames.Pop());
                        break;
                    }

                case InlineGroup g:
                    WalkInlines(g.Inlines(), st, link);
                    break;
                default:
                    // Bookmarks, proofing marks, math and unknown markup are not part of the model.
                    break;
            }
        }
    }

    private string? LinkTarget(Hyperlink h, string part)
    {
        string? target = null;
        if (h.RelationshipId is { Length: > 0 } id && RelationshipsOf(part).TryGetValue(id, out Relationship? rel))
        {
            target = rel.Target;
        }

        if (h.Anchor is { Length: > 0 } anchor)
        {
            target = (target ?? string.Empty) + "#" + anchor;
        }

        return target;
    }

    private Dictionary<string, Relationship> RelationshipsOf(string part)
    {
        if (!relationships.TryGetValue(part, out Dictionary<string, Relationship>? map))
        {
            map = new Dictionary<string, Relationship>(StringComparer.Ordinal);
            foreach (Relationship r in doc.Package.GetRelationships(part))
            {
                map.TryAdd(r.Id, r);
            }

            relationships[part] = map;
        }

        return map;
    }

    private string? StoryPart(string suffix)
    {
        foreach (Relationship r in doc.Package.GetRelationships(mainPart))
        {
            if (r.TargetMode == RelationshipTargetMode.Internal && r.Type.EndsWith(suffix, StringComparison.Ordinal)
                && PartUri.TryResolve(new PartUri(mainPart), r.Target, out PartUri uri))
            {
                return uri.Value;
            }
        }

        return null;
    }

    private void AppendRun(Run run, ParaState st, string? link)
    {
        FormatSet? format = null;
        MarkupNode runNode = run.Markup.Node;
        for (MarkupNode n = runNode.FirstChild; !n.IsNull; n = n.NextSibling)
        {
            if (n.Kind != MarkupNodeKind.Element)
            {
                continue;
            }

            RunChild(n, run, runNode, st, link, ref format);
        }
    }

    private void RunChild(MarkupNode n, Run run, MarkupNode runNode, ParaState st, string? link, ref FormatSet? format)
    {
        MarkupRef e = new(n);
        if (e.Namespace != SchemaNamespace.Wordprocessing)
        {
            if (n.NamespaceUri == McNamespace && n.LocalName.SequenceEqual("AlternateContent"u8))
            {
                for (MarkupNode c = n.FirstChild; !c.IsNull; c = c.NextSibling)
                {
                    if (c.Kind == MarkupNodeKind.Element && c.LocalName.SequenceEqual("Choice"u8))
                    {
                        for (MarkupNode d = c.FirstChild; !d.IsNull; d = d.NextSibling)
                        {
                            if (d.Kind == MarkupNodeKind.Element)
                            {
                                RunChild(d, run, runNode, st, link, ref format);
                            }
                        }

                        break;
                    }
                }
            }

            return;
        }

        ReadOnlySpan<byte> name = n.LocalName;
        if (name.SequenceEqual("t"u8))
        {
            AppendText(n.InnerText, run, runNode, st, link, ref format);
        }
        else if (name.SequenceEqual("tab"u8))
        {
            AppendText("\t", run, runNode, st, link, ref format);
        }
        else if (name.SequenceEqual("br"u8) || name.SequenceEqual("cr"u8))
        {
            AppendText("\n", run, runNode, st, link, ref format);
        }
        else if (name.SequenceEqual("noBreakHyphen"u8))
        {
            AppendText("\u2011", run, runNode, st, link, ref format);
        }
        else if (name.SequenceEqual("softHyphen"u8))
        {
            AppendText("\u00AD", run, runNode, st, link, ref format);
        }
        else if (name.SequenceEqual("fldChar"u8))
        {
            FieldChar(n, runNode, st);
        }
        else if (name.SequenceEqual("instrText"u8))
        {
            if (st.Frames.Count > 0 && !st.Frames.Peek().InResult)
            {
                st.Frames.Peek().Code.Append(n.InnerText);
            }
        }
        else if (name.SequenceEqual("drawing"u8) || name.SequenceEqual("pict"u8) || name.SequenceEqual("object"u8))
        {
            Drawing(n, runNode, st);
        }
        else if (name.SequenceEqual("footnoteReference"u8) || name.SequenceEqual("endnoteReference"u8))
        {
            NoteReference(n, runNode, st, name.SequenceEqual("footnoteReference"u8));
        }
        else if (name.SequenceEqual("commentReference"u8))
        {
            CommentReference(n, st);
        }
    }

    private void AppendText(string text, Run run, MarkupNode runNode, ParaState st, string? link, ref FormatSet? format)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (st.Frames.Count > 0 && !st.Frames.Peek().InResult)
        {
            return;
        }

        format ??= RunFormat(run, link);
        TextBuf sink = st.Frames.Count > 0 ? st.Frames.Peek().Result : st.Main;
        sink.Append(text, format, runNode, st.Hub, text.Length);
        st.Hub += text.Length;
        CountCharacters(text.Length);
    }

    private static FormatSet RunFormat(Run run, string? link)
    {
        ResolvedRunProperties rp = run.ResolvedProperties;
        var items = new List<KeyValuePair<string, string>>(8);
        if (rp.Bold)
        {
            items.Add(new(FormatKeys.Bold, "1"));
        }

        if (rp.Italic)
        {
            items.Add(new(FormatKeys.Italic, "1"));
        }

        if (rp.Underline is { Length: > 0 } u && u != "none")
        {
            items.Add(new(FormatKeys.Underline, u));
        }

        if (rp.DoubleStrike)
        {
            items.Add(new(FormatKeys.Strike, "double"));
        }
        else if (rp.Strike)
        {
            items.Add(new(FormatKeys.Strike, "1"));
        }

        ResolvedFonts fonts = rp.Fonts;
        string? font = fonts.Ascii ?? fonts.HighAnsi ?? fonts.EastAsia ?? fonts.ComplexScript;
        if (!string.IsNullOrEmpty(font))
        {
            items.Add(new(FormatKeys.Font, font));
        }

        if (rp.SizeHalfPoints is { } sz)
        {
            items.Add(new(FormatKeys.Size, (sz / 2).ToString(CultureInfo.InvariantCulture) + (sz % 2 != 0 ? ".5" : string.Empty)));
        }

        if (rp.Color is { Length: > 0 } color)
        {
            items.Add(new(FormatKeys.Color, color));
        }

        if (rp.Highlight is { Length: > 0 } hl && hl != "none")
        {
            items.Add(new(FormatKeys.Highlight, hl));
        }

        if (rp.SmallCaps)
        {
            items.Add(new(FormatKeys.Caps, "small"));
        }
        else if (rp.Caps)
        {
            items.Add(new(FormatKeys.Caps, "caps"));
        }

        if (rp.VerticalAlignment is "superscript" or "subscript")
        {
            items.Add(new(FormatKeys.VerticalAlign, rp.VerticalAlignment));
        }

        if (rp.Hidden)
        {
            items.Add(new("run.hidden", "1"));
        }

        if (link is not null)
        {
            items.Add(new(FormatKeys.Link, link));
        }

        return FormatSet.Create(items);
    }

    // ---- fields and other inline objects ------------------------------------------------------------------------------------------

    private void FieldChar(MarkupNode fldChar, MarkupNode runNode, ParaState st)
    {
        string? type = null;
        foreach (MarkupAttributeRef a in fldChar.Attributes)
        {
            if (!a.IsNamespaceDeclaration && a.LocalName.SequenceEqual("fldCharType"u8))
            {
                type = a.Value;
            }
        }

        switch (type)
        {
            case "begin":
                st.Frames.Push(new FieldFrame("field", runNode, st.Hub));
                break;
            case "separate":
                if (st.Frames.Count > 0)
                {
                    st.Frames.Peek().InResult = true;
                }

                break;
            case "end":
                if (st.Frames.Count > 0)
                {
                    FinishFrame(st, st.Frames.Pop());
                }

                break;
        }
    }

    private void FinishFrame(ParaState st, FieldFrame f)
    {
        if (st.Frames.Count > 0)
        {
            // Nested: its result reads as text of the enclosing field (its code is not kept apart).
            FieldFrame parent = st.Frames.Peek();
            if (parent.InResult)
            {
                parent.Result.AppendBuffer(f.Result);
            }
            else
            {
                parent.Code.Append(f.Result.Raw);
            }

            return;
        }

        int hubLength = st.Hub - f.HubStart;
        st.Main.Append(Placeholder, null, f.BeginRun, f.HubStart, hubLength);
        string part = st.Part;
        if (f.Kind == "field")
        {
            string code = CollapseSpace(f.Code.ToString());
            st.Children.Add(() => EmitLeaf(
                NodeKind.Field,
                Finish(f.Result, part, FormatSet.Of(FormatKeys.FieldCode, code), Anchor.ForNode(WordAnchors.Of(part, f.BeginRun)))));
        }
        else
        {
            st.Children.Add(() => EmitLeaf(
                NodeKind.ContentControl,
                Finish(f.Result, part, f.ControlAttributes, Anchor.ForNode(WordAnchors.Of(part, f.BeginRun)))));
        }
    }

    private static string CollapseSpace(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private void EmitLeaf(NodeKind kind, NodeContent content)
    {
        CountNode();
        builder.Add(kind, content);
    }

    private void Drawing(MarkupNode drawing, MarkupNode runNode, ParaState st)
    {
        var found = new List<(string Kind, MarkupNode Node, string? Embed)>();
        string? alt = null;
        string? extent = null;
        ScanDrawing(drawing, found, ref alt, ref extent);
        string part = st.Part;
        if (found.Count == 0)
        {
            found.Add(("drawing", drawing, null));
        }

        foreach ((string kind, MarkupNode node, string? embed) in found)
        {
            TextBuf sink = st.Frames.Count > 0 && st.Frames.Peek().InResult ? st.Frames.Peek().Result : st.Main;
            sink.Append(Placeholder, null, runNode, st.Hub, 0);
            CountCharacters(1);
            if (kind == "textbox")
            {
                st.Children.Add(() => EmitTextBox(node, part));
            }
            else
            {
                string? altText = alt;
                string? ext = extent;
                st.Children.Add(() => EmitImage(kind, node, embed, part, altText, ext));
            }
        }
    }

    private static void ScanDrawing(MarkupNode node, List<(string Kind, MarkupNode Node, string? Embed)> found, ref string? alt, ref string? extent)
    {
        for (MarkupNode c = node.FirstChild; !c.IsNull; c = c.NextSibling)
        {
            if (c.Kind != MarkupNodeKind.Element)
            {
                continue;
            }

            ReadOnlySpan<byte> local = c.LocalName;
            if (c.NamespaceUri == McNamespace && local.SequenceEqual("Fallback"u8))
            {
                continue;
            }

            if (local.SequenceEqual("txbxContent"u8))
            {
                found.Add(("textbox", c, null));
                continue;
            }

            if (local.SequenceEqual("blip"u8))
            {
                found.Add(("picture", c, AttributeByLocalName(c, "embed") ?? AttributeByLocalName(c, "link")));
            }
            else if (local.SequenceEqual("imagedata"u8))
            {
                found.Add(("picture", c, AttributeByLocalName(c, "id")));
            }
            else if (local.SequenceEqual("docPr"u8))
            {
                alt ??= c.GetAttribute("descr");
            }
            else if (local.SequenceEqual("extent"u8) && extent is null)
            {
                extent = c.GetAttribute("cx") + "x" + c.GetAttribute("cy");
            }

            ScanDrawing(c, found, ref alt, ref extent);
        }
    }

    private static string? AttributeByLocalName(MarkupNode n, string local)
    {
        byte[] name = Encoding.UTF8.GetBytes(local);
        foreach (MarkupAttributeRef a in n.Attributes)
        {
            if (!a.IsNamespaceDeclaration && a.LocalName.SequenceEqual(name))
            {
                return a.Value;
            }
        }

        return null;
    }

    private void EmitImage(string kind, MarkupNode node, string? relationshipId, string part, string? alt, string? extent)
    {
        var items = new List<KeyValuePair<string, string>>();
        string hash;
        if (kind == "picture")
        {
            hash = HashImage(relationshipId, part, items);
        }
        else
        {
            // A chart, shape or diagram the model does not itemize: known by a hash of its markup.
            items.Add(new("image.kind", kind));
            hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(node.OuterXml)));
        }

        items.Add(new(FormatKeys.ContentHash, hash));
        if (!string.IsNullOrEmpty(alt))
        {
            items.Add(new("image.alt", alt));
        }

        if (extent is not null)
        {
            items.Add(new("image.extent", extent));
        }

        EmitLeaf(NodeKind.Image, new NodeContent { Attributes = FormatSet.Create(items), Anchor = Anchor.ForNode(WordAnchors.Of(part, node)) });
    }

    private string HashImage(string? relationshipId, string part, List<KeyValuePair<string, string>> items)
    {
        if (relationshipId is null || !RelationshipsOf(part).TryGetValue(relationshipId, out Relationship? rel))
        {
            return Convert.ToHexStringLower(SHA256.HashData([]));
        }

        if (rel.TargetMode != RelationshipTargetMode.Internal)
        {
            items.Add(new("image.link", rel.Target));
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rel.Target)));
        }

        if (!PartUri.TryResolve(new PartUri(part), rel.Target, out PartUri uri) || doc.Package.FindPart(uri.Value) is not { } imagePart)
        {
            return Convert.ToHexStringLower(SHA256.HashData([]));
        }

        ReadOnlyMemory<byte> bytes = imagePart.GetBytes();
        if (bytes.Length > options.MaxImageBytes)
        {
            throw WordSourceErrors.Make(WordSourceFailure.TooLarge, $"an image is {bytes.Length} bytes; the limit is {options.MaxImageBytes}");
        }

        if (ImageHashes.Perceptual(bytes.Span) is { } perceptual)
        {
            items.Add(new(FormatKeys.PerceptualHash, perceptual));
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
    }

    private void EmitTextBox(MarkupNode content, string part)
    {
        Begin(NodeKind.TextBox, new NodeContent { Anchor = Anchor.ForNode(WordAnchors.Of(part, content)) });
        EmitPlainParagraphs(content, part);
        End();
    }

    private void EmitPlainParagraphs(MarkupNode container, string part)
    {
        for (MarkupNode c = container.FirstChild; !c.IsNull; c = c.NextSibling)
        {
            if (c.Kind != MarkupNodeKind.Element)
            {
                continue;
            }

            cancellation.ThrowIfCancellationRequested();
            if (IsW(c, "p"u8))
            {
                var sb = new StringBuilder();
                PlainText(c, sb);
                string raw = sb.ToString();
                var buf = new TextBuf();
                buf.AppendWhole(raw, WordAnchors.OfText(part, c, 0, raw.Length));
                CountCharacters(raw.Length);
                Begin(NodeKind.Paragraph, Finish(buf, part, null, Anchor.ForNode(WordAnchors.Of(part, c))));
                End();
            }
            else if (!IsW(c, "txbxContent"u8))
            {
                EmitPlainParagraphs(c, part);
            }
        }
    }

    private static void PlainText(MarkupNode n, StringBuilder sb)
    {
        for (MarkupNode c = n.FirstChild; !c.IsNull; c = c.NextSibling)
        {
            if (c.Kind != MarkupNodeKind.Element)
            {
                continue;
            }

            ReadOnlySpan<byte> local = c.LocalName;
            if (local.SequenceEqual("t"u8))
            {
                sb.Append(c.InnerText);
            }
            else if (local.SequenceEqual("tab"u8))
            {
                sb.Append('\t');
            }
            else if (local.SequenceEqual("br"u8) || local.SequenceEqual("cr"u8))
            {
                sb.Append('\n');
            }
            else if (!local.SequenceEqual("txbxContent"u8) && !local.SequenceEqual("pPr"u8) && !local.SequenceEqual("rPr"u8) && !local.SequenceEqual("instrText"u8)
                && !(c.NamespaceUri == McNamespace && local.SequenceEqual("Fallback"u8)))
            {
                PlainText(c, sb);
            }
        }
    }

    private void NoteReference(MarkupNode reference, MarkupNode runNode, ParaState st, bool footnote)
    {
        long id = long.TryParse(AttributeByLocalName(reference, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : -1;
        Note? note = (footnote ? doc.Footnotes : doc.Endnotes).Find(id);
        string? part = footnote ? footnotesPart : endnotesPart;
        if (note is null || part is null)
        {
            return;
        }

        TextBuf sink = st.Frames.Count > 0 && st.Frames.Peek().InResult ? st.Frames.Peek().Result : st.Main;
        sink.Append(Placeholder, null, runNode, st.Hub, 0);
        CountCharacters(1);
        st.Children.Add(() =>
        {
            Begin(footnote ? NodeKind.Footnote : NodeKind.Endnote, new NodeContent { Anchor = Anchor.ForNode(WordAnchors.Of(part, note.Container.Node)) });
            EmitBlocks(note.Blocks(), part);
            End();
        });
    }

    private void CommentReference(MarkupNode reference, ParaState st)
    {
        long id = long.TryParse(AttributeByLocalName(reference, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : -1;
        Comment? comment = doc.Comments.Find(id);
        string? part = commentsPart;
        if (comment is null || part is null)
        {
            return;
        }

        // A comment leaves no mark in the text: it is hidden into the ledger by default, and its place is its position among the paragraph's children.
        st.Children.Add(() =>
        {
            var items = new List<KeyValuePair<string, string>> { new("comment.author", comment.Author) };
            if (!string.IsNullOrEmpty(comment.Initials))
            {
                items.Add(new("comment.initials", comment.Initials));
            }

            if (comment.Date is { } date)
            {
                items.Add(new("comment.date", date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
            }

            if (comment.IsReply)
            {
                items.Add(new("comment.reply", "1"));
            }

            if (comment.IsDone)
            {
                items.Add(new("comment.done", "1"));
            }

            Begin(NodeKind.Comment, new NodeContent { Attributes = FormatSet.Create(items), Anchor = Anchor.ForNode(WordAnchors.Of(part, comment.Container.Node)) });
            EmitBlocks(comment.Blocks(), part);
            End();
        });
    }

    // ---- properties and parts the model does not itemize --------------------------------------------------------------------------

    private void EmitProperties()
    {
        var found = new SortedDictionary<string, (string Value, string Part)>(StringComparer.Ordinal);
        foreach (Relationship r in doc.Package.GetRelationships())
        {
            bool core = r.Type.EndsWith("/metadata/core-properties", StringComparison.Ordinal);
            bool custom = r.Type.EndsWith("/custom-properties", StringComparison.Ordinal);
            if (!(core || custom) || r.TargetMode != RelationshipTargetMode.Internal
                || !PartUri.TryResolve(null, r.Target, out PartUri uri) || doc.Package.FindPart(uri.Value) is not { } part
                || !MarkupDocument.TryParse(part.GetBytes(), out MarkupDocument? props, doc.Context) || props is null || props.Root.IsNull)
            {
                continue;
            }

            for (MarkupNode c = props.Root.FirstChild; !c.IsNull; c = c.NextSibling)
            {
                if (c.Kind != MarkupNodeKind.Element)
                {
                    continue;
                }

                string name = core ? c.LocalNameString : "custom:" + (c.GetAttribute("name") ?? string.Empty);
                string value = c.InnerText.Trim();
                if (value.Length > 0 || !core)
                {
                    found[name] = (value, uri.Value);
                }
            }
        }

        foreach (KeyValuePair<string, (string Value, string Part)> kv in found)
        {
            NormalizedText text = TextNormalization.Normalize(kv.Value.Value, Normalizers);
            CountCharacters(text.Text.Length);
            CountNode();
            builder.Add(
                NodeKind.Property,
                new NodeContent
                {
                    Text = text.Text,
                    Folds = text.Folds,
                    Attributes = FormatSet.Of(FormatKeys.PropertyName, kv.Key),
                    Anchor = Anchor.ForNode(Prefix(kv.Value.Part)),
                });
        }
    }

    private static string Prefix(string part) => WordAnchors.Prefix + part + "#";

    private static readonly string[] OpaquePrefixes =
    [
        "/customXml/", "/word/embeddings/", "/word/charts/", "/word/diagrams/", "/word/activeX/", "/word/ink/", "/word/glossary/",
        "/word/drawings/", "/word/vbaProject", "/word/vbaData", "/word/macrosheets/", "/word/ole",
    ];

    private void EmitOpaque()
    {
        var parts = new List<IPackagePart>();
        foreach (IPackagePart p in doc.Package.Parts)
        {
            foreach (string prefix in OpaquePrefixes)
            {
                if (p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(p);
                    break;
                }
            }
        }

        parts.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (IPackagePart p in parts)
        {
            cancellation.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte> bytes = p.GetBytes();
            builder.AddOpaque(p.Name, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), Anchor.ForNode(Prefix(p.Name)));
        }
    }

    // ---- node content -------------------------------------------------------------------------------------------------------------

    private NodeContent Finish(TextBuf buf, string part, FormatSet? attributes, Anchor anchor)
    {
        NormalizedText text = TextNormalization.Normalize(buf.Raw.ToString(), Normalizers);
        var runs = ImmutableArray.CreateBuilder<FormatRun>(buf.Runs.Count);
        foreach (TextBuf.RawRun r in buf.Runs)
        {
            int s = text.MapRawIndex(r.Start);
            int e = text.MapRawIndex(r.Start + r.Length);
            if (e > s)
            {
                runs.Add(new FormatRun(s, e - s, r.Format));
            }
        }

        var segs = ImmutableArray.CreateBuilder<AnchoredSpan>(buf.Segs.Count);
        int last = 0;
        foreach (TextBuf.RawSeg g in buf.Segs)
        {
            int s = Math.Max(text.MapRawIndex(g.Start), last);
            int e = text.MapRawIndex(g.Start + g.Length);
            if (e > s)
            {
                string reference = g.Ref ?? WordAnchors.OfText(part, g.Element, g.HubOffset, g.HubLength);
                segs.Add(new AnchoredSpan(s, e - s, Anchor.ForNode(reference)));
                last = e;
            }
        }

        CountCharacters(0);
        return new NodeContent
        {
            Text = text.Text,
            Runs = runs.ToImmutable(),
            Attributes = attributes,
            Anchor = anchor,
            Segments = segs.ToImmutable(),
            Folds = text.Folds,
        };
    }

    // ---- bounds ---------------------------------------------------------------------------------------------------------------------

    private void Begin(NodeKind kind, NodeContent content)
    {
        CountNode();
        builder.Begin(kind, content);
    }

    private void End() => builder.End();

    private void CountNode()
    {
        if (++nodes > options.MaxNodes)
        {
            throw WordSourceErrors.Make(WordSourceFailure.TooLarge, $"the document has more than {options.MaxNodes} nodes");
        }
    }

    private void CountCharacters(int n)
    {
        characters += n;
        if (characters > options.MaxCharacters)
        {
            throw WordSourceErrors.Make(WordSourceFailure.TooLarge, $"the document has more than {options.MaxCharacters} characters of text");
        }
    }

    private static bool IsW(MarkupNode n, ReadOnlySpan<byte> local) =>
        n.LocalName.SequenceEqual(local) && new MarkupRef(n).Namespace == SchemaNamespace.Wordprocessing;
}
