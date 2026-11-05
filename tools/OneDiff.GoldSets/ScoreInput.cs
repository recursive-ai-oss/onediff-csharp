using System.Text;
using System.Text.Json;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.GoldSets;

/// <summary>How a change takes part in the boundary and fragmentation rows.</summary>
internal enum SKind
{
    /// <summary>A text change: its extent is the union of its sides' trimmed spans.</summary>
    Text,

    /// <summary>A move: it spans its whole node and counts only for a gold move unit.</summary>
    Move,

    /// <summary>A change with no text span (attribute, split, merge): left out of both rows.</summary>
    NoExtent,
}

/// <summary>A change in the gold documents' coordinates: the intervals of node text it covers on either side.</summary>
/// <param name="Id">The change's id in its comparison.</param>
/// <param name="Kind">How it takes part in the rows.</param>
/// <param name="Extent">The intervals it covers.</param>
/// <param name="Flagged">Whether the comparison marked it as a possible OCR error.</param>
internal sealed record SChange(int Id, SKind Kind, List<Iv> Extent, bool Flagged = false);

/// <summary>A correspondence of content blocks by gold node id. Kind is match, move, split or merge.</summary>
internal sealed record SLink(string Kind, int[] Old, int[] New);

/// <summary>
/// A comparison reduced to what the scorer reads, in the coordinates of the gold documents (node ids and character offsets of the
/// labelled pair). The engine's <see cref="Comparison"/> converts directly when its nodes are the gold nodes; a comparison made from
/// a rendered file (PDF, DOCX) or the tracked-changes output of another tool is first mapped through the pair's block map.
/// </summary>
internal sealed class ScoreInput
{
    /// <summary>Text of every gold node, indexed by node id.</summary>
    public string[] OldText { get; init; } = [];

    public string[] NewText { get; init; } = [];

    /// <summary>Whether a node is a content block (paragraph or list item), indexed by node id.</summary>
    public bool[] OldContent { get; init; } = [];

    public bool[] NewContent { get; init; } = [];

    public List<SLink> Links { get; init; } = [];

    public List<SChange> Changes { get; init; } = [];

    /// <summary>Display groups, as the ids of the changes they hold.</summary>
    public List<int[]> Groups { get; init; } = [];

    public bool Passed { get; init; } = true;

    public int FallbackLevel { get; init; }

    private static bool IsContent(NodeKind k) => k is NodeKind.Paragraph or NodeKind.ListItem;

    /// <summary>Reads a comparison whose nodes are the gold nodes (same ids, same text).</summary>
    public static ScoreInput From(Comparison cmp)
    {
        CompareDocument od = cmp.Old;
        CompareDocument nd = cmp.New;
        var links = new List<SLink>();
        foreach (AlignmentLink l in cmp.Alignment.Links)
        {
            if (!l.OldNodes.All(i => IsContent(od[i].Kind)) || !l.NewNodes.All(i => IsContent(nd[i].Kind)))
            {
                continue;
            }

            string kind = l.Kind switch { LinkKind.Move => "move", LinkKind.Split => "split", LinkKind.Merge => "merge", _ => "match" };
            links.Add(new SLink(kind, [.. l.OldNodes], [.. l.NewNodes]));
        }

        var changes = new List<SChange>();
        foreach (Change ch in cmp.Changes)
        {
            SKind kind = ch.Kind switch { ChangeKind.Attribute or ChangeKind.Split or ChangeKind.Merge => SKind.NoExtent, ChangeKind.Move => SKind.Move, _ => SKind.Text };
            var ivs = new List<Iv>();
            if (kind != SKind.NoExtent)
            {
                Sides(ch.Old, false, od, ivs);
                Sides(ch.New, true, nd, ivs);
            }

            changes.Add(new SChange(ch.Id, kind, ivs, ch.PossibleOcrError));
        }

        return new ScoreInput
        {
            OldText = [.. od.Nodes.Select(n => n.Text)],
            NewText = [.. nd.Nodes.Select(n => n.Text)],
            OldContent = [.. od.Nodes.Select(n => IsContent(n.Kind))],
            NewContent = [.. nd.Nodes.Select(n => IsContent(n.Kind))],
            Links = links,
            Changes = changes,
            Groups = [.. cmp.Groups.Select(g => g.ChangeIds.ToArray())],
            Passed = cmp.Verification.Passed,
            FallbackLevel = cmp.Verification.FallbackLevel,
        };
    }

    /// <summary>
    /// Reads a comparison made from a rendered file: its content nodes are matched to the pair's gold blocks by text, in order, and
    /// every interval is carried over by counting the characters that matching ignores (see <see cref="Squash"/>). Nodes that match
    /// no gold block keep a place of their own, so they are counted as wrong rather than dropped.
    /// </summary>
    /// <param name="cmp">The comparison; its node ids are the source's own.</param>
    /// <param name="gold">The pair's block map (both sides).</param>
    /// <param name="stats">Receives how many content nodes were matched.</param>
    /// <param name="fuzzy">True for text that was recognized from pixels: nodes are matched to gold blocks by similarity and offsets are carried over by a character alignment (see <see cref="NodeMap"/>).</param>
    public static ScoreInput From(Comparison cmp, BlockMap gold, MapStats? stats = null, bool fuzzy = false)
    {
        NodeMap om = NodeMap.Build(cmp.Old.Nodes.Where(n => IsContent(n.Kind)).Select(n => (n.Id, n.Text)).ToList(), gold.Old, cmp.Old.Nodes.Length, fuzzy);
        NodeMap nm = NodeMap.Build(cmp.New.Nodes.Where(n => IsContent(n.Kind)).Select(n => (n.Id, n.Text)).ToList(), gold.New, cmp.New.Nodes.Length, fuzzy);
        if (stats is not null)
        {
            stats.Matched += om.Matched + nm.Matched;
            stats.Total += om.Total + nm.Total;
        }

        // Source nodes that are not content map to themselves above the gold ids, so nothing collides.
        ScoreInput plain = From(cmp);
        var links = new List<SLink>();
        foreach (SLink l in plain.Links)
        {
            links.Add(new SLink(l.Kind, [.. l.Old.Select(om.Id)], [.. l.New.Select(nm.Id)]));
        }

        var changes = new List<SChange>();
        foreach (SChange ch in plain.Changes)
        {
            var ivs = new List<Iv>();
            foreach (Iv v in ch.Extent)
            {
                NodeMap map = v.IsNew ? nm : om;
                ivs.Add(new Iv(v.IsNew, map.Id(v.Node), map.Position(v.Node, v.Start, atEnd: false), map.Position(v.Node, v.End, atEnd: true)));
            }

            changes.Add(new SChange(ch.Id, ch.Kind, ivs, ch.Flagged));
        }

        return new ScoreInput
        {
            OldText = om.GoldText,
            NewText = nm.GoldText,
            OldContent = om.GoldContent,
            NewContent = nm.GoldContent,
            Links = links,
            Changes = changes,
            Groups = plain.Groups,
            Passed = plain.Passed,
            FallbackLevel = plain.FallbackLevel,
        };
    }

    /// <summary>Maps a change side (a range of the nodes' joined text) back to intervals of single nodes.</summary>
    private static void Sides(ChangeSide side, bool isNew, CompareDocument doc, List<Iv> into)
    {
        if (!side.IsPresent)
        {
            return;
        }

        int offset = 0;
        int from = side.Start;
        int to = side.Start + side.Length;
        foreach (int id in side.Nodes)
        {
            string text = doc[id].Text;
            int lo = Math.Max(from, offset);
            int hi = Math.Min(to, offset + text.Length);
            if (hi > lo)
            {
                into.Add(new Iv(isNew, id, lo - offset, hi - offset));
            }

            offset += text.Length + 1;
        }
    }
}

/// <summary>Counts of content nodes matched to gold blocks, for the report.</summary>
internal sealed class MapStats
{
    public long Matched;
    public long Total;
}

/// <summary>One block of a rendered document: the gold node it renders, its text, and where it was found.</summary>
internal sealed record GoldBlock(int Node, string Kind, string Label, string Text, int Page = -1, int Paragraph = -1);

/// <param name="Blocks">The content blocks in document order.</param>
/// <param name="Paragraphs">Paragraphs in the rendered file (DOCX) or pages (PDF); informational.</param>
/// <param name="Located">How many blocks a re-reading of the rendered file found where the map says.</param>
/// <param name="Total">Number of blocks.</param>
/// <param name="LabelsOk">How many list labels computed from the rendered file equal the gold label (DOCX).</param>
/// <param name="LabelsTotal">Number of gold list labels (DOCX).</param>
internal sealed record BlockSide(List<GoldBlock> Blocks, int Paragraphs = 0, int Located = 0, int Total = 0, int LabelsOk = 0, int LabelsTotal = 0);

/// <summary>
/// A pair's block map, as written next to the rendered files (<c>&lt;id&gt;.blocks.json</c>): for each side the content blocks of the
/// gold document in order. PDF and DOCX renderings write the same shape.
/// </summary>
internal sealed record BlockMap(string Id, string Lang, BlockSide Old, BlockSide New, int Variant = 0)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static BlockMap Read(string path) =>
        JsonSerializer.Deserialize<BlockMap>(File.ReadAllBytes(path), Options) ?? throw new InvalidDataException("empty block map " + path);
}

/// <summary>
/// Text comparison that ignores what rendering changes: white space, soft hyphens and direction marks. A rendered file keeps the
/// same words in the same order and little else.
/// </summary>
internal static class Squash
{
    public static bool Ignored(char c) => char.IsWhiteSpace(c) || c is '\u00AD' or '\u200E' or '\u200F' or '\u200B';

    public static string Of(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (!Ignored(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>The number of kept characters before <paramref name="pos"/>.</summary>
    public static int Count(string s, int pos)
    {
        int n = 0;
        for (int i = 0; i < pos && i < s.Length; i++)
        {
            if (!Ignored(s[i]))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>The index in <paramref name="s"/> of its <paramref name="k"/>-th kept character (the length when there are fewer).</summary>
    public static int IndexOfKept(string s, int k)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (!Ignored(s[i]))
            {
                if (n == k)
                {
                    return i;
                }

                n++;
            }
        }

        return s.Length;
    }
}

/// <summary>
/// The assignment of a source's content nodes to gold blocks: the longest common subsequence of equal squashed texts, so order is
/// kept and repeated texts pair up in order. A gold block may carry its list label in the rendered text (a PDF prints it), so a node
/// also matches the label followed by the text.
/// </summary>
internal sealed class NodeMap
{
    private readonly int[] gold;
    private readonly int[] prefix;
    private readonly string[] sourceText;
    private readonly string[] goldTextByNode;
    private readonly int[]?[] alignment;

    private NodeMap(int[] gold, int[] prefix, string[] sourceText, string[] goldTextByNode, string[] goldText, bool[] goldContent, int matched, int total, int[]?[] alignment)
    {
        this.alignment = alignment;
        this.gold = gold;
        this.prefix = prefix;
        this.sourceText = sourceText;
        this.goldTextByNode = goldTextByNode;
        GoldText = goldText;
        GoldContent = goldContent;
        Matched = matched;
        Total = total;
    }

    /// <summary>Text of every node of the extended gold document: the gold nodes first, then one empty content node per unmatched source node.</summary>
    public string[] GoldText { get; }

    public bool[] GoldContent { get; }

    public int Matched { get; }

    public int Total { get; }

    /// <summary>The gold node id of a source node (an id past the gold nodes when it matched nothing).</summary>
    public int Id(int sourceNode) => gold[sourceNode];

    /// <summary>
    /// The offset in the gold text for an offset in the source text. A start lands on the kept character it precedes, an end just
    /// after the last kept character before it, so an interval keeps exactly the characters it covered.
    /// </summary>
    public int Position(int sourceNode, int pos, bool atEnd)
    {
        string src = sourceText[sourceNode];
        string g = goldTextByNode[sourceNode];
        int k = Squash.Count(src, pos) - prefix[sourceNode];
        if (k <= 0)
        {
            return 0;
        }

        // Recognized text: the k-th kept character of the source stands where the alignment puts it among the gold's.
        if (alignment[sourceNode] is int[] map)
        {
            k = map[Math.Min(k, map.Length - 1)];
            if (k <= 0)
            {
                return 0;
            }
        }

        return atEnd ? Math.Min(g.Length, Squash.IndexOfKept(g, k - 1) + 1) : Math.Min(g.Length, Squash.IndexOfKept(g, k));
    }

    /// <param name="source">The source's content nodes: id and text, in document order.</param>
    /// <param name="side">The gold blocks of that side.</param>
    /// <param name="sourceNodeCount">Number of nodes in the source document (ids are below this).</param>
    /// <param name="fuzzy">Match by similarity instead of equality and carry offsets over by a character alignment.</param>
    public static NodeMap Build(IReadOnlyList<(int Id, string Text)> source, BlockSide side, int sourceNodeCount, bool fuzzy = false)
    {
        if (fuzzy)
        {
            return BuildFuzzy(source, side, sourceNodeCount);
        }

        List<GoldBlock> blocks = side.Blocks;
        int goldCount = blocks.Count == 0 ? 0 : blocks.Max(b => b.Node) + 1;
        string[] s = [.. source.Select(x => Squash.Of(x.Text))];
        string[] plain = [.. blocks.Select(b => Squash.Of(b.Text))];
        string[] labelled = [.. blocks.Select(b => Squash.Of(b.Label + b.Text))];
        int n = s.Length, m = blocks.Count;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                bool eq = s[i].Length > 0 && (s[i] == plain[j] || s[i] == labelled[j]);
                lcs[i, j] = eq ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var assigned = new int[sourceNodeCount];
        var pref = new int[sourceNodeCount];
        var srcText = new string[sourceNodeCount];
        var goldByNode = new string[sourceNodeCount];
        Array.Fill(assigned, -1);
        Array.Fill(srcText, string.Empty);
        Array.Fill(goldByNode, string.Empty);
        int matched = 0;
        for (int i = 0, j = 0; i < n && j < m;)
        {
            bool eq = s[i].Length > 0 && (s[i] == plain[j] || s[i] == labelled[j]);
            if (eq && lcs[i, j] == lcs[i + 1, j + 1] + 1)
            {
                assigned[source[i].Id] = blocks[j].Node;
                pref[source[i].Id] = s[i] == plain[j] ? 0 : Squash.Of(blocks[j].Label).Length;
                goldByNode[source[i].Id] = blocks[j].Text;
                matched++;
                i++;
                j++;
            }
            else if (lcs[i + 1, j] >= lcs[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        // Unmatched content nodes get ids past the gold nodes; every other node keeps an id too, so lookups never fail.
        var text = new List<string>(Enumerable.Repeat(string.Empty, goldCount));
        var content = new List<bool>(Enumerable.Repeat(false, goldCount));
        foreach (GoldBlock b in blocks)
        {
            text[b.Node] = b.Text;
            content[b.Node] = true;
        }

        foreach ((int id, string t) in source)
        {
            srcText[id] = t;
            if (assigned[id] < 0)
            {
                assigned[id] = text.Count;
                text.Add(t);
                content.Add(true);
                goldByNode[id] = t;
            }
        }

        for (int id = 0; id < sourceNodeCount; id++)
        {
            if (assigned[id] < 0)
            {
                assigned[id] = text.Count;
                text.Add(string.Empty);
                content.Add(false);
            }
        }

        return new NodeMap(assigned, pref, srcText, goldByNode, [.. text], [.. content], matched, n, new int[]?[sourceNodeCount]);
    }

    /// <summary>
    /// The least similarity (Dice coefficient over the character pairs of the squashed texts) at which a recognized node is taken
    /// for a gold block. Recognition changes a few percent of the characters; a different block shares far fewer pairs.
    /// </summary>
    public const double MinSimilarity = 0.6;

    private static NodeMap BuildFuzzy(IReadOnlyList<(int Id, string Text)> source, BlockSide side, int sourceNodeCount)
    {
        List<GoldBlock> blocks = side.Blocks;
        int goldCount = blocks.Count == 0 ? 0 : blocks.Max(b => b.Node) + 1;
        string[] s = [.. source.Select(x => Squash.Of(x.Text))];
        string[] plain = [.. blocks.Select(b => Squash.Of(b.Text))];
        string[] labelled = [.. blocks.Select(b => Squash.Of(b.Label + b.Text))];
        int n = s.Length, m = blocks.Count;
        Dictionary<int, int>[] sp = [.. s.Select(Pairs)], pp = [.. plain.Select(Pairs)], lp = [.. labelled.Select(Pairs)];

        // sim[i, j]: the better of the plain and the labelled gold text; labelled[i, j] says which one.
        var sim = new double[n, m];
        var useLabel = new bool[n, m];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < m; j++)
            {
                if (s[i].Length == 0)
                {
                    continue;
                }

                double a = Dice(sp[i], s[i].Length, pp[j], plain[j].Length);
                double b = blocks[j].Label.Length > 0 ? Dice(sp[i], s[i].Length, lp[j], labelled[j].Length) : 0;
                sim[i, j] = Math.Max(a, b);
                useLabel[i, j] = b > a;
            }
        }

        // The order-preserving assignment of the greatest total similarity (each pair at least MinSimilarity).
        var best = new double[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                double v = Math.Max(best[i + 1, j], best[i, j + 1]);
                if (sim[i, j] >= MinSimilarity)
                {
                    v = Math.Max(v, best[i + 1, j + 1] + sim[i, j]);
                }

                best[i, j] = v;
            }
        }

        var assigned = new int[sourceNodeCount];
        var pref = new int[sourceNodeCount];
        var srcText = new string[sourceNodeCount];
        var goldByNode = new string[sourceNodeCount];
        var alignment = new int[]?[sourceNodeCount];
        Array.Fill(assigned, -1);
        Array.Fill(srcText, string.Empty);
        Array.Fill(goldByNode, string.Empty);
        int matched = 0;
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (sim[i, j] >= MinSimilarity && Math.Abs(best[i, j] - (best[i + 1, j + 1] + sim[i, j])) < 1e-12)
            {
                int id = source[i].Id;
                assigned[id] = blocks[j].Node;
                int label = useLabel[i, j] ? Squash.Of(blocks[j].Label).Length : 0;
                pref[id] = label;
                goldByNode[id] = blocks[j].Text;
                alignment[id] = s[i] == (useLabel[i, j] ? labelled[j] : plain[j]) ? null : CharMap(s[i][Math.Min(label, s[i].Length)..], plain[j]);
                matched++;
                i++;
                j++;
            }
            else if (best[i + 1, j] >= best[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        var text = new List<string>(Enumerable.Repeat(string.Empty, goldCount));
        var content = new List<bool>(Enumerable.Repeat(false, goldCount));
        foreach (GoldBlock b in blocks)
        {
            text[b.Node] = b.Text;
            content[b.Node] = true;
        }

        foreach ((int id, string t) in source)
        {
            srcText[id] = t;
            if (assigned[id] < 0)
            {
                assigned[id] = text.Count;
                text.Add(t);
                content.Add(true);
                goldByNode[id] = t;
            }
        }

        for (int id = 0; id < sourceNodeCount; id++)
        {
            if (assigned[id] < 0)
            {
                assigned[id] = text.Count;
                text.Add(string.Empty);
                content.Add(false);
            }
        }

        return new NodeMap(assigned, pref, srcText, goldByNode, [.. text], [.. content], matched, n, alignment);
    }

    private static Dictionary<int, int> Pairs(string t)
    {
        var d = new Dictionary<int, int>();
        for (int i = 0; i + 1 < t.Length; i++)
        {
            int key = (t[i] << 16) | t[i + 1];
            d[key] = d.GetValueOrDefault(key) + 1;
        }

        return d;
    }

    private static double Dice(Dictionary<int, int> a, int la, Dictionary<int, int> b, int lb)
    {
        if (la < 2 || lb < 2)
        {
            return 0;
        }

        // Texts whose lengths differ by more than a recognition error can are not the same block.
        if (Math.Min(la, lb) < 0.5 * Math.Max(la, lb))
        {
            return 0;
        }

        int shared = 0;
        foreach (KeyValuePair<int, int> kv in a)
        {
            if (b.TryGetValue(kv.Key, out int c))
            {
                shared += Math.Min(kv.Value, c);
            }
        }

        return 2.0 * shared / ((la - 1) + (lb - 1));
    }

    /// <summary>
    /// For each boundary k (0..n) of the recognized text, the boundary of the gold text it stands for: the minimal edit script
    /// between the two squashed texts is followed, so equal characters map to each other, characters the recognizer got wrong collapse
    /// onto the gold characters they replace, and characters it added or lost shift nothing.
    /// </summary>
    private static int[] CharMap(string recognized, string gold)
    {
        var ids = new Dictionary<char, int>();
        int Id(char c) => ids.TryGetValue(c, out int v) ? v : ids[c] = ids.Count;
        int[] a = [.. recognized.Select(Id)], b = [.. gold.Select(Id)];
        OneDiff.EditScript script = OneDiff.Algorithms.MyersDiff.Compute(a, b);
        var map = new int[recognized.Length + 1];
        foreach (OneDiff.Edit e in script)
        {
            switch (e.Kind)
            {
                case OneDiff.EditKind.Equal:
                    for (int t = 0; t < e.OldLength; t++)
                    {
                        map[e.OldStart + t] = e.NewStart + t;
                    }

                    break;
                case OneDiff.EditKind.Delete:
                    // Recognized characters with no gold counterpart: they all sit at the gold boundary where they were put.
                    for (int t = 0; t < e.OldLength; t++)
                    {
                        map[e.OldStart + t] = e.NewStart;
                    }

                    break;
                case OneDiff.EditKind.Insert:
                    // Gold characters the recognizer lost: the boundary stays before them until a matched character follows.
                    map[e.OldStart] = e.NewStart;
                    break;
            }
        }

        map[recognized.Length] = gold.Length;
        // A boundary that falls just before a run of gold characters the recognizer lost is placed after the last matched character.
        for (int k = recognized.Length - 1; k >= 0; k--)
        {
            if (map[k] > map[k + 1])
            {
                map[k] = map[k + 1];
            }
        }

        return map;
    }
}

/// <summary>An interval of one line of a tool's output view.</summary>
internal sealed record LineSpan(int Line, int Start, int Length);

/// <summary>One change of a tool's output: a run of adjacent revisions with no unchanged text between them.</summary>
internal sealed record ToolChange(int Id, string Kind, List<LineSpan> Old, List<LineSpan> New);

/// <summary>A link between lines of a tool's two views: kind is match, move, split or merge.</summary>
internal sealed record ToolLink(string Kind, int[] Old, int[] New);

/// <summary>
/// What the scorer needs from a tool that writes tracked changes (<c>&lt;id&gt;.changes.json</c>), read from its output file by
/// <c>OneDiff.GoldSets.Docx read</c>. The two views are the document with every revision rejected and with every revision accepted,
/// one line per non-empty paragraph.
/// </summary>
internal sealed record ToolOutput(
    string Schema, string Id, string Tool, List<string> OldLines, List<string> NewLines, List<ToolLink> Links, List<ToolChange> Changes)
{
    public const string SchemaName = "onediff.changes/1";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static ToolOutput Read(string path) =>
        JsonSerializer.Deserialize<ToolOutput>(File.ReadAllBytes(path), Options) ?? throw new InvalidDataException("empty changes file " + path);

    public static byte[] Write(ToolOutput value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>Maps the output into the gold documents' coordinates through the pair's block map. Every change is its own display group.</summary>
    public ScoreInput ToScoreInput(BlockMap gold, MapStats? stats = null)
    {
        NodeMap om = NodeMap.Build([.. OldLines.Select((t, i) => (i, t))], gold.Old, OldLines.Count);
        NodeMap nm = NodeMap.Build([.. NewLines.Select((t, i) => (i, t))], gold.New, NewLines.Count);
        if (stats is not null)
        {
            stats.Matched += om.Matched + nm.Matched;
            stats.Total += om.Total + nm.Total;
        }

        var changes = new List<SChange>();
        foreach (ToolChange ch in Changes)
        {
            var ivs = new List<Iv>();
            foreach (LineSpan s in ch.Old)
            {
                ivs.Add(new Iv(false, om.Id(s.Line), om.Position(s.Line, s.Start, false), om.Position(s.Line, s.Start + s.Length, true)));
            }

            foreach (LineSpan s in ch.New)
            {
                ivs.Add(new Iv(true, nm.Id(s.Line), nm.Position(s.Line, s.Start, false), nm.Position(s.Line, s.Start + s.Length, true)));
            }

            changes.Add(new SChange(ch.Id, ch.Kind == "move" ? SKind.Move : SKind.Text, ivs));
        }

        return new ScoreInput
        {
            OldText = om.GoldText,
            NewText = nm.GoldText,
            OldContent = om.GoldContent,
            NewContent = nm.GoldContent,
            Links = [.. Links.Select(l => new SLink(l.Kind, [.. l.Old.Select(om.Id)], [.. l.New.Select(nm.Id)]))],
            Changes = changes,
            Groups = [.. Changes.Select(c => new[] { c.Id })],
        };
    }
}
