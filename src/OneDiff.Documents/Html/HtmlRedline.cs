using System.Text;

namespace OneDiff.Documents.Html;

/// <summary>
/// Writes a <see cref="Comparison"/> as one self-contained HTML page: inline CSS, no scripts, nothing fetched from anywhere,
/// system font stacks. Every character from the documents is escaped, so a page is safe to open whatever the inputs held.
/// The look comes from a <see cref="RedlineStyle"/>: mark colours and decorations, glyphs, change bars, cover page, footer, type
/// tags, language, title and an optional timestamp. Output is UTF-8 without a byte order mark, lines end in LF, and the same
/// comparison and style give the same bytes on every platform (the clock is never read).
/// <para>
/// Two layouts. <b>Inline</b>: the new document's blocks in order, inserted text in <c>&lt;ins&gt;</c>, deleted text in
/// <c>&lt;del&gt;</c>, deleted blocks and the old places of moved blocks where they used to be, with hidden labels for screen
/// readers. <b>Side by side</b>: a table with old on the left and new on the right, one row per block. Both end with the
/// ledger of differences the settings hide. Every element that stands for a change carries <c>data-change</c> with the
/// change's id; see docs/output-html.md for the page's structure.
/// </para>
/// </summary>
public static class HtmlRedline
{
    /// <summary>The inline layout as a string.</summary>
    /// <param name="comparison">What to write.</param>
    /// <param name="style">The look; the comparison settings' style (blackline by default) when null.</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    public static string Inline(Comparison comparison, RedlineStyle? style = null, CancellationToken cancellationToken = default)
    {
        using var sw = new StringWriter { NewLine = "\n" };
        Write(comparison, sw, style ?? comparison?.Settings.Style, sideBySide: false, cancellationToken);
        return sw.ToString();
    }

    /// <summary>The side-by-side layout as a string.</summary>
    /// <param name="comparison">What to write.</param>
    /// <param name="style">The look; the comparison settings' style when null.</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    public static string SideBySide(Comparison comparison, RedlineStyle? style = null, CancellationToken cancellationToken = default)
    {
        using var sw = new StringWriter { NewLine = "\n" };
        Write(comparison, sw, style ?? comparison?.Settings.Style, sideBySide: true, cancellationToken);
        return sw.ToString();
    }

    /// <summary>Writes the inline layout as UTF-8 to a stream, which is left open.</summary>
    /// <param name="comparison">What to write.</param>
    /// <param name="output">The destination.</param>
    /// <param name="style">The look; the comparison settings' style when null.</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    public static void WriteInline(Comparison comparison, Stream output, RedlineStyle? style = null, CancellationToken cancellationToken = default) =>
        WriteStream(comparison, output, style, sideBySide: false, cancellationToken);

    /// <summary>Writes the side-by-side layout as UTF-8 to a stream, which is left open.</summary>
    /// <param name="comparison">What to write.</param>
    /// <param name="output">The destination.</param>
    /// <param name="style">The look; the comparison settings' style when null.</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    public static void WriteSideBySide(Comparison comparison, Stream output, RedlineStyle? style = null, CancellationToken cancellationToken = default) =>
        WriteStream(comparison, output, style, sideBySide: true, cancellationToken);

    /// <summary>
    /// The page for a renderer that lays out paper (the PDF writers): the same marks and furniture, with a style sheet that needs
    /// no browser. Page size, margins and the page number come from an <c>@page</c> rule, text uses only the fonts a renderer
    /// carries, the text-for-screen-readers spans are left out, and the closed sections of the ledger are written open.
    /// </summary>
    internal static string Static(Comparison comparison, RedlineStyle? style, bool sideBySide, string paper, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        using var sw = new StringWriter { NewLine = "\n" };
        Write(comparison, sw, style ?? comparison.Settings.Style, sideBySide, cancellationToken, print: true, paper: paper);
        return sw.ToString();
    }

    private static void WriteStream(Comparison comparison, Stream output, RedlineStyle? style, bool sideBySide, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(output);
        using var sw = new StreamWriter(output, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" };
        Write(comparison, sw, style ?? comparison.Settings.Style, sideBySide, ct);
        sw.Flush();
    }

    private static void Write(Comparison comparison, TextWriter writer, RedlineStyle? style, bool sideBySide, CancellationToken ct, bool print = false, string paper = "A4")
    {
        ArgumentNullException.ThrowIfNull(comparison);
        RedlineStyle s = style ?? RedlineStyle.Blackline;
        var o = new HtmlOut(writer);
        RedlineView view = RedlineView.Build(comparison, s.MarkWholeWords, ct);
        HtmlPage.Begin(o, comparison, s, sideBySide, print, paper);
        if (sideBySide)
        {
            using var w = new SideBySideWriter(o, view, s, ct);
            w.Write();
        }
        else
        {
            new InlineWriter(o, view, s, ct).Write();
        }

        HtmlPage.End(o, comparison, s, print);
    }
}
