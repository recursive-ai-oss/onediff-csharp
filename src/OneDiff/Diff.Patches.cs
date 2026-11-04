using OneDiff.Patching;

namespace OneDiff;

public static partial class Diff
{
    /// <summary>
    /// Makes a unified diff (GNU format) that turns <paramref name="oldText"/> into <paramref name="newText"/>, comparing line by line.
    /// Lines end at <c>'\n'</c>; a carriage return is part of the line. Identical texts give the empty string.
    /// </summary>
    /// <param name="oldText">The old text.</param>
    /// <param name="newText">The new text.</param>
    /// <param name="oldName">Name on the <c>---</c> line. <c>a</c> by default.</param>
    /// <param name="newName">Name on the <c>+++</c> line. <c>b</c> by default.</param>
    /// <param name="contextLines">Unchanged lines kept around each change. Three by default.</param>
    /// <param name="algorithm">Which algorithm finds the changes. Myers when omitted.</param>
    /// <param name="cancellationToken">Observed while searching.</param>
    /// <exception cref="ArgumentNullException">A text or name is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="contextLines"/> is negative.</exception>
    public static string Unified(
        string oldText,
        string newText,
        string oldName = "a",
        string newName = "b",
        int contextLines = 3,
        DiffAlgorithm algorithm = DiffAlgorithm.Myers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        List<string> oldLines = PatchLines.Split(oldText);
        List<string> newLines = PatchLines.Split(newText);
        EditScript script = Sequences<string>(oldLines.ToArray(), newLines.ToArray(), algorithm, StringComparer.Ordinal, cancellationToken);
        return UnifiedFilePatch.Create(oldLines, newLines, script, oldName, newName, contextLines).ToString();
    }

    /// <summary>
    /// Applies a single-file unified diff to <paramref name="text"/>, with offset and up to two lines of fuzz as the GNU patch manual
    /// describes. Hunks that do not fit are reported in the result and leave the text unchanged there.
    /// </summary>
    /// <param name="text">The text to patch.</param>
    /// <param name="patchText">A unified diff for one file. An empty diff leaves the text unchanged.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="PatchFormatException"><paramref name="patchText"/> is malformed.</exception>
    /// <exception cref="ArgumentException"><paramref name="patchText"/> holds more than one file.</exception>
    public static UnifiedApplyResult ApplyUnified(string text, string patchText)
    {
        ArgumentNullException.ThrowIfNull(text);
        UnifiedPatch patch = UnifiedPatch.Parse(patchText);
        return patch.Files.Count switch
        {
            0 => new UnifiedApplyResult(text, [], null),
            1 => patch.Files[0].Apply(text),
            _ => throw new ArgumentException("The patch holds more than one file.", nameof(patchText)),
        };
    }

    /// <summary>
    /// Makes diff-match-patch patch text that turns <paramref name="oldText"/> into <paramref name="newText"/>, using the default
    /// <see cref="DmpPatcher"/> settings. Identical texts give the empty string.
    /// </summary>
    /// <exception cref="ArgumentNullException">A text is null.</exception>
    public static string PatchText(string oldText, string newText, CancellationToken cancellationToken = default)
        => DmpPatcher.ToText(new DmpPatcher().Make(oldText, newText, cancellationToken));

    /// <summary>
    /// Applies diff-match-patch patch text to <paramref name="text"/> with fuzzy placement (default <see cref="DmpPatcher"/> settings).
    /// Patches that cannot be placed are reported in the result and skipped.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="PatchFormatException"><paramref name="patchText"/> is malformed.</exception>
    public static DmpApplyResult ApplyPatchText(string text, string patchText)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new DmpPatcher().Apply(DmpPatcher.FromText(patchText), text);
    }
}
