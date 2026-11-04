using OneDiff.Output;

namespace OneDiff;

public static partial class Diff
{
    /// <summary>
    /// Renders a line diff for a terminal: red deletions, green insertions, changed words picked out inside replaced lines, three
    /// lines of context. For other settings use <see cref="TerminalRenderer"/>.
    /// </summary>
    /// <param name="diff">A diff made with line tokens (<see cref="Lines"/>).</param>
    /// <param name="color">False for plain <c>+</c>/<c>-</c> lines without ANSI sequences. True when omitted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="diff"/> was not made with line tokens.</exception>
    public static string ToTerminal(TextDiff diff, bool color = true) => new TerminalRenderer { Color = color }.Render(diff);

    /// <summary>Renders a line diff as a fenced Markdown <c>diff</c> block with three lines of context. See <see cref="MarkdownRenderer"/>.</summary>
    /// <param name="diff">A diff made with line tokens (<see cref="Lines"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="diff"/> was not made with line tokens.</exception>
    public static string ToMarkdown(TextDiff diff) => new MarkdownRenderer().RenderLines(diff);

    /// <summary>Renders any text diff as Markdown with <c>&lt;del&gt;</c> and <c>&lt;ins&gt;</c> marks. See <see cref="MarkdownRenderer.RenderInline"/>.</summary>
    /// <param name="diff">A diff of any token kind; word diffs read best.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diff"/> is null.</exception>
    public static string ToMarkdownInline(TextDiff diff) => MarkdownRenderer.RenderInline(diff);
}
