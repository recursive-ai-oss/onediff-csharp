using System.Globalization;
using System.Text;
using RecursiveAI.OneOffice.Markup;

namespace OneDiff.Word.Internal;

/// <summary>
/// The anchor references of the Word source (docs/word.md, "Anchors"). A reference names a part of the package and an element in it
/// by the position of that element among the element children at each level, counted from the part's root element, so a writer that
/// opens the same bytes in the same revision mode finds the same element.
/// </summary>
internal static class WordAnchors
{
    /// <summary>The prefix of every reference this source writes.</summary>
    public const string Prefix = "docx:";

    /// <summary>The reference of an element: <c>docx:/word/document.xml#0.3.12</c>. An empty path is the part's root element.</summary>
    public static string Of(string part, MarkupNode element)
    {
        var sb = new StringBuilder(Prefix.Length + part.Length + 12);
        sb.Append(Prefix).Append(part).Append('#');
        AppendPath(sb, element);
        return sb.ToString();
    }

    /// <summary>The reference of a stretch of text inside a paragraph: the element's reference, then <c>@offset+length</c> in UTF-16 units of the paragraph's current text.</summary>
    public static string OfText(string part, MarkupNode element, int offset, int length)
    {
        var sb = new StringBuilder(Prefix.Length + part.Length + 24);
        sb.Append(Prefix).Append(part).Append('#');
        AppendPath(sb, element);
        sb.Append('@').Append(offset.ToString(CultureInfo.InvariantCulture)).Append('+').Append(length.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static void AppendPath(StringBuilder sb, MarkupNode element)
    {
        // Climb to the root element collecting each step, then write the steps from the top.
        // Bounded: the first 64 steps live in a fixed stack buffer (MaxXmlDepth is 256 by default); deeper paths spill to a list.
        Span<int> steps = stackalloc int[64];
        List<int>? more = null;
        int count = 0;
        MarkupNode n = element;
        while (true)
        {
            MarkupNode parent = n.Parent;
            if (parent.IsNull || parent.Kind == MarkupNodeKind.Document)
            {
                break;
            }

            int index = 0;
            for (MarkupNode s = n.PreviousSibling; !s.IsNull; s = s.PreviousSibling)
            {
                if (s.Kind == MarkupNodeKind.Element)
                {
                    index++;
                }
            }

            if (count < steps.Length)
            {
                steps[count] = index;
            }
            else
            {
                (more ??= []).Add(index);
            }

            count++;
            n = parent;
        }

        for (int i = count - 1; i >= 0; i--)
        {
            int v = i < steps.Length ? steps[i] : more![i - steps.Length];
            sb.Append(v.ToString(CultureInfo.InvariantCulture));
            if (i > 0)
            {
                sb.Append('.');
            }
        }
    }

    /// <summary>Splits a reference into the part name, the path and the optional text stretch. Returns false for a reference this source did not write.</summary>
    public static bool TryParse(string reference, out string part, out int[] path, out int offset, out int length)
    {
        part = "";
        path = [];
        offset = -1;
        length = 0;
        if (!reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int hash = reference.IndexOf('#', Prefix.Length);
        if (hash < 0)
        {
            return false;
        }

        part = reference[Prefix.Length..hash];
        string rest = reference[(hash + 1)..];
        int at = rest.IndexOf('@', StringComparison.Ordinal);
        string pathText = at < 0 ? rest : rest[..at];
        if (at >= 0)
        {
            int plus = rest.IndexOf('+', at);
            if (plus < 0
                || !int.TryParse(rest.AsSpan(at + 1, plus - at - 1), NumberStyles.None, CultureInfo.InvariantCulture, out offset)
                || !int.TryParse(rest.AsSpan(plus + 1), NumberStyles.None, CultureInfo.InvariantCulture, out length))
            {
                return false;
            }
        }

        if (pathText.Length == 0)
        {
            return true;
        }

        string[] pieces = pathText.Split('.');
        path = new int[pieces.Length];
        for (int i = 0; i < pieces.Length; i++)
        {
            if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out path[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Follows a path from a part's root element. Returns a null node when the path leaves the tree.</summary>
    public static MarkupNode Follow(MarkupNode root, int[] path)
    {
        MarkupNode n = root;
        foreach (int step in path)
        {
            MarkupNode c = n.FirstChild;
            int seen = -1;
            while (!c.IsNull)
            {
                if (c.Kind == MarkupNodeKind.Element && ++seen == step)
                {
                    break;
                }

                c = c.NextSibling;
            }

            if (c.IsNull)
            {
                return default;
            }

            n = c;
        }

        return n;
    }
}
