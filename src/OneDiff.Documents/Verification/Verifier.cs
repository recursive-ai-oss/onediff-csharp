using System.Collections.Immutable;
using System.Text;
using OneDiff.Documents.Ledger;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Verification;

/// <summary>One check that failed.</summary>
/// <param name="Code">A stable code: V1xx change and group bookkeeping, V2xx node accounting, V3xx links and replay, V4xx structure, V5xx ledger.</param>
/// <param name="Message">What is wrong, in a sentence.</param>
public sealed record VerificationFailure(string Code, string Message);

/// <summary>Thrown by verification in strict mode (see <see cref="CompareSettings.ThrowOnFailure"/>) when a comparison is not complete.</summary>
public sealed class ComparisonVerificationException : Exception
{
    /// <summary>Creates the exception from the failures found.</summary>
    public ComparisonVerificationException(IEnumerable<VerificationFailure> failures)
        : base(Describe(failures, out ImmutableArray<VerificationFailure> list))
    {
        Failures = list;
    }

    /// <summary>The failures, at most the first 50.</summary>
    public ImmutableArray<VerificationFailure> Failures { get; }

    private static string Describe(IEnumerable<VerificationFailure> failures, out ImmutableArray<VerificationFailure> list)
    {
        list = [.. failures];
        return list.Length == 0
            ? "comparison verification failed"
            : $"comparison verification failed ({list.Length} problem{(list.Length == 1 ? "" : "s")}); first: {list[0].Code} {list[0].Message}";
    }
}

/// <summary>
/// The completeness checks. Given an alignment, changes, groups and ledger, it proves or refutes, without trusting any stage:
/// every node is matched or inserted/deleted exactly once; replaying the changes' edits on the old compared content gives the
/// new content and replaying them backwards gives the old; matched blocks keep their parents and sibling order unless a move is
/// recorded; every change carries both sides with text and anchors that resolve to the documents; changes are grouped exactly
/// once; and the ledger lists every hidden difference.
/// </summary>
internal static class Verifier
{
    private const int MaxFailures = 50;

    public static List<VerificationFailure> Check(
        Alignment al,
        ImmutableArray<Change> changes,
        ImmutableArray<ChangeGroup> groups,
        ChangeLedger ledger,
        CancellationToken ct)
    {
        var run = new Run(al, changes, groups, ledger, ct);
        run.Execute();
        return run.Failures;
    }

    private sealed class Run
    {
        private readonly Alignment al;
        private readonly ImmutableArray<Change> changes;
        private readonly ImmutableArray<ChangeGroup> groups;
        private readonly ChangeLedger ledger;
        private readonly CancellationToken ct;
        private readonly CompareSettings settings;
        private readonly LedgerCategory?[] hiddenOld;
        private readonly LedgerCategory?[] hiddenNew;
        private readonly Dictionary<string, ProjectedGroup> cache = new(StringComparer.Ordinal);

        public Run(Alignment al, ImmutableArray<Change> changes, ImmutableArray<ChangeGroup> groups, ChangeLedger ledger, CancellationToken ct)
        {
            this.al = al;
            this.changes = changes.IsDefault ? [] : changes;
            this.groups = groups.IsDefault ? [] : groups;
            this.ledger = ledger;
            this.ct = ct;
            settings = al.Settings;
            hiddenOld = HiddenNodes.Compute(al.Old, settings);
            hiddenNew = HiddenNodes.Compute(al.New, settings);
        }

        public List<VerificationFailure> Failures { get; } = [];

        public void Execute()
        {
            var linkChanges = new List<Change>?[al.Links.Length];
            var delOld = new int[al.Old.Count];
            var insNew = new int[al.New.Count];
            CheckIdsAndGroups();
            foreach (Change c in changes)
            {
                ct.ThrowIfCancellationRequested();
                CheckChange(c, linkChanges, delOld, insNew);
            }

            for (int o = 0; o < al.Old.Count; o++)
            {
                bool linked = al.LinkOfOld(o) is not null;
                int expected = linked || hiddenOld[o] is not null ? 0 : 1;
                if (delOld[o] != expected)
                {
                    Fail("V200", linked ? $"old node {o} is matched but also deleted {delOld[o]} time(s)" : $"old node {o} is neither matched nor deleted exactly once (deleted {delOld[o]} times)");
                }
            }

            for (int n = 0; n < al.New.Count; n++)
            {
                bool linked = al.LinkOfNew(n) is not null;
                int expected = linked || hiddenNew[n] is not null ? 0 : 1;
                if (insNew[n] != expected)
                {
                    Fail("V200", linked ? $"new node {n} is matched but also inserted {insNew[n]} time(s)" : $"new node {n} is neither matched nor inserted exactly once (inserted {insNew[n]} times)");
                }
            }

            var editsOfLink = new List<RegionEdit>[al.Links.Length];
            foreach (AlignmentLink link in al.Links)
            {
                ct.ThrowIfCancellationRequested();
                List<Change> chs = linkChanges[link.Id] ?? [];
                editsOfLink[link.Id] = chs.SelectMany(static c => c.Edits.IsDefault ? [] : c.Edits).ToList();
                CheckLink(link, chs, editsOfLink[link.Id]);
            }

            CheckStructure();
            CheckLedger(editsOfLink);
        }

        private void CheckIdsAndGroups()
        {
            var ids = new HashSet<int>();
            foreach (Change c in changes)
            {
                if (c.Id < 0 || !ids.Add(c.Id))
                {
                    Fail("V100", $"change id {c.Id} is negative or used twice");
                }
            }

            var groupIds = new HashSet<int>();
            var memberCount = new Dictionary<int, int>();
            foreach (ChangeGroup g in groups)
            {
                if (!groupIds.Add(g.Id))
                {
                    Fail("V101", $"group id {g.Id} is used twice");
                }

                foreach (int cid in g.ChangeIds.IsDefault ? [] : g.ChangeIds)
                {
                    if (!ids.Contains(cid))
                    {
                        Fail("V102", $"group {g.Id} lists unknown change {cid}");
                    }

                    memberCount[cid] = memberCount.GetValueOrDefault(cid) + 1;
                }
            }

            foreach (int id in ids.Order())
            {
                int n = memberCount.GetValueOrDefault(id);
                if (n != 1)
                {
                    Fail("V103", $"change {id} is in {n} groups; it must be in exactly one");
                }
            }
        }

        private void CheckChange(Change c, List<Change>?[] linkChanges, int[] delOld, int[] insNew)
        {
            if (c.Old is null || c.New is null || c.Edits.IsDefault || c.AttributeEdits.IsDefault)
            {
                Fail("V104", $"change {c.Id} is missing a side or an edit list");
                return;
            }

            if (c.LinkId == -1)
            {
                if (c.Kind is not (ChangeKind.Insert or ChangeKind.Delete) || !c.Edits.IsEmpty || !c.AttributeEdits.IsEmpty)
                {
                    Fail("V105", $"change {c.Id} is attached to no link, so it must be a whole-node insertion or deletion without edits");
                    return;
                }

                bool del = c.Kind == ChangeKind.Delete;
                ChangeSide present = del ? c.Old : c.New;
                ChangeSide absent = del ? c.New : c.Old;
                if (present.Nodes.IsDefault || present.Nodes.Length != 1 || absent.IsPresent)
                {
                    Fail("V105", $"change {c.Id} must name exactly one node on the side that exists and none on the other");
                    return;
                }

                CompareDocument doc = del ? al.Old : al.New;
                int node = present.Nodes[0];
                if (node < 0 || node >= doc.Count)
                {
                    Fail("V105", $"change {c.Id} names node {node}, which does not exist");
                    return;
                }

                (del ? delOld : insNew)[node]++;
                if ((del ? hiddenOld : hiddenNew)[node] is not null)
                {
                    Fail("V106", $"change {c.Id} shows node {node}, which is hidden into the ledger");
                }

                CheckSide(c, c.Old, true, null);
                CheckSide(c, c.New, false, null);
                if (present.Start != 0 || present.Length != ProjectedLength(del, node))
                {
                    Fail("V107", $"change {c.Id} must cover the whole text of node {node}");
                }

                return;
            }

            if (c.LinkId < 0 || c.LinkId >= al.Links.Length)
            {
                Fail("V105", $"change {c.Id} refers to link {c.LinkId}, which does not exist");
                return;
            }

            AlignmentLink link = al.Links[c.LinkId];
            (linkChanges[link.Id] ??= []).Add(c);
            bool shapeOk = c.Kind switch
            {
                ChangeKind.Insert or ChangeKind.Delete or ChangeKind.Replace or ChangeKind.Format => !c.Edits.IsEmpty && c.AttributeEdits.IsEmpty,
                ChangeKind.Move => c.Edits.IsEmpty && c.AttributeEdits.IsEmpty,
                ChangeKind.Attribute => c.Edits.IsEmpty && !c.AttributeEdits.IsEmpty,
                ChangeKind.Split or ChangeKind.Merge => true,
                _ => false,
            };
            if (!shapeOk)
            {
                Fail("V105", $"change {c.Id} ({c.Kind}) has edits that do not fit its kind");
            }

            if (!SameNodes(c.Old.Nodes, link.OldNodes) || !SameNodes(c.New.Nodes, link.NewNodes))
            {
                Fail("V108", $"change {c.Id} does not name the nodes of link {link.Id} on both sides");
                return;
            }

            CheckSide(c, c.Old, true, link);
            CheckSide(c, c.New, false, link);
            if (c.Kind is ChangeKind.Insert or ChangeKind.Delete or ChangeKind.Replace or ChangeKind.Format)
            {
                // A change may show more than its edits (a whole number, a whole sentence) but never less or elsewhere.
                foreach (RegionEdit e in c.Edits)
                {
                    if (e.OldStart < c.Old.Start || e.OldEnd > c.Old.Start + c.Old.Length || e.NewStart < c.New.Start || e.NewEnd > c.New.Start + c.New.Length)
                    {
                        Fail("V109", $"change {c.Id} shows ranges that do not contain its edit at {e.OldStart}/{e.NewStart}");
                    }
                }
            }
        }

        private void CheckSide(Change c, ChangeSide s, bool old, AlignmentLink? link)
        {
            if (s.Text is null || s.Anchor is null)
            {
                Fail("V110", $"change {c.Id} has a side without text or anchor");
                return;
            }

            if (!s.IsPresent)
            {
                if (link is not null || s.Start != 0 || s.Length != 0 || s.Text.Length != 0 || s.Anchor.Kind != AnchorKind.None)
                {
                    Fail("V110", $"change {c.Id} has an absent side that is not empty, or a missing side on a change inside a link");
                }

                return;
            }

            CompareDocument doc = old ? al.Old : al.New;
            foreach (int id in s.Nodes)
            {
                if (id < 0 || id >= doc.Count)
                {
                    Fail("V110", $"change {c.Id} names node {id}, which does not exist");
                    return;
                }
            }

            ProjectedGroup g = Project(old, s.Nodes);
            if (s.Start < 0 || s.Length < 0 || s.Start > g.Text.Length || s.Length > g.Text.Length - s.Start)
            {
                Fail("V111", $"change {c.Id} has a range outside the text it refers to");
                return;
            }

            if (!string.Equals(s.Text, g.Text.Substring(s.Start, s.Length), StringComparison.Ordinal))
            {
                Fail("V112", $"change {c.Id} carries text that is not the text of its range");
            }

            if (!s.Anchor.SameAs(g.ResolveAnchor(doc, s.Start, s.Length)))
            {
                Fail("V113", $"change {c.Id} carries an anchor that does not resolve back to its source range");
            }
        }

        private void CheckLink(AlignmentLink link, List<Change> chs, List<RegionEdit> edits)
        {
            bool hidden = hiddenOld[link.OldNodes[0]] is not null || hiddenNew[link.NewNodes[0]] is not null;
            if (hidden)
            {
                if (chs.Count > 0)
                {
                    Fail("V300", $"link {link.Id} is hidden into the ledger but has changes");
                }

                return;
            }

            int moves = chs.Count(static c => c.Kind == ChangeKind.Move);
            int splits = chs.Count(static c => c.Kind == ChangeKind.Split);
            int merges = chs.Count(static c => c.Kind == ChangeKind.Merge);
            if (moves != (link.Kind == LinkKind.Move ? 1 : 0) || splits != (link.Kind == LinkKind.Split ? 1 : 0) || merges != (link.Kind == LinkKind.Merge ? 1 : 0))
            {
                Fail("V301", $"link {link.Id} is a {link.Kind} but has {moves} move, {splits} split and {merges} merge changes");
            }

            ProjectedGroup og = Project(true, link.OldNodes), ng = Project(false, link.NewNodes);
            string? forward = Replay(og, ng, edits, false);
            if (forward is not null)
            {
                Fail("V302", $"link {link.Id}: old with the changes is not new: {forward}");
            }

            string? backward = Replay(ng, og, edits, true);
            if (backward is not null)
            {
                Fail("V303", $"link {link.Id}: new without the changes is not old: {backward}");
            }

            var attributeEdits = chs.SelectMany(static c => c.AttributeEdits.IsDefault ? [] : c.AttributeEdits).ToList();
            string? attrs = ReplayAttributes(link, og, ng, attributeEdits);
            if (attrs is not null)
            {
                Fail("V304", $"link {link.Id}: attributes do not replay: {attrs}");
            }
        }

        /// <summary>Applies the edits to <paramref name="src"/> (backwards when <paramref name="reverse"/>) and compares with <paramref name="dst"/>.</summary>
        private static string? Replay(ProjectedGroup src, ProjectedGroup dst, List<RegionEdit> edits, bool reverse)
        {
            var ordered = edits.Select((e, i) => (e, i)).OrderBy(x => reverse ? x.e.NewStart : x.e.OldStart).ThenBy(x => x.i).Select(x => x.e).ToList();
            var sb = new StringBuilder();
            ImmutableArray<FormatRun>.Builder runs = ImmutableArray.CreateBuilder<FormatRun>();
            int pos = 0;
            foreach (RegionEdit e in ordered)
            {
                int bs = reverse ? e.NewStart : e.OldStart, bl = reverse ? e.NewLength : e.OldLength;
                string? bt = reverse ? e.NewText : e.OldText;
                ImmutableArray<FormatRun> br = reverse ? e.NewRuns : e.OldRuns;
                int os = reverse ? e.OldStart : e.NewStart, ol = reverse ? e.OldLength : e.NewLength;
                string? ot = reverse ? e.OldText : e.NewText;
                ImmutableArray<FormatRun> orr = reverse ? e.OldRuns : e.NewRuns;
                if (bt is null || ot is null || br.IsDefault || orr.IsDefault)
                {
                    return "an edit has no text or formatting";
                }

                if (bs < pos || bl < 0 || bs > src.Text.Length || bl > src.Text.Length - bs || ol < 0)
                {
                    return $"an edit at {bs} overlaps another or lies outside the text";
                }

                if (bt.Length != bl || ot.Length != ol)
                {
                    return $"the edit at {bs} states lengths that its texts do not have";
                }

                if (!src.Text.AsSpan(bs, bl).SequenceEqual(bt))
                {
                    return $"the edit at {bs} does not match the text it replaces";
                }

                if (!FormatRuns.SameRuns(FormatRuns.Slice(src.Runs, bs, bl), br))
                {
                    return $"the edit at {bs} does not match the formatting it replaces";
                }

                sb.Append(src.Text, pos, bs - pos);
                FormatRuns.Append(runs, FormatRuns.Slice(src.Runs, pos, bs - pos), sb.Length - (bs - pos));
                if (sb.Length != os)
                {
                    return $"the edit at {bs} says it lands at {os} but replay is at {sb.Length}";
                }

                sb.Append(ot);
                FormatRuns.Append(runs, orr, os);
                pos = bs + bl;
            }

            sb.Append(src.Text, pos, src.Text.Length - pos);
            FormatRuns.Append(runs, FormatRuns.Slice(src.Runs, pos, src.Text.Length - pos), sb.Length - (src.Text.Length - pos));
            if (!string.Equals(sb.ToString(), dst.Text, StringComparison.Ordinal))
            {
                return "the text differs";
            }

            ImmutableArray<FormatRun> got = runs.ToImmutable();
            return FormatRuns.SameRuns(got, dst.Runs) ? null : $"the formatting differs (replay gives {Describe(got)}, expected {Describe(dst.Runs)})";
        }

        private static string Describe(ImmutableArray<FormatRun> runs) =>
            "[" + string.Join("; ", runs.Select(static r => $"{r.Start}+{r.Length} {r.Format}")) + "]";

        private static string? ReplayAttributes(AlignmentLink link, ProjectedGroup og, ProjectedGroup ng, List<AttributeEdit> edits)
        {
            foreach (bool reverse in new[] { false, true })
            {
                var state = new Dictionary<(int, int), Dictionary<string, string>>();
                foreach (int o in link.OldNodes)
                {
                    foreach (int n in link.NewNodes)
                    {
                        ProjectedGroup.Member from = (reverse ? ng : og).Members.First(m => m.NodeId == (reverse ? n : o));
                        state[(o, n)] = from.Attributes.Entries.ToDictionary(static kv => kv.Key, static kv => kv.Value, StringComparer.Ordinal);
                    }
                }

                foreach (AttributeEdit e in edits)
                {
                    if (e.Key is null || !state.TryGetValue((e.OldNode, e.NewNode), out Dictionary<string, string>? cur))
                    {
                        return "an attribute edit names a pair of nodes outside the link";
                    }

                    string? expect = reverse ? e.NewValue : e.OldValue, set = reverse ? e.OldValue : e.NewValue;
                    string? have = cur.GetValueOrDefault(e.Key);
                    if (!string.Equals(have, expect, StringComparison.Ordinal))
                    {
                        return $"attribute {e.Key} is not what the edit says it replaces";
                    }

                    if (set is null)
                    {
                        cur.Remove(e.Key);
                    }
                    else
                    {
                        cur[e.Key] = set;
                    }
                }

                foreach (KeyValuePair<(int, int), Dictionary<string, string>> kv in state)
                {
                    ProjectedGroup.Member target = (reverse ? og : ng).Members.First(m => m.NodeId == (reverse ? kv.Key.Item1 : kv.Key.Item2));
                    if (kv.Value.Count != target.Attributes.Count || target.Attributes.Entries.Any(e => !kv.Value.TryGetValue(e.Key, out string? v) || !string.Equals(v, e.Value, StringComparison.Ordinal)))
                    {
                        return $"attributes of nodes {kv.Key.Item1} and {kv.Key.Item2} differ after replay";
                    }
                }
            }

            return null;
        }

        private void CheckStructure()
        {
            var newOfOld = new int[al.Old.Count];
            Array.Fill(newOfOld, -1);
            foreach (AlignmentLink l in al.Links)
            {
                if (l.Kind is LinkKind.Match or LinkKind.Move)
                {
                    newOfOld[l.OldNodes[0]] = l.NewNodes[0];
                }
            }

            var lastOld = new Dictionary<int, int>();
            foreach (AlignmentLink l in al.Links)
            {
                if (l.Kind != LinkKind.Match)
                {
                    continue;
                }

                int o = l.OldNodes[0], n = l.NewNodes[0];
                if (hiddenOld[o] is not null || hiddenNew[n] is not null)
                {
                    continue;
                }

                int po = al.Old[o].Parent, pn = al.New[n].Parent;
                bool parentOk = (po < 0 && pn < 0) || (po >= 0 && pn >= 0 && newOfOld[po] == pn);
                if (!parentOk)
                {
                    Fail("V400", $"link {l.Id} keeps its place but its parent changed; it needs a move");
                    continue;
                }

                if (lastOld.TryGetValue(pn, out int prev) && prev > o)
                {
                    Fail("V401", $"link {l.Id} comes after a sibling that was after it in the old document; one of them needs a move");
                }

                lastOld[pn] = o;
            }
        }

        private void CheckLedger(List<RegionEdit>[] editsOfLink)
        {
            ChangeLedger expected;
            try
            {
                expected = LedgerBuilder.Build(al, id => editsOfLink[id], ct);
            }
            catch (ArgumentException ex)
            {
                Fail("V500", "the ledger could not be recomputed: " + ex.Message);
                return;
            }

            foreach (LedgerEntry want in expected.Entries)
            {
                LedgerEntry? have = ledger.Get(want.Category);
                var remaining = new Dictionary<(int, int, string), int>();
                if (have is not null)
                {
                    foreach (LedgerLocation l in have.Locations)
                    {
                        var key = (l.OldNode, l.NewNode, l.Detail);
                        remaining[key] = remaining.GetValueOrDefault(key) + 1;
                    }
                }

                foreach (LedgerLocation l in want.Locations)
                {
                    var key = (l.OldNode, l.NewNode, l.Detail);
                    if (remaining.GetValueOrDefault(key) <= 0)
                    {
                        Fail("V500", $"the ledger does not list a hidden {LedgerCategoryNames.NameOf(want.Category)} difference ({l.Detail}) at nodes {l.OldNode}/{l.NewNode}");
                    }
                    else
                    {
                        remaining[key]--;
                    }
                }
            }
        }

        private ProjectedGroup Project(bool old, ImmutableArray<int> nodes)
        {
            string key = (old ? "o:" : "n:") + string.Join(',', nodes);
            if (!cache.TryGetValue(key, out ProjectedGroup? g))
            {
                g = ProjectedGroup.Create(old ? al.Old : al.New, nodes, settings);
                cache[key] = g;
            }

            return g;
        }

        private int ProjectedLength(bool old, int node) => Project(old, [node]).Text.Length;

        private static bool SameNodes(ImmutableArray<int> a, ImmutableArray<int> b)
        {
            if (a.IsDefault || b.IsDefault || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private void Fail(string code, string message)
        {
            if (Failures.Count < MaxFailures)
            {
                Failures.Add(new VerificationFailure(code, message));
            }
        }
    }
}
