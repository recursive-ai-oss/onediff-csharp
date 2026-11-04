using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using OneDiff.Documents.Model;
using OneDiff.Documents.Scoring;

namespace OneDiff.Documents.Pipeline;

/// <summary>
/// The reference alignment: exact matches by the text core over node keys, then pairing inside the gaps between them (splits and
/// merges first, then the best order-preserving pairing of similar blocks), then moves found by comparing what is left over the
/// whole document. Simple and correct, not clever: it is positional for containers and uses word overlap for text.
/// </summary>
internal static class ReferenceAligner
{
    private const int MaxRun = 6;

    public static Alignment Run(
        CompareDocument oldDoc,
        CompareDocument newDoc,
        IPairScorer? scorer,
        CompareSettings settings,
        Alignment? seed,
        CancellationToken ct)
    {
        settings.Validate();
        var diagnostics = new List<Diagnostic>();
        var links = new List<AlignmentLink>();
        var usedOld = new bool[oldDoc.Count];
        var usedNew = new bool[newDoc.Count];

        if (seed is not null)
        {
            if (!ReferenceEquals(seed.Old, oldDoc) || !ReferenceEquals(seed.New, newDoc))
            {
                throw new ArgumentException("the seed alignment was made for other documents", nameof(seed));
            }

            foreach (AlignmentLink l in seed.Links)
            {
                if (!l.Pinned)
                {
                    continue;
                }

                links.Add(l);
                foreach (int o in l.OldNodes)
                {
                    usedOld[o] = true;
                }

                foreach (int n in l.NewNodes)
                {
                    usedNew[n] = true;
                }
            }
        }

        var ctx = new Context(oldDoc, newDoc, scorer, settings, diagnostics, links, usedOld, usedNew, ct);
        ctx.MatchExactSequence();
        ctx.MatchMoves();
        return Alignment.Create(oldDoc, newDoc, settings, links, diagnostics).Normalize();
    }

    private sealed class Context
    {
        private readonly CompareDocument oldDoc;
        private readonly CompareDocument newDoc;
        private readonly IPairScorer? scorer;
        private readonly CompareSettings settings;
        private readonly List<Diagnostic> diagnostics;
        private readonly List<AlignmentLink> links;
        private readonly bool[] usedOld;
        private readonly bool[] usedNew;
        private readonly CancellationToken ct;
        private readonly WordBag?[] oldBags;
        private readonly WordBag?[] newBags;
        private long splitWork;
        private bool capReported;

        public Context(
            CompareDocument oldDoc,
            CompareDocument newDoc,
            IPairScorer? scorer,
            CompareSettings settings,
            List<Diagnostic> diagnostics,
            List<AlignmentLink> links,
            bool[] usedOld,
            bool[] usedNew,
            CancellationToken ct)
        {
            this.oldDoc = oldDoc;
            this.newDoc = newDoc;
            this.scorer = scorer is WordOverlapScorer ? null : scorer;
            this.settings = settings;
            this.diagnostics = diagnostics;
            this.links = links;
            this.usedOld = usedOld;
            this.usedNew = usedNew;
            this.ct = ct;
            oldBags = new WordBag?[oldDoc.Count];
            newBags = new WordBag?[newDoc.Count];
        }

        /// <summary>Exact matches: the text core over node keys, then the gaps between matches.</summary>
        public void MatchExactSequence()
        {
            List<int> oldIds = Free(usedOld), newIds = Free(usedNew);
            string[] oldKeys = oldIds.Select(i => Key(oldDoc, oldDoc[i])).ToArray();
            string[] newKeys = newIds.Select(i => Key(newDoc, newDoc[i])).ToArray();
            DiffAlgorithm algorithm = settings.Algorithm;
            if ((long)oldKeys.Length * newKeys.Length > settings.CostCap && algorithm == DiffAlgorithm.Myers)
            {
                algorithm = DiffAlgorithm.Histogram;
                Report("align.cap", "the documents are large; blocks were matched with the histogram algorithm instead of Myers");
            }

            EditScript script = Diff.Sequences<string>(oldKeys, newKeys, algorithm, StringComparer.Ordinal, ct);
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

                // A gap is a deletion followed by an insertion, or either alone.
                int oStart = edit.OldStart, oLen = edit.OldLength, nStart = edit.NewStart, nLen = edit.NewLength;
                if (edit.Kind == EditKind.Delete && e + 1 < script.Count && script[e + 1].Kind == EditKind.Insert)
                {
                    e++;
                    nStart = script[e].NewStart;
                    nLen = script[e].NewLength;
                }

                if (oLen > 0 && nLen > 0)
                {
                    PairGap(oldIds.GetRange(oStart, oLen), newIds.GetRange(nStart, nLen));
                }
            }
        }

        /// <summary>Pairs what is left across the whole document as moves: equal text first, then similar text.</summary>
        public void MatchMoves()
        {
            if (!settings.DetectMoves)
            {
                return;
            }

            List<int> oldLeft = Free(usedOld).Where(i => Movable(oldDoc[i])).ToList();
            List<int> newLeft = Free(usedNew).Where(i => Movable(newDoc[i])).ToList();
            if (oldLeft.Count == 0 || newLeft.Count == 0)
            {
                return;
            }

            // Equal text, same kind: first come, first served in document order.
            var byKey = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            foreach (int o in oldLeft)
            {
                string k = MoveKey(oldDoc, oldDoc[o]);
                if (!byKey.TryGetValue(k, out Queue<int>? q))
                {
                    byKey[k] = q = new Queue<int>();
                }

                q.Enqueue(o);
            }

            foreach (int n in newLeft)
            {
                ct.ThrowIfCancellationRequested();
                if (byKey.TryGetValue(MoveKey(newDoc, newDoc[n]), out Queue<int>? q) && q.Count > 0)
                {
                    AddLink(LinkKind.Match, [q.Dequeue()], [n], 1.0, LinkOrigin.Relocated);
                }
            }

            oldLeft = oldLeft.Where(i => !usedOld[i]).ToList();
            newLeft = newLeft.Where(i => !usedNew[i]).ToList();
            if (oldLeft.Count == 0 || newLeft.Count == 0)
            {
                return;
            }

            if ((long)oldLeft.Count * newLeft.Count > settings.CostCap / 16)
            {
                Report("align.cap", "too many unmatched blocks to look for edited moves; they are shown as deletions and insertions");
                return;
            }

            // Similar text: best pairs first, ties by position.
            var candidates = new List<(double Score, int Old, int New)>();
            foreach (int n in newLeft)
            {
                ct.ThrowIfCancellationRequested();
                foreach (int o in oldLeft)
                {
                    if (oldDoc[o].Kind != newDoc[n].Kind)
                    {
                        continue;
                    }

                    double s = Similarity(o, n);
                    if (s >= settings.MoveThreshold)
                    {
                        candidates.Add((s, o, n));
                    }
                }
            }

            candidates.Sort(static (a, b) =>
            {
                int c = b.Score.CompareTo(a.Score);
                if (c != 0)
                {
                    return c;
                }

                c = a.New.CompareTo(b.New);
                return c != 0 ? c : a.Old.CompareTo(b.Old);
            });
            foreach ((double score, int o, int n) in candidates)
            {
                if (!usedOld[o] && !usedNew[n])
                {
                    AddLink(LinkKind.Match, [o], [n], score, LinkOrigin.Relocated);
                }
            }
        }

        private void PairGap(List<int> oldGap, List<int> newGap)
        {
            ct.ThrowIfCancellationRequested();
            if (settings.DetectSplitsAndMerges)
            {
                FindSplits(oldGap, newGap);
                FindMerges(oldGap, newGap);
            }

            List<int> o = oldGap.Where(i => !usedOld[i]).ToList();
            List<int> n = newGap.Where(i => !usedNew[i]).ToList();
            if (o.Count == 0 || n.Count == 0)
            {
                return;
            }

            long cells = (long)(o.Count + 1) * (n.Count + 1);
            if (cells > settings.CostCap)
            {
                Report("align.cap", $"a stretch of {o.Count} old and {n.Count} new unmatched blocks is too large to pair; they are shown as deletions and insertions");
                return;
            }

            // Best order-preserving pairing: maximize the total similarity of pairs of the same kind and depth.
            int cols = n.Count + 1;
            var dp = new int[(o.Count + 1) * cols];
            var weight = new int[o.Count * n.Count];
            var sim = new double[o.Count * n.Count];
            for (int i = 0; i < o.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                for (int j = 0; j < n.Count; j++)
                {
                    Node a = oldDoc[o[i]], b = newDoc[n[j]];
                    if (a.Kind != b.Kind || a.Depth != b.Depth)
                    {
                        continue;
                    }

                    double s = Similarity(o[i], n[j]);
                    if (s >= settings.MatchThreshold)
                    {
                        sim[i * n.Count + j] = s;
                        weight[i * n.Count + j] = (int)(s * 1000) + 1;
                    }
                }
            }

            for (int i = 1; i <= o.Count; i++)
            {
                for (int j = 1; j <= n.Count; j++)
                {
                    int best = Math.Max(dp[(i - 1) * cols + j], dp[i * cols + j - 1]);
                    int w = weight[(i - 1) * n.Count + (j - 1)];
                    if (w > 0)
                    {
                        best = Math.Max(best, dp[(i - 1) * cols + j - 1] + w);
                    }

                    dp[i * cols + j] = best;
                }
            }

            var pairs = new List<(int, int, double)>();
            int x = o.Count, y = n.Count;
            while (x > 0 && y > 0)
            {
                int w = weight[(x - 1) * n.Count + (y - 1)];
                if (w > 0 && dp[x * cols + y] == dp[(x - 1) * cols + y - 1] + w)
                {
                    pairs.Add((o[x - 1], n[y - 1], sim[(x - 1) * n.Count + (y - 1)]));
                    x--;
                    y--;
                }
                else if (dp[x * cols + y] == dp[(x - 1) * cols + y])
                {
                    x--;
                }
                else
                {
                    y--;
                }
            }

            foreach ((int po, int pn, double s) in pairs)
            {
                AddLink(LinkKind.Match, [po], [pn], s, LinkOrigin.Similar);
            }
        }

        private void FindSplits(List<int> oldGap, List<int> newGap)
        {
            foreach (int o in oldGap)
            {
                if (usedOld[o] || !HasWords(oldDoc[o]))
                {
                    continue;
                }

                WordBag ob = BagOf(oldDoc, oldBags, o);
                double bestSingle = 0;
                foreach (int n in newGap)
                {
                    if (!usedNew[n] && newDoc[n].Kind == oldDoc[o].Kind)
                    {
                        bestSingle = Math.Max(bestSingle, ob.Dice(BagOf(newDoc, newBags, n)));
                    }
                }

                if (TryBestRun(ob, newDoc, newBags, newGap, usedNew, oldDoc[o].Kind, bestSingle, out List<int>? run, out double score))
                {
                    AddLink(LinkKind.Split, [o], [.. run], score, LinkOrigin.SplitMerge);
                }
            }
        }

        private void FindMerges(List<int> oldGap, List<int> newGap)
        {
            foreach (int n in newGap)
            {
                if (usedNew[n] || !HasWords(newDoc[n]))
                {
                    continue;
                }

                WordBag nb = BagOf(newDoc, newBags, n);
                double bestSingle = 0;
                foreach (int o in oldGap)
                {
                    if (!usedOld[o] && oldDoc[o].Kind == newDoc[n].Kind)
                    {
                        bestSingle = Math.Max(bestSingle, nb.Dice(BagOf(oldDoc, oldBags, o)));
                    }
                }

                if (TryBestRun(nb, oldDoc, oldBags, oldGap, usedOld, newDoc[n].Kind, bestSingle, out List<int>? run, out double score))
                {
                    AddLink(LinkKind.Merge, [.. run], [n], score, LinkOrigin.SplitMerge);
                }
            }
        }

        /// <summary>
        /// Looks for 2 to <see cref="MaxRun"/> neighbouring free nodes (same kind, same parent, next to each other in the gap)
        /// whose combined words match <paramref name="target"/> well enough and clearly better than any single node does.
        /// </summary>
        private bool TryBestRun(
            WordBag target,
            CompareDocument doc,
            WordBag?[] bags,
            List<int> gap,
            bool[] used,
            NodeKind kind,
            double bestSingle,
            [NotNullWhen(true)] out List<int>? bestRun,
            out double bestScore)
        {
            bestRun = null;
            bestScore = 0;
            for (int j = 0; j < gap.Count; j++)
            {
                if (used[gap[j]] || doc[gap[j]].Kind != kind)
                {
                    continue;
                }

                WordBag union = BagOf(doc, bags, gap[j]).Clone();
                for (int k = 1; k < MaxRun && j + k < gap.Count; k++)
                {
                    int id = gap[j + k];
                    if (used[id] || doc[id].Kind != kind || doc[id].Parent != doc[gap[j]].Parent || id != gap[j + k - 1] + 1 + SubtreeSpan(doc, gap[j + k - 1]))
                    {
                        break;
                    }

                    WordBag nb = BagOf(doc, bags, id);
                    union.Add(nb);
                    splitWork += union.Total + target.Total;
                    if (splitWork > settings.CostCap)
                    {
                        Report("align.cap", "too much work looking for splits and merges; the rest were skipped");
                        return bestRun is not null;
                    }

                    double s = target.Dice(union);
                    if (s >= settings.SplitMergeThreshold && s > bestSingle + 0.05 && s > bestScore)
                    {
                        bestScore = s;
                        bestRun = gap.GetRange(j, k + 1);
                    }
                }
            }

            return bestRun is not null;
        }

        private static int SubtreeSpan(CompareDocument doc, int id)
        {
            int span = 0;
            foreach (int c in doc.ChildrenOf(id))
            {
                span += 1 + SubtreeSpan(doc, c);
            }

            return span;
        }

        private double Similarity(int o, int n)
        {
            if (scorer is not null)
            {
                double s = scorer.Score(oldDoc[o], newDoc[n]);
                return double.IsNaN(s) ? 0 : Math.Clamp(s, 0, 1);
            }

            return BagOf(oldDoc, oldBags, o).Dice(BagOf(newDoc, newBags, n));
        }

        private static WordBag BagOf(CompareDocument doc, WordBag?[] cache, int id) => cache[id] ??= WordBag.Of(doc[id].Text);

        private static bool HasWords(Node node) => node.Text.Length > 0;

        private static bool Movable(Node node) => node.Text.Length > 0 || node.Kind == NodeKind.Image;

        private string Key(CompareDocument doc, Node node) =>
            $"{(int)node.Kind}|{node.Depth}|{ProjectedGroup.TextOf(doc, node, settings)}|{node.Attributes.Get(FormatKeys.ContentHash)}";

        private string MoveKey(CompareDocument doc, Node node) =>
            $"{(int)node.Kind}|{ProjectedGroup.TextOf(doc, node, settings)}|{node.Attributes.Get(FormatKeys.ContentHash)}";

        private static List<int> Free(bool[] used)
        {
            var list = new List<int>();
            for (int i = 0; i < used.Length; i++)
            {
                if (!used[i])
                {
                    list.Add(i);
                }
            }

            return list;
        }

        private void AddLink(LinkKind kind, IEnumerable<int> oldNodes, IEnumerable<int> newNodes, double score, LinkOrigin origin)
        {
            ImmutableArray<int> o = [.. oldNodes], n = [.. newNodes];
            foreach (int x in o)
            {
                usedOld[x] = true;
            }

            foreach (int x in n)
            {
                usedNew[x] = true;
            }

            links.Add(new AlignmentLink(0, kind, o, n, false, score, origin));
        }

        private void Report(string code, string message)
        {
            if (capReported && code == "align.cap")
            {
                return;
            }

            capReported |= code == "align.cap";
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, code, message));
        }
    }
}
