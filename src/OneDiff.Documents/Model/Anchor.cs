using System.Collections.Immutable;

namespace OneDiff.Documents.Model;

/// <summary>What an <see cref="Anchor"/> points at in the source.</summary>
public enum AnchorKind : byte
{
    /// <summary>No anchor (an absent side of a change, or a node the source cannot locate).</summary>
    None = 0,

    /// <summary>An opaque reference the adapter that produced the document can resolve (a node of a document hub, say).</summary>
    Node = 1,

    /// <summary>A set of glyph quadrilaterals on PDF pages.</summary>
    PdfQuads = 2,

    /// <summary>One or more OCR words with a recognition confidence.</summary>
    OcrWords = 3,

    /// <summary>A spreadsheet cell: a sheet and an A1 address.</summary>
    Cell = 4,

    /// <summary>A character range of a text part of the input (the plain-text source uses this).</summary>
    TextSpan = 5,
}

/// <summary>A quadrilateral on a page, in the source's own coordinate units, corners in drawing order.</summary>
/// <param name="Page">The zero-based page number.</param>
/// <param name="X1">First corner, x.</param>
/// <param name="Y1">First corner, y.</param>
/// <param name="X2">Second corner, x.</param>
/// <param name="Y2">Second corner, y.</param>
/// <param name="X3">Third corner, x.</param>
/// <param name="Y3">Third corner, y.</param>
/// <param name="X4">Fourth corner, x.</param>
/// <param name="Y4">Fourth corner, y.</param>
public readonly record struct Quad(int Page, double X1, double Y1, double X2, double Y2, double X3, double Y3, double X4, double Y4)
{
    /// <summary>Whether the page is non-negative and every coordinate is a finite number.</summary>
    public bool IsValid =>
        Page >= 0 && double.IsFinite(X1) && double.IsFinite(Y1) && double.IsFinite(X2) && double.IsFinite(Y2)
        && double.IsFinite(X3) && double.IsFinite(Y3) && double.IsFinite(X4) && double.IsFinite(Y4);
}

/// <summary>
/// Where a piece of the compare model came from in its source. Anchors are the contract between sources and writers: a source
/// attaches them while reading, the engine copies them onto changes, and a writer resolves them back (to a hub node, a page
/// rectangle, a cell). The engine never interprets an anchor beyond combining neighbours; nothing here refers to a particular
/// source library. Immutable.
/// </summary>
public sealed class Anchor
{
    private Anchor(AnchorKind kind, string reference, int start, int length, string address, ImmutableArray<Quad> quads, double? confidence)
    {
        Kind = kind;
        Reference = reference;
        Start = start;
        Length = length;
        Address = address;
        Quads = quads;
        Confidence = confidence;
    }

    /// <summary>The absent anchor.</summary>
    public static Anchor None { get; } = new(AnchorKind.None, "", 0, 0, "", [], null);

    /// <summary>Which kind of source location this is.</summary>
    public AnchorKind Kind { get; }

    /// <summary>The adapter-defined reference (<see cref="AnchorKind.Node"/>), the text part name (<see cref="AnchorKind.TextSpan"/>) or the sheet name (<see cref="AnchorKind.Cell"/>); empty otherwise.</summary>
    public string Reference { get; }

    /// <summary>The first character of a <see cref="AnchorKind.TextSpan"/>; 0 for other kinds.</summary>
    public int Start { get; }

    /// <summary>The number of characters of a <see cref="AnchorKind.TextSpan"/>; 0 for other kinds.</summary>
    public int Length { get; }

    /// <summary>The A1 cell address of a <see cref="AnchorKind.Cell"/>; empty otherwise.</summary>
    public string Address { get; }

    /// <summary>The quadrilaterals of a <see cref="AnchorKind.PdfQuads"/> or <see cref="AnchorKind.OcrWords"/> anchor; empty otherwise.</summary>
    public ImmutableArray<Quad> Quads { get; }

    /// <summary>The lowest word confidence in [0, 1] of an <see cref="AnchorKind.OcrWords"/> anchor; null for other kinds.</summary>
    public double? Confidence { get; }

    /// <summary>An opaque, adapter-defined reference to a node of the source.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is null.</exception>
    public static Anchor ForNode(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        WellFormed.Check(reference, "an anchor reference");
        return new Anchor(AnchorKind.Node, reference, 0, 0, "", [], null);
    }

    /// <summary>A character range of a text part of the input.</summary>
    /// <param name="part">The name of the part (for example the input's own name).</param>
    /// <param name="start">The first character, counted in UTF-16 code units of the decoded part.</param>
    /// <param name="length">The number of code units.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> or <paramref name="length"/> is negative, or their sum overflows.</exception>
    public static Anchor ForTextSpan(string part, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(part);
        WellFormed.Check(part, "an anchor part name");
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, int.MaxValue - start);

        return new Anchor(AnchorKind.TextSpan, part, start, length, "", [], null);
    }

    /// <summary>A set of glyph quadrilaterals on PDF pages.</summary>
    /// <exception cref="ArgumentException">There are no quads, or one is not <see cref="Quad.IsValid"/>.</exception>
    public static Anchor ForPdfQuads(IEnumerable<Quad> quads)
    {
        ImmutableArray<Quad> q = CheckQuads(quads);
        return new Anchor(AnchorKind.PdfQuads, "", 0, 0, "", q, null);
    }

    /// <summary>One or more OCR words.</summary>
    /// <param name="quads">The word boxes.</param>
    /// <param name="confidence">The lowest recognition confidence of the words, in [0, 1].</param>
    /// <exception cref="ArgumentException">There are no quads, or one is not <see cref="Quad.IsValid"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="confidence"/> is outside [0, 1] or not a number.</exception>
    public static Anchor ForOcrWords(IEnumerable<Quad> quads, double confidence)
    {
        ImmutableArray<Quad> q = CheckQuads(quads);
        CheckConfidence(confidence);
        return new Anchor(AnchorKind.OcrWords, "", 0, 0, "", q, confidence);
    }

    /// <summary>A spreadsheet cell.</summary>
    /// <param name="sheet">The sheet name.</param>
    /// <param name="address">The A1-style address, for example <c>B7</c>.</param>
    public static Anchor ForCell(string sheet, string address)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(address);
        WellFormed.Check(sheet, "a sheet name");
        WellFormed.Check(address, "a cell address");
        return new Anchor(AnchorKind.Cell, sheet, 0, 0, address, [], null);
    }

    /// <summary>
    /// Combines anchors of neighbouring pieces into one that covers them all. Absent anchors are skipped.
    /// Text spans of one part become their hull; PDF quads and OCR words concatenate (OCR keeps the lowest confidence); any other
    /// mix yields the first anchor. The result depends only on the inputs and their order.
    /// </summary>
    public static Anchor Union(IReadOnlyList<Anchor> anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        Anchor? first = null;
        int count = 0;
        foreach (Anchor a in anchors)
        {
            if (a.Kind == AnchorKind.None)
            {
                continue;
            }

            first ??= a;
            count++;
        }

        if (first is null)
        {
            return None;
        }

        if (count == 1)
        {
            return first;
        }

        switch (first.Kind)
        {
            case AnchorKind.TextSpan:
            {
                int lo = int.MaxValue, hi = int.MinValue;
                foreach (Anchor a in anchors)
                {
                    if (a.Kind != AnchorKind.TextSpan || !string.Equals(a.Reference, first.Reference, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    lo = Math.Min(lo, a.Start);
                    hi = Math.Max(hi, a.Start + a.Length);
                }

                return new Anchor(AnchorKind.TextSpan, first.Reference, lo, hi - lo, "", [], null);
            }

            case AnchorKind.PdfQuads:
            case AnchorKind.OcrWords:
            {
                ImmutableArray<Quad>.Builder q = ImmutableArray.CreateBuilder<Quad>();
                double? conf = null;
                foreach (Anchor a in anchors)
                {
                    if (a.Kind != first.Kind)
                    {
                        continue;
                    }

                    q.AddRange(a.Quads);
                    if (a.Confidence is double c)
                    {
                        conf = conf is double m ? Math.Min(m, c) : c;
                    }
                }

                return new Anchor(first.Kind, "", 0, 0, "", q.ToImmutable(), conf);
            }

            default:
                return first;
        }
    }

    /// <summary>Whether two anchors describe the same source location (structural comparison).</summary>
    public bool SameAs(Anchor? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Kind != other.Kind || Start != other.Start || Length != other.Length || Confidence != other.Confidence
            || !string.Equals(Reference, other.Reference, StringComparison.Ordinal) || !string.Equals(Address, other.Address, StringComparison.Ordinal)
            || Quads.Length != other.Quads.Length)
        {
            return false;
        }

        for (int i = 0; i < Quads.Length; i++)
        {
            if (Quads[i] != other.Quads[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A short description for diagnostics: kind and main coordinates.</summary>
    public override string ToString() => Kind switch
    {
        AnchorKind.None => "none",
        AnchorKind.Node => "node:" + Reference,
        AnchorKind.TextSpan => $"span:{Reference}[{Start}+{Length}]",
        AnchorKind.Cell => $"cell:{Reference}!{Address}",
        AnchorKind.PdfQuads => $"pdf:{Quads.Length} quads",
        _ => $"ocr:{Quads.Length} words",
    };

    internal static Anchor FromParts(AnchorKind kind, string reference, int start, int length, string address, ImmutableArray<Quad> quads, double? confidence)
    {
        switch (kind)
        {
            case AnchorKind.None:
                return None;
            case AnchorKind.Node:
                return ForNode(reference);
            case AnchorKind.TextSpan:
                return ForTextSpan(reference, start, length);
            case AnchorKind.Cell:
                return ForCell(reference, address);
            case AnchorKind.PdfQuads:
                return ForPdfQuads(quads);
            case AnchorKind.OcrWords:
                return ForOcrWords(quads, confidence ?? throw new ArgumentException("An OCR anchor needs a confidence."));
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static ImmutableArray<Quad> CheckQuads(IEnumerable<Quad> quads)
    {
        ArgumentNullException.ThrowIfNull(quads);
        ImmutableArray<Quad> q = [.. quads];
        if (q.Length == 0)
        {
            throw new ArgumentException("An anchor needs at least one quad.", nameof(quads));
        }

        foreach (Quad quad in q)
        {
            if (!quad.IsValid)
            {
                throw new ArgumentException("A quad has a negative page or a coordinate that is not a finite number.", nameof(quads));
            }
        }

        return q;
    }

    private static void CheckConfidence(double confidence)
    {
        if (!(confidence >= 0.0 && confidence <= 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "confidence must be a number in [0, 1]");
        }
    }
}

/// <summary>An anchor for a range of a node's text. A node lists these when its source can locate pieces (a line, a word) of it.</summary>
/// <param name="Start">The first character of the range, in the node's <see cref="Node.Text"/>.</param>
/// <param name="Length">The number of characters.</param>
/// <param name="Anchor">Where that range came from.</param>
public readonly record struct AnchoredSpan(int Start, int Length, Anchor Anchor);
