using System.Globalization;
using System.Text;

namespace OneDiff.Documents.Tests.Html;

/// <summary>A parsed element of a written page.</summary>
internal sealed class El
{
    public string Tag = "";
    public Dictionary<string, string> Attrs = [];
    public List<object> Kids = [];
    public El? Parent;

    public string Class => Attrs.GetValueOrDefault("class") ?? "";

    public bool Has(string cls) => Class.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(cls);

    public IEnumerable<El> Elements => Kids.OfType<El>();

    public IEnumerable<El> Descendants()
    {
        foreach (El e in Elements)
        {
            yield return e;
            foreach (El d in e.Descendants())
            {
                yield return d;
            }
        }
    }

    public string? Attr(string name) => Attrs.GetValueOrDefault(name);
}

/// <summary>
/// A strict reader for the pages the writers produce: tags must nest and close properly, only the entities the writer uses are
/// allowed, and anything unexpected throws. It is deliberately not a general HTML parser; the independent validation is the
/// W3C checker run in the gate script.
/// </summary>
internal static class HtmlDom
{
    private static readonly HashSet<string> Void = ["meta", "br", "hr", "img", "link", "input"];

    public static El Parse(string html)
    {
        var root = new El { Tag = "#root" };
        El cur = root;
        int i = 0;
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0)
            {
                cur.Kids.Add(text.ToString());
                text.Clear();
            }
        }

        while (i < html.Length)
        {
            char c = html[i];
            if (c == '<')
            {
                if (string.CompareOrdinal(html, i, "<!DOCTYPE", 0, 9) == 0)
                {
                    i = html.IndexOf('>', i) + 1;
                    continue;
                }

                Flush();
                if (html[i + 1] == '/')
                {
                    int end = html.IndexOf('>', i);
                    string name = html[(i + 2)..end];
                    if (cur.Tag != name)
                    {
                        throw new FormatException($"closing </{name}> but <{cur.Tag}> is open at {i}");
                    }

                    cur = cur.Parent!;
                    i = end + 1;
                    continue;
                }

                int j = i + 1;
                while (j < html.Length && html[j] is not (' ' or '>' or '/'))
                {
                    j++;
                }

                var el = new El { Tag = html[(i + 1)..j], Parent = cur };
                while (true)
                {
                    while (html[j] == ' ')
                    {
                        j++;
                    }

                    if (html[j] == '>')
                    {
                        j++;
                        break;
                    }

                    int eq = html.IndexOf('=', j);
                    string an = html[j..eq];
                    if (html[eq + 1] != '"')
                    {
                        throw new FormatException("unquoted attribute at " + j);
                    }

                    int q = html.IndexOf('"', eq + 2);
                    el.Attrs[an] = Decode(html[(eq + 2)..q]);
                    j = q + 1;
                }

                cur.Kids.Add(el);
                i = j;
                if (el.Tag == "style")
                {
                    int close = html.IndexOf("</style>", i, StringComparison.Ordinal);
                    el.Kids.Add(html[i..close]);
                    i = close + 8;
                    continue;
                }

                if (!Void.Contains(el.Tag))
                {
                    cur = el;
                }

                continue;
            }

            if (c == '&')
            {
                int semi = html.IndexOf(';', i);
                text.Append(Decode(html[i..(semi + 1)]));
                i = semi + 1;
                continue;
            }

            text.Append(c);
            i++;
        }

        Flush();
        if (cur != root)
        {
            throw new FormatException($"<{cur.Tag}> never closed");
        }

        return root;
    }

    private static string Decode(string s)
    {
        if (s.IndexOf('&', StringComparison.Ordinal) < 0)
        {
            return s;
        }

        return s.Replace("&quot;", "\"", StringComparison.Ordinal).Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
    }

    /// <summary>
    /// The text a reader sees inside an element, as the document had it: hidden labels, glyphs, paragraph marks and type tags
    /// are dropped, control-character marks and node separators map back to their characters.
    /// </summary>
    public static string TextOf(El e, bool includeInserted, bool includeDeleted)
    {
        var sb = new StringBuilder();
        Collect(e, includeInserted, includeDeleted, sb);
        return sb.ToString();
    }

    private static void Collect(El e, bool ins, bool del, StringBuilder sb)
    {
        foreach (object k in e.Kids)
        {
            if (k is string s)
            {
                sb.Append(s);
                continue;
            }

            var child = (El)k;
            if (child.Tag == "ins" && !ins || child.Tag == "del" && !del)
            {
                continue;
            }

            if (child.Has("sr") || child.Has("gl") || child.Has("pbrk") || child.Has("tag"))
            {
                continue;
            }

            if (child.Has("ctl"))
            {
                sb.Append((char)int.Parse(child.Attr("data-cp")!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                continue;
            }

            if (child.Has("sep"))
            {
                sb.Append('\u001E');
                continue;
            }

            Collect(child, ins, del, sb);
        }
    }
}
