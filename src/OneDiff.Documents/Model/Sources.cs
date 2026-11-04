using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace OneDiff.Documents.Model;

/// <summary>An input to a source: the bytes, and a display name. The name is never used to decide what the bytes are.</summary>
public sealed class DocumentInput
{
    private string? hash;

    private DocumentInput(ReadOnlyMemory<byte> content, string name)
    {
        Content = content;
        Name = name;
    }

    /// <summary>The bytes of the input.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>A display name (a file name, say). It is carried into the document for captions only.</summary>
    public string Name { get; }

    /// <summary>Wraps bytes. The memory is not copied; do not change it while the input is in use.</summary>
    public static DocumentInput FromBytes(ReadOnlyMemory<byte> content, string name = "")
    {
        ArgumentNullException.ThrowIfNull(name);
        return new DocumentInput(content, name);
    }

    /// <summary>Encodes a string as UTF-8 (no byte order mark).</summary>
    public static DocumentInput FromString(string text, string name = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);
        return new DocumentInput(new UTF8Encoding(false).GetBytes(text), name);
    }

    /// <summary>Reads a stream to its end.</summary>
    /// <param name="stream">The stream; read from its current position.</param>
    /// <param name="name">A display name.</param>
    /// <param name="maxBytes">The most to read; default 1 GiB.</param>
    /// <exception cref="InvalidDataException">The stream holds more than <paramref name="maxBytes"/>.</exception>
    public static DocumentInput FromStream(Stream stream, string name = "", long maxBytes = 1L << 30)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(name);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[81920];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + n > maxBytes)
            {
                throw new InvalidDataException($"the input is larger than {maxBytes} bytes");
            }

            ms.Write(buffer, 0, n);
        }

        return new DocumentInput(ms.ToArray(), name);
    }

    /// <summary>The SHA-256 of the bytes as lower-case hex.</summary>
    public string Sha256Hex() => hash ??= Convert.ToHexStringLower(SHA256.HashData(Content.Span));
}

/// <summary>
/// Turns an input into a <see cref="CompareDocument"/> with anchors back to the input. A source recognizes its input by
/// content, never by file name. Implementations are pure and thread-safe: the same bytes give the same document.
/// </summary>
public interface IDocumentSource
{
    /// <summary>A short name for diagnostics, such as <c>plain-text</c>.</summary>
    string Name { get; }

    /// <summary>Whether this source can read the input, judged from its bytes.</summary>
    bool CanRead(DocumentInput input);

    /// <summary>Reads the input.</summary>
    /// <exception cref="InvalidDataException">The input is malformed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    CompareDocument Read(DocumentInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// The registry of document sources. The built-in list is explicit; adapter packages add their sources with <see cref="Register"/>
/// (each package exposes one call that does so). Nothing is discovered by reflection.
/// </summary>
public static class DocumentSources
{
    private static readonly object Gate = new();
    private static ImmutableArray<IDocumentSource> registered = [];

    /// <summary>The sources in this package, in the order they are tried: just plain text.</summary>
    public static IReadOnlyList<IDocumentSource> BuiltIn { get; } = [PlainTextSource.Instance];

    /// <summary>The sources added with <see cref="Register"/>, newest first.</summary>
    public static IReadOnlyList<IDocumentSource> Registered => registered;

    /// <summary>
    /// Adds a source to the process-wide list that <see cref="Sniff"/> consults before the built-in ones. A source with the same
    /// <see cref="IDocumentSource.Name"/> is replaced, so calling a package's registration twice is harmless. Thread-safe.
    /// </summary>
    public static void Register(IDocumentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (Gate)
        {
            ImmutableArray<IDocumentSource> kept = [.. registered.Where(s => !string.Equals(s.Name, source.Name, StringComparison.Ordinal))];
            registered = kept.Insert(0, source);
        }
    }

    /// <summary>
    /// Picks the first source that can read the input: <paramref name="additional"/> first, then the registered ones, then the
    /// built-in ones.
    /// </summary>
    /// <exception cref="NotSupportedException">No source recognizes the input. When the bytes look like a format another package reads, the message names that package.</exception>
    public static IDocumentSource Sniff(DocumentInput input, IEnumerable<IDocumentSource>? additional = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (additional is not null)
        {
            foreach (IDocumentSource s in additional)
            {
                if (s.CanRead(input))
                {
                    return s;
                }
            }
        }

        foreach (IDocumentSource s in registered)
        {
            if (s.CanRead(input))
            {
                return s;
            }
        }

        // Binary formats that a package we know of reads: say which, instead of reading them as text.
        string? hint = FormatHints.PackageFor(input.Content.Span);
        if (hint is not null)
        {
            throw new NotSupportedException(
                $"no document source is registered for this input, which looks like {hint}. Reference that package and call its Register method once at startup.");
        }

        foreach (IDocumentSource s in BuiltIn)
        {
            if (s.CanRead(input))
            {
                return s;
            }
        }

        throw new NotSupportedException("no document source recognizes this input");
    }
}

/// <summary>
/// The plain-text source. Paragraphs are runs of non-blank lines separated by blank lines (lines that are empty or only
/// whitespace); the lines of a paragraph are kept, joined by LF. The document is one section holding the paragraphs. Text is
/// normalized with <see cref="ControlCharacterNormalizer"/> and <see cref="TypographicNormalizer"/>, so quote, dash and
/// whitespace differences are recorded as folds. Anchors are text spans of the decoded input: the paragraph's span on the
/// paragraph, one span per line as segments. A UTF-8 or UTF-16 byte order mark selects the encoding; otherwise UTF-8
/// (invalid bytes become U+FFFD). Input containing a zero byte (and no UTF-16 mark) is not text.
/// </summary>
public sealed class PlainTextSource : IDocumentSource
{
    /// <summary>The name of the text part that this source's span anchors refer to.</summary>
    public const string PartName = "text";

    private static readonly ITextNormalizer[] Normalizers = [ControlCharacterNormalizer.Instance, TypographicNormalizer.Instance];

    /// <summary>The shared instance; the source has no state.</summary>
    public static PlainTextSource Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "plain-text";

    /// <inheritdoc />
    public bool CanRead(DocumentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ReadOnlySpan<byte> b = input.Content.Span;
        if (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)))
        {
            return true;
        }

        return !b.Contains((byte)0);
    }

    /// <inheritdoc />
    public CompareDocument Read(DocumentInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        string raw = Decode(input.Content.Span);
        var builder = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(input.Name, input.Sha256Hex(), "text/plain");
        builder.Begin(NodeKind.Section, new NodeContent { Anchor = Anchor.ForTextSpan(PartName, 0, raw.Length) });

        // Walk the lines, collecting runs of non-blank ones.
        int pos = 0, parStart = -1, parEnd = -1;
        var lines = new List<(int Start, int End)>();
        while (pos <= raw.Length)
        {
            int eol = pos;
            while (eol < raw.Length && raw[eol] != '\n' && raw[eol] != '\r')
            {
                eol++;
            }

            bool blank = IsBlank(raw, pos, eol);
            if (!blank)
            {
                if (parStart < 0)
                {
                    parStart = pos;
                }

                parEnd = eol;
                lines.Add((pos, eol));
            }

            if (blank || eol >= raw.Length)
            {
                if (parStart >= 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddParagraph(builder, raw, parStart, parEnd, lines);
                    parStart = -1;
                    lines.Clear();
                }
            }

            if (eol >= raw.Length)
            {
                break;
            }

            pos = eol + 1;
            if (raw[eol] == '\r' && pos < raw.Length && raw[pos] == '\n')
            {
                pos++;
            }
        }

        builder.End();
        return builder.Build();
    }

    private static void AddParagraph(CompareDocumentBuilder builder, string raw, int start, int end, List<(int Start, int End)> lines)
    {
        NormalizedText norm = TextNormalization.Normalize(raw.Substring(start, end - start), Normalizers);
        var segments = new List<AnchoredSpan>(lines.Count);
        foreach ((int ls, int le) in lines)
        {
            int a = norm.MapRawIndex(ls - start), b = norm.MapRawIndex(le - start);
            segments.Add(new AnchoredSpan(a, b - a, Anchor.ForTextSpan(PartName, ls, le - ls)));
        }

        builder.Add(NodeKind.Paragraph, new NodeContent
        {
            Text = norm.Text,
            Folds = norm.Folds,
            Anchor = Anchor.ForTextSpan(PartName, start, end - start),
            Segments = [.. segments],
        });
    }

    private static bool IsBlank(string raw, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(raw[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(b[3..]);
        }

        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(b[2..]);
        }

        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(b[2..]);
        }

        return Encoding.UTF8.GetString(b);
    }
}
