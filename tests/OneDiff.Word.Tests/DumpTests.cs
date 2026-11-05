using System.Text;
using OneDiff.Documents.Model;

namespace OneDiff.Word.Tests;

public class DumpTests
{
    [Fact]
    public void Dump()
    {
        string? path = Environment.GetEnvironmentVariable("ONEDIFF_DUMP");
        if (path is null)
        {
            return;
        }

        CompareDocument d = Harness.Read(DocFactory.Build(Harness.Rich()));
        var sb = new StringBuilder();
        foreach (Node n in d.Nodes)
        {
            sb.Append(new string(' ', n.Depth * 2)).Append(n.Kind).Append(' ').Append(n.Text.Replace("\n", "\\n", StringComparison.Ordinal)).Append(" | ").Append(n.Attributes.ToString()).Append(" | ").Append(n.Anchor.Reference);
            sb.Append(" runs=").Append(n.Runs.Length).Append(" segs=").Append(n.Segments.Length).AppendLine();
        }

        foreach (OpaquePart o in d.Opaque)
        {
            sb.AppendLine("opaque " + o.Name);
        }

        File.WriteAllText(path, sb.ToString());
    }
}
