using System.Globalization;
using System.Text;

namespace OneDiff.Output;

/// <summary>
/// Renders a line diff for a terminal in the usual developer look: hunks with context, deletions in red, insertions in green,
/// and, within a replaced block of lines, the words that changed picked out in reverse video. Output is a pure function of the
/// diff and these settings: the same input gives the same bytes on every platform. Lines always end in <c>\n</c>.
/// </summary>
/// <remarks>
/// Control characters in the compared text (escape, backspace and the like) are shown as their visible pictures (U+2400 block) so
/// the content cannot move the cursor or recolour the screen. An identical pair renders as the empty string.
/// </remarks>
public sealed record TerminalRenderer
{
    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string Dim = "\u001b[2m";
    private const string Red = "\u001b[31m";
    private const string Green = "\u001b[32m";
    private const string Cyan = "\u001b[36m";
    private const string ReverseOn = "\u001b[7m";
    private const string ReverseOff = "\u001b[27m";

    private readonly int contextLines = 3;

    /// <summary>Whether to emit ANSI colour sequences. True by default. When false the output is plain <c>+</c>/<c>-</c> lines and has no word highlighting.</summary>
    public bool Color { get; init; } = true;

    /// <summary>Whether to mark changed words inside replaced lines (colour mode only). True by default. Blocks over about 8k chars per side are shown without it.</summary>
    public bool WordLevel { get; init; } = true;

    /// <summary>Unchanged lines shown around each change. 3 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int ContextLines
    {
        get => contextLines;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            contextLines = value;
        }
    }

    /// <summary>Name shown on a <c>--- </c> header line; no file header is written when both names are null. Null by default.</summary>
    public string? OldName { get; init; }

    /// <summary>Name shown on a <c>+++ </c> header line. Null by default.</summary>
    public string? NewName { get; init; }

    /// <summary>Renders <paramref name="diff"/> to a string.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="diff"/> was not made with line tokens.</exception>
    public string Render(TextDiff diff)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        Render(diff, writer);
        return writer.ToString();
    }

    /// <summary>Renders <paramref name="diff"/> to <paramref name="writer"/>.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="diff"/> was not made with line tokens.</exception>
    public void Render(TextDiff diff, TextWriter writer)
    {
        LineHunks.RequireLines(diff);
        ArgumentNullException.ThrowIfNull(writer);

        List<LineHunk> hunks = LineHunks.Build(diff, contextLines);
        if (hunks.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        if (OldName is not null || NewName is not null)
        {
            Styled(sb, Bold, "--- " + OldName);
            Styled(sb, Bold, "+++ " + NewName);
        }

        foreach (LineHunk hunk in hunks)
        {
            Styled(sb, Cyan, hunk.Header());
            RenderHunk(diff, hunk, sb);
        }

        writer.Write(sb.ToString());
    }

    private void Styled(StringBuilder sb, string style, string text)
    {
        if (Color)
        {
            sb.Append(style);
        }

        Intraline.AppendSafe(sb, text);
        if (Color)
        {
            sb.Append(Reset);
        }

        sb.Append('\n');
    }

    private void RenderHunk(TextDiff diff, LineHunk hunk, StringBuilder sb)
    {
        List<HunkLine> lines = hunk.Lines;
        for (int i = 0; i < lines.Count;)
        {
            if (lines[i].Kind == EditKind.Equal)
            {
                ReadOnlySpan<char> text = LineHunks.NewLine(diff, lines[i].NewIndex, out bool terminated);
                Line(sb, ' ', null, text, terminated, null);
                i++;
                continue;
            }

            // A changed block: the deletions and insertions between two equal lines.
            int end = i;
            while (end < lines.Count && lines[end].Kind != EditKind.Equal)
            {
                end++;
            }

            var oldText = new List<string>();
            var newText = new List<string>();
            for (int k = i; k < end; k++)
            {
                if (lines[k].Kind == EditKind.Delete)
                {
                    oldText.Add(LineHunks.OldLine(diff, lines[k].OldIndex, out _).ToString());
                }
                else
                {
                    newText.Add(LineHunks.NewLine(diff, lines[k].NewIndex, out _).ToString());
                }
            }

            List<(int Start, int End)>[] oldMarks = [];
            List<(int Start, int End)>[] newMarks = [];
            bool marked = Color && WordLevel && oldText.Count > 0 && newText.Count > 0
                && Intraline.TryMark(oldText, newText, out oldMarks, out newMarks);

            int oi = 0, ni = 0;
            for (int k = i; k < end; k++)
            {
                if (lines[k].Kind == EditKind.Delete)
                {
                    ReadOnlySpan<char> text = LineHunks.OldLine(diff, lines[k].OldIndex, out bool terminated);
                    Line(sb, '-', Red, text, terminated, marked ? oldMarks[oi] : null);
                    oi++;
                }
                else
                {
                    ReadOnlySpan<char> text = LineHunks.NewLine(diff, lines[k].NewIndex, out bool terminated);
                    Line(sb, '+', Green, text, terminated, marked ? newMarks[ni] : null);
                    ni++;
                }
            }

            i = end;
        }
    }

    private void Line(StringBuilder sb, char prefix, string? colour, ReadOnlySpan<char> text, bool terminated, List<(int Start, int End)>? marks)
    {
        bool coloured = Color && colour is not null;
        if (coloured)
        {
            sb.Append(colour);
        }

        sb.Append(prefix);
        if (marks is null || marks.Count == 0)
        {
            Intraline.AppendSafe(sb, text);
        }
        else
        {
            int at = 0;
            foreach ((int s, int t) in marks)
            {
                Intraline.AppendSafe(sb, text.Slice(at, s - at));
                sb.Append(ReverseOn);
                Intraline.AppendSafe(sb, text.Slice(s, t - s));
                sb.Append(ReverseOff);
                at = t;
            }

            Intraline.AppendSafe(sb, text.Slice(at));
        }

        if (coloured)
        {
            sb.Append(Reset);
        }

        sb.Append('\n');
        if (!terminated)
        {
            if (Color)
            {
                sb.Append(Dim);
            }

            sb.Append("\\ No newline at end of file");
            if (Color)
            {
                sb.Append(Reset);
            }

            sb.Append('\n');
        }
    }
}
