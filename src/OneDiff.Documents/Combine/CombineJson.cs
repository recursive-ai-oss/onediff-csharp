using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using OneDiff.Documents.Serialization;

namespace OneDiff.Documents.Combine;

/// <summary>
/// The JSON forms of a <see cref="Combination"/> (<c>onediff.combination</c>) and a <see cref="ComparisonChain"/>
/// (<c>onediff.chain</c>), version 1, written with <see cref="Utf8JsonWriter"/> (no reflection). Each embeds the full
/// <c>onediff.comparison</c> document of every reviewer or step unchanged, so a combined change refers to the reviewer's own
/// change by <c>(reviewer, change)</c>; the combination adds the attribution, the sharing and the conflicts. The form is
/// byte-identical for equal inputs. See docs/combine.md for the members.
/// </summary>
public static class CombineJson
{
    /// <summary>The combination's schema name.</summary>
    public const string CombinationSchemaName = "onediff.combination";

    /// <summary>The chain's schema name.</summary>
    public const string ChainSchemaName = "onediff.chain";

    /// <summary>The version of both forms.</summary>
    public const int SchemaVersion = 1;

    private static readonly string[] ConflictNames = ["editAndDelete", "edits", "move", "structure", "attribute", "insert"];

    /// <summary>The combination as UTF-8 JSON.</summary>
    public static byte[] Write(Combination combination)
    {
        ArgumentNullException.ThrowIfNull(combination);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            w.WriteStartObject();
            w.WriteString("schema", CombinationSchemaName);
            w.WriteNumber("version", SchemaVersion);
            w.WriteStartObject("original");
            w.WriteString("name", combination.Original.Name);
            w.WriteString("hash", combination.Original.SourceHash);
            w.WriteEndObject();
            w.WriteStartArray("reviewers");
            foreach (Reviewer r in combination.Reviewers)
            {
                w.WriteStartObject();
                w.WriteNumber("index", r.Index);
                w.WriteString("name", r.Name);
                w.WriteString("hash", r.Hash);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartObject("verification");
            w.WriteBoolean("passed", combination.Verification.Passed);
            w.WriteNumber("fallbackLevel", combination.Verification.FallbackLevel);
            w.WriteNumber("failureCount", combination.Verification.FailureCount);
            w.WriteEndObject();
            w.WriteStartArray("changes");
            foreach (CombinedChange c in combination.Changes)
            {
                w.WriteStartObject();
                w.WriteNumber("id", c.Id);
                w.WriteString("kind", JsonNames.ChangeKinds.Name(c.Change.Kind));
                Ints(w, "reviewers", c.Reviewers);
                w.WriteStartArray("holders");
                foreach (ChangeHolder h in c.Holders)
                {
                    w.WriteStartObject();
                    w.WriteNumber("reviewer", h.Reviewer);
                    w.WriteNumber("change", h.ChangeId);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                if (!c.EditReviewers.IsDefaultOrEmpty)
                {
                    w.WriteStartArray("editReviewers");
                    foreach (ImmutableArray<int> e in c.EditReviewers)
                    {
                        w.WriteStartArray();
                        foreach (int r in e)
                        {
                            w.WriteNumberValue(r);
                        }

                        w.WriteEndArray();
                    }

                    w.WriteEndArray();
                }

                if (c.ConflictId >= 0)
                {
                    w.WriteNumber("conflict", c.ConflictId);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("conflicts");
            foreach (Conflict k in combination.Conflicts)
            {
                w.WriteStartObject();
                w.WriteNumber("id", k.Id);
                w.WriteString("kind", ConflictNames[(int)k.Kind]);
                Ints(w, "changes", k.ChangeIds);
                Ints(w, "reviewers", k.Reviewers);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            Diagnostics(w, combination.Diagnostics);
            w.WriteStartArray("comparisons");
            foreach (Comparison c in combination.Comparisons)
            {
                w.WriteRawValue(ComparisonJson.Write(c), skipInputValidation: true);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The chain as UTF-8 JSON.</summary>
    public static byte[] Write(ComparisonChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            w.WriteStartObject();
            w.WriteString("schema", ChainSchemaName);
            w.WriteNumber("version", SchemaVersion);
            w.WriteStartArray("versions");
            foreach (Model.CompareDocument v in chain.Versions)
            {
                w.WriteStartObject();
                w.WriteString("name", v.Name);
                w.WriteString("hash", v.SourceHash);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("steps");
            foreach (ChainStep s in chain.Steps)
            {
                w.WriteStartObject();
                w.WriteNumber("index", s.Index);
                w.WriteString("from", s.FromName);
                w.WriteString("to", s.ToName);
                w.WritePropertyName("comparison");
                w.WriteRawValue(ComparisonJson.Write(s.Comparison), skipInputValidation: true);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartObject("net");
            w.WritePropertyName("comparison");
            w.WriteRawValue(ComparisonJson.Write(chain.Net), skipInputValidation: true);
            w.WriteStartArray("changeSteps");
            foreach (Change c in chain.Net.Changes)
            {
                w.WriteStartObject();
                w.WriteNumber("change", c.Id);
                Ints(w, "steps", chain.StepsOf(c.Id));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            Diagnostics(w, chain.Diagnostics);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void Ints(Utf8JsonWriter w, string name, ImmutableArray<int> values)
    {
        w.WriteStartArray(name);
        foreach (int v in values)
        {
            w.WriteNumberValue(v);
        }

        w.WriteEndArray();
    }

    private static void Diagnostics(Utf8JsonWriter w, ImmutableArray<Diagnostic> diagnostics)
    {
        w.WriteStartArray("diagnostics");
        foreach (Diagnostic d in diagnostics)
        {
            w.WriteStartObject();
            w.WriteNumber("severity", (int)d.Severity);
            w.WriteString("code", d.Code);
            w.WriteString("message", d.Message);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }
}
