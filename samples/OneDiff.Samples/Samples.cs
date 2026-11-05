using System.Collections.Immutable;
using OneDiff;
using OneDiff.Documents;
using OneDiff.Documents.Combine;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Serialization;
using OneDiff.Merge;
using OneDiff.Output;
using OneDiff.Ocr;
using OneDiff.Pdf;

namespace OneDiff.Samples;

/// <summary>
/// One task per method, each one or two lines over the happy path (the line or two that matter are marked "the sample").
/// Paths are arguments, so a sample reads like the call you would write in your own program. Each sample runs in the
/// repository's test suite on small documents written for the purpose.
/// </summary>
public static class Samples
{
    // ---- The text core -------------------------------------------------------------------------------------------

    /// <summary>The words that changed between two strings, as Markdown with <c>&lt;del&gt;</c> and <c>&lt;ins&gt;</c> marks around the changed words.</summary>
    public static string DiffTwoStrings(string oldText, string newText) =>
        Diff.ToMarkdownInline(Diff.Words(oldText, newText));                                    // the sample

    /// <summary>Makes a unified diff of two texts and applies it to the old text, which gives the new text back.</summary>
    public static string MakeAndApplyAUnifiedPatch(string oldText, string newText)
    {
        string patch = Diff.Unified(oldText, newText, "a/clause.txt", "b/clause.txt");          // the sample
        return Diff.ApplyUnified(oldText, patch).Text;
    }

    /// <summary>A three-way merge of two edits of one base text. Edits to different lines merge; edits to the same line are a conflict.</summary>
    public static TextMerge ThreeWayMerge(string baseText, string ours, string theirs) =>
        Diff.Merge(baseText, ours, theirs);                                                     // the sample

    /// <summary>The lines of a line diff with their kind and line numbers, the way a code-review view lists them: <c>-</c>, <c>+</c> or a blank prefix.</summary>
    public static IEnumerable<string> ReadTheLines(string oldText, string newText) =>
        Diff.Lines(oldText, newText).Lines.Select(l => $"{(l.Kind == EditKind.Insert ? '+' : l.Kind == EditKind.Delete ? '-' : ' ')} {l.Text}"); // the sample

    /// <summary>A line diff laid out in two columns: aligned rows, with the changed words marked inside modified lines.</summary>
    public static SideBySideDiff TwoColumns(string oldText, string newText) =>
        Diff.SideBySide(oldText, newText);                                                      // the sample

    /// <summary>A line diff of untrusted text with a work cap; the diagnostic says whether the cap made the diff coarser than the minimum.</summary>
    public static bool CappedLineDiff(string oldText, string newText) =>
        Diff.Lines(oldText, newText, new DiffSettings { MaxWork = 100_000 }).Diagnostic.CostCapReached; // the sample

    // ---- Documents -----------------------------------------------------------------------------------------------

    /// <summary>Compares two PDFs and writes a rendered redline: a new, tagged PDF laid out from the comparison.</summary>
    public static void ComparePdfsToARedlinePdf(string oldPdf, string newPdf, string redlinePdf)
    {
        PdfRedline.Register();                                                                  // once at startup
        Comparison.Of(oldPdf, newPdf).SaveAs(redlinePdf, PdfRedline.Rendered);                  // the sample
    }

    /// <summary>Compares two PDFs and marks the changes on the new PDF's own pages with highlights and notes.</summary>
    public static void MarkChangesOnThePages(string oldPdf, string newPdf, string markedPdf)
    {
        PdfRedline.Register();                                                                  // once at startup
        Redline.Create(oldPdf, newPdf, markedPdf);                                              // the sample
    }

    /// <summary>Saves the versioned JSON change list and reads it back as a <see cref="Comparison"/>.</summary>
    public static Comparison SaveTheChangeListAndReadItBack(string oldPath, string newPath, string jsonPath)
    {
        Redline.Create(oldPath, newPath, jsonPath);                                             // the sample
        return ComparisonJson.Read(File.ReadAllBytes(jsonPath));
    }

    /// <summary>Writes a self-contained HTML page with the old and new text side by side.</summary>
    public static void HtmlSideBySide(string oldPath, string newPath, string htmlPath) =>
        Comparison.Of(oldPath, newPath).SaveAs(htmlPath, RedlineForm.HtmlSideBySide);           // the sample

    /// <summary>Compares two scans (images or image-only PDFs) and marks the changes on the new scan; returns the changes whose words were read with low confidence.</summary>
    public static ImmutableArray<Change> CompareScans(string oldScan, string newScan, string markedPdf)
    {
        OcrRedline.Register();                                                                  // once at startup
        Redline.Create(oldScan, newScan, markedPdf);                                            // the sample
        return [.. Comparison.Of(oldScan, newScan).Changes.Where(static c => c.PossibleOcrError)];
    }

    /// <summary>Lists what changed in substance, one line per change, with the resolved value when the change is a number, date, duration or amount.</summary>
    public static IEnumerable<string> WalkTheSubstantiveChanges(string oldPath, string newPath)
    {
        Comparison comparison = Comparison.Of(oldPath, newPath, CompareSettings.Legal);
        foreach (Change change in comparison.Changes.Where(static c => c.IsSubstantive))        // the sample
        {
            yield return change.OldValue is { } was && change.NewValue is { } now
                ? $"{change.Type}: {was.Canonical} -> {now.Canonical}"
                : $"{change.Kind}: \"{change.Old.Text}\" -> \"{change.New.Text}\"";
        }
    }

    /// <summary>Combines several reviewers' copies of one original into a report of who changed what and where they conflict.</summary>
    public static void CombineReviewers(string original, IReadOnlyList<string> copies, string reportHtml) =>
        Redline.Combine(original, copies, reportHtml);                                          // the sample

    /// <summary>Compares versions one after the other and writes a report that attributes each change to its step.</summary>
    public static void CompareAVersionChain(IReadOnlyList<string> versions, string reportHtml) =>
        Redline.Chain(versions, reportHtml);                                                    // the sample

    // ---- The low level -------------------------------------------------------------------------------------------

    /// <summary>
    /// Aligns two documents, corrects the alignment by hand and runs the rest of the stages again. The engine reads a paragraph that
    /// was rewritten from scratch as a deletion plus an insertion; pinning the two together turns that into one replacement.
    /// Returns the comparison before and after the correction.
    /// </summary>
    public static (Comparison Before, Comparison After) PinAnAlignmentAndRerun(string oldText, string newText, string oldStartsWith, string newStartsWith)
    {
        CompareDocument a = CompareDocument.FromText(oldText, "old");
        CompareDocument b = CompareDocument.FromText(newText, "new");
        Alignment alignment = Stages.Align(a, b);
        Comparison before = Run(alignment);

        int oldNode = a.Nodes.First(n => n.Kind == NodeKind.Paragraph && n.Text.StartsWith(oldStartsWith, StringComparison.Ordinal)).Id;
        int newNode = b.Nodes.First(n => n.Kind == NodeKind.Paragraph && n.Text.StartsWith(newStartsWith, StringComparison.Ordinal)).Id;
        return (before, Run(alignment.Pin(oldNode, newNode)));                                  // the sample

        static Comparison Run(Alignment al) => Stages.Verify(Stages.Classify(Stages.Group(Stages.DiffPairs(al))));
    }
}
