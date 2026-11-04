using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.GoldSets;

/// <summary>One node of a stored document: nodes are listed in document order and a node's id is its index.</summary>
internal sealed record DocNode(string Kind, int Parent, string Text, SortedDictionary<string, string> Attrs, List<DocRun> Runs);

/// <summary>Character formatting over a node's text.</summary>
internal sealed record DocRun(int Start, int Length, SortedDictionary<string, string> Format);

/// <summary>A document in the gold set's own file form (<c>*.docs.json</c> holds an old and a new one).</summary>
internal sealed record DocFile(string Name, List<DocNode> Nodes);

/// <summary>The two documents of a pair.</summary>
internal sealed record PairDocs(string Id, string Lang, DocFile Old, DocFile New);

/// <summary>A character range of one node's text.</summary>
internal sealed record Span(int Node, int Start, int Len);

/// <summary>A typed value change recorded for later typing rows. Values are canonical strings, the same in every language.</summary>
internal sealed record Typed(string Kind, string Old, string New);

/// <summary>
/// One human-meaningful change. A unit with no spans is structural (a split, a merge, a renumbering): it counts in the
/// alignment rows and in the label file but not in the boundary rows.
/// </summary>
internal sealed record Unit(int Id, string Type, string Group, Typed? Typed, List<Span> Old, List<Span> New);

/// <summary>A correspondence of content blocks (paragraphs and list items) by node id. Kind is match, move, split or merge.</summary>
internal sealed record GoldLink(string Kind, List<int> Old, List<int> New);

/// <summary>A sentence that carries a change, on the new side or, for pure deletions, on the old side.</summary>
internal sealed record GoldSentence(string Side, int Node, int Start, int Len, List<int> Units);

/// <summary>The labels of one pair. Node ids are positions in <see cref="PairDocs"/>.</summary>
internal sealed record PairLabels(
    string Id,
    string Lang,
    int BaseDoc,
    string BaseHash,
    string Role,
    List<string> Edits,
    List<GoldLink> Links,
    List<int> Deleted,
    List<int> Inserted,
    List<Unit> Units,
    List<GoldSentence> Sentences);

/// <summary>The files of the gold set share one JSON form: compact, members in declaration order, so equal data gives equal bytes.</summary>
internal static class GoldJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Write<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Read<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Options) ?? throw new InvalidDataException("empty JSON");

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

    /// <summary>Builds a compare-model document from a stored one. Text is stored in its compared (normalized) form.</summary>
    /// <summary>Whether the old and the new document have the same nodes, text, attributes and formatting runs.</summary>
    public static bool SameNodes(PairDocs docs) =>
        JsonSerializer.SerializeToUtf8Bytes(docs.Old.Nodes, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(docs.New.Nodes, Options));

    public static CompareDocument Build(DocFile file)
    {
        var b = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(file.Name);
        var chain = new Stack<int>();
        for (int i = 0; i < file.Nodes.Count; i++)
        {
            DocNode n = file.Nodes[i];
            while (chain.Count > 0 && chain.Peek() != n.Parent)
            {
                chain.Pop();
                b.End();
            }

            var runs = new List<FormatRun>();
            foreach (DocRun r in n.Runs)
            {
                runs.Add(new FormatRun(r.Start, r.Length, FormatSet.Create(r.Format)));
            }

            var content = new NodeContent
            {
                Text = n.Text,
                Runs = [.. runs],
                Attributes = FormatSet.Create(n.Attrs),
            };
            bool hasChildren = i + 1 < file.Nodes.Count && file.Nodes[i + 1].Parent == i;
            if (hasChildren)
            {
                b.Begin(Enum.Parse<NodeKind>(n.Kind), content);
                chain.Push(i);
            }
            else
            {
                b.Add(Enum.Parse<NodeKind>(n.Kind), content);
            }
        }

        while (chain.Count > 0)
        {
            chain.Pop();
            b.End();
        }

        return b.Build();
    }
}
