using System.Runtime.CompilerServices;
using OneDiff.Documents;

namespace OneDiff.Pdf.Tests;

/// <summary>
/// A generated English contract whose table holds a cell that wraps over two lines ("6" and "months"). Read as a grid, the cell's
/// lines are joined by a space, not a hard break: a hard break made the cell narrower than its neighbours' lines and the inline page,
/// read back, no longer carried the cell's text where the change list puts it.
/// </summary>
public class RenderedReadbackTests
{
    private static string TestData([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "testdata", "rendered-readback"));

    [Fact]
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
