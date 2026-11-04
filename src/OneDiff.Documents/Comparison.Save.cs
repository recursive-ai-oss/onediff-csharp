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

    /// <summary>Writes the comparison to a file in the form its extension selects, with the settings' style.</summary>
    /// <param name="path">The output file; replaced if it exists. The directory must exist.</param>
    /// <exception cref="NotSupportedException">No registered form claims the extension.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public void SaveAs(string path) => SaveAs(path, Redline.FormFor(path), null);

    /// <summary>Writes the comparison to a file in the form its extension selects, in the given look.</summary>
    /// <exception cref="NotSupportedException">No registered form claims the extension.</exception>
    public void SaveAs(string path, RedlineStyle style) => SaveAs(path, Redline.FormFor(path), style);

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
}
