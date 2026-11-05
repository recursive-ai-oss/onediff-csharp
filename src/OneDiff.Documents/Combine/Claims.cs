using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Combine;

internal enum ClaimKind
{
    /// <summary>A character range (or a point, when empty) of the joined text of a link's old nodes.</summary>
    Text,

    /// <summary>The whole of some old nodes, deleted.</summary>
    Whole,

    /// <summary>A split or merge of old nodes.</summary>
    Structure,

    /// <summary>One attribute of an old node set to a value.</summary>
    Attr,

    /// <summary>An old node moved to a place.</summary>
    Moved,

    /// <summary>New content at a place between old nodes.</summary>
    Insert,
}

/// <summary>
/// What one change of one reviewer asserts about the original, in the original's own coordinates. Two reviewers' claims are
/// the same when every field but the reviewer and change agree; they conflict by the rules in <see cref="ClaimRules"/>.
/// </summary>
internal sealed class Claim
{
    public required ClaimKind Kind { get; init; }

    /// <summary>The old nodes whose joined text the range is in, as a list ("3" or "3,4"); empty for an insertion.</summary>
    public required ImmutableArray<int> Nodes { get; init; }

    public int Start { get; init; }

    public int End { get; init; }

    /// <summary>The attribute key, or the place of an insertion or a move's destination.</summary>
    public string Key { get; init; } = "";

    /// <summary>What the claim makes of the content: new text and formatting, attribute value, kind of structure change.</summary>
    public string Effect { get; init; } = "";

    /// <summary>The claim is a consequence of other changes (a list label or a reference renumbered because something before it was added or removed): it is attributed and merged like any other, but never conflicts, because merging the causes gives a third value that no reviewer chose.</summary>
    public bool Derived { get; init; }

    public bool Same(Claim o) =>
        Kind == o.Kind && Start == o.Start && End == o.End && string.Equals(Key, o.Key, StringComparison.Ordinal)
        && string.Equals(Effect, o.Effect, StringComparison.Ordinal) && Nodes.AsSpan().SequenceEqual(o.Nodes.AsSpan());

    public string Identity()
    {
        var sb = new StringBuilder();
        sb.Append((int)Kind).Append('|');
        foreach (int n in Nodes)
        {
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append(',');
        }

        sb.Append('|').Append(Start.ToString(CultureInfo.InvariantCulture)).Append('|').Append(End.ToString(CultureInfo.InvariantCulture)).Append('|').Append(Key).Append('|').Append(Effect);
        return sb.ToString();
    }
}

internal static class Claims
{
    /// <summary>The claims of one change: text claims first, one per edit in edit order, then the others.</summary>
    public static List<Claim> Of(Change c, Comparison cmp)
    {
        var list = new List<Claim>();
        ImmutableArray<int> old = c.Old.Nodes.IsDefault ? [] : c.Old.Nodes;
        bool derived = c.Type == ChangeType.Numbering || (c.Type == ChangeType.Reference && !c.IsSubstantive);
        foreach (RegionEdit e in c.Edits)
        {
            string runs = RunsKey(e.NewRuns);
            list.Add(new Claim
            {
                Kind = ClaimKind.Text,
                Nodes = old,
                Start = e.OldStart,
                End = e.OldEnd,
                Effect = (e.IsFormatOnly ? "fmt|" : "txt|") + e.NewText + "|" + runs,
                Derived = derived,
            });
        }

        foreach (AttributeEdit a in c.AttributeEdits)
        {
            list.Add(new Claim { Kind = ClaimKind.Attr, Nodes = [a.OldNode], Key = a.Key, Effect = a.NewValue ?? "\u0000null", Derived = derived || string.Equals(a.Key, FormatKeys.ListLabel, StringComparison.Ordinal) });
        }

        switch (c.Kind)
        {
            case ChangeKind.Insert when !c.Old.IsPresent:
                list.Add(new Claim { Kind = ClaimKind.Insert, Nodes = [], Key = Position(cmp, c.New.Nodes[0]), Effect = "ins|" + NodeEffect(cmp.New, c.New.Nodes[0]) });
                break;
            case ChangeKind.Delete when !c.New.IsPresent && c.Edits.IsEmpty:
                list.Add(new Claim { Kind = ClaimKind.Whole, Nodes = old, Effect = "del" });
                break;
            case ChangeKind.Move when old.Length > 0 && c.New.IsPresent:
                list.Add(new Claim { Kind = ClaimKind.Moved, Nodes = [old[0]], Key = Position(cmp, c.New.Nodes[0]), Effect = "mv" });
                break;
            case ChangeKind.Split or ChangeKind.Merge:
                list.Add(new Claim { Kind = ClaimKind.Structure, Nodes = old, Effect = c.Kind + "|" + c.New.Nodes.Length.ToString(CultureInfo.InvariantCulture) + "|" + c.New.Text });
                break;
        }

        if (list.Count == 0)
        {
            // A change no rule above describes still claims its old nodes, so it can never pass unnoticed next to another's.
            list.Add(old.Length > 0
                ? new Claim { Kind = ClaimKind.Structure, Nodes = old, Effect = c.Kind + "|" + c.New.Text }
                : new Claim { Kind = ClaimKind.Insert, Nodes = [], Key = c.New.IsPresent ? Position(cmp, c.New.Nodes[0]) : "?", Effect = c.Kind + "|" + c.New.Text });
        }

        return list;
    }

    public static string RunsKey(ImmutableArray<FormatRun> runs)
    {
        if (runs.IsDefaultOrEmpty)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (FormatRun r in runs)
        {
            sb.Append(r.Start.ToString(CultureInfo.InvariantCulture)).Append('+').Append(r.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(r.Format.ToString()).Append(';');
        }

        return sb.ToString();
    }

    private static string NodeEffect(CompareDocument d, int node)
    {
        Node n = d[node];
        return n.Kind + "|" + n.Text + "|" + n.Attributes + "|" + RunsKey(n.Runs);
    }

    /// <summary>
    /// Where a new node sits in terms of the original: the old node of the linked parent (or -1), the old node of the nearest
    /// linked sibling before it (or -1), and how many unlinked siblings lie between. A node inside an inserted subtree is
    /// placed by the top of that subtree and its path below it.
    /// </summary>
    private static string Position(Comparison cmp, int newNode)
    {
        CompareDocument nd = cmp.New;
        Alignment al = cmp.Alignment;
        int top = newNode;
        var path = new List<int>();
        while (nd[top].Parent >= 0 && al.LinkOfNew(nd[top].Parent) is null)
        {
            ImmutableArray<int> sib = nd.ChildrenOf(nd[top].Parent);
            path.Add(sib.IndexOf(top));
            top = nd[top].Parent;
        }

        int parent = nd[top].Parent;
        int parentOld = parent >= 0 && al.LinkOfNew(parent) is { OldNodes.Length: > 0 } pl ? pl.OldNodes[0] : -1;
        ImmutableArray<int> siblings = parent >= 0 ? nd.ChildrenOf(parent) : nd.Roots;
        int at = siblings.IndexOf(top);
        int prevOld = -1;
        int unlinked = 0;
        for (int i = at - 1; i >= 0; i--)
        {
            if (al.LinkOfNew(siblings[i]) is { OldNodes.Length: > 0 } l)
            {
                prevOld = l.OldNodes[^1];
                break;
            }

            unlinked++;
        }

        path.Reverse();
        return $"{parentOld.ToString(CultureInfo.InvariantCulture)}/{prevOld.ToString(CultureInfo.InvariantCulture)}/{unlinked.ToString(CultureInfo.InvariantCulture)}/{string.Join('.', path)}";
    }

    /// <summary>The place in the original's order a change belongs to: a node and a rank within it.</summary>
    public static (int Node, int Rank, int Start) OrderOf(Change c, List<Claim> claims)
    {
        if (c.Old.IsPresent)
        {
            int start = claims.Count > 0 && claims[0].Kind == ClaimKind.Text ? claims[0].Start : c.Old.Start;
            return (c.Old.Nodes[0], 0, start);
        }

        foreach (Claim k in claims)
        {
            if (k.Kind == ClaimKind.Insert)
            {
                string[] p = k.Key.Split('/');
                if (p.Length >= 3 && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int prev) && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parent))
                {
                    return (prev >= 0 ? prev : parent, 1, 0);
                }
            }
        }

        return (-1, 1, 0);
    }
}

/// <summary>The conflict rules between claims of different reviewers; see docs/combine.md.</summary>
internal static class ClaimRules
{
    public static ConflictKind? Test(Claim a, Claim b)
    {
        if (a.Same(b) || a.Derived || b.Derived)
        {
            return null;
        }

        if (a.Kind == ClaimKind.Insert || b.Kind == ClaimKind.Insert)
        {
            return a.Kind == b.Kind && string.Equals(a.Key, b.Key, StringComparison.Ordinal) ? ConflictKind.Insert : null;
        }

        if (!Intersect(a.Nodes, b.Nodes))
        {
            return null;
        }

        if (a.Kind == ClaimKind.Whole || b.Kind == ClaimKind.Whole)
        {
            return ConflictKind.EditAndDelete;
        }

        switch (a.Kind, b.Kind)
        {
            case (ClaimKind.Text, ClaimKind.Text):
                return a.Nodes.AsSpan().SequenceEqual(b.Nodes.AsSpan()) ? (Overlap(a, b) ? ConflictKind.Edits : null) : ConflictKind.Edits;
            case (ClaimKind.Structure, ClaimKind.Structure):
                return ConflictKind.Structure;
            case (ClaimKind.Attr, ClaimKind.Attr):
                return string.Equals(a.Key, b.Key, StringComparison.Ordinal) ? ConflictKind.Attribute : null;
            case (ClaimKind.Moved, ClaimKind.Moved):
                return ConflictKind.Move;
            default:
                return null;
        }
    }

    private static bool Intersect(ImmutableArray<int> a, ImmutableArray<int> b)
    {
        foreach (int x in a)
        {
            if (b.Contains(x))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ranges overlap when they share a character; a point (an insertion) conflicts inside a range or at the same point as another, and not at a range's edge.</summary>
    private static bool Overlap(Claim a, Claim b)
    {
        bool pa = a.Start == a.End, pb = b.Start == b.End;
        if (pa && pb)
        {
            return a.Start == b.Start;
        }

        if (pa)
        {
            return b.Start < a.Start && a.Start < b.End;
        }

        if (pb)
        {
            return a.Start < b.Start && b.Start < a.End;
        }

        return a.Start < b.End && b.Start < a.End;
    }
}
