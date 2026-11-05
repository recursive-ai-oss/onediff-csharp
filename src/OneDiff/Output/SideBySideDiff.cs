using OneDiff.Cleanup;
using OneDiff.Tokenization;

namespace OneDiff.Output;

/// <summary>What a row of a side-by-side view shows.</summary>
public enum SideBySideKind
{
    /// <summary>The same line on both sides.</summary>
    Unchanged = 0,

    /// <summary>A line only the old text has; the right cell is null.</summary>
    Deleted = 1,

    /// <summary>A line only the new text has; the left cell is null.</summary>
    Inserted = 2,

    /// <summary>A line the new text changed: both cells are set and their parts mark the changed words.</summary>
    Modified = 3,
}

/// <summary>One side of a row: a line, and that line cut into unchanged and changed parts.</summary>
/// <param name="Line">The line's number in its text, from 1.</param>
/// <param name="Text">The line without its line break.</param>
/// <param name="Parts">
/// The line in order. Unchanged and changed rows hold one part (the whole line, Equal, Delete or Insert). A modified row holds the
/// word-level pieces: Equal and Delete on the left, Equal and Insert on the right. Concatenated, the parts are <paramref name="Text"/>.
/// </param>
public sealed record SideBySideCell(int Line, string Text, IReadOnlyList<DiffRun> Parts);

/// <summary>One row of a side-by-side view.</summary>
/// <param name="Kind">What the row shows.</param>
/// <param name="Left">The old line, or null when the row only exists in the new text.</param>
/// <param name="Right">The new line, or null when the row only exists in the old text.</param>
public sealed record SideBySideRow(SideBySideKind Kind, SideBySideCell? Left, SideBySideCell? Right);

/// <summary>
/// A line diff laid out for two columns: aligned rows, with the words that changed inside a modified line marked. A UI or console
/// can render it directly.
/// </summary>
/// <remarks>
/// A run of deleted lines followed by a run of inserted lines is paired line by line into modified rows; the longer run's remainder
/// becomes deleted or inserted rows. Words inside a modified row are compared with a word diff and semantic cleanup.
/// </remarks>
public sealed class SideBySideDiff
{
    private SideBySideDiff(SideBySideRow[] rows) => Rows = rows;

    /// <summary>The rows in order. Every line of both texts appears in exactly one row.</summary>
    public IReadOnlyList<SideBySideRow> Rows { get; }

    /// <summary>Lays out a line diff in two columns.</summary>
    /// <param name="diff">A diff made with line tokens (<see cref="Diff.Lines(string, string, DiffAlgorithm, CancellationToken)"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="diff"/> was not made with line tokens.</exception>
    public static SideBySideDiff From(TextDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (diff.Kind != TokenKind.Line)
        {
            throw new ArgumentException("a side-by-side view needs a diff made with line tokens", nameof(diff));
        }

        IReadOnlyList<DiffRun> lines = diff.Lines;
        var rows = new List<SideBySideRow>(lines.Count);
        var deleted = new List<DiffRun>();
        var inserted = new List<DiffRun>();
        foreach (DiffRun line in lines)
        {
            if (line.Kind == EditKind.Equal)
            {
                Flush(rows, deleted, inserted);
                rows.Add(new SideBySideRow(SideBySideKind.Unchanged, Cell(line, line.OldLine!.Value), Cell(line, line.NewLine!.Value)));
            }
            else if (line.Kind == EditKind.Delete)
            {
                // A delete after inserts starts a new block: the previous one is complete.
                if (inserted.Count > 0)
                {
                    Flush(rows, deleted, inserted);
                }

                deleted.Add(line);
            }
            else
            {
                inserted.Add(line);
            }
        }

        Flush(rows, deleted, inserted);
        return new SideBySideDiff(rows.ToArray());
    }

    // The line is already a piece with the right kind and numbers; it is the cell's single part.
    private static SideBySideCell Cell(DiffRun line, int number) => new(number, line.Text, [line]);

    private static void Flush(List<SideBySideRow> rows, List<DiffRun> deleted, List<DiffRun> inserted)
    {
        int paired = Math.Min(deleted.Count, inserted.Count);
        for (int i = 0; i < paired; i++)
        {
            rows.Add(Modified(deleted[i], inserted[i]));
        }

        for (int i = paired; i < deleted.Count; i++)
        {
            rows.Add(new SideBySideRow(SideBySideKind.Deleted, Cell(deleted[i], deleted[i].OldLine!.Value), null));
        }

        for (int i = paired; i < inserted.Count; i++)
        {
            rows.Add(new SideBySideRow(SideBySideKind.Inserted, null, Cell(inserted[i], inserted[i].NewLine!.Value)));
        }

        deleted.Clear();
        inserted.Clear();
    }

    private static SideBySideRow Modified(DiffRun oldLine, DiffRun newLine)
    {
        int oldNumber = oldLine.OldLine!.Value, newNumber = newLine.NewLine!.Value;
        TextDiff words = DiffCleanup.Semantic(Diff.Words(oldLine.Text, newLine.Text));
        var left = new List<DiffRun>();
        var right = new List<DiffRun>();
        foreach (DiffRun p in words.Pieces)
        {
            if (p.Kind != EditKind.Insert)
            {
                left.Add(new DiffRun(p.Kind, oldNumber, p.Kind == EditKind.Equal ? newNumber : null, p.Text));
            }

            if (p.Kind != EditKind.Delete)
            {
                right.Add(new DiffRun(p.Kind, p.Kind == EditKind.Equal ? oldNumber : null, newNumber, p.Text));
            }
        }

        return new SideBySideRow(SideBySideKind.Modified, new SideBySideCell(oldNumber, oldLine.Text, left.ToArray()), new SideBySideCell(newNumber, newLine.Text, right.ToArray()));
    }
}
