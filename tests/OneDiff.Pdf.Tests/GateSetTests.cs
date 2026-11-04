using OneDiff.Documents;
using OnePdf;

namespace OneDiff.Pdf.Tests;

/// <summary>
/// A seeded set of generated pairs: random edits (word replacements, inserted and deleted sentences and paragraphs, moves, splits,
/// merges, number changes) over documents laid out in different ways. Every pair must verify without falling back, and its marks must
/// read back from the marked PDF exactly.
/// </summary>
public class GateSetTests
{
    private static readonly string[] Subjects = ["The supplier", "The customer", "Either party", "The licensee", "The company", "Each director", "The guarantor", "The borrower"];
    private static readonly string[] Verbs = ["shall deliver", "may terminate", "must disclose", "will indemnify", "agrees to review", "is required to notify", "undertakes to maintain", "shall not assign"];
    private static readonly string[] Objects = ["the goods listed in the schedule", "this agreement upon written notice", "all confidential information received", "the other party against any loss", "the accounts of the business each quarter", "the relevant authority without delay", "adequate insurance throughout the term", "its rights without prior consent"];
    private static readonly string[] Tails = ["within thirty days", "within sixty days", "at its own expense", "in accordance with applicable law", "subject to clause nine", "during normal business hours", "for a period of two years", "to the extent permitted"];

    private static string Sentence(Random r) =>
        $"{Subjects[r.Next(Subjects.Length)]} {Verbs[r.Next(Verbs.Length)]} {Objects[r.Next(Objects.Length)]} {Tails[r.Next(Tails.Length)]}.";

    private static string Paragraph(Random r) => string.Join(' ', Enumerable.Range(0, 1 + r.Next(4)).Select(_ => Sentence(r)));

    private static List<string> Edit(Random r, List<string> doc)
    {
        var d = new List<string>(doc);
        int edits = 1 + r.Next(4);
        for (int e = 0; e < edits; e++)
        {
            int i = r.Next(d.Count);
            switch (r.Next(8))
            {
                case 0: // replace a word
                {
                    string[] words = d[i].Split(' ');
                    int w = r.Next(words.Length);
                    words[w] = Tails[r.Next(Tails.Length)].Split(' ')[^1];
                    d[i] = string.Join(' ', words);
                    break;
                }

                case 1: // insert a sentence
                    d[i] = d[i] + " " + Sentence(r);
                    break;
                case 2: // delete a paragraph
                    if (d.Count > 3)
                    {
                        d.RemoveAt(i);
                    }

                    break;
                case 3: // insert a paragraph
                    d.Insert(i, Paragraph(r));
                    break;
                case 4: // move a paragraph
                {
                    string p = d[i];
                    d.RemoveAt(i);
                    d.Insert(r.Next(d.Count + 1), p);
                    break;
                }

                case 5: // split after the first sentence
                {
                    int cut = d[i].IndexOf(". ", StringComparison.Ordinal);
                    if (cut > 0)
                    {
                        d[i] = d[i][..(cut + 1)];
                        d.Insert(i + 1, doc[Math.Min(i, doc.Count - 1)][(cut + 2)..]);
                    }

                    break;
                }

                case 6: // merge with the next paragraph
                    if (i + 1 < d.Count)
                    {
                        d[i] = d[i] + " " + d[i + 1];
                        d.RemoveAt(i + 1);
                    }

                    break;
                default: // change a number
                    d[i] = d[i].Replace("thirty", "forty", StringComparison.Ordinal).Replace("sixty", "ninety", StringComparison.Ordinal);
                    break;
            }
        }

        return d;
    }

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(0, 48)];

    private static Run Pair(int seed)
    {
        var r = new Random(seed);
        List<string> before = [.. Enumerable.Range(0, 6 + r.Next(10)).Select(_ => Paragraph(r))];
        List<string> after = Edit(r, before);
        var spec = (seed % 6) switch
        {
            0 => new Fixtures.Spec(),
            1 => new Fixtures.Spec { Columns = 2, PageHeight = 420 },
            2 => new Fixtures.Spec { Header = "Services Agreement", PageNumbers = true, PageHeight = 360 },
            3 => new Fixtures.Spec { Justify = true, Hyphenate = true, PageWidth = 340 },
            4 => new Fixtures.Spec { PageHeight = 300, Size = 10 },
            _ => new Fixtures.Spec { Header = "Schedule 3", PageNumbers = true, Columns = 2, PageHeight = 460 },
        };

        return Run.Of(Fixtures.Make(spec with { Paragraphs = [.. before] }), Fixtures.Make(spec with { Paragraphs = [.. after] }));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeneratedPairVerifiesAndItsMarksReadBack(int seed)
    {
        Run run = Pair(seed);
        run.AssertComplete();
        run.AssertReextraction();
        Assert.Equal(0, run.Comparison.Verification.FallbackLevel);
    }

    [Fact]
    public void TheSetCoversEveryKindOfChangeAndMark()
    {
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var marks = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int pairs = 0, fallbacks = 0, skipped = 0;
        foreach (int seed in Enumerable.Range(0, 48))
        {
            Run run = Pair(seed);
            pairs++;
            fallbacks += run.Comparison.Verification.FallbackLevel;
            skipped += run.Result.Skipped.Length;
            foreach (Change c in run.Comparison.Changes)
            {
                kinds[c.Kind.ToString()] = kinds.GetValueOrDefault(c.Kind.ToString()) + 1;
            }

            foreach (PdfMark m in run.Result.Marks)
            {
                marks[m.Kind.ToString()] = marks.GetValueOrDefault(m.Kind.ToString()) + 1;
            }
        }

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "gate-summary.txt"),
            $"pairs={pairs} fallbacks={fallbacks} skipped={skipped}\nchanges: {string.Join(", ", kinds.Select(k => k.Key + "=" + k.Value))}\nmarks: {string.Join(", ", marks.Select(k => k.Key + "=" + k.Value))}\n");
        Assert.Equal(0, fallbacks);
        foreach (string k in new[] { "Insert", "Delete", "Replace", "Move" })
        {
            Assert.True(kinds.ContainsKey(k), $"no {k} change in the generated set");
        }

        Assert.True(marks.ContainsKey("Highlight") && marks.ContainsKey("Caret"));
    }

    [Fact]
    public void LayoutOnlyDifferencesNeverBecomeChanges()
    {
        var r = new Random(99);
        string[] doc = [.. Enumerable.Range(0, 12).Select(_ => Paragraph(r))];
        byte[] a = Fixtures.Make(new Fixtures.Spec { Paragraphs = doc, PageWidth = 595 });
        byte[] b = Fixtures.Make(new Fixtures.Spec { Paragraphs = doc, PageWidth = 380, Justify = true, Hyphenate = true, PageHeight = 500 });
        Run run = Run.Of(a, b);
        run.AssertComplete();
        Assert.Empty(run.Comparison.Changes);
    }
}
