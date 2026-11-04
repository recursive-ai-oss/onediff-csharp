using System.Diagnostics;
using System.Globalization;
using System.Text;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;

namespace OneDiff.Fuzz;

/// <summary>
/// The comparison JSON reader as a fuzz target. <see cref="Run"/> is the one-input entry point (for an external coverage-guided
/// driver); <see cref="Campaign"/> is a self-contained mutation fuzzer that starts from valid comparisons, so the reader's deeper
/// checks are reached, and records executions, outcomes, the slowest input and every crash.
/// </summary>
internal static class ComparisonJsonFuzz
{
    private static readonly string[] Tokens =
    [
        "\"nodes\"", "\"parent\"", "\"kind\"", "\"links\"", "\"changes\"", "\"groups\"", "\"ledger\"", "\"edits\"", "\"runs\"", "\"quads\"",
        "\"anchor\"", "\"segments\"", "\"folds\"", "-1", "0", "2147483647", "-2147483648", "4294967296", "1e999", "1.7976931348623157e308",
        "null", "true", "[]", "{}", "[[[[[[[[", "\"\\ud800\"", "\"\\u001e\"", "\"textSpan\"", "\"pdfQuads\"", "\"ocrWords\"", "\"match\"", "\"split\"",
        "\"merge\"", "\"move\"", "\"replace\"", "\"insert\"", "\"delete\"",
    ];

    /// <summary>Feeds one input to the reader. Only <see cref="ComparisonFormatException"/> is an acceptable failure.</summary>
    public static void Run(ReadOnlySpan<byte> input)
    {
        try
        {
            Comparison c = ComparisonJson.Read(input);
            _ = c.Check();
            _ = ComparisonJson.Write(c);
        }
        catch (ComparisonFormatException)
        {
        }
    }

    public static int Campaign(string[] args)
    {
        double seconds = 60;
        int seed = 1;
        string? crashDir = null;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--seconds":
                    seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                    break;
                case "--crashes":
                    crashDir = args[i + 1];
                    break;
            }
        }

        List<byte[]> corpus = Seeds();
        var rng = new Random(seed);
        var clock = Stopwatch.StartNew();
        long executions = 0, accepted = 0, crashes = 0;
        double slowest = 0;
        var crashSigs = new HashSet<string>(StringComparer.Ordinal);
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            byte[] input = Mutate(corpus[rng.Next(corpus.Count)], rng, corpus);
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                ComparisonJson.Read(input).Check();
                accepted++;
                if (rng.Next(50) == 0 && corpus.Count < 2000)
                {
                    corpus.Add(input);
                }
            }
            catch (ComparisonFormatException)
            {
            }
            catch (Exception ex)
            {
                crashes++;
                string sig = ex.GetType().Name + "@" + (ex.StackTrace ?? "").Split('\n').FirstOrDefault();
                if (crashSigs.Add(sig) && crashDir is not null)
                {
                    Directory.CreateDirectory(crashDir);
                    File.WriteAllBytes(Path.Combine(crashDir, $"crash-{crashSigs.Count:D3}.json"), input);
                    File.WriteAllText(Path.Combine(crashDir, $"crash-{crashSigs.Count:D3}.txt"), ex.ToString());
                }
            }

            slowest = Math.Max(slowest, Stopwatch.GetElapsedTime(t0).TotalSeconds);
            executions++;
        }

        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"comparison-json: {executions} executions in {clock.Elapsed.TotalSeconds:F0}s, {accepted} accepted, {executions - accepted - crashes} rejected, {crashes} crashes ({crashSigs.Count} distinct), slowest input {slowest * 1000:F1} ms, corpus {corpus.Count}"));
        return crashes == 0 ? 0 : 1;
    }

    private static List<byte[]> Seeds()
    {
        var list = new List<byte[]>();
        string[][] pairs =
        [
            ["A first paragraph about delivery.\n\nA second one about payment terms.", "A first paragraph about delivery.\n\nA changed second one about payment terms.\n\nA third."],
            ["He said \u201Chi\u201D \u2014 twice.\n\nUnchanged.", "He said \"hi\" - twice.\n\nUnchanged."],
            ["One two three four five six.\n\nTail.", "One two three.\n\nFour five six.\n\nTail."],
            ["Moved A paragraph.\n\nMoved B paragraph.\n\nMoved C paragraph.", "Moved C paragraph.\n\nMoved A paragraph.\n\nMoved B paragraph."],
            ["", "Only new text."],
        ];
        foreach (string[] p in pairs)
        {
            list.Add(ComparisonJson.Write(Comparison.Of(CompareDocument.FromText(p[0], "old"), CompareDocument.FromText(p[1], "new"))));
        }

        // A document with PDF and OCR anchors, formatting runs and attributes.
        CompareDocument Rich(string text, double conf)
        {
            var b = new CompareDocumentBuilder().WithMetadata("r", "ab", "application/pdf");
            b.Begin(NodeKind.Section, new NodeContent { Anchor = Anchor.ForCell("S", "A1") });
            b.Add(NodeKind.Paragraph, new NodeContent
            {
                Text = text,
                Runs = [new FormatRun(0, 3, FormatSet.Of(FormatKeys.Bold, "1"))],
                Attributes = FormatSet.Of(FormatKeys.ParagraphStyle, "Normal"),
                Anchor = Anchor.ForPdfQuads([new Quad(0, 1, 2, 3, 2, 3, 4, 1, 4)]),
                Segments = [new AnchoredSpan(0, text.Length, Anchor.ForOcrWords([new Quad(0, 1, 2, 3, 2, 3, 4, 1, 4)], conf))],
            });
            b.End();
            return b.Build();
        }

        list.Add(ComparisonJson.Write(Comparison.Of(Rich("pay within thirty days", 0.9), Rich("pay within forty days", 0.5))));
        return list;
    }

    private static byte[] Mutate(byte[] seed, Random rng, List<byte[]> corpus)
    {
        var b = new List<byte>(seed);
        for (int n = rng.Next(1, 5); n > 0; n--)
        {
            switch (rng.Next(9))
            {
                case 0:
                    b[rng.Next(b.Count)] ^= (byte)(1 << rng.Next(8));
                    break;
                case 1:
                    b[rng.Next(b.Count)] = (byte)rng.Next(256);
                    break;
                case 2:
                    b.RemoveRange(rng.Next(b.Count), Math.Min(rng.Next(1, 64), b.Count - 1 > 0 ? 1 : 0));
                    break;
                case 3:
                {
                    int at = rng.Next(b.Count), len = Math.Min(rng.Next(1, 200), b.Count - at);
                    b.RemoveRange(at, len);
                    break;
                }

                case 4:
                {
                    int at = rng.Next(b.Count), len = Math.Min(rng.Next(1, 200), b.Count - at);
                    b.InsertRange(rng.Next(b.Count), b.GetRange(at, len));
                    break;
                }

                case 5:
                    b.InsertRange(rng.Next(b.Count), Encoding.UTF8.GetBytes(Tokens[rng.Next(Tokens.Length)]));
                    break;
                case 6:
                {
                    // Replace a number or string-ish token with a dictionary entry: find a digit run.
                    int at = rng.Next(b.Count);
                    for (int k = 0; k < b.Count; k++)
                    {
                        int p = (at + k) % b.Count;
                        if (b[p] is >= (byte)'0' and <= (byte)'9')
                        {
                            int end = p;
                            while (end < b.Count && b[end] is >= (byte)'0' and <= (byte)'9')
                            {
                                end++;
                            }

                            b.RemoveRange(p, end - p);
                            b.InsertRange(p, Encoding.UTF8.GetBytes(Tokens[rng.Next(Tokens.Length)]));
                            break;
                        }
                    }

                    break;
                }

                case 7:
                    b.RemoveRange(rng.Next(b.Count), b.Count - 1 > 0 ? Math.Min(b.Count - 1, 1) : 0);
                    b.AddRange(corpus[rng.Next(corpus.Count)].Take(rng.Next(0, 300)));
                    break;
                default:
                    b.RemoveRange(rng.Next(b.Count), 0);
                    b.InsertRange(rng.Next(b.Count), new byte[rng.Next(1, 8)].Select(_ => (byte)rng.Next(128)));
                    break;
            }

            if (b.Count == 0)
            {
                b.Add((byte)'{');
            }
        }

        return [.. b];
    }
}
