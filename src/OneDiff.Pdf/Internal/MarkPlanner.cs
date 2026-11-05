using System.Collections.Immutable;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OnePdf;

namespace OneDiff.Pdf.Internal;

/// <summary>The part of one annotation that sits on one page: quadrilaterals in upright page space, or the box of a caret.</summary>
internal sealed record PagePlan(int Page, List<Quad> Quads, PdfRectangle CaretBox);

/// <summary>What to write for one change: the annotation kind, colour, text and where.</summary>
internal sealed record MarkPlan(PdfMarkupKind Kind, string Color, string Contents, List<PagePlan> Pages);

/// <summary>Decides, for each change of a comparison, which annotation shows it on the new or the old document.</summary>
internal sealed class MarkPlanner
{
    private readonly PdfRedline settings;
    private readonly Comparison comparison;
    private readonly bool markNew;
    private int[]? previousLinkedNew;

    public MarkPlanner(PdfRedline settings, Comparison comparison, bool markNew)
    {
        this.settings = settings;
        this.comparison = comparison;
        this.markNew = markNew;
    }

    public MarkPlan? Plan(Change c, out string? why)
    {
        why = null;
        ChangeSide mine = markNew ? c.New : c.Old;
        ChangeSide other = markNew ? c.Old : c.New;
        RedlineStyle style = settings.Style;
        string body = other.Text;

        switch (c.Kind)
        {
            case ChangeKind.Insert:
                if (!markNew)
                {
                    why = "an insertion has no place in the old document";
                    return null;
                }

                return Highlight(mine, PdfMarkupKind.Highlight, style.Insertion, Label(style.Insertion, "Inserted"), body, out why);

            case ChangeKind.Delete:
                if (!markNew)
                {
                    return Highlight(mine, PdfMarkupKind.StrikeOut, style.Deletion, Label(style.Deletion, "Deleted"), body, out why);
                }

                return DeletedOnNew(c, mine, body, out why);

            case ChangeKind.Replace:
            case ChangeKind.Split:
            case ChangeKind.Merge:
            {
                string word = c.Kind == ChangeKind.Replace ? "Replaced" : c.Kind == ChangeKind.Split ? "Split" : "Merged";
                if (!markNew)
                {
                    return Highlight(mine, PdfMarkupKind.StrikeOut, style.Deletion, Label(style.Deletion, word), body, out why);
                }

                if (mine.Length == 0)
                {
                    return DeletedOnNew(c, mine, body, out why);
                }

                return Highlight(mine, PdfMarkupKind.Highlight, style.Insertion, Label(style.Insertion, word), body, out why);
            }

            case ChangeKind.Move:
                return markNew
                    ? Highlight(mine, PdfMarkupKind.Highlight, style.MoveTo, Label(style.MoveTo, "Moved"), body, out why)
                    : Highlight(mine, PdfMarkupKind.Highlight, style.MoveFrom, Label(style.MoveFrom, "Moved away"), body, out why);

            case ChangeKind.Format:
                return Highlight(mine, PdfMarkupKind.Underline, style.Formatting, Label(style.Formatting, "Formatting changed: " + DescribeFormat(c)), body, out why);

            case ChangeKind.Attribute:
                return Highlight(mine, PdfMarkupKind.Squiggly, style.Formatting, Label(style.Formatting, "Changed: " + DescribeAttributes(c)), body, out why);

            default:
                why = "unknown kind of change";
                return null;
        }
    }

    /// <summary>Whether an anchor is a place on a page: glyph quadrilaterals of a digital PDF or recognized words of a scan.</summary>
    private static bool IsPageAnchor(Anchor a) => a.Kind is AnchorKind.PdfQuads or AnchorKind.OcrWords;

    private static string Label(MarkStyle style, string text) => style.Glyph.Length > 0 ? style.Glyph + " " + text : text;

    private static string Contents(string label, string body) => body.Length == 0 ? label : label + "\n" + body;

    private static MarkPlan? Highlight(ChangeSide side, PdfMarkupKind kind, MarkStyle mark, string label, string body, out string? why)
    {
        why = null;
        if (!side.IsPresent || side.Length == 0 && side.Anchor.Kind == AnchorKind.None)
        {
            why = "a container with no text of its own: its content carries its own changes";
            return null;
        }

        if (!IsPageAnchor(side.Anchor) || side.Anchor.Quads.IsDefaultOrEmpty)
        {
            why = side.Anchor.Kind == AnchorKind.None ? "a container with no text of its own: its content carries its own changes" : "the anchor is not a PDF location";
            return null;
        }

        if (side.Length == 0)
        {
            // Nothing is marked on this side: the other side's text is gone from here.
            return CaretAt(side, label, mark, body, out why);
        }

        var byPage = new SortedDictionary<int, List<Quad>>();
        foreach (Quad q in side.Anchor.Quads)
        {
            if (!byPage.TryGetValue(q.Page, out List<Quad>? list))
            {
                byPage[q.Page] = list = [];
            }

            list.Add(q);
        }

        return new MarkPlan(kind, mark.Color, Contents(label, body), [.. byPage.Select(kv => new PagePlan(kv.Key, kv.Value, default))]);
    }

    /// <summary>Text that is gone from the new document: a caret where it used to be.</summary>
    private MarkPlan? DeletedOnNew(Change c, ChangeSide mine, string body, out string? why)
    {
        MarkStyle mark = settings.Style.Deletion;
        string label = Label(mark, c.Kind == ChangeKind.Replace ? "Replaced" : "Deleted");
        if (mine.IsPresent)
        {
            return CaretAt(mine, label, mark, body, out why);
        }

        why = null;
        if (body.Length == 0)
        {
            why = "a container with no text of its own: its content carries its own changes";
            return null;
        }

        // A whole block was deleted: put the caret after the last block before it that has a place in the new document.
        previousLinkedNew ??= ComputePreviousLinkedNew();
        int oldNode = c.Old.Nodes[0];
        CompareDocument newDoc = comparison.New;
        int prev = previousLinkedNew[oldNode];
        Quad q;
        bool atEnd;
        if (prev >= 0)
        {
            ImmutableArray<Quad> quads = QuadsOf(newDoc[prev]);
            q = quads[^1];
            atEnd = true;
        }
        else
        {
            Quad? first = FirstQuad(newDoc);
            if (first is null)
            {
                why = "the new document has no text to put the mark beside";
                return null;
            }

            q = first.Value;
            atEnd = false;
        }

        return new MarkPlan(PdfMarkupKind.Caret, mark.Color, Contents(label, body), [new PagePlan(q.Page, [], CaretBox(q, atEnd))]);
    }

    private static MarkPlan? CaretAt(ChangeSide side, string label, MarkStyle mark, string body, out string? why)
    {
        why = null;
        if (!IsPageAnchor(side.Anchor) || side.Anchor.Quads.IsDefaultOrEmpty)
        {
            why = "the anchor is not a PDF location";
            return null;
        }

        ImmutableArray<Quad> quads = side.Anchor.Quads;
        // A zero-length range resolves to the tokens that touch the point: the one before and the one after it, or only one at
        // an end of the text.
        Quad q;
        bool atEnd;
        if (quads.Length >= 2)
        {
            q = quads[0];
            atEnd = true;
        }
        else
        {
            q = quads[0];
            atEnd = side.Start != 0;
        }

        return new MarkPlan(PdfMarkupKind.Caret, mark.Color, Contents(label, body), [new PagePlan(q.Page, [], CaretBox(q, atEnd))]);
    }

    private static PdfRectangle CaretBox(Quad q, bool atEnd)
    {
        // Lower point and height of the line at the chosen end; the caret sits just below the baseline.
        double x = atEnd ? q.X2 : q.X1, y = atEnd ? q.Y2 : q.Y1;
        double hx = q.X4 - q.X1, hy = q.Y4 - q.Y1;
        double h = Math.Max(4, Math.Sqrt(hx * hx + hy * hy));
        return new PdfRectangle(x - 0.35 * h, y - 0.6 * h, x + 0.35 * h, y + 0.1 * h);
    }

    private static ImmutableArray<Quad> QuadsOf(Node node)
    {
        if (!node.Segments.IsDefaultOrEmpty)
        {
            return node.Segments[^1].Anchor.Quads;
        }

        return node.Anchor.Quads;
    }

    private static Quad? FirstQuad(CompareDocument doc)
    {
        foreach (Node n in doc.Nodes)
        {
            if (IsPageAnchor(n.Anchor))
            {
                return n.Anchor.Quads[0];
            }
        }

        return null;
    }

    /// <summary>For every old node, the new node that the nearest earlier old node with a place in the new document corresponds to (-1 if none).</summary>
    private int[] ComputePreviousLinkedNew()
    {
        CompareDocument oldDoc = comparison.Old, newDoc = comparison.New;
        Alignment al = comparison.Alignment;
        var result = new int[oldDoc.Count];
        int last = -1;
        foreach (Node n in oldDoc.Nodes)
        {
            result[n.Id] = last;
            AlignmentLink? link = al.LinkOfOld(n.Id);
            if (link is null || link.Kind == LinkKind.Move)
            {
                continue;
            }

            for (int i = link.NewNodes.Length - 1; i >= 0; i--)
            {
                Node target = newDoc[link.NewNodes[i]];
                if (IsPageAnchor(target.Anchor))
                {
                    last = target.Id;
                    break;
                }
            }
        }

        return result;
    }

    private static string DescribeFormat(Change c)
    {
        if (c.Edits.IsDefaultOrEmpty)
        {
            return "formatting";
        }

        RegionEdit e = c.Edits[0];
        FormatSet o = e.OldRuns.IsDefaultOrEmpty ? FormatSet.Empty : e.OldRuns[0].Format;
        FormatSet n = e.NewRuns.IsDefaultOrEmpty ? FormatSet.Empty : e.NewRuns[0].Format;
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in o.Entries)
        {
            keys.Add(kv.Key);
        }

        foreach (KeyValuePair<string, string> kv in n.Entries)
        {
            keys.Add(kv.Key);
        }

        var parts = new List<string>();
        foreach (string k in keys)
        {
            string a = o.Get(k) ?? "none", b = n.Get(k) ?? "none";
            if (a != b)
            {
                parts.Add((k.StartsWith("run.", StringComparison.Ordinal) ? k[4..] : k) + " " + a + " \u2192 " + b);
            }
        }

        string text = parts.Count == 0 ? "formatting" : string.Join("; ", parts);
        return c.Edits.Length > 1 ? text + $" (+{c.Edits.Length - 1} more)" : text;
    }

    private static string DescribeAttributes(Change c)
    {
        if (c.AttributeEdits.IsDefaultOrEmpty)
        {
            return "attributes";
        }

        AttributeEdit e = c.AttributeEdits[0];
        string text = e.Key + " " + (e.OldValue ?? "none") + " \u2192 " + (e.NewValue ?? "none");
        return c.AttributeEdits.Length > 1 ? text + $" (+{c.AttributeEdits.Length - 1} more)" : text;
    }
}
