using System.Collections.Immutable;
using System.Text;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Pipeline;

/// <summary>
/// The node numbers a document's hidden subtrees hold under some settings. Comments and properties can be hidden into the
/// ledger; everything inside a hidden node is hidden with it.
/// </summary>
internal static class HiddenNodes
{
    /// <summary>For each node, the category that hides it (the nearest hidden ancestor-or-self), or null when it is visible.</summary>
    public static LedgerCategory?[] Compute(CompareDocument doc, CompareSettings settings)
    {
        var result = new LedgerCategory?[doc.Count];
        bool comments = settings.Hidden.Contains(LedgerCategory.Comments);
        bool properties = settings.Hidden.Contains(LedgerCategory.Properties);
        if (!comments && !properties)
        {
            return result;
        }

        foreach (Node n in doc.Nodes)
        {
            LedgerCategory? inherited = n.Parent >= 0 ? result[n.Parent] : null;
            result[n.Id] = inherited
                ?? (n.Kind == NodeKind.Comment && comments ? LedgerCategory.Comments
                    : n.Kind == NodeKind.Property && properties ? LedgerCategory.Properties
                    : null);
        }

        return result;
    }
}

/// <summary>
/// One or more nodes of a document seen through the settings: the text that is compared (normalization folds of categories that
/// are not hidden are undone, so they show as changes), the formatting split into what is compared and what is hidden, and
/// the maps back to the nodes' own coordinates. Several nodes (a split or merge) are joined with U+001E.
/// </summary>
internal sealed class ProjectedGroup
{
    public const char Separator = '\u001E';

    private ProjectedGroup(string text, ImmutableArray<FormatRun> runs, ImmutableArray<FormatRun> hiddenRuns, List<NormalizationFold> hiddenFolds, Member[] members)
    {
        Text = text;
        Runs = runs;
        HiddenRuns = hiddenRuns;
        HiddenFolds = hiddenFolds;
        Members = members;
    }

    public string Text { get; }

    /// <summary>Formatting that is compared.</summary>
    public ImmutableArray<FormatRun> Runs { get; }

    /// <summary>Formatting that is hidden into the ledger.</summary>
    public ImmutableArray<FormatRun> HiddenRuns { get; }

    /// <summary>Folds that stay folded (their category is hidden), at their projected positions.</summary>
    public List<NormalizationFold> HiddenFolds { get; }

    public Member[] Members { get; }

    public sealed record Member(int NodeId, int Start, int Length, int[] ToNode, FormatSet Attributes, FormatSet HiddenAttributes);

    public static ProjectedGroup Create(CompareDocument doc, ImmutableArray<int> ids, CompareSettings settings)
    {
        var text = new StringBuilder();
        ImmutableArray<FormatRun>.Builder runs = ImmutableArray.CreateBuilder<FormatRun>();
        ImmutableArray<FormatRun>.Builder hiddenRuns = ImmutableArray.CreateBuilder<FormatRun>();
        var hiddenFolds = new List<NormalizationFold>();
        var members = new Member[ids.Length];
        for (int m = 0; m < ids.Length; m++)
        {
            Node node = doc[ids[m]];
            if (m > 0)
            {
                text.Append(Separator);
            }

            int offset = text.Length;
            ProjectNode(node, settings, text, offset, runs, hiddenRuns, hiddenFolds, out int[] toNode);
            FormatSet visible = node.Attributes.Where(k => !IsHiddenKey(k, settings));
            FormatSet hidden = node.Attributes.Where(k => IsHiddenKey(k, settings));
            members[m] = new Member(node.Id, offset, text.Length - offset, toNode, visible, hidden);
        }

        return new ProjectedGroup(text.ToString(), runs.ToImmutable(), hiddenRuns.ToImmutable(), hiddenFolds, members);
    }

    /// <summary>The text of one node under the settings; cheap when nothing needs undoing.</summary>
    public static string TextOf(CompareDocument doc, Node node, CompareSettings settings)
    {
        bool any = false;
        foreach (NormalizationFold f in node.Folds)
        {
            if (!settings.Hidden.Contains(f.Category))
            {
                any = true;
                break;
            }
        }

        return any ? Create(doc, [node.Id], settings).Text : node.Text;
    }

    public static bool IsHiddenKey(string key, CompareSettings settings) =>
        FormatKeys.CategoryOf(key) is LedgerCategory c && settings.Hidden.Contains(c);

    /// <summary>Resolves the anchor of the range [start, start+length) of the joined text.</summary>
    public Anchor ResolveAnchor(CompareDocument doc, int start, int length)
    {
        int end = start + length;
        var parts = new List<Anchor>();
        foreach (Member m in Members)
        {
            int ms = m.Start, me = m.Start + m.Length;
            bool overlaps = length == 0 ? start >= ms && start <= me : start < me && end > ms;
            if (!overlaps)
            {
                continue;
            }

            int a = Math.Clamp(start, ms, me) - ms, b = Math.Clamp(end, ms, me) - ms;
            parts.Add(ResolveInNode(doc[m.NodeId], m.ToNode[a], m.ToNode[b]));
        }

        return Anchor.Union(parts);
    }

    private static Anchor ResolveInNode(Node node, int from, int to)
    {
        if (node.Segments.IsDefaultOrEmpty)
        {
            return node.Anchor;
        }

        var hits = new List<Anchor>();
        foreach (AnchoredSpan s in node.Segments)
        {
            int se = s.Start + s.Length;
            bool hit = from == to ? s.Start <= from && from <= se : s.Start < to && se > from;
            if (hit)
            {
                hits.Add(s.Anchor);
            }
        }

        return hits.Count == 0 ? node.Anchor : Anchor.Union(hits);
    }

    private static void ProjectNode(
        Node node,
        CompareSettings settings,
        StringBuilder sb,
        int offset,
        ImmutableArray<FormatRun>.Builder runs,
        ImmutableArray<FormatRun>.Builder hiddenRuns,
        List<NormalizationFold> hiddenFolds,
        out int[] toNode)
    {
        string t = node.Text;
        var p = new int[t.Length + 1];
        var back = new List<int>(t.Length + 1);
        int pos = 0;
        foreach (NormalizationFold f in node.Folds)
        {
            for (; pos < f.Start; pos++)
            {
                p[pos] = sb.Length - offset;
                back.Add(pos);
                sb.Append(t[pos]);
            }

            if (settings.Hidden.Contains(f.Category))
            {
                hiddenFolds.Add(new NormalizationFold(f.Category, sb.Length, f.Length, f.Original));
                for (; pos < f.Start + f.Length; pos++)
                {
                    p[pos] = sb.Length - offset;
                    back.Add(pos);
                    sb.Append(t[pos]);
                }
            }
            else
            {
                // Undo the fold: the original text takes the place of the replacement, so it shows as a difference.
                int startProj = sb.Length - offset;
                for (int k = 0; k < f.Length; k++)
                {
                    p[f.Start + k] = startProj + Math.Min(k, f.Original.Length);
                }

                for (int j = 0; j < f.Original.Length; j++)
                {
                    back.Add(f.Start + (f.Length == 0 ? 0 : Math.Min(j, f.Length)));
                }

                sb.Append(f.Original);
                pos = f.Start + f.Length;
            }
        }

        for (; pos < t.Length; pos++)
        {
            p[pos] = sb.Length - offset;
            back.Add(pos);
            sb.Append(t[pos]);
        }

        p[t.Length] = sb.Length - offset;
        back.Add(t.Length);
        toNode = [.. back];

        foreach (FormatRun r in node.Runs)
        {
            int s = p[r.Start], e = p[r.End];
            if (e <= s)
            {
                continue;
            }

            FormatSet vis = r.Format.Where(k => !IsHiddenKey(k, settings));
            FormatSet hid = r.Format.Where(k => IsHiddenKey(k, settings));
            if (!vis.IsEmpty)
            {
                FormatRuns.AppendMerged(runs, new FormatRun(s + offset, e - s, vis));
            }

            if (!hid.IsEmpty)
            {
                FormatRuns.AppendMerged(hiddenRuns, new FormatRun(s + offset, e - s, hid));
            }
        }
    }
}
