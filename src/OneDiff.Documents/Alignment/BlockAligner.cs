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

    // How far ahead of every other candidate a far pair must be to count as a move below the move threshold.
    private const double UniqueMargin = 0.15;

    // How many times the exact sequence is run again after banning matches that contradict their container context.
    private const int MaxRepairRounds = 4;

    // A block of at most this many words is easily taken for another one with the same words.
    private const int ShortBlockWords = 5;

    // A run of this many matches in step is taken as settled; the stretches between such runs are searched again for the best
    // reading among equally long ones, if they fit in this many cells (and sides).
    private const int StableRun = 8;
    private const int WindowSide = 1500;
    private const long WindowCells = 1L << 20;

    // The longest run of blocks (in nodes) looked at as a copy inside a longer run of additions or removals.
    private const int MaxCopyRun = 256;

    // How many equal candidates of a relocation are looked at before the first is taken.
    private const int RelocationLook = 64;

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

    // The best and second-best candidate similarity of every block, with their partners.
    private sealed class BestTwo(int count)
    {
        private readonly double[] first = new double[count];
        private readonly double[] second = new double[count];
        private readonly int[] firstPartner = Enumerable.Repeat(-1, count).ToArray();
        private readonly int[] secondPartner = Enumerable.Repeat(-1, count).ToArray();

        public void Add(int block, int other, double s)
        {
            if (s > first[block])
            {
                second[block] = first[block];
                secondPartner[block] = firstPartner[block];
                first[block] = s;
                firstPartner[block] = other;
            }
            else if (s > second[block])
            {
                second[block] = s;
                secondPartner[block] = other;
            }
        }

        /// <summary>The best similarity of a block to any block but <paramref name="other"/>, and that block (-1 if none).</summary>
        public (double Similarity, int Partner) Excluding(int block, int other) =>
            firstPartner[block] == other ? (second[block], secondPartner[block]) : (first[block], firstPartner[block]);

    }

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
        private Dictionary<int, int> copiesOld = null!;
        private Dictionary<int, int> copiesNew = null!;
        private int[] wordsUnderOld = [];
        private int[] wordsUnderNew = [];
        private int[] provOld = [];
        private int[] provNew = [];

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
            copiesOld = CountCopies(o);
            copiesNew = CountCopies(n);
            wordsUnderOld = WordsUnder(o);
            wordsUnderNew = WordsUnder(n);
            ExactSequence();
            BuildContext(oneOld, oneNew);
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
            int unique = 0;
            for (int i = 0; i < oldKeys.Length; i++)
            {
                oldKeys[i] = o.Placed[oldIds[i]];
                unique = Math.Max(unique, oldKeys[i] + 1);
            }

            for (int i = 0; i < newKeys.Length; i++)
            {
                newKeys[i] = n.Placed[newIds[i]];
                unique = Math.Max(unique, newKeys[i] + 1);
            }

            DiffAlgorithm algorithm = settings.Algorithm;
            if (algorithm == DiffAlgorithm.Myers && (long)oldKeys.Length * newKeys.Length > settings.CostCap)
            {
                algorithm = DiffAlgorithm.Histogram;
                Note(DiagnosticSeverity.Info, "align.large", "the documents are large; blocks were matched in order with the histogram algorithm instead of Myers");
            }

            // A sequence match of a repeated block can be one of several equally short readings; the text core picks one without
            // looking at where the block sits. A match whose container context contradicts the rest of the evidence is banned (its
            // subtree gets keys nothing else has) and the sequence is run again, so that the copy in the right row or section wins.
            int firstBanned = unique;
            int[]? oldSizes = SizesOf(o, oldIds), newSizes = SizesOf(n, newIds);
            int[] partner; // new index matched to each old index, or -1
            for (int round = 0; ; round++)
            {
                EditScript script = Diff.Sequences<int>(oldKeys, newKeys, algorithm, null, ct);
                work += oldKeys.Length + newKeys.Length;
                partner = PartnersOf(script, oldKeys.Length);
                work += SettleUncertain(partner, oldKeys, newKeys);
                SlideCopies(partner, oldKeys, newKeys, oldSizes, newSizes);
                if (round == MaxRepairRounds)
                {
                    break;
                }

                int[] mo = [.. oneOld];
                int[] mn = [.. oneNew];
                for (int i = 0; i < partner.Length; i++)
                {
                    if (partner[i] >= 0)
                    {
                        mo[oldIds[i]] = newIds[partner[i]];
                        mn[newIds[partner[i]]] = oldIds[i];
                    }
                }

                BuildContext(mo, mn);
                // Position of each free node in its list (-1 when pinned).
                int[] posOld = PositionsOf(oldIds, oldDoc.Count);
                int[] posNew = PositionsOf(newIds, newDoc.Count);
                bool banned = false;
                for (int i = 0; i < partner.Length; i++)
                {
                    if (partner[i] < 0)
                    {
                        continue;
                    }

                    int a = oldIds[i], b = newIds[partner[i]];
                    if (!IsCopy(a, b) || !Conflicts(a, b, mo, mn))
                    {
                        continue;
                    }

                    for (int k = a; k < a + o.Size[a]; k++)
                    {
                        if (posOld[k] is int at and >= 0 && oldKeys[at] < firstBanned)
                        {
                            oldKeys[at] = unique++;
                            banned = true;
                        }
                    }

                    for (int k = b; k < b + n.Size[b]; k++)
                    {
                        if (posNew[k] is int at and >= 0 && newKeys[at] < firstBanned)
                        {
                            newKeys[at] = unique++;
                            banned = true;
                        }
                    }
                }

                if (!banned)
                {
                    break;
                }
            }

            // Register the matches. Old and new blocks between the same two matches are in one stretch (a "gap").
            var matchedNew = new bool[newIds.Count];
            int seen = 0;
            for (int i = 0; i < partner.Length; i++)
            {
                if (partner[i] < 0)
                {
                    oldGap[oldIds[i]] = seen;
                    continue;
                }

                matchedNew[partner[i]] = true;
                AddLink(LinkKind.Match, [oldIds[i]], [newIds[partner[i]]], 1.0, LinkOrigin.Exact);
                seen++;
            }

            seen = 0;
            for (int j = 0; j < matchedNew.Length; j++)
            {
                if (matchedNew[j])
                {
                    seen++;
                }
                else
                {
                    newGap[newIds[j]] = seen;
                }
            }
        }

        // The subtree size at each index of a list of node ids, or null when the list is not whole subtrees of the document.
        private static int[]? SizesOf(DocIndex ix, List<int> ids)
        {
            if (ids.Count != ix.Doc.Count)
            {
                return null;
            }

            var sizes = new int[ids.Count];
            for (int i = 0; i < sizes.Length; i++)
            {
                sizes[i] = ix.Size[ids[i]];
            }

            return sizes;
        }

        // Between long runs of matches that keep step (which nothing could change), the sequence match may have chosen among equally
        // long readings arbitrarily: of three clauses that differ in a figure, either the first or the last can be the one that
        // matches. Within each such window the readings are searched again for the longest match, and among those the one in
        // which matched blocks sit nearest to the place they had relative to the window's start, so that a block does not jump
        // over its neighbours when it could stay put. Windows too large to search are left as they are. Returns the work done.
        private static long SettleUncertain(int[] partner, int[] oldKeys, int[] newKeys)
        {
            long cells = 0;
            int windowOld = 0, windowNew = 0;
            int at = 0;
            while (true)
            {
                // The next long run of matches in step, from `at`: it starts at runStart and is run long.
                int runStart = at, run = 0;
                while (runStart < partner.Length)
                {
                    if (partner[runStart] < 0)
                    {
                        runStart++;
                        continue;
                    }

                    run = 1;
                    while (runStart + run < partner.Length && partner[runStart + run] == partner[runStart] + run)
                    {
                        run++;
                    }

                    if (run >= StableRun)
                    {
                        break;
                    }

                    runStart += run;
                    run = 0;
                }

                bool atEnd = runStart >= partner.Length;
                int windowOldEnd = atEnd ? partner.Length : runStart;
                int windowNewEnd = atEnd ? newKeys.Length : partner[runStart];
                int rows = windowOldEnd - windowOld, cols = windowNewEnd - windowNew;
                if (rows > 0 && cols > 0 && rows <= WindowSide && cols <= WindowSide && (long)rows * cols <= WindowCells)
                {
                    cells += (long)rows * cols;
                    SearchWindow(partner, oldKeys, newKeys, windowOld, windowOldEnd, windowNew, windowNewEnd);
                }

                if (atEnd)
                {
                    return cells >> 4;
                }

                windowOld = runStart + run;
                windowNew = partner[runStart] + run;
                at = windowOld;
            }
        }

        private static void SearchWindow(int[] partner, int[] oldKeys, int[] newKeys, int os, int oe, int ns, int ne)
        {
            int rows = oe - os, cols = ne - ns;
            int width = cols + 1;
            var count = new int[(rows + 1) * width];
            var cost = new int[(rows + 1) * width];

            // best(i, j): the longest match of old[os + i ..] against new[ns + j ..], then the smallest total drift.
            for (int i = rows - 1; i >= 0; i--)
            {
                for (int j = cols - 1; j >= 0; j--)
                {
                    int cell = (i * width) + j;
                    int bc = count[cell + width], bp = cost[cell + width]; // skip old i
                    if (count[cell + 1] > bc || (count[cell + 1] == bc && cost[cell + 1] < bp))
                    {
                        bc = count[cell + 1];
                        bp = cost[cell + 1]; // skip new j
                    }

                    if (oldKeys[os + i] == newKeys[ns + j])
                    {
                        int mc = count[cell + width + 1] + 1;
                        int mp = cost[cell + width + 1] + Math.Abs(j - i);
                        if (mc > bc || (mc == bc && mp <= bp))
                        {
                            bc = mc;
                            bp = mp;
                        }
                    }

                    count[cell] = bc;
                    cost[cell] = bp;
                }
            }

            for (int i = os; i < oe; i++)
            {
                partner[i] = -1;
            }

            int x = 0, y = 0;
            while (x < rows && y < cols)
            {
                int cell = (x * width) + y;
                if (oldKeys[os + x] == newKeys[ns + y]
                    && count[cell] == count[cell + width + 1] + 1
                    && cost[cell] == cost[cell + width + 1] + Math.Abs(y - x))
                {
                    partner[os + x] = ns + y;
                    x++;
                    y++;
                }
                else if (count[cell] == count[cell + width] && cost[cell] == cost[cell + width])
                {
                    x++;
                }
                else
                {
                    y++;
                }
            }
        }

        private static int[] PositionsOf(List<int> ids, int count)
        {
            var pos = new int[count];
            Array.Fill(pos, -1);
            for (int i = 0; i < ids.Count; i++)
            {
                pos[ids[i]] = i;
            }

            return pos;
        }

        private static int[] PartnersOf(EditScript script, int oldCount)
        {
            var partner = new int[oldCount];
            Array.Fill(partner, -1);
            for (int e = 0; e < script.Count; e++)
            {
                Edit edit = script[e];
                if (edit.Kind == EditKind.Equal)
                {
                    for (int k = 0; k < edit.OldLength; k++)
                    {
                        partner[edit.OldStart + k] = edit.NewStart + k;
                    }
                }
            }

            return partner;
        }

        // When a run of added (or removed) blocks is an exact copy of a neighbouring run, which copy is the addition is a toss-up
        // for the sequence match. The copies are laid out in a chain, and the one left unmatched is the one that leaves every
        // other copy matched nearest to the place it had in the other document (ties go to the later copy: a copy is made after
        // its original). Copies move as whole runs, so no block is ever cut.
        private static void SlideCopies(int[] partner, int[] oldKeys, int[] newKeys, int[]? oldSize, int[]? newSize)
        {
            var oldOf = new int[newKeys.Length];
            Array.Fill(oldOf, -1);
            for (int i = 0; i < partner.Length; i++)
            {
                if (partner[i] >= 0)
                {
                    oldOf[partner[i]] = i;
                }
            }

            SettleCopies(newKeys, newSize, oldOf, partner);
            SettleCopies(oldKeys, oldSize, partner, oldOf);
        }

        // keys: one side's keys; size: the subtree size at each index (null when the indexes are not whole subtrees); mate[i]: the
        // other side's index matched to i, or -1; back: the inverse, updated alongside.
        private static void SettleCopies(int[] keys, int[]? size, int[] mate, int[] back)
        {
            for (int s = 0; s < keys.Length;)
            {
                if (mate[s] >= 0)
                {
                    s++;
                    continue;
                }

                int e = s;
                while (e < keys.Length && mate[e] < 0)
                {
                    e++;
                }

                int done = e;

                // The whole unmatched run may be a copy; failing that, its last or first whole blocks may be (a run of several
                // inserted rows whose last row repeats the row that follows).
                if (!TryChain(keys, mate, back, s, e - s, ref done) && size is not null)
                {
                    for (int len = 1; len < e - s && len <= MaxCopyRun; len++)
                    {
                        if (e + len <= keys.Length && CopyOf(keys, e - len, e, len) && WholeBlocks(size, e - len, e) && TryChain(keys, mate, back, e - len, len, ref done))
                        {
                            break;
                        }
                    }

                    for (int len = 1; len < e - s && len <= MaxCopyRun; len++)
                    {
                        if (s - len >= 0 && CopyOf(keys, s - len, s, len) && WholeBlocks(size, s, s + len) && TryChain(keys, mate, back, s, len, ref done))
                        {
                            break;
                        }
                    }
                }

                s = done;
            }
        }

        // Whether [from, to) is a run of complete sibling subtrees.
        private static bool WholeBlocks(int[] size, int from, int to)
        {
            int p = from;
            while (p < to)
            {
                p += size[p];
            }

            return p == to;
        }

        // The unmatched block [s, s + len) and its exact copies on either side form a chain; the copy left unmatched becomes the
        // one that keeps the others nearest their place on the other side. Returns false when the block has no copy.
        private static bool TryChain(int[] keys, int[] mate, int[] back, int s, int len, ref int done)
        {
            int first = s;
            while (first - len >= 0 && CopyOf(keys, first - len, first, len) && AllMatched(mate, first - len, len))
            {
                first -= len;
            }

            int last = s;
            while (last + len + len <= keys.Length && CopyOf(keys, last, last + len, len) && AllMatched(mate, last + len, len))
            {
                last += len;
            }

            if (first == last)
            {
                return false;
            }

            int copies = ((last - first) / len) + 1;
            int current = (s - first) / len;
            var mates = new int[(copies - 1) * len]; // the other side's indexes of the matched copies, in order
            for (int t = 0, m = 0; t < copies; t++)
            {
                if (t == current)
                {
                    continue;
                }

                for (int k = 0; k < len; k++)
                {
                    mates[(m * len) + k] = mate[first + (t * len) + k];
                }

                m++;
            }

            int best = current;
            long bestCost = long.MaxValue;
            for (int r = 0; r < copies; r++)
            {
                long cost = 0;
                for (int t = 0, m = 0; t < copies; t++)
                {
                    if (t == r)
                    {
                        continue;
                    }

                    for (int k = 0; k < len; k++)
                    {
                        cost += Math.Abs(mates[(m * len) + k] - (first + (t * len) + k));
                    }

                    m++;
                }

                if (cost <= bestCost)
                {
                    bestCost = cost;
                    best = r;
                }
            }

            for (int t = 0, m = 0; t < copies; t++)
            {
                for (int k = 0; k < len; k++)
                {
                    int at = first + (t * len) + k;
                    if (t == best)
                    {
                        mate[at] = -1;
                    }
                    else
                    {
                        mate[at] = mates[(m * len) + k];
                        back[mate[at]] = at;
                    }
                }

                if (t != best)
                {
                    m++;
                }
            }

            done = Math.Max(done, last + len);
            return true;
        }

        private static bool CopyOf(int[] keys, int start, int end, int len)
        {
            for (int k = 0; k < len; k++)
            {
                if (keys[start + k] != keys[end + k])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AllMatched(int[] mate, int at, int len)
        {
            for (int k = 0; k < len; k++)
            {
                if (mate[at + k] < 0)
                {
                    return false;
                }
            }

            return true;
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
            var oldInGap = TextByGap(o, oldGap, usedOld);
            var newInGap = TextByGap(n, newGap, usedNew);
            var cursor = new Dictionary<int, int>();
            foreach (int a in olds)
            {
                ct.ThrowIfCancellationRequested();
                if (usedOld[a] || !newByTree.TryGetValue(o.Tree[a], out List<int>? candidates))
                {
                    continue;
                }

                // Equal copies pair in document order, but a copy whose container context contradicts the evidence is passed over,
                // and a copy whose parent is the partner of this one's parent is preferred.
                int at = cursor.GetValueOrDefault(o.Tree[a]);
                while (at < candidates.Count && usedNew[candidates[at]])
                {
                    at++;
                }

                cursor[o.Tree[a]] = at;
                int pick = -1;
                for (int k = at, seen = 0; k < candidates.Count && seen < RelocationLook; k++)
                {
                    int b = candidates[k];
                    if (usedNew[b])
                    {
                        continue;
                    }

                    seen++;
                    bool copy = IsCopy(a, b);
                    if (copy && Conflicts(a, b, oneOld, oneNew))
                    {
                        continue;
                    }

                    if (pick < 0)
                    {
                        pick = b;
                        if (!copy)
                        {
                            break;
                        }
                    }

                    int pa = oldDoc[a].Parent, pb = newDoc[b].Parent;
                    if (pa >= 0 && pb >= 0 && (oneOld[pa] == pb || provOld[pa] == pb))
                    {
                        pick = b;
                        break;
                    }
                }

                if (pick < 0)
                {
                    continue;
                }

                int b2 = pick;
                if (o.Size[a] == 1 && Contested(a, b2, oldInGap, newInGap))
                {
                    continue;
                }

                for (int k = 0; k < o.Size[a]; k++)
                {
                    AddLink(LinkKind.Match, [a + k], [b2 + k], 1.0, k == 0 ? LinkOrigin.Relocated : LinkOrigin.Exact);
                }
            }
        }

        // Free text blocks of each stretch, for the check of a relocation against local edits.
        private static Dictionary<int, List<int>> TextByGap(DocIndex ix, int[] gapOf, bool[] used)
        {
            var byGap = new Dictionary<int, List<int>>();
            for (int i = 0; i < gapOf.Length; i++)
            {
                if (!used[i] && gapOf[i] >= 0 && ix.Words[i].Length > 0 && ix.Size[i] == 1)
                {
                    if (!byGap.TryGetValue(gapOf[i], out List<int>? list))
                    {
                        byGap[gapOf[i]] = list = [];
                    }

                    list.Add(i);
                }
            }

            return byGap;
        }

        // Equal text far apart is evidence of a move, unless each block also has a close edited partner in the stretch it sits in
        // (clauses that differ only in a figure, each figure shifted by one place: edits, not a clause that jumped). Such pairs are
        // left to the text step, where they compete with those edits.
        private bool Contested(int a, int b, Dictionary<int, List<int>> oldInGap, Dictionary<int, List<int>> newInGap)
        {
            if (oldGap[a] < 0 || newGap[b] < 0 || oldGap[a] == newGap[b])
            {
                return false;
            }

            if (!newInGap.TryGetValue(oldGap[a], out List<int>? near) || near.Count > AllPairsInStretch)
            {
                return false;
            }

            bool aHasPartner = false;
            foreach (int c in near)
            {
                if (c != b && TextSimilarity.Of(o.Words[a], o.Text[a], n.Words[c], n.Text[c]) >= settings.MatchThreshold)
                {
                    aHasPartner = true;
                    break;
                }
            }

            if (!aHasPartner || !oldInGap.TryGetValue(newGap[b], out List<int>? far) || far.Count > AllPairsInStretch)
            {
                return false;
            }

            foreach (int d in far)
            {
                if (d != a && TextSimilarity.Of(o.Words[d], o.Text[d], n.Words[b], n.Text[b]) >= settings.MatchThreshold)
                {
                    return true;
                }
            }

            return false;
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

            AddStretchPairs(oldText, newText, found, splitParts, mergeParts);
            foreach (List<int> list in splitParts.Values.Concat(mergeParts.Values))
            {
                list.Sort();
                int kept = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (i == 0 || list[i] != list[i - 1])
                    {
                        list[kept++] = list[i];
                    }
                }

                list.RemoveRange(kept, list.Count - kept);
            }
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

            var rivalOld = new BestTwo(oldDoc.Count);
            var rivalNew = new BestTwo(newDoc.Count);
            foreach (Candidate c in cands)
            {
                rivalOld.Add(c.Old, c.New, c.Raw);
                rivalNew.Add(c.New, c.Old, c.Raw);
            }

            if (settings.DetectSplitsAndMerges)
            {
                var proposals = new List<Proposal>();
                Propose(o, n, splitParts, bestOld, rivalNew, true, proposals);
                Propose(n, o, mergeParts, bestNew, rivalOld, false, proposals);
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
        private void AddStretchPairs(List<int> oldText, List<int> newText, List<long> found, Dictionary<int, List<int>> splitParts, Dictionary<int, List<int>> mergeParts)
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
                        if (oldDoc[a].Kind != newDoc[b].Kind)
                        {
                            continue;
                        }

                        found.Add(Key(a, b));

                        // Shingles of very common phrases are ignored by the index; inside a short stretch the words say it directly.
                        int common = TextSimilarity.Common(o.Words[a], n.Words[b]);
                        if (common >= Containment * n.Words[b].Length && n.Words[b].Length >= MinPartWords && o.Words[a].Length >= MinWholeWords)
                        {
                            GetList(splitParts, a).Add(b);
                        }

                        if (common >= Containment * o.Words[a].Length && o.Words[a].Length >= MinPartWords && n.Words[b].Length >= MinWholeWords)
                        {
                            GetList(mergeParts, b).Add(a);
                        }
                    }
                }
            }
        }

        // A split or merge: a whole block against a run of neighbouring parts that together say what it says, clearly better
        // than any single part does.
        private void Propose(DocIndex wholeIx, DocIndex partIx, Dictionary<int, List<int>> partsOf, double[] bestSingle, BestTwo partRivals, bool wholeIsOld, List<Proposal> into)
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

                // A block that is better explained by the neighbour of the whole (an edited sentence beside the template it shares
                // words with) is not a part of the whole: two neighbours that were both edited would otherwise be read as a split.
                var parts = new List<int>(partsOf[whole].Count);
                foreach (int part in partsOf[whole])
                {
                    (double rival, int rivalBlock) = partRivals.Excluding(part, whole);
                    int[] partWords = partIx.Words[part];
                    double inWhole = (double)TextSimilarity.Common(partWords, wholeIx.Words[whole]) / partWords.Length;
                    double inRival = rivalBlock >= 0 ? (double)TextSimilarity.Common(partWords, wholeIx.Words[rivalBlock]) / partWords.Length : 0;
                    double share = wholeIsOld
                        ? TextSimilarity.Of(wholeIx.Words[whole], wholeIx.Text[whole], partIx.Words[part], partIx.Text[part])
                        : TextSimilarity.Of(partIx.Words[part], partIx.Text[part], wholeIx.Words[whole], wholeIx.Text[whole]);
                    bool neighbour = rivalBlock >= 0 && (wholeIx.Next[whole] == rivalBlock || wholeIx.Prev[whole] == rivalBlock);
                    if (!neighbour || rival < settings.MatchThreshold || inRival < inWhole || (inRival == inWhole && rival <= share))
                    {
                        parts.Add(part);
                    }
                }

                if (parts.Count < 2)
                {
                    continue;
                }

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
                if (!usedOld[c.Old] && !usedNew[c.New] && !(IsCopy(c.Old, c.New) && Conflicts(c.Old, c.New, oneOld, oneNew)))
                {
                    live.Add(c);
                }
            }

            double[] final = Judge(live);

            // For the unique-partner rule: the best and second-best judged similarity of every block.
            var best1Old = new double[oldDoc.Count];
            var best2Old = new double[oldDoc.Count];
            var best1New = new double[newDoc.Count];
            var best2New = new double[newDoc.Count];
            for (int k = 0; k < live.Count; k++)
            {
                Top2(best1Old, best2Old, live[k].Old, final[k]);
                Top2(best1New, best2New, live[k].New, final[k]);
            }

            var pairs = new List<WeightedPair>(live.Count);
            var scoreOf = new Dictionary<long, (double Score, bool Local)>();
            for (int k = 0; k < live.Count; k++)
            {
                Candidate c = live[k];
                bool local = oldGap[c.Old] >= 0 && oldGap[c.Old] == newGap[c.New];
                double s = final[k];
                if (local ? s < settings.MatchThreshold : !AdmitFar(c, s, best1Old, best2Old, best1New, best2New))
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

        // A pair outside its own stretch is a move. It needs the move threshold, or, failing that, to reach the match threshold
        // and be the clear first choice of both blocks: no other candidate of either comes within the margin. An edited clause
        // that moved has exactly one such partner; two similar clauses have two, and are left alone.
        private bool AdmitFar(Candidate c, double s, double[] best1Old, double[] best2Old, double[] best1New, double[] best2New)
        {
            if (!settings.DetectMoves)
            {
                return false;
            }

            if (s >= settings.MoveThreshold)
            {
                return true;
            }

            return s >= settings.MatchThreshold
                && s >= best1Old[c.Old] && s >= best1New[c.New]
                && s - best2Old[c.Old] >= UniqueMargin
                && s - best2New[c.New] >= UniqueMargin;
        }

        private static void Top2(double[] best1, double[] best2, int i, double s)
        {
            if (s > best1[i])
            {
                best2[i] = best1[i];
                best1[i] = s;
            }
            else if (s > best2[i])
            {
                best2[i] = s;
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

            bool inCell = parentOld >= 0 && parentNew >= 0 && oldDoc[parentOld].Kind == NodeKind.TableCell && newDoc[parentNew].Kind == NodeKind.TableCell;
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
                PairSegment(a, fromOld, toOld, b, fromNew, toNew, inCell);
                fromOld = toOld + 1;
                fromNew = toNew + 1;
            }
        }

        private void PairSegment(ImmutableArray<int> a, int fromA, int toA, ImmutableArray<int> b, int fromB, int toB, bool inCell)
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

            if (inCell)
            {
                PairCellText(a, fromA, toA, b, fromB, toB);
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

        // A cell whose text was rewritten past recognition still holds the same entry: when a paired cell has one unmatched text block
        // of a kind on each side, between the same two matched siblings, the two are the cell's old and new text.
        private void PairCellText(ImmutableArray<int> a, int fromA, int toA, ImmutableArray<int> b, int fromB, int toB)
        {
            for (int i = fromA; i < toA; i++)
            {
                int x = a[i];
                if (usedOld[x] || o.IsContainer[x] || o.Words[x].Length == 0)
                {
                    continue;
                }

                int match = -1, count = 0;
                for (int j = fromB; j < toB; j++)
                {
                    int y = b[j];
                    if (!usedNew[y] && !n.IsContainer[y] && n.Words[y].Length > 0 && newDoc[y].Kind == oldDoc[x].Kind)
                    {
                        match = y;
                        count++;
                    }
                }

                int others = 0;
                for (int k = fromA; k < toA; k++)
                {
                    if (k != i && !usedOld[a[k]] && !o.IsContainer[a[k]] && o.Words[a[k]].Length > 0 && oldDoc[a[k]].Kind == oldDoc[x].Kind)
                    {
                        others++;
                    }
                }

                if (count == 1 && others == 0)
                {
                    AddLink(LinkKind.Match, [x], [match], 0.5, LinkOrigin.Similar);
                }
            }
        }

        // ---- repeated blocks and their context ---------------------------------------------------------------------

        // How many nodes with content have each subtree id; an id met more than once on either side is a repeated block.
        private static Dictionary<int, int> CountCopies(DocIndex ix)
        {
            var seen = new Dictionary<int, int>();
            for (int i = 0; i < ix.Doc.Count; i++)
            {
                if (ix.Content[i] > 0)
                {
                    seen[ix.Tree[i]] = seen.GetValueOrDefault(ix.Tree[i]) + 1;
                }
            }

            var repeated = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> kv in seen)
            {
                if (kv.Value > 1)
                {
                    repeated[kv.Key] = kv.Value;
                }
            }

            return repeated;
        }

        // A block is easily mistaken for another when its subtree id occurs more than once on a side, or when it says so little (a
        // table value, a short label) that equal text elsewhere is more likely chance than a move.
        private bool IsCopy(int a, int b) =>
            o.Content[a] > 0
            && (copiesOld.ContainsKey(o.Tree[a]) || copiesNew.ContainsKey(n.Tree[b]) || wordsUnderOld[a] <= ShortBlockWords || wordsUnderNew[b] <= ShortBlockWords);

        // Words in each node's subtree. Children follow their parents, so one pass downwards adds every child to its parent.
        private static int[] WordsUnder(DocIndex ix)
        {
            var under = new int[ix.Doc.Count];
            for (int i = under.Length - 1; i >= 0; i--)
            {
                under[i] += ix.Words[i].Length;
                int parent = ix.Doc[i].Parent;
                if (parent >= 0)
                {
                    under[parent] += under[i];
                }
            }

            return under;
        }

        // Which unmatched containers belong together according to the blocks that are not repeated: each such matched pair votes
        // for the pairs of its ancestors, and two containers are provisional partners when each is the other's only best vote.
        private void BuildContext(int[] mo, int[] mn)
        {
            provOld = new int[oldDoc.Count];
            provNew = new int[newDoc.Count];
            Array.Fill(provOld, -1);
            Array.Fill(provNew, -1);
            var votes = new Dictionary<long, int>();
            for (int a = 0; a < oldDoc.Count; a++)
            {
                int b = mo[a];
                if (b < 0 || !o.IsContent(a) || copiesOld.ContainsKey(o.Tree[a]) || copiesNew.ContainsKey(n.Tree[b]))
                {
                    continue;
                }

                int pa = oldDoc[a].Parent, pb = newDoc[b].Parent;
                while (pa >= 0 && pb >= 0 && oldDoc[pa].Kind == newDoc[pb].Kind)
                {
                    if (mo[pa] < 0 && mn[pb] < 0 && o.IsContainer[pa] && n.IsContainer[pb])
                    {
                        long k = Key(pa, pb);
                        votes[k] = votes.GetValueOrDefault(k) + 1;
                    }

                    pa = oldDoc[pa].Parent;
                    pb = newDoc[pb].Parent;
                }
            }

            var keys = new List<long>(votes.Keys);
            keys.Sort();
            var bestOld = new int[oldDoc.Count];
            var bestNew = new int[newDoc.Count];
            var tiedOld = new bool[oldDoc.Count];
            var tiedNew = new bool[newDoc.Count];
            var partnerOld = new int[oldDoc.Count];
            var partnerNew = new int[newDoc.Count];
            foreach (long key in keys)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF), v = votes[key];
                if (v > bestOld[a])
                {
                    bestOld[a] = v;
                    partnerOld[a] = b;
                    tiedOld[a] = false;
                }
                else if (v == bestOld[a])
                {
                    tiedOld[a] = true;
                }

                if (v > bestNew[b])
                {
                    bestNew[b] = v;
                    partnerNew[b] = a;
                    tiedNew[b] = false;
                }
                else if (v == bestNew[b])
                {
                    tiedNew[b] = true;
                }
            }

            foreach (long key in keys)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
                if (partnerOld[a] == b && partnerNew[b] == a && !tiedOld[a] && !tiedNew[b])
                {
                    provOld[a] = b;
                    provNew[b] = a;
                }
            }
        }

        // Whether pairing a repeated block of the old document with one of the new contradicts what is known of their containers:
        // walking up from both, a container already matched to another, or provisionally the partner of another, says the copy
        // belongs elsewhere. Reaching a pair that is matched to each other says it belongs here.
        private bool Conflicts(int a, int b, int[] mo, int[] mn)
        {
            int pa = oldDoc[a].Parent, pb = newDoc[b].Parent;
            while (pa >= 0 && pb >= 0 && oldDoc[pa].Kind == newDoc[pb].Kind)
            {
                if (mo[pa] == pb)
                {
                    return false;
                }

                if (mo[pa] >= 0 || mn[pb] >= 0)
                {
                    return true;
                }

                if ((provOld[pa] >= 0 && provOld[pa] != pb) || (provNew[pb] >= 0 && provNew[pb] != pa))
                {
                    return true;
                }

                pa = oldDoc[pa].Parent;
                pb = newDoc[pb].Parent;
            }

            return false;
        }

        // ---- helpers -----------------------------------------------------------------------------------------------

        private void CapReached(string message) => Note(DiagnosticSeverity.Warning, "align.cap", message);

        private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

        private static List<int> GetList(Dictionary<int, List<int>> map, int key)
        {
            if (!map.TryGetValue(key, out List<int>? list))
            {
                map[key] = list = [];
            }

            return list;
        }

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
