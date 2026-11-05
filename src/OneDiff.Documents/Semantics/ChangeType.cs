using System.Collections.Immutable;

namespace OneDiff.Documents;

/// <summary>
/// What a <see cref="Change"/> is about, beyond what happened to its text: a value that was edited, a name, a reference, a list
/// label. <see cref="None"/> is an ordinary text change. A type is only set when <see cref="CompareSettings.TypedChanges"/> is on.
/// </summary>
public enum ChangeType
{
    /// <summary>An ordinary text change (or a change of a kind no type describes).</summary>
    None = 0,

    /// <summary>An amount of money; the resolved value is <c>USD 25000</c> (ISO 4217 code, a space, the number).</summary>
    Amount = 1,

    /// <summary>A calendar date; the resolved value is ISO 8601: <c>2027-03-01</c>, or <c>2027-03</c> and <c>--03-01</c> when the year or the day is missing.</summary>
    Date = 2,

    /// <summary>A length of time; the resolved value is the number, a space and a unit: <c>30 d</c>, <c>9 mo</c>, <c>3 y</c> (units s, min, h, d, bd, w, mo, y).</summary>
    Duration = 3,

    /// <summary>A percentage; the resolved value is <c>12.5%</c>.</summary>
    Percentage = 4,

    /// <summary>A plain number; the resolved value is the number in invariant form.</summary>
    Number = 5,

    /// <summary>The name of a party (a company with a legal-form suffix); the resolved value is the name.</summary>
    Party = 6,

    /// <summary>A term the document defines (<c>"Services" means</c>), renamed or changed; the resolved value is the term.</summary>
    DefinedTerm = 7,

    /// <summary>A cross-reference to a clause or section; the resolved value is its number, <c>8.4</c>, whatever the language of the keyword.</summary>
    Reference = 8,

    /// <summary>A list label (clause number) that changed; the resolved value is the label without its closing punctuation.</summary>
    Numbering = 9,

    /// <summary>A change of character formatting only.</summary>
    Formatting = 10,

    /// <summary>Text that differs only in white space, quotation marks or dashes.</summary>
    Typography = 11,
}

/// <summary>
/// A value in a language-neutral canonical form. The change's <see cref="Change.Type"/> says what kind of value it is.
/// </summary>
/// <param name="Canonical">The canonical text: <c>USD 25000</c>, <c>2027-03-01</c>, <c>30 d</c>, <c>12.5%</c>, <c>8.4</c>. Equal canonical text means equal values.</param>
/// <param name="Number">The numeric part (an amount, a duration, a percentage, a number), or null.</param>
/// <param name="Unit">The unit of <paramref name="Number"/>: a currency code, a duration unit or <c>%</c>; null for dates, numbers, names and references.</param>
public sealed record ResolvedValue(string Canonical, decimal? Number = null, string? Unit = null);

/// <summary>A value a recognizer found in a text.</summary>
/// <param name="Start">The first character of the value, in UTF-16 code units.</param>
/// <param name="Length">The length of the value, in UTF-16 code units; positive.</param>
/// <param name="Type">What kind of value it is (one of <see cref="ChangeType.Amount"/>, <see cref="ChangeType.Date"/>, <see cref="ChangeType.Duration"/>, <see cref="ChangeType.Percentage"/>, <see cref="ChangeType.Number"/>, <see cref="ChangeType.Party"/>).</param>
/// <param name="Value">The resolved value, or null when the text reads as that kind of value but cannot be resolved (a date like <c>3/4/2025</c> that could be March or April).</param>
public readonly record struct RecognizedValue(int Start, int Length, ChangeType Type, ResolvedValue? Value);

/// <summary>
/// Finds and resolves values (amounts, dates, durations, percentages, numbers, party names) in a short text. This is the seam
/// where a multilingual recognizer from another package plugs in; <see cref="BuiltInValueRecognizer"/> is the default. An
/// implementation must be pure and thread-safe: the same text gives the same values, with no shared mutable state, because
/// <see cref="Stages.Classify(Pipeline.ChangeSet, IValueRecognizer?, CancellationToken)"/> may call it from several comparisons at
/// once. It must not use the clock, the current culture or the network.
/// </summary>
public interface IValueRecognizer
{
    /// <summary>Finds the values in <paramref name="text"/>.</summary>
    /// <param name="text">The text, as compared. Typically one side of a change, a few words long.</param>
    /// <param name="language">A BCP 47 language tag if the caller knows it, otherwise null. The built-in recognizer ignores it.</param>
    /// <returns>The values in order of position, not overlapping. Empty when there are none.</returns>
    ImmutableArray<RecognizedValue> Recognize(string text, string? language);
}
