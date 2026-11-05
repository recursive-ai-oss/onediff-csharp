namespace OneDiff;

/// <summary>
/// One stretch of text in a diff, read without token arithmetic: how it changed, where it starts in each text, and the text itself.
/// Line numbers count from 1 and follow <see cref="TextDiff.Lines"/>: a line ends at <c>\n</c>, <c>\r\n</c> or a lone <c>\r</c>.
/// </summary>
/// <param name="Kind">Equal (in both texts), Delete (old only) or Insert (new only).</param>
/// <param name="OldLine">The line of the old text the stretch starts on; null for an insertion.</param>
/// <param name="NewLine">The line of the new text the stretch starts on; null for a deletion.</param>
/// <param name="Text">The text. In <see cref="TextDiff.Pieces"/> it is the whole run with its line breaks; in <see cref="TextDiff.Lines"/> it lies within one line and has no line break.</param>
public readonly record struct DiffRun(EditKind Kind, int? OldLine, int? NewLine, string Text);
