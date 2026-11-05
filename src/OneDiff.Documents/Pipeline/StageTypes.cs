using System.Collections.Immutable;
using OneDiff.Documents.Ledger;

namespace OneDiff.Documents.Pipeline;

/// <summary>The edits found inside one alignment link.</summary>
/// <param name="LinkId">The link.</param>
/// <param name="Edits">Text and format edits of the link's joined text, sorted by old start.</param>
/// <param name="AttributeEdits">Attribute differences.</param>
public sealed record LinkDiff(int LinkId, ImmutableArray<RegionEdit> Edits, ImmutableArray<AttributeEdit> AttributeEdits);

/// <summary>The output of <see cref="Stages.DiffPairs"/>: the alignment, the edits inside each link and the ledger so far. Immutable.</summary>
public sealed class PairDiffs
{
    /// <summary>Wraps stage output; use this to build a value from a custom stage.</summary>
    /// <param name="alignment">The alignment the diffs belong to.</param>
    /// <param name="links">One <see cref="LinkDiff"/> per link of the alignment, indexed by link id.</param>
    /// <param name="ledger">The hidden differences found.</param>
    /// <param name="diagnostics">All diagnostics so far.</param>
    /// <exception cref="ArgumentException">The diffs do not match the alignment's links.</exception>
    public PairDiffs(Alignment alignment, ImmutableArray<LinkDiff> links, ChangeLedger ledger, ImmutableArray<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(ledger);
        if (links.IsDefault || links.Length != alignment.Links.Length)
        {
            throw new ArgumentException("there must be one LinkDiff per alignment link", nameof(links));
        }

        for (int i = 0; i < links.Length; i++)
        {
            if (links[i].LinkId != i)
            {
                throw new ArgumentException("link diffs must be indexed by link id", nameof(links));
            }
        }

        Alignment = alignment;
        Links = links;
        Ledger = ledger;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>The alignment.</summary>
    public Alignment Alignment { get; }

    /// <summary>The diff of each link, indexed by link id.</summary>
    public ImmutableArray<LinkDiff> Links { get; }

    /// <summary>The hidden differences found so far.</summary>
    public ChangeLedger Ledger { get; }

    /// <summary>Diagnostics from all stages so far.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }
}

/// <summary>
/// The output of <see cref="Stages.Group"/> and <see cref="Stages.Classify(ChangeSet, CancellationToken)"/>: atomic changes, the display groups over them, and
/// the ledger. Custom stages and tests make variants with the <c>With</c> methods; <see cref="Stages.Verify"/> checks whatever
/// it is given. Immutable.
/// </summary>
public sealed class ChangeSet
{
    /// <summary>Wraps stage output.</summary>
    /// <param name="alignment">The alignment the changes belong to.</param>
    /// <param name="changes">The changes, with unique ids.</param>
    /// <param name="groups">The display groups; every change must be in exactly one.</param>
    /// <param name="ledger">The hidden differences.</param>
    /// <param name="diagnostics">All diagnostics so far.</param>
    /// <param name="isClassified">Whether <see cref="Stages.Classify(ChangeSet, CancellationToken)"/> has run.</param>
    public ChangeSet(
        Alignment alignment,
        ImmutableArray<Change> changes,
        ImmutableArray<ChangeGroup> groups,
        ChangeLedger ledger,
        ImmutableArray<Diagnostic> diagnostics,
        bool isClassified)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        ArgumentNullException.ThrowIfNull(ledger);
        Alignment = alignment;
        Changes = changes.IsDefault ? [] : changes;
        Groups = groups.IsDefault ? [] : groups;
        Ledger = ledger;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
        IsClassified = isClassified;
    }

    /// <summary>The alignment.</summary>
    public Alignment Alignment { get; }

    /// <summary>The settings, from the alignment.</summary>
    public CompareSettings Settings => Alignment.Settings;

    /// <summary>The atomic changes in comparison order.</summary>
    public ImmutableArray<Change> Changes { get; }

    /// <summary>The display groups.</summary>
    public ImmutableArray<ChangeGroup> Groups { get; }

    /// <summary>The hidden differences.</summary>
    public ChangeLedger Ledger { get; }

    /// <summary>Diagnostics from all stages so far.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>Whether classification has run.</summary>
    public bool IsClassified { get; }

    /// <summary>A copy with other changes.</summary>
    public ChangeSet WithChanges(IEnumerable<Change> changes) => new(Alignment, [.. changes], Groups, Ledger, Diagnostics, IsClassified);

    /// <summary>A copy with other groups.</summary>
    public ChangeSet WithGroups(IEnumerable<ChangeGroup> groups) => new(Alignment, Changes, [.. groups], Ledger, Diagnostics, IsClassified);

    /// <summary>A copy with another ledger.</summary>
    public ChangeSet WithLedger(ChangeLedger ledger) => new(Alignment, Changes, Groups, ledger, Diagnostics, IsClassified);

    /// <summary>A copy with extra diagnostics.</summary>
    public ChangeSet WithDiagnostics(IEnumerable<Diagnostic> more) => new(Alignment, Changes, Groups, Ledger, [.. Diagnostics, .. more], IsClassified);
}
