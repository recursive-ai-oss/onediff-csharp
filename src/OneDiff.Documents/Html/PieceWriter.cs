using System.Globalization;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Html;

/// <summary>
/// Writes the marked-up pieces of a block's text: unchanged text bare; inserted, deleted and reformatted text in elements that
/// carry the change's id, hidden text for screen readers and, when the style has one, a glyph, so a change never depends on
/// colour alone. Characters are escaped by <see cref="HtmlOut"/>.
/// </summary>
internal sealed class PieceWriter(HtmlOut o, RedlineStyle style, RedlineView view)
{
    private readonly HashSet<int> tagged = [];

    /// <summary>Starts a new table cell: type tags are shown once per change per cell.</summary>
    public void NewCell() => tagged.Clear();

    public void Pieces(List<Piece> pieces, bool separatorsAsMarks)
    {
        foreach (Piece p in pieces)
        {
            switch (p.Kind)
            {
                case PieceKind.Equal:
                    o.Text(p.Text);
                    break;
                case PieceKind.Insert:
                    Inserted(p.ChangeId, p.Text);
                    break;
                case PieceKind.Delete:
                    Deleted(p.ChangeId, p.Text, separatorsAsMarks);
                    break;
                case PieceKind.Format:
                    Reformatted(p);
                    break;
                case PieceKind.InsertMark:
                    Element("ins", "", p.ChangeId, style.Insertion, " inserted paragraph break ", " end inserted ", "", false, null, "\u00B6");
                    break;
                default:
                    Element("del", "", p.ChangeId, style.Deletion, " deleted paragraph break ", " end deleted ", "", false, null, "\u00B6");
                    break;
            }
        }
    }

    public void Inserted(int changeId, string text) =>
        Element("ins", "", changeId, style.Insertion, " inserted: ", " end inserted ", text, false, null, null);

    public void Deleted(int changeId, string text, bool separatorsAsMarks) =>
        Element("del", "", changeId, style.Deletion, " deleted: ", " end deleted ", text, separatorsAsMarks, null, null);

    private void Reformatted(Piece p)
    {
        string title = "Formatting changed. Was: " + Describe(p.Edit?.OldRuns) + ". Now: " + Describe(p.Edit?.NewRuns) + ".";
        Element("span", "fmt", p.ChangeId, style.Formatting, " formatting changed: ", " end formatting ", p.Text, false, title, null);
    }

    private static string Describe(System.Collections.Immutable.ImmutableArray<FormatRun>? runs)
    {
        if (runs is not { IsDefaultOrEmpty: false } r)
        {
            return "no formatting";
        }

        return string.Join("; ", r.Select(x => x.Format.IsEmpty ? "plain" : x.Format.ToString()).Distinct());
    }

    /// <summary>One element of a change. <paramref name="mark"/> is the paragraph-break mark for a break, which then replaces the text.</summary>
    private void Element(string tag, string cls, int changeId, MarkStyle m, string start, string end, string text, bool seps, string? title, string? mark)
    {
        o.Raw("<");
        o.Raw(tag);
        if (cls.Length > 0)
        {
            o.Attr("class", cls);
        }

        if (changeId >= 0)
        {
            o.IntAttr("data-change", changeId);
        }

        if (title is not null)
        {
            o.Attr("title", title);
        }

        o.Raw(">");
        ChangeTag(changeId);
        if (m.Glyph.Length > 0)
        {
            o.Raw("<span class=\"gl\" aria-hidden=\"true\">");
            o.Text(m.Glyph);
            o.Raw("</span>");
        }

        o.Raw("<span class=\"sr\">");
        o.Text(start);
        o.Raw("</span>");
        if (mark is not null)
        {
            o.Raw("<span class=\"pbrk\" aria-hidden=\"true\">");
            o.Text(mark);
            o.Raw("</span>");
        }
        else
        {
            o.Text(text, seps);
        }

        o.Raw("<span class=\"sr\">");
        o.Text(end);
        o.Raw("</span></");
        o.Raw(tag);
        o.Raw(">");
    }

    /// <summary>The optional type tag, once per change.</summary>
    private void ChangeTag(int changeId)
    {
        if (!style.TypeTags || changeId < 0 || !tagged.Add(changeId) || !view.ChangeKinds.TryGetValue(changeId, out ChangeKind kind))
        {
            return;
        }

        o.Raw("<span class=\"tag\">");
        o.Raw(KindName(kind));
        o.Raw("</span>");
    }

    public static string KindName(ChangeKind k) => k switch
    {
        ChangeKind.Insert => "Insert",
        ChangeKind.Delete => "Delete",
        ChangeKind.Replace => "Replace",
        ChangeKind.Move => "Move",
        ChangeKind.Split => "Split",
        ChangeKind.Merge => "Merge",
        ChangeKind.Format => "Format",
        _ => "Attribute",
    };

    /// <summary>The attribute notes of a new node: what changed in its style, list label or other block-level attributes.</summary>
    public void AttributeNotes(List<(int ChangeId, AttributeEdit Edit)> notes)
    {
        o.Raw("<div class=\"attrs\">");
        foreach ((int id, AttributeEdit e) in notes)
        {
            o.Raw("<span class=\"ae\" data-change=\"");
            o.Int(id);
            o.Raw("\"");
            o.Attr("data-key", e.Key);
            if (e.OldValue is not null)
            {
                o.Attr("data-old", e.OldValue);
            }

            if (e.NewValue is not null)
            {
                o.Attr("data-new", e.NewValue);
            }

            o.Raw("><span class=\"sr\"> attribute changed: </span>");
            o.Text(AttributeLabel(e.Key) + ": " + (e.OldValue ?? "(none)") + " \u2192 " + (e.NewValue ?? "(none)"));
            o.Raw("</span>");
        }

        o.Raw("</div>");
    }

    /// <summary>The label a reader sees for an attribute key; the key itself stays in <c>data-key</c>.</summary>
    internal static string AttributeLabel(string key) => key switch
    {
        "list.label" => "List number",
        "list.level" => "List level",
        "list.format" => "List format",
        "para.style" => "Paragraph style",
        "para.align" => "Alignment",
        "para.indent" => "Indent",
        "para.spacing" => "Spacing",
        "para.keep" => "Keep with next",
        "run.bold" => "Bold",
        "run.italic" => "Italic",
        "run.underline" => "Underline",
        "run.strike" => "Strikethrough",
        "run.font" => "Font",
        "run.size" => "Font size",
        "run.color" => "Font color",
        "run.highlight" => "Highlight",
        "run.caps" => "Capitals",
        "run.vert" => "Superscript or subscript",
        "run.link" => "Link",
        "run.hidden" => "Hidden text",
        "section.page" => "Page setup",
        "field.code" => "Field code",
        "prop.name" => "Property",
        "hash.content" => "Content",
        "hash.perceptual" => "Image appearance",
        _ => ReadableKey(key),
    };

    /// <summary>A readable form of a key with no label of its own: separators become spaces and the first letter is capitalized.</summary>
    private static string ReadableKey(string key)
    {
        string t = key.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ').Trim();
        return t.Length == 0 ? "Attribute" : char.ToUpperInvariant(t[0]) + t[1..];
    }

    /// <summary>The link that jumps between the two places of a moved block.</summary>
    public void MoveLink(int changeId, bool fromHere)
    {
        o.Raw("<a class=\"mvlink\" href=\"#");
        o.Raw(fromHere ? "mvt-" : "mvf-");
        o.Int(changeId);
        o.Raw("\"><span class=\"gl\" aria-hidden=\"true\">");
        o.Raw(fromHere ? "\u2192" : "\u2190");
        o.Raw("</span><span class=\"sr\">");
        o.Raw(fromHere ? " moved from here; go to the new place " : " moved here; go to the old place ");
        o.Raw("</span></a>");
    }

    public static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Names and shapes shared by the block writers.</summary>
internal static class BlockShapes
{
    public static string KindClass(NodeKind k) => k switch
    {
        NodeKind.Section => "section",
        NodeKind.Paragraph => "paragraph",
        NodeKind.ListItem => "listitem",
        NodeKind.Table => "table",
        NodeKind.TableRow => "row",
        NodeKind.TableCell => "cell",
        NodeKind.Footnote => "footnote",
        NodeKind.Endnote => "endnote",
        NodeKind.Header => "header",
        NodeKind.Footer => "footer",
        NodeKind.Comment => "comment",
        NodeKind.Field => "field",
        NodeKind.Image => "image",
        NodeKind.TextBox => "textbox",
        NodeKind.ContentControl => "control",
        NodeKind.Property => "property",
        _ => "node",
    };

    /// <summary>A short label for kinds that are not ordinary text blocks, or null.</summary>
    public static string? KindLabel(Node n)
    {
        switch (n.Kind)
        {
            case NodeKind.Footnote:
                return "Footnote";
            case NodeKind.Endnote:
                return "Endnote";
            case NodeKind.Header:
                return "Header";
            case NodeKind.Footer:
                return "Footer";
            case NodeKind.Comment:
                return "Comment";
            case NodeKind.Field:
                return "Field";
            case NodeKind.TextBox:
                return "Text box";
            case NodeKind.ContentControl:
                return "Content control";
            case NodeKind.Image:
                string? h = n.Attributes.Get(FormatKeys.ContentHash);
                return h is null ? "Image" : "Image " + (h.Length > 12 ? h[..12] : h);
            case NodeKind.Property:
                string? name = n.Attributes.Get(FormatKeys.PropertyName);
                return name is null ? "Property" : "Property " + name;
            default:
                return null;
        }
    }

    /// <summary>The element for a block: real table elements where the structure allows them, a div elsewhere.</summary>
    public static string TagFor(NodeKind kind, NodeKind? parent) => kind switch
    {
        NodeKind.Table => "table",
        NodeKind.TableRow when parent == NodeKind.Table => "tr",
        NodeKind.TableCell when parent == NodeKind.TableRow => "td",
        _ => "div",
    };
}
