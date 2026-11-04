using System.Collections.Immutable;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Ledger;

/// <summary>One hidden difference: where it is and what it is.</summary>
/// <param name="OldNode">The old-document node it concerns, or -1.</param>
/// <param name="NewNode">The new-document node it concerns, or -1.</param>
/// <param name="Detail">A short deterministic description: for a folded character the two spellings, for an attribute its key, for an opaque part its name.</param>
/// <param name="OldAnchor">Where it is in the old source; <see cref="Anchor.None"/> when absent.</param>
/// <param name="NewAnchor">Where it is in the new source; <see cref="Anchor.None"/> when absent.</param>
public sealed record LedgerLocation(int OldNode, int NewNode, string Detail, Anchor OldAnchor, Anchor NewAnchor);

/// <summary>
/// All differences hidden for one category: how many and where. <see cref="Count"/> equals the number of
/// <see cref="Locations"/> in everything this library writes; a reader of a comparison made elsewhere may find fewer
/// locations than the count, never more.
/// </summary>
/// <param name="Category">What kind of difference.</param>
/// <param name="Count">How many differences were hidden.</param>
/// <param name="Locations">Where, in comparison order.</param>
public sealed record LedgerEntry(LedgerCategory Category, int Count, ImmutableArray<LedgerLocation> Locations);

/// <summary>
/// The ledger of a comparison: every difference between the compared models that is not shown as a change, by category.
/// Together with the changes it accounts for every difference; verification recomputes it and fails when an entry is missing.
/// </summary>
public sealed class ChangeLedger
{
    internal ChangeLedger(ImmutableArray<LedgerEntry> entries) => Entries = entries;

    /// <summary>The ledger with nothing hidden.</summary>
    public static ChangeLedger Empty { get; } = new([]);

    /// <summary>One entry per category that hid anything, in category order.</summary>
    public ImmutableArray<LedgerEntry> Entries { get; }

    /// <summary>The number of hidden differences over all categories.</summary>
    public int TotalCount
    {
        get
        {
            int total = 0;
            foreach (LedgerEntry e in Entries)
            {
                total += e.Count;
            }

            return total;
        }
    }

    /// <summary>The entry of a category, or null when nothing was hidden under it.</summary>
    public LedgerEntry? Get(LedgerCategory category)
    {
        foreach (LedgerEntry e in Entries)
        {
            if (e.Category == category)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>Builds a ledger from entries; sorts them by category and rejects duplicates and empty entries.</summary>
    /// <exception cref="ArgumentException">Two entries share a category, or an entry has a negative count.</exception>
    public static ChangeLedger Create(IEnumerable<LedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = new List<LedgerEntry>(entries);
        list.Sort(static (a, b) => ((int)a.Category).CompareTo((int)b.Category));
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Count < 0 || list[i].Locations.IsDefault || (i > 0 && list[i].Category == list[i - 1].Category))
            {
                throw new ArgumentException("ledger entries need distinct categories and non-negative counts");
            }
        }

        return list.Count == 0 ? Empty : new ChangeLedger([.. list]);
    }
}
