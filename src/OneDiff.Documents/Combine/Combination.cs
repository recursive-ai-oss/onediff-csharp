using System.Collections.Immutable;
using System.Globalization;
using OneDiff.Documents.Model;
using OneDiff.Documents.Scoring;
using OneDiff.Documents.Verification;

namespace OneDiff.Documents.Combine;

/// <summary>
/// N reviewers' copies of one original, combined: the changes of every reviewer's own <see cref="Comparison"/> (original →
/// copy) in the order of the original, each attributed to the reviewers who made it, identical changes merged, conflicting
/// changes all shown and flagged. No winner is ever picked. Built on the per-reviewer comparisons, which stay available, the
/// result is invertible per reviewer: the changes attributed to reviewer <c>k</c> are exactly the changes of <c>k</c>'s
/// comparison, so projecting the combination onto <c>k</c> reproduces <c>k</c>'s copy. <see cref="Check"/> verifies that and the
/// merge and conflict bookkeeping. Immutable.
/// </summary>
public sealed class Combination
{
    internal Combination(
        CompareDocument original,
        ImmutableArray<Reviewer> reviewers,
        ImmutableArray<Comparison> comparisons,
        ImmutableArray<CombinedChange> changes,
        ImmutableArray<Conflict> conflicts,
        ImmutableArray<Diagnostic> diagnostics,
        CombinationVerification verification)
    {
        Original = original;
        Reviewers = reviewers;
        Comparisons = comparisons;
        Changes = changes;
        Conflicts = conflicts;
        Diagnostics = diagnostics;
        Verification = verification;
    }

    /// <summary>The original every reviewer started from.</summary>
    public CompareDocument Original { get; }

    /// <summary>The reviewers, in the order given.</summary>
    public ImmutableArray<Reviewer> Reviewers { get; }

    /// <summary>Each reviewer's own comparison, original → copy, by reviewer index. Each is verified.</summary>
    public ImmutableArray<Comparison> Comparisons { get; }

    /// <summary>The combined changes in document order of the original; ids are 0..n-1.</summary>
    public ImmutableArray<CombinedChange> Changes { get; }

    /// <summary>The conflicts; every change that conflicts is in exactly one.</summary>
    public ImmutableArray<Conflict> Conflicts { get; }

    /// <summary>What the comparisons and the combination had to say: fallbacks, failures recovered from.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>How the combination's checks went.</summary>
    public CombinationVerification Verification { get; }

    /// <summary>Combines copies of an original, comparing each with the reference stages.</summary>
    /// <param name="original">The document every copy started from.</param>
    /// <param name="copies">The reviewers' copies, at least one; a reviewer's index is its position here and its name is the document's name.</param>
    /// <param name="settings">What to show and hide in each comparison; <see cref="CompareSettings.Legal"/> when null.</param>
    /// <param name="scorer">Scores ambiguous pairs; word overlap when null.</param>
    /// <param name="cancellationToken">Observed between and inside the comparisons.</param>
    /// <exception cref="ArgumentException">There is no copy.</exception>
    /// <exception cref="ComparisonVerificationException">A comparison or the combination failed verification and the settings ask for an exception, or no fallback could repair it.</exception>
    public static Combination Of(CompareDocument original, IReadOnlyList<CompareDocument> copies, CompareSettings? settings = null, IPairScorer? scorer = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(copies);
        if (copies.Count == 0)
        {
            throw new ArgumentException("at least one copy is required", nameof(copies));
        }

        var comparisons = new List<Comparison>(copies.Count);
        foreach (CompareDocument copy in copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            comparisons.Add(Comparison.Of(original, copy, settings, scorer, cancellationToken));
        }

        return From(comparisons, cancellationToken);
    }

    /// <summary>Combines comparisons that were made from one original (the very same <see cref="CompareDocument"/>).</summary>
    /// <param name="comparisons">Original → copy comparisons, at least one; a reviewer's index is its position here and its name is the copy's name.</param>
    /// <param name="cancellationToken">Observed while combining.</param>
    /// <exception cref="ArgumentException">The list is empty or the comparisons do not share one original.</exception>
    /// <exception cref="ComparisonVerificationException">The combination failed its checks even without merging.</exception>
    public static Combination From(IReadOnlyList<Comparison> comparisons, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        if (comparisons.Count == 0)
        {
            throw new ArgumentException("at least one comparison is required", nameof(comparisons));
        }

        CompareDocument original = comparisons[0].Old;
        foreach (Comparison c in comparisons)
        {
            if (!ReferenceEquals(c.Old, original))
            {
                throw new ArgumentException("every comparison must be made from the same original document object", nameof(comparisons));
            }
        }

        var reviewers = new List<Reviewer>();
        for (int i = 0; i < comparisons.Count; i++)
        {
            reviewers.Add(new Reviewer(i, comparisons[i].New.Name, comparisons[i].New.SourceHash));
        }

        ImmutableArray<Comparison> cmps = [.. comparisons];
        ImmutableArray<Reviewer> revs = [.. reviewers];
        var diagnostics = new List<Diagnostic>();
        foreach (Comparison c in comparisons)
        {
            diagnostics.AddRange(c.Diagnostics);
        }

        Combination merged = Combiner.Build(original, revs, cmps, true, diagnostics, new CombinationVerification(true, 0, 0), cancellationToken);
        ImmutableArray<VerificationFailure> failures = merged.Check(cancellationToken);
        if (failures.IsEmpty)
        {
            return merged;
        }

        if (comparisons[0].Settings.ThrowOnFailure)
        {
            throw new ComparisonVerificationException(failures);
        }

        foreach (VerificationFailure f in failures)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "combine." + f.Code, f.Message));
        }

        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "combine.fallback", "the combination failed its checks; identical changes of different reviewers are listed separately"));
        Combination plain = Combiner.Build(original, revs, cmps, false, diagnostics, new CombinationVerification(true, 1, failures.Length), cancellationToken);
        ImmutableArray<VerificationFailure> again = plain.Check(cancellationToken);
        if (again.IsEmpty)
        {
            return plain;
        }

        throw new ComparisonVerificationException(again);
    }

    /// <summary>
    /// Runs the checks again: every reviewer's comparison is complete and made from this original; the changes attributed to a
    /// reviewer are exactly that reviewer's changes, each once (so the projection onto the reviewer is the reviewer's copy);
    /// changes held by several reviewers are identical in place and effect; edit attributions are true; every conflict is a
    /// group of at least two reviewers' changes, and no two changes of different reviewers conflict outside a conflict.
    /// </summary>
    /// <returns>The failures; empty when everything holds.</returns>
    public ImmutableArray<VerificationFailure> Check(CancellationToken cancellationToken = default) => CombinationChecker.Check(this, cancellationToken);

    /// <summary>The changes attributed to one reviewer, in the combination's order: the projection onto that reviewer.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No such reviewer.</exception>
    public IEnumerable<CombinedChange> ChangesOf(int reviewer)
    {
        if (reviewer < 0 || reviewer >= Reviewers.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(reviewer));
        }

        return Changes.Where(c => c.Reviewers.Contains(reviewer));
    }

    /// <summary>Writes the combination's change report page (see <see cref="ChangeReport"/>) or its JSON form, by the extension: <c>.html</c> or <c>.json</c>.</summary>
    /// <param name="path">The output file; replaced if it exists. The directory must exist.</param>
    /// <exception cref="NotSupportedException">The extension is neither <c>.html</c>, <c>.htm</c> nor <c>.json</c>; other forms come from the adapter packages.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public void SaveAs(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, ToBytes(path));
    }

    internal byte[] ToBytes(string path)
    {
        string ext = Path.GetExtension(path);
        if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return CombineJson.Write(this);
        }

        if (string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase))
        {
            return System.Text.Encoding.UTF8.GetBytes(ChangeReport.From(this).ToHtml());
        }

        throw new NotSupportedException($"no combined output form for '{ext}'; .html and .json are built in");
    }
}

internal static class Combiner
{
    private sealed class Entry
    {
        public required Change Rep;
        public required int RepReviewer;
        public required List<Claim> Claims;
        public required string Key;
        public List<ChangeHolder> Holders = [];
        public (int Node, int Rank, int Start) Order;
    }

    public static Combination Build(
        CompareDocument original,
        ImmutableArray<Reviewer> reviewers,
        ImmutableArray<Comparison> cmps,
        bool merge,
        List<Diagnostic> diagnostics,
        CombinationVerification verification,
        CancellationToken ct)
    {
        var entries = new List<Entry>();
        var byKey = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        // Every text claim anywhere, by identity, for the edit-level attribution.
        var editHolders = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        for (int k = 0; k < cmps.Length; k++)
        {
            foreach (Change c in cmps[k].Changes)
            {
                ct.ThrowIfCancellationRequested();
                List<Claim> claims = Claims.Of(c, cmps[k]);
                foreach (Claim cl in claims)
                {
                    if (cl.Kind == ClaimKind.Text)
                    {
                        string id = cl.Identity();
                        if (!editHolders.TryGetValue(id, out SortedSet<int>? set))
                        {
                            editHolders[id] = set = [];
                        }

                        set.Add(k);
                    }
                }

                string key = KeyOf(c, claims);
                Entry? target = null;
                if (merge && byKey.TryGetValue(key, out List<Entry>? same))
                {
                    target = same.FirstOrDefault(e => e.Holders.All(h => h.Reviewer != k));
                }

                if (target is null)
                {
                    target = new Entry { Rep = c, RepReviewer = k, Claims = claims, Key = key, Order = Claims.OrderOf(c, claims) };
                    entries.Add(target);
                    if (!byKey.TryGetValue(key, out List<Entry>? list))
                    {
                        byKey[key] = list = [];
                    }

                    list.Add(target);
                }

                target.Holders.Add(new ChangeHolder(k, c.Id));
            }
        }

        // Document order of the original; ties by first reviewer and the change's own number, so the order is total.
        List<Entry> ordered = [.. entries.OrderBy(e => e.Order.Node).ThenBy(e => e.Order.Rank).ThenBy(e => e.Order.Start).ThenBy(e => e.RepReviewer).ThenBy(e => e.Rep.Id)];
        int[] conflictOf = new int[ordered.Count];
        ImmutableArray<Conflict> conflicts = FindConflicts(ordered.Select(e => (e.Claims, Reviewers: e.Holders.Select(h => h.Reviewer).ToArray())).ToList(), conflictOf);

        var changes = ImmutableArray.CreateBuilder<CombinedChange>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            Entry e = ordered[i];
            var editReviewers = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
            int edit = 0;
            foreach (Claim cl in e.Claims)
            {
                if (cl.Kind == ClaimKind.Text && edit++ < e.Rep.Edits.Length)
                {
                    editReviewers.Add([.. editHolders[cl.Identity()]]);
                }
            }

            changes.Add(new CombinedChange(
                i,
                e.Rep,
                [.. e.Holders.Select(h => h.Reviewer).Distinct().Order()],
                [.. e.Holders.OrderBy(h => h.Reviewer)],
                editReviewers.ToImmutable(),
                conflictOf[i]));
        }

        return new Combination(original, reviewers, cmps, changes.ToImmutable(), conflicts, [.. diagnostics], verification);
    }

    /// <summary>The identity of a change for merging: kind, claims (place and effect) and the new text, never the numbers that differ between comparisons.</summary>
    private static string KeyOf(Change c, List<Claim> claims) =>
        ((int)c.Kind).ToString(CultureInfo.InvariantCulture) + "#" + string.Join("##", claims.Select(x => x.Identity())) + "#" + c.New.Text + "#" + c.Old.Start.ToString(CultureInfo.InvariantCulture) + "+" + c.Old.Length.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Finds the conflicting pairs among entries (claims by node, insertions by place; only pairs of different reviewers) and
    /// groups them into connected components. Fills <paramref name="conflictOf"/> with each entry's conflict id or -1.
    /// </summary>
    internal static ImmutableArray<Conflict> FindConflicts(List<(List<Claim> Claims, int[] Reviewers)> items, int[] conflictOf)
    {
        var buckets = new SortedDictionary<string, List<(int Item, Claim Claim)>>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            foreach (Claim cl in items[i].Claims)
            {
                if (cl.Kind == ClaimKind.Insert)
                {
                    Add("i:" + cl.Key, i, cl);
                }
                else
                {
                    foreach (int n in cl.Nodes)
                    {
                        Add("n:" + n.ToString("D10", CultureInfo.InvariantCulture), i, cl);
                    }
                }
            }
        }

        void Add(string key, int item, Claim cl)
        {
            if (!buckets.TryGetValue(key, out List<(int, Claim)>? l))
            {
                buckets[key] = l = [];
            }

            l.Add((item, cl));
        }

        int[] parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        var edges = new List<(int A, int B, ConflictKind Kind)>();
        foreach (List<(int Item, Claim Claim)> bucket in buckets.Values)
        {
            for (int x = 0; x < bucket.Count; x++)
            {
                for (int y = x + 1; y < bucket.Count; y++)
                {
                    (int ia, Claim a) = bucket[x];
                    (int ib, Claim b) = bucket[y];
                    if (ia == ib || !Different(items[ia].Reviewers, items[ib].Reviewers))
                    {
                        continue;
                    }

                    if (ClaimRules.Test(a, b) is { } kind)
                    {
                        edges.Add((ia, ib, kind));
                        int ra = Find(ia), rb = Find(ib);
                        if (ra != rb)
                        {
                            parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
                        }
                    }
                }
            }
        }

        var kinds = new Dictionary<int, ConflictKind>();
        var members = new SortedDictionary<int, SortedSet<int>>();
        foreach ((int a, int b, ConflictKind kind) in edges)
        {
            int r = Find(a);
            kinds[r] = kinds.TryGetValue(r, out ConflictKind old) && old < kind ? old : kind;
            if (!members.TryGetValue(r, out SortedSet<int>? s))
            {
                members[r] = s = [];
            }

            s.Add(a);
            s.Add(b);
        }

        Array.Fill(conflictOf, -1);
        var list = ImmutableArray.CreateBuilder<Conflict>();
        // Roots are the smallest member (smallest item index = earliest in document order), so ids follow document order.
        foreach ((int root, SortedSet<int> set) in members)
        {
            int id = list.Count;
            var revs = new SortedSet<int>();
            foreach (int m in set)
            {
                conflictOf[m] = id;
                foreach (int r in items[m].Reviewers)
                {
                    revs.Add(r);
                }
            }

            list.Add(new Conflict(id, kinds[root], [.. set], [.. revs]));
        }

        return list.ToImmutable();
    }

    private static bool Different(int[] a, int[] b)
    {
        // Two entries only conflict when some reviewer of one differs from some reviewer of the other.
        foreach (int x in a)
        {
            foreach (int y in b)
            {
                if (x != y)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

internal static class CombinationChecker
{
    public static ImmutableArray<VerificationFailure> Check(Combination c, CancellationToken ct)
    {
        var f = new List<VerificationFailure>();
        void Fail(string code, string message) => f.Add(new VerificationFailure(code, message));

        // C0xx: the per-reviewer comparisons.
        for (int k = 0; k < c.Comparisons.Length; k++)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(c.Comparisons[k].Old, c.Original))
            {
                Fail("C001", $"reviewer {k}'s comparison was not made from the combination's original");
            }

            foreach (VerificationFailure v in c.Comparisons[k].Check(ct))
            {
                Fail("C002", $"reviewer {k}'s comparison: {v.Code} {v.Message}");
            }
        }

        // C1xx: projection. The changes attributed to each reviewer are exactly that reviewer's changes, once each.
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < c.Changes.Length; i++)
        {
            CombinedChange cc = c.Changes[i];
            if (cc.Id != i)
            {
                Fail("C101", $"combined change at {i} has id {cc.Id}");
            }

            if (cc.Holders.IsDefaultOrEmpty)
            {
                Fail("C102", $"combined change {i} has no holder");
                continue;
            }

            foreach (ChangeHolder h in cc.Holders)
            {
                if (h.Reviewer < 0 || h.Reviewer >= c.Comparisons.Length || h.ChangeId < 0 || h.ChangeId >= c.Comparisons[h.Reviewer].Changes.Length || c.Comparisons[h.Reviewer].Changes[h.ChangeId].Id != h.ChangeId)
                {
                    Fail("C103", $"combined change {i} holds a change that does not exist ({h.Reviewer}, {h.ChangeId})");
                    continue;
                }

                if (!seen.Add((h.Reviewer, h.ChangeId)))
                {
                    Fail("C104", $"reviewer {h.Reviewer}'s change {h.ChangeId} is attributed twice");
                }

                // C105: holders agree on the original's side and on what they make of it.
                Change mine = c.Comparisons[h.Reviewer].Changes[h.ChangeId];
                if (!SameEffect(cc.Change, mine))
                {
                    Fail("C105", $"combined change {i}: reviewer {h.Reviewer}'s change {h.ChangeId} differs from the shown one");
                }
            }

            if (!cc.Reviewers.SequenceEqual(cc.Holders.Select(h => h.Reviewer).Distinct().Order()))
            {
                Fail("C106", $"combined change {i}: reviewers do not match holders");
            }

            // C107: an edit attribution is true for every reviewer it names, and includes every holder.
            if (!cc.EditReviewers.IsDefaultOrEmpty && cc.EditReviewers.Length != cc.Change.Edits.Length)
            {
                Fail("C107", $"combined change {i}: {cc.EditReviewers.Length} edit attributions for {cc.Change.Edits.Length} edits");
            }
            else if (!cc.EditReviewers.IsDefaultOrEmpty)
            {
                for (int e = 0; e < cc.Change.Edits.Length; e++)
                {
                    foreach (int h in cc.Reviewers)
                    {
                        if (!cc.EditReviewers[e].Contains(h))
                        {
                            Fail("C107", $"combined change {i}: holder {h} is missing from edit {e}");
                        }
                    }

                    foreach (int r in cc.EditReviewers[e])
                    {
                        if (r < 0 || r >= c.Comparisons.Length || !HasEdit(c.Comparisons[r], cc.Change, cc.Change.Edits[e]))
                        {
                            Fail("C108", $"combined change {i}: reviewer {r} did not make edit {e}");
                        }
                    }
                }
            }
        }

        for (int k = 0; k < c.Comparisons.Length; k++)
        {
            foreach (Change mine in c.Comparisons[k].Changes)
            {
                if (!seen.Contains((k, mine.Id)))
                {
                    Fail("C109", $"reviewer {k}'s change {mine.Id} is in no combined change");
                }
            }
        }

        // C2xx: conflicts, recomputed independently of the stored ids.
        var items = new List<(List<Claim>, int[])>();
        for (int i = 0; i < c.Changes.Length; i++)
        {
            CombinedChange cc = c.Changes[i];
            ChangeHolder h0 = cc.Holders.IsDefaultOrEmpty ? new ChangeHolder(0, 0) : cc.Holders[0];
            items.Add((h0.Reviewer < c.Comparisons.Length ? Claims.Of(cc.Change, c.Comparisons[cc.Holders.IsDefaultOrEmpty ? 0 : h0.Reviewer]) : [], [.. cc.Reviewers]));
        }

        int[] again = new int[items.Count];
        ImmutableArray<Conflict> expected = Combiner.FindConflicts(items, again);
        if (expected.Length != c.Conflicts.Length)
        {
            Fail("C201", $"{c.Conflicts.Length} conflicts stored, {expected.Length} found");
        }
        else
        {
            for (int i = 0; i < expected.Length; i++)
            {
                Conflict a = expected[i], b = c.Conflicts[i];
                if (a.Id != b.Id || a.Kind != b.Kind || !a.ChangeIds.SequenceEqual(b.ChangeIds) || !a.Reviewers.SequenceEqual(b.Reviewers))
                {
                    Fail("C202", $"conflict {i} differs from the one found");
                }

                if (b.ChangeIds.Length < 2 || b.Reviewers.Length < 2)
                {
                    Fail("C203", $"conflict {i} does not involve two changes of two reviewers");
                }
            }
        }

        for (int i = 0; i < c.Changes.Length && i < again.Length; i++)
        {
            if (c.Changes[i].ConflictId != again[i])
            {
                Fail("C204", $"combined change {i} says conflict {c.Changes[i].ConflictId}, found {again[i]}");
            }
        }

        return [.. f];
    }

    /// <summary>Two changes make the same of the original: same kind, same old side, same edits and new text.</summary>
    private static bool SameEffect(Change a, Change b)
    {
        if (a.Kind != b.Kind || !a.Old.Nodes.AsSpan().SequenceEqual(b.Old.Nodes.AsSpan()) || a.Old.Start != b.Old.Start || a.Old.Length != b.Old.Length || !string.Equals(a.New.Text, b.New.Text, StringComparison.Ordinal) || a.Edits.Length != b.Edits.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Edits.Length; i++)
        {
            RegionEdit x = a.Edits[i], y = b.Edits[i];
            if (x.OldStart != y.OldStart || x.OldLength != y.OldLength || !string.Equals(x.NewText, y.NewText, StringComparison.Ordinal) || Claims.RunsKey(x.NewRuns) != Claims.RunsKey(y.NewRuns))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasEdit(Comparison cmp, Change shown, RegionEdit edit)
    {
        foreach (Change c in cmp.Changes)
        {
            if (!c.Old.Nodes.AsSpan().SequenceEqual(shown.Old.Nodes.AsSpan()))
            {
                continue;
            }

            foreach (RegionEdit e in c.Edits)
            {
                if (e.OldStart == edit.OldStart && e.OldLength == edit.OldLength && string.Equals(e.NewText, edit.NewText, StringComparison.Ordinal) && Claims.RunsKey(e.NewRuns) == Claims.RunsKey(edit.NewRuns))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
