using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Word.Tests;

/// <summary>
/// Properties Word needs to open a file without offering to repair it. The accept/reject and schema gates subtract the errors that the
/// two inputs already have, so a fixture that is invalid in the same way as its output goes unseen; here nothing is subtracted.
/// </summary>
public class WordNeedsTests
{
    private static readonly CompareSettings[] Settings = [CompareSettings.Legal, CompareSettings.Strict];

    /// <summary>
    /// A picture without its non-visual properties and shape properties, or a text-box shape without its body properties, is a
    /// schema error that Word answers with a repair prompt, wherever the revisions are. Generated documents must not have it, and
    /// neither may the redline of any pair of them.
    /// </summary>
    [Fact]
    public void GeneratedDocumentsAndTheirRedlinesHaveNoSchemaErrorAtAll()
    {
        var source = new WordDocumentSource();
        var problems = new List<string>();
        for (int seed = 0; seed < 40; seed++)
        {
            var gen = new Gen(seed);
            DocSpec o = gen.Document(), n = gen.Edit(o);
            byte[] ob = DocFactory.Build(o), nb = DocFactory.Build(n);
            foreach (string e in SchemaCheck.Signatures(ob).Concat(SchemaCheck.Signatures(nb)))
            {
                problems.Add($"seed {seed} input: {e}");
            }

            CompareDocument od = source.Read(DocumentInput.FromBytes(ob, "o.docx")), nd = source.Read(DocumentInput.FromBytes(nb, "n.docx"));
            foreach (CompareSettings settings in Settings)
            {
                foreach (WordBuildOn on in new[] { WordBuildOn.New, WordBuildOn.Old })
                {
                    using var ms = new MemoryStream();
                    new DocxRedline { BuildOn = on }.Create(Comparison.Of(od, nd, settings), ms);
                    foreach (string e in SchemaCheck.Signatures(ms.ToArray()))
                    {
                        problems.Add($"seed {seed} output ({on}): {e}");
                    }
                }
            }
        }

        Assert.Empty(problems.Distinct().Take(20));
    }
}
