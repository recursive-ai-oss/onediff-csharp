using System.Collections.Immutable;
using System.Text.Json;
using OneDiff.Documents.Ledger;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Serialization;

/// <summary>Thrown when JSON is not a comparison this library can read: malformed, over a limit, or inconsistent.</summary>
public sealed class ComparisonFormatException : FormatException
{
    /// <summary>Creates the exception.</summary>
    public ComparisonFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the underlying problem.</summary>
    public ComparisonFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Bounds the reader enforces so hostile input cannot exhaust memory or time.</summary>
/// <param name="MaxInputBytes">The most input bytes accepted. Default 256 MiB.</param>
/// <param name="MaxItems">The most elements any one array may have. Default 4 million.</param>
/// <param name="MaxDepth">The deepest JSON nesting accepted. Default 32; a comparison needs 9.</param>
public sealed record ComparisonJsonLimits(int MaxInputBytes = 256 << 20, int MaxItems = 4_000_000, int MaxDepth = 32)
{
    /// <summary>The default limits.</summary>
    public static ComparisonJsonLimits Default { get; } = new();
}

public static partial class ComparisonJson
{
    /// <summary>
    /// Reads a comparison written by <see cref="Write(Comparison)"/>. The structure is validated (known names, ids in range, no node
    /// in two links, text and run bounds) but the completeness of the changes is not re-proved; call
    /// <see cref="Comparison.Check"/> for that.
    /// </summary>
    /// <param name="utf8">The JSON text, UTF-8, without a byte order mark.</param>
    /// <param name="limits">Bounds on size, array length and depth; <see cref="ComparisonJsonLimits.Default"/> when null.</param>
    /// <exception cref="ComparisonFormatException">The input is not a readable comparison or exceeds a limit.</exception>
    public static Comparison Read(ReadOnlySpan<byte> utf8, ComparisonJsonLimits? limits = null)
    {
        limits ??= ComparisonJsonLimits.Default;
        if (limits.MaxInputBytes <= 0 || limits.MaxItems <= 0 || limits.MaxDepth <= 0)
        {
            throw new ArgumentException("limits must be positive", nameof(limits));
        }

        if (utf8.Length > limits.MaxInputBytes)
        {
            throw new ComparisonFormatException($"the input is {utf8.Length} bytes, over the limit of {limits.MaxInputBytes}");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = limits.MaxDepth });
            return new Reader(limits).ReadComparison(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new ComparisonFormatException("the input is not valid JSON: " + ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            throw new ComparisonFormatException("the input holds an invalid value: " + ex.Message, ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new ComparisonFormatException("the input has a member of the wrong type: " + ex.Message, ex);
        }
        catch (FormatException ex) when (ex is not ComparisonFormatException)
        {
            throw new ComparisonFormatException("the input has a malformed value: " + ex.Message, ex);
        }
        catch (OverflowException ex)
        {
            throw new ComparisonFormatException("the input has a number out of range: " + ex.Message, ex);
        }
    }

    private sealed class Reader(ComparisonJsonLimits limits)
    {
        public Comparison ReadComparison(JsonElement root)
        {
            RequireObject(root, "the document");
            if (Str(root, "schema") != SchemaName || Int(root, "version") != SchemaVersion)
            {
                throw new ComparisonFormatException("this is not a version 1 onediff comparison");
            }

            JsonElement prov = Member(root, "provenance");
            var provenance = new Provenance(
                Str(prov, "library"),
                SchemaVersion,
                Str(prov, "oldName"),
                Str(prov, "newName"),
                Str(prov, "oldHash"),
                Str(prov, "newHash"));
            CompareSettings settings = ReadSettings(Member(root, "settings"));
            CompareDocument oldDoc = ReadDocument(Member(root, "old")), newDoc = ReadDocument(Member(root, "new"));
            var links = new List<AlignmentLink>();
            foreach (JsonElement l in Items(Member(root, "links")))
            {
                links.Add(new AlignmentLink(
                    0,
                    Enum(JsonNames.LinkKinds, Str(l, "kind")),
                    Ints(Member(l, "old")),
                    Ints(Member(l, "new")),
                    Bool(l, "pinned"),
                    Num(l, "score"),
                    Enum(JsonNames.LinkOrigins, Str(l, "origin"))));
            }

            var alignment = Alignment.Create(oldDoc, newDoc, settings, links);
            ImmutableArray<Change>.Builder changes = ImmutableArray.CreateBuilder<Change>();
            foreach (JsonElement c in Items(Member(root, "changes")))
            {
                changes.Add(ReadChange(c));
            }

            ImmutableArray<ChangeGroup>.Builder groups = ImmutableArray.CreateBuilder<ChangeGroup>();
            foreach (JsonElement g in Items(Member(root, "groups")))
            {
                groups.Add(new ChangeGroup(Int(g, "id"), Enum(JsonNames.GroupKinds, Str(g, "kind")), Str(g, "title"), Ints(Member(g, "changes"))));
            }

            var entries = new List<LedgerEntry>();
            foreach (JsonElement e in Items(Member(root, "ledger")))
            {
                ImmutableArray<LedgerLocation>.Builder locs = ImmutableArray.CreateBuilder<LedgerLocation>();
                foreach (JsonElement l in Items(Member(e, "locations")))
                {
                    locs.Add(new LedgerLocation(
                        Int(l, "oldNode"),
                        Int(l, "newNode"),
                        Str(l, "detail"),
                        ReadAnchor(Member(l, "oldAnchor")),
                        ReadAnchor(Member(l, "newAnchor"))));
                }

                if (!LedgerCategoryNames.TryParse(Str(e, "category"), out LedgerCategory cat))
                {
                    throw new ComparisonFormatException("unknown ledger category");
                }

                entries.Add(new LedgerEntry(cat, Int(e, "count"), locs.ToImmutable()));
            }

            ImmutableArray<Diagnostic>.Builder diags = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (JsonElement d in Items(Member(root, "diagnostics")))
            {
                diags.Add(new Diagnostic(Enum(JsonNames.Severities, Str(d, "severity")), Str(d, "code"), Str(d, "message"), Int(d, "oldNode"), Int(d, "newNode")));
            }

            JsonElement v = Member(root, "verification");
            var verification = new VerificationResult(Bool(v, "passed"), Int(v, "fallbackLevel"), Int(v, "failureCount"));
            return new Comparison(alignment, changes.ToImmutable(), groups.ToImmutable(), ChangeLedger.Create(entries), diags.ToImmutable(), provenance, verification);
        }

        private CompareSettings ReadSettings(JsonElement s)
        {
            var hidden = CategorySet.None;
            foreach (JsonElement h in Items(Member(s, "hidden")))
            {
                if (h.ValueKind != JsonValueKind.String || !LedgerCategoryNames.TryParse(h.GetString() ?? "", out LedgerCategory c))
                {
                    throw new ComparisonFormatException("unknown hidden category");
                }

                hidden = hidden.With(c);
            }

            JsonElement st = Member(s, "style");
            var style = new RedlineStyle
            {
                Name = Str(st, "name"),
                Insertion = ReadMark(Member(st, "insertion")),
                Deletion = ReadMark(Member(st, "deletion")),
                MoveFrom = ReadMark(Member(st, "moveFrom")),
                MoveTo = ReadMark(Member(st, "moveTo")),
                Formatting = ReadMark(Member(st, "formatting")),
                ChangeBars = Bool(st, "changeBars"),
                ChangeBarColor = Str(st, "changeBarColor"),
                CoverPage = Bool(st, "coverPage"),
                Footer = Bool(st, "footer"),
                TypeTags = Bool(st, "typeTags"),
            };
            var settings = new CompareSettings
            {
                Name = Str(s, "name"),
                Hidden = hidden,
                ThrowOnFailure = Bool(s, "throwOnFailure"),
                CostCap = Member(s, "costCap").GetInt64(),
                Algorithm = Enum(JsonNames.Algorithms, Str(s, "algorithm")),
                MatchThreshold = Num(s, "matchThreshold"),
                SplitMergeThreshold = Num(s, "splitMergeThreshold"),
                MoveThreshold = Num(s, "moveThreshold"),
                DetectMoves = Bool(s, "detectMoves"),
                DetectSplitsAndMerges = Bool(s, "detectSplitsAndMerges"),
                CollapseCascades = Bool(s, "collapseCascades"),
                TypedChanges = Bool(s, "typedChanges"),
                Seed = Member(s, "seed").GetUInt64(),
                Style = style,
            };
            settings.Validate();
            return settings;
        }

        private static MarkStyle ReadMark(JsonElement m) =>
            new(Str(m, "color"), Enum(JsonNames.Decorations, Str(m, "decoration")), Str(m, "glyph"));

        private CompareDocument ReadDocument(JsonElement d)
        {
            var b = new CompareDocumentBuilder(Enum(JsonNames.DocumentKinds, Str(d, "kind")));
            b.WithMetadata(Str(d, "name"), Str(d, "sourceHash"), Str(d, "sourceFormat"));
            int count = 0;
            foreach (JsonElement n in Items(Member(d, "nodes")))
            {
                string text = Opt(n, "text") is JsonElement t ? Text(t) : "";
                ImmutableArray<FormatRun> runs = Opt(n, "runs") is JsonElement r ? ReadRuns(r, text.Length) : [];
                var segments = ImmutableArray.CreateBuilder<AnchoredSpan>();
                if (Opt(n, "segments") is JsonElement sg)
                {
                    foreach (JsonElement s in Items(sg))
                    {
                        segments.Add(new AnchoredSpan(Int(s, "start"), Int(s, "length"), ReadAnchor(Member(s, "anchor"))));
                    }
                }

                var folds = ImmutableArray.CreateBuilder<NormalizationFold>();
                if (Opt(n, "folds") is JsonElement fl)
                {
                    foreach (JsonElement f in Items(fl))
                    {
                        if (!LedgerCategoryNames.TryParse(Str(f, "category"), out LedgerCategory cat))
                        {
                            throw new ComparisonFormatException("unknown fold category");
                        }

                        folds.Add(new NormalizationFold(cat, Int(f, "start"), Int(f, "length"), Str(f, "original")));
                    }
                }

                int parent = Int(n, "parent");
                if (parent < -1 || parent >= count)
                {
                    throw new ComparisonFormatException($"node {count} names parent {parent}");
                }

                b.Append(
                    parent,
                    Enum(JsonNames.NodeKinds, Str(n, "kind")),
                    new NodeContent
                    {
                        Text = text,
                        Runs = runs,
                        Attributes = Opt(n, "attributes") is JsonElement a ? ReadFormat(a) : FormatSet.Empty,
                        Anchor = Opt(n, "anchor") is JsonElement an ? ReadAnchor(an) : Anchor.None,
                        Segments = segments.ToImmutable(),
                        Folds = folds.ToImmutable(),
                    });
                count++;
            }

            if (Opt(d, "opaque") is JsonElement op)
            {
                foreach (JsonElement p in Items(op))
                {
                    b.AddOpaque(Str(p, "name"), Str(p, "hash"), Opt(p, "anchor") is JsonElement pa ? ReadAnchor(pa) : Anchor.None);
                }
            }

            return b.Build();
        }

        private Change ReadChange(JsonElement c)
        {
            ImmutableArray<RegionEdit>.Builder edits = ImmutableArray.CreateBuilder<RegionEdit>();
            if (Opt(c, "edits") is JsonElement ed)
            {
                foreach (JsonElement e in Items(ed))
                {
                    edits.Add(new RegionEdit(
                        Int(e, "oldStart"),
                        Int(e, "oldLength"),
                        Int(e, "newStart"),
                        Int(e, "newLength"),
                        Str(e, "oldText"),
                        Str(e, "newText"),
                        Opt(e, "oldRuns") is JsonElement oldR ? ReadRuns(oldR, int.MaxValue) : [],
                        Opt(e, "newRuns") is JsonElement newR ? ReadRuns(newR, int.MaxValue) : []));
                }
            }

            ImmutableArray<AttributeEdit>.Builder attrs = ImmutableArray.CreateBuilder<AttributeEdit>();
            if (Opt(c, "attributeEdits") is JsonElement ae)
            {
                foreach (JsonElement e in Items(ae))
                {
                    attrs.Add(new AttributeEdit(
                        Int(e, "oldNode"),
                        Int(e, "newNode"),
                        Str(e, "key"),
                        Opt(e, "oldValue") is JsonElement ov ? Text(ov) : null,
                        Opt(e, "newValue") is JsonElement nv ? Text(nv) : null));
                }
            }

            JsonElement dec = Member(c, "decision");
            return new Change(
                Int(c, "id"),
                Enum(JsonNames.ChangeKinds, Str(c, "kind")),
                Int(c, "link"),
                ReadSide(Member(c, "old")),
                ReadSide(Member(c, "new")),
                edits.ToImmutable(),
                attrs.ToImmutable(),
                new Decision(Enum(JsonNames.DecidedBys, Str(dec, "by")), Str(dec, "source"), Num(dec, "score")),
                Opt(c, "properties") is JsonElement p ? ReadFormat(p) : FormatSet.Empty);
        }

        private ChangeSide ReadSide(JsonElement s) =>
            new(Ints(Member(s, "nodes")), Int(s, "start"), Int(s, "length"), Str(s, "text"), ReadAnchor(Member(s, "anchor")));

        private ImmutableArray<FormatRun> ReadRuns(JsonElement e, int textLength)
        {
            var runs = new List<FormatRun>();
            foreach (JsonElement r in Items(e))
            {
                runs.Add(new FormatRun(Int(r, "start"), Int(r, "length"), ReadFormat(Member(r, "format"))));
            }

            // Run lists in edits are relative to the edit; the bound is checked against the text they belong to when replayed.
            return textLength == int.MaxValue ? [.. runs] : FormatRuns.Canonicalize(runs, textLength);
        }

        private FormatSet ReadFormat(JsonElement e)
        {
            RequireObject(e, "a format object");
            var items = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty p in e.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String || !seen.Add(p.Name))
                {
                    throw new ComparisonFormatException("a format object holds a non-string value or a repeated key");
                }

                if (items.Count >= limits.MaxItems)
                {
                    throw new ComparisonFormatException("a format object is over the item limit");
                }

                items.Add(new KeyValuePair<string, string>(p.Name, p.Value.GetString()!));
            }

            return FormatSet.Create(items);
        }

        private Anchor ReadAnchor(JsonElement a)
        {
            AnchorKind kind = Enum(JsonNames.AnchorKinds, Str(a, "kind"));
            switch (kind)
            {
                case AnchorKind.None:
                    return Anchor.None;
                case AnchorKind.Node:
                    return Anchor.ForNode(Str(a, "reference"));
                case AnchorKind.TextSpan:
                    return Anchor.ForTextSpan(Str(a, "reference"), Int(a, "start"), Int(a, "length"));
                case AnchorKind.Cell:
                    return Anchor.ForCell(Str(a, "reference"), Str(a, "address"));
                default:
                    var quads = new List<Quad>();
                    foreach (JsonElement q in Items(Member(a, "quads")))
                    {
                        if (q.ValueKind != JsonValueKind.Array || q.GetArrayLength() != 9)
                        {
                            throw new ComparisonFormatException("a quad is an array of nine numbers");
                        }

                        double[] v = new double[9];
                        int i = 0;
                        foreach (JsonElement x in q.EnumerateArray())
                        {
                            v[i++] = x.GetDouble();
                        }

                        quads.Add(new Quad((int)v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]));
                    }

                    return kind == AnchorKind.PdfQuads ? Anchor.ForPdfQuads(quads) : Anchor.ForOcrWords(quads, Num(a, "confidence"));
            }
        }

        private static string Text(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.String)
            {
                throw new ComparisonFormatException("expected a string");
            }

            return e.GetString()!;
        }

        private ImmutableArray<int> Ints(JsonElement e)
        {
            ImmutableArray<int>.Builder b = ImmutableArray.CreateBuilder<int>();
            foreach (JsonElement x in Items(e))
            {
                b.Add(x.GetInt32());
            }

            return b.ToImmutable();
        }

        private JsonElement.ArrayEnumerator Items(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Array)
            {
                throw new ComparisonFormatException("expected an array");
            }

            if (e.GetArrayLength() > limits.MaxItems)
            {
                throw new ComparisonFormatException($"an array has {e.GetArrayLength()} items, over the limit of {limits.MaxItems}");
            }

            return e.EnumerateArray();
        }

        private static void RequireObject(JsonElement e, string what)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                throw new ComparisonFormatException(what + " must be an object");
            }
        }

        private static JsonElement Member(JsonElement e, string name)
        {
            RequireObject(e, "a value holding \"" + name + "\"");
            return e.TryGetProperty(name, out JsonElement v) ? v : throw new ComparisonFormatException($"missing member \"{name}\"");
        }

        private static JsonElement? Opt(JsonElement e, string name)
        {
            RequireObject(e, "a value holding \"" + name + "\"");
            return e.TryGetProperty(name, out JsonElement v) ? v : null;
        }

        private static string Str(JsonElement e, string name) => Text(Member(e, name));

        private static int Int(JsonElement e, string name) => Member(e, name).GetInt32();

        private static double Num(JsonElement e, string name)
        {
            double d = Member(e, name).GetDouble();
            return double.IsFinite(d) ? d : throw new ComparisonFormatException("numbers must be finite");
        }

        private static bool Bool(JsonElement e, string name)
        {
            JsonElement v = Member(e, name);
            return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new ComparisonFormatException($"\"{name}\" must be true or false");
        }

        private static T Enum<T>(EnumNames<T> names, string name)
            where T : struct =>
            names.TryParse(name, out T v) ? v : throw new ComparisonFormatException($"unknown name \"{name}\"");
    }
}
