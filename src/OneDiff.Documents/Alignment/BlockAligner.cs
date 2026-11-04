using System.Collections.Immutable;
using OneDiff.Documents.Model;
using OneDiff.Documents.Scoring;

namespace OneDiff.Documents.Pipeline;

/// <summary>
/// The alignment stage: which node of the old document is which node of the new one, with moves, splits and merges.
/// It works in the order of increasing doubt:
/// <list type="number">
/// <item>Exact sequence: the text core over the subtree hashes of all nodes in document order, so equal text, equal tables and
/// equal sections line up in order, and a repeated block lines up with its own repetition rather than crossing.</item>
/// <item>Exact relocation: equal subtrees that are still unmatched are paired wherever they sit (a moved clause, a moved table).</item>
/// <item>Text blocks: candidates are found with a shingle index (near-linear), splits and merges are proposed from containment,
/// the scorer judges the ambiguous pairs, and a global matcher picks the best consistent assignment, weighing similarity
/// first and then whether the pair sits in the same changed stretch, has matched neighbours and a similar position.</item>
/// <item>Containers (tables, rows, cells, sections, notes): paired by how much of their content matched, then by position between
/// matched siblings.</item>
/// </list>
/// Every link is verified later, so the stage can only err in how a change reads, never in whether it is complete. Over the cost
/// cap the text-block step is skipped and the result keeps the exact matches and the container pairing.
/// </summary>
internal static class BlockAligner
{
    // Blocks below this similarity are not candidates at all.
    private const double CandidateFloor = 0.30;

    // Candidates at or above this similarity are accepted on the cheap measure; below it (and above the floor) the scorer decides.
    private const double Unambiguous = 0.95;

    // How many candidates each block keeps from the shingle index.
    private const int TopCandidates = 6;

    // Weights added to similarity when choosing among candidates: same changed stretch, matched neighbour, nearness in the document.
    private const double SameStretchBonus = 0.10;
    private const double NeighbourBonus = 0.05;
    private const double PositionWeight = 0.02;

    // A part of a split or merge must lie this much inside the whole (share of its shingles), and the whole must be this long.
    private const double Containment = 0.60;
    private const int MinPartWords = 3;
    private const int MinWholeWords = 4;
    private const int MaxRun = 8;
    private const double BetterThanSingle = 0.05;

    // Stretches with at most this many old-by-new text blocks are compared in full, so short edits with no shared word pair are found.
    private const int AllPairsInStretch = 100;

    // A container needs this share of matched content to be paired by evidence.
    private const double ContainerFloor = 0.25;

    public static Alignment Run(
        CompareDocument oldDoc,
        CompareDocument newDoc,
        IPairScorer? scorer,
        CompareSettings settings,
        Alignment? seed,
        CancellationToken ct)
    {
        settings.Validate();
        IPairScorer? custom = scorer is null or WordOverlapScorer or RulesPairScorer ? null : scorer;
        return new Session(oldDoc, newDoc, custom, settings, ct).Execute(seed);
    }

    private readonly record struct Candidate(int Old, int New, double Raw);

    private sealed record Proposal(double Score, bool IsSplit, int Whole, ImmutableArray<int> Parts);

    private sealed class Session
    {
        private readonly CompareDocument oldDoc;
        private readonly CompareDocument newDoc;
        private readonly IPairScorer? custom;
        private readonly CompareSettings settings;
        private readonly CancellationToken ct;
        private readonly List<Diagnostic> diagnostics = [];
        private readonly List<AlignmentLink> links = [];
        private readonly bool[] usedOld;
        private readonly bool[] usedNew;
        private readonly int[] oneOld;
        private readonly int[] oneNew;
        private readonly int[] oldGap;
        private readonly int[] newGap;
        private DocIndex o = null!;
        private DocIndex n = null!;
        private long work;

        public Session(CompareDocument oldDoc, CompareDocument newDoc, IPairScorer? custom, CompareSettings settings, CancellationToken ct)
        {
            this.oldDoc = oldDoc;
            this.newDoc = newDoc;
            this.custom = custom;
            this.settings = settings;
            this.ct = ct;
            usedOld = new bool[oldDoc.Count];
            usedNew = new bool[newDoc.Count];
            oneOld = new int[oldDoc.Count];
            oneNew = new int[newDoc.Count];
            oldGap = new int[oldDoc.Count];
            newGap = new int[newDoc.Count];
            Array.Fill(oneOld, -1);
            Array.Fill(oneNew, -1);
            Array.Fill(oldGap, -1);
            Array.Fill(newGap, -1);
        }

        public Alignment Execute(Alignment? seed)
        {
            if (seed is not null)
            {
                if (!ReferenceEquals(seed.Old, oldDoc) || !ReferenceEquals(seed.New, newDoc))
                {
                    throw new ArgumentException("the seed alignment was made for other documents", nameof(seed));
                }

                foreach (AlignmentLink l in seed.Links)
                {
                    if (l.Pinned)
                    {
                        Register(l);
                    }
                }
            }

            var interners = new Interners();
            o = DocIndex.Build(oldDoc, settings, interners, ct);
            n = DocIndex.Build(newDoc, settings, interners, ct);
            ExactSequence();
            if (settings.DetectMoves)
            {
                ExactRelocations();
            }

            TextBlocks();
            PairContainersByEvidence();
            PairContainersByPosition();
            return Alignment.Create(oldDoc, newDoc, settings, links, diagnostics).Normalize();
        }

        // ---- links -------------------------------------------------------------------------------------------------

        private void AddLink(LinkKind kind, ImmutableArray<int> oldNodes, ImmutableArray<int> newNodes, double score, LinkOrigin origin) =>
            Register(new AlignmentLink(0, kind, oldNodes, newNodes, false, score, origin));

        private void Register(AlignmentLink l)
        {
            links.Add(l);
            foreach (int x in l.OldNodes)
            {
                usedOld[x] = true;
            }

            foreach (int x in l.NewNodes)
            {
                usedNew[x] = true;
            }

            if (l.Kind is LinkKind.Match or LinkKind.Move)
            {
                oneOld[l.OldNodes[0]] = l.NewNodes[0];
                oneNew[l.NewNodes[0]] = l.OldNodes[0];
            }
        }

        private void Note(DiagnosticSeverity severity, string code, string message) => diagnostics.Add(new Diagnostic(severity, code, message));

        // ---- 1. exact sequence -------------------------------------------------------------------------------------

        // The text core over subtree hashes of every node in document order. A stretch between equal runs is a "gap";
        // old and new blocks in the same gap are the natural partners for each other.
        private void ExactSequence()
        {
            List<int> oldIds = Free(usedOld), newIds = Free(usedNew);
            var oldKeys = new int[oldIds.Count];
            var newKeys = new int[newIds.Count];
            for (int i = 0; i < oldKeys.Length; i++)
            {
                oldKeys[i] = o.Placed[oldIds[i]];
            }

            for (int i = 0; i < newKeys.Length; i++)
            {
                newKeys[i] = n.Placed[newIds[i]];
            }

            DiffAlgorithm algorithm = settings.Algorithm;
            if (algorithm == DiffAlgorithm.Myers && (long)oldKeys.Length * newKeys.Length > settings.CostCap)
            {
                algorithm = DiffAlgorithm.Histogram;
                Note(DiagnosticSeverity.Info, "align.large", "the documents are large; blocks were matched in order with the histogram algorithm instead of Myers");
            }

            EditScript script = Diff.Sequences<int>(oldKeys, newKeys, algorithm, null, ct);
            work += oldKeys.Length + newKeys.Length;
            int gap = 0;
            for (int e = 0; e < script.Count; e++)
            {
                Edit edit = script[e];
                if (edit.Kind == EditKind.Equal)
                {
                    for (int k = 0; k < edit.OldLength; k++)
                    {
                        AddLink(LinkKind.Match, [oldIds[edit.OldStart + k]], [newIds[edit.NewStart + k]], 1.0, LinkOrigin.Exact);
                    }

                    continue;
                }

                if (edit.Kind == EditKind.Delete)
                {
                    for (int k = 0; k < edit.OldLength; k++)
                    {
                        oldGap[oldIds[edit.OldStart + k]] = gap;
                    }

                    if (e + 1 < script.Count && script[e + 1].Kind == EditKind.Insert)
                    {
                        e++;
                        edit = script[e];
                    }
                    else
                    {
                        gap++;
                        continue;
                    }
                }

                for (int k = 0; k < edit.NewLength; k++)
                {
                    newGap[newIds[edit.NewStart + k]] = gap;
                }

                gap++;
            }
        }

        // ---- 2. exact relocation -----------------------------------------------------------------------------------

        // Unmatched subtrees that are equal on both sides are the same block moved. Largest first, so a moved table is one
        // move and not a row of moved cells; equal blocks pair first-come in document order, so duplicates never cross.
        private void ExactRelocations()
        {
            bool[] freeOld = FreeSubtrees(o, usedOld), freeNew = FreeSubtrees(n, usedNew);
            var newByTree = new Dictionary<int, List<int>>();
            for (int i = 0; i < newDoc.Count; i++)
            {
                if (freeNew[i] && n.Content[i] > 0)
                {
                    if (!newByTree.TryGetValue(n.Tree[i], out List<int>? list))
                    {
                        newByTree[n.Tree[i]] = list = [];
                    }

                    list.Add(i);
                }
            }

            if (newByTree.Count == 0)
            {
                return;
            }

            var olds = new List<int>();
            for (int i = 0; i < oldDoc.Count; i++)
            {
                if (freeOld[i] && o.Content[i] > 0)
                {
                    olds.Add(i);
                }
            }

            olds.Sort((a, b) =>
            {
                int c = o.Size[b].CompareTo(o.Size[a]);
                return c != 0 ? c : a.CompareTo(b);
            });
            var cursor = new Dictionary<int, int>();
            foreach (int a in olds)
            {
                ct.ThrowIfCancellationRequested();
                if (usedOld[a] || !newByTree.TryGetValue(o.Tree[a], out List<int>? candidates))
                {
                    continue;
                }

                int at = cursor.GetValueOrDefault(o.Tree[a]);
                while (at < candidates.Count && usedNew[candidates[at]])
                {
                    at++;
                }

                if (at >= candidates.Count)
                {
                    cursor[o.Tree[a]] = at;
                    continue;
                }

                int b = candidates[at];
                cursor[o.Tree[a]] = at + 1;
                for (int k = 0; k < o.Size[a]; k++)
                {
                    AddLink(LinkKind.Match, [a + k], [b + k], 1.0, k == 0 ? LinkOrigin.Relocated : LinkOrigin.Exact);
                }
            }
        }

        // ---- 3. text blocks ----------------------------------------------------------------------------------------

        private void TextBlocks()
        {
            List<int> oldText = FreeText(o, usedOld), newText = FreeText(n, usedNew);
            if (oldText.Count == 0 || newText.Count == 0)
            {
                return;
            }

            var newIndex = new ShingleIndex(n, newText);
            var oldIndex = new ShingleIndex(o, oldText);
            var votes = new int[Math.Max(oldDoc.Count, newDoc.Count)];
            var touched = new List<int>();
            var found = new List<long>();
            var splitParts = new Dictionary<int, List<int>>();
            var mergeParts = new Dictionary<int, List<int>>();

            // Old blocks looking for new ones: top candidates, and "this old block lies inside that new one" (a merge part).
            foreach (int a in oldText)
            {
                ct.ThrowIfCancellationRequested();
                touched.Clear();
                work += newIndex.Vote(o.Shingles[a], votes, touched) >> 4;
                Harvest(a, o, n, votes, touched, true, found, mergeParts);
            }

            foreach (int b in newText)
            {
                ct.ThrowIfCancellationRequested();
                touched.Clear();
                work += oldIndex.Vote(n.Shingles[b], votes, touched) >> 4;
                Harvest(b, n, o, votes, touched, false, found, splitParts);
            }

            AddStretchPairs(oldText, newText, found);
            if (work > settings.CostCap)
            {
                CapReached("too much work finding similar blocks; edited blocks that were not exactly equal are shown as deletions and insertions");
                return;
            }

            found.Sort();
            var cands = new List<Candidate>(found.Count);
            var bestOld = new double[oldDoc.Count];
            var bestNew = new double[newDoc.Count];
            long previous = -1;
            foreach (long key in found)
            {
                if (key == previous)
                {
                    continue;
                }

                previous = key;
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
                double raw = TextSimilarity.Of(o.Words[a], o.Text[a], n.Words[b], n.Text[b]);
                work++;
                if (raw >= CandidateFloor)
                {
                    cands.Add(new Candidate(a, b, raw));
                    bestOld[a] = Math.Max(bestOld[a], raw);
                    bestNew[b] = Math.Max(bestNew[b], raw);
                }
            }

            if (work > settings.CostCap)
            {
                CapReached("too much work comparing similar blocks; edited blocks that were not exactly equal are shown as deletions and insertions");
                return;
            }

            if (settings.DetectSplitsAndMerges)
            {
                var proposals = new List<Proposal>();
                Propose(o, n, splitParts, bestOld, true, proposals);
                Propose(n, o, mergeParts, bestNew, false, proposals);
                if (work > settings.CostCap)
                {
                    CapReached("too much work looking for splits and merges; the rest were skipped");
                    proposals.Clear();
                }

                proposals.Sort(static (x, y) =>
                {
                    int c = y.Score.CompareTo(x.Score);
                    if (c != 0)
                    {
                        return c;
                    }

                    c = y.IsSplit.CompareTo(x.IsSplit);
                    return c != 0 ? c : x.Whole.CompareTo(y.Whole);
                });
                foreach (Proposal p in proposals)
                {
                    if (p.IsSplit && !usedOld[p.Whole] && p.Parts.All(x => !usedNew[x]))
                    {
                        AddLink(LinkKind.Split, [p.Whole], p.Parts, p.Score, LinkOrigin.SplitMerge);
                    }
                    else if (!p.IsSplit && !usedNew[p.Whole] && p.Parts.All(x => !usedOld[x]))
                    {
                        AddLink(LinkKind.Merge, p.Parts, [p.Whole], p.Score, LinkOrigin.SplitMerge);
                    }
                }
            }

            ChoosePairs(cands);
        }

        // One block's votes become candidates (best few) and containment records ("this block lies inside that one").
        private static void Harvest(
            int source,
            DocIndex sourceIx,
            DocIndex targetIx,
            int[] votes,
            List<int> touched,
            bool sourceIsOld,
            List<long> found,
            Dictionary<int, List<int>> insideOf)
        {
            int shingles = sourceIx.Shingles[source].Length;
            NodeKind kind = sourceIx.Doc[source].Kind;
            double rel = (double)source / sourceIx.Doc.Count;
            var ranked = new List<(int Node, int Votes, double Distance)>(touched.Count);
            foreach (int t in touched)
            {
                int v = votes[t];
                votes[t] = 0;
                if (targetIx.Doc[t].Kind != kind)
                {
                    continue;
                }

                ranked.Add((t, v, Math.Abs(rel - (double)t / targetIx.Doc.Count)));
                if (v >= 2 && v >= Containment * shingles && sourceIx.Words[source].Length >= MinPartWords && targetIx.Words[t].Length >= MinWholeWords)
                {
                    if (!insideOf.TryGetValue(t, out List<int>? list))
                    {
                        insideOf[t] = list = [];
                    }

                    list.Add(source);
                }
            }

            ranked.Sort(static (x, y) =>
            {
                int c = y.Votes.CompareTo(x.Votes);
                if (c != 0)
                {
                    return c;
                }

                c = x.Distance.CompareTo(y.Distance);
                return c != 0 ? c : x.Node.CompareTo(y.Node);
            });
            for (int k = 0; k < ranked.Count && k < TopCandidates; k++)
            {
                found.Add(sourceIsOld ? Key(source, ranked[k].Node) : Key(ranked[k].Node, source));
            }
        }

        // Short stretches are compared in full: two short blocks that differ in one word share no word pair to find them by.
        private void AddStretchPairs(List<int> oldText, List<int> newText, List<long> found)
        {
            var oldBy = new SortedDictionary<int, List<int>>();
            var newBy = new SortedDictionary<int, List<int>>();
            foreach (int a in oldText)
            {
                if (oldGap[a] >= 0)
                {
                    GetList(oldBy, oldGap[a]).Add(a);
                }
            }

            foreach (int b in newText)
            {
                if (newGap[b] >= 0)
                {
                    GetList(newBy, newGap[b]).Add(b);
                }
            }

            foreach (KeyValuePair<int, List<int>> g in oldBy)
            {
                if (!newBy.TryGetValue(g.Key, out List<int>? partners) || (long)g.Value.Count * partners.Count > AllPairsInStretch)
                {
                    continue;
                }

                foreach (int a in g.Value)
                {
                    foreach (int b in partners)
                    {
                        if (oldDoc[a].Kind == newDoc[b].Kind)
                        {
                            found.Add(Key(a, b));
                        }
                    }
                }
            }
        }

        // A split or merge: a whole block against a run of neighbouring parts that together say what it says, clearly better
        // than any single part does.
        private void Propose(DocIndex wholeIx, DocIndex partIx, Dictionary<int, List<int>> partsOf, double[] bestSingle, bool wholeIsOld, List<Proposal> into)
        {
            var wholes = new List<int>(partsOf.Keys);
            wholes.Sort();
            var union = new List<int>();
            var merged = new List<int>();
            foreach (int whole in wholes)
            {
                ct.ThrowIfCancellationRequested();
                if ((wholeIsOld ? usedOld[whole] : usedNew[whole]) || partsOf[whole].Count < 2)
                {
                    continue;
                }

                List<int> parts = partsOf[whole];
                int[] wholeWords = wholeIx.Words[whole];
                double bestScore = 0;
                int bestFrom = -1, bestTo = -1;
                int i = 0;
                while (i < parts.Count)
                {
                    int j = i;
                    while (j + 1 < parts.Count && partIx.Next[parts[j]] == parts[j + 1])
                    {
                        j++;
                    }

                    for (int s = i; s < j; s++)
                    {
                        union.Clear();
                        union.AddRange(partIx.Words[parts[s]]);
                        for (int e = s + 1; e <= j && e - s < MaxRun; e++)
                        {
                            MergeSorted(union, partIx.Words[parts[e]], merged);
                            work++;
                            double d = TextSimilarity.Dice(wholeWords, union);
                            if (d >= settings.SplitMergeThreshold && d > bestSingle[whole] + BetterThanSingle && d > bestScore)
                            {
                                bestScore = d;
                                bestFrom = s;
                                bestTo = e;
                            }
                        }
                    }

                    i = j + 1;
                }

                if (bestFrom >= 0)
                {
                    into.Add(new Proposal(bestScore, wholeIsOld, whole, [.. parts.GetRange(bestFrom, bestTo - bestFrom + 1)]));
                }
            }
        }

        private static void MergeSorted(List<int> union, int[] add, List<int> scratch)
        {
            scratch.Clear();
            int i = 0, j = 0;
            while (i < union.Count && j < add.Length)
            {
                scratch.Add(union[i] <= add[j] ? union[i++] : add[j++]);
            }

            while (i < union.Count)
            {
                scratch.Add(union[i++]);
            }

            while (j < add.Length)
            {
                scratch.Add(add[j++]);
            }

            union.Clear();
            union.AddRange(scratch);
        }

        // Judge the ambiguous candidates, weigh them, and let the global matcher choose.
        private void ChoosePairs(List<Candidate> cands)
        {
            var live = new List<Candidate>(cands.Count);
            foreach (Candidate c in cands)
            {
                if (!usedOld[c.Old] && !usedNew[c.New])
                {
                    live.Add(c);
                }
            }

            double[] final = Judge(live);
            var pairs = new List<WeightedPair>(live.Count);
            var scoreOf = new Dictionary<long, (double Score, bool Local)>();
            for (int k = 0; k < live.Count; k++)
            {
                Candidate c = live[k];
                bool local = oldGap[c.Old] >= 0 && oldGap[c.Old] == newGap[c.New];
                double s = final[k];
                if (local ? s < settings.MatchThreshold : !settings.DetectMoves || s < settings.MoveThreshold)
                {
                    continue;
                }

                double weight = s
                    + (local ? SameStretchBonus : 0)
                    + (NeighbourLinked(c.Old, c.New) ? NeighbourBonus : 0)
                    + (PositionWeight * (1.0 - Math.Abs(((double)c.Old / oldDoc.Count) - ((double)c.New / newDoc.Count))));
                pairs.Add(new WeightedPair(c.Old, c.New, (long)(weight * 1_000_000) + 1));
                scoreOf[Key(c.Old, c.New)] = (s, local);
            }

            if (pairs.Count == 0)
            {
                return;
            }

            foreach ((int a, int b) in AssignmentSolver.Solve(pairs, oldDoc.Count, newDoc.Count, ref work, ct))
            {
                (double score, bool local) = scoreOf[Key(a, b)];
                AddLink(LinkKind.Match, [a], [b], score, local ? LinkOrigin.Similar : LinkOrigin.Relocated);
            }
        }

        // The scorer is asked only about candidates the cheap measure cannot settle; the rest keep the measure.
        private double[] Judge(List<Candidate> cands)
        {
            var final = new double[cands.Count];
            var ambiguous = new List<int>();
            for (int k = 0; k < cands.Count; k++)
            {
                final[k] = cands[k].Raw;
                if (cands[k].Raw < Unambiguous)
                {
                    ambiguous.Add(k);
                }
            }

            if (custom is null)
            {
                foreach (int k in ambiguous)
                {
                    final[k] = Math.Clamp(cands[k].Raw + RulesPairScorer.Adjustment(oldDoc[cands[k].Old], newDoc[cands[k].New]), 0.0, 1.0);
                }

                return final;
            }

            const int Batch = 1024;
            var pairs = new NodePair[Batch];
            var scores = new double[Batch];
            for (int from = 0; from < ambiguous.Count; from += Batch)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(Batch, ambiguous.Count - from);
                for (int k = 0; k < count; k++)
                {
                    Candidate c = cands[ambiguous[from + k]];
                    pairs[k] = new NodePair(oldDoc[c.Old], newDoc[c.New]);
                }

                custom.ScoreBatch(pairs.AsSpan(0, count), scores);
                for (int k = 0; k < count; k++)
                {
                    double s = scores[k];
                    final[ambiguous[from + k]] = double.IsNaN(s) ? 0 : Math.Clamp(s, 0.0, 1.0);
                }
            }

            return final;
        }

        private bool NeighbourLinked(int a, int b)
        {
            int pa = o.Prev[a], pb = n.Prev[b], na = o.Next[a], nb = n.Next[b];
            return (pa >= 0 && pb >= 0 && oneOld[pa] == pb) || (na >= 0 && nb >= 0 && oneOld[na] == nb);
        }

        // ---- 4. containers -----------------------------------------------------------------------------------------

        // A container (table, row, cell, section, note) is the same as another when the content inside matched: count, for each
        // pair of ancestors of matched blocks, how many matched pairs they share.
        private void PairContainersByEvidence()
        {
            var votes = new Dictionary<long, int>();
            foreach (AlignmentLink l in links)
            {
                foreach (int a in l.OldNodes)
                {
                    if (!o.IsContent(a))
                    {
                        continue;
                    }

                    foreach (int b in l.NewNodes)
                    {
                        if (!n.IsContent(b))
                        {
                            continue;
                        }

                        int pa = oldDoc[a].Parent, pb = newDoc[b].Parent;
                        while (pa >= 0 && pb >= 0 && oldDoc[pa].Kind == newDoc[pb].Kind)
                        {
                            if (!usedOld[pa] && !usedNew[pb] && o.IsContainer[pa] && n.IsContainer[pb])
                            {
                                long k = Key(pa, pb);
                                votes[k] = votes.GetValueOrDefault(k) + 1;
                            }

                            pa = oldDoc[pa].Parent;
                            pb = newDoc[pb].Parent;
                        }
                    }
                }
            }

            if (votes.Count == 0)
            {
                return;
            }

            var keys = new List<long>(votes.Keys);
            keys.Sort();
            var pairs = new List<WeightedPair>(keys.Count);
            var diceOf = new Dictionary<long, double>();
            foreach (long key in keys)
            {
                ct.ThrowIfCancellationRequested();
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
                double dice = Math.Min(1.0, 2.0 * votes[key] / (o.Content[a] + n.Content[b]));
                if (dice < ContainerFloor)
                {
                    continue;
                }

                int pa = oldDoc[a].Parent, pb = newDoc[b].Parent;
                bool parentsPaired = (pa < 0 && pb < 0) || (pa >= 0 && pb >= 0 && oneOld[pa] == pb);
                double weight = dice
                    + (parentsPaired ? NeighbourBonus : 0)
                    + (PositionWeight * (1.0 - Math.Abs(((double)a / oldDoc.Count) - ((double)b / newDoc.Count))));
                pairs.Add(new WeightedPair(a, b, (long)(weight * 1_000_000) + 1));
                diceOf[key] = dice;
            }

            foreach ((int a, int b) in AssignmentSolver.Solve(pairs, oldDoc.Count, newDoc.Count, ref work, ct))
            {
                AddLink(LinkKind.Match, [a], [b], diceOf[Key(a, b)], LinkOrigin.Similar);
            }
        }

        // Containers with no matched content to go by (empty cells, rows whose text was all rewritten) are paired by position:
        // between two matched siblings, as many unmatched containers of a kind on one side as on the other pair in order.
        private void PairContainersByPosition()
        {
            PairChildren(-1, -1);
            for (int i = 0; i < links.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                AlignmentLink l = links[i];
                if (l.Kind is LinkKind.Match or LinkKind.Move && (o.IsContainer[l.OldNodes[0]] || n.IsContainer[l.NewNodes[0]]))
                {
                    PairChildren(l.OldNodes[0], l.NewNodes[0]);
                }
            }
        }

        private void PairChildren(int parentOld, int parentNew)
        {
            ImmutableArray<int> a = parentOld < 0 ? oldDoc.Roots : oldDoc.ChildrenOf(parentOld);
            ImmutableArray<int> b = parentNew < 0 ? newDoc.Roots : newDoc.ChildrenOf(parentNew);
            if (a.Length == 0 || b.Length == 0)
            {
                return;
            }

            var anchorOld = new List<int>();
            var anchorNew = new List<int>();
            for (int ia = 0; ia < a.Length; ia++)
            {
                int partner = oneOld[a[ia]];
                if (partner >= 0 && newDoc[partner].Parent == parentNew)
                {
                    anchorOld.Add(ia);
                    anchorNew.Add(n.Pos[partner]);
                }
            }

            bool[] keep = LongestIncreasing(anchorNew);
            int fromOld = 0, fromNew = 0;
            for (int k = 0; k <= anchorOld.Count; k++)
            {
                if (k < anchorOld.Count && !keep[k])
                {
                    continue;
                }

                int toOld = k < anchorOld.Count ? anchorOld[k] : a.Length;
                int toNew = k < anchorOld.Count ? anchorNew[k] : b.Length;
                PairSegment(a, fromOld, toOld, b, fromNew, toNew);
                fromOld = toOld + 1;
                fromNew = toNew + 1;
            }
        }

        private void PairSegment(ImmutableArray<int> a, int fromA, int toA, ImmutableArray<int> b, int fromB, int toB)
        {
            var oldFree = new List<int>();
            var newFree = new List<int>();
            for (int i = fromA; i < toA; i++)
            {
                if (!usedOld[a[i]] && o.IsContainer[a[i]])
                {
                    oldFree.Add(a[i]);
                }
            }

            for (int i = fromB; i < toB; i++)
            {
                if (!usedNew[b[i]] && n.IsContainer[b[i]])
                {
                    newFree.Add(b[i]);
                }
            }

            if (oldFree.Count == 0 || newFree.Count == 0)
            {
                return;
            }

            for (NodeKind kind = NodeKind.Section; kind <= NodeKind.Property; kind++)
            {
                List<int> ok = oldFree.FindAll(x => oldDoc[x].Kind == kind), nk = newFree.FindAll(x => newDoc[x].Kind == kind);
                if (ok.Count == 0 || ok.Count != nk.Count)
                {
                    continue;
                }

                for (int i = 0; i < ok.Count; i++)
                {
                    AddLink(LinkKind.Match, [ok[i]], [nk[i]], 0.5, LinkOrigin.Similar);
                }
            }
        }

        // ---- helpers -----------------------------------------------------------------------------------------------

        private void CapReached(string message) => Note(DiagnosticSeverity.Warning, "align.cap", message);

        private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

        private static List<int> GetList(SortedDictionary<int, List<int>> map, int key)
        {
            if (!map.TryGetValue(key, out List<int>? list))
            {
                map[key] = list = [];
            }

            return list;
        }

        private static List<int> Free(bool[] used)
        {
            var list = new List<int>(used.Length);
            for (int i = 0; i < used.Length; i++)
            {
                if (!used[i])
                {
                    list.Add(i);
                }
            }

            return list;
        }

        private static List<int> FreeText(DocIndex ix, bool[] used)
        {
            var list = new List<int>();
            for (int i = 0; i < used.Length; i++)
            {
                if (!used[i] && ix.Words[i].Length > 0)
                {
                    list.Add(i);
                }
            }

            return list;
        }

        // Whether a node and everything under it is unmatched. Children come after parents, so one downward pass settles it.
        private static bool[] FreeSubtrees(DocIndex ix, bool[] used)
        {
            var free = new bool[used.Length];
            for (int i = used.Length - 1; i >= 0; i--)
            {
                bool all = !used[i];
                if (all)
                {
                    foreach (int c in ix.Doc.ChildrenOf(i))
                    {
                        if (!free[c])
                        {
                            all = false;
                            break;
                        }
                    }
                }

                free[i] = all;
            }

            return free;
        }

        // Marks a longest strictly increasing run of values (patience sorting with back pointers); deterministic.
        private static bool[] LongestIncreasing(List<int> values)
        {
            var keep = new bool[values.Count];
            var tails = new List<int>();
            var previous = new int[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                int lo = 0, hi = tails.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >>> 1;
                    if (values[tails[mid]] < values[i])
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                previous[i] = lo > 0 ? tails[lo - 1] : -1;
                if (lo == tails.Count)
                {
                    tails.Add(i);
                }
                else
                {
                    tails[lo] = i;
                }
            }

            for (int k = tails.Count > 0 ? tails[^1] : -1; k >= 0; k = previous[k])
            {
                keep[k] = true;
            }

            return keep;
        }
    }
}
