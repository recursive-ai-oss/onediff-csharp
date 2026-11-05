using System.Collections.Immutable;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Pipeline;

/// <summary>How the nodes of an <see cref="AlignmentLink"/> relate.</summary>
public enum LinkKind
{
    /// <summary>One old node is one new node (possibly edited), in the same place.</summary>
    Match = 0,

    /// <summary>One old node is one new node, at a different place (another position among its siblings, or another parent).</summary>
    Move = 1,

    /// <summary>One old node became two or more new nodes.</summary>
    Split = 2,

    /// <summary>Two or more old nodes became one new node.</summary>
    Merge = 3,
}

/// <summary>Why a link exists.</summary>
public enum LinkOrigin
{
    /// <summary>The nodes have equal compared text and kind and sit in matching order.</summary>
    Exact = 0,

    /// <summary>The nodes are similar enough to be one block edited.</summary>
    Similar = 1,

    /// <summary>Found by comparing blocks outside the matched order (a move candidate).</summary>
    Relocated = 2,

    /// <summary>Found as a split or a merge.</summary>
    SplitMerge = 3,

    /// <summary>Set by a person with <see cref="Alignment.Pin"/> or <see cref="Alignment.Force"/>.</summary>
    User = 4,

    /// <summary>Produced by a fallback.</summary>
    Fallback = 5,
}

/// <summary>
/// One correspondence between old and new nodes.
/// </summary>
/// <param name="Id">The link's number in its alignment (its index in <see cref="Alignment.Links"/>).</param>
/// <param name="Kind">How the nodes relate.</param>
/// <param name="OldNodes">The old nodes: one, or several for a merge; ascending.</param>
/// <param name="NewNodes">The new nodes: one, or several for a split; ascending.</param>
/// <param name="Pinned">Whether a person fixed this link; a re-run of <see cref="Stages.Align"/> with the alignment as seed keeps pinned links.</param>
/// <param name="Score">The similarity in [0, 1] the link was made on (1 for exact).</param>
/// <param name="Origin">Why the link exists.</param>
public sealed record AlignmentLink(int Id, LinkKind Kind, ImmutableArray<int> OldNodes, ImmutableArray<int> NewNodes, bool Pinned, double Score, LinkOrigin Origin);

/// <summary>
/// Which nodes of the old document correspond to which nodes of the new one. Every node is in at most one link; nodes in none
/// are deleted (old) or inserted (new). The alignment is an editable value: <see cref="Pin"/>, <see cref="Unpin"/> and
/// <see cref="Force"/> return corrected copies, and the downstream stages are then run again on the copy. Immutable.
/// </summary>
public sealed class Alignment
{
    private readonly int[] oldToLink;
    private readonly int[] newToLink;

    private Alignment(
        CompareDocument oldDocument,
        CompareDocument newDocument,
        CompareSettings settings,
        ImmutableArray<AlignmentLink> links,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Old = oldDocument;
        New = newDocument;
        Settings = settings;
        Links = links;
        Diagnostics = diagnostics;
        oldToLink = new int[oldDocument.Count];
        newToLink = new int[newDocument.Count];
        Array.Fill(oldToLink, -1);
        Array.Fill(newToLink, -1);
        foreach (AlignmentLink l in links)
        {
            foreach (int o in l.OldNodes)
            {
                oldToLink[o] = l.Id;
            }

            foreach (int n in l.NewNodes)
            {
                newToLink[n] = l.Id;
            }
        }
    }

    /// <summary>The old document.</summary>
    public CompareDocument Old { get; }

    /// <summary>The new document.</summary>
    public CompareDocument New { get; }

    /// <summary>The settings this alignment was made under; later stages use them.</summary>
    public CompareSettings Settings { get; }

    /// <summary>The links, ordered by first new node (then first old node); <c>Links[i].Id == i</c>.</summary>
    public ImmutableArray<AlignmentLink> Links { get; }

    /// <summary>What the stages that made this value had to say.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>The link an old node is in, or null.</summary>
    public AlignmentLink? LinkOfOld(int oldNode)
    {
        int id = oldToLink[CheckIndex(oldNode, Old.Count)];
        return id < 0 ? null : Links[id];
    }

    /// <summary>The link a new node is in, or null.</summary>
    public AlignmentLink? LinkOfNew(int newNode)
    {
        int id = newToLink[CheckIndex(newNode, New.Count)];
        return id < 0 ? null : Links[id];
    }

    /// <summary>The old nodes in no link (deleted), ascending.</summary>
    public IEnumerable<int> UnmatchedOld()
    {
        for (int i = 0; i < oldToLink.Length; i++)
        {
            if (oldToLink[i] < 0)
            {
                yield return i;
            }
        }
    }

    /// <summary>The new nodes in no link (inserted), ascending.</summary>
    public IEnumerable<int> UnmatchedNew()
    {
        for (int i = 0; i < newToLink.Length; i++)
        {
            if (newToLink[i] < 0)
            {
                yield return i;
            }
        }
    }

    /// <summary>
    /// Builds an alignment from links (their ids are ignored and reassigned) after checking them: nodes exist, no node is in two
    /// links, matches and moves pair one node with one of the same kind, splits and merges are 1-to-n with the same kind.
    /// The links are not normalized; see <see cref="Normalize"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A link is invalid.</exception>
    public static Alignment Create(
        CompareDocument oldDocument,
        CompareDocument newDocument,
        CompareSettings settings,
        IEnumerable<AlignmentLink> links,
        IEnumerable<Diagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(oldDocument);
        ArgumentNullException.ThrowIfNull(newDocument);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(links);
        var list = new List<AlignmentLink>(links);
        var seenOld = new bool[oldDocument.Count];
        var seenNew = new bool[newDocument.Count];
        foreach (AlignmentLink l in list)
        {
            Validate(l, oldDocument, newDocument, seenOld, seenNew);
        }

        list.Sort(static (a, b) =>
        {
            int c = a.NewNodes[0].CompareTo(b.NewNodes[0]);
            return c != 0 ? c : a.OldNodes[0].CompareTo(b.OldNodes[0]);
        });
        ImmutableArray<AlignmentLink>.Builder b = ImmutableArray.CreateBuilder<AlignmentLink>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            b.Add(list[i] with { Id = i });
        }

        return new Alignment(oldDocument, newDocument, settings, b.MoveToImmutable(), diagnostics is null ? [] : [.. diagnostics]);
    }

    /// <summary>A copy of this alignment with extra diagnostics.</summary>
    public Alignment WithDiagnostics(IEnumerable<Diagnostic> more)
    {
        ArgumentNullException.ThrowIfNull(more);
        return new Alignment(Old, New, Settings, Links, [.. Diagnostics, .. more]);
    }

    /// <summary>A copy under different settings (the links are unchanged).</summary>
    public Alignment WithSettings(CompareSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return new Alignment(Old, New, settings, Links, Diagnostics);
    }

    /// <summary>
    /// Fixes <paramref name="oldNode"/> and <paramref name="newNode"/> as one block. Links that touch either node are dropped
    /// (their other nodes become unmatched). The result is normalized, so a pin that conflicts with the surrounding order shows
    /// as a move rather than breaking the alignment.
    /// </summary>
    /// <exception cref="ArgumentException">The nodes do not exist or are of different kinds.</exception>
    public Alignment Pin(int oldNode, int newNode) => Force([oldNode], [newNode], LinkKind.Match);

    /// <summary>
    /// Removes a pinned or forced link: its nodes become unmatched. Running <see cref="Stages.Align"/> with this alignment as
    /// seed pairs them again by the ordinary rules.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="linkId"/> is not a link of this alignment.</exception>
    /// <exception cref="InvalidOperationException">The link is not pinned.</exception>
    public Alignment Unpin(int linkId)
    {
        AlignmentLink link = Links[CheckIndex(linkId, Links.Length)];
        if (!link.Pinned)
        {
            throw new InvalidOperationException($"Link {linkId} is not pinned.");
        }

        return Create(Old, New, Settings, Links.Where(l => l.Id != linkId), Diagnostics).Normalize();
    }

    /// <summary>
    /// Fixes any link shape: a match or move (one old, one new), a split (one old, several new) or a merge (several old, one new).
    /// Links that touch any of the nodes are dropped. The result is normalized.
    /// </summary>
    /// <exception cref="ArgumentException">The shape does not fit <paramref name="kind"/>, a node does not exist, or the kinds differ.</exception>
    public Alignment Force(IReadOnlyList<int> oldNodes, IReadOnlyList<int> newNodes, LinkKind kind)
    {
        ArgumentNullException.ThrowIfNull(oldNodes);
        ArgumentNullException.ThrowIfNull(newNodes);
        var forced = new AlignmentLink(0, kind, [.. oldNodes.OrderBy(static x => x)], [.. newNodes.OrderBy(static x => x)], true, 1.0, LinkOrigin.User);
        Validate(forced, Old, New, new bool[Old.Count], new bool[New.Count]);
        var touched = new HashSet<int>();
        foreach (int o in forced.OldNodes)
        {
            if (oldToLink[o] >= 0)
            {
                touched.Add(oldToLink[o]);
            }
        }

        foreach (int n in forced.NewNodes)
        {
            if (newToLink[n] >= 0)
            {
                touched.Add(newToLink[n]);
            }
        }

        var kept = Links.Where(l => !touched.Contains(l.Id)).Append(forced);
        return Create(Old, New, Settings, kept, Diagnostics).Normalize();
    }

    /// <summary>
    /// Settles which one-to-one links are matches and which are moves, so that the alignment satisfies the order and parent
    /// rules verification checks. A one-to-one link is a move when its old parent is not paired with its new parent, or when
    /// it is not part of the longest run of siblings that keep their order; links forced as moves stay moves, and links in
    /// hidden subtrees and splits and merges are left as they are. Idempotent.
    /// </summary>
    public Alignment Normalize()
    {
        LedgerCategory?[] hiddenOld = HiddenNodes.Compute(Old, Settings);
        LedgerCategory?[] hiddenNew = HiddenNodes.Compute(New, Settings);
        var kinds = new LinkKind[Links.Length];
        var oneToOneNewOfOld = new int[Old.Count];
        Array.Fill(oneToOneNewOfOld, -1);
        foreach (AlignmentLink l in Links)
        {
            if (l.Kind is LinkKind.Match or LinkKind.Move)
            {
                oneToOneNewOfOld[l.OldNodes[0]] = l.NewNodes[0];
            }
        }

        // New parent -> its candidate children links, in new order (links are sorted by first new node).
        var byParent = new Dictionary<int, List<AlignmentLink>>();
        foreach (AlignmentLink l in Links)
        {
            kinds[l.Id] = l.Kind;
            if (l.Kind is not (LinkKind.Match or LinkKind.Move))
            {
                continue;
            }

            int o = l.OldNodes[0], n = l.NewNodes[0];
            if (hiddenOld[o] is not null || hiddenNew[n] is not null)
            {
                kinds[l.Id] = LinkKind.Match;
                continue;
            }

            if (l.Kind == LinkKind.Move && l.Pinned)
            {
                continue;
            }

            int po = Old[o].Parent, pn = New[n].Parent;
            bool parentOk = (po < 0 && pn < 0) || (po >= 0 && pn >= 0 && oneToOneNewOfOld[po] == pn);
            if (!parentOk)
            {
                kinds[l.Id] = LinkKind.Move;
                continue;
            }

            kinds[l.Id] = LinkKind.Match;
            if (!byParent.TryGetValue(pn, out List<AlignmentLink>? list))
            {
                byParent[pn] = list = [];
            }

            list.Add(l);
        }

        foreach (List<AlignmentLink> siblings in byParent.Values)
        {
            var keep = LongestIncreasing(siblings);
            for (int i = 0; i < siblings.Count; i++)
            {
                if (!keep[i])
                {
                    kinds[siblings[i].Id] = LinkKind.Move;
                }
            }
        }

        ImmutableArray<AlignmentLink>.Builder b = ImmutableArray.CreateBuilder<AlignmentLink>(Links.Length);
        foreach (AlignmentLink l in Links)
        {
            b.Add(kinds[l.Id] == l.Kind ? l : l with { Kind = kinds[l.Id] });
        }

        return new Alignment(Old, New, Settings, b.MoveToImmutable(), Diagnostics);
    }

    /// <summary>Marks which siblings (given in new order) belong to a longest strictly increasing run of old node ids. Deterministic.</summary>
    private static bool[] LongestIncreasing(List<AlignmentLink> siblings)
    {
        int n = siblings.Count;
        var tails = new List<int>();   // tails[k] = index of the smallest tail of an increasing run of length k+1
        var prev = new int[n];
        for (int i = 0; i < n; i++)
        {
            int v = siblings[i].OldNodes[0];
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (siblings[tails[mid]].OldNodes[0] < v)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            prev[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count)
            {
                tails.Add(i);
            }
            else
            {
                tails[lo] = i;
            }
        }

        var keep = new bool[n];
        for (int k = tails.Count > 0 ? tails[^1] : -1; k >= 0; k = prev[k])
        {
            keep[k] = true;
        }

        return keep;
    }

    private static void Validate(AlignmentLink l, CompareDocument oldDocument, CompareDocument newDocument, bool[] seenOld, bool[] seenNew)
    {
        if (l.OldNodes.IsDefaultOrEmpty || l.NewNodes.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A link needs old and new nodes.");
        }

        bool shapeOk = l.Kind switch
        {
            LinkKind.Match or LinkKind.Move => l.OldNodes.Length == 1 && l.NewNodes.Length == 1,
            LinkKind.Split => l.OldNodes.Length == 1 && l.NewNodes.Length >= 2,
            LinkKind.Merge => l.OldNodes.Length >= 2 && l.NewNodes.Length == 1,
            _ => false,
        };
        if (!shapeOk)
        {
            throw new ArgumentException($"A {l.Kind} link cannot join {l.OldNodes.Length} old and {l.NewNodes.Length} new nodes.");
        }

        NodeKind kind = oldDocument[l.OldNodes[0]].Kind;
        int last = -1;
        foreach (int o in l.OldNodes)
        {
            if (o < 0 || o >= oldDocument.Count || o <= last || seenOld[o] || oldDocument[o].Kind != kind)
            {
                throw new ArgumentException($"Old node {o} is missing, repeated, out of order, in another link or of another kind.");
            }

            seenOld[o] = true;
            last = o;
        }

        last = -1;
        foreach (int n in l.NewNodes)
        {
            if (n < 0 || n >= newDocument.Count || n <= last || seenNew[n] || newDocument[n].Kind != kind)
            {
                throw new ArgumentException($"New node {n} is missing, repeated, out of order, in another link or of another kind.");
            }

            seenNew[n] = true;
            last = n;
        }
    }

    private static int CheckIndex(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        return index;
    }
}
