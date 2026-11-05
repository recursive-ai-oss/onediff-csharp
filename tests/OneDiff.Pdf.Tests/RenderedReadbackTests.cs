using System.Runtime.CompilerServices;
using OneDiff.Documents;

namespace OneDiff.Pdf.Tests;

/// <summary>
/// A minimized failure of the rendered-redline read-back gate. The pair is a generated English contract whose table holds a cell
/// that wraps over two lines ("6" and "months"); the source keeps the line break in the node text, and the inline rendered page, read
/// back, does not carry that text at the place the change list gives. Side-by-side pages of the same pair read back clean.
/// </summary>
public class RenderedReadbackTests
{
    private static string TestData([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "testdata", "rendered-readback"));

    [Fact(Skip = "Open finding: the inline rendered redline of this pair does not read back in change-list order (a table cell whose text wraps over two lines); unskip when fixed.")]
    [Trait("Host", "Native")] // reads fixture files
    public void TheInlineRenderedRedlineOfAWrappedTableCellReadsBack()
    {
        Comparison c = Comparison.Of(
            Run.Read(File.ReadAllBytes(Path.Combine(TestData(), "en-021-s2.old.pdf")), "old.pdf"),
            Run.Read(File.ReadAllBytes(Path.Combine(TestData(), "en-021-s2.new.pdf")), "new.pdf"),
            CompareSettings.Strict with { ThrowOnFailure = false });
        using var ms = new MemoryStream();
        PdfRedline.Rendered.Render(c, ms, RenderedGate.Style);
        string[] errors = [.. RenderedGate.Check(c, ms.ToArray(), RenderedGate.Style, false)];
        Assert.True(errors.Length == 0, string.Join("\n", errors));
    }
}
