using System.Security.Cryptography;

namespace OneDiff.Documents.Model;

/// <summary>
/// The bytes a document was read from, kept on the document by the source that read it, for output forms that work on the original
/// file (marking a PDF's own pages). It is part of the value in memory only: it is not hashed, not compared and never written to
/// JSON, so a comparison read back from JSON has none, and an output form that needs it says so.
/// </summary>
public sealed class DocumentOrigin
{
    /// <summary>Wraps the bytes of a file a source read.</summary>
    /// <param name="content">The file the output form will open. Not copied.</param>
    /// <param name="password">The password the source opened it with, or null.</param>
    /// <param name="sha256">The SHA-256 (hex) of <paramref name="content"/>; computed when null.</param>
    public DocumentOrigin(ReadOnlyMemory<byte> content, string? password = null, string? sha256 = null)
    {
        Content = content;
        Password = password;
        Sha256 = sha256 ?? Convert.ToHexStringLower(SHA256.HashData(content.Span));
    }

    /// <summary>The file's bytes.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>The password the file was opened with, or null. Never written anywhere by this library.</summary>
    public string? Password { get; }

    /// <summary>The SHA-256 of <see cref="Content"/> as lower-case hex.</summary>
    public string Sha256 { get; }
}

/// <summary>A problem a source found in its input that did not stop it reading: a damaged file it repaired, content it could not recover.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Code">A stable machine-readable code such as <c>pdf.repaired</c>.</param>
/// <param name="Message">A sentence for a person.</param>
public sealed record SourceWarning(DiagnosticSeverity Severity, string Code, string Message);
