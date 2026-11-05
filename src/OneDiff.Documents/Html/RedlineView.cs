using System.Collections.Immutable;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Html;

internal enum PieceKind : byte
{
    Equal,
    Insert,
    Delete,
    Format,

    /// <summary>A paragraph break that the change inserted (the block boundary itself is the visible part).</summary>
    InsertMark,

    /// <summary>A paragraph break that the change removed.</summary>
    DeleteMark,
}

/// <summary>A stretch of a block's text: unchanged, inserted, deleted or reformatted, with the change it belongs to.</summary>
internal readonly record struct Piece(PieceKind Kind, string Text, int ChangeId, RegionEdit? Edit);

/// <summary>
/// The text of one alignment link laid out as a single timeline of pieces (unchanged text from the new side, with each edit's
/// deleted and inserted text at its place), and the same timeline cut into the blocks of each side.
/// </summary>
internal sealed class LinkView
{
    private List<Piece>[]? inline;
    private List<Piece>[]? oldSide;
    private List<Piece>[]? newSide;

    public LinkView(AlignmentLink link, List<Piece> pieces, int moveChange)
    {
        Link = link;
        Pieces = pieces;
        MoveChange = moveChange;
    }

    public AlignmentLink Link { get; }

    public List<Piece> Pieces { get; }

    /// <summary>The id of the link's Move change, or -1.</summary>
    public int MoveChange { get; }

    /// <summary>Pieces per new-side block with deleted text in place (the single-document view).</summary>
    public List<Piece>[] Inline() => inline ??= Split(Pieces, Link.NewNodes.Length, keepInsert: true, keepDelete: true, splitInsert: true, splitDelete: false);

    /// <summary>Pieces per old-side block: unchanged and deleted text only.</summary>
    public List<Piece>[] OldSide() => oldSide ??= Split(Pieces, Link.OldNodes.Length, keepInsert: false, keepDelete: true, splitInsert: false, splitDelete: true);

    /// <summary>Pieces per new-side block: unchanged and inserted text only.</summary>
    public List<Piece>[] NewSide() => newSide ??= Split(Pieces, Link.NewNodes.Length, keepInsert: true, keepDelete: false, splitInsert: true, splitDelete: false);

    /// <summary>
    /// Cuts a timeline at the node boundaries (U+001E) of the stream it keeps. A boundary inside an inserted or deleted piece
    /// leaves a mark at the end of the block so the change stays visible there.
    /// </summary>
    private static List<Piece>[] Split(List<Piece> pieces, int members, bool keepInsert, bool keepDelete, bool splitInsert, bool splitDelete)
    {
        int count = Math.Max(members, 1);
        var result = new List<Piece>[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = [];
        }

        int cur = 0;
        foreach (Piece p in pieces)
        {
            bool keep = p.Kind switch { PieceKind.Insert => keepInsert, PieceKind.Delete => keepDelete, _ => true };
            if (!keep)
            {
                continue;
            }

            bool split = p.Kind switch { PieceKind.Insert => splitInsert, PieceKind.Delete => splitDelete, _ => true };
            if (!split || count == 1 || p.Text.IndexOf(HtmlOut.Separator, StringComparison.Ordinal) < 0)
            {
                result[cur].Add(p);
                continue;
            }

            string t = p.Text;
            int pos = 0;
            while (true)
            {
                int idx = t.IndexOf(HtmlOut.Separator, pos);
                string part = idx < 0 ? t[pos..] : t[pos..idx];
                if (part.Length > 0)
                {
                    result[cur].Add(p with { Text = part });
                }

                if (idx < 0)
                {
                    break;
                }

                if (p.Kind == PieceKind.Insert)
                {
                    result[cur].Add(new Piece(PieceKind.InsertMark, "", p.ChangeId, p.Edit));
                }
                else if (p.Kind == PieceKind.Delete)
                {
                    result[cur].Add(new Piece(PieceKind.DeleteMark, "", p.ChangeId, p.Edit));
                }

                cur = Math.Min(cur + 1, count - 1);
                pos = idx + 1;
            }
        }

        return result;
    }
}

/// <summary>A place in the merged tree of the redline: a node of the new document, or an old node that is shown where it used to be.</summary>
internal sealed class MNode(bool ghost, int id, bool moveFrom)
{
    /// <summary>True for an old node shown at its old place (deleted, or the old place of a move).</summary>
    public bool Ghost { get; } = ghost;

    /// <summary>The new node id, or for a ghost the old node id.</summary>
    public int Id { get; } = id;

    /// <summary>True for the old place of a moved node.</summary>
    public bool MoveFrom { get; } = moveFrom;

    public List<MNode> Children { get; set; } = [];
}

/// <summary>
/// Everything a writer needs to lay a comparison out as a document: which change goes with which node, the text of each link as
/// pieces, and the merged tree in reading order, in which deleted and moved-away nodes appear where they used to be.
/// </summary>
internal sealed class RedlineView
{
    private readonly Dictionary<MNode, List<MNode>> before = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MNode, List<MNode>> after = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MNode, List<MNode>> tail = new(ReferenceEqualityComparer.Instance);
    private readonly List<MNode> tailRoots = [];

    private RedlineView(Comparison comparison)
    {
        Comparison = comparison;
        Alignment = comparison.Alignment;
        Old = Alignment.Old;
        New = Alignment.New;
        Settings = Alignment.Settings;
        HiddenOld = HiddenNodes.Compute(Old, Settings);
        HiddenNew = HiddenNodes.Compute(New, Settings);
        InsertOfNew = new Change?[New.Count];
        DeleteOfOld = new Change?[Old.Count];
        NewMap = new MNode?[New.Count];
    }

    public Comparison Comparison { get; }

    /// <summary>Whether marks inside a word are widened to the whole word (<see cref="RedlineStyle.MarkWholeWords"/>).</summary>
    public bool WholeWords { get; private init; }

    public Alignment Alignment { get; }

    public CompareDocument Old { get; }

    public CompareDocument New { get; }

    public CompareSettings Settings { get; }

    public LedgerCategory?[] HiddenOld { get; }

    public LedgerCategory?[] HiddenNew { get; }

    /// <summary>The whole-node Insert change of each new node (null when none).</summary>
    public Change?[] InsertOfNew { get; }

    /// <summary>The whole-node Delete change of each old node (null when none).</summary>
    public Change?[] DeleteOfOld { get; }

    /// <summary>The Move change of each moved link, by link id.</summary>
    public Dictionary<int, Change> MoveOfLink { get; } = [];

    /// <summary>The text view of each shown link, by link id.</summary>
    public Dictionary<int, LinkView> Links { get; } = [];

    /// <summary>The attribute edits to show on each new node, with the change each belongs to.</summary>
    public Dictionary<int, List<(int ChangeId, AttributeEdit Edit)>> AttributesOfNew { get; } = [];

    /// <summary>The kind of each change, by id.</summary>
    public Dictionary<int, ChangeKind> ChangeKinds { get; } = [];

    public MNode?[] NewMap { get; }

    public List<MNode> Roots { get; private set; } = [];

    public static RedlineView Build(Comparison comparison, bool wholeWords, CancellationToken cancellationToken)
    {
        var v = new RedlineView(comparison) { WholeWords = wholeWords };
        v.IndexChanges();
        v.BuildLinks(cancellationToken);
        v.BuildTree(cancellationToken);
        return v;
    }

    private void IndexChanges()
    {
        foreach (Change c in Comparison.Changes)
        {
            ChangeKinds[c.Id] = c.Kind;
            if (c.LinkId < 0)
            {
                if (c.Kind == ChangeKind.Insert && c.New.IsPresent)
                {
                    InsertOfNew[c.New.Nodes[0]] = c;
                }
                else if (c.Kind == ChangeKind.Delete && c.Old.IsPresent)
                {
                    DeleteOfOld[c.Old.Nodes[0]] = c;
                }

                continue;
            }

            if (c.Kind == ChangeKind.Move)
            {
                MoveOfLink[c.LinkId] = c;
            }

            foreach (AttributeEdit e in c.AttributeEdits)
            {
                if (!AttributesOfNew.TryGetValue(e.NewNode, out List<(int, AttributeEdit)>? list))
                {
                    list = [];
                    AttributesOfNew[e.NewNode] = list;
                }

                list.Add((c.Id, e));
            }
        }
    }

    private void BuildLinks(CancellationToken ct)
    {
        var editsOfLink = new Dictionary<int, List<(RegionEdit Edit, int ChangeId)>>();
        foreach (Change c in Comparison.Changes)
        {
            if (c.LinkId < 0 || c.Edits.IsDefaultOrEmpty)
            {
                continue;
            }

            if (!editsOfLink.TryGetValue(c.LinkId, out List<(RegionEdit, int)>? list))
            {
                list = [];
                editsOfLink[c.LinkId] = list;
            }

            foreach (RegionEdit e in c.Edits)
            {
                list.Add((e, c.Id));
            }
        }

        foreach (AlignmentLink link in Alignment.Links)
        {
            ct.ThrowIfCancellationRequested();
            if (HiddenOld[link.OldNodes[0]] is not null || HiddenNew[link.NewNodes[0]] is not null)
            {
                continue;
            }

            string newText = ProjectedGroup.Create(New, link.NewNodes, Settings).Text;
            List<(RegionEdit Edit, int ChangeId)> edits = editsOfLink.TryGetValue(link.Id, out List<(RegionEdit, int)>? l) ? l : [];
            edits = [.. edits.OrderBy(x => x.Edit.NewStart).ThenBy(x => x.Edit.OldStart)];
            int move = MoveOfLink.TryGetValue(link.Id, out Change? mc) ? mc.Id : -1;
            Links[link.Id] = new LinkView(link, Timeline(newText, edits, WholeWords), move);
        }
    }

    // A character of a word written with spaces. Han, kana and Hangul are left out: a run of them is a sentence, not a word.
    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) && !(c is (>= '\u3040' and <= '\u30FF') or (>= '\u3400' and <= '\u9FFF') or (>= '\uAC00' and <= '\uD7AF') or (>= '\uF900' and <= '\uFAFF'));

    private static bool StartsWithWordChar(string s) => s.Length > 0 && IsWordChar(s[0]);

    private static bool EndsWithWordChar(string s) => s.Length > 0 && IsWordChar(s[^1]);

    /// <summary>
    /// Lays a link's edits over its new text: unchanged text from the new side, and at each edit its deleted then inserted text. With
    /// <paramref name="wholeWords"/> an edit that begins or ends inside a word takes the rest of the word on both sides (the rest is
    /// the same text in old and new), and edits of one change that touch the same word become one.
    /// </summary>
    private static List<Piece> Timeline(string newText, List<(RegionEdit Edit, int ChangeId)> edits, bool wholeWords)
    {
        var pieces = new List<Piece>();
        int pos = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            (RegionEdit e, int id) = edits[i];
            int start = Math.Clamp(e.NewStart, pos, newText.Length);
            string del = e.OldText, ins = e.NewText;
            int end = Math.Clamp(start + e.NewLength, start, newText.Length);
            int from = start;
            RegionEdit shown = e;
            if (wholeWords && !e.IsFormatOnly)
            {
                int left = start;
                if (start > pos && IsWordChar(newText[start - 1]) && (StartsWithWordChar(del) || StartsWithWordChar(ins)))
                {
                    while (left > pos && IsWordChar(newText[left - 1]))
                    {
                        left--;
                    }
                }

                string head = newText[left..start];
                string body = del, bodyNew = ins;
                int right = end;
                while (true)
                {
                    int wordEnd = right;
                    if (right < newText.Length && IsWordChar(newText[right]) && (EndsWithWordChar(body) || EndsWithWordChar(bodyNew) || right > end))
                    {
                        while (wordEnd < newText.Length && IsWordChar(newText[wordEnd]))
                        {
                            wordEnd++;
                        }
                    }

                    // Another edit of the same change that starts inside the word joins this one.
                    if (i + 1 < edits.Count && edits[i + 1].ChangeId == id && !edits[i + 1].Edit.IsFormatOnly && edits[i + 1].Edit.NewStart >= right && edits[i + 1].Edit.NewStart <= wordEnd && wordEnd > right)
                    {
                        RegionEdit next = edits[++i].Edit;
                        string gap = newText[right..next.NewStart];
                        body += gap + next.OldText;
                        bodyNew += gap + next.NewText;
                        right = Math.Min(next.NewStart + next.NewLength, newText.Length);
                        continue;
                    }

                    // Do not run into an edit of another change: keep this one as it is.
                    bool blocked = i + 1 < edits.Count && edits[i + 1].Edit.NewStart < wordEnd;
                    if (wordEnd > right && !blocked)
                    {
                        string rest = newText[right..wordEnd];
                        body += rest;
                        bodyNew += rest;
                        right = wordEnd;
                    }

                    break;
                }

                if (right - left <= 64 && (left < start || right > end || !ReferenceEquals(body, del) || !ReferenceEquals(bodyNew, ins)))
                {
                    from = left;
                    del = head + body;
                    ins = head + bodyNew;
                    end = right;
                    shown = e with { OldText = del, NewText = ins, OldLength = del.Length, NewLength = ins.Length };
                }
            }

            if (from > pos)
            {
                pieces.Add(new Piece(PieceKind.Equal, newText[pos..from], -1, null));
            }

            if (e.IsFormatOnly)
            {
                pieces.Add(new Piece(PieceKind.Format, e.NewText, id, e));
            }
            else
            {
                if (del.Length > 0)
                {
                    pieces.Add(new Piece(PieceKind.Delete, del, id, shown));
                }

                if (ins.Length > 0)
                {
                    pieces.Add(new Piece(PieceKind.Insert, ins, id, shown));
                }
            }

            pos = Math.Clamp(end, from, newText.Length);
        }

        if (pos < newText.Length)
        {
            pieces.Add(new Piece(PieceKind.Equal, newText[pos..], -1, null));
        }

        return pieces;
    }

    private void BuildTree(CancellationToken ct)
    {
        // The new document's tree, hidden subtrees left out.
        foreach (Node n in New.Nodes)
        {
            if (HiddenNew[n.Id] is not null)
            {
                continue;
            }

            var m = new MNode(false, n.Id, false);
            NewMap[n.Id] = m;
            if (n.Parent < 0)
            {
                Roots.Add(m);
            }
            else
            {
                NewMap[n.Parent]?.Children.Add(m);
            }
        }

        // Old nodes that are shown where they used to be: deleted ones, and the old place of a moved node.
        var ghostDelete = new bool[Old.Count];
        var ghostMove = new bool[Old.Count];
        foreach (Node o in Old.Nodes)
        {
            if (HiddenOld[o.Id] is not null)
            {
                continue;
            }

            AlignmentLink? link = Alignment.LinkOfOld(o.Id);
            if (link is null)
            {
                ghostDelete[o.Id] = true;
            }
            else if (link.Kind == LinkKind.Move && link.OldNodes.Length == 1 && Links.ContainsKey(link.Id))
            {
                ghostMove[o.Id] = true;
            }
        }

        var ghosts = new MNode?[Old.Count];
        for (int p = -1; p < Old.Count; p++)
        {
            ct.ThrowIfCancellationRequested();
            if (p >= 0 && HiddenOld[p] is not null)
            {
                continue;
            }

            ImmutableArray<int> siblings = p < 0 ? Old.Roots : Old.ChildrenOf(p);
            if (!siblings.Any(s => ghostDelete[s] || ghostMove[s]))
            {
                continue;
            }

            if (p >= 0 && ghostDelete[p])
            {
                // Inside a deleted node, its deleted children stay inside it.
                foreach (int s in siblings)
                {
                    if (ghostDelete[s] || ghostMove[s])
                    {
                        var g = new MNode(true, s, ghostMove[s]);
                        ghosts[s] = g;
                        ghosts[p]!.Children.Add(g);
                    }
                }

                continue;
            }

            // Otherwise a ghost goes before the next sibling that kept its place, or after the last one.
            var pending = new List<MNode>();
            MNode? lastAnchor = null;
            foreach (int s in siblings)
            {
                if (HiddenOld[s] is not null)
                {
                    continue;
                }

                if (ghostDelete[s] || ghostMove[s])
                {
                    var g = new MNode(true, s, ghostMove[s]);
                    ghosts[s] = g;
                    pending.Add(g);
                    continue;
                }

                AlignmentLink? link = Alignment.LinkOfOld(s);
                MNode? first = link is null ? null : NewMap[link.NewNodes[0]];
                if (first is null)
                {
                    continue;
                }

                if (pending.Count > 0)
                {
                    Add(before, first, pending);
                    pending.Clear();
                }

                lastAnchor = NewMap[link!.NewNodes[^1]];
            }

            if (pending.Count > 0)
            {
                if (lastAnchor is not null)
                {
                    Add(after, lastAnchor, pending);
                }
                else
                {
                    MNode? home = null;
                    if (p >= 0 && Alignment.LinkOfOld(p) is { } pl)
                    {
                        home = NewMap[pl.NewNodes[0]];
                    }

                    if (home is not null)
                    {
                        Add(tail, home, pending);
                    }
                    else
                    {
                        tailRoots.AddRange(pending);
                    }
                }
            }
        }

        // Weave the ghosts into the children lists.
        Roots = Weave(Roots, null);
        Roots.AddRange(tailRoots);
        foreach (MNode? m in NewMap)
        {
            if (m is not null)
            {
                m.Children = Weave(m.Children, m);
            }
        }
    }

    private static void Add(Dictionary<MNode, List<MNode>> map, MNode key, List<MNode> ghosts)
    {
        if (!map.TryGetValue(key, out List<MNode>? list))
        {
            list = [];
            map[key] = list;
        }

        list.AddRange(ghosts);
    }

    private List<MNode> Weave(List<MNode> children, MNode? parent)
    {
        if (before.Count == 0 && after.Count == 0 && tail.Count == 0)
        {
            return children;
        }

        // Ghosts go before the next kept block; when new blocks were inserted just before it, the ghosts go ahead of those, so a
        // removed block reads before the block that replaced it.
        var pending = new Dictionary<int, List<MNode>>();
        for (int i = 0; i < children.Count; i++)
        {
            if (!before.TryGetValue(children[i], out List<MNode>? b))
            {
                continue;
            }

            int at = i;
            while (at > 0 && !children[at - 1].Ghost && Alignment.LinkOfNew(children[at - 1].Id) is null)
            {
                at--;
            }

            if (!pending.TryGetValue(at, out List<MNode>? list))
            {
                list = [];
                pending[at] = list;
            }

            list.AddRange(b);
        }

        var result = new List<MNode>(children.Count);
        for (int i = 0; i < children.Count; i++)
        {
            MNode c = children[i];
            if (pending.TryGetValue(i, out List<MNode>? b))
            {
                result.AddRange(b);
            }

            result.Add(c);
            if (after.TryGetValue(c, out List<MNode>? a))
            {
                result.AddRange(a);
            }
        }

        if (parent is not null && tail.TryGetValue(parent, out List<MNode>? t))
        {
            result.AddRange(t);
        }

        return result;
    }
}
