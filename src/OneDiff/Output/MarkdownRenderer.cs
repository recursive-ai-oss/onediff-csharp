using System.Text;

namespace OneDiff.Output;

/// <summary>
/// Renders diffs as Markdown. <see cref="RenderLines"/> gives a fenced <c>diff</c> block for a line diff; <see cref="RenderInline"/>
/// gives the new text with deletions in <c>&lt;del&gt;</c> and insertions in <c>&lt;ins&gt;</c>, for a word-level (or any other) diff.
/// Output is deterministic and lines end in <c>\n</c>.
/// </summary>
public sealed record MarkdownRenderer
{
    private readonly int contextLines = 3;

    /// <summary>Unchanged lines shown around each change in <see cref="RenderLines"/>. 3 by default.</summary>
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

    /// <summary>Name for a <c>--- </c> line at the top of the block; no file header when both names are null. Null by default.</summary>
    public string? OldName { get; init; }

    /// <summary>Name for a <c>+++ </c> line at the top of the block. Null by default.</summary>
    public string? NewName { get; init; }

    /// <summary>
    /// Renders a line diff as a fenced <c>diff</c> block. The fence is longer than any run of backticks in the content, so the
    /// block cannot be closed early. An identical pair renders as the empty string.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="diff"/> was not made with line tokens.</exception>
    public string RenderLines(TextDiff diff)
    {
        LineHunks.RequireLines(diff);
        List<LineHunk> hunks = LineHunks.Build(diff, contextLines);
        if (hunks.Count == 0)
        {
            return string.Empty;
        }

        var body = new StringBuilder();
        if (OldName is not null || NewName is not null)
        {
            body.Append("--- ").Append(OldName).Append('\n');
            body.Append("+++ ").Append(NewName).Append('\n');
        }

        foreach (LineHunk hunk in hunks)
        {
            body.Append(hunk.Header()).Append('\n');
            foreach (HunkLine l in hunk.Lines)
            {
                bool terminated;
                switch (l.Kind)
                {
                    case EditKind.Delete:
                        body.Append('-').Append(LineHunks.OldLine(diff, l.OldIndex, out terminated));
                        break;
                    case EditKind.Insert:
                        body.Append('+').Append(LineHunks.NewLine(diff, l.NewIndex, out terminated));
                        break;
                    default:
                        body.Append(' ').Append(LineHunks.NewLine(diff, l.NewIndex, out terminated));
                        break;
                }

                body.Append('\n');
                if (!terminated)
                {
                    body.Append("\\ No newline at end of file\n");
                }
            }
        }

        string content = body.ToString();
        int longest = 0, run = 0;
        foreach (char c in content)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        string fence = new('`', Math.Max(3, longest + 1));
        return fence + "diff\n" + content + fence + "\n";
    }

    /// <summary>
    /// Renders the new text with each deletion wrapped in <c>&lt;del&gt;</c> and each insertion in <c>&lt;ins&gt;</c>. Works for any
    /// token kind; word diffs read best. A run that spans line breaks is wrapped line by line (the breaks stay outside the tags)
    /// so Markdown block structure survives. <c>&amp;</c> and <c>&lt;</c> in the text are escaped; other Markdown is left as written.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is null.</exception>
    public static string RenderInline(TextDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var sb = new StringBuilder();
        foreach (Edit e in diff.Script.Edits)
        {
            switch (e.Kind)
            {
                case EditKind.Equal:
                    AppendEscaped(sb, diff.NewSpan(e));
                    break;
                case EditKind.Delete:
                    AppendWrapped(sb, "del", diff.OldSpan(e));
                    break;
                default:
                    AppendWrapped(sb, "ins", diff.NewSpan(e));
                    break;
            }
        }

        return sb.ToString();
    }

    private static void AppendWrapped(StringBuilder sb, string tag, ReadOnlySpan<char> text)
    {
        while (text.Length > 0)
        {
            int brk = text.IndexOfAny('\r', '\n');
            ReadOnlySpan<char> part = brk < 0 ? text : text.Slice(0, brk);
            if (part.Length > 0)
            {
                sb.Append('<').Append(tag).Append('>');
                AppendEscaped(sb, part);
                sb.Append("</").Append(tag).Append('>');
            }

            if (brk < 0)
            {
                break;
            }

            int stop = brk;
            while (stop < text.Length && (text[stop] == '\r' || text[stop] == '\n'))
            {
                stop++;
            }

            sb.Append(text.Slice(brk, stop - brk));
            text = text.Slice(stop);
        }
    }

    private static void AppendEscaped(StringBuilder sb, ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
    }
}
