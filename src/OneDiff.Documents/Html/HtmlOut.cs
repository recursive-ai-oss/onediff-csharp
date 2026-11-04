using System.Buffers;
using System.Globalization;

namespace OneDiff.Documents.Html;

/// <summary>
/// A small HTML emitter over a <see cref="TextWriter"/>. It knows only escaping: text is written so that reading the page back
/// gives exactly the same characters (characters HTML cannot carry, such as control characters and unpaired surrogates, become
/// a marked span that names the code point), and attribute values are written with the same characters replaced by U+FFFD.
/// Nothing from a document ever reaches the page unescaped.
/// </summary>
internal sealed class HtmlOut(TextWriter writer)
{
    private static readonly SearchValues<char> TextSpecials = SearchValues.Create(BuildSpecials());

    /// <summary>The paragraph-break mark a joined text uses between nodes (a split or merge).</summary>
    public const char Separator = '\u001E';

    public void Raw(string s) => writer.Write(s);

    public void Raw(char c) => writer.Write(c);

    public void Int(int value) => writer.Write(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Writes text content. With <paramref name="separatorsAsMarks"/>, U+001E (a node boundary inside joined text) becomes a <c>sep</c> mark that reading back maps to U+001E.</summary>
    public void Text(string s, bool separatorsAsMarks = false)
    {
        ReadOnlySpan<char> rest = s;
        while (rest.Length > 0)
        {
            int i = rest.IndexOfAny(TextSpecials);
            if (i < 0)
            {
                writer.Write(rest);
                return;
            }

            writer.Write(rest[..i]);
            char c = rest[i];
            int advance = 1;
            switch (c)
            {
                case '&':
                    writer.Write("&amp;");
                    break;
                case '<':
                    writer.Write("&lt;");
                    break;
                case '>':
                    writer.Write("&gt;");
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < rest.Length && char.IsLowSurrogate(rest[i + 1]))
                    {
                        writer.Write(c);
                        writer.Write(rest[i + 1]);
                        advance = 2;
                    }
                    else if (c == Separator && separatorsAsMarks)
                    {
                        writer.Write("<span class=\"sep\">\u00B6</span>");
                    }
                    else
                    {
                        ControlMark(c);
                    }

                    break;
            }

            rest = rest[(i + advance)..];
        }
    }

    /// <summary>Writes <c> name="value"</c>.</summary>
    public void Attr(string name, string value)
    {
        writer.Write(' ');
        writer.Write(name);
        writer.Write("=\"");
        foreach (char c in SanitizeAttribute(value))
        {
            switch (c)
            {
                case '&':
                    writer.Write("&amp;");
                    break;
                case '"':
                    writer.Write("&quot;");
                    break;
                case '<':
                    writer.Write("&lt;");
                    break;
                case '>':
                    writer.Write("&gt;");
                    break;
                default:
                    writer.Write(c);
                    break;
            }
        }

        writer.Write('"');
    }

    public void IntAttr(string name, int value) => Attr(name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>The value an attribute carries for <paramref name="value"/>: characters HTML cannot carry become U+FFFD.</summary>
    public static string SanitizeAttribute(string value)
    {
        int first = -1;
        for (int i = 0; i < value.Length; i++)
        {
            if (IsUncarriable(value, i))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return value;
        }

        var chars = value.ToCharArray();
        for (int i = first; i < chars.Length; i++)
        {
            if (IsUncarriable(value, i))
            {
                chars[i] = '\uFFFD';
            }
        }

        return new string(chars);
    }

    private static bool IsUncarriable(string s, int i)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c))
        {
            return !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]));
        }

        if (char.IsLowSurrogate(c))
        {
            return !(i > 0 && char.IsHighSurrogate(s[i - 1]));
        }

        return IsSpecial(c) && c is not ('&' or '<' or '>');
    }

    private static bool IsSpecial(char c) =>
        c is '&' or '<' or '>'
        || (c < 0x20 && c is not ('\t' or '\n'))
        || (c is >= '\u007F' and <= '\u009F')
        || (c is >= '\uD800' and <= '\uDFFF')
        || (c is >= '\uFDD0' and <= '\uFDEF')
        || c is '\uFFFE' or '\uFFFF';

    private void ControlMark(char c)
    {
        char picture = c < 0x20 ? (char)(0x2400 + c) : c == '\u007F' ? '\u2421' : '\uFFFD';
        string hex = ((int)c).ToString("x4", CultureInfo.InvariantCulture);
        writer.Write("<span class=\"ctl\" data-cp=\"");
        writer.Write(hex);
        writer.Write("\" title=\"U+");
        writer.Write(hex.ToUpperInvariant());
        writer.Write("\">");
        writer.Write(picture);
        writer.Write("</span>");
    }

    private static char[] BuildSpecials()
    {
        var list = new List<char>();
        for (int c = 0; c <= 0xFFFF; c++)
        {
            if (IsSpecial((char)c))
            {
                list.Add((char)c);
            }
        }

        return [.. list];
    }
}
