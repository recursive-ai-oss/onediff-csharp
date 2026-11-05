using OneDiff.Documents.Model;
using OneDiff.Documents.Scoring;
using OneDiff.Documents.Verification;

namespace OneDiff.Documents;

public sealed partial class Comparison
{
    /// <summary>Compares two files, read by content (the file name, without its directory, is only a display name).</summary>
    /// <param name="oldPath">The original file.</param>
    /// <param name="newPath">The revised file.</param>
    /// <param name="settings">What to show and hide; <see cref="CompareSettings.Legal"/> when null.</param>
    /// <param name="scorer">Scores ambiguous pairs; word overlap when null.</param>
    /// <param name="cancellationToken">Observed between stages and inside them.</param>
    /// <exception cref="NotSupportedException">An input is not recognized by any registered or built-in source.</exception>
    /// <exception cref="IOException">A file cannot be read.</exception>
    /// <exception cref="ComparisonVerificationException">Verification failed and the settings ask for an exception.</exception>
    public static Comparison Of(string oldPath, string newPath, CompareSettings? settings = null, IPairScorer? scorer = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        return Of(
            DocumentInput.FromBytes(File.ReadAllBytes(oldPath), Path.GetFileName(oldPath)),
            DocumentInput.FromBytes(File.ReadAllBytes(newPath), Path.GetFileName(newPath)),
            settings,
            scorer,
            cancellationToken);
    }

    /// <summary>Compares two inputs, each read by the first registered or built-in source that recognizes its content.</summary>
    /// <exception cref="NotSupportedException">An input is not recognized.</exception>
    /// <exception cref="ComparisonVerificationException">Verification failed and the settings ask for an exception.</exception>
    public static Comparison Of(DocumentInput oldInput, DocumentInput newInput, CompareSettings? settings = null, IPairScorer? scorer = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldInput);
        ArgumentNullException.ThrowIfNull(newInput);
        return Of(CompareDocument.From(oldInput, null, cancellationToken), CompareDocument.From(newInput, null, cancellationToken), settings, scorer, cancellationToken);
    }

    /// <summary>
    /// Compares two files, reading them asynchronously and then comparing synchronously on the calling thread. Everything else is as
    /// <see cref="Of(string, string, CompareSettings?, IPairScorer?, CancellationToken)"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">An input is not recognized by any registered or built-in source.</exception>
    /// <exception cref="IOException">A file cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<Comparison> OfAsync(string oldPath, string newPath, CompareSettings? settings = null, IPairScorer? scorer = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        byte[] oldBytes = await File.ReadAllBytesAsync(oldPath, cancellationToken).ConfigureAwait(false);
        byte[] newBytes = await File.ReadAllBytesAsync(newPath, cancellationToken).ConfigureAwait(false);
        return Of(DocumentInput.FromBytes(oldBytes, Path.GetFileName(oldPath)), DocumentInput.FromBytes(newBytes, Path.GetFileName(newPath)), settings, scorer, cancellationToken);
    }

    /// <summary>
    /// Compares two streams, each read asynchronously to its end (so a request body works under ASP.NET Core's default settings),
    /// then compares synchronously on the calling thread. The streams are not closed.
    /// </summary>
    /// <param name="oldStream">The original, read from its current position.</param>
    /// <param name="newStream">The revision, read from its current position.</param>
    /// <param name="settings">What to show and hide; <see cref="CompareSettings.Legal"/> when null.</param>
    /// <param name="scorer">Scores ambiguous pairs; word overlap when null.</param>
    /// <param name="cancellationToken">Observed while reading and comparing.</param>
    /// <exception cref="NotSupportedException">An input is not recognized.</exception>
    /// <exception cref="InvalidDataException">A stream is larger than 1 GiB.</exception>
    public static async Task<Comparison> OfAsync(Stream oldStream, Stream newStream, CompareSettings? settings = null, IPairScorer? scorer = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldStream);
        ArgumentNullException.ThrowIfNull(newStream);
        DocumentInput a = await DocumentInput.FromStreamAsync(oldStream, "old", cancellationToken: cancellationToken).ConfigureAwait(false);
        DocumentInput b = await DocumentInput.FromStreamAsync(newStream, "new", cancellationToken: cancellationToken).ConfigureAwait(false);
        return Of(a, b, settings, scorer, cancellationToken);
    }

    /// <summary>Writes the comparison to a file in the form its extension selects, with the settings' style.</summary>
    /// <param name="path">The output file; replaced if it exists. The directory must exist.</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    /// <exception cref="NotSupportedException">No registered form claims the extension.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public void SaveAs(string path, CancellationToken cancellationToken = default) => SaveAs(path, Redline.FormFor(path), null, cancellationToken);

    /// <summary>Writes the comparison to a file in the form its extension selects, in the given look.</summary>
    /// <param name="path">The output file; replaced if it exists. The directory must exist.</param>
    /// <param name="style">The look.</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    /// <exception cref="NotSupportedException">No registered form claims the extension.</exception>
    public void SaveAs(string path, RedlineStyle style, CancellationToken cancellationToken = default) => SaveAs(path, Redline.FormFor(path), style, cancellationToken);

    /// <summary>Writes the comparison to a file in an explicit form.</summary>
    /// <param name="path">The output file; replaced if it exists. Nothing is written if the form fails.</param>
    /// <param name="form">The output form.</param>
    /// <param name="style">The look; when null, the settings' style (or the form's preferred one while the settings are at the default).</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    public void SaveAs(string path, RedlineForm form, RedlineStyle? style = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, ToBytes(form, style, cancellationToken));
    }

    /// <summary>Writes the comparison to a stream, which is not closed.</summary>
    /// <param name="output">The destination.</param>
    /// <param name="form">The output form.</param>
    /// <param name="style">The look; see <see cref="RedlineForm.ResolveStyle"/>.</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    public void Save(Stream output, RedlineForm form, RedlineStyle? style = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(form);
        form.Write(this, output, form.ResolveStyle(this, style), cancellationToken);
    }

    /// <summary>Writes the comparison to memory.</summary>
    /// <param name="form">The output form.</param>
    /// <param name="style">The look; see <see cref="RedlineForm.ResolveStyle"/>.</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    public byte[] ToBytes(RedlineForm form, RedlineStyle? style = null, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        Save(ms, form, style, cancellationToken);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the comparison to a file asynchronously: the output is built in memory (synchronously, on the calling thread) and
    /// then written to <paramref name="path"/> without blocking on the file. The form is the one the extension selects.
    /// </summary>
    /// <param name="path">The output file; replaced if it exists. Nothing is written if the form fails.</param>
    /// <param name="style">The look; see <see cref="RedlineForm.ResolveStyle"/>.</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    /// <exception cref="NotSupportedException">No registered form claims the extension.</exception>
    public Task SaveAsync(string path, RedlineStyle? style = null, CancellationToken cancellationToken = default) =>
        SaveAsync(path, Redline.FormFor(path ?? throw new ArgumentNullException(nameof(path))), style, cancellationToken);

    /// <summary>Writes the comparison to a file asynchronously in an explicit form; see <see cref="SaveAsync(string, RedlineStyle?, CancellationToken)"/>.</summary>
    public async Task SaveAsync(string path, RedlineForm form, RedlineStyle? style = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(form);
        await File.WriteAllBytesAsync(path, ToBytes(form, style, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the comparison to a stream asynchronously, which is not closed: the output is built in memory and then written with
    /// <c>WriteAsync</c>, so it works on streams that refuse synchronous writes (an ASP.NET Core response body under its default settings).
    /// </summary>
    /// <param name="output">The destination.</param>
    /// <param name="form">The output form.</param>
    /// <param name="style">The look; see <see cref="RedlineForm.ResolveStyle"/>.</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    public async Task SaveAsync(Stream output, RedlineForm form, RedlineStyle? style = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(form);
        await output.WriteAsync(ToBytes(form, style, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
}
