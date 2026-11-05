using System.Collections.Immutable;

namespace OneDiff.Documents.Model;

/// <summary>
/// An immutable set of string attributes (bold, a paragraph style, a list label, a content hash) kept sorted by key
/// (ordinal) so that equal sets always serialize alike. A key's prefix says what kind of attribute it is; see
/// <see cref="FormatKeys"/>.
/// </summary>
public sealed class FormatSet
{
    private readonly KeyValuePair<string, string>[] entries;

    private FormatSet(KeyValuePair<string, string>[] entries) => this.entries = entries;

    /// <summary>The set with nothing in it.</summary>
    public static FormatSet Empty { get; } = new([]);

    /// <summary>How many attributes the set holds.</summary>
    public int Count => entries.Length;

    /// <summary>Whether the set is empty.</summary>
    public bool IsEmpty => entries.Length == 0;

    /// <summary>The attributes in key order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Entries => entries;

    /// <summary>Builds a set. A key given twice keeps its last value.</summary>
    /// <exception cref="ArgumentException">A key or value is null or a key is empty.</exception>
    public static FormatSet Create(IEnumerable<KeyValuePair<string, string>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in items)
        {
            if (string.IsNullOrEmpty(kv.Key) || kv.Value is null)
            {
                throw new ArgumentException("Attribute keys cannot be empty and values cannot be null.", nameof(items));
            }

            WellFormed.Check(kv.Key, "an attribute key");
            WellFormed.Check(kv.Value, "an attribute value");

            map[kv.Key] = kv.Value;
        }

        return map.Count == 0 ? Empty : new FormatSet([.. map]);
    }

    /// <summary>Builds a set from alternating keys and values.</summary>
    /// <exception cref="ArgumentException">An odd number of strings, or an invalid key or value.</exception>
    public static FormatSet Of(params ReadOnlySpan<string> keysAndValues)
    {
        if ((keysAndValues.Length & 1) != 0)
        {
            throw new ArgumentException("Keys and values must come in pairs.", nameof(keysAndValues));
        }

        var list = new List<KeyValuePair<string, string>>(keysAndValues.Length / 2);
        for (int i = 0; i < keysAndValues.Length; i += 2)
        {
            list.Add(new KeyValuePair<string, string>(keysAndValues[i], keysAndValues[i + 1]));
        }

        return Create(list);
    }

    /// <summary>The value of <paramref name="key"/>, or null when absent.</summary>
    public string? Get(string key)
    {
        int lo = 0, hi = entries.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = string.CompareOrdinal(entries[mid].Key, key);
            if (c == 0)
            {
                return entries[mid].Value;
            }

            if (c < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return null;
    }

    /// <summary>A copy with <paramref name="key"/> set to <paramref name="value"/>.</summary>
    public FormatSet With(string key, string value) => Create([.. entries, new KeyValuePair<string, string>(key, value)]);

    /// <summary>A copy without <paramref name="key"/>.</summary>
    public FormatSet Without(string key) => Where(k => !string.Equals(k, key, StringComparison.Ordinal));

    /// <summary>A copy holding only the attributes whose key satisfies <paramref name="keep"/>.</summary>
    public FormatSet Where(Func<string, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        bool all = true;
        foreach (KeyValuePair<string, string> kv in entries)
        {
            if (!keep(kv.Key))
            {
                all = false;
                break;
            }
        }

        if (all)
        {
            return this;
        }

        var list = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> kv in entries)
        {
            if (keep(kv.Key))
            {
                list.Add(kv);
            }
        }

        return list.Count == 0 ? Empty : new FormatSet([.. list]);
    }

    /// <summary>Whether two sets hold the same attributes.</summary>
    public static bool ContentEquals(FormatSet? a, FormatSet? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.entries.Length != b.entries.Length)
        {
            return false;
        }

        for (int i = 0; i < a.entries.Length; i++)
        {
            if (!string.Equals(a.entries[i].Key, b.entries[i].Key, StringComparison.Ordinal)
                || !string.Equals(a.entries[i].Value, b.entries[i].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A compact description such as <c>run.bold=1;run.size=11</c>, for diagnostics.</summary>
    public override string ToString() => string.Join(';', entries.Select(e => e.Key + "=" + e.Value));
}

/// <summary>An attribute set that applies to a character range of a node's text.</summary>
/// <param name="Start">The first character, in the node's <see cref="Node.Text"/>.</param>
/// <param name="Length">The number of characters; positive.</param>
/// <param name="Format">The attributes; never empty in a canonical run list.</param>
public readonly record struct FormatRun(int Start, int Length, FormatSet Format)
{
    /// <summary>One past the last character.</summary>
    public int End => Start + Length;
}

/// <summary>
/// The attribute keys the engine knows. A key's prefix says which hiding category it belongs to: <c>run.</c> character
/// formatting, <c>para.</c> paragraph formatting, <c>list.</c> list formatting, <c>section.</c> page setup, <c>field.</c> field
/// codes; every other key (<c>hash.</c>, <c>table.</c>, <c>prop.</c> …) is content or structure and can never be hidden.
/// Sources may add keys of their own; they follow the same prefix rule.
/// </summary>
public static class FormatKeys
{
    /// <summary>Bold (<c>1</c> when on).</summary>
    public const string Bold = "run.bold";

    /// <summary>Italic.</summary>
    public const string Italic = "run.italic";

    /// <summary>Underline style.</summary>
    public const string Underline = "run.underline";

    /// <summary>Strike-through.</summary>
    public const string Strike = "run.strike";

    /// <summary>Font family.</summary>
    public const string Font = "run.font";

    /// <summary>Font size in points.</summary>
    public const string Size = "run.size";

    /// <summary>Text colour as <c>#RRGGBB</c>.</summary>
    public const string Color = "run.color";

    /// <summary>Highlight colour.</summary>
    public const string Highlight = "run.highlight";

    /// <summary>Capitalisation style.</summary>
    public const string Caps = "run.caps";

    /// <summary>Superscript or subscript.</summary>
    public const string VerticalAlign = "run.vert";

    /// <summary>A hyperlink target. Hyperlinks are an attribute of the linked text, not a node of their own.</summary>
    public const string Link = "run.link";

    /// <summary>Paragraph style name.</summary>
    public const string ParagraphStyle = "para.style";

    /// <summary>Paragraph alignment.</summary>
    public const string Alignment = "para.align";

    /// <summary>Paragraph indentation.</summary>
    public const string Indent = "para.indent";

    /// <summary>Paragraph spacing.</summary>
    public const string Spacing = "para.spacing";

    /// <summary>Keep-with-next and similar settings.</summary>
    public const string Keep = "para.keep";

    /// <summary>The resolved numbering label of a list item, such as <c>4.2</c> or <c>(a)</c>.</summary>
    public const string ListLabel = "list.label";

    /// <summary>The nesting level of a list item.</summary>
    public const string ListLevel = "list.level";

    /// <summary>The numbering format of a list item.</summary>
    public const string ListFormat = "list.format";

    /// <summary>Page setup of a section.</summary>
    public const string SectionPageSetup = "section.page";

    /// <summary>The code of a field; its result is the node's text.</summary>
    public const string FieldCode = "field.code";

    /// <summary>The content hash of an image or other payload, as hex.</summary>
    public const string ContentHash = "hash.content";

    /// <summary>The perceptual hash of an image, as hex; absent until a source supplies one.</summary>
    public const string PerceptualHash = "hash.perceptual";

    /// <summary>The name of a document property (the value is the node's text).</summary>
    public const string PropertyName = "prop.name";

    /// <summary>Which ledger category hides differences in the attribute <paramref name="key"/>, or null when it can never be hidden.</summary>
    public static LedgerCategory? CategoryOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.StartsWith("run.", StringComparison.Ordinal))
        {
            return LedgerCategory.RunFormatting;
        }

        if (key.StartsWith("para.", StringComparison.Ordinal))
        {
            return LedgerCategory.ParagraphFormatting;
        }

        if (key.StartsWith("list.", StringComparison.Ordinal))
        {
            return LedgerCategory.ListFormatting;
        }

        if (key.StartsWith("section.", StringComparison.Ordinal))
        {
            return LedgerCategory.SectionFormatting;
        }

        return key.StartsWith("field.", StringComparison.Ordinal) ? LedgerCategory.FieldCodes : null;
    }
}

/// <summary>Helpers for canonical run lists.</summary>
internal static class FormatRuns
{
    /// <summary>
    /// Sorts, validates and merges runs: no empty formats, no overlaps, nothing outside [0, <paramref name="textLength"/>],
    /// and neighbouring runs with equal formats become one.
    /// </summary>
    public static ImmutableArray<FormatRun> Canonicalize(IEnumerable<FormatRun>? runs, int textLength)
    {
        if (runs is null)
        {
            return [];
        }

        var list = new List<FormatRun>(runs);
        list.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        ImmutableArray<FormatRun>.Builder b = ImmutableArray.CreateBuilder<FormatRun>();
        int pos = 0;
        foreach (FormatRun r in list)
        {
            if (r.Format is null)
            {
                throw new ArgumentException("A format run has no format.");
            }

            if (r.Length <= 0 || r.Start < pos || r.Start > textLength || r.Length > textLength - r.Start)
            {
                throw new ArgumentException($"Format run [{r.Start}, +{r.Length}) is empty, overlaps another or lies outside the text of {textLength} characters.");
            }

            pos = r.End;
            if (r.Format.IsEmpty)
            {
                continue;
            }

            AppendMerged(b, r);
        }

        return b.ToImmutable();
    }

    public static void AppendMerged(ImmutableArray<FormatRun>.Builder b, FormatRun r)
    {
        if (b.Count > 0)
        {
            FormatRun last = b[^1];
            if (last.End == r.Start && FormatSet.ContentEquals(last.Format, r.Format))
            {
                b[^1] = new FormatRun(last.Start, last.Length + r.Length, last.Format);
                return;
            }
        }

        b.Add(r);
    }

    /// <summary>The runs inside [start, start+length), rebased so the range starts at 0.</summary>
    public static ImmutableArray<FormatRun> Slice(ImmutableArray<FormatRun> runs, int start, int length)
    {
        ImmutableArray<FormatRun>.Builder b = ImmutableArray.CreateBuilder<FormatRun>();
        int end = start + length;
        foreach (FormatRun r in runs)
        {
            if (r.End <= start)
            {
                continue;
            }

            if (r.Start >= end)
            {
                break;
            }

            int s = Math.Max(r.Start, start), e = Math.Min(r.End, end);
            if (e > s)
            {
                AppendMerged(b, new FormatRun(s - start, e - s, r.Format));
            }
        }

        return b.ToImmutable();
    }

    /// <summary>Adds <paramref name="runs"/> shifted by <paramref name="offset"/>.</summary>
    public static void Append(ImmutableArray<FormatRun>.Builder b, ImmutableArray<FormatRun> runs, int offset)
    {
        foreach (FormatRun r in runs)
        {
            AppendMerged(b, new FormatRun(r.Start + offset, r.Length, r.Format));
        }
    }

    public static bool SameRuns(ImmutableArray<FormatRun> a, ImmutableArray<FormatRun> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Start != b[i].Start || a[i].Length != b[i].Length || !FormatSet.ContentEquals(a[i].Format, b[i].Format))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The format at character <paramref name="pos"/> (empty when no run covers it), using a moving cursor.</summary>
    public static FormatSet FormatAt(ImmutableArray<FormatRun> runs, ref int cursor, int pos)
    {
        while (cursor < runs.Length && runs[cursor].End <= pos)
        {
            cursor++;
        }

        return cursor < runs.Length && runs[cursor].Start <= pos ? runs[cursor].Format : FormatSet.Empty;
    }

    /// <summary>
    /// Compares the formats of two equally long ranges and returns the maximal sub-ranges where they differ, as (offset, length)
    /// pairs relative to the range starts.
    /// </summary>
    public static List<(int Offset, int Length)> Differences(ImmutableArray<FormatRun> a, int aStart, ImmutableArray<FormatRun> b, int bStart, int length)
    {
        var result = new List<(int, int)>();
        // Walk the union of run boundaries; formats are constant between boundaries.
        var cuts = new SortedSet<int> { 0, length };
        foreach (FormatRun r in a)
        {
            AddCut(cuts, r.Start - aStart, length);
            AddCut(cuts, r.End - aStart, length);
        }

        foreach (FormatRun r in b)
        {
            AddCut(cuts, r.Start - bStart, length);
            AddCut(cuts, r.End - bStart, length);
        }

        int ca = 0, cb = 0, openStart = -1, prev = 0;
        bool first = true;
        foreach (int cut in cuts)
        {
            if (first)
            {
                first = false;
                prev = cut;
                continue;
            }

            FormatSet fa = FormatAt(a, ref ca, aStart + prev);
            FormatSet fb = FormatAt(b, ref cb, bStart + prev);
            if (FormatSet.ContentEquals(fa, fb))
            {
                if (openStart >= 0)
                {
                    result.Add((openStart, prev - openStart));
                    openStart = -1;
                }
            }
            else if (openStart < 0)
            {
                openStart = prev;
            }

            prev = cut;
        }

        if (openStart >= 0)
        {
            result.Add((openStart, length - openStart));
        }

        return result;
    }

    private static void AddCut(SortedSet<int> cuts, int pos, int length)
    {
        if (pos > 0 && pos < length)
        {
            cuts.Add(pos);
        }
    }
}
