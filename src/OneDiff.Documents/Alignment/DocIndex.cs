using System.Collections.Immutable;
using System.Text;
using OneDiff.Documents.Model;
using OneDiff.Tokenization;

namespace OneDiff.Documents.Pipeline;

/// <summary>
/// Dense ids shared by the two documents of one alignment, so equal things get equal numbers. Ids are handed out in the order
/// things are first met, which makes them a function of the inputs alone.
/// </summary>
internal sealed class Interners
{
    private readonly Dictionary<string, int> texts = new(StringComparer.Ordinal);
    private readonly Dictionary<(byte Kind, int Text, int Extra), int> keys = [];
    private readonly Dictionary<int, int> leaves = [];
    private readonly Dictionary<string, int> trees = new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> placed = [];
    private readonly Dictionary<string, int> words = new(StringComparer.Ordinal);
    private int treeCount;

    public int Text(string s) => Intern(texts, s);

    public int Key(NodeKind kind, int text, int extra)
    {
        var k = ((byte)kind, text, extra);
        if (!keys.TryGetValue(k, out int id))
        {
            id = keys.Count;
            keys[k] = id;
        }

        return id;
    }

    /// <summary>The id of a subtree with this root key and these child subtrees.</summary>
    public int Tree(int key, List<int> childTrees)
    {
        if (childTrees.Count == 0)
        {
            if (!leaves.TryGetValue(key, out int leaf))
            {
                leaf = treeCount++;
                leaves[key] = leaf;
            }

            return leaf;
        }

        var sb = new StringBuilder();
        sb.Append(key);
        foreach (int c in childTrees)
        {
            sb.Append(',').Append(c);
        }

        string s = sb.ToString();
        if (!trees.TryGetValue(s, out int id))
        {
            id = treeCount++;
            trees[s] = id;
        }

        return id;
    }

    /// <summary>A subtree id together with the depth it sits at.</summary>
    public int Placed(int tree, int depth)
    {
        long k = ((long)tree << 20) | (uint)Math.Min(depth, (1 << 20) - 1);
        if (!placed.TryGetValue(k, out int id))
        {
            id = placed.Count;
            placed[k] = id;
        }

        return id;
    }

    public int Word(ReadOnlySpan<char> w)
    {
        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup = words.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(w, out int id))
        {
            return id;
        }

        id = words.Count;
        words[w.ToString()] = id;
        return id;
    }

    private static int Intern(Dictionary<string, int> map, string s)
    {
        if (!map.TryGetValue(s, out int id))
        {
            id = map.Count;
            map[s] = id;
        }

        return id;
    }
}

/// <summary>
/// What the alignment needs to know about every node of one document, computed once: the compared text, the words and word-pair
/// shingles of text blocks, a hash of the whole subtree (so equal subtrees are equal numbers), sizes and sibling links.
/// All arrays are indexed by node id and linear in the document.
/// </summary>
internal sealed class DocIndex
{
    private DocIndex(CompareDocument doc)
    {
        Doc = doc;
        int n = doc.Count;
        Text = new string[n];
        Words = new int[n][];
        Shingles = new int[n][];
        Tree = new int[n];
        Placed = new int[n];
        Size = new int[n];
        Content = new int[n];
        Pos = new int[n];
        Prev = new int[n];
        Next = new int[n];
        IsContainer = new bool[n];
    }

    public CompareDocument Doc { get; }

    /// <summary>The compared text of each node under the settings.</summary>
    public string[] Text { get; }

    /// <summary>Sorted word ids of each text block (empty for nodes without words).</summary>
    public int[][] Words { get; }

    /// <summary>Sorted distinct hashes of the word pairs of each text block, the unit candidates are found by.</summary>
    public int[][] Shingles { get; }

    /// <summary>The id of each node's subtree: equal ids mean equal kind, text, content hash and child subtrees.</summary>
    public int[] Tree { get; }

    /// <summary>Subtree id with depth, the token of the exact sequence match.</summary>
    public int[] Placed { get; }

    /// <summary>Number of nodes in each node's subtree, itself included; the subtree is the id range [id, id + Size).</summary>
    public int[] Size { get; }

    /// <summary>Number of text-bearing nodes and images in each subtree.</summary>
    public int[] Content { get; }

    /// <summary>Index of each node among its siblings (roots count as siblings).</summary>
    public int[] Pos { get; }

    /// <summary>The previous sibling, or -1.</summary>
    public int[] Prev { get; }

    /// <summary>The next sibling, or -1.</summary>
    public int[] Next { get; }

    /// <summary>Whether a node holds no text of its own and groups others (a table, a row, a section, a note).</summary>
    public bool[] IsContainer { get; }

    public bool HasText(int id) => Text[id].Length > 0;

    public bool IsContent(int id) => Text[id].Length > 0 || Doc[id].Kind == NodeKind.Image;

    public static DocIndex Build(CompareDocument doc, CompareSettings settings, Interners interners, CancellationToken ct)
    {
        var ix = new DocIndex(doc);
        var ranges = new List<TokenRange>();
        var ordered = new List<int>();
        for (int i = 0; i < doc.Count; i++)
        {
            if ((i & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            Node node = doc[i];
            string text = ProjectedGroup.TextOf(doc, node, settings);
            ix.Text[i] = text;
            ix.Words[i] = [];
            ix.Shingles[i] = [];
            if (text.Length > 0)
            {
                ranges.Clear();
                ordered.Clear();
                Tokenizer.Tokenize(text, TokenKind.Word, ranges);
                foreach (TokenRange t in ranges)
                {
                    if (char.IsLetterOrDigit(text[t.Start]))
                    {
                        ordered.Add(interners.Word(text.AsSpan(t.Start, t.Length)));
                    }
                }

                if (ordered.Count > 0)
                {
                    ix.Shingles[i] = ShinglesOf(ordered);
                    int[] sorted = [.. ordered];
                    Array.Sort(sorted);
                    ix.Words[i] = sorted;
                }
            }
        }

        // Children have larger ids than their parents, so walking ids downwards sees every child before its parent.
        var childTrees = new List<int>();
        for (int i = doc.Count - 1; i >= 0; i--)
        {
            Node node = doc[i];
            ImmutableArray<int> children = doc.ChildrenOf(i);
            string extra = node.Kind == NodeKind.Property
                ? node.Attributes.Get(FormatKeys.PropertyName) ?? ""
                : node.Attributes.Get(FormatKeys.ContentHash) ?? "";
            int key = interners.Key(node.Kind, interners.Text(ix.Text[i]), interners.Text(extra));
            childTrees.Clear();
            int size = 1, content = ix.IsContent(i) ? 1 : 0;
            foreach (int c in children)
            {
                childTrees.Add(ix.Tree[c]);
                size += ix.Size[c];
                content += ix.Content[c];
            }

            ix.Tree[i] = interners.Tree(key, childTrees);
            ix.Placed[i] = interners.Placed(ix.Tree[i], node.Depth);
            ix.Size[i] = size;
            ix.Content[i] = content;
            ix.IsContainer[i] = ix.Text[i].Length == 0 && node.Kind != NodeKind.Image && (children.Length > 0 || IsStructural(node.Kind));
        }

        LinkSiblings(ix, doc.Roots);
        for (int i = 0; i < doc.Count; i++)
        {
            LinkSiblings(ix, doc.ChildrenOf(i));
        }

        return ix;
    }

    private static void LinkSiblings(DocIndex ix, ImmutableArray<int> ids)
    {
        for (int k = 0; k < ids.Length; k++)
        {
            ix.Pos[ids[k]] = k;
            ix.Prev[ids[k]] = k > 0 ? ids[k - 1] : -1;
            ix.Next[ids[k]] = k + 1 < ids.Length ? ids[k + 1] : -1;
        }
    }

    private static bool IsStructural(NodeKind kind) => kind is
        NodeKind.Section or NodeKind.Table or NodeKind.TableRow or NodeKind.TableCell or NodeKind.Footnote or NodeKind.Endnote
        or NodeKind.Header or NodeKind.Footer or NodeKind.Comment or NodeKind.TextBox or NodeKind.ContentControl;

    /// <summary>
    /// Shingles of a text: hashes of neighbouring word pairs, plus the single words of very short texts (which have few or no
    /// pairs). Two texts that share most of their words in order share most of their shingles; that is what makes candidates
    /// findable without comparing every pair of blocks.
    /// </summary>
    private static int[] ShinglesOf(List<int> words)
    {
        var set = new List<int>(words.Count + 1);
        if (words.Count <= 3)
        {
            foreach (int w in words)
            {
                set.Add(Mix((ulong)w * 0x9E3779B97F4A7C15UL + 1));
            }
        }

        for (int k = 0; k + 1 < words.Count; k++)
        {
            set.Add(Mix(((ulong)words[k] << 32) ^ (uint)words[k + 1] ^ 0xC2B2AE3D27D4EB4FUL));
        }

        int[] result = [.. set];
        Array.Sort(result);
        int n = 0;
        for (int k = 0; k < result.Length; k++)
        {
            if (k == 0 || result[k] != result[k - 1])
            {
                result[n++] = result[k];
            }
        }

        return result.AsSpan(0, n).ToArray();
    }

    /// <summary>SplitMix64 finalizer folded to 32 bits: a fixed, platform-independent hash.</summary>
    private static int Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (int)(x ^ (x >> 32));
    }
}
