using System.Globalization;
using System.Text;
using RecursiveAI.OneOffice;
using RecursiveAI.OneOffice.Documents;
using RecursiveAI.OneOffice.Documents.Numbering;
using RecursiveAI.OneOffice.Documents.Styles;
using RecursiveAI.OneOffice.Packaging;

namespace OneDiff.Word.Tests;

// ---- a small description of a document, written here and rendered through OneOffice's authoring API ---------------------------------------

internal abstract record Inl;

internal sealed record T(string Text) : Inl
{
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public string? Underline { get; init; }
    public string? Color { get; init; }
    public int? Size { get; init; }
    public string? Font { get; init; }
    public string? Link { get; init; }
}

internal sealed record Fld(string Code, string Result) : Inl;

internal sealed record SimpleFld(string Code, string Result) : Inl;

internal sealed record Foot(string Text, bool End = false) : Inl;

internal sealed record Img(byte[] Png, string Alt) : Inl;

internal sealed record Box(string Text) : Inl;

internal sealed record Cc(string Text, string Tag) : Inl;

internal sealed record TabChar : Inl;

internal sealed record Brk : Inl;

internal abstract record Blk;

internal sealed record Para(params Inl[] Inlines) : Blk
{
    public string? Style { get; init; }
    public int List { get; init; } = -1;
    public int Level { get; init; }
    public string? Jc { get; init; }
    public bool KeepNext { get; init; }
    public int? IndentLeft { get; init; }
    public string? Comment { get; init; }
    public bool SectionBreak { get; init; }
    public bool Landscape { get; init; }

    public static Para Of(string text) => new(new T(text));
}

internal sealed record Cell(params Blk[] Blocks)
{
    public int Span { get; init; } = 1;
    public string? VMerge { get; init; }
}

internal sealed record Tbl(params Cell[][] Rows) : Blk
{
    public string? Style { get; init; }
}

internal sealed record Sdt(string Tag, params Blk[] Blocks) : Blk;

internal sealed record DocSpec(params Blk[] Blocks)
{
    /// <summary>The number of multi-level list definitions to create (decimal, lower letter, lower roman); paragraphs refer to them by index.</summary>
    public int Lists { get; init; }

    /// <summary>A bullet list definition is added after the numbered ones when true; its index is <see cref="Lists"/>.</summary>
    public bool Bullets { get; init; }

    public string? Header { get; init; }
    public string? Footer { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? CustomXml { get; init; }
}

internal static class DocFactory
{
    private const string Decl =
        " xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\""
        + " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\""
        + " xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\""
        + " xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\""
        + " xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\""
        + " xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\""
        + " xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\""
        + " xmlns:v=\"urn:schemas-microsoft-com:vml\"";

    /// <summary>A valid 1x1 PNG; <paramref name="shade"/> picks the pixel's grey so that images differ.</summary>
    public static byte[] Png(byte shade)
    {
        // 8-bit greyscale, one pixel; the zlib stream is stored (no compression) so no compressor is needed.
        byte[] ihdr = [0, 0, 0, 1, 0, 0, 0, 1, 8, 0, 0, 0, 0];
        byte[] raw = [0, shade];
        byte[] z = [0x78, 0x01, 0x01, 0x02, 0x00, 0xFD, 0xFF, 0, shade, 0, 0, 0, 0];
        uint adler = Adler(raw);
        z[^4] = (byte)(adler >> 24);
        z[^3] = (byte)(adler >> 16);
        z[^2] = (byte)(adler >> 8);
        z[^1] = (byte)adler;
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", z);
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>A greyscale PNG of <paramref name="width"/> x <paramref name="height"/> whose pixel at (x, y) is <paramref name="pixel"/>(x, y), with filter type <paramref name="filter"/> (0 none, 1 sub, 2 up) on every row.</summary>
    public static byte[] PngGray(int width, int height, Func<int, int, byte> pixel, byte filter = 0)
    {
        var raw = new byte[(width + 1) * height];
        var plain = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                plain[(y * width) + x] = pixel(x, y);
            }
        }

        for (int y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = filter;
            for (int x = 0; x < width; x++)
            {
                byte v = plain[(y * width) + x];
                int left = x > 0 ? plain[(y * width) + x - 1] : 0, up = y > 0 ? plain[((y - 1) * width) + x] : 0;
                raw[(y * (width + 1)) + 1 + x] = (byte)(v - (filter == 1 ? left : filter == 2 ? up : 0));
            }
        }

        using var z = new MemoryStream();
        z.Write([0x78, 0x01]);
        for (int at = 0; at < raw.Length || at == 0; at += 65535)
        {
            int n = Math.Min(65535, raw.Length - at);
            bool last = at + n >= raw.Length;
            z.WriteByte((byte)(last ? 1 : 0));
            z.Write([(byte)n, (byte)(n >> 8), (byte)~n, (byte)(~n >> 8)]);
            z.Write(raw, at, n);
            if (last)
            {
                break;
            }
        }

        uint adler = Adler(raw);
        z.Write([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler]);
        byte[] ihdr = [0, 0, (byte)(width >> 8), (byte)width, 0, 0, (byte)(height >> 8), (byte)height, 8, 0, 0, 0, 0];
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static uint Adler(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        byte[] t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte x in t.Concat(data))
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

    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => c.ToString() });
        }

        return sb.ToString();
    }

    private sealed class Context(Document doc, DocSpec spec, List<long> listIds)
    {
        public readonly Document Doc = doc;
        public readonly DocSpec Spec = spec;
        public readonly List<long> ListIds = listIds;
        public readonly List<Relationship> NewRelationships = [];
        public readonly List<(string Name, byte[] Bytes)> Media = [];
        public readonly HashSet<string> Styles = [];
        public int Counter;
    }

    public static byte[] Build(DocSpec spec)
    {
        using Document doc = Document.Create();
        var lists = new List<long>();
        for (int i = 0; i < spec.Lists; i++)
        {
            long abs = doc.Numbering.AddAbstractNumbering(
                [new NumberingDefinitions.LevelSpec("decimal", "%1."), new NumberingDefinitions.LevelSpec("lowerLetter", "%2)", 1, 1440), new NumberingDefinitions.LevelSpec("lowerRoman", "%3.", 1, 2160)]);
            lists.Add(doc.Numbering.AddInstance(abs));
        }

        if (spec.Bullets)
        {
            long abs = doc.Numbering.AddAbstractNumbering([new NumberingDefinitions.LevelSpec("bullet", "\uF0B7")]);
            lists.Add(doc.Numbering.AddInstance(abs));
        }

        var cx = new Context(doc, spec, lists);
        var blocks = new List<string>();
        foreach (Blk b in spec.Blocks)
        {
            blocks.Add(RenderBlock(b, cx));
        }

        foreach (string style in cx.Styles)
        {
            if (doc.Styles.Find(style) is null)
            {
                doc.Styles.AddStyle(style, style, StyleType.Paragraph);
            }
        }

        RecursiveAI.OneOffice.Markup.MarkupNode body = doc.Body.Container.Node;
        RecursiveAI.OneOffice.Markup.MarkupNode sect = doc.Body.SectionProperties!.Value.Node;
        for (RecursiveAI.OneOffice.Markup.MarkupNode c = body.FirstChild; !c.IsNull; c = c.NextSibling)
        {
            if (c.Kind == RecursiveAI.OneOffice.Markup.MarkupNodeKind.Element && c.LocalNameString == "p")
            {
                c.Remove();
                break;
            }
        }

        foreach (string xml in blocks)
        {
            sect.InsertXmlBefore(Encoding.UTF8.GetBytes(xml));
        }

        if (spec.Header is not null)
        {
            doc.Sections[0].AddOwn(HeaderFooterKind.Header, HeaderFooterType.Default).Paragraphs().First().SetText(spec.Header);
        }

        if (spec.Footer is not null)
        {
            doc.Sections[0].AddOwn(HeaderFooterKind.Footer, HeaderFooterType.Default).Paragraphs().First().SetText(spec.Footer);
        }

        foreach (Para p in AllParas(spec.Blocks).Where(p => p.Comment is not null))
        {
            string text = PlainText(p);
            Paragraph? hp = doc.Body.Paragraphs().FirstOrDefault(x => x.Text == text);
            Run? run = hp?.Runs().FirstOrDefault();
            if (run is not null)
            {
                doc.Comments.Add(run, run, "Reviewer", p.Comment!, "RV");
            }
        }

        var rels = doc.Package.GetRelationships(doc.MainPartName).ToList();
        foreach ((string name, byte[] bytes) in cx.Media)
        {
            doc.Package.AddPart(name, "image/png", bytes);
        }

        if (spec.CustomXml is not null)
        {
            doc.Package.AddPart("/customXml/item1.xml", "application/xml", Encoding.UTF8.GetBytes(spec.CustomXml));
            cx.NewRelationships.Add(new Relationship("rIdCustom1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXml", "../customXml/item1.xml"));
        }

        rels.AddRange(cx.NewRelationships);
        doc.Package.SetRelationships(doc.MainPartName, rels);

        if (spec.Title is not null || spec.Author is not null)
        {
            string core = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
                + (spec.Title is null ? "" : "<dc:title>" + Escape(spec.Title) + "</dc:title>")
                + (spec.Author is null ? "" : "<dc:creator>" + Escape(spec.Author) + "</dc:creator>")
                + "</cp:coreProperties>";
            doc.Package.AddPart("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml", Encoding.UTF8.GetBytes(core));
            var root = doc.Package.GetRelationships().ToList();
            root.Add(new Relationship("rIdCore", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml"));
            doc.Package.SetRelationships(null, root);
        }

        using var ms = new MemoryStream();
        doc.SaveAs(ms, OfficeFormat.Docx);
        return ms.ToArray();
    }

    private static IEnumerable<Para> AllParas(IEnumerable<Blk> blocks)
    {
        foreach (Blk b in blocks)
        {
            switch (b)
            {
                case Para p:
                    yield return p;
                    break;
                case Tbl t:
                    foreach (Para q in AllParas(t.Rows.SelectMany(r => r).SelectMany(c => c.Blocks)))
                    {
                        yield return q;
                    }

                    break;
                case Sdt s:
                    foreach (Para q in AllParas(s.Blocks))
                    {
                        yield return q;
                    }

                    break;
            }
        }
    }

    private static string PlainText(Para p)
    {
        var sb = new StringBuilder();
        foreach (Inl i in p.Inlines)
        {
            switch (i)
            {
                case T t:
                    sb.Append(t.Text);
                    break;
                case Fld f:
                    sb.Append(f.Result);
                    break;
                case SimpleFld f:
                    sb.Append(f.Result);
                    break;
                case Cc c:
                    sb.Append(c.Text);
                    break;
                case TabChar:
                    sb.Append('\t');
                    break;
                case Brk:
                    sb.Append('\n');
                    break;
            }
        }

        return sb.ToString();
    }

    private static string RenderBlock(Blk b, Context cx)
    {
        switch (b)
        {
            case Para p:
                return RenderPara(p, cx);
            case Tbl t:
                {
                    int cols = t.Rows.Max(r => r.Sum(c => c.Span));
                    var sb = new StringBuilder("<w:tbl" + Decl + "><w:tblPr>");
                    if (t.Style is not null)
                    {
                        cx.Styles.Add(t.Style);
                        sb.Append("<w:tblStyle w:val=\"" + t.Style + "\"/>");
                    }

                    sb.Append("<w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr><w:tblGrid>");
                    for (int i = 0; i < cols; i++)
                    {
                        sb.Append("<w:gridCol w:w=\"2000\"/>");
                    }

                    sb.Append("</w:tblGrid>");
                    foreach (Cell[] row in t.Rows)
                    {
                        sb.Append("<w:tr>");
                        foreach (Cell c in row)
                        {
                            sb.Append("<w:tc><w:tcPr><w:tcW w:w=\"" + (2000 * c.Span).ToString(CultureInfo.InvariantCulture) + "\" w:type=\"dxa\"/>");
                            if (c.Span > 1)
                            {
                                sb.Append("<w:gridSpan w:val=\"" + c.Span.ToString(CultureInfo.InvariantCulture) + "\"/>");
                            }

                            if (c.VMerge is not null)
                            {
                                sb.Append(c.VMerge == "continue" ? "<w:vMerge/>" : "<w:vMerge w:val=\"restart\"/>");
                            }

                            sb.Append("</w:tcPr>");
                            Blk[] inner = c.Blocks.Length == 0 ? [Para.Of("")] : c.Blocks;
                            foreach (Blk x in inner)
                            {
                                sb.Append(Strip(RenderBlock(x, cx)));
                            }

                            sb.Append("</w:tc>");
                        }

                        sb.Append("</w:tr>");
                    }

                    sb.Append("</w:tbl>");
                    return sb.ToString();
                }

            case Sdt s:
                {
                    var sb = new StringBuilder("<w:sdt" + Decl + "><w:sdtPr><w:tag w:val=\"" + Escape(s.Tag) + "\"/></w:sdtPr><w:sdtContent>");
                    foreach (Blk x in s.Blocks)
                    {
                        sb.Append(Strip(RenderBlock(x, cx)));
                    }

                    sb.Append("</w:sdtContent></w:sdt>");
                    return sb.ToString();
                }

            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>Nested elements need no namespace declarations of their own.</summary>
    private static string Strip(string xml) => xml.Replace(Decl, "", StringComparison.Ordinal);

    private static string RenderPara(Para p, Context cx)
    {
        var sb = new StringBuilder("<w:p" + Decl + ">");
        var ppr = new StringBuilder();
        if (p.Style is not null)
        {
            cx.Styles.Add(p.Style);
            ppr.Append("<w:pStyle w:val=\"" + p.Style + "\"/>");
        }

        if (p.KeepNext)
        {
            ppr.Append("<w:keepNext/>");
        }

        if (p.List >= 0 && p.List < cx.ListIds.Count)
        {
            ppr.Append("<w:numPr><w:ilvl w:val=\"" + p.Level.ToString(CultureInfo.InvariantCulture) + "\"/><w:numId w:val=\"" + cx.ListIds[p.List].ToString(CultureInfo.InvariantCulture) + "\"/></w:numPr>");
        }

        if (p.IndentLeft is { } ind)
        {
            ppr.Append("<w:ind w:left=\"" + ind.ToString(CultureInfo.InvariantCulture) + "\"/>");
        }

        if (p.Jc is not null)
        {
            ppr.Append("<w:jc w:val=\"" + p.Jc + "\"/>");
        }

        if (p.SectionBreak)
        {
            ppr.Append(p.Landscape
                ? "<w:sectPr><w:pgSz w:w=\"15840\" w:h=\"12240\" w:orient=\"landscape\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>"
                : "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>");
        }

        if (ppr.Length > 0)
        {
            sb.Append("<w:pPr>").Append(ppr).Append("</w:pPr>");
        }

        foreach (Inl i in p.Inlines)
        {
            RenderInline(i, cx, sb);
        }

        sb.Append("</w:p>");
        return sb.ToString();
    }

    private static string RunProps(T t)
    {
        var sb = new StringBuilder();
        if (t.Font is not null)
        {
            sb.Append("<w:rFonts w:ascii=\"" + t.Font + "\" w:hAnsi=\"" + t.Font + "\"/>");
        }

        if (t.Bold)
        {
            sb.Append("<w:b/>");
        }

        if (t.Italic)
        {
            sb.Append("<w:i/>");
        }

        if (t.Color is not null)
        {
            sb.Append("<w:color w:val=\"" + t.Color + "\"/>");
        }

        if (t.Size is { } size)
        {
            sb.Append("<w:sz w:val=\"" + (size * 2).ToString(CultureInfo.InvariantCulture) + "\"/>");
        }

        if (t.Underline is not null)
        {
            sb.Append("<w:u w:val=\"" + t.Underline + "\"/>");
        }

        return sb.Length == 0 ? "" : "<w:rPr>" + sb + "</w:rPr>";
    }

    private static string TextRun(string text, string props = "") =>
        "<w:r>" + props + "<w:t xml:space=\"preserve\">" + Escape(text) + "</w:t></w:r>";

    private static void RenderInline(Inl i, Context cx, StringBuilder sb)
    {
        switch (i)
        {
            case T t:
                {
                    string run = TextRun(t.Text, RunProps(t));
                    if (t.Link is not null)
                    {
                        string id = "rIdLink" + (++cx.Counter).ToString(CultureInfo.InvariantCulture);
                        cx.NewRelationships.Add(new Relationship(id, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", t.Link, RelationshipTargetMode.External));
                        run = "<w:hyperlink r:id=\"" + id + "\">" + run + "</w:hyperlink>";
                    }

                    sb.Append(run);
                    break;
                }

            case Fld f:
                sb.Append("<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> " + Escape(f.Code) + " </w:instrText></w:r>"
                    + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" + TextRun(f.Result) + "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>");
                break;
            case SimpleFld f:
                sb.Append("<w:fldSimple w:instr=\"" + Escape(f.Code) + "\">" + TextRun(f.Result) + "</w:fldSimple>");
                break;
            case Foot n:
                {
                    long id = n.End ? cx.Doc.Endnotes.Add(n.Text).Id : cx.Doc.Footnotes.Add(n.Text).Id;
                    sb.Append(n.End ? "<w:r><w:endnoteReference w:id=\"" : "<w:r><w:footnoteReference w:id=\"").Append(id.ToString(CultureInfo.InvariantCulture)).Append("\"/></w:r>");
                    break;
                }

            case Img img:
                {
                    int n = ++cx.Counter;
                    string id = "rIdImg" + n.ToString(CultureInfo.InvariantCulture);
                    string name = "/word/media/image" + n.ToString(CultureInfo.InvariantCulture) + ".png";
                    cx.Media.Add((name, img.Png));
                    cx.NewRelationships.Add(new Relationship(id, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", "media/image" + n.ToString(CultureInfo.InvariantCulture) + ".png"));
                    sb.Append("<w:r><w:drawing><wp:inline><wp:extent cx=\"914400\" cy=\"914400\"/><wp:docPr id=\"" + n.ToString(CultureInfo.InvariantCulture) + "\" name=\"Picture " + n.ToString(CultureInfo.InvariantCulture)
                        + "\" descr=\"" + Escape(img.Alt) + "\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"" + n.ToString(CultureInfo.InvariantCulture) + "\" name=\"image" + n.ToString(CultureInfo.InvariantCulture)
                        + ".png\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\""
                        + id + "\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"914400\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>");
                    break;
                }

            case Box box:
                {
                    int n = ++cx.Counter;
                    string content = "<w:txbxContent><w:p><w:r><w:t xml:space=\"preserve\">" + Escape(box.Text) + "</w:t></w:r></w:p></w:txbxContent>";
                    sb.Append("<w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing><wp:inline><wp:extent cx=\"1828800\" cy=\"914400\"/><wp:docPr id=\"" + n.ToString(CultureInfo.InvariantCulture)
                        + "\" name=\"Text Box " + n.ToString(CultureInfo.InvariantCulture) + "\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvSpPr txBox=\"1\"/><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1828800\" cy=\"914400\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></wps:spPr><wps:txbx>"
                        + content + "</wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></mc:Choice><mc:Fallback><w:pict><v:shape><v:textbox>"
                        + content + "</v:textbox></v:shape></w:pict></mc:Fallback></mc:AlternateContent></w:r>");
                    break;
                }

            case Cc c:
                sb.Append("<w:sdt><w:sdtPr><w:tag w:val=\"" + Escape(c.Tag) + "\"/></w:sdtPr><w:sdtContent>" + TextRun(c.Text) + "</w:sdtContent></w:sdt>");
                break;
            case TabChar:
                sb.Append("<w:r><w:tab/></w:r>");
                break;
            case Brk:
                sb.Append("<w:r><w:br/></w:r>");
                break;
        }
    }
}
