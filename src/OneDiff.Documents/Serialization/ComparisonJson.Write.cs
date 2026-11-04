using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using OneDiff.Documents.Ledger;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Serialization;

/// <summary>
/// Writes a <see cref="Comparison"/> as JSON (schema version 1, see docs/schema/comparison-v1.json). The output is compact
/// UTF-8, keys in a fixed order, optional empty members omitted by fixed rules, numbers in shortest round-trip form and
/// non-ASCII text escaped, so the same comparison always yields the same bytes on every platform.
/// </summary>
public static partial class ComparisonJson
{
    /// <summary>The value of the <c>schema</c> member.</summary>
    public const string SchemaName = "onediff.comparison";

    /// <summary>The schema version this library writes and reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Serializes a comparison.</summary>
    /// <exception cref="ArgumentException">A string in the comparison holds an unpaired surrogate (only possible for text a custom stage put there).</exception>
    public static byte[] Write(Comparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var buffer = new ArrayBufferWriter<byte>(4096);
        Write(comparison, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Serializes a comparison into a buffer writer.</summary>
    public static void Write(Comparison comparison, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(output);
        using var w = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false });
        w.WriteStartObject();
        w.WriteString("schema", SchemaName);
        w.WriteNumber("version", SchemaVersion);
        w.WriteStartObject("provenance");
        w.WriteString("library", comparison.Provenance.LibraryVersion);
        w.WriteString("oldName", comparison.Provenance.OldName);
        w.WriteString("newName", comparison.Provenance.NewName);
        w.WriteString("oldHash", comparison.Provenance.OldHash);
        w.WriteString("newHash", comparison.Provenance.NewHash);
        w.WriteEndObject();
        w.WritePropertyName("settings");
        WriteSettings(w, comparison.Settings);
        w.WritePropertyName("old");
        WriteDocument(w, comparison.Old);
        w.WritePropertyName("new");
        WriteDocument(w, comparison.New);
        w.WriteStartArray("links");
        foreach (AlignmentLink l in comparison.Alignment.Links)
        {
            w.WriteStartObject();
            w.WriteString("kind", JsonNames.LinkKinds.Name(l.Kind));
            WriteInts(w, "old", l.OldNodes);
            WriteInts(w, "new", l.NewNodes);
            w.WriteBoolean("pinned", l.Pinned);
            w.WriteNumber("score", l.Score);
            w.WriteString("origin", JsonNames.LinkOrigins.Name(l.Origin));
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("changes");
        foreach (Change c in comparison.Changes)
        {
            WriteChange(w, c);
        }

        w.WriteEndArray();
        w.WriteStartArray("groups");
        foreach (ChangeGroup g in comparison.Groups)
        {
            w.WriteStartObject();
            w.WriteNumber("id", g.Id);
            w.WriteString("kind", JsonNames.GroupKinds.Name(g.Kind));
            w.WriteString("title", g.Title);
            WriteInts(w, "changes", g.ChangeIds);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("ledger");
        foreach (LedgerEntry e in comparison.Ledger.Entries)
        {
            w.WriteStartObject();
            w.WriteString("category", LedgerCategoryNames.NameOf(e.Category));
            w.WriteNumber("count", e.Count);
            w.WriteStartArray("locations");
            foreach (LedgerLocation l in e.Locations)
            {
                w.WriteStartObject();
                w.WriteNumber("oldNode", l.OldNode);
                w.WriteNumber("newNode", l.NewNode);
                w.WriteString("detail", l.Detail);
                WriteAnchorMember(w, "oldAnchor", l.OldAnchor);
                WriteAnchorMember(w, "newAnchor", l.NewAnchor);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("diagnostics");
        foreach (Diagnostic d in comparison.Diagnostics)
        {
            w.WriteStartObject();
            w.WriteString("severity", JsonNames.Severities.Name(d.Severity));
            w.WriteString("code", d.Code);
            w.WriteString("message", d.Message);
            w.WriteNumber("oldNode", d.OldNode);
            w.WriteNumber("newNode", d.NewNode);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartObject("verification");
        w.WriteBoolean("passed", comparison.Verification.Passed);
        w.WriteNumber("fallbackLevel", comparison.Verification.FallbackLevel);
        w.WriteNumber("failureCount", comparison.Verification.FailureCount);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteSettings(Utf8JsonWriter w, CompareSettings s)
    {
        w.WriteStartObject();
        w.WriteString("name", s.Name);
        w.WriteStartArray("hidden");
        foreach (LedgerCategory c in s.Hidden.ToArray())
        {
            w.WriteStringValue(LedgerCategoryNames.NameOf(c));
        }

        w.WriteEndArray();
        w.WriteBoolean("throwOnFailure", s.ThrowOnFailure);
        w.WriteNumber("costCap", s.CostCap);
        w.WriteString("algorithm", JsonNames.Algorithms.Name(s.Algorithm));
        w.WriteNumber("matchThreshold", s.MatchThreshold);
        w.WriteNumber("splitMergeThreshold", s.SplitMergeThreshold);
        w.WriteNumber("moveThreshold", s.MoveThreshold);
        w.WriteBoolean("detectMoves", s.DetectMoves);
        w.WriteBoolean("detectSplitsAndMerges", s.DetectSplitsAndMerges);
        w.WriteBoolean("collapseCascades", s.CollapseCascades);
        w.WriteBoolean("typedChanges", s.TypedChanges);
        w.WriteNumber("seed", s.Seed);
        w.WriteStartObject("style");
        RedlineStyle st = s.Style;
        w.WriteString("name", st.Name);
        WriteMark(w, "insertion", st.Insertion);
        WriteMark(w, "deletion", st.Deletion);
        WriteMark(w, "moveFrom", st.MoveFrom);
        WriteMark(w, "moveTo", st.MoveTo);
        WriteMark(w, "formatting", st.Formatting);
        w.WriteBoolean("changeBars", st.ChangeBars);
        w.WriteString("changeBarColor", st.ChangeBarColor);
        w.WriteBoolean("coverPage", st.CoverPage);
        w.WriteBoolean("footer", st.Footer);
        w.WriteBoolean("typeTags", st.TypeTags);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteMark(Utf8JsonWriter w, string name, MarkStyle m)
    {
        w.WriteStartObject(name);
        w.WriteString("color", m.Color);
        w.WriteString("decoration", JsonNames.Decorations.Name(m.Decoration));
        w.WriteString("glyph", m.Glyph);
        w.WriteEndObject();
    }

    private static void WriteDocument(Utf8JsonWriter w, CompareDocument d)
    {
        w.WriteStartObject();
        w.WriteString("kind", JsonNames.DocumentKinds.Name(d.Kind));
        w.WriteString("name", d.Name);
        w.WriteString("sourceHash", d.SourceHash);
        w.WriteString("sourceFormat", d.SourceFormat);
        w.WriteStartArray("nodes");
        foreach (Node n in d.Nodes)
        {
            w.WriteStartObject();
            w.WriteNumber("parent", n.Parent);
            w.WriteString("kind", JsonNames.NodeKinds.Name(n.Kind));
            if (n.Text.Length > 0)
            {
                w.WriteString("text", n.Text);
            }

            if (!n.Runs.IsEmpty)
            {
                WriteRuns(w, "runs", n.Runs);
            }

            if (!n.Attributes.IsEmpty)
            {
                WriteFormat(w, "attributes", n.Attributes);
            }

            if (n.Anchor.Kind != AnchorKind.None)
            {
                WriteAnchorMember(w, "anchor", n.Anchor);
            }

            if (!n.Segments.IsEmpty)
            {
                w.WriteStartArray("segments");
                foreach (AnchoredSpan s in n.Segments)
                {
                    w.WriteStartObject();
                    w.WriteNumber("start", s.Start);
                    w.WriteNumber("length", s.Length);
                    WriteAnchorMember(w, "anchor", s.Anchor);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            if (!n.Folds.IsEmpty)
            {
                w.WriteStartArray("folds");
                foreach (NormalizationFold f in n.Folds)
                {
                    w.WriteStartObject();
                    w.WriteString("category", LedgerCategoryNames.NameOf(f.Category));
                    w.WriteNumber("start", f.Start);
                    w.WriteNumber("length", f.Length);
                    w.WriteString("original", f.Original);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        if (!d.Opaque.IsEmpty)
        {
            w.WriteStartArray("opaque");
            foreach (OpaquePart p in d.Opaque)
            {
                w.WriteStartObject();
                w.WriteString("name", p.Name);
                w.WriteString("hash", p.Hash);
                if (p.Anchor.Kind != AnchorKind.None)
                {
                    WriteAnchorMember(w, "anchor", p.Anchor);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void WriteChange(Utf8JsonWriter w, Change c)
    {
        w.WriteStartObject();
        w.WriteNumber("id", c.Id);
        w.WriteString("kind", JsonNames.ChangeKinds.Name(c.Kind));
        w.WriteNumber("link", c.LinkId);
        WriteSide(w, "old", c.Old);
        WriteSide(w, "new", c.New);
        if (!c.Edits.IsEmpty)
        {
            w.WriteStartArray("edits");
            foreach (RegionEdit e in c.Edits)
            {
                w.WriteStartObject();
                w.WriteNumber("oldStart", e.OldStart);
                w.WriteNumber("oldLength", e.OldLength);
                w.WriteNumber("newStart", e.NewStart);
                w.WriteNumber("newLength", e.NewLength);
                w.WriteString("oldText", e.OldText);
                w.WriteString("newText", e.NewText);
                if (!e.OldRuns.IsEmpty)
                {
                    WriteRuns(w, "oldRuns", e.OldRuns);
                }

                if (!e.NewRuns.IsEmpty)
                {
                    WriteRuns(w, "newRuns", e.NewRuns);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (!c.AttributeEdits.IsEmpty)
        {
            w.WriteStartArray("attributeEdits");
            foreach (AttributeEdit e in c.AttributeEdits)
            {
                w.WriteStartObject();
                w.WriteNumber("oldNode", e.OldNode);
                w.WriteNumber("newNode", e.NewNode);
                w.WriteString("key", e.Key);
                if (e.OldValue is not null)
                {
                    w.WriteString("oldValue", e.OldValue);
                }

                if (e.NewValue is not null)
                {
                    w.WriteString("newValue", e.NewValue);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteStartObject("decision");
        w.WriteString("by", JsonNames.DecidedBys.Name(c.Decision.By));
        w.WriteString("source", c.Decision.Source);
        w.WriteNumber("score", c.Decision.Score);
        w.WriteEndObject();
        if (!c.Properties.IsEmpty)
        {
            WriteFormat(w, "properties", c.Properties);
        }

        w.WriteEndObject();
    }

    private static void WriteSide(Utf8JsonWriter w, string name, ChangeSide s)
    {
        w.WriteStartObject(name);
        WriteInts(w, "nodes", s.Nodes);
        w.WriteNumber("start", s.Start);
        w.WriteNumber("length", s.Length);
        w.WriteString("text", s.Text);
        WriteAnchorMember(w, "anchor", s.Anchor);
        w.WriteEndObject();
    }

    private static void WriteInts(Utf8JsonWriter w, string name, ImmutableArray<int> values)
    {
        w.WriteStartArray(name);
        if (!values.IsDefaultOrEmpty)
        {
            foreach (int v in values)
            {
                w.WriteNumberValue(v);
            }
        }

        w.WriteEndArray();
    }

    private static void WriteRuns(Utf8JsonWriter w, string name, ImmutableArray<FormatRun> runs)
    {
        w.WriteStartArray(name);
        foreach (FormatRun r in runs)
        {
            w.WriteStartObject();
            w.WriteNumber("start", r.Start);
            w.WriteNumber("length", r.Length);
            WriteFormat(w, "format", r.Format);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteFormat(Utf8JsonWriter w, string name, FormatSet f)
    {
        w.WriteStartObject(name);
        foreach (KeyValuePair<string, string> kv in f.Entries)
        {
            w.WriteString(kv.Key, kv.Value);
        }

        w.WriteEndObject();
    }

    private static void WriteAnchorMember(Utf8JsonWriter w, string name, Anchor a)
    {
        w.WriteStartObject(name);
        w.WriteString("kind", JsonNames.AnchorKinds.Name(a.Kind));
        switch (a.Kind)
        {
            case AnchorKind.Node:
                w.WriteString("reference", a.Reference);
                break;
            case AnchorKind.TextSpan:
                w.WriteString("reference", a.Reference);
                w.WriteNumber("start", a.Start);
                w.WriteNumber("length", a.Length);
                break;
            case AnchorKind.Cell:
                w.WriteString("reference", a.Reference);
                w.WriteString("address", a.Address);
                break;
            case AnchorKind.PdfQuads:
            case AnchorKind.OcrWords:
                if (a.Confidence is double conf)
                {
                    w.WriteNumber("confidence", conf);
                }

                w.WriteStartArray("quads");
                foreach (Quad q in a.Quads)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(q.Page);
                    w.WriteNumberValue(q.X1);
                    w.WriteNumberValue(q.Y1);
                    w.WriteNumberValue(q.X2);
                    w.WriteNumberValue(q.Y2);
                    w.WriteNumberValue(q.X3);
                    w.WriteNumberValue(q.Y3);
                    w.WriteNumberValue(q.X4);
                    w.WriteNumberValue(q.Y4);
                    w.WriteEndArray();
                }

                w.WriteEndArray();
                break;
        }

        w.WriteEndObject();
    }
}
