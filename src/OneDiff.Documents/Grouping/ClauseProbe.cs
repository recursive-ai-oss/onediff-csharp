using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Grouping;

/// <summary>One atomic edit inside a clause query, in the coordinates of the link's joined text.</summary>
internal readonly record struct ClauseEdit(int OldStart, int OldEnd, int NewStart, int NewEnd, bool FormatOnly);

/// <summary>A unit before the clause is joined: its range on each side and the atomic edits it holds.</summary>
internal readonly record struct ClauseUnit(int OldFrom, int OldTo, int NewFrom, int NewTo, ClauseEdit[] Edits);

/// <summary>
/// What the grouping asks when a clause holds two or more units: the link's texts, the clause's extent on each side, its units,
/// and what the rules decide. The rules join a unit to the next when few words separate them, or merge the whole clause into one
/// unit widened to the clause when enough of it changed.
/// </summary>
internal readonly record struct ClauseQuery(
    CompareDocument OldDocument,
    CompareDocument NewDocument,
    AlignmentLink Link,
    string Old,
    string New,
    int OldClauseStart,
    int OldClauseEnd,
    int NewClauseStart,
    int NewClauseEnd,
    ClauseUnit[] Units,
    bool RulesWhole,
    bool[] RulesJoin);

/// <summary>The answer: one clause-wide unit, or for each unit after the first whether it joins the unit before it.</summary>
internal readonly record struct ClauseAnswer(bool Whole, bool[] Join);

/// <summary>
/// An offline hook for measurement tools: a function that sees every multi-unit clause and may decide it instead of the rules.
/// It is null in every shipped path and is read only by the gold-set tool, which uses it to dump clauses for model training and to
/// score another decision procedure's answers through the unchanged pipeline.
/// </summary>
internal static class ClauseProbe
{
    /// <summary>The hook, or null for the rules. Set once before comparisons start; the function must be thread-safe.</summary>
    public static Func<ClauseQuery, ClauseAnswer?>? Hook { get; set; }
}
