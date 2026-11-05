using System.Collections.Immutable;
using System.Text;

namespace OneDiff.Documents.Model;

/// <summary>
/// What a node of the compare model is. The flowing kinds are complete; slides and grids will add kinds of their own
/// (slide, shape, sheet, row, column, cell) to this enum and use the same <see cref="Node"/> shape, with geometry and formulas
/// as attributes and cell anchors as sources.
/// </summary>
public enum NodeKind : byte
{
    /// <summary>A section: page setup scope and top-level container of body blocks.</summary>
    Section = 0,

    /// <summary>A paragraph of text.</summary>
    Paragraph = 1,

    /// <summary>A paragraph that is a list item; its resolved label is the <see cref="FormatKeys.ListLabel"/> attribute.</summary>
    ListItem = 2,

    /// <summary>A table; its children are rows.</summary>
    Table = 3,

    /// <summary>A table row; its children are cells.</summary>
    TableRow = 4,

    /// <summary>A table cell; its children are blocks.</summary>
    TableCell = 5,

    /// <summary>A footnote; its children are blocks.</summary>
    Footnote = 6,

    /// <summary>An endnote; its children are blocks.</summary>
    Endnote = 7,

    /// <summary>A header of a section; its children are blocks.</summary>
    Header = 8,

    /// <summary>A footer of a section; its children are blocks.</summary>
    Footer = 9,

    /// <summary>A comment; its children are blocks. Comments can be hidden into the ledger.</summary>
    Comment = 10,

    /// <summary>A field inside a paragraph: the code is the <see cref="FormatKeys.FieldCode"/> attribute, the result is the text.</summary>
    Field = 11,

    /// <summary>An image: identified by <see cref="FormatKeys.ContentHash"/> and, when known, <see cref="FormatKeys.PerceptualHash"/>.</summary>
    Image = 12,

    /// <summary>A text box; its children are blocks.</summary>
    TextBox = 13,

    /// <summary>A content control; its children are blocks, or its text is the control's value.</summary>
    ContentControl = 14,

    /// <summary>A document property: the name is the <see cref="FormatKeys.PropertyName"/> attribute, the value is the text. Properties can be hidden into the ledger.</summary>
    Property = 15,
}

/// <summary>Which family of documents a compare model describes.</summary>
public enum DocumentKind : byte
{
    /// <summary>Flowing text: sections, paragraphs, tables, notes (word-processing documents, PDF, plain text).</summary>
    Flowing = 0,

    /// <summary>Slides: slides, shapes, text bodies. Reserved; no node kinds yet.</summary>
    Slides = 1,

    /// <summary>Grids: sheets, rows, columns, cells. Reserved; no node kinds yet.</summary>
    Grid = 2,
}

/// <summary>
/// One node of a compare model's block tree. Nodes are numbered in document order starting at 0 (pre-order), so a node's id is
/// also its position and a parent always has a smaller id than its children. A node carries normalized text, formatting runs
/// over that text, block-level attributes, the anchor of its source, and the normalization folds that say how the text differs
/// from the source. Immutable.
/// </summary>
public sealed class Node
{
    internal Node(
        int id,
        int parent,
        int depth,
        NodeKind kind,
        string text,
        ImmutableArray<FormatRun> runs,
        FormatSet attributes,
        Anchor anchor,
        ImmutableArray<AnchoredSpan> segments,
        ImmutableArray<NormalizationFold> folds)
    {
        Id = id;
        Parent = parent;
        Depth = depth;
        Kind = kind;
        Text = text;
        Runs = runs;
        Attributes = attributes;
        Anchor = anchor;
        Segments = segments;
        Folds = folds;
    }

    /// <summary>The node's number: its position in document order.</summary>
    public int Id { get; }

    /// <summary>The parent's id, or -1 for a root.</summary>
    public int Parent { get; }

    /// <summary>The distance from a root (roots are 0).</summary>
    public int Depth { get; }

    /// <summary>What the node is.</summary>
    public NodeKind Kind { get; }

    /// <summary>The normalized text the node's own content is compared on; empty for pure containers. Never contains U+001E.</summary>
    public string Text { get; }

    /// <summary>Character formatting over <see cref="Text"/>: sorted, non-overlapping, merged, never empty-format.</summary>
    public ImmutableArray<FormatRun> Runs { get; }

    /// <summary>Block-level attributes (paragraph style, list label, table shape, hashes).</summary>
    public FormatSet Attributes { get; }

    /// <summary>Where the node as a whole came from.</summary>
    public Anchor Anchor { get; }

    /// <summary>Anchors for pieces of the text (a line, a word), sorted by start; empty when only the node anchor is known.</summary>
    public ImmutableArray<AnchoredSpan> Segments { get; }

    /// <summary>What normalization changed, in text order.</summary>
    public ImmutableArray<NormalizationFold> Folds { get; }

    /// <summary>The text as the source had it: <see cref="Text"/> with every fold undone.</summary>
    public string RawText
    {
        get
        {
            if (Folds.IsDefaultOrEmpty)
            {
                return Text;
            }

            var sb = new StringBuilder(Text.Length + 16);
            int pos = 0;
            foreach (NormalizationFold f in Folds)
            {
                sb.Append(Text, pos, f.Start - pos);
                sb.Append(f.Original);
                pos = f.Start + f.Length;
            }

            sb.Append(Text, pos, Text.Length - pos);
            return sb.ToString();
        }
    }
}

/// <summary>A part of the document the compare model does not itemize (OLE internals, custom XML, unknown parts), known by a content hash.</summary>
/// <param name="Name">A stable name for the part, for example its package path.</param>
/// <param name="Hash">A hex content hash (SHA-256 is expected).</param>
/// <param name="Anchor">Where the part lives in the source.</param>
public sealed record OpaquePart(string Name, string Hash, Anchor Anchor);

/// <summary>The content of a node being added to a <see cref="CompareDocumentBuilder"/>.</summary>
public sealed record NodeContent
{
    /// <summary>The empty content.</summary>
    public static NodeContent Empty { get; } = new();

    /// <summary>The normalized text; must not contain U+001E.</summary>
    public string Text { get; init; } = "";

    /// <summary>Formatting runs; sorted, merged and checked by the builder.</summary>
    public ImmutableArray<FormatRun> Runs { get; init; }

    /// <summary>Block-level attributes.</summary>
    public FormatSet? Attributes { get; init; }

    /// <summary>The node's anchor; absent when null.</summary>
    public Anchor? Anchor { get; init; }

    /// <summary>Anchors for pieces of the text.</summary>
    public ImmutableArray<AnchoredSpan> Segments { get; init; }

    /// <summary>Normalization folds, sorted, non-overlapping, inside the text.</summary>
    public ImmutableArray<NormalizationFold> Folds { get; init; }

    /// <summary>Content for text that went through <see cref="TextNormalization"/>: the text and its folds.</summary>
    public static NodeContent FromNormalized(NormalizedText normalized, Anchor? anchor = null) =>
        new() { Text = normalized.Text, Folds = normalized.Folds, Anchor = anchor };
}

/// <summary>
/// An immutable compare model: a forest of <see cref="Node"/>s in document order, plus the opaque parts the model does not
/// itemize, plus identifying metadata. Build one with <see cref="CompareDocumentBuilder"/>, from a source with
/// <see cref="From(DocumentInput, IDocumentSource?, CancellationToken)"/>, or from plain text with <see cref="FromText"/>.
/// </summary>
public sealed class CompareDocument
{
    private readonly ImmutableArray<int>[] children;

    internal CompareDocument(DocumentKind kind, string name, string sourceHash, string sourceFormat, ImmutableArray<Node> nodes, ImmutableArray<OpaquePart> opaque)
    {
        Kind = kind;
        Name = name;
        SourceHash = sourceHash;
        SourceFormat = sourceFormat;
        Nodes = nodes;
        Opaque = opaque;
        var lists = new List<int>[nodes.Length];
        var roots = new List<int>();
        foreach (Node n in nodes)
        {
            if (n.Parent < 0)
            {
                roots.Add(n.Id);
            }
            else
            {
                (lists[n.Parent] ??= []).Add(n.Id);
            }
        }

        children = new ImmutableArray<int>[nodes.Length];
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = lists[i] is null ? [] : [.. lists[i]];
        }

        Roots = [.. roots];
    }

    /// <summary>Which family of documents this is.</summary>
    public DocumentKind Kind { get; }

    /// <summary>A display name (a file name or caption); never used to decide anything.</summary>
    public string Name { get; }

    /// <summary>A hex SHA-256 of the input bytes the source read, or empty when the document was built by hand.</summary>
    public string SourceHash { get; }

    /// <summary>A media type or short format name the source reported, such as <c>text/plain</c>.</summary>
    public string SourceFormat { get; }

    /// <summary>All nodes in document order; <c>Nodes[i].Id == i</c>.</summary>
    public ImmutableArray<Node> Nodes { get; }

    /// <summary>Parts outside the model, reported as "differs, not itemized" from their hashes.</summary>
    public ImmutableArray<OpaquePart> Opaque { get; }

    /// <summary>The ids of the nodes without a parent, in order.</summary>
    public ImmutableArray<int> Roots { get; }

    /// <summary>The number of nodes.</summary>
    public int Count => Nodes.Length;

    /// <summary>The node with the given id.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is not a node of this document.</exception>
    public Node this[int id]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(id);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, Nodes.Length);
            return Nodes[id];
        }
    }

    /// <summary>The ids of a node's children in order.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is not a node of this document.</exception>
    public ImmutableArray<int> ChildrenOf(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, Nodes.Length);
        return children[id];
    }

    /// <summary>Reads a document from an input. With no explicit source, the built-in sources are tried by content.</summary>
    /// <exception cref="NotSupportedException">No source recognizes the input.</exception>
    public static CompareDocument From(DocumentInput input, IDocumentSource? source = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        IDocumentSource s = source ?? DocumentSources.Sniff(input);
        return s.Read(input, cancellationToken);
    }

    /// <summary>Reads a document from plain text with the built-in plain-text source.</summary>
    public static CompareDocument FromText(string text, string name = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        return PlainTextSource.Instance.Read(DocumentInput.FromString(text, name), CancellationToken.None);
    }
}

/// <summary>
/// Builds a <see cref="CompareDocument"/> node by node, in document order. <see cref="Begin"/> opens a container,
/// <see cref="Add"/> adds a leaf or a node whose children you will not add, <see cref="End"/> closes. Not thread-safe.
/// </summary>
public sealed class CompareDocumentBuilder
{
    private readonly List<Node> nodes = [];
    private readonly List<OpaquePart> opaque = [];
    private readonly List<int> path = [];
    private readonly Stack<int> open = new();
    private readonly DocumentKind kind;

    /// <summary>
    /// The most containers that may be open at once (256). Real documents nest a few levels; the limit keeps the writers that walk
    /// the tree recursively within the call stack of a small host such as a browser.
    /// </summary>
    public const int MaxDepth = 256;

    private string name = "";
    private string sourceHash = "";
    private string sourceFormat = "";

    /// <summary>Starts a document of the given kind.</summary>
    public CompareDocumentBuilder(DocumentKind kind = DocumentKind.Flowing)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        this.kind = kind;
    }

    /// <summary>Sets the display name, source hash and format that the finished document reports.</summary>
    public CompareDocumentBuilder WithMetadata(string name, string sourceHash = "", string sourceFormat = "")
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(sourceHash);
        ArgumentNullException.ThrowIfNull(sourceFormat);
        this.name = WellFormed.Check(name, "the document name");
        this.sourceHash = WellFormed.Check(sourceHash, "the source hash");
        this.sourceFormat = WellFormed.Check(sourceFormat, "the source format");
        return this;
    }

    /// <summary>Adds a node that has children; close it with <see cref="End"/>.</summary>
    /// <returns>The node's id.</returns>
    /// <exception cref="ArgumentException">The content is invalid (see <see cref="NodeContent"/>).</exception>
    /// <exception cref="InvalidOperationException"><see cref="MaxDepth"/> containers are already open.</exception>
    public int Begin(NodeKind kind, NodeContent? content = null)
    {
        if (open.Count >= MaxDepth)
        {
            throw new InvalidOperationException("documents nest at most " + MaxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture) + " containers deep");
        }

        int id = Append(open.Count > 0 ? open.Peek() : -1, kind, content ?? NodeContent.Empty);
        open.Push(id);
        return id;
    }

    /// <summary>Closes the container opened last.</summary>
    /// <exception cref="InvalidOperationException">No container is open.</exception>
    public void End()
    {
        if (open.Count == 0)
        {
            throw new InvalidOperationException("no container is open");
        }

        open.Pop();
    }

    /// <summary>Adds a node under the open container (or as a root).</summary>
    /// <returns>The node's id.</returns>
    /// <exception cref="ArgumentException">The content is invalid.</exception>
    public int Add(NodeKind kind, NodeContent? content = null) => Append(open.Count > 0 ? open.Peek() : -1, kind, content ?? NodeContent.Empty);

    /// <summary>Records a part the model does not itemize.</summary>
    /// <exception cref="ArgumentException">The name or hash is empty.</exception>
    public void AddOpaque(string name, string hash, Anchor? anchor = null)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(hash))
        {
            throw new ArgumentException("an opaque part needs a name and a hash");
        }

        opaque.Add(new OpaquePart(WellFormed.Check(name, "an opaque part name"), WellFormed.Check(hash, "an opaque part hash"), anchor ?? Anchor.None));
    }

    /// <summary>Finishes the document.</summary>
    /// <exception cref="InvalidOperationException">A container is still open.</exception>
    public CompareDocument Build()
    {
        if (open.Count != 0)
        {
            throw new InvalidOperationException("a container is still open");
        }

        return new CompareDocument(kind, name, sourceHash, sourceFormat, [.. nodes], [.. opaque]);
    }

    /// <summary>Appends a node under <paramref name="parent"/>, which must be the last node or one of its ancestors (or -1).</summary>
    internal int Append(int parent, NodeKind nodeKind, NodeContent content)
    {
        if (!Enum.IsDefined(nodeKind))
        {
            throw new ArgumentOutOfRangeException(nameof(nodeKind));
        }

        while (path.Count > 0 && path[^1] != parent)
        {
            path.RemoveAt(path.Count - 1);
        }

        if (parent >= 0 && path.Count == 0)
        {
            throw new ArgumentException($"parent {parent} is not the previous node or one of its ancestors");
        }

        string text = content.Text ?? throw new ArgumentException("node text cannot be null");
        WellFormed.Check(text, "node text");
        if (text.Contains('\u001E', StringComparison.Ordinal))
        {
            throw new ArgumentException("node text cannot contain U+001E; normalize it away (see ControlCharacterNormalizer)");
        }

        ImmutableArray<FormatRun> runs = FormatRuns.Canonicalize(content.Runs.IsDefault ? null : content.Runs, text.Length);
        CheckFolds(content.Folds, text.Length);
        ImmutableArray<AnchoredSpan> segments = CheckSegments(content.Segments, text.Length);
        int id = nodes.Count;
        int depth = parent < 0 ? 0 : nodes[parent].Depth + 1;
        nodes.Add(new Node(
            id,
            parent,
            depth,
            nodeKind,
            text,
            runs,
            content.Attributes ?? FormatSet.Empty,
            content.Anchor ?? Anchor.None,
            segments,
            content.Folds.IsDefault ? [] : content.Folds));
        path.Add(id);
        return id;
    }

    private static void CheckFolds(ImmutableArray<NormalizationFold> folds, int textLength)
    {
        if (folds.IsDefaultOrEmpty)
        {
            return;
        }

        int pos = 0;
        foreach (NormalizationFold f in folds)
        {
            if (f.Original is null || f.Original.Length == 0 || f.Start < pos || f.Length < 0 || f.Start > textLength || f.Length > textLength - f.Start
                || (int)f.Category < 0 || (int)f.Category >= LedgerCategoryNames.Count || !WellFormed.IsValid(f.Original))
            {
                throw new ArgumentException("normalization folds must be sorted, non-overlapping, inside the text and have an original");
            }

            pos = f.Start + f.Length;
        }
    }

    private static ImmutableArray<AnchoredSpan> CheckSegments(ImmutableArray<AnchoredSpan> segments, int textLength)
    {
        if (segments.IsDefaultOrEmpty)
        {
            return [];
        }

        int last = 0;
        foreach (AnchoredSpan s in segments)
        {
            if (s.Anchor is null || s.Start < last || s.Length < 0 || s.Start > textLength || s.Length > textLength - s.Start)
            {
                throw new ArgumentException("anchored spans must be sorted by start, inside the text and carry an anchor");
            }

            last = s.Start;
        }

        return segments;
    }
}
