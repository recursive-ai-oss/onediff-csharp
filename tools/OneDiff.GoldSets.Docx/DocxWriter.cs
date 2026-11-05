using System.Globalization;
using System.IO.Compression;
using System.Text;
using RecursiveAI.OneDocument;
using RecursiveAI.OneDocument.Documents;
using RecursiveAI.OneDocument.Packaging;

namespace OneDiff.GoldSets.Docx;

/// <summary>How a document looks: chosen from the pair's id, so the old and new side of a pair look alike and a rerun gives the same files.</summary>
internal sealed record Look(int Variant, string Latin, int Size, int After, int Line, bool Justify, bool PageA4, int Margin, bool TitleCentered, bool ShadedHeader, bool Bookmarks, bool Notes, bool ControlOnTitle, string Logo);

/// <summary>One node of a gold document, as the writer reads it.</summary>
internal sealed record GNode(int Id, string Kind, int Parent, string Text, string Label, int Level, string Style, List<(int Start, int Length)> Bold);

/// <summary>What a rendered side reports: the DOCX bytes and where each gold content block went.</summary>
internal sealed record Rendered(byte[] Bytes, List<GoldBlock> Blocks, int Paragraphs);

/// <summary>
/// Writes a gold document as a Word document with the structures real contracts have: multilevel numbering with list labels worked
/// out by the numbering definition (not typed in the text), styles, a table with a repeating header row, a signature box with merged
/// cells, a title carrying a footnote and a comment, cross-references as fields with bookmarks, a header with a picture, a footer
/// with page-number fields, and for some looks a content control. The package is assembled with OneDocument's package model, saved by
/// its writer and read back by its document model (see <see cref="Check"/>).
/// </summary>
internal static class DocxWriter
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Ns = " xmlns:w=\"" + W + "\" xmlns:r=\"" + R + "\"";
    private const string WpNs = " xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"";
    private const string Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    private static readonly string[] Latin = ["Calibri", "Times New Roman", "Arial", "Cambria", "Calibri", "Arial"];
    private static readonly string[] Logos = ["1F4E79", "7F6000", "375623", "833C0B", "3B3838", "7030A0"];

    /// <summary>
    /// Six looks, chosen from the pair's id. The first three differ in fonts, page, spacing and table style only. The others add what real
    /// contracts also carry and some tools do not survive: cross-reference fields with bookmarks (3), a footnote and a comment on the
    /// title (4), and all of those with a content control around the title (5).
    /// </summary>
    public static Look LookFor(string pairId)
    {
        byte[] h = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(pairId));
        int v = h[0] % 6;
        return new Look(v, Latin[v], v switch { 0 => 22, 1 => 24, 2 => 20, 3 => 21, 4 => 22, _ => 20 }, v switch { 0 => 160, 1 => 120, 2 => 200, 3 => 100, 4 => 140, _ => 180 },
            v switch { 0 => 259, 1 => 276, 2 => 240, 3 => 300, 4 => 259, _ => 264 }, v is 0 or 3 or 5, v is 1 or 3 or 4, v switch { 0 => 1440, 1 => 1134, 2 => 1701, 3 => 1247, 4 => 1304, _ => 1440 },
            v is 1 or 2 or 5, v is 2 or 3, v >= 3, v >= 4, v == 5, Logos[v]);
    }

    private static string E(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (c >= ' ' || c is '\t' or '\n' or '\r')
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private static string RunProps(bool bold, bool rtl) =>
        bold || rtl ? "<w:rPr>" + (bold ? "<w:b/><w:bCs/>" : string.Empty) + (rtl ? "<w:rtl/>" : string.Empty) + "</w:rPr>" : string.Empty;

    private static string Run(string text, bool bold, bool rtl) =>
        text.Length == 0 ? string.Empty : "<w:r>" + RunProps(bold, rtl) + "<w:t xml:space=\"preserve\">" + E(text) + "</w:t></w:r>";

    private static string Field(string instruction, string result, bool rtl) =>
        "<w:r>" + RunProps(false, rtl) + "<w:fldChar w:fldCharType=\"begin\"/></w:r>"
        + "<w:r>" + RunProps(false, rtl) + "<w:instrText xml:space=\"preserve\"> " + E(instruction) + " </w:instrText></w:r>"
        + "<w:r>" + RunProps(false, rtl) + "<w:fldChar w:fldCharType=\"separate\"/></w:r>"
        + Run(result, false, rtl)
        + "<w:r>" + RunProps(false, rtl) + "<w:fldChar w:fldCharType=\"end\"/></w:r>";

    private static string Bookmark(string label) => "_Ref_" + label.TrimEnd('.').Replace('.', '_');

    /// <summary>Splits a plain stretch of text at the numbers that name a numbered clause of this document, which become cross-reference fields.</summary>
    private static string PlainRuns(string text, HashSet<string> labels, bool rtl)
    {
        if (labels.Count == 0)
        {
            return Run(text, false, rtl);
        }

        var sb = new StringBuilder();
        int pos = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]) || (i > 0 && (char.IsAsciiDigit(text[i - 1]) || text[i - 1] is '.' or ',')))
            {
                continue;
            }

            int j = i;
            while (j < text.Length && char.IsAsciiDigit(text[j]))
            {
                j++;
            }

            if (j + 1 < text.Length && text[j] == '.' && char.IsAsciiDigit(text[j + 1]))
            {
                int k = j + 1;
                while (k < text.Length && char.IsAsciiDigit(text[k]))
                {
                    k++;
                }

                bool more = k < text.Length && (text[k] is '.' or ',') && k + 1 < text.Length && char.IsAsciiDigit(text[k + 1]);
                string token = text[i..k];
                if (!more && labels.Contains(token))
                {
                    sb.Append(Run(text[pos..i], false, rtl));
                    sb.Append(Field("REF " + Bookmark(token) + " \\r \\h", token, rtl));
                    pos = k;
                }

                i = k;
            }
            else
            {
                i = j;
            }
        }

        sb.Append(Run(text[pos..], false, rtl));
        return sb.ToString();
    }

    private static string Runs(GNode n, HashSet<string> labels, bool rtl)
    {
        var sb = new StringBuilder();
        int pos = 0;
        foreach ((int start, int len) in n.Bold.OrderBy(b => b.Start))
        {
            if (start > pos)
            {
                sb.Append(PlainRuns(n.Text[pos..start], labels, rtl));
            }

            sb.Append(Run(n.Text.Substring(start, len), true, rtl));
            pos = start + len;
        }

        if (pos < n.Text.Length)
        {
            sb.Append(PlainRuns(n.Text[pos..], labels, rtl));
        }

        return sb.ToString();
    }

    /// <summary>Renders one side of a pair.</summary>
    /// <param name="nodes">The gold document's nodes; node 0 is the section.</param>
    /// <param name="lang">Language code of the pair.</param>
    /// <param name="look">The look chosen for the pair.</param>
    /// <param name="path">Where the file is written.</param>
    public static Rendered Write(List<GNode> nodes, string lang, Look look, string path)
    {
        bool rtl = lang == "ar";
        var labels = look.Bookmarks ? new HashSet<string>(nodes.Where(n => n.Level == 2 && n.Label.Length > 0).Select(n => n.Label), StringComparer.Ordinal) : [];
        var body = new StringBuilder();
        var blocks = new List<GoldBlock>();
        int para = 0;
        int bookmarkId = 1;
        var children = nodes.ToLookup(n => n.Parent);

        string ParaXml(GNode n, bool inCell, bool decorate)
        {
            string style = n.Style == "Title" ? "Title" : n.Style == "Heading1" ? "Heading1" : n.Level == 2 ? "Clause" : inCell ? "TableText" : "BodyText";
            var sb = new StringBuilder("<w:p><w:pPr><w:pStyle w:val=\"" + style + "\"/>");
            if (n.Level > 0)
            {
                sb.Append("<w:numPr><w:ilvl w:val=\"" + (n.Level - 1).ToString(CultureInfo.InvariantCulture) + "\"/><w:numId w:val=\"1\"/></w:numPr>");
            }

            if (rtl)
            {
                sb.Append("<w:bidi/>");
            }

            sb.Append("</w:pPr>");
            int bm = -1;
            if (look.Bookmarks && n.Level == 2 && n.Label.Length > 0)
            {
                bm = bookmarkId++;
                sb.Append("<w:bookmarkStart w:id=\"" + bm.ToString(CultureInfo.InvariantCulture) + "\" w:name=\"" + Bookmark(n.Label) + "\"/>");
            }

            if (decorate)
            {
                sb.Append("<w:commentRangeStart w:id=\"0\"/>");
            }

            sb.Append(Runs(n, labels, rtl));
            if (decorate)
            {
                sb.Append("<w:commentRangeEnd w:id=\"0\"/><w:r><w:rPr><w:rStyle w:val=\"CommentReference\"/></w:rPr><w:commentReference w:id=\"0\"/></w:r>");
                sb.Append("<w:r><w:rPr><w:rStyle w:val=\"FootnoteReference\"/></w:rPr><w:footnoteReference w:id=\"1\"/></w:r>");
            }

            if (bm >= 0)
            {
                sb.Append("<w:bookmarkEnd w:id=\"" + bm.ToString(CultureInfo.InvariantCulture) + "\"/>");
            }

            sb.Append("</w:p>");
            return sb.ToString();
        }

        void Content(GNode n, bool inCell, bool decorate)
        {
            blocks.Add(new GoldBlock(n.Id, n.Kind, n.Label, n.Text, Paragraph: para));
            para++;
            string xml = ParaXml(n, inCell, decorate);
            if (look.ControlOnTitle && n.Style == "Title")
            {
                xml = "<w:sdt><w:sdtPr><w:alias w:val=\"Title\"/><w:tag w:val=\"document-title\"/><w:id w:val=\"1001\"/><w:text/></w:sdtPr><w:sdtContent>" + xml + "</w:sdtContent></w:sdt>";
            }

            body.Append(xml);
        }

        bool decorated = false;
        foreach (GNode top in children[0])
        {
            switch (top.Kind)
            {
                case "Paragraph" or "ListItem":
                    bool decorate = look.Notes && !decorated && top.Style == "Title";
                    decorated |= decorate;
                    Content(top, false, decorate);
                    break;
                case "Table":
                    var rows = children[top.Id].ToList();
                    int cols = Math.Max(1, rows.Count == 0 ? 1 : children[rows[0].Id].Count());
                    body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/>" + (rtl ? "<w:bidiVisual/>" : string.Empty)
                        + "<w:tblLook w:val=\"04A0\" w:firstRow=\"1\" w:lastRow=\"0\" w:firstColumn=\"1\" w:lastColumn=\"0\" w:noHBand=\"0\" w:noVBand=\"1\"/></w:tblPr><w:tblGrid>");
                    for (int c = 0; c < cols; c++)
                    {
                        body.Append("<w:gridCol w:w=\"" + (c == 0 ? 3600 : 2700).ToString(CultureInfo.InvariantCulture) + "\"/>");
                    }

                    body.Append("</w:tblGrid>");
                    for (int r = 0; r < rows.Count; r++)
                    {
                        body.Append("<w:tr>" + (r == 0 ? "<w:trPr><w:cantSplit/><w:tblHeader/></w:trPr>" : string.Empty));
                        foreach (GNode cell in children[rows[r].Id])
                        {
                            body.Append("<w:tc><w:tcPr><w:tcW w:w=\"" + (cell == children[rows[r].Id].First() ? 3600 : 2700).ToString(CultureInfo.InvariantCulture) + "\" w:type=\"dxa\"/>"
                                + (r == 0 && look.ShadedHeader ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"D9E2F3\"/>" : string.Empty) + "</w:tcPr>");
                            List<GNode> inner = [.. children[cell.Id]];
                            foreach (GNode p in inner)
                            {
                                Content(p, true, false);
                            }

                            if (inner.Count == 0)
                            {
                                body.Append("<w:p/>");
                                para++;
                            }

                            body.Append("</w:tc>");
                        }

                        body.Append("</w:tr>");
                    }

                    body.Append("</w:tbl><w:p/>");
                    para++;
                    break;
                default:
                    break;
            }
        }

        // A signature box: a merged top cell and a two-row merged left cell, all empty. It carries no text, so it cannot change
        // what a comparison of the content sees, but a comparison tool has to cope with the merges.
        body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"4500\"/><w:gridCol w:w=\"4500\"/></w:tblGrid>"
            + "<w:tr><w:tc><w:tcPr><w:tcW w:w=\"9000\" w:type=\"dxa\"/><w:gridSpan w:val=\"2\"/></w:tcPr><w:p/></w:tc></w:tr>"
            + "<w:tr><w:tc><w:tcPr><w:tcW w:w=\"4500\" w:type=\"dxa\"/><w:vMerge w:val=\"restart\"/></w:tcPr><w:p/></w:tc><w:tc><w:tcPr><w:tcW w:w=\"4500\" w:type=\"dxa\"/></w:tcPr><w:p/></w:tc></w:tr>"
            + "<w:tr><w:tc><w:tcPr><w:tcW w:w=\"4500\" w:type=\"dxa\"/><w:vMerge/></w:tcPr><w:p/></w:tc><w:tc><w:tcPr><w:tcW w:w=\"4500\" w:type=\"dxa\"/></w:tcPr><w:p/></w:tc></w:tr></w:tbl>");
        para += 5;
        body.Append("<w:p/>");
        para++;

        (int pw, int ph) = look.PageA4 ? (11906, 16838) : (12240, 15840);
        string document = Xml + "<w:document" + Ns + "><w:body>" + body
            + "<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rId6\"/><w:footerReference w:type=\"default\" r:id=\"rId7\"/>"
            + "<w:pgSz w:w=\"" + pw.ToString(CultureInfo.InvariantCulture) + "\" w:h=\"" + ph.ToString(CultureInfo.InvariantCulture) + "\"/>"
            + "<w:pgMar w:top=\"" + look.Margin.ToString(CultureInfo.InvariantCulture) + "\" w:right=\"" + look.Margin.ToString(CultureInfo.InvariantCulture) + "\" w:bottom=\"" + look.Margin.ToString(CultureInfo.InvariantCulture)
            + "\" w:left=\"" + look.Margin.ToString(CultureInfo.InvariantCulture) + "\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";

        using OpcPackage package = OpcPackage.Create(new OpenContext().Limits);
        package.SetRelationships(null, [new Relationship("rId1", R + "/officeDocument", "word/document.xml")]);
        package.AddPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", Encoding.UTF8.GetBytes(document));
        package.AddPart("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml", Encoding.UTF8.GetBytes(Styles(look, lang)));
        package.AddPart("/word/numbering.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml", Encoding.UTF8.GetBytes(Numbering(look)));
        package.AddPart("/word/settings.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml", Encoding.UTF8.GetBytes(
            Xml + "<w:settings" + Ns + "><w:zoom w:percent=\"100\"/><w:defaultTabStop w:val=\"720\"/>"
            + (look.Notes ? "<w:footnotePr><w:footnote w:id=\"-1\"/><w:footnote w:id=\"0\"/></w:footnotePr>" : string.Empty) + "</w:settings>"));
        if (look.Notes)
        {
        package.AddPart("/word/footnotes.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml", Encoding.UTF8.GetBytes(
            Xml + "<w:footnotes" + Ns + "><w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>"
            + "<w:footnote w:type=\"continuationSeparator\" w:id=\"0\"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>"
            + "<w:footnote w:id=\"1\"><w:p><w:pPr><w:pStyle w:val=\"FootnoteText\"/></w:pPr><w:r><w:rPr><w:rStyle w:val=\"FootnoteReference\"/></w:rPr><w:footnoteRef/></w:r><w:r><w:t xml:space=\"preserve\"> Form agreement.</w:t></w:r></w:p></w:footnote></w:footnotes>"));
        package.AddPart("/word/comments.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml", Encoding.UTF8.GetBytes(
            Xml + "<w:comments" + Ns + "><w:comment w:id=\"0\" w:author=\"Reviewer\" w:date=\"2026-01-01T00:00:00Z\" w:initials=\"R\"><w:p><w:pPr><w:pStyle w:val=\"CommentText\"/></w:pPr>"
            + "<w:r><w:rPr><w:rStyle w:val=\"CommentReference\"/></w:rPr><w:annotationRef/></w:r><w:r><w:t>Check before signing.</w:t></w:r></w:p></w:comment></w:comments>"));
        }

        package.AddPart("/word/header1.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml", Encoding.UTF8.GetBytes(Header(look, rtl)));
        package.AddPart("/word/footer1.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml", Encoding.UTF8.GetBytes(
            Xml + "<w:ftr" + Ns + "><w:p><w:pPr><w:pStyle w:val=\"Footer\"/><w:jc w:val=\"center\"/></w:pPr><w:r><w:t xml:space=\"preserve\">Page </w:t></w:r>"
            + "<w:fldSimple w:instr=\" PAGE \"><w:r><w:t>1</w:t></w:r></w:fldSimple><w:r><w:t xml:space=\"preserve\"> of </w:t></w:r><w:fldSimple w:instr=\" NUMPAGES \"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p></w:ftr>"));
        package.AddPart("/word/media/logo.png", "image/png", Png(look.Logo));
        var relationships = new List<Relationship>
        {
            new("rId1", R + "/styles", "styles.xml"),
            new("rId2", R + "/numbering", "numbering.xml"),
            new("rId3", R + "/settings", "settings.xml"),
            new("rId6", R + "/header", "header1.xml"),
            new("rId7", R + "/footer", "footer1.xml"),
        };
        if (look.Notes)
        {
            relationships.Add(new Relationship("rId4", R + "/footnotes", "footnotes.xml"));
            relationships.Add(new Relationship("rId5", R + "/comments", "comments.xml"));
        }

        package.SetRelationships("/word/document.xml", relationships);
        package.SetRelationships("/word/header1.xml", [new Relationship("rId1", R + "/image", "media/logo.png")]);

        using Document doc = Document.FromPackage(package, new OpenContext(), ownsPackage: false);
        doc.SaveAs(path);
        return new Rendered(File.ReadAllBytes(path), blocks, para);
    }

    private static string Header(Look look, bool rtl) =>
        Xml + "<w:hdr" + Ns + WpNs + "><w:p><w:pPr><w:pStyle w:val=\"Header\"/><w:jc w:val=\"right\"/>" + (rtl ? "<w:bidi/>" : string.Empty) + "</w:pPr>"
        + "<w:r><w:t>CONFIDENTIAL</w:t></w:r><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"228600\" cy=\"228600\"/>"
        + "<wp:docPr id=\"1\" name=\"Logo\" descr=\"Logo\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>"
        + "<pic:nvPicPr><pic:cNvPr id=\"0\" name=\"logo.png\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"rId1\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
        + "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"228600\" cy=\"228600\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p></w:hdr>";

    private static string Numbering(Look look) =>
        Xml + "<w:numbering" + Ns + "><w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"multilevel\"/>"
        + "<w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:pStyle w:val=\"Heading1\"/><w:lvlText w:val=\"%1.\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"432\" w:hanging=\"432\"/></w:pPr></w:lvl>"
        + "<w:lvl w:ilvl=\"1\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:pStyle w:val=\"Clause\"/><w:lvlText w:val=\"%1.%2\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\""
        + (look.Variant == 2 ? "720" : "576") + "\" w:hanging=\"" + (look.Variant == 2 ? "720" : "576") + "\"/></w:pPr></w:lvl></w:abstractNum>"
        + "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>";

    private static string Styles(Look look, string lang)
    {
        string latin = look.Latin;
        string east = lang == "zh" ? "SimSun" : lang == "ja" ? "MS Mincho" : latin;
        string cs = lang == "ar" ? "Arial" : latin;
        string langTag = lang switch { "en" => "en-US", "de" => "de-DE", "fr" => "fr-FR", "es" => "es-ES", "zh" => "zh-CN", "ja" => "ja-JP", _ => "ar-SA" };
        string jc = look.Justify ? "<w:jc w:val=\"both\"/>" : string.Empty;
        string sz = look.Size.ToString(CultureInfo.InvariantCulture);
        string after = look.After.ToString(CultureInfo.InvariantCulture);
        string line = look.Line.ToString(CultureInfo.InvariantCulture);
        return Xml + "<w:styles" + Ns + "><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"" + latin + "\" w:hAnsi=\"" + latin + "\" w:eastAsia=\"" + east + "\" w:cs=\"" + cs + "\"/>"
            + "<w:sz w:val=\"" + sz + "\"/><w:szCs w:val=\"" + sz + "\"/><w:lang w:val=\"" + langTag + "\" w:eastAsia=\"" + (lang == "zh" ? "zh-CN" : lang == "ja" ? "ja-JP" : "en-US") + "\" w:bidi=\"ar-SA\"/></w:rPr></w:rPrDefault>"
            + "<w:pPrDefault><w:pPr><w:spacing w:after=\"" + after + "\" w:line=\"" + line + "\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>"
            + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"BodyText\"><w:name w:val=\"Body Text\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr>" + jc + "</w:pPr></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"BodyText\"/><w:qFormat/><w:pPr><w:keepNext/>"
            + (look.TitleCentered ? "<w:jc w:val=\"center\"/>" : string.Empty) + "<w:spacing w:before=\"240\" w:after=\"240\"/></w:pPr><w:rPr><w:b/><w:bCs/><w:sz w:val=\"" + (look.Size + 12).ToString(CultureInfo.InvariantCulture) + "\"/></w:rPr></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Clause\"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"80\"/><w:outlineLvl w:val=\"0\"/></w:pPr>"
            + "<w:rPr><w:b/><w:bCs/><w:sz w:val=\"" + (look.Size + 2).ToString(CultureInfo.InvariantCulture) + "\"/></w:rPr></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"Clause\"><w:name w:val=\"Clause\"/><w:basedOn w:val=\"BodyText\"/><w:qFormat/></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"TableText\"><w:name w:val=\"Table Text\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:before=\"40\" w:after=\"40\"/></w:pPr></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"Header\"><w:name w:val=\"header\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"0\"/></w:pPr><w:rPr><w:sz w:val=\"16\"/></w:rPr></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"Footer\"><w:name w:val=\"footer\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"0\"/></w:pPr><w:rPr><w:sz w:val=\"16\"/></w:rPr></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"FootnoteText\"><w:name w:val=\"footnote text\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:rPr><w:sz w:val=\"18\"/></w:rPr></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"CommentText\"><w:name w:val=\"annotation text\"/><w:basedOn w:val=\"Normal\"/><w:rPr><w:sz w:val=\"20\"/></w:rPr></w:style>"
            + "<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/><w:uiPriority w:val=\"1\"/><w:semiHidden/></w:style>"
            + "<w:style w:type=\"character\" w:styleId=\"FootnoteReference\"><w:name w:val=\"footnote reference\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr></w:style>"
            + "<w:style w:type=\"character\" w:styleId=\"CommentReference\"><w:name w:val=\"annotation reference\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:rPr><w:sz w:val=\"16\"/></w:rPr></w:style>"
            + "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:semiHidden/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>"
            + "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:basedOn w:val=\"TableNormal\"/><w:tblPr><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/><w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/>"
            + "<w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/><w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/></w:tblBorders></w:tblPr></w:style></w:styles>";
    }

    // ---- a tiny PNG for the header picture --------------------------------------------------------------------------

    private static byte[] Png(string rgb)
    {
        byte r = Convert.ToByte(rgb[..2], 16), g = Convert.ToByte(rgb.Substring(2, 2), 16), b = Convert.ToByte(rgb.Substring(4, 2), 16);
        const int size = 8;
        var raw = new MemoryStream();
        for (int y = 0; y < size; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < size; x++)
            {
                raw.WriteByte(r);
                raw.WriteByte(g);
                raw.WriteByte(b);
            }
        }

        var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.NoCompression, leaveOpen: true))
        {
            z.Write(raw.ToArray());
        }

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", [0, 0, 0, size, 0, 0, 0, size, 8, 2, 0, 0, 0]);
        Chunk(png, "IDAT", idat.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] td = [.. Encoding.ASCII.GetBytes(type), .. data];
        s.Write(td);
        uint crc = 0xFFFFFFFF;
        foreach (byte x in td)
        {
            crc ^= x;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, ~crc);
        s.Write(len);
    }

    // ---- reading it back through OneDocument ----------------------------------------------------------------------------

    /// <summary>
    /// Opens a rendered file with OneDocument and compares it with what was meant: every block is found at its paragraph with the same
    /// text, and every list label worked out by the numbering definition equals the gold label.
    /// </summary>
    public static (int Located, int Total, int LabelsOk, int LabelsTotal) Check(string path, List<GoldBlock> blocks, List<GNode> nodes)
    {
        using Document doc = Document.Open(path);
        List<Paragraph> paragraphs = [.. doc.Body.Paragraphs()];
        int located = 0;
        foreach (GoldBlock b in blocks)
        {
            if (b.Paragraph >= 0 && b.Paragraph < paragraphs.Count && paragraphs[b.Paragraph].Text == b.Text)
            {
                located++;
            }
        }

        var labelOf = new Dictionary<int, string>();
        foreach (RecursiveAI.OneDocument.Documents.Numbering.ListLabel l in doc.Numbering.LabelBody())
        {
            int idx = paragraphs.FindIndex(p => p.Equals(l.Paragraph));
            if (idx >= 0)
            {
                labelOf[idx] = l.Text;
            }
        }

        int ok = 0, total = 0;
        foreach (GoldBlock b in blocks.Where(b => b.Label.Length > 0))
        {
            total++;
            if (labelOf.TryGetValue(b.Paragraph, out string? t) && t == b.Label)
            {
                ok++;
            }
        }

        return (located, blocks.Count, ok, total);
    }
}
