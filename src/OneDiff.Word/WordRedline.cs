using OneDiff.Documents;

namespace OneDiff.Word;

/// <summary>Registration of the Word package.</summary>
public static class WordRedline
{
    /// <summary>
    /// Registers <see cref="WordDocumentSource"/> (default options) and the tracked-changes form <see cref="DocxRedline"/> so that <see cref="Redline"/> and <see cref="Documents.Model.DocumentSources.Sniff"/>
    /// read DOCX, DOCM, DOTX and DOTM inputs. Process-wide and safe to call more than once. A later registration of a source with
    /// another <see cref="WordSourceOptions"/> (for example <see cref="WordRevisionMode.AsOriginal"/>) replaces this one.
    /// </summary>
    public static void Register() => DocxRedline.Register();

    /// <summary>Registers a Word source with the given options in place of any registered one.</summary>
    /// <param name="options">The settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static void Register(WordSourceOptions options) => Redline.Register(new WordDocumentSource(options));
}
