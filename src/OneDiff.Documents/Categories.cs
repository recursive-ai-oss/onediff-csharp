using System.Collections.Immutable;

namespace OneDiff.Documents;

/// <summary>
/// Why a difference between the two compare models is not shown as a change. Each hidden difference lands in the
/// <see cref="Ledger.ChangeLedger"/> under exactly one category. The numeric values are part of the JSON form's meaning only
/// through <see cref="LedgerCategoryNames"/>; do not reorder.
/// </summary>
public enum LedgerCategory
{
    /// <summary>Straight and curly quotation marks folded together.</summary>
    Quotes = 0,

    /// <summary>Hyphens, en and em dashes and minus signs folded together.</summary>
    Dashes = 1,

    /// <summary>Runs of spaces, non-breaking and exotic spaces and line-end styles folded together.</summary>
    Whitespace = 2,

    /// <summary>Line and page breaks that are an artefact of layout (PDF text).</summary>
    LineBreaks = 3,

    /// <summary>Hyphenation at line ends (PDF text).</summary>
    Hyphenation = 4,

    /// <summary>Page numbers and running page furniture.</summary>
    PageNumbers = 5,

    /// <summary>Character formatting: bold, italic, underline, font, size, colour and the like.</summary>
    RunFormatting = 6,

    /// <summary>Paragraph formatting: style, alignment, indent, spacing, keep settings.</summary>
    ParagraphFormatting = 7,

    /// <summary>List formatting: label format and level.</summary>
    ListFormatting = 8,

    /// <summary>Section formatting: page setup.</summary>
    SectionFormatting = 9,

    /// <summary>Document properties.</summary>
    Properties = 10,

    /// <summary>Comments and everything inside them.</summary>
    Comments = 11,

    /// <summary>Field codes (the result text is still compared).</summary>
    FieldCodes = 12,

    /// <summary>Content outside the compare model (opaque parts), reported as "differs, not itemized" from a content hash.</summary>
    NotItemized = 13,
}

/// <summary>The stable names of <see cref="LedgerCategory"/> values, used by the JSON form.</summary>
public static class LedgerCategoryNames
{
    private static readonly string[] Names =
    [
        "quotes", "dashes", "whitespace", "lineBreaks", "hyphenation", "pageNumbers", "runFormatting",
        "paragraphFormatting", "listFormatting", "sectionFormatting", "properties", "comments", "fieldCodes", "notItemized",
    ];

    /// <summary>The number of defined categories.</summary>
    public static int Count => Names.Length;

    /// <summary>The stable name of a category.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="category"/> is not a defined value.</exception>
    public static string NameOf(LedgerCategory category)
    {
        int i = (int)category;
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, Names.Length);
        return Names[i];
    }

    /// <summary>Finds a category by its stable name (ordinal, case-sensitive).</summary>
    /// <returns>True when the name is known.</returns>
    public static bool TryParse(string name, out LedgerCategory category)
    {
        for (int i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.Ordinal))
            {
                category = (LedgerCategory)i;
                return true;
            }
        }

        category = default;
        return false;
    }
}

/// <summary>An immutable set of <see cref="LedgerCategory"/> values.</summary>
public readonly struct CategorySet : IEquatable<CategorySet>
{
    private readonly uint bits;

    private CategorySet(uint bits) => this.bits = bits;

    /// <summary>The empty set.</summary>
    public static CategorySet None => default;

    /// <summary>Builds a set from categories.</summary>
    public static CategorySet Of(params ReadOnlySpan<LedgerCategory> categories)
    {
        uint b = 0;
        foreach (LedgerCategory c in categories)
        {
            b |= Bit(c);
        }

        return new CategorySet(b);
    }

    /// <summary>The categories in ascending order.</summary>
    public ImmutableArray<LedgerCategory> ToArray()
    {
        ImmutableArray<LedgerCategory>.Builder b = ImmutableArray.CreateBuilder<LedgerCategory>();
        for (int i = 0; i < LedgerCategoryNames.Count; i++)
        {
            if ((bits & (1u << i)) != 0)
            {
                b.Add((LedgerCategory)i);
            }
        }

        return b.ToImmutable();
    }

    /// <summary>Whether the set holds <paramref name="category"/>.</summary>
    public bool Contains(LedgerCategory category) => (bits & Bit(category)) != 0;

    /// <summary>A copy with <paramref name="category"/> added.</summary>
    public CategorySet With(LedgerCategory category) => new(bits | Bit(category));

    /// <summary>A copy with <paramref name="category"/> removed.</summary>
    public CategorySet Without(LedgerCategory category) => new(bits & ~Bit(category));

    /// <summary>A copy holding the categories of both sets.</summary>
    public CategorySet Union(CategorySet other) => new(bits | other.bits);

    /// <inheritdoc />
    public bool Equals(CategorySet other) => bits == other.bits;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CategorySet other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (int)bits;

    /// <summary>Equality.</summary>
    public static bool operator ==(CategorySet left, CategorySet right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(CategorySet left, CategorySet right) => !left.Equals(right);

    private static uint Bit(LedgerCategory c)
    {
        int i = (int)c;
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, LedgerCategoryNames.Count);
        return 1u << i;
    }
}

/// <summary>How serious a <see cref="Diagnostic"/> is.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational: nothing was lost.</summary>
    Info = 0,

    /// <summary>The result is complete but coarser or less certain than it could have been (a cost cap was hit, a fallback ran).</summary>
    Warning = 1,

    /// <summary>A check failed. Verification records the failure it recovered from as an error diagnostic.</summary>
    Error = 2,
}

/// <summary>A note a stage leaves on the result: a cap that was hit, a fallback that ran, a check that failed.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Code">A stable machine-readable code such as <c>align.cap</c>.</param>
/// <param name="Message">A human-readable sentence.</param>
/// <param name="OldNode">The old-document node it concerns, or -1.</param>
/// <param name="NewNode">The new-document node it concerns, or -1.</param>
public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message, int OldNode = -1, int NewNode = -1);
