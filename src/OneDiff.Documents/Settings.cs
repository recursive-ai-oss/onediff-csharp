using OneDiff;

namespace OneDiff.Documents;

/// <summary>How a kind of change is marked in a rendered redline.</summary>
public enum MarkDecoration
{
    /// <summary>Colour only.</summary>
    None = 0,

    /// <summary>A single underline.</summary>
    Underline = 1,

    /// <summary>A double underline.</summary>
    DoubleUnderline = 2,

    /// <summary>A single strike-through.</summary>
    StrikeThrough = 3,

    /// <summary>A double strike-through.</summary>
    DoubleStrikeThrough = 4,

    /// <summary>A background highlight.</summary>
    Highlight = 5,
}

/// <summary>The mark for one kind of change.</summary>
/// <param name="Color">An <c>#RRGGBB</c> colour.</param>
/// <param name="Decoration">The decoration.</param>
/// <param name="Glyph">A short glyph shown beside the change (<c>+</c>, <c>−</c>), or empty for none.</param>
public sealed record MarkStyle(string Color, MarkDecoration Decoration, string Glyph = "");

/// <summary>
/// The look of a redline, as data. This type only describes the look; writers read it. The presets are
/// <see cref="Blackline"/> (the single-document default), <see cref="SideBySide"/>, <see cref="Word"/> and <see cref="Developer"/>.
/// </summary>
public sealed record RedlineStyle
{
    /// <summary>The preset's name, or a name you choose.</summary>
    public string Name { get; init; } = "Blackline";

    /// <summary>How inserted text is marked.</summary>
    public MarkStyle Insertion { get; init; } = new("#0000FF", MarkDecoration.DoubleUnderline);

    /// <summary>How deleted text is marked.</summary>
    public MarkStyle Deletion { get; init; } = new("#D00000", MarkDecoration.StrikeThrough);

    /// <summary>How the old place of moved text is marked.</summary>
    public MarkStyle MoveFrom { get; init; } = new("#008000", MarkDecoration.DoubleStrikeThrough);

    /// <summary>How the new place of moved text is marked.</summary>
    public MarkStyle MoveTo { get; init; } = new("#008000", MarkDecoration.DoubleUnderline);

    /// <summary>How formatting-only changes are marked.</summary>
    public MarkStyle Formatting { get; init; } = new("#6B6B6B", MarkDecoration.None);

    /// <summary>Whether a bar is drawn in the margin beside changed lines.</summary>
    public bool ChangeBars { get; init; } = true;

    /// <summary>The colour of change bars.</summary>
    public string ChangeBarColor { get; init; } = "#000000";

    /// <summary>Whether a cover page (file names, settings, counts by kind) is added.</summary>
    public bool CoverPage { get; init; } = true;

    /// <summary>Whether a footer naming the inputs and their hashes is added.</summary>
    public bool Footer { get; init; } = true;

    /// <summary>Whether each change carries a tag naming its type. Off by default.</summary>
    public bool TypeTags { get; init; }

    /// <summary>The page heading and title of a written redline; empty for "Comparison of <i>old</i> and <i>new</i>" from the input names.</summary>
    public string Title { get; init; } = "";

    /// <summary>The BCP 47 language tag written on the page (<c>lang</c>); "en" by default. It describes the page furniture, which is in English.</summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// The time to print in the footer, or null (the default) for none. Writers never read the clock, so output stays
    /// reproducible: a timestamp appears only when the caller supplies one.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>A legal blackline: insertions blue double underline, deletions red strike-through, moves green, change bars, cover page.</summary>
    public static RedlineStyle Blackline { get; } = new();

    /// <summary>Side by side with colour-blind-safe blue and orange highlights and +/− glyphs; no bars or cover page.</summary>
    public static RedlineStyle SideBySide { get; } = new()
    {
        Name = "SideBySide",
        Insertion = new MarkStyle("#0072B2", MarkDecoration.Highlight, "+"),
        Deletion = new MarkStyle("#E69F00", MarkDecoration.Highlight, "\u2212"),
        MoveFrom = new MarkStyle("#E69F00", MarkDecoration.Highlight, "\u2192"),
        MoveTo = new MarkStyle("#0072B2", MarkDecoration.Highlight, "\u2190"),
        Formatting = new MarkStyle("#999999", MarkDecoration.Highlight, "~"),
        ChangeBars = false,
        CoverPage = false,
        Footer = false,
    };

    /// <summary>The look of a word processor's tracked changes: underlined insertions, struck deletions, double-marked moves, a bar in the margin.</summary>
    public static RedlineStyle Word { get; } = new()
    {
        Name = "Word",
        Insertion = new MarkStyle("#C00000", MarkDecoration.Underline),
        Deletion = new MarkStyle("#C00000", MarkDecoration.StrikeThrough),
        MoveFrom = new MarkStyle("#00A046", MarkDecoration.DoubleStrikeThrough),
        MoveTo = new MarkStyle("#00A046", MarkDecoration.DoubleUnderline),
        Formatting = new MarkStyle("#C00000", MarkDecoration.None),
        CoverPage = false,
        Footer = false,
    };

    /// <summary>The look of a code diff: green and red backgrounds with +/- glyphs.</summary>
    public static RedlineStyle Developer { get; } = new()
    {
        Name = "Developer",
        Insertion = new MarkStyle("#22863A", MarkDecoration.Highlight, "+"),
        Deletion = new MarkStyle("#CB2431", MarkDecoration.Highlight, "-"),
        MoveFrom = new MarkStyle("#6F42C1", MarkDecoration.Highlight, "<"),
        MoveTo = new MarkStyle("#6F42C1", MarkDecoration.Highlight, ">"),
        Formatting = new MarkStyle("#586069", MarkDecoration.Highlight, "~"),
        ChangeBars = false,
        CoverPage = false,
        Footer = false,
    };
}

/// <summary>
/// What a comparison does and does not show, and its limits. Immutable; derive variants with <c>with</c>. The presets are
/// <see cref="Legal"/> (the default), <see cref="Strict"/> and <see cref="TextOnly"/>. The reference stages honour
/// <see cref="Hidden"/>, <see cref="ThrowOnFailure"/>, <see cref="CostCap"/>, <see cref="Algorithm"/>, the thresholds and the two
/// detection switches; the remaining members are read by later stages and writers.
/// </summary>
public sealed record CompareSettings
{
    /// <summary>The preset's name, or a name you choose.</summary>
    public string Name { get; init; } = "Legal";

    /// <summary>The difference categories that go to the ledger instead of being shown as changes.</summary>
    public CategorySet Hidden { get; init; } = LegalHidden;

    /// <summary>When true, a failed verification throws instead of falling back to a coarser grouping.</summary>
    public bool ThrowOnFailure { get; init; }

    /// <summary>
    /// The most work a stage may spend before it returns a coarser, still complete result with a diagnostic. Counted in
    /// comparisons of two items (token pairs, block pairs); default 16 million.
    /// </summary>
    public long CostCap { get; init; } = 16_000_000;

    /// <summary>The algorithm for diffs of text inside a pair of blocks. Myers by default.</summary>
    public DiffAlgorithm Algorithm { get; init; } = DiffAlgorithm.Myers;

    /// <summary>The least similarity in (0, 1] at which two blocks of the same kind are paired as an edit. Default 0.5.</summary>
    public double MatchThreshold { get; init; } = 0.5;

    /// <summary>The least similarity in (0, 1] at which one block and a run of neighbours are a split or merge. Default 0.8.</summary>
    public double SplitMergeThreshold { get; init; } = 0.8;

    /// <summary>The least similarity in (0, 1] at which blocks far apart count as a move. Default 0.8. Below it, a far pair still counts as a move when it reaches <see cref="MatchThreshold"/> and is clearly the best candidate of both blocks.</summary>
    public double MoveThreshold { get; init; } = 0.8;

    /// <summary>Whether blocks outside the matched order are paired as moves. Default true; when false they are deleted and inserted.</summary>
    public bool DetectMoves { get; init; } = true;

    /// <summary>Whether splits and merges are looked for. Default true.</summary>
    public bool DetectSplitsAndMerges { get; init; } = true;

    /// <summary>Whether cascades (renumbering and the cross-references it causes) are shown as one grouped change. Read by later stages.</summary>
    public bool CollapseCascades { get; init; }

    /// <summary>Whether changes carry resolved types and values. Read by later stages.</summary>
    public bool TypedChanges { get; init; }

    /// <summary>The look writers use unless told otherwise.</summary>
    public RedlineStyle Style { get; init; } = RedlineStyle.Blackline;

    /// <summary>The seed from which writers derive identifiers (never the clock), so output is reproducible.</summary>
    public ulong Seed { get; init; }

    // Declared before the presets: static initializers run in textual order and the presets read it.
    private static CategorySet LegalHidden { get; } = CategorySet.Of(
        LedgerCategory.Quotes, LedgerCategory.Dashes, LedgerCategory.Whitespace, LedgerCategory.LineBreaks, LedgerCategory.Hyphenation,
        LedgerCategory.PageNumbers, LedgerCategory.Properties, LedgerCategory.Comments, LedgerCategory.FieldCodes);

    /// <summary>The default: typographic, layout, property, comment and field-code differences go to the ledger; the blackline look; cascades collapsed; typed changes.</summary>
    public static CompareSettings Legal { get; } = new() { CollapseCascades = true, TypedChanges = true };

    /// <summary>Nothing hidden except content outside the model, and a failed verification throws.</summary>
    public static CompareSettings Strict { get; } = new() { Name = "Strict", Hidden = CategorySet.None, ThrowOnFailure = true };

    /// <summary>Like <see cref="Legal"/> and also character, paragraph, list and section formatting go to the ledger.</summary>
    public static CompareSettings TextOnly { get; } = new()
    {
        Name = "TextOnly",
        Hidden = LegalHidden.Union(CategorySet.Of(
            LedgerCategory.RunFormatting, LedgerCategory.ParagraphFormatting, LedgerCategory.ListFormatting, LedgerCategory.SectionFormatting)),
    };

    /// <summary>The settings used when none are given: <see cref="Legal"/>.</summary>
    public static CompareSettings Default => Legal;

    /// <summary>Checks the numeric limits.</summary>
    /// <exception cref="ArgumentException">A threshold is outside (0, 1], the cost cap is not positive, or the algorithm or style is invalid.</exception>
    public void Validate()
    {
        if (!(MatchThreshold > 0 && MatchThreshold <= 1) || !(SplitMergeThreshold > 0 && SplitMergeThreshold <= 1) || !(MoveThreshold > 0 && MoveThreshold <= 1))
        {
            throw new ArgumentException("similarity thresholds must lie in (0, 1]");
        }

        if (CostCap <= 0)
        {
            throw new ArgumentException("the cost cap must be positive");
        }

        if (!Enum.IsDefined(Algorithm) || Style is null || Name is null)
        {
            throw new ArgumentException("the algorithm, style or name is invalid");
        }
    }
}
