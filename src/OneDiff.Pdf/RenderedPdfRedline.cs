using System.Collections.Immutable;
using OneDiff.Documents;
using OneDiff.Documents.Html;
using OneRender.Pdf;

namespace OneDiff.Pdf;

/// <summary>The paper a rendered redline is laid out for.</summary>
public enum PdfPaper
{
    /// <summary>ISO A4, 210 × 297 mm (the default).</summary>
    A4,

    /// <summary>US Letter, 8.5 × 11 in.</summary>
    Letter,
}

/// <summary>
/// Turns a <see cref="Comparison"/> of any kind of input, plain text included, into a redline PDF that is laid out and drawn from the
/// comparison alone: the same marks, cover page, legend, ledger and footer as the HTML redline, set on paper (A4 by default, with the
/// running page number in the bottom margin) and written by the OneRender engine as a tagged PDF with the document language and title
/// set. Inline is the default layout; <see cref="SideBySide"/> sets old and new in two columns on a landscape page.
/// </summary>
/// <remarks>
/// <para>
/// The page is a static document: its style sheet is derived from the <see cref="RedlineStyle"/> and nothing in it needs a script, a
/// network or a font beyond the ones the renderer carries (Liberation Sans for the page; a script the carried fonts do not cover is drawn
/// with the renderer's fallback face). Output is byte-identical for the same comparison and style: the clock is never read, and the
/// file carries no creation date.
/// </para>
/// <para>
/// The text-for-screen-readers labels of the HTML page ("inserted:", "deleted:") are not written; the colour, the decoration and the
/// glyph of the style, which the legend explains, mark each change. The structure tree (headings, paragraphs, tables, the legend and ledger
/// lists) is the renderer's; every character of the documents' text is real, extractable text.
/// </para>
/// <para>
/// The look is the comparison's settings style unless the caller passes one: <see cref="RedlineStyle.Blackline"/> for the inline layout,
/// <see cref="RedlineStyle.SideBySide"/> for the side-by-side layout when the settings left the style at the default.
/// </para>
/// </remarks>
public sealed class RenderedPdfRedline : RedlineForm
{
    /// <summary>Creates a rendered redline form.</summary>
    /// <param name="sideBySide">True for the side-by-side layout (old left, new right, landscape); false (the default) for the inline layout.</param>
    public RenderedPdfRedline(bool sideBySide = false) : base(sideBySide ? "pdf-rendered-side-by-side" : "pdf-rendered", [], sideBySide ? RedlineStyle.SideBySide : null)
    {
        SideBySide = sideBySide;
    }

    /// <summary>The inline layout: the new document in order with insertions and deletions marked in the text. Same as <see cref="PdfRedline.Rendered"/>.</summary>
    public static RenderedPdfRedline Inline { get; } = new(false);

    /// <summary>The side-by-side layout: old on the left, new on the right, on a landscape page. Same as <see cref="PdfRedline.RenderedSideBySide"/>.</summary>
    public static RenderedPdfRedline SideBySideLayout { get; } = new(true);

    /// <summary>True for the side-by-side layout.</summary>
    public bool SideBySide { get; }

    /// <summary>The paper. Default <see cref="PdfPaper.A4"/>. Set it on a new form: <c>new RenderedPdfRedline { Paper = PdfPaper.Letter }</c>.</summary>
    public PdfPaper Paper { get; init; } = PdfPaper.A4;

    /// <summary>Writes the redline PDF to <paramref name="output"/>, which is left open. Same as <see cref="Render"/>, without its result.</summary>
    /// <param name="comparison">What to write.</param>
    /// <param name="output">Where to write.</param>
    /// <param name="style">The look; see <see cref="RedlineForm.ResolveStyle"/>.</param>
    /// <param name="cancellationToken">Observed while laying out and writing.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The document needs more pages than the renderer allows (100,000), or more glyphs than its page list holds (several million characters).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the output is then incomplete.</exception>
    public override void Write(Comparison comparison, Stream output, RedlineStyle style, CancellationToken cancellationToken = default) =>
        Render(comparison, output, style, cancellationToken);

    /// <summary>Writes the redline PDF and reports what the renderer could not draw.</summary>
    /// <param name="comparison">What to write.</param>
    /// <param name="output">Where to write; left open.</param>
    /// <param name="style">The look; the comparison settings' style when null.</param>
    /// <param name="cancellationToken">Observed while laying out and writing.</param>
    /// <returns>The page count and the renderer's notes. A character that no font the renderer has covers is not drawn (its text stays in the file, so it can still be extracted and searched) and is noted with the opt-in font package that would draw it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="comparison"/> or <paramref name="output"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The document needs more pages than the renderer allows (100,000), or more glyphs than its page list holds (several million characters).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the output is then incomplete.</exception>
    public RenderedPdfResult Render(Comparison comparison, Stream output, RedlineStyle? style = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(output);
        string html = HtmlRedline.Static(comparison, style ?? ResolveStyle(comparison, null), SideBySide, Paper == PdfPaper.Letter ? "Letter" : "A4", cancellationToken);
        var options = new HtmlPdfOptions
        {
            Page = Paper == PdfPaper.Letter ? PdfPageSetup.Letter : PdfPageSetup.A4,
            Output = new PdfOutputOptions { Creator = "OneDiff " + Comparison.LibraryVersion, LeaveOpen = true },
        };
        try
        {
            HtmlPdfResult r = HtmlPdf.ToPdf(html, output, options, cancellationToken);
            var notes = ImmutableArray.CreateBuilder<string>(r.Diagnostics.Count);
            foreach (OneRender.Diagnostic d in r.Diagnostics)
            {
                notes.Add(d.Message);
            }

            return new RenderedPdfResult(r.PageCount, notes.MoveToImmutable());
        }
        catch (OneRender.Painting.DisplayListLimitException e)
        {
            // The renderer's hard limit on the glyphs of one page list (a redline of several million characters): say so, and name the
            // forms that have no such limit.
            throw new InvalidOperationException("the redline is larger than the renderer can lay out (" + e.Message + "); save it as HTML, JSON or markup on the original pages instead", e);
        }
        catch (PdfRenderException e)
        {
            throw new InvalidOperationException("the redline could not be rendered: " + e.Message, e);
        }
    }
}

/// <summary>What <see cref="RenderedPdfRedline.Render"/> did.</summary>
/// <param name="PageCount">The number of pages written.</param>
/// <param name="Notes">What the renderer approximated or left out, one sentence each (for example a script with no font).</param>
public sealed record RenderedPdfResult(int PageCount, ImmutableArray<string> Notes);

/// <summary>
/// The form registered for <c>.pdf</c> output: original-page markup when the comparison's new document was read from a PDF, a rendered
/// redline otherwise. Name a form explicitly to override the choice.
/// </summary>
internal sealed class AutoPdfRedline() : RedlineForm("pdf-auto", [".pdf"])
{
    public override void Write(Comparison comparison, Stream output, RedlineStyle style, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (PdfRedline.CanMark(comparison))
        {
            // Marks on the original pages default to the colour-blind-safe highlights, like the markup preset.
            PdfRedline.OnPages.Write(comparison, output, style == RedlineStyle.Blackline ? PdfRedline.OnPages.PreferredStyle! : style, cancellationToken);
        }
        else
        {
            PdfRedline.Rendered.Write(comparison, output, style, cancellationToken);
        }
    }
}
