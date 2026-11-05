using System.Collections.Immutable;
using OneDiff.Documents.Combine;
using OneDiff.Documents.Model;

namespace OneDiff.Documents;

/// <summary>
/// The happy path: compare two documents and write the redline in one call. Inputs are recognized by their content, never by
/// file name; the output form follows the output file's extension unless one is given. The statics hold no options of their
/// own: the look and the rules come from <see cref="CompareSettings"/> and <see cref="RedlineStyle"/>.
/// <para>
/// This package reads plain text and writes JSON and HTML. Adapter packages add inputs and output forms by registering them
/// once at startup (each exposes one <c>Register</c> call); an input or
/// extension nobody registered fails with a message naming the package to add. Registration is process-wide, idempotent and
/// thread-safe. Code that wants no global state skips the statics and uses
/// <see cref="CompareDocument.From(DocumentInput, IDocumentSource?, CancellationToken)"/>, <see cref="Comparison.Of(CompareDocument, CompareDocument, CompareSettings?, Scoring.IPairScorer?, CancellationToken)"/>
/// and a <see cref="RedlineForm"/> directly.
/// </para>
/// </summary>
public static class Redline
{
    private static readonly object Gate = new();
    private static ImmutableArray<RedlineForm> forms = [RedlineForm.Html, RedlineForm.Json];

    /// <summary>The registered output forms, in registration order (this package's own first).</summary>
    public static IReadOnlyList<RedlineForm> Forms => forms;

    /// <summary>Adds an input source to the process-wide list used when reading inputs by content. Same as <see cref="DocumentSources.Register"/>.</summary>
    /// <param name="source">The source.</param>
    /// <param name="priority">Higher is asked first, whatever the order of registration; see <see cref="DocumentSources.Register"/>.</param>
    public static void Register(IDocumentSource source, int priority = 0) => DocumentSources.Register(source, priority);

    /// <summary>
    /// Adds an output form so that its extensions select it. A later registration of the same extension wins; registering a
    /// form with the same name again replaces the earlier one.
    /// </summary>
    public static void Register(RedlineForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        lock (Gate)
        {
            forms = [.. forms.Where(f => !string.Equals(f.Name, form.Name, StringComparison.Ordinal)), form];
        }
    }

    /// <summary>The registered form that the extension of <paramref name="path"/> selects (the latest registered wins).</summary>
    /// <exception cref="NotSupportedException">No registered form claims the extension. If another package writes it, the message says which.</exception>
    public static RedlineForm FormFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        ImmutableArray<RedlineForm> snapshot = forms;
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            foreach (string e in snapshot[i].Extensions)
            {
                if (string.Equals(e, ext, StringComparison.Ordinal))
                {
                    return snapshot[i];
                }
            }
        }

        string? advice = FormatHints.PackageForExtension(ext);
        throw new NotSupportedException(
            ext.Length == 0
                ? "The output path has no extension; name the form explicitly."
                : $"No output form is registered for '{ext}'. " + (advice ?? "Name the form explicitly, or register one with Redline.Register."));
    }

    /// <summary>Compares two files and writes the redline to <paramref name="outputPath"/> in the form its extension selects.</summary>
    /// <param name="oldPath">The original file.</param>
    /// <param name="newPath">The revised file.</param>
    /// <param name="outputPath">Where to write; replaced if it exists. The directory must exist.</param>
    /// <param name="settings">What to show and hide; <see cref="CompareSettings.Legal"/> when null.</param>
    /// <param name="cancellationToken">Observed throughout.</param>
    /// <exception cref="NotSupportedException">An input is not recognized, or no form is registered for the extension.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    public static void Create(string oldPath, string newPath, string outputPath, CompareSettings? settings = null, CancellationToken cancellationToken = default) =>
        Create(oldPath, newPath, outputPath, FormFor(outputPath), settings, cancellationToken);

    /// <summary>Compares two files and writes the redline to <paramref name="outputPath"/> in an explicit form; the extension is not consulted.</summary>
    /// <exception cref="NotSupportedException">An input is not recognized.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    public static void Create(string oldPath, string newPath, string outputPath, RedlineForm form, CompareSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(form);
        Comparison.Of(oldPath, newPath, settings, null, cancellationToken).SaveAs(outputPath, form, null, cancellationToken);
    }

    /// <summary>Compares two streams (each read to its end) and writes the redline to <paramref name="output"/>.</summary>
    /// <exception cref="NotSupportedException">An input is not recognized.</exception>
    public static void Create(Stream oldStream, Stream newStream, Stream output, RedlineForm form, CompareSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldStream);
        ArgumentNullException.ThrowIfNull(newStream);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(form);
        Comparison c = Comparison.Of(DocumentInput.FromStream(oldStream, "old"), DocumentInput.FromStream(newStream, "new"), settings, null, cancellationToken);
        c.Save(output, form, null, cancellationToken);
    }

    /// <summary>
    /// Compares two files and writes the redline asynchronously: the files are read and the output written without blocking, and the
    /// comparison itself runs synchronously on the calling thread. The form follows the extension of <paramref name="outputPath"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">An input is not recognized, or no form is registered for the extension.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static Task CreateAsync(string oldPath, string newPath, string outputPath, CompareSettings? settings = null, CancellationToken cancellationToken = default) =>
        CreateAsync(oldPath, newPath, outputPath, FormFor(outputPath), settings, cancellationToken);

    /// <summary>Compares two files and writes the redline asynchronously in an explicit form; the extension is not consulted.</summary>
    /// <exception cref="NotSupportedException">An input is not recognized.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    public static async Task CreateAsync(string oldPath, string newPath, string outputPath, RedlineForm form, CompareSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(form);
        Comparison c = await Comparison.OfAsync(oldPath, newPath, settings, null, cancellationToken).ConfigureAwait(false);
        await c.SaveAsync(outputPath, form, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compares two streams (each read asynchronously to its end) and writes the redline to <paramref name="output"/> asynchronously.
    /// This is the form to use with an ASP.NET Core request and response body, which refuse synchronous I/O by default. The comparison
    /// itself runs synchronously on the calling thread. No stream is closed.
    /// </summary>
    /// <exception cref="NotSupportedException">An input is not recognized.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task CreateAsync(Stream oldStream, Stream newStream, Stream output, RedlineForm form, CompareSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(form);
        Comparison c = await Comparison.OfAsync(oldStream, newStream, settings, null, cancellationToken).ConfigureAwait(false);
        await c.SaveAsync(output, form, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compares two byte arrays and returns the redline's bytes.</summary>
    /// <exception cref="NotSupportedException">An input is not recognized.</exception>
    public static byte[] Create(ReadOnlyMemory<byte> oldContent, ReadOnlyMemory<byte> newContent, RedlineForm form, CompareSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        Comparison c = Comparison.Of(DocumentInput.FromBytes(oldContent, "old"), DocumentInput.FromBytes(newContent, "new"), settings, null, cancellationToken);
        return c.ToBytes(form, null, cancellationToken);
    }

    /// <summary>
    /// Combines reviewers' copies of one original and writes the result: each change attributed to the reviewers who made it,
    /// identical changes merged, conflicts all shown and flagged. The output is the combination's change report as HTML
    /// (<c>.html</c>) or its JSON form (<c>.json</c>); adapter packages add other forms to <see cref="Combine.Combination"/>.
    /// </summary>
    /// <param name="originalPath">The original every copy started from.</param>
    /// <param name="copyPaths">The reviewers' copies, at least one, in reviewer order.</param>
    /// <param name="outputPath">Where to write; replaced if it exists. The directory must exist.</param>
    /// <param name="settings">What to show and hide in each comparison; <see cref="CompareSettings.Legal"/> when null.</param>
    /// <param name="cancellationToken">Observed throughout.</param>
    /// <exception cref="ArgumentException">There is no copy.</exception>
    /// <exception cref="NotSupportedException">An input is not recognized, or the output extension is not built in.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    public static void Combine(string originalPath, IReadOnlyList<string> copyPaths, string outputPath, CompareSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalPath);
        ArgumentNullException.ThrowIfNull(copyPaths);
        ArgumentNullException.ThrowIfNull(outputPath);
        CompareDocument original = Read(originalPath, cancellationToken);
        var copies = new List<CompareDocument>(copyPaths.Count);
        foreach (string p in copyPaths)
        {
            copies.Add(Read(p, cancellationToken));
        }

        Combination.Of(original, copies, settings, null, cancellationToken).SaveAs(outputPath);
    }

    /// <summary>
    /// Compares a chain of versions v1 → … → vN and writes the chain's change report as HTML (<c>.html</c>) or its JSON form
    /// (<c>.json</c>): every step verified, every change attributed to its step, plus the net change from the first to the last.
    /// </summary>
    /// <param name="versionPaths">The versions, oldest first; at least two.</param>
    /// <param name="outputPath">Where to write; replaced if it exists. The directory must exist.</param>
    /// <param name="settings">What to show and hide; <see cref="CompareSettings.Legal"/> when null.</param>
    /// <param name="cancellationToken">Observed throughout.</param>
    /// <exception cref="ArgumentException">There are fewer than two versions.</exception>
    /// <exception cref="NotSupportedException">An input is not recognized, or the output extension is not built in.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    public static void Chain(IReadOnlyList<string> versionPaths, string outputPath, CompareSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionPaths);
        ArgumentNullException.ThrowIfNull(outputPath);
        var versions = new List<CompareDocument>(versionPaths.Count);
        foreach (string p in versionPaths)
        {
            versions.Add(Read(p, cancellationToken));
        }

        ComparisonChain.Of(versions, settings, null, cancellationToken).SaveAs(outputPath);
    }

    private static CompareDocument Read(string path, CancellationToken cancellationToken) =>
        CompareDocument.From(DocumentInput.FromBytes(File.ReadAllBytes(path), Path.GetFileName(path)), null, cancellationToken);
}
