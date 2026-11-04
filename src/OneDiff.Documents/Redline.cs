using System.Collections.Immutable;
using OneDiff.Documents.Model;

namespace OneDiff.Documents;

/// <summary>
/// The happy path: compare two documents and write the redline in one call. Inputs are recognized by their content, never by
/// file name; the output form follows the output file's extension unless one is given. The statics hold no options of their
/// own: the look and the rules come from <see cref="CompareSettings"/> and <see cref="RedlineStyle"/>.
/// <para>
/// This package reads plain text and writes JSON and HTML. Adapter packages add inputs and output forms by registering them
/// once at startup (each exposes one <c>Register</c> call, and the <c>.All</c> package one for all of them); an input or
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
    public static void Register(IDocumentSource source) => DocumentSources.Register(source);

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
                ? "the output path has no extension; name the form explicitly."
                : $"no output form is registered for '{ext}'. " + (advice ?? "Name the form explicitly, or register one with Redline.Register."));
    }

    /// <summary>Compares two files, read by content. Same as <see cref="Comparison.Of(string, string, CompareSettings?, Scoring.IPairScorer?, CancellationToken)"/>.</summary>
    public static Comparison Compare(string oldPath, string newPath, CompareSettings? settings = null, CancellationToken cancellationToken = default) =>
        Comparison.Of(oldPath, newPath, settings, null, cancellationToken);

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

    /// <summary>Compares two byte arrays and returns the redline's bytes.</summary>
    /// <exception cref="NotSupportedException">An input is not recognized.</exception>
    public static byte[] Create(ReadOnlyMemory<byte> oldContent, ReadOnlyMemory<byte> newContent, RedlineForm form, CompareSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        Comparison c = Comparison.Of(DocumentInput.FromBytes(oldContent, "old"), DocumentInput.FromBytes(newContent, "new"), settings, null, cancellationToken);
        return c.ToBytes(form, null, cancellationToken);
    }
}
