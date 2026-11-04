using System.Collections.Immutable;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Pipeline;

/// <summary>The reference grouping and classification: one change per diff region, plain kinds, no typing.</summary>
internal static class ReferenceGrouper
{
    /// <summary>Makes the changes for the text and format edits of one ordinary link (not a split or merge), each with the new-text position it sorts by.</summary>
    internal delegate List<(int Position, Change Change)> LinkUnitizer(AlignmentLink link, ProjectedGroup oldText, ProjectedGroup newText, LinkDiff diff, Alignment alignment);

    public static ChangeSet Group(PairDiffs diffs, CancellationToken ct, LinkUnitizer? unitizer = null)
    {
        Alignment al = diffs.Alignment;
        CompareSettings settings = al.Settings;
        LedgerCategory?[] hiddenOld = HiddenNodes.Compute(al.Old, settings);
        LedgerCategory?[] hiddenNew = HiddenNodes.Compute(al.New, settings);

        // For a deleted node, the new position it sits before: the new node of the next old node that has a partner.
        var nextNew = new int[al.Old.Count + 1];
        nextNew[al.Old.Count] = int.MaxValue;
        for (int o = al.Old.Count - 1; o >= 0; o--)
        {
            AlignmentLink? l = al.LinkOfOld(o);
            nextNew[o] = l is null ? nextNew[o + 1] : l.NewNodes[0];
        }

        var items = new List<(int Primary, int Secondary, int Tertiary, Change Change)>();

        foreach (int o in al.UnmatchedOld())
        {
            if (hiddenOld[o] is not null)
            {
                continue;
            }

            ProjectedGroup g = ProjectedGroup.Create(al.Old, [o], settings);
            items.Add((nextNew[o], 0, o, Make(ChangeKind.Delete, -1, Side(al.Old, g, [o], 0, g.Text.Length), ChangeSide.Absent, [], [])));
        }

        foreach (int n in al.UnmatchedNew())
        {
            if (hiddenNew[n] is not null)
            {
                continue;
            }

            ProjectedGroup g = ProjectedGroup.Create(al.New, [n], settings);
            items.Add((n, 1, -1, Make(ChangeKind.Insert, -1, ChangeSide.Absent, Side(al.New, g, [n], 0, g.Text.Length), [], [])));
        }

        foreach (AlignmentLink link in al.Links)
        {
            ct.ThrowIfCancellationRequested();
            if (hiddenOld[link.OldNodes[0]] is not null || hiddenNew[link.NewNodes[0]] is not null)
            {
                continue;
            }

            LinkDiff diff = diffs.Links[link.Id];
            ProjectedGroup og = ProjectedGroup.Create(al.Old, link.OldNodes, settings);
            ProjectedGroup ng = ProjectedGroup.Create(al.New, link.NewNodes, settings);
            int primary = link.NewNodes[0];
            ChangeSide wholeOld = Side(al.Old, og, link.OldNodes, 0, og.Text.Length);
            ChangeSide wholeNew = Side(al.New, ng, link.NewNodes, 0, ng.Text.Length);

            if (link.Kind is LinkKind.Split or LinkKind.Merge)
            {
                items.Add((primary, 1, -1, Make(link.Kind == LinkKind.Split ? ChangeKind.Split : ChangeKind.Merge, link.Id, wholeOld, wholeNew, diff.Edits, diff.AttributeEdits)));
                continue;
            }

            if (link.Kind == LinkKind.Move)
            {
                items.Add((primary, 1, -1, Make(ChangeKind.Move, link.Id, wholeOld, wholeNew, [], [])));
            }

            if (unitizer is not null)
            {
                foreach ((int position, Change change) in unitizer(link, og, ng, diff, al))
                {
                    items.Add((primary, 1, position, change));
                }
            }
            else
            {
                foreach (RegionEdit e in diff.Edits)
                {
                    ChangeKind kind = e.IsFormatOnly ? ChangeKind.Format : e.OldLength == 0 ? ChangeKind.Insert : e.NewLength == 0 ? ChangeKind.Delete : ChangeKind.Replace;
                    items.Add((primary, 1, e.NewStart, Make(
                        kind,
                        link.Id,
                        Side(al.Old, og, link.OldNodes, e.OldStart, e.OldLength),
                        Side(al.New, ng, link.NewNodes, e.NewStart, e.NewLength),
                        [e],
                        [])));
                }
            }

            if (!diff.AttributeEdits.IsEmpty)
            {
                items.Add((primary, 1, -1, Make(ChangeKind.Attribute, link.Id, wholeOld, wholeNew, [], diff.AttributeEdits)));
            }
        }

        // Stable sort: the generation order breaks ties.
        var order = Enumerable.Range(0, items.Count).ToList();
        order.Sort((x, y) =>
        {
            int c = items[x].Primary.CompareTo(items[y].Primary);
            if (c != 0)
            {
                return c;
            }

            c = items[x].Secondary.CompareTo(items[y].Secondary);
            if (c != 0)
            {
                return c;
            }

            c = items[x].Tertiary.CompareTo(items[y].Tertiary);
            return c != 0 ? c : x.CompareTo(y);
        });

        ImmutableArray<Change>.Builder changes = ImmutableArray.CreateBuilder<Change>(items.Count);
        ImmutableArray<ChangeGroup>.Builder groups = ImmutableArray.CreateBuilder<ChangeGroup>(items.Count);
        foreach (int idx in order)
        {
            int id = changes.Count;
            changes.Add(items[idx].Change with { Id = id });
            groups.Add(new ChangeGroup(id, GroupKind.Region, "", [id]));
        }

        return new ChangeSet(al, changes.MoveToImmutable(), groups.MoveToImmutable(), diffs.Ledger, diffs.Diagnostics, false);
    }

    public static ChangeSet Classify(ChangeSet set, CancellationToken ct)
    {
        var decision = new Decision(DecidedBy.Rule, "reference", 1.0);
        ImmutableArray<Change>.Builder b = ImmutableArray.CreateBuilder<Change>(set.Changes.Length);
        foreach (Change c in set.Changes)
        {
            ct.ThrowIfCancellationRequested();
            b.Add(c with { Decision = decision });
        }

        return new ChangeSet(set.Alignment, b.MoveToImmutable(), set.Groups, set.Ledger, set.Diagnostics, true);
    }

    /// <summary>The side of a change for a range of the joined text of <paramref name="nodes"/>.</summary>
    public static ChangeSide Side(CompareDocument doc, ProjectedGroup g, ImmutableArray<int> nodes, int start, int length) =>
        new(nodes, start, length, g.Text.Substring(start, length), g.ResolveAnchor(doc, start, length));

    internal static Change Make(ChangeKind kind, int linkId, ChangeSide old, ChangeSide @new, ImmutableArray<RegionEdit> edits, ImmutableArray<AttributeEdit> attributeEdits) =>
        new(0, kind, linkId, old, @new, edits, attributeEdits, Decision.Unclassified, FormatSet.Empty);
}
