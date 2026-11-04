using System.Collections.Immutable;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Ledger;

/// <summary>
/// Works out the ledger: every difference between the two compare models that the changes do not show. The result depends only on
/// the alignment, the edits of each link and the settings, so verification can recompute it and compare.
/// </summary>
internal static class LedgerBuilder
{
    public static ChangeLedger Build(Alignment alignment, Func<int, IReadOnlyList<RegionEdit>> editsOf, CancellationToken ct)
    {
        CompareSettings settings = alignment.Settings;
        CompareDocument oldDoc = alignment.Old, newDoc = alignment.New;
        LedgerCategory?[] hiddenOld = HiddenNodes.Compute(oldDoc, settings);
        LedgerCategory?[] hiddenNew = HiddenNodes.Compute(newDoc, settings);
        var found = new Dictionary<LedgerCategory, List<LedgerLocation>>();

        void Add(LedgerCategory category, LedgerLocation location)
        {
            if (!found.TryGetValue(category, out List<LedgerLocation>? list))
            {
                found[category] = list = [];
            }

            list.Add(location);
        }

        foreach (AlignmentLink link in alignment.Links)
        {
            ct.ThrowIfCancellationRequested();
            LedgerCategory? hidden = hiddenOld[link.OldNodes[0]] ?? hiddenNew[link.NewNodes[0]];
            if (hidden is LedgerCategory hc)
            {
                BuildHiddenLink(alignment, link, hc, Add);
                continue;
            }

            BuildLink(alignment, link, editsOf(link.Id), Add);
        }

        foreach (int o in alignment.UnmatchedOld())
        {
            if (hiddenOld[o] is LedgerCategory c)
            {
                Add(c, new LedgerLocation(o, -1, "removed", oldDoc[o].Anchor, Anchor.None));
            }
        }

        foreach (int n in alignment.UnmatchedNew())
        {
            if (hiddenNew[n] is LedgerCategory c)
            {
                Add(c, new LedgerLocation(-1, n, "added", Anchor.None, newDoc[n].Anchor));
            }
        }

        BuildOpaque(oldDoc, newDoc, Add);

        var entries = new List<LedgerEntry>();
        foreach (KeyValuePair<LedgerCategory, List<LedgerLocation>> kv in found.OrderBy(static kv => (int)kv.Key))
        {
            entries.Add(new LedgerEntry(kv.Key, kv.Value.Count, [.. kv.Value]));
        }

        return ChangeLedger.Create(entries);
    }

    private static void BuildOpaque(CompareDocument oldDoc, CompareDocument newDoc, Action<LedgerCategory, LedgerLocation> add)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var oldParts = new Dictionary<string, List<OpaquePart>>(StringComparer.Ordinal);
        var newParts = new Dictionary<string, List<OpaquePart>>(StringComparer.Ordinal);
        foreach (OpaquePart p in oldDoc.Opaque)
        {
            names.Add(p.Name);
            (oldParts.TryGetValue(p.Name, out List<OpaquePart>? l) ? l : oldParts[p.Name] = []).Add(p);
        }

        foreach (OpaquePart p in newDoc.Opaque)
        {
            names.Add(p.Name);
            (newParts.TryGetValue(p.Name, out List<OpaquePart>? l) ? l : newParts[p.Name] = []).Add(p);
        }

        foreach (string name in names)
        {
            List<OpaquePart> a = oldParts.GetValueOrDefault(name) ?? [], b = newParts.GetValueOrDefault(name) ?? [];
            for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
            {
                if (i < a.Count && i < b.Count)
                {
                    if (!string.Equals(a[i].Hash, b[i].Hash, StringComparison.Ordinal))
                    {
                        add(LedgerCategory.NotItemized, new LedgerLocation(-1, -1, "differs: " + name, a[i].Anchor, b[i].Anchor));
                    }
                }
                else if (i < a.Count)
                {
                    add(LedgerCategory.NotItemized, new LedgerLocation(-1, -1, "removed: " + name, a[i].Anchor, Anchor.None));
                }
                else
                {
                    add(LedgerCategory.NotItemized, new LedgerLocation(-1, -1, "added: " + name, Anchor.None, b[i].Anchor));
                }
            }
        }
    }

    /// <summary>A link inside a hidden subtree is not compared at all; if its content differs in any way that is one hidden difference.</summary>
    private static void BuildHiddenLink(Alignment alignment, AlignmentLink link, LedgerCategory category, Action<LedgerCategory, LedgerLocation> add)
    {
        // Hidden nodes are compared as they are: nothing of them is folded back, so use settings that hide nothing.
        CompareSettings none = alignment.Settings with { Hidden = CategorySet.None };
        ProjectedGroup og = ProjectedGroup.Create(alignment.Old, link.OldNodes, none);
        ProjectedGroup ng = ProjectedGroup.Create(alignment.New, link.NewNodes, none);
        bool same = string.Equals(og.Text, ng.Text, StringComparison.Ordinal) && FormatRuns.SameRuns(og.Runs, ng.Runs) && og.Members.Length == ng.Members.Length;
        for (int i = 0; same && i < og.Members.Length; i++)
        {
            same = FormatSet.ContentEquals(og.Members[i].Attributes, ng.Members[i].Attributes);
        }

        if (!same)
        {
            add(category, new LedgerLocation(
                link.OldNodes[0],
                link.NewNodes[0],
                "changed",
                alignment.Old[link.OldNodes[0]].Anchor,
                alignment.New[link.NewNodes[0]].Anchor));
        }
    }

    private static void BuildLink(Alignment alignment, AlignmentLink link, IReadOnlyList<RegionEdit> edits, Action<LedgerCategory, LedgerLocation> add)
    {
        CompareSettings settings = alignment.Settings;
        ProjectedGroup og = ProjectedGroup.Create(alignment.Old, link.OldNodes, settings);
        ProjectedGroup ng = ProjectedGroup.Create(alignment.New, link.NewNodes, settings);

        // Hidden attributes: compare every old/new pair of the link.
        foreach (int o in link.OldNodes)
        {
            foreach (int n in link.NewNodes)
            {
                ProjectedGroup.Member mo = og.Members.First(m => m.NodeId == o), mn = ng.Members.First(m => m.NodeId == n);
                foreach (string key in KeysOf(mo.HiddenAttributes, mn.HiddenAttributes))
                {
                    string? a = mo.HiddenAttributes.Get(key), b = mn.HiddenAttributes.Get(key);
                    if (!string.Equals(a, b, StringComparison.Ordinal) && FormatKeys.CategoryOf(key) is LedgerCategory c)
                    {
                        add(c, new LedgerLocation(o, n, key, alignment.Old[o].Anchor, alignment.New[n].Anchor));
                    }
                }
            }
        }

        if (og.HiddenFolds.Count == 0 && ng.HiddenFolds.Count == 0 && og.HiddenRuns.IsEmpty && ng.HiddenRuns.IsEmpty)
        {
            return;
        }

        // The text outside the edits is equal on both sides; compare what was folded or formatted there.
        var sorted = new List<RegionEdit>(edits);
        sorted.Sort(static (a, b) => a.OldStart.CompareTo(b.OldStart));
        int po = 0, pn = 0;
        foreach (RegionEdit e in sorted)
        {
            Gap(po, e.OldStart, pn, e.NewStart, false);
            po = e.OldEnd;
            pn = e.NewEnd;
        }

        Gap(po, og.Text.Length, pn, ng.Text.Length, true);

        void Gap(int a, int b, int c, int d, bool last)
        {
            if (b - a != d - c || b < a)
            {
                return; // verification reports malformed edits; nothing sensible to compare here
            }

            // Folds: a fold present on only one side, or with a different original, is a hidden difference.
            var oldFolds = new Dictionary<(LedgerCategory, int, int), string>();
            var newFolds = new Dictionary<(LedgerCategory, int, int), string>();
            foreach (NormalizationFold f in og.HiddenFolds)
            {
                if (f.Start >= a && (f.Start < b || (last && f.Start == b)))
                {
                    oldFolds[(f.Category, f.Start - a + c, f.Length)] = f.Original;
                }
            }

            foreach (NormalizationFold f in ng.HiddenFolds)
            {
                if (f.Start >= c && (f.Start < d || (last && f.Start == d)))
                {
                    newFolds[(f.Category, f.Start, f.Length)] = f.Original;
                }
            }

            foreach ((LedgerCategory cat, int pos, int len) key in oldFolds.Keys.Union(newFolds.Keys).OrderBy(static k => k.Item2).ThenBy(static k => (int)k.Item1).ThenBy(static k => k.Item3))
            {
                bool inOld = oldFolds.TryGetValue(key, out string? so), inNew = newFolds.TryGetValue(key, out string? sn);
                if (inOld && inNew && string.Equals(so, sn, StringComparison.Ordinal))
                {
                    continue;
                }

                string plain = ng.Text.Substring(key.pos, key.len);
                string left = inOld ? so! : plain, right = inNew ? sn! : plain;
                int oldPos = key.pos - c + a;
                add(key.cat, new LedgerLocation(
                    NodeAt(og, oldPos),
                    NodeAt(ng, key.pos),
                    left + " | " + right,
                    og.ResolveAnchor(alignment.Old, oldPos, key.len),
                    ng.ResolveAnchor(alignment.New, key.pos, key.len)));
            }

            // Hidden formatting, one category at a time.
            if (og.HiddenRuns.IsEmpty && ng.HiddenRuns.IsEmpty)
            {
                return;
            }

            foreach (LedgerCategory cat in HiddenFormatCategories(settings))
            {
                ImmutableArray<FormatRun> ra = Only(og.HiddenRuns, cat), rb = Only(ng.HiddenRuns, cat);
                if (ra.IsEmpty && rb.IsEmpty)
                {
                    continue;
                }

                foreach ((int off, int len) in FormatRuns.Differences(ra, a, rb, c, b - a))
                {
                    add(cat, new LedgerLocation(
                        NodeAt(og, a + off),
                        NodeAt(ng, c + off),
                        "format",
                        og.ResolveAnchor(alignment.Old, a + off, len),
                        ng.ResolveAnchor(alignment.New, c + off, len)));
                }
            }
        }
    }

    private static IEnumerable<LedgerCategory> HiddenFormatCategories(CompareSettings settings)
    {
        foreach (LedgerCategory c in new[] { LedgerCategory.RunFormatting, LedgerCategory.ParagraphFormatting, LedgerCategory.ListFormatting, LedgerCategory.SectionFormatting, LedgerCategory.FieldCodes })
        {
            if (settings.Hidden.Contains(c))
            {
                yield return c;
            }
        }
    }

    private static ImmutableArray<FormatRun> Only(ImmutableArray<FormatRun> runs, LedgerCategory category)
    {
        ImmutableArray<FormatRun>.Builder b = ImmutableArray.CreateBuilder<FormatRun>();
        foreach (FormatRun r in runs)
        {
            FormatSet f = r.Format.Where(k => FormatKeys.CategoryOf(k) == category);
            if (!f.IsEmpty)
            {
                FormatRuns.AppendMerged(b, new FormatRun(r.Start, r.Length, f));
            }
        }

        return b.ToImmutable();
    }

    private static int NodeAt(ProjectedGroup g, int pos)
    {
        foreach (ProjectedGroup.Member m in g.Members)
        {
            if (pos >= m.Start && pos <= m.Start + m.Length)
            {
                return m.NodeId;
            }
        }

        return g.Members[^1].NodeId;
    }

    private static SortedSet<string> KeysOf(FormatSet a, FormatSet b)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in a.Entries)
        {
            keys.Add(kv.Key);
        }

        foreach (KeyValuePair<string, string> kv in b.Entries)
        {
            keys.Add(kv.Key);
        }

        return keys;
    }
}
