using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace OneDiff.Word.Tests;

/// <summary>
/// Validates a DOCX against the ECMA-376 schemas with the Open XML SDK's validator (Office 2019 file format version). The inputs are
/// validated too, because the generator and the converter that wrote them have quirks of their own: only an error that no input has
/// (same description and element) counts against the tracked-changes output.
/// </summary>
internal static class SchemaCheck
{
    public static HashSet<string> Signatures(byte[] docx)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string e in Errors(docx))
        {
            set.Add(e);
        }

        return set;
    }

    /// <summary>The errors of the output that are in none of the baselines.</summary>
    public static List<string> NewErrors(byte[] output, params HashSet<string>[] baselines)
    {
        var list = new List<string>();
        foreach (string e in Errors(output))
        {
            if (!baselines.Any(b => b.Contains(e)))
            {
                list.Add(e);
            }
        }

        return list;
    }

    private static IEnumerable<string> Errors(byte[] docx)
    {
        using var ms = new MemoryStream(docx);
        using WordprocessingDocument doc = WordprocessingDocument.Open(ms, false);
        var validator = new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019);
        foreach (ValidationErrorInfo e in validator.Validate(doc))
        {
            // The element's local name and the rule, not its position: the same defect is the same signature wherever it occurs.
            yield return e.Id + " " + (e.Node?.LocalName ?? "?") + " " + e.Description;
        }
    }
}
