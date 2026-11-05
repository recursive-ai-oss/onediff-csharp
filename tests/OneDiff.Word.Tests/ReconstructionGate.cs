using System.Collections.Immutable;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Word.Internal;

namespace OneDiff.Word.Tests;

/// <summary>
/// The checks for a tracked-changes DOCX whose sides may be reconstructed (not Word files): the reconstruction reads back as its model,
/// and accept-all and reject-all give back the reconstructed (or real) new and old documents.
/// </summary>
internal static class ReconstructionGate
{
    /// <summary>The character formatting a reconstruction carries.</summary>
    private static readonly string[] Carried = [FormatKeys.Bold, FormatKeys.Italic, FormatKeys.Font, FormatKeys.Size, FormatKeys.Color, FormatKeys.Underline, FormatKeys.Strike];

    public sealed record Side(CompareDocument Document, byte[] Bytes, bool Reconstructed);

    /// <summary>The side as the writer sees it: a Word document as is, anything else rebuilt (the same rebuild the writer makes).</summary>
    public static Side Prepare(CompareDocument model, bool note = true)
    {
        if (WordOrigins.TryGet(model, out WordOrigin o))
        {
            return new Side(model, o.Bytes.ToArray(), false);
        }

        ReconstructedDocx r = Reconstruction.Build(model, model.SourceFormat, note);
        return new Side(Harness.Read(r.Bytes, model.Name + ".docx"), r.Bytes, true);
    }

    /// <summary>Whether every paragraph of the package, read back, carries the model's character formatting and list label.</summary>
    public static List<string> Fidelity(CompareDocument model, byte[] package)
    {
        var problems = new List<string>();
        CompareDocument read = Harness.Read(package);
        // The two documents have the same blocks in the same order (the reconstruction checks this); the header and footer of the
        // model come first in the read-back tree, so compare paragraph by paragraph in the body only.
        List<Node> mine = [.. BodyParagraphs(model)], theirs = [.. BodyParagraphs(read)];
        if (mine.Count != theirs.Count)
        {
            problems.Add($"paragraph count {mine.Count} != {theirs.Count}");
            return problems;
        }

        for (int i = 0; i < mine.Count; i++)
        {
            Node a = mine[i], b = theirs[i];
            if (a.Text != b.Text)
            {
                problems.Add($"text of paragraph {i}");
                continue;
            }

            string[] fa = PerChar(a), fb = PerChar(b);
            for (int k = 0; k < fa.Length; k++)
            {
                // A character the model gives no formatting (the space that stands for a line break) reads back with the document's defaults.
                if (fa[k].Length > 0 && fa[k] != fb[k])
                {
                    problems.Add($"paragraph {i} char {k}: model '{fa[k]}' package '{fb[k]}'");
                    break;
                }
            }

            if (a.Kind != b.Kind)
            {
                problems.Add($"paragraph {i}: {a.Kind} vs {b.Kind}");
            }
            else if (a.Attributes.Get(FormatKeys.ListLabel) != b.Attributes.Get(FormatKeys.ListLabel))
            {
                problems.Add($"paragraph {i}: list label");
            }
        }

        return problems;
    }

    private static IEnumerable<Node> BodyParagraphs(CompareDocument d)
    {
        foreach (Node n in d.Nodes)
        {
            if (n.Kind is not (NodeKind.Paragraph or NodeKind.ListItem))
            {
                continue;
            }

            bool furniture = false;
            for (int p = n.Parent; p >= 0; p = d[p].Parent)
            {
                furniture |= d[p].Kind is NodeKind.Header or NodeKind.Footer;
            }

            if (!furniture)
            {
                yield return n;
            }
        }
    }

    private static string[] PerChar(Node n)
    {
        var res = new string[n.Text.Length];
        Array.Fill(res, "");
        foreach (FormatRun r in n.Runs)
        {
            var sb = new StringBuilder();
            foreach (string key in Carried)
            {
                if (r.Format.Get(key) is { } v)
                {
                    sb.Append(key).Append('=').Append(key == FormatKeys.Color ? v.TrimStart('#').ToUpperInvariant() : v).Append(';');
                }
            }

            for (int i = r.Start; i < r.End; i++)
            {
                res[i] = sb.ToString();
            }
        }

        return res;
    }

    /// <summary>Writes the comparison as a DOCX and runs every check; the problems found (empty when all hold).</summary>
    public static List<string> Run(Comparison cmp, WordBuildOn on, out byte[] output, out DocxRedlineResult result, bool detail = false)
    {
        using var ms = new MemoryStream();
        result = new DocxRedline { BuildOn = on }.Create(cmp, ms);
        output = ms.ToArray();
        Side o = Prepare(cmp.Old), n = Prepare(cmp.New);
        var problems = new List<string>();
        foreach (Side s in new[] { o, n })
        {
            if (s.Reconstructed)
            {
                problems.AddRange(Fidelity(s == o ? cmp.Old : cmp.New, s.Bytes).Select(x => "fidelity: " + x));
            }
        }

        problems.AddRange(TrackedGate.Problems(o.Document, n.Document, output, cmp.Settings, on, result.NotCarried, detail));
        return problems;
    }

    public static ImmutableArray<string> Describe(DocxRedlineResult r) => [.. r.NotCarried.Select(x => x.Category)];
}
