using System.Text;
using RecursiveAI.OneOffice.Markup;

namespace OneDiff.Word.Internal;

/// <summary>Small helpers over the OneOffice markup layer for the WordprocessingML elements the tracked-changes writer touches (ECMA-376 Part 1 §17).</summary>
internal static class Wx
{
    public const string Main = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string MainStrict = "http://purl.oclc.org/ooxml/wordprocessingml/main";

    /// <summary>The author written on the elements a writer step creates only to move them: they are renamed or replaced before the document is saved.</summary>
    public const string Marker = "~onediff.marker~";

    public static bool Is(MarkupNode n, string local)
    {
        if (n.IsNull || n.Kind != MarkupNodeKind.Element || !n.LocalNameString.Equals(local, StringComparison.Ordinal))
        {
            return false;
        }

        string ns = n.NamespaceUri;
        return ns == Main || ns == MainStrict;
    }

    public static bool InMain(MarkupNode n)
    {
        if (n.IsNull || n.Kind != MarkupNodeKind.Element)
        {
            return false;
        }

        string ns = n.NamespaceUri;
        return ns == Main || ns == MainStrict;
    }

    public static string? Attr(MarkupNode n, string local) => n.GetAttribute(Main, local) ?? n.GetAttribute(MainStrict, local);

    public static MarkupNode Child(MarkupNode parent, string local)
    {
        for (MarkupNode c = parent.FirstChild; !c.IsNull; c = c.NextSibling)
        {
            if (Is(c, local))
            {
                return c;
            }
        }

        return default;
    }

    public static IEnumerable<MarkupNode> ChildElements(MarkupNode parent)
    {
        for (MarkupNode c = parent.FirstChild; !c.IsNull; c = c.NextSibling)
        {
            if (c.Kind == MarkupNodeKind.Element)
            {
                yield return c;
            }
        }
    }

    /// <summary>All element descendants of <paramref name="root"/> in document order (not the root itself).</summary>
    public static List<MarkupNode> Descendants(MarkupNode root)
    {
        var list = new List<MarkupNode>();
        var stack = new Stack<MarkupNode>();
        for (MarkupNode c = root.LastChild; !c.IsNull; c = c.PreviousSibling)
        {
            if (c.Kind == MarkupNodeKind.Element)
            {
                stack.Push(c);
            }
        }

        while (stack.Count > 0)
        {
            MarkupNode n = stack.Pop();
            list.Add(n);
            for (MarkupNode c = n.LastChild; !c.IsNull; c = c.PreviousSibling)
            {
                if (c.Kind == MarkupNodeKind.Element)
                {
                    stack.Push(c);
                }
            }
        }

        return list;
    }

    public static MarkupNode NextElement(MarkupNode n)
    {
        MarkupNode s = n.NextSibling;
        while (!s.IsNull && s.Kind != MarkupNodeKind.Element)
        {
            s = s.NextSibling;
        }

        return s;
    }

    public static MarkupNode PreviousElement(MarkupNode n)
    {
        MarkupNode s = n.PreviousSibling;
        while (!s.IsNull && s.Kind != MarkupNodeKind.Element)
        {
            s = s.PreviousSibling;
        }

        return s;
    }

    private static readonly KeyValuePair<string, string>[] KnownPrefixes =
    [
        new("w", Main),
        new("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"),
        new("wp", "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"),
        new("a", "http://schemas.openxmlformats.org/drawingml/2006/main"),
        new("pic", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
        new("wps", "http://schemas.microsoft.com/office/word/2010/wordprocessingShape"),
        new("mc", "http://schemas.openxmlformats.org/markup-compatibility/2006"),
        new("v", "urn:schemas-microsoft-com:vml"),
        new("o", "urn:schemas-microsoft-com:office:office"),
        new("w10", "urn:schemas-microsoft-com:office:word"),
        new("m", "http://schemas.openxmlformats.org/officeDocument/2006/math"),
        new("w14", "http://schemas.microsoft.com/office/word/2010/wordml"),
        new("w15", "http://schemas.microsoft.com/office/word/2012/wordml"),
        new("wp14", "http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing"),
        new("wpg", "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup"),
        new("a14", "http://schemas.microsoft.com/office/drawing/2010/main"),
        new("c", "http://schemas.openxmlformats.org/drawingml/2006/chart"),
        new("dgm", "http://schemas.openxmlformats.org/drawingml/2006/diagram"),
    ];

    /// <summary>
    /// UTF-8 bytes of XML to be inserted into a document, with a declaration added to each top-level element for every well-known prefix
    /// it uses and does not declare: a node serialized on its own does not repeat the declarations it inherited from its ancestors.
    /// </summary>
    public static byte[] Bytes(string xml) => Encoding.UTF8.GetBytes(Complete(xml));

    private static string Complete(string xml)
    {
        if (xml.Length == 0 || xml[0] != '<')
        {
            return xml;
        }

        var used = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> kv in KnownPrefixes)
        {
            if (xml.Contains(kv.Key + ":", StringComparison.Ordinal))
            {
                used.Add(kv);
            }
        }

        if (used.Count == 0)
        {
            return xml;
        }

        var sb = new StringBuilder(xml.Length + 64);
        int i = 0, depth = 0;
        while (i < xml.Length)
        {
            int lt = xml.IndexOf('<', i);
            if (lt < 0)
            {
                sb.Append(xml, i, xml.Length - i);
                break;
            }

            sb.Append(xml, i, lt - i);
            int gt = TagEnd(xml, lt);
            if (gt < 0)
            {
                sb.Append(xml, lt, xml.Length - lt);
                break;
            }

            bool end = xml[lt + 1] == '/';
            bool special = xml[lt + 1] is '?' or '!';
            bool selfClosing = xml[gt - 1] == '/';
            if (!end && !special && depth == 0)
            {
                int nameEnd = lt + 1;
                while (nameEnd < gt && xml[nameEnd] is not (' ' or '/' or '>' or '\t' or '\r' or '\n'))
                {
                    nameEnd++;
                }

                string tag = xml[lt..(gt + 1)];
                sb.Append(xml, lt, nameEnd - lt);
                foreach (KeyValuePair<string, string> kv in used)
                {
                    if (!tag.Contains("xmlns:" + kv.Key + "=", StringComparison.Ordinal))
                    {
                        sb.Append(" xmlns:").Append(kv.Key).Append("=\"").Append(kv.Value).Append('"');
                    }
                }

                sb.Append(xml, nameEnd, gt + 1 - nameEnd);
            }
            else
            {
                sb.Append(xml, lt, gt + 1 - lt);
            }

            if (!special)
            {
                if (end)
                {
                    depth--;
                }
                else if (!selfClosing)
                {
                    depth++;
                }
            }

            i = gt + 1;
        }

        return sb.ToString();
    }

    private static int TagEnd(string xml, int lt)
    {
        char quote = '\0';
        for (int i = lt + 1; i < xml.Length; i++)
        {
            char c = xml[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Escapes text for an attribute value in double quotes.</summary>
    public static string EscapeAttribute(string s)
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
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renames every element <c>prefix:from</c> in <paramref name="xml"/> to <c>prefix:to</c>, matching whole names only
    /// (<c>w:del</c> is not <c>w:delText</c>).
    /// </summary>
    public static string Rename(string xml, string prefix, string from, string to)
    {
        if (xml.IndexOf(from, StringComparison.Ordinal) < 0)
        {
            return xml;
        }

        string open = "<" + prefix + ":" + from;
        string close = "</" + prefix + ":" + from;
        var sb = new StringBuilder(xml.Length);
        int i = 0;
        while (i < xml.Length)
        {
            int a = xml.IndexOf('<', i);
            if (a < 0)
            {
                sb.Append(xml, i, xml.Length - i);
                break;
            }

            sb.Append(xml, i, a - i);
            string? name = null;
            if (string.CompareOrdinal(xml, a, open, 0, open.Length) == 0)
            {
                name = open;
            }
            else if (string.CompareOrdinal(xml, a, close, 0, close.Length) == 0)
            {
                name = close;
            }

            if (name is not null && a + name.Length < xml.Length && (xml[a + name.Length] is ' ' or '>' or '/' or '\t' or '\r' or '\n'))
            {
                sb.Append(name == open ? "<" : "</").Append(prefix).Append(':').Append(to);
                i = a + name.Length;
            }
            else
            {
                sb.Append('<');
                i = a + 1;
            }
        }

        return sb.ToString();
    }

    /// <summary>Removes the start and end tags of every <c>prefix:local</c> element (keeping what is inside) and drops the self-closing ones.</summary>
    public static string Unwrap(string xml, string prefix, string local)
    {
        string open = "<" + prefix + ":" + local;
        string close = "</" + prefix + ":" + local + ">";
        var sb = new StringBuilder(xml.Length);
        int i = 0;
        while (i < xml.Length)
        {
            int a = xml.IndexOf('<', i);
            if (a < 0)
            {
                sb.Append(xml, i, xml.Length - i);
                break;
            }

            sb.Append(xml, i, a - i);
            if (string.CompareOrdinal(xml, a, close, 0, close.Length) == 0)
            {
                i = a + close.Length;
            }
            else if (string.CompareOrdinal(xml, a, open, 0, open.Length) == 0 && a + open.Length < xml.Length && xml[a + open.Length] is ' ' or '>' or '/')
            {
                int gt = xml.IndexOf('>', a);
                i = gt + 1;
            }
            else
            {
                sb.Append('<');
                i = a + 1;
            }
        }

        return sb.ToString();
    }
}
