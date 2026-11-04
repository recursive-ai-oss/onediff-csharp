using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Serialization;

/// <summary>Stable names for the enums in the JSON form, as explicit tables (no reflection, no <c>Enum.ToString</c>).</summary>
internal sealed class EnumNames<T>
    where T : struct
{
    private readonly string[] names;
    private readonly Func<T, int> toInt;
    private readonly Func<int, T> fromInt;

    public EnumNames(string[] names, Func<T, int> toInt, Func<int, T> fromInt)
    {
        this.names = names;
        this.toInt = toInt;
        this.fromInt = fromInt;
    }

    public string Name(T value)
    {
        int i = toInt(value);
        if (i < 0 || i >= names.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return names[i];
    }

    public bool TryParse(string? name, out T value)
    {
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                value = fromInt(i);
                return true;
            }
        }

        value = default;
        return false;
    }
}

internal static class JsonNames
{
    public static readonly EnumNames<ChangeKind> ChangeKinds = new(
        ["insert", "delete", "replace", "move", "split", "merge", "format", "attribute"], static v => (int)v, static i => (ChangeKind)i);

    public static readonly EnumNames<DecidedBy> DecidedBys = new(["none", "rule", "model", "user"], static v => (int)v, static i => (DecidedBy)i);

    public static readonly EnumNames<GroupKind> GroupKinds = new(["region", "cascade", "rename", "custom"], static v => (int)v, static i => (GroupKind)i);

    public static readonly EnumNames<LinkKind> LinkKinds = new(["match", "move", "split", "merge"], static v => (int)v, static i => (LinkKind)i);

    public static readonly EnumNames<LinkOrigin> LinkOrigins = new(
        ["exact", "similar", "relocated", "splitMerge", "user", "fallback"], static v => (int)v, static i => (LinkOrigin)i);

    public static readonly EnumNames<NodeKind> NodeKinds = new(
        [
            "section", "paragraph", "listItem", "table", "tableRow", "tableCell", "footnote", "endnote", "header", "footer", "comment",
            "field", "image", "textBox", "contentControl", "property",
        ],
        static v => (int)v,
        static i => (NodeKind)i);

    public static readonly EnumNames<DocumentKind> DocumentKinds = new(["flowing", "slides", "grid"], static v => (int)v, static i => (DocumentKind)i);

    public static readonly EnumNames<AnchorKind> AnchorKinds = new(
        ["none", "node", "pdfQuads", "ocrWords", "cell", "textSpan"], static v => (int)v, static i => (AnchorKind)i);

    public static readonly EnumNames<DiagnosticSeverity> Severities = new(["info", "warning", "error"], static v => (int)v, static i => (DiagnosticSeverity)i);

    public static readonly EnumNames<MarkDecoration> Decorations = new(
        ["none", "underline", "doubleUnderline", "strikeThrough", "doubleStrikeThrough", "highlight"], static v => (int)v, static i => (MarkDecoration)i);

    public static readonly EnumNames<DiffAlgorithm> Algorithms = new(["myers", "patience", "histogram"], static v => (int)v, static i => (DiffAlgorithm)i);
}
