using System.Collections.Immutable;
using System.Text;
using OneDiff.Documents.Model;
using RecursiveAI.OneDocument;
using RecursiveAI.OneDocument.Documents;
using RecursiveAI.OneDocument.Documents.Revisions;
using RecursiveAI.OneDocument.IO;
using RecursiveAI.OneDocument.Markup;

namespace OneDiff.Word.Internal;

/// <summary>One side of a tracked-changes write: a Word document opened from the bytes the Word source read, in the revision mode it read them in.</summary>
internal sealed class TrackedSide : IDisposable
{
    private readonly Dictionary<string, MarkupDocument> parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly MarkupNode[] elements;
    private readonly Dictionary<MarkupNode, int> nodeOfElement = [];

    public TrackedSide(CompareDocument model, ReadOnlyMemory<byte> bytes, WordRevisionMode mode)
    {
        Model = model;
        Doc = Document.Open(ByteSource.FromMemory(bytes));
        if (Doc.Revisions.Revisions.Count > 0)
        {
            if (mode == WordRevisionMode.AsOriginal)
            {
                Doc.Revisions.RejectAll();
            }
            else
            {
                Doc.Revisions.AcceptAll();
            }
        }

        foreach (IRevisionPart p in Doc.Revisions.Parts)
        {
            parts[p.Uri] = p.Markup;
        }

        Marker = Doc.Revisions.CreateAuthor(Wx.Marker, null);
        elements = new MarkupNode[model.Count];
        // Resolve every node before the first edit: references are positions, and an edit moves later elements.
        foreach (Node n in model.Nodes)
        {
            if (n.Anchor.Kind != AnchorKind.Node || !WordAnchors.TryParse(n.Anchor.Reference, out string part, out int[] path, out int off, out _) || off >= 0)
            {
                continue;
            }

            if (!parts.TryGetValue(part, out MarkupDocument? md) || md.Root.IsNull)
            {
                continue;
            }

            MarkupNode e = WordAnchors.Follow(md.Root, path);
            if (!e.IsNull)
            {
                elements[n.Id] = e;
                nodeOfElement.TryAdd(e, n.Id);
            }
        }
    }

    public CompareDocument Model { get; }

    /// <summary>Whether <paramref name="node"/> belongs to one of this document's parts.</summary>
    public bool Owns(MarkupNode node) => parts.Values.Any(p => ReferenceEquals(p, node.Document));

    private readonly Dictionary<string, string> numberingKeys = [];

    /// <summary>What a numbering number of this document stands for (its definition without ids); the number itself when it names nothing.</summary>
    public string NumberingKey(string numId)
    {
        if (numberingKeys.TryGetValue(numId, out string? known))
        {
            return known;
        }

        string key = numId;
        if (long.TryParse(numId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n) && n != 0
            && Doc.Numbering.FindInstance(n) is { } inst && Doc.Numbering.FindAbstract(inst.AbstractNumberingId) is { } abs)
        {
            key = TrackedImporter.NumberingSignature(abs.Markup.Node, inst.Markup.Node);
        }

        numberingKeys[numId] = key;
        return key;
    }

    public Document Doc { get; }

    public RevisionAuthor Marker { get; }

    public string Prefix => RevisionAuthor.Prefix(Doc.MainMarkup.Root);

    public IEnumerable<MarkupDocument> Parts => parts.Values;

    public IEnumerable<string> PartNames => parts.Keys;

    /// <summary>The name of the package part an element belongs to.</summary>
    public string PartNameOf(MarkupNode element)
    {
        foreach (KeyValuePair<string, MarkupDocument> kv in parts)
        {
            if (ReferenceEquals(kv.Value, element.Document))
            {
                return kv.Key;
            }
        }

        return Doc.MainPartName;
    }

    public MarkupNode Element(int nodeId) => elements[nodeId];

    public MarkupNode Root(string part) => parts.TryGetValue(part, out MarkupDocument? md) ? md.Root : default;

    /// <summary>The model node whose anchor element is <paramref name="element"/>, or -1.</summary>
    public int NodeOf(MarkupNode element) => !element.IsNull && nodeOfElement.TryGetValue(element, out int id) ? id : -1;

    /// <summary>Points a model node at a new element after the old one was replaced.</summary>
    public void Rebind(int nodeId, MarkupNode element)
    {
        nodeOfElement.Remove(elements[nodeId]);
        elements[nodeId] = element;
        nodeOfElement[element] = nodeId;
    }

    public void Dispose() => Doc.Dispose();
}
