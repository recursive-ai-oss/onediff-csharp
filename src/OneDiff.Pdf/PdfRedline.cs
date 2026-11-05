using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Pdf.Internal;
using OnePdf;

namespace OneDiff.Pdf;

/// <summary>One annotation a markup writer added: the change it shows, the page, its name and its kind.</summary>
/// <param name="ChangeId">The id of the <see cref="Change"/>.</param>
/// <param name="Page">The zero-based page index.</param>
/// <param name="Name">The annotation's <c>/NM</c>: <c>onediff-c{change}-p{page}</c>, with <c>.{n}</c> appended for the n-th further annotation of the same change on the same page.</param>
/// <param name="Kind">The annotation subtype.</param>
/// <param name="QuadCount">How many quadrilaterals it covers (0 for a caret).</param>
public sealed record PdfMark(int ChangeId, int Page, string Name, PdfMarkupKind Kind, int QuadCount);

/// <summary>A change the writer did not mark, and why.</summary>
/// <param name="ChangeId">The id of the <see cref="Change"/>.</param>
/// <param name="Reason">A sentence: an empty container (its content carries its own changes), or an anchor the writer cannot place.</param>
public sealed record PdfSkippedChange(int ChangeId, string Reason);

/// <summary>What a markup writer did.</summary>
/// <param name="Marks">The annotations added, in the order they were added.</param>
/// <param name="Skipped">The changes that got no annotation.</param>
/// <param name="SavedIncrementally">True when the output is the original bytes followed by an update. False when the whole file was rewritten, either because <see cref="PdfRedline.Incremental"/> is off or because the input needed repair on opening and cannot take an update.</param>
public sealed record PdfMarkupResult(ImmutableArray<PdfMark> Marks, ImmutableArray<PdfSkippedChange> Skipped, bool SavedIncrementally);

/// <summary>
/// Marks a comparison's changes on the original pages of PDF inputs with real markup annotations (ISO 32000-2 12.5.6.10 to
/// 12.5.6.14), so that the pages stay exactly as they were and the review sits on top of them. An immutable value; derive variants
/// with <c>with</c>. <see cref="OnPages"/> is the preset.
/// </summary>
/// <remarks>
/// <para><b>On the new document</b> (<see cref="MarkNewDocument"/>), by kind of change:</para>
/// <list type="bullet">
/// <item><description>inserted text: a Highlight in the style's insertion colour; the pop-up says "Inserted";</description></item>
/// <item><description>replaced text (also a split or a merge): a Highlight in the insertion colour; the pop-up says "Replaced" and holds the old text;</description></item>
/// <item><description>deleted text: a Caret at the place it was, in the deletion colour; the pop-up holds the old text;</description></item>
/// <item><description>moved text: a Highlight in the move colour; the pop-up holds the old text;</description></item>
/// <item><description>formatting changes: an Underline on the text; the pop-up describes the old and new formatting;</description></item>
/// <item><description>attribute changes (list labels): a Squiggly on the item; the pop-up names the attribute and its old value.</description></item>
/// </list>
/// <para>
/// The annotation's <c>/Contents</c> is a first line naming the change (starting with the style's glyph, "+" or "−") and, after the
/// first line feed, the change's old text exactly. <see cref="SplitContents"/> takes it apart. <c>/NM</c> names the change and page
/// (see <see cref="PdfMark.Name"/>), so every annotation maps back to one change. A change whose text spans several pages gets one
/// annotation per page; a change on a container with no text of its own gets none (its content carries its own changes) and is
/// listed in <see cref="PdfMarkupResult.Skipped"/>.
/// </para>
/// <para>
/// <b>On the old document</b> (<see cref="MarkOldDocument"/>), the optional counterpart for a linked pair: deleted and replaced text gets a
/// StrikeOut, moved text a Highlight in the move-from colour, formatting changes an Underline; the pop-up holds the new text.
/// </para>
/// <para>Output is deterministic: the same comparison, input and settings give the same bytes.</para>
/// </remarks>
public sealed class PdfRedline : RedlineForm
{
    private const int MaxQuadsPerAnnotation = 2048;

    /// <summary>Creates the default markup (same as <see cref="OnPages"/>).</summary>
    public PdfRedline() : base("pdf", [".pdf"], RedlineStyle.SideBySide)
    {
    }

    /// <summary>The preset for marking PDF pages: colour-blind-safe blue and orange highlights with "+" and "−" glyphs, author "OneDiff", no modification date, incremental save.</summary>
    public static PdfRedline OnPages { get; } = new();

    /// <summary>The look: colours and glyphs for insertions, deletions, moves and formatting. Default <see cref="RedlineStyle.SideBySide"/>; the decorations are fixed by annotation kind.</summary>
    public RedlineStyle Style { get; init; } = RedlineStyle.SideBySide;

    /// <summary>The author written to each annotation's <c>/T</c>. Default "OneDiff".</summary>
    public string Author { get; init; } = "OneDiff";

    /// <summary>The modification date written to each annotation's <c>/M</c>, or null (the default) to write none. The clock is never read.</summary>
    public DateTimeOffset? ModifiedDate { get; init; }

    /// <summary>
    /// Whether to save as an incremental update (the default), which leaves the original bytes untouched, so a signature over them
    /// stays intact where the signature's permissions allow markup, or to rewrite the whole file. A file that was damaged and
    /// rebuilt on opening cannot take an update and is always rewritten (<see cref="PdfMarkupResult.SavedIncrementally"/> says which happened).
    /// </summary>
    public bool Incremental { get; init; } = true;

    /// <summary>The password of an encrypted input, or null.</summary>
    public string? Password { get; init; }

    /// <summary>The rendered redline, inline layout: a new PDF laid out from the comparison (cover page, marks in the text, ledger, footer), whatever the inputs were. Same as <see cref="RenderedPdfRedline.Inline"/>.</summary>
    public static RenderedPdfRedline Rendered => RenderedPdfRedline.Inline;

    /// <summary>The rendered redline, side-by-side layout on a landscape page. Same as <see cref="RenderedPdfRedline.SideBySideLayout"/>.</summary>
    public static RenderedPdfRedline RenderedSideBySide => RenderedPdfRedline.SideBySideLayout;

    /// <summary>
    /// The form <see cref="Register"/> selects for <c>.pdf</c>: markup on the original pages when the comparison's new document was read
    /// from a PDF (<see cref="OnPages"/>), a <see cref="Rendered"/> redline otherwise (plain text, or any other kind of input).
    /// </summary>
    public static RedlineForm Auto { get; } = new AutoPdfRedline();

    /// <summary>True when the comparison's new document was read from a PDF by <see cref="PdfDocumentSource"/> in this process, so that <see cref="OnPages"/> can mark it.</summary>
    internal static bool CanMark(Comparison comparison) => PdfOrigins.TryGet(comparison.New, out _);

    /// <summary>
    /// Registers the PDF source and the <see cref="Auto"/> form as the output form of <c>.pdf</c> files, process-wide, so that
    /// <c>Redline.Create("a.pdf", "b.pdf", "out.pdf")</c> marks the new PDF's pages and <c>Redline.Create("a.txt", "b.txt", "out.pdf")</c>
    /// renders a redline. Safe to call more than once.
    /// </summary>
    public static void Register()
    {
        Redline.Register(new PdfDocumentSource());
        Redline.Register(Auto);
    }

    /// <summary>
    /// Writes the new PDF with the changes marked on its pages (<see cref="MarkNewDocument"/>), as a registered or explicit output
    /// form. The PDF's bytes are those the comparison's new document was read from by <see cref="PdfDocumentSource"/>; a style
    /// passed by the caller or chosen from the settings replaces <see cref="Style"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The comparison's new document was not read from a PDF by <see cref="PdfDocumentSource"/> in this process (for example it was read back from JSON); use <see cref="MarkNewDocument"/> with the bytes.</exception>
    public override void Write(Comparison comparison, Stream output, RedlineStyle style, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(style);
        if (!PdfOrigins.TryGet(comparison.New, out PdfOrigin origin))
        {
            throw new InvalidOperationException("the new document was not read from a PDF by PdfDocumentSource, so its bytes are not known; call MarkNewDocument with the PDF's bytes");
        }

        var use = new PdfRedline { Style = style, Author = Author, ModifiedDate = ModifiedDate, Incremental = Incremental, Password = Password ?? origin.Password };
        use.MarkNewDocument(comparison, origin.Bytes, output, cancellationToken);
    }

    /// <summary>Splits an annotation's <c>/Contents</c> written by this type into its first line and the old (or, on the old document, new) text after it.</summary>
    /// <param name="contents">The annotation text.</param>
    /// <returns>The first line (without its line feed) and the rest, which is empty when the text has one line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> is null.</exception>
    public static (string Label, string Text) SplitContents(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        int i = contents.IndexOf('\n', StringComparison.Ordinal);
        return i < 0 ? (contents, "") : (contents[..i], contents[(i + 1)..]);
    }

    /// <summary>Marks the changes on the new document's pages and writes the PDF.</summary>
    /// <param name="comparison">A comparison whose new document was read by <see cref="PdfDocumentSource"/>.</param>
    /// <param name="newPdf">The bytes of the new PDF; their SHA-256 must be the comparison's new source hash.</param>
    /// <param name="output">Where to write the marked PDF.</param>
    /// <param name="cancellationToken">Observed between changes.</param>
    /// <returns>The annotations added and the changes skipped.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="newPdf"/> is not the input the comparison read.</exception>
    /// <exception cref="InvalidDataException">The PDF cannot be opened (damaged, or encrypted without <see cref="Password"/>).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; nothing is written.</exception>
    public PdfMarkupResult MarkNewDocument(Comparison comparison, ReadOnlyMemory<byte> newPdf, Stream output, CancellationToken cancellationToken = default) =>
        Mark(comparison, newPdf, output, markNew: true, cancellationToken);

    /// <summary>Marks the changes on the old document's pages (see the type remarks) and writes the PDF.</summary>
    /// <param name="comparison">A comparison whose old document was read by <see cref="PdfDocumentSource"/>.</param>
    /// <param name="oldPdf">The bytes of the old PDF; their SHA-256 must be the comparison's old source hash.</param>
    /// <param name="output">Where to write the marked PDF.</param>
    /// <param name="cancellationToken">Observed between changes.</param>
    /// <returns>The annotations added and the changes skipped.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="oldPdf"/> is not the input the comparison read.</exception>
    /// <exception cref="InvalidDataException">The PDF cannot be opened.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; nothing is written.</exception>
    public PdfMarkupResult MarkOldDocument(Comparison comparison, ReadOnlyMemory<byte> oldPdf, Stream output, CancellationToken cancellationToken = default) =>
        Mark(comparison, oldPdf, output, markNew: false, cancellationToken);

    private PdfMarkupResult Mark(Comparison comparison, ReadOnlyMemory<byte> pdfBytes, Stream output, bool markNew, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(Style);
        ArgumentNullException.ThrowIfNull(Author);
        CompareDocument doc = markNew ? comparison.New : comparison.Old;
        string hash = Convert.ToHexStringLower(SHA256.HashData(pdfBytes.Span));
        if (!string.Equals(hash, doc.SourceHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"the PDF is not the {(markNew ? "new" : "old")} input of the comparison (the hashes differ)", nameof(pdfBytes));
        }

        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(pdfBytes, new PdfOpenOptions { AllowEditing = true, Password = Password });
        }
        catch (PdfException ex)
        {
            throw PdfSourceErrors.Make(ex is PdfPasswordException or PdfEncryptionException ? PdfSourceFailure.Encrypted : PdfSourceFailure.Malformed, "the PDF cannot be opened for marking", ex);
        }

        using (pdf)
        {
            var marks = ImmutableArray.CreateBuilder<PdfMark>();
            var skipped = ImmutableArray.CreateBuilder<PdfSkippedChange>();
            var planner = new MarkPlanner(this, comparison, markNew);
            var geometry = new Dictionary<int, PageGeometry>();
            foreach (Change change in comparison.Changes)
            {
                ct.ThrowIfCancellationRequested();
                MarkPlan? plan = planner.Plan(change, out string? why);
                if (plan is null)
                {
                    skipped.Add(new PdfSkippedChange(change.Id, why!));
                    continue;
                }

                if (plan.Pages.Any(p => p.Page >= pdf.PageCount))
                {
                    skipped.Add(new PdfSkippedChange(change.Id, "the anchor names a page the PDF does not have"));
                    continue;
                }

                foreach (PagePlan pp in plan.Pages)
                {
                    PdfPage page = pdf.Pages[pp.Page];
                    if (!geometry.TryGetValue(pp.Page, out PageGeometry g))
                    {
                        geometry[pp.Page] = g = PageGeometry.Of(page);
                    }

                    AddOnPage(page, g, change.Id, plan, pp, marks);
                }
            }

            ct.ThrowIfCancellationRequested();
            // Saved to memory first: a file that was rebuilt by scanning cannot take an update, and then the whole file is
            // written instead, which must not leave half an update in the caller's stream.
            using var buffer = new MemoryStream();
            bool incremental = Incremental;
            if (incremental)
            {
                try
                {
                    pdf.SaveIncremental(buffer, cancellationToken: ct);
                }
                catch (PdfFormatException)
                {
                    buffer.SetLength(0);
                    incremental = false;
                }
            }

            if (!incremental)
            {
                pdf.Save(buffer, cancellationToken: ct);
            }

            buffer.WriteTo(output);
            return new PdfMarkupResult(marks.ToImmutable(), skipped.ToImmutable(), incremental);
        }
    }

    private void AddOnPage(PdfPage page, PageGeometry g, int changeId, MarkPlan plan, PagePlan pp, ImmutableArray<PdfMark>.Builder marks)
    {
        IReadOnlyList<double> color = ParseColor(plan.Color);
        if (plan.Kind == PdfMarkupKind.Caret)
        {
            PdfRectangle rect = ToUserRectangle(g, pp.CaretBox);
            Add(page, changeId, pp.Page, new PdfMarkupAnnotationOptions
            {
                Kind = PdfMarkupKind.Caret,
                Rectangle = rect,
                Color = color,
                Contents = plan.Contents,
                Title = Author,
                Name = AnnotationName(changeId, pp.Page, 0),
                ModifiedDate = ModifiedDate,
                Popup = Popup(rect, page),
            }, marks);
            return;
        }

        List<Quad> quads = MergeQuads(pp.Quads);
        for (int start = 0, part = 0; start < quads.Count; start += MaxQuadsPerAnnotation, part++)
        {
            int n = Math.Min(MaxQuadsPerAnnotation, quads.Count - start);
            var user = new List<PdfMarkupQuad>(n);
            for (int i = start; i < start + n; i++)
            {
                Quad q = quads[i];
                (double blx, double bly) = g.ToUser(q.X1, q.Y1);
                (double brx, double bry) = g.ToUser(q.X2, q.Y2);
                (double trx, double try_) = g.ToUser(q.X3, q.Y3);
                (double tlx, double tly) = g.ToUser(q.X4, q.Y4);
                user.Add(new PdfMarkupQuad(tlx, tly, trx, try_, blx, bly, brx, bry));
            }

            PdfRectangle bounds = Bounds(user);
            Add(page, changeId, pp.Page, new PdfMarkupAnnotationOptions
            {
                Kind = plan.Kind,
                Quads = user,
                Color = color,
                Contents = part == 0 ? plan.Contents : plan.Contents.Split('\n')[0],
                Title = Author,
                Name = AnnotationName(changeId, pp.Page, part),
                ModifiedDate = ModifiedDate,
                Popup = part == 0 ? Popup(bounds, page) : null,
            }, marks);
        }
    }

    private static void Add(PdfPage page, int changeId, int pageIndex, PdfMarkupAnnotationOptions options, ImmutableArray<PdfMark>.Builder marks)
    {
        page.AddMarkupAnnotation(options);
        marks.Add(new PdfMark(changeId, pageIndex, options.Name!, options.Kind, options.Quads.Count));
    }

    private static string AnnotationName(int changeId, int page, int part) =>
        "onediff-c" + changeId.ToString(CultureInfo.InvariantCulture) + "-p" + page.ToString(CultureInfo.InvariantCulture)
        + (part == 0 ? "" : "." + part.ToString(CultureInfo.InvariantCulture));

    /// <summary>The pop-up window sits to the right of the marked area when there is room, else to its left, and inside the page.</summary>
    private static PdfMarkupPopup Popup(PdfRectangle annotation, PdfPage page)
    {
        PdfRectangle box = page.CropBox;
        const double width = 220, height = 120;
        double left = annotation.Right + 8 + width <= box.Right ? annotation.Right + 8 : Math.Max(box.Left, annotation.Left - 8 - width);
        left = Math.Clamp(left, box.Left, Math.Max(box.Left, box.Right - width));
        double top = Math.Clamp(annotation.Top, box.Bottom + height, Math.Max(box.Bottom + height, box.Top));
        double right = Math.Min(box.Right, left + width);
        double bottom = Math.Max(box.Bottom, top - height);
        if (right - left < 1 || top - bottom < 1)
        {
            // A page smaller than the window: the whole page is the window.
            return new PdfMarkupPopup(box, Open: false);
        }

        return new PdfMarkupPopup(new PdfRectangle(left, bottom, right, top), Open: false);
    }

    private static PdfRectangle Bounds(List<PdfMarkupQuad> quads)
    {
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        foreach (PdfMarkupQuad q in quads)
        {
            l = Math.Min(l, Math.Min(Math.Min(q.UpperLeftX, q.UpperRightX), Math.Min(q.LowerLeftX, q.LowerRightX)));
            r = Math.Max(r, Math.Max(Math.Max(q.UpperLeftX, q.UpperRightX), Math.Max(q.LowerLeftX, q.LowerRightX)));
            b = Math.Min(b, Math.Min(Math.Min(q.UpperLeftY, q.UpperRightY), Math.Min(q.LowerLeftY, q.LowerRightY)));
            t = Math.Max(t, Math.Max(Math.Max(q.UpperLeftY, q.UpperRightY), Math.Max(q.LowerLeftY, q.LowerRightY)));
        }

        return new PdfRectangle(l, b, r, t);
    }

    private static PdfRectangle ToUserRectangle(PageGeometry g, PdfRectangle upright)
    {
        (double x0, double y0) = g.ToUser(upright.Left, upright.Bottom);
        (double x1, double y1) = g.ToUser(upright.Right, upright.Top);
        return new PdfRectangle(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    internal static IReadOnlyList<double> ParseColor(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#'
            && int.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
        {
            return [((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0];
        }

        return [0, 0, 0];
    }

    /// <summary>
    /// Joins quadrilaterals that sit on one line, one after another, into one, so a highlighted phrase is one band rather than a
    /// string of boxes with gaps at the spaces. Quadrilaterals are neighbours when the next starts within two line heights after
    /// the previous ends, on the same baseline.
    /// </summary>
    internal static List<Quad> MergeQuads(IReadOnlyList<Quad> quads)
    {
        var result = new List<Quad>(quads.Count);
        foreach (Quad q in quads)
        {
            if (result.Count > 0 && TryJoin(result[^1], q, out Quad joined))
            {
                result[^1] = joined;
            }
            else
            {
                result.Add(q);
            }
        }

        return result;
    }

    private static bool TryJoin(Quad p, Quad n, out Quad joined)
    {
        joined = default;
        if (p.Page != n.Page)
        {
            return false;
        }

        double ux = p.X2 - p.X1, uy = p.Y2 - p.Y1;
        double len = Math.Sqrt(ux * ux + uy * uy);
        double hx = p.X4 - p.X1, hy = p.Y4 - p.Y1;
        double height = Math.Sqrt(hx * hx + hy * hy);
        if (len < 1e-6 || height < 1e-6)
        {
            return false;
        }

        ux /= len;
        uy /= len;
        double dx = n.X1 - p.X2, dy = n.Y1 - p.Y2;
        double along = dx * ux + dy * uy, across = -dx * uy + dy * ux;
        if (Math.Abs(across) > 0.3 * height || along < -0.2 * height || along > 2.0 * height)
        {
            return false;
        }

        joined = new Quad(p.Page, p.X1, p.Y1, n.X2, n.Y2, n.X3, n.Y3, p.X4, p.Y4);
        return true;
    }
}
