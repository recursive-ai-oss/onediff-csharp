using System.Collections.Immutable;
using OneDiff.Documents.Model;

namespace OneDiff.Documents;

/// <summary>What happened to the content a <see cref="Change"/> describes.</summary>
public enum ChangeKind
{
    /// <summary>Content exists only in the new document: a whole node, or text inserted into a paired node.</summary>
    Insert = 0,

    /// <summary>Content exists only in the old document.</summary>
    Delete = 1,

    /// <summary>Text in a paired node was replaced by other text.</summary>
    Replace = 2,

    /// <summary>A node (with everything under it) is at a different place in the new document. Edits to its text are separate changes of the same link.</summary>
    Move = 3,

    /// <summary>One old node became several new nodes; the change carries the edits of the joined text.</summary>
    Split = 4,

    /// <summary>Several old nodes became one new node; the change carries the edits of the joined text.</summary>
    Merge = 5,

    /// <summary>Only character formatting of unchanged text differs.</summary>
    Format = 6,

    /// <summary>Block-level attributes (style, list label, hashes) differ.</summary>
    Attribute = 7,
}

/// <summary>Who decided a change's kind, type or grouping.</summary>
public enum DecidedBy
{
    /// <summary>Not classified yet.</summary>
    None = 0,

    /// <summary>A deterministic rule.</summary>
    Rule = 1,

    /// <summary>A model; the score says how sure it was.</summary>
    Model = 2,

    /// <summary>A person, by editing the alignment or the changes.</summary>
    User = 3,
}

/// <summary>Who decided about a change and how sure they were.</summary>
/// <param name="By">Rule, model or user; <see cref="DecidedBy.None"/> before classification.</param>
/// <param name="Source">The name of the rule, model or user that decided.</param>
/// <param name="Score">A score in [0, 1]; 1 for exact rules.</param>
public sealed record Decision(DecidedBy By, string Source, double Score)
{
    /// <summary>The state before classification.</summary>
    public static Decision Unclassified { get; } = new(DecidedBy.None, "", 0);
}

/// <summary>
/// One side of a change: which nodes it concerns, the range of their joined text, that text, and where it came from. A
/// side with no nodes is the absent side of an insertion or deletion.
/// </summary>
/// <param name="Nodes">The nodes of the document; several only for a split or merge.</param>
/// <param name="Start">The first character of the range in the nodes' joined text (nodes joined by U+001E, text as compared).</param>
/// <param name="Length">The length of the range.</param>
/// <param name="Text">The text of the range as compared.</param>
/// <param name="Anchor">The source location of the range; <see cref="Anchor.None"/> for an absent side.</param>
public sealed record ChangeSide(ImmutableArray<int> Nodes, int Start, int Length, string Text, Anchor Anchor)
{
    /// <summary>The absent side.</summary>
    public static ChangeSide Absent { get; } = new([], 0, 0, "", Anchor.None);

    /// <summary>Whether the side refers to any node.</summary>
    public bool IsPresent => !Nodes.IsDefaultOrEmpty;
}

/// <summary>
/// An edit inside a pair of nodes, in the coordinates of their joined compared text. Replaying the edits of a link on the old
/// text gives the new text, and replaying them backwards gives the old text again. A format-only edit has equal old and new text.
/// </summary>
/// <param name="OldStart">Where the edit starts in the old joined text.</param>
/// <param name="OldLength">How many old characters it covers.</param>
/// <param name="NewStart">Where it starts in the new joined text.</param>
/// <param name="NewLength">How many new characters it covers.</param>
/// <param name="OldText">The old characters.</param>
/// <param name="NewText">The new characters.</param>
/// <param name="OldRuns">The formatting of the old characters, relative to the edit.</param>
/// <param name="NewRuns">The formatting of the new characters, relative to the edit.</param>
public sealed record RegionEdit(
    int OldStart,
    int OldLength,
    int NewStart,
    int NewLength,
    string OldText,
    string NewText,
    ImmutableArray<FormatRun> OldRuns,
    ImmutableArray<FormatRun> NewRuns)
{
    /// <summary>One past the last old character.</summary>
    public int OldEnd => OldStart + OldLength;

    /// <summary>One past the last new character.</summary>
    public int NewEnd => NewStart + NewLength;

    /// <summary>Whether the text is the same and only formatting differs.</summary>
    public bool IsFormatOnly => OldLength > 0 && string.Equals(OldText, NewText, StringComparison.Ordinal);
}

/// <summary>A difference in one block-level attribute between an old and a new node of a link.</summary>
/// <param name="OldNode">The old node.</param>
/// <param name="NewNode">The new node.</param>
/// <param name="Key">The attribute key.</param>
/// <param name="OldValue">The old value, or null when the attribute was absent.</param>
/// <param name="NewValue">The new value, or null when the attribute is absent now.</param>
public sealed record AttributeEdit(int OldNode, int NewNode, string Key, string? OldValue, string? NewValue);

/// <summary>
/// One shown difference, atomic: a node inserted or deleted, a node moved, a region of text edited, a run of formatting or a set
/// of attributes changed. Every change carries both sides, both anchors and enough data to be replayed in either direction.
/// How changes are grouped for display lives apart, in <see cref="ChangeGroup"/>; grouping never alters a change.
/// </summary>
/// <param name="Id">The change's number in the comparison; unique.</param>
/// <param name="Kind">What happened.</param>
/// <param name="LinkId">The alignment link the change belongs to, or -1 for a node inserted or deleted whole.</param>
/// <param name="Old">The old side.</param>
/// <param name="New">The new side.</param>
/// <param name="Edits">Text and format edits of the link's joined text (empty for whole-node and move changes).</param>
/// <param name="AttributeEdits">Attribute edits (empty except for attribute changes and splits and merges).</param>
/// <param name="Decision">Who decided and with what score.</param>
/// <param name="Properties">Open slots a stage or a user may fill with their own key/value pairs; empty from the reference stages.</param>
public sealed record Change(
    int Id,
    ChangeKind Kind,
    int LinkId,
    ChangeSide Old,
    ChangeSide New,
    ImmutableArray<RegionEdit> Edits,
    ImmutableArray<AttributeEdit> AttributeEdits,
    Decision Decision,
    FormatSet Properties)
{
    /// <summary>
    /// What the change is about: a value, a name, a reference, a list label. <see cref="ChangeType.None"/> for an ordinary text change,
    /// and always for a comparison made without <see cref="CompareSettings.TypedChanges"/>. Set by <see cref="Stages.Classify(Pipeline.ChangeSet, CancellationToken)"/>.
    /// </summary>
    public ChangeType Type { get; init; }

    /// <summary>The old value in canonical form (<c>30 d</c>), or null when the change has no typed old value or the old text could not be resolved.</summary>
    public ResolvedValue? OldValue { get; init; }

    /// <summary>The new value in canonical form (<c>60 d</c>), or null when the change has no typed new value or the new text could not be resolved.</summary>
    public ResolvedValue? NewValue { get; init; }

    /// <summary>
    /// Whether the change alters what the document says or means. False for formatting, list labels and the references a renumbering
    /// caused, white-space and typography, empty paragraphs, other attributes, and splits and merges that change no words. True
    /// otherwise, and for a change nobody has classified yet. Filter with <c>comparison.Changes.Where(c =&gt; c.IsSubstantive)</c>.
    /// </summary>
    public bool IsSubstantive { get; init; } = true;
}

/// <summary>What a <see cref="ChangeGroup"/> collects.</summary>
public enum GroupKind
{
    /// <summary>One display region: the changes shown together as one.</summary>
    Region = 0,

    /// <summary>A renumbering and the references it caused: the changes of every list label that moved and every reference rewritten to follow. Display only; each change stays a change.</summary>
    Cascade = 1,

    /// <summary>A defined term renamed everywhere: the changes that replace the old term by the new one, in the definition and in every use. Display only; each occurrence stays a change.</summary>
    Rename = 2,

    /// <summary>A grouping a user or a later stage defined.</summary>
    Custom = 3,
}

/// <summary>
/// A display grouping over atomic changes. Every change belongs to exactly one group; groups hold change ids and no change
/// data, so removing the grouping never loses a change.
/// </summary>
/// <param name="Id">The group's number; unique.</param>
/// <param name="Kind">What the group is.</param>
/// <param name="Title">A short label, or empty.</param>
/// <param name="ChangeIds">The ids of the grouped changes, in comparison order.</param>
public sealed record ChangeGroup(int Id, GroupKind Kind, string Title, ImmutableArray<int> ChangeIds);
