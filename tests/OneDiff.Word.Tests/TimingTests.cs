using System.Diagnostics;
using System.Globalization;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;

namespace OneDiff.Word.Tests;

public class TimingTests
{
    /// <summary>A synthetic contract of about <paramref name="pages"/> pages (ten paragraphs of five sentences a page, a table and a list every few pages).</summary>
    internal static (DocSpec Old, DocSpec New) Contract(int pages, int changes, int seed)
    {
        var gen = new Gen(seed);
        var blocks = new List<Blk>();
        for (int p = 0; p < pages; p++)
        {
            for (int i = 0; i < 10; i++)
            {
                var runs = new List<Inl>();
                for (int s = 0; s < 5; s++)
                {
                    runs.Add(new T(gen.Sentence() + " ") { Bold = s == 0 && i == 0 });
                }

                blocks.Add(new Para([.. runs]) { List = i % 4 == 1 ? 0 : -1, Style = i == 0 ? "Heading1" : null });
            }

            if (p % 5 == 2)
            {
                blocks.Add(new Tbl([new Cell(Para.Of(gen.Sentence())), new Cell(Para.Of("1000"))], [new Cell(Para.Of(gen.Sentence())), new Cell(Para.Of("2000"))]));
            }
        }

        var old = new DocSpec([.. blocks]) { Lists = 1, Header = "Header", Footer = "Footer" };
        var edited = blocks.ToList();
        var rng = new Random(seed);
        for (int c = 0; c < changes; c++)
        {
            int at = rng.Next(edited.Count);
            if (edited[at] is Para para && para.Inlines.Length > 1 && para.Inlines[1] is T t)
            {
                Inl[] copy = (Inl[])para.Inlines.Clone();
                string[] words = t.Text.Split(' ');
                words[rng.Next(words.Length)] = "amended" + c.ToString(CultureInfo.InvariantCulture);
                copy[1] = t with { Text = string.Join(' ', words) };
                edited[at] = para with { Inlines = copy };
            }
        }

        return (old, old with { Blocks = [.. edited] });
    }

    [Fact]
    public void ReadAndCompareAHundredPages()
    {
        if (Environment.GetEnvironmentVariable("ONEDIFF_WORD_TIMING") is null)
        {
            return;
        }

        (DocSpec o, DocSpec n) = Contract(100, 300, 7);
        byte[] oldBytes = DocFactory.Build(o), newBytes = DocFactory.Build(n);
        var source = new WordDocumentSource();
        var results = new List<string>();
        for (int rep = 0; rep < 4; rep++)
        {
            var sw = Stopwatch.StartNew();
            CompareDocument a = source.Read(DocumentInput.FromBytes(oldBytes, "a.docx"));
            CompareDocument b = source.Read(DocumentInput.FromBytes(newBytes, "b.docx"));
            double read = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            Comparison c = Comparison.Of(a, b);
            double compare = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            byte[] json = ComparisonJson.Write(c);
            double write = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            using var tracked = new MemoryStream();
            new DocxRedline().Create(c, tracked);
            double track = sw.Elapsed.TotalMilliseconds;
            results.Add(string.Create(CultureInfo.InvariantCulture, $"rep {rep}: nodes={a.Count}/{b.Count} changes={c.Changes.Length} read(2)={read:F0}ms compare={compare:F0}ms json={write:F0}ms ({json.Length} bytes) tracked-docx={track:F0}ms ({tracked.Length} bytes) passed={c.Verification.Passed} fallback={c.Verification.FallbackLevel}"));
        }

        Console.WriteLine(string.Join('\n', results));
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "word-timing.txt"), results);
    }
}
