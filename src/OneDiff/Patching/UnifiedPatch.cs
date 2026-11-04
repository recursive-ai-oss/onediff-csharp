using System.Text;

namespace OneDiff.Patching;

/// <summary>What a line of a unified hunk does.</summary>
public enum UnifiedLineKind : byte
{
    /// <summary>Unchanged context, present in both files (prefix space).</summary>
    Context = 0,

    /// <summary>Present in the old file only (prefix minus).</summary>
    Delete = 1,

    /// <summary>Present in the new file only (prefix plus).</summary>
    Insert = 2,
}

/// <summary>One line of a hunk.</summary>
/// <param name="Kind">What the line does.</param>
/// <param name="Text">
/// The file line, including its terminator exactly as the file has it (<c>"\n"</c>, <c>"\r\n"</c>, or nothing for a last line
/// without one). Text without a trailing <c>'\n'</c> is what the format marks with <c>\ No newline at end of file</c>.
/// </param>
public readonly record struct UnifiedLine(UnifiedLineKind Kind, string Text)
{
    /// <summary>True when the line ends in a newline.</summary>
    public bool HasNewline => Text.Length > 0 && Text[^1] == '\n';
}

/// <summary>
/// One hunk of a unified diff: where it applies and the lines it removes, adds and keeps. The counts are derived from the lines, so
/// they can never disagree with them.
/// </summary>
public sealed class UnifiedHunk
{
    private readonly UnifiedLine[] lines;

    /// <summary>Creates a hunk.</summary>
    /// <param name="oldStart">
    /// The first old-file line number as written in the header (one-based). When the hunk has no old lines, the header names the line
    /// before the hunk instead, so this is that line's number (zero for the top of the file).
    /// </param>
    /// <param name="newStart">The same for the new file.</param>
    /// <param name="lines">The hunk body in order.</param>
    /// <param name="sectionHeading">The text after the closing <c>@@</c>, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is null or has a null text.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A start is negative, or zero while its side has lines.</exception>
    public UnifiedHunk(int oldStart, int newStart, IEnumerable<UnifiedLine> lines, string? sectionHeading = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        this.lines = lines.ToArray();
        int oldCount = 0;
        int newCount = 0;
        foreach (UnifiedLine line in this.lines)
        {
            ArgumentNullException.ThrowIfNull(line.Text);
            if (line.Kind != UnifiedLineKind.Insert)
            {
                oldCount++;
            }

            if (line.Kind != UnifiedLineKind.Delete)
            {
                newCount++;
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegative(oldStart);
        ArgumentOutOfRangeException.ThrowIfNegative(newStart);
        if ((oldCount > 0 && oldStart == 0) || (newCount > 0 && newStart == 0))
        {
            throw new ArgumentOutOfRangeException(nameof(oldStart), "A start of zero is only valid for a side with no lines.");
        }

        OldStart = oldStart;
        NewStart = newStart;
        OldCount = oldCount;
        NewCount = newCount;
        SectionHeading = sectionHeading;
    }

    /// <summary>The old-file start as written in the header (see the constructor).</summary>
    public int OldStart { get; }

    /// <summary>Number of old-file lines the hunk covers (context and deletions).</summary>
    public int OldCount { get; }

    /// <summary>The new-file start as written in the header.</summary>
    public int NewStart { get; }

    /// <summary>Number of new-file lines the hunk covers (context and insertions).</summary>
    public int NewCount { get; }

    /// <summary>The text after the closing <c>@@</c> of the header (usually the enclosing function), or null.</summary>
    public string? SectionHeading { get; }

    /// <summary>The body lines.</summary>
    public ReadOnlySpan<UnifiedLine> Lines => lines;

    /// <summary>The zero-based index in the old file of the first old line of the hunk (or the insertion point when there are none).</summary>
    public int OldIndex => OldCount == 0 ? OldStart : OldStart - 1;

    /// <summary>The zero-based index in the new file of the first new line of the hunk.</summary>
    public int NewIndex => NewCount == 0 ? NewStart : NewStart - 1;

    /// <summary>Returns the hunk that undoes this one: deletions become insertions and the sides swap.</summary>
    public UnifiedHunk Reverse()
    {
        var flipped = new UnifiedLine[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            UnifiedLine l = lines[i];
            flipped[i] = l.Kind switch
            {
                UnifiedLineKind.Delete => l with { Kind = UnifiedLineKind.Insert },
                UnifiedLineKind.Insert => l with { Kind = UnifiedLineKind.Delete },
                _ => l,
            };
        }

        return new UnifiedHunk(NewStart, OldStart, flipped, SectionHeading);
    }

    internal void AppendTo(StringBuilder sb)
    {
        sb.Append("@@ -");
        AppendRange(sb, OldStart, OldCount);
        sb.Append(" +");
        AppendRange(sb, NewStart, NewCount);
        sb.Append(" @@");
        if (SectionHeading is not null)
        {
            sb.Append(' ').Append(SectionHeading);
        }

        sb.Append('\n');
        foreach (UnifiedLine line in lines)
        {
            sb.Append(line.Kind switch { UnifiedLineKind.Delete => '-', UnifiedLineKind.Insert => '+', _ => ' ' });
            sb.Append(line.Text);
            if (!line.HasNewline)
            {
                // GNU unified format, "Detailed Unified": the marker follows a line that has no terminator.
                sb.Append("\n\\ No newline at end of file\n");
            }
        }
    }

    private static void AppendRange(StringBuilder sb, int start, int count)
    {
        sb.Append(start);
        if (count != 1)
        {
            sb.Append(',').Append(count);
        }
    }
}

/// <summary>The patch for one file: its header names and its hunks.</summary>
public sealed class UnifiedFilePatch
{
    private readonly UnifiedHunk[] hunks;
    private readonly string[] extendedHeader;

    /// <summary>Creates a file patch.</summary>
    /// <param name="oldName">Name after <c>---</c>, without timestamp.</param>
    /// <param name="newName">Name after <c>+++</c>, without timestamp.</param>
    /// <param name="hunks">The hunks in file order.</param>
    /// <param name="oldTimestamp">Text after a tab on the <c>---</c> line, or null for none.</param>
    /// <param name="newTimestamp">The same for <c>+++</c>.</param>
    /// <param name="extendedHeader">Header lines before <c>---</c> (such as git's <c>diff --git</c> and <c>index</c> lines), kept verbatim without line ends.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public UnifiedFilePatch(
        string oldName,
        string newName,
        IEnumerable<UnifiedHunk> hunks,
        string? oldTimestamp = null,
        string? newTimestamp = null,
        IEnumerable<string>? extendedHeader = null)
    {
        ArgumentNullException.ThrowIfNull(oldName);
        ArgumentNullException.ThrowIfNull(newName);
        ArgumentNullException.ThrowIfNull(hunks);
        OldName = oldName;
        NewName = newName;
        OldTimestamp = oldTimestamp;
        NewTimestamp = newTimestamp;
        this.hunks = hunks.ToArray();
        this.extendedHeader = extendedHeader?.ToArray() ?? [];
    }

    /// <summary>The old file's name as written (git's <c>a/</c> prefixes are not stripped; <c>/dev/null</c> means the file is created).</summary>
    public string OldName { get; }

    /// <summary>The new file's name as written (<c>/dev/null</c> means the file is deleted).</summary>
    public string NewName { get; }

    /// <summary>The timestamp text on the <c>---</c> line, or null.</summary>
    public string? OldTimestamp { get; }

    /// <summary>The timestamp text on the <c>+++</c> line, or null.</summary>
    public string? NewTimestamp { get; }

    /// <summary>Lines before <c>---</c> that belong to this file (git's extended header), verbatim.</summary>
    public IReadOnlyList<string> ExtendedHeader => extendedHeader;

    /// <summary>The hunks in file order.</summary>
    public IReadOnlyList<UnifiedHunk> Hunks => hunks;

    /// <summary>
    /// Makes the patch that turns <paramref name="oldLines"/> into <paramref name="newLines"/> along <paramref name="script"/>.
    /// Changes closer together than twice the context length share a hunk. Identical inputs give no hunks.
    /// </summary>
    /// <param name="oldLines">Old lines with their terminators (see <see cref="PatchLines.Split"/>).</param>
    /// <param name="newLines">New lines with their terminators.</param>
    /// <param name="script">An edit script over those two line lists.</param>
    /// <param name="oldName">Name for the <c>---</c> line.</param>
    /// <param name="newName">Name for the <c>+++</c> line.</param>
    /// <param name="contextLines">Context lines around each change; three by default, zero is allowed.</param>
    /// <param name="oldTimestamp">Optional timestamp text for the <c>---</c> line (none are invented).</param>
    /// <param name="newTimestamp">Optional timestamp text for the <c>+++</c> line.</param>
    /// <exception cref="ArgumentNullException">A list, the script or a name is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="contextLines"/> is negative.</exception>
    /// <exception cref="ArgumentException">The script's lengths do not match the line lists.</exception>
    public static UnifiedFilePatch Create(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        EditScript script,
        string oldName,
        string newName,
        int contextLines = 3,
        string? oldTimestamp = null,
        string? newTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(newLines);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
        if (script.OldLength != oldLines.Count || script.NewLength != newLines.Count)
        {
            throw new ArgumentException("The edit script does not describe these line lists.", nameof(script));
        }

        var hunks = new List<UnifiedHunk>();
        ReadOnlySpan<Edit> edits = script.Edits;
        int i = 0;
        while (i < edits.Length)
        {
            if (edits[i].Kind == EditKind.Equal)
            {
                i++;
                continue;
            }

            // First change of a hunk; extend over following changes while the equal run between them is short enough to share context.
            int first = i;
            int last = i;
            int j = i + 1;
            while (j < edits.Length)
            {
                if (edits[j].Kind != EditKind.Equal)
                {
                    last = j;
                    j++;
                }
                else if (j + 1 < edits.Length && edits[j].OldLength <= 2 * contextLines)
                {
                    j++;
                }
                else
                {
                    break;
                }
            }

            int lead = first > 0 && edits[first - 1].Kind == EditKind.Equal ? Math.Min(contextLines, edits[first - 1].OldLength) : 0;
            int trail = last + 1 < edits.Length && edits[last + 1].Kind == EditKind.Equal ? Math.Min(contextLines, edits[last + 1].OldLength) : 0;

            int oldIndex = edits[first].OldStart - lead;
            int newIndex = edits[first].NewStart - lead;
            var body = new List<UnifiedLine>();
            for (int k = oldIndex; k < edits[first].OldStart; k++)
            {
                body.Add(new UnifiedLine(UnifiedLineKind.Context, oldLines[k]));
            }

            for (int e = first; e <= last; e++)
            {
                Edit edit = edits[e];
                switch (edit.Kind)
                {
                    case EditKind.Equal:
                        for (int k = 0; k < edit.OldLength; k++)
                        {
                            body.Add(new UnifiedLine(UnifiedLineKind.Context, oldLines[edit.OldStart + k]));
                        }

                        break;
                    case EditKind.Delete:
                        for (int k = 0; k < edit.OldLength; k++)
                        {
                            body.Add(new UnifiedLine(UnifiedLineKind.Delete, oldLines[edit.OldStart + k]));
                        }

                        break;
                    default:
                        for (int k = 0; k < edit.NewLength; k++)
                        {
                            body.Add(new UnifiedLine(UnifiedLineKind.Insert, newLines[edit.NewStart + k]));
                        }

                        break;
                }
            }

            int tailStart = edits[last].OldEnd;
            for (int k = 0; k < trail; k++)
            {
                body.Add(new UnifiedLine(UnifiedLineKind.Context, oldLines[tailStart + k]));
            }

            int oldCount = 0;
            int newCount = 0;
            foreach (UnifiedLine l in body)
            {
                if (l.Kind != UnifiedLineKind.Insert)
                {
                    oldCount++;
                }

                if (l.Kind != UnifiedLineKind.Delete)
                {
                    newCount++;
                }
            }

            // A side with no lines names the line before the hunk, which is its zero-based index.
            hunks.Add(new UnifiedHunk(oldCount == 0 ? oldIndex : oldIndex + 1, newCount == 0 ? newIndex : newIndex + 1, body));
            i = last + 1;
        }

        return new UnifiedFilePatch(oldName, newName, hunks, oldTimestamp, newTimestamp);
    }

    /// <summary>Returns the patch that undoes this one (names, timestamps and hunks swapped).</summary>
    public UnifiedFilePatch Reverse()
    {
        var flipped = new UnifiedHunk[hunks.Length];
        for (int i = 0; i < hunks.Length; i++)
        {
            flipped[i] = hunks[i].Reverse();
        }

        return new UnifiedFilePatch(NewName, OldName, flipped, NewTimestamp, OldTimestamp, extendedHeader);
    }

    /// <summary>Applies the patch to <paramref name="text"/>. See <see cref="UnifiedApplyOptions"/> and <see cref="UnifiedApplyResult"/>.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public UnifiedApplyResult Apply(string text, UnifiedApplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return UnifiedApplier.Apply(this, text, options ?? new UnifiedApplyOptions());
    }

    /// <summary>The patch as text. A patch without hunks is the empty string, as <c>diff</c> prints nothing for identical files.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        AppendTo(sb);
        return sb.ToString();
    }

    internal void AppendTo(StringBuilder sb)
    {
        if (hunks.Length == 0)
        {
            return;
        }

        foreach (string h in extendedHeader)
        {
            sb.Append(h).Append('\n');
        }

        AppendName(sb, "--- ", OldName, OldTimestamp);
        AppendName(sb, "+++ ", NewName, NewTimestamp);
        foreach (UnifiedHunk h in hunks)
        {
            h.AppendTo(sb);
        }
    }

    private static void AppendName(StringBuilder sb, string marker, string name, string? timestamp)
    {
        sb.Append(marker).Append(name);
        if (timestamp is not null)
        {
            sb.Append('\t').Append(timestamp);
        }

        sb.Append('\n');
    }
}

/// <summary>A unified diff: one patch per file.</summary>
public sealed class UnifiedPatch
{
    /// <summary>The longest patch text, in characters, that the parser accepts.</summary>
    public const int MaxInputLength = 1 << 28;

    private readonly UnifiedFilePatch[] files;

    /// <summary>Creates a patch from file patches.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="files"/> is null.</exception>
    public UnifiedPatch(IEnumerable<UnifiedFilePatch> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        this.files = files.ToArray();
    }

    /// <summary>The per-file patches in order. Empty for text that holds no changes.</summary>
    public IReadOnlyList<UnifiedFilePatch> Files => files;

    /// <summary>
    /// Parses a unified diff. Text before the first <c>---</c>/<c>+++</c> pair and between files that is not part of a header or hunk is
    /// skipped, as the patch manual describes. Whitespace-only input is a patch with no files.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="PatchFormatException">The text is malformed or truncated; see <see cref="PatchErrorKind"/>.</exception>
    public static UnifiedPatch Parse(string text)
    {
        if (!TryParse(text, out UnifiedPatch? patch, out PatchParseError error))
        {
            throw new PatchFormatException(error);
        }

        return patch;
    }

    /// <summary>Parses a unified diff without throwing for malformed input. See <see cref="Parse"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static bool TryParse(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out UnifiedPatch? patch, out PatchParseError error)
    {
        ArgumentNullException.ThrowIfNull(text);
        return UnifiedParser.TryParse(text, out patch, out error);
    }

    /// <summary>Returns the patch that undoes this one.</summary>
    public UnifiedPatch Reverse() => new(files.Select(f => f.Reverse()));

    /// <summary>The patch as text, files in order.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (UnifiedFilePatch f in files)
        {
            f.AppendTo(sb);
        }

        return sb.ToString();
    }
}
