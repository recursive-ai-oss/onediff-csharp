using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using RecursiveAI.OneOffice;
using RecursiveAI.OneOffice.Documents;
using RecursiveAI.OneOffice.Documents.Numbering;

namespace OneDiff.Word.Internal;

/// <summary>A DOCX rebuilt from a compare model, and the model re-anchored onto it.</summary>
/// <param name="Bytes">The package.</param>
/// <param name="Model">The same nodes (ids, kinds, parents, text, runs and attributes) as the model it was built from, with anchors and segments that point into <paramref name="Bytes"/> in the grammar of docs/word.md.</param>
internal sealed record ReconstructedDocx(byte[] Bytes, CompareDocument Model);

/// <summary>
/// Builds a DOCX from the compare model of a document that has no Word package of its own (a PDF, a scan, plain text), so that a
/// tracked-changes writer has a base to work on. Everything the model itemizes for a flowing document is written back: paragraphs, list
/// items with their resolved labels, tables, one header and one footer, and the character formatting the model carries. Nothing else is
/// invented: no page layout, no styles, no pictures. The package says it is reconstructed (core properties and a note in the header).
/// </summary>
/// <remarks>
/// The text written is the text the comparison projected (the node's text with every fold whose category the settings do not hide undone;
/// see <see cref="Project"/>), because the offsets of a comparison's edits are in that text. Under the default settings this is the
/// normalized text, so a PDF paragraph is one reflowable paragraph; under settings that show line breaks and hyphenation (Strict) the
/// line breaks and hyphens of the PDF are written, because they are then differences like any other. Reading the package back gives the
/// same projected text for every node (checked, and a difference is a failure). The reconstruction keeps the model's node ids, because the
/// alignment and the changes of a comparison are keyed on them: <see cref="Build"/> reads the package back through the Word source and
/// gives each node of the model the content (text, folds, anchor, segments) of the node read back.
/// </remarks>
internal static class Reconstruction
{
    /// <summary>The visible note written into the header of a reconstructed document.</summary>
    public const string NoteText = "Reconstructed by OneDiff from a document that is not a Word file: text, lists, tables and basic character formatting only.";

    private const string Decl =
        " xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\""
        + " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    /// <summary>Whether <paramref name="model"/> comes from a Word file (it has bytes the writer can reopen).</summary>
    public static bool IsWordSourced(CompareDocument model) => WordOrigins.TryGet(model, out _);

    /// <summary>Rebuilds <paramref name="model"/> as a DOCX.</summary>
    /// <param name="model">A flowing compare model.</param>
    /// <param name="settings">The settings of the comparison: they decide which folds are written as the original characters.</param>
    /// <param name="sourceFormat">What it was reconstructed from, for the labels (for example <c>application/pdf</c>).</param>
    /// <param name="note">Whether to write the visible note into the header.</param>
    /// <param name="cancellationToken">Observed per block.</param>
    /// <exception cref="NotSupportedException">The model holds nodes a reconstruction cannot write (children of paragraphs such as fields or pictures, notes, comments, text boxes, properties).</exception>
    /// <exception cref="InvalidOperationException">The package read back does not match the model (a defect, reported with the first mismatch).</exception>
    public static ReconstructedDocx Build(CompareDocument model, CompareSettings settings, string sourceFormat, bool note, CancellationToken cancellationToken = default)
    {
        if (model.Kind != DocumentKind.Flowing)
        {
            throw new NotSupportedException("only flowing documents can be written as a DOCX");
        }

        var body = new List<int>();
        var headers = new List<int>();
        var footers = new List<int>();
        var bodySections = new List<int>();
        var furnitureSections = new List<int>();
        foreach (int root in model.Roots)
        {
            Node r = model[root];
            if (r.Kind == NodeKind.Section)
            {
                bool any = false;
                foreach (int c in model.ChildrenOf(root))
                {
                    switch (model[c].Kind)
                    {
                        case NodeKind.Header:
                            headers.Add(c);
                            break;
                        case NodeKind.Footer:
                            footers.Add(c);
                            break;
                        default:
                            body.Add(c);
                            any = true;
                            break;
                    }
                }

                (any ? bodySections : furnitureSections).Add(root);
            }
            else
            {
                body.Add(root);
            }
        }

        using Document doc = Document.Create();
        var labels = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (Node n in model.Nodes)
        {
            if (n.Kind == NodeKind.ListItem && n.Attributes.Get(FormatKeys.ListLabel) is { Length: > 0 } label && !labels.ContainsKey(label))
            {
                long abs = doc.Numbering.AddAbstractNumbering([new NumberingDefinitions.LevelSpec("none", label)]);
                labels[label] = doc.Numbering.AddInstance(abs);
            }
        }

        if (labels.Count == 0)
        {
            // A package always has a numbering part, so that list definitions of the other document can be added to it as revisions.
            doc.Numbering.AddInstance(doc.Numbering.AddAbstractNumbering([new NumberingDefinitions.LevelSpec("none", "")]));
        }

        var sb = new StringBuilder();
        foreach (int id in body)
        {
            Block(model, id, labels, settings, sb, cancellationToken);
        }

        RecursiveAI.OneOffice.Markup.MarkupNode bodyNode = doc.Body.Container.Node;
        RecursiveAI.OneOffice.Markup.MarkupNode sect = doc.Body.SectionProperties!.Value.Node;
        for (RecursiveAI.OneOffice.Markup.MarkupNode c = bodyNode.FirstChild; !c.IsNull; c = c.NextSibling)
        {
            if (c.Kind == RecursiveAI.OneOffice.Markup.MarkupNodeKind.Element && c.LocalNameString == "p")
            {
                c.Remove();
                break;
            }
        }

        if (sb.Length > 0)
        {
            sect.InsertXmlBefore(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        // One header and one footer (the default ones); a model with more than one of a kind keeps the first in the package and the
        // others have no place in it (their nodes get no anchor, and the writer lists them as not carried).
        Story(doc, HeaderFooterKind.Header, headers.Count > 0 ? headers[0] : -1, model, labels, settings, note ? NoteText : null, cancellationToken);
        if (footers.Count > 0)
        {
            Story(doc, HeaderFooterKind.Footer, footers[0], model, labels, settings, null, cancellationToken);
        }

        doc.Properties.Core.Description = "Reconstructed by OneDiff from " + sourceFormat + "; not a Word file originally.";
        doc.Properties.Core.Keywords = "reconstructed";
        using var ms = new MemoryStream();
        doc.SaveAs(ms, OfficeFormat.Docx, cancellationToken);
        byte[] bytes = ms.ToArray();

        CompareDocument read = new WordDocumentSource().Read(DocumentInput.FromBytes(bytes, model.Name + ".docx"), cancellationToken);
        return new ReconstructedDocx(bytes, Graft(model, read, settings, body, headers, footers, bodySections, furnitureSections));
    }

    /// <summary>
    /// The text a comparison under <paramref name="settings"/> works on: the node's text with every fold whose category is not hidden
    /// undone (the fold's original characters put back). <c>Map[i]</c> is where position <c>i</c> of the node text lands in it; a position
    /// inside an undone fold lands at the start of the characters that replace it.
    /// </summary>
    public static (string Text, int[] Map) Project(Node n, CompareSettings settings)
    {
        string text = n.Text;
        var map = new int[text.Length + 1];
        if (n.Folds.IsDefaultOrEmpty)
        {
            for (int i = 0; i < map.Length; i++)
            {
                map[i] = i;
            }

            return (text, map);
        }

        var sb = new StringBuilder(text.Length + 16);
        int pos = 0;
        foreach (NormalizationFold f in n.Folds)
        {
            for (int i = pos; i < f.Start; i++)
            {
                map[i] = sb.Length + (i - pos);
            }

            sb.Append(text, pos, f.Start - pos);
            bool stays = settings.Hidden.Contains(f.Category);
            for (int k = 0; k < f.Length; k++)
            {
                map[f.Start + k] = sb.Length + (stays ? k : 0);
            }

            sb.Append(stays ? text.AsSpan(f.Start, f.Length) : f.Original.AsSpan());
            pos = f.Start + f.Length;
        }

        for (int i = pos; i < text.Length; i++)
        {
            map[i] = sb.Length + (i - pos);
        }

        sb.Append(text, pos, text.Length - pos);
        map[text.Length] = sb.Length;
        return (sb.ToString(), map);
    }

    // ------------------------------------------------------------------------------------------------------------------ writing

    private static void Story(Document doc, HeaderFooterKind kind, int node, CompareDocument model, SortedDictionary<string, long> labels, CompareSettings settings, string? note, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (node >= 0)
        {
            foreach (int c in model.ChildrenOf(node))
            {
                Block(model, c, labels, settings, sb, ct);
            }
        }

        if (note is not null)
        {
            sb.Append("<w:p").Append(Decl).Append("><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:rPr><w:i/><w:color w:val=\"595959\"/><w:sz w:val=\"16\"/></w:rPr><w:t xml:space=\"preserve\">")
              .Append(Escape(note)).Append("</w:t></w:r></w:p>");
        }

        if (sb.Length == 0)
        {
            return;
        }

        HeaderFooter hf = doc.Sections[0].AddOwn(kind, HeaderFooterType.Default);
        RecursiveAI.OneOffice.Markup.MarkupNode container = hf.Container.Node;
        for (RecursiveAI.OneOffice.Markup.MarkupNode c = container.FirstChild; !c.IsNull;)
        {
            RecursiveAI.OneOffice.Markup.MarkupNode next = c.NextSibling;
            if (c.Kind == RecursiveAI.OneOffice.Markup.MarkupNodeKind.Element && c.LocalNameString == "p")
            {
                c.Remove();
            }

            c = next;
        }

        container.AppendXml(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void Block(CompareDocument m, int id, SortedDictionary<string, long> labels, CompareSettings settings, StringBuilder sb, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Node n = m[id];
        switch (n.Kind)
        {
            case NodeKind.Paragraph or NodeKind.ListItem:
                if (!m.ChildrenOf(id).IsDefaultOrEmpty)
                {
                    throw new NotSupportedException($"a {n.Kind} with child nodes (fields, pictures, notes) cannot be reconstructed");
                }

                Paragraph(n, labels, settings, sb);
                break;
            case NodeKind.Table:
                {
                    ImmutableArray<int> rows = m.ChildrenOf(id);
                    int cols = 1;
                    foreach (int r in rows)
                    {
                        cols = Math.Max(cols, m.ChildrenOf(r).Length);
                    }

                    sb.Append("<w:tbl").Append(Decl).Append("><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr><w:tblGrid>");
                    for (int i = 0; i < cols; i++)
                    {
                        sb.Append("<w:gridCol w:w=\"2000\"/>");
                    }

                    sb.Append("</w:tblGrid>");
                    foreach (int r in rows)
                    {
                        sb.Append("<w:tr>");
                        foreach (int c in m.ChildrenOf(r))
                        {
                            sb.Append("<w:tc><w:tcPr><w:tcW w:w=\"2000\" w:type=\"dxa\"/></w:tcPr>");
                            ImmutableArray<int> inner = m.ChildrenOf(c);
                            if (inner.IsDefaultOrEmpty)
                            {
                                sb.Append("<w:p/>");
                            }

                            foreach (int x in inner)
                            {
                                Block(m, x, labels, settings, sb, ct);
                            }

                            sb.Append("</w:tc>");
                        }

                        sb.Append("</w:tr>");
                    }

                    sb.Append("</w:tbl>");
                    break;
                }

            default:
                throw new NotSupportedException($"a {n.Kind} node cannot be reconstructed");
        }
    }

    private static void Paragraph(Node n, SortedDictionary<string, long> labels, CompareSettings settings, StringBuilder sb)
    {
        sb.Append("<w:p").Append(Decl).Append('>');
        if (n.Kind == NodeKind.ListItem && n.Attributes.Get(FormatKeys.ListLabel) is { Length: > 0 } label)
        {
            sb.Append("<w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"").Append(labels[label].ToString(CultureInfo.InvariantCulture)).Append("\"/></w:numPr></w:pPr>");
        }

        (string text, int[] map) = Project(n, settings);
        int pos = 0;
        foreach (FormatRun run in n.Runs)
        {
            int start = map[run.Start], end = map[run.End];
            if (end <= start || start < pos)
            {
                continue;
            }

            if (start > pos)
            {
                Run(sb, text.AsSpan(pos, start - pos), null);
            }

            Run(sb, text.AsSpan(start, end - start), run.Format);
            pos = end;
        }

        if (pos < text.Length)
        {
            Run(sb, text.AsSpan(pos), null);
        }

        sb.Append("</w:p>");
    }

    private static void Run(StringBuilder sb, ReadOnlySpan<char> text, FormatSet? f)
    {
        if (text.IsEmpty)
        {
            return;
        }

        sb.Append("<w:r>");
        if (f is not null)
        {
            RunProperties(sb, f);
        }

        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            char c = i < text.Length ? text[i] : '\0';
            if (i < text.Length && c != '\t' && c != '\n')
            {
                continue;
            }

            if (i > start)
            {
                sb.Append("<w:t xml:space=\"preserve\">").Append(Escape(text[start..i])).Append("</w:t>");
            }

            if (i < text.Length)
            {
                sb.Append(c == '\t' ? "<w:tab/>" : "<w:br/>");
            }

            start = i + 1;
        }

        sb.Append("</w:r>");
    }

    /// <summary>The run properties in the order of ECMA-376 Part 1 §17.3.2.28 (CT_RPr).</summary>
    private static void RunProperties(StringBuilder sb, FormatSet f)
    {
        var p = new StringBuilder();
        if (f.Get(FormatKeys.Font) is { Length: > 0 } font)
        {
            string e = Escape(font);
            p.Append("<w:rFonts w:ascii=\"").Append(e).Append("\" w:hAnsi=\"").Append(e).Append("\" w:cs=\"").Append(e).Append("\" w:eastAsia=\"").Append(e).Append("\"/>");
        }

        if (f.Get(FormatKeys.Bold) is not null)
        {
            p.Append("<w:b/>");
        }

        if (f.Get(FormatKeys.Italic) is not null)
        {
            p.Append("<w:i/>");
        }

        switch (f.Get(FormatKeys.Caps))
        {
            case "caps":
                p.Append("<w:caps/>");
                break;
            case "small":
                p.Append("<w:smallCaps/>");
                break;
            default:
                break;
        }

        switch (f.Get(FormatKeys.Strike))
        {
            case "1":
                p.Append("<w:strike/>");
                break;
            case "double":
                p.Append("<w:dstrike/>");
                break;
            default:
                break;
        }

        if (f.Get("run.hidden") is not null)
        {
            p.Append("<w:vanish/>");
        }

        if (f.Get(FormatKeys.Color) is { Length: > 0 } color)
        {
            string hex = color.TrimStart('#');
            if (hex.Length == 6)
            {
                p.Append("<w:color w:val=\"").Append(Escape(hex)).Append("\"/>");
            }
        }

        if (f.Get(FormatKeys.Size) is { } size && double.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out double pt) && pt > 0)
        {
            p.Append("<w:sz w:val=\"").Append(Math.Round(pt * 2).ToString(CultureInfo.InvariantCulture)).Append("\"/>");
        }

        if (f.Get(FormatKeys.Highlight) is { Length: > 0 } hl)
        {
            p.Append("<w:highlight w:val=\"").Append(Escape(hl)).Append("\"/>");
        }

        if (f.Get(FormatKeys.Underline) is { Length: > 0 } u)
        {
            p.Append("<w:u w:val=\"").Append(Escape(u)).Append("\"/>");
        }

        switch (f.Get(FormatKeys.VerticalAlign))
        {
            case "superscript":
                p.Append("<w:vertAlign w:val=\"superscript\"/>");
                break;
            case "subscript":
                p.Append("<w:vertAlign w:val=\"subscript\"/>");
                break;
            default:
                break;
        }

        if (p.Length > 0)
        {
            sb.Append("<w:rPr>").Append(p).Append("</w:rPr>");
        }
    }

    private static string Escape(ReadOnlySpan<char> s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"':
                    sb.Append("&quot;");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------------------------ re-anchoring

    private static CompareDocument Graft(CompareDocument m, CompareDocument read, CompareSettings settings, List<int> body, List<int> headers, List<int> footers, List<int> bodySections, List<int> furnitureSections)
    {
        var map = new Dictionary<int, int>(m.Count);
        int rs = read.Roots.FirstOrDefault(r => read[r].Kind == NodeKind.Section, -1);
        if (rs < 0)
        {
            throw new InvalidOperationException("the reconstructed package has no section");
        }

        var rBody = new List<int>();
        int rHeader = -1, rFooter = -1;
        foreach (int c in read.ChildrenOf(rs))
        {
            switch (read[c].Kind)
            {
                case NodeKind.Header:
                    rHeader = c;
                    break;
                case NodeKind.Footer:
                    rFooter = c;
                    break;
                default:
                    rBody.Add(c);
                    break;
            }
        }

        if (bodySections.Count > 0)
        {
            map[bodySections[0]] = rs;
        }

        Pair(m, read, settings, body, rBody, map);
        if (headers.Count > 0)
        {
            if (rHeader < 0)
            {
                throw new InvalidOperationException("the reconstructed package lost its header");
            }

            map[headers[0]] = rHeader;
            Pair(m, read, settings, [.. m.ChildrenOf(headers[0])], [.. read.ChildrenOf(rHeader)], map, allowTrailing: true);
        }

        if (footers.Count > 0)
        {
            if (rFooter < 0)
            {
                throw new InvalidOperationException("the reconstructed package lost its footer");
            }

            map[footers[0]] = rFooter;
            Pair(m, read, settings, [.. m.ChildrenOf(footers[0])], [.. read.ChildrenOf(rFooter)], map, allowTrailing: true);
        }

        var builder = new CompareDocumentBuilder(m.Kind).WithMetadata(m.Name, m.SourceHash, m.SourceFormat);
        var open = new Stack<int>();
        foreach (Node n in m.Nodes)
        {
            while (open.Count > 0 && open.Peek() != n.Parent)
            {
                builder.End();
                open.Pop();
            }

            Node? r = map.TryGetValue(n.Id, out int rid) ? read[rid] : null;
            // The node read back has the text, folds, segments and anchor that fit the package; the model's own (a PDF's quadrilaterals,
            // its line-break folds) do not exist in it. Nodes with no counterpart keep their text and lose their anchor.
            NodeContent content = r is null
                ? new NodeContent { Text = n.Text, Runs = n.Runs, Attributes = n.Attributes, Folds = n.Folds }
                : new NodeContent { Text = r.Text, Runs = r.Runs, Attributes = r.Attributes, Anchor = r.Anchor, Segments = r.Segments, Folds = r.Folds };
            builder.Begin(n.Kind, content);
            open.Push(n.Id);
        }

        while (open.Count > 0)
        {
            builder.End();
            open.Pop();
        }

        foreach (OpaquePart o in m.Opaque)
        {
            builder.AddOpaque(o.Name, o.Hash, o.Anchor);
        }

        return builder.Build();
    }

    /// <summary>Pairs the model's blocks with the blocks read back, in order, kind by kind, requiring the same text.</summary>
    private static void Pair(CompareDocument m, CompareDocument read, CompareSettings settings, List<int> mine, List<int> theirs, Dictionary<int, int> map, bool allowTrailing = false)
    {
        if (mine.Count > theirs.Count || (!allowTrailing && mine.Count != theirs.Count))
        {
            throw new InvalidOperationException($"the reconstructed package holds {theirs.Count} blocks where the model has {mine.Count}");
        }

        for (int i = 0; i < mine.Count; i++)
        {
            Node a = m[mine[i]], b = read[theirs[i]];
            bool para = a.Kind is NodeKind.Paragraph or NodeKind.ListItem;
            if (a.Kind != b.Kind && !(para && b.Kind is NodeKind.Paragraph or NodeKind.ListItem))
            {
                throw new InvalidOperationException($"node {a.Id}: the model has a {a.Kind}, the package a {b.Kind}");
            }

            if (para)
            {
                string want = Project(a, settings).Text, got = Project(b, settings).Text;
                if (!string.Equals(want, got, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"node {a.Id}: the package reads back as '{Clip(got)}' where the model has '{Clip(want)}'");
                }

                if (a.Kind == NodeKind.ListItem && !string.Equals(a.Attributes.Get(FormatKeys.ListLabel), b.Attributes.Get(FormatKeys.ListLabel), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"node {a.Id}: the list label reads back as '{b.Attributes.Get(FormatKeys.ListLabel)}' where the model has '{a.Attributes.Get(FormatKeys.ListLabel)}'");
                }
            }

            map[a.Id] = b.Id;
            if (!para)
            {
                ImmutableArray<int> ac = m.ChildrenOf(a.Id);
                var bc = new List<int>(read.ChildrenOf(b.Id));
                // A cell with no blocks is written with one empty paragraph, because a cell needs one.
                Pair(m, read, settings, [.. ac], bc, map, allowTrailing: a.Kind == NodeKind.TableCell && ac.IsDefaultOrEmpty);
            }
        }
    }

    private static string Clip(string s) => s.Length <= 60 ? s : s[..60] + "...";
}
