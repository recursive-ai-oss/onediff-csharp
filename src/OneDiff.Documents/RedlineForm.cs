using System.Collections.Immutable;
using OneDiff.Documents.Html;
using OneDiff.Documents.Serialization;

namespace OneDiff.Documents;

/// <summary>
/// An output form of a <see cref="Comparison"/>: what it is called, which file extensions select it, and how it writes itself.
/// A form is self-contained, so passing one explicitly (<c>cmp.SaveAs(path, form)</c>) needs no registration; registering it
/// with <see cref="Redline.Register(RedlineForm)"/> lets <c>Redline.Create</c> and <see cref="Comparison.SaveAs(string)"/>
/// choose it from the output file's extension. Adapter packages derive their own forms (a PDF with markup on the original
/// pages, a tracked-changes DOCX, ...). Implementations are stateless and thread-safe.
/// </summary>
public abstract class RedlineForm
{
    /// <summary>JSON: the versioned change list (<see cref="ComparisonJson"/>). The style is not used. Extension <c>.json</c>.</summary>
    public static RedlineForm Json { get; } = new JsonForm();

    /// <summary>A single self-contained HTML page with the changes marked inline. Extensions <c>.html</c> and <c>.htm</c>.</summary>
    public static RedlineForm Html { get; } = new InlineHtmlForm();

    /// <summary>A single self-contained HTML page with old and new side by side; colour-blind-safe highlights unless the settings choose another style. No extension selects it.</summary>
    public static RedlineForm HtmlSideBySide { get; } = new SideBySideHtmlForm();

    /// <summary>Creates a form.</summary>
    /// <param name="name">A short stable name such as <c>html</c>.</param>
    /// <param name="extensions">The output file extensions (with the dot, case-insensitive) that select this form; may be empty.</param>
    /// <param name="preferredStyle">The look to use when the caller and the comparison's settings leave it at the default; null for the settings' own.</param>
    /// <exception cref="ArgumentException">The name is empty or an extension does not start with a dot.</exception>
    protected RedlineForm(string name, IEnumerable<string> extensions, RedlineStyle? preferredStyle = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(extensions);
        ImmutableArray<string>.Builder list = ImmutableArray.CreateBuilder<string>();
        foreach (string e in extensions)
        {
            if (string.IsNullOrEmpty(e) || e[0] != '.')
            {
                throw new ArgumentException("an extension must start with a dot", nameof(extensions));
            }

            list.Add(e.ToLowerInvariant());
        }

        Name = name;
        Extensions = list.ToImmutable();
        PreferredStyle = preferredStyle;
    }

    /// <summary>The form's name.</summary>
    public string Name { get; }

    /// <summary>The lower-case extensions (with the dot) that select this form when it is registered.</summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>The look this form uses when nobody chose one: used instead of the settings' style while that is still the default blackline. Null: always the settings' style.</summary>
    public RedlineStyle? PreferredStyle { get; }

    /// <summary>Writes the comparison.</summary>
    /// <param name="comparison">What to write.</param>
    /// <param name="output">Where to write; not closed.</param>
    /// <param name="style">The look; forms that have no look ignore it.</param>
    /// <param name="cancellationToken">Observed while writing.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public abstract void Write(Comparison comparison, Stream output, RedlineStyle style, CancellationToken cancellationToken = default);

    /// <summary>
    /// The look to use: the caller's choice if given; otherwise the settings' style, unless that is still the default blackline
    /// and the form prefers another.
    /// </summary>
    public RedlineStyle ResolveStyle(Comparison comparison, RedlineStyle? requested)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (requested is not null)
        {
            return requested;
        }

        RedlineStyle fromSettings = comparison.Settings.Style;
        return PreferredStyle is not null && fromSettings == RedlineStyle.Blackline ? PreferredStyle : fromSettings;
    }

    /// <inheritdoc />
    public override string ToString() => Name;

    private sealed class JsonForm() : RedlineForm("json", [".json"])
    {
        public override void Write(Comparison comparison, Stream output, RedlineStyle style, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(comparison);
            ArgumentNullException.ThrowIfNull(output);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = ComparisonJson.Write(comparison);
            output.Write(bytes, 0, bytes.Length);
        }
    }

    private sealed class InlineHtmlForm() : RedlineForm("html", [".html", ".htm"])
    {
        public override void Write(Comparison comparison, Stream output, RedlineStyle style, CancellationToken cancellationToken = default) =>
            HtmlRedline.WriteInline(comparison, output, style, cancellationToken);
    }

    private sealed class SideBySideHtmlForm() : RedlineForm("html-side-by-side", [], RedlineStyle.SideBySide)
    {
        public override void Write(Comparison comparison, Stream output, RedlineStyle style, CancellationToken cancellationToken = default) =>
            HtmlRedline.WriteSideBySide(comparison, output, style, cancellationToken);
    }
}
