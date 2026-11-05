using System.Collections.Immutable;

namespace OneDiff.Documents.Combine;

/// <summary>One reviewer's copy of the original.</summary>
/// <param name="Index">The reviewer's position in the list given to the combination; the number every attribution uses.</param>
/// <param name="Name">The copy's display name (the file name for files).</param>
/// <param name="Hash">The SHA-256 (hex) of the copy's input, or empty for a document built by hand.</param>
public sealed record Reviewer(int Index, string Name, string Hash);

/// <summary>A change of one reviewer's own comparison that a combined change stands for.</summary>
/// <param name="Reviewer">The reviewer's index.</param>
/// <param name="ChangeId">The change's id in that reviewer's <see cref="Comparison"/>.</param>
public sealed record ChangeHolder(int Reviewer, int ChangeId);

/// <summary>
/// Why changes of different reviewers conflict. The values run from the most to the least serious; a conflict of several
/// changes carries the most serious reason among them.
/// </summary>
public enum ConflictKind
{
    /// <summary>One reviewer deleted (or replaced as a whole) content that another edited, moved or restructured.</summary>
    EditAndDelete = 0,

    /// <summary>Two reviewers edited overlapping text differently, or one inserted inside a range the other rewrote.</summary>
    Edits = 1,

    /// <summary>Two reviewers moved the same block to different places.</summary>
    Move = 2,

    /// <summary>Two reviewers split or merged the same blocks differently.</summary>
    Structure = 3,

    /// <summary>Two reviewers gave the same block attribute different values.</summary>
    Attribute = 4,

    /// <summary>Two reviewers inserted different content at the same place.</summary>
    Insert = 5,
}

/// <summary>
/// One shown change of the combined result: a change of one reviewer's comparison, or the same change made identically by
/// several. Both sides, both anchors and the edits are those of <see cref="Change"/>, taken from the first holder.
/// </summary>
/// <param name="Id">The number in the combination; unique, in document order of the original.</param>
/// <param name="Change">The change as the first holder's comparison has it (its id and new-side node ids belong to that comparison).</param>
/// <param name="Reviewers">The reviewers who made this change, ascending.</param>
/// <param name="Holders">The change in each of those reviewers' comparisons, by reviewer.</param>
/// <param name="EditReviewers">For each of <c>Change.Edits</c>, every reviewer who made that same edit (same place in the original, same new text and formatting), ascending. A reviewer can be listed here without being a holder when the reviewer's change grouped the edit differently. Empty for a change without edits.</param>
/// <param name="ConflictId">The conflict this change is part of, or -1.</param>
public sealed record CombinedChange(
    int Id,
    Change Change,
    ImmutableArray<int> Reviewers,
    ImmutableArray<ChangeHolder> Holders,
    ImmutableArray<ImmutableArray<int>> EditReviewers,
    int ConflictId);

/// <summary>
/// Changes of different reviewers that cannot all be applied to the original: all of them are shown, none is preferred.
/// </summary>
/// <param name="Id">The number in the combination; unique.</param>
/// <param name="Kind">The most serious reason among the conflicting pairs.</param>
/// <param name="ChangeIds">The ids of the combined changes in the conflict, ascending.</param>
/// <param name="Reviewers">The reviewers involved, ascending.</param>
public sealed record Conflict(int Id, ConflictKind Kind, ImmutableArray<int> ChangeIds, ImmutableArray<int> Reviewers);

/// <summary>How the combination's own checks went.</summary>
/// <param name="Passed">True when every check passed (possibly after falling back).</param>
/// <param name="FallbackLevel">0: identical changes were merged. 1: the merge was dropped and every reviewer's change is listed on its own.</param>
/// <param name="FailureCount">How many checks failed before the result that was kept.</param>
public sealed record CombinationVerification(bool Passed, int FallbackLevel, int FailureCount);
