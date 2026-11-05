using System.Diagnostics;
using System.Globalization;
using OneDiff.Documents.Model;
using OneDiff.Pdf;

namespace OneDiff.Fuzz;

/// <summary>
/// The PDF source as a fuzz target: a mutation campaign that starts from valid PDFs (the files of a seed folder), flips, cuts,
/// splices and repeats bytes, and requires that every input is either read or refused with the documented
/// <see cref="InvalidDataException"/> (a typed <see cref="PdfSourceFailure"/>). Any other exception, or an input slower than three
/// seconds, is a crash. Usage: <c>OneDiff.Fuzz pdf-source --seeds DIR [--seconds N] [--seed N] [--crashes DIR]</c>.
/// </summary>
internal static class PdfSourceFuzz
{
    private static readonly string[] Numbers = ["2147483647", "-1", "99999999999", "4294967296", "0"];

    public static int Campaign(string[] args)
    {
        double seconds = 60;
        int seed = 1;
        string? crashDir = null, seedDir = null;
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
                case "--seeds":
                    seedDir = args[i + 1];
                    break;
            }
        }

        if (seedDir is null)
        {
            Console.Error.WriteLine("pdf-source needs --seeds DIR (a folder of valid PDFs)");
            return 2;
        }

        List<byte[]> corpus = [.. Directory.EnumerateFiles(seedDir, "*.pdf").Order(StringComparer.Ordinal).Select(File.ReadAllBytes)];
        if (corpus.Count == 0)
        {
            Console.Error.WriteLine("no .pdf files in " + seedDir);
            return 2;
        }

        var rng = new Random(seed);
        var source = new PdfDocumentSource();
        var clock = Stopwatch.StartNew();
        long executions = 0, read = 0, refused = 0, crashes = 0, slow = 0;
        var crashSigs = new HashSet<string>(StringComparer.Ordinal);
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            byte[] input = Mutate(corpus[rng.Next(corpus.Count)], rng, corpus);
            long t0 = Stopwatch.GetTimestamp();
            Exception? failure = null;
            try
            {
                _ = source.Read(DocumentInput.FromBytes(input, "x.pdf"));
                read++;
            }
            catch (InvalidDataException e) when (PdfSourceErrors.FailureOf(e) is not null)
            {
                refused++;
            }
            catch (Exception e)
            {
                failure = e;
            }

            if (Stopwatch.GetElapsedTime(t0).TotalSeconds > 3)
            {
                slow++;
                failure ??= new TimeoutException("input took more than 3 s");
            }

            if (failure is not null)
            {
                crashes++;
                string sig = failure.GetType().Name + "@" + (failure.StackTrace ?? "").Split('\n').FirstOrDefault();
                if (crashSigs.Add(sig) && crashDir is not null)
                {
                    Directory.CreateDirectory(crashDir);
                    File.WriteAllBytes(Path.Combine(crashDir, $"crash-{crashSigs.Count:D3}.pdf"), input);
                    File.WriteAllText(Path.Combine(crashDir, $"crash-{crashSigs.Count:D3}.txt"), failure.ToString());
                }
            }

            executions++;
        }

        Console.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"pdf-source: {executions} executions in {clock.Elapsed.TotalSeconds:F0}s, {read} read, {refused} refused, {crashes} crashes ({crashSigs.Count} distinct), {slow} slow, seeds {corpus.Count}"));
        return crashes == 0 ? 0 : 1;
    }

    private static byte[] Mutate(byte[] seedBytes, Random rng, List<byte[]> corpus)
    {
        byte[] b = (byte[])seedBytes.Clone();
        int rounds = 1 + rng.Next(4);
        for (int r = 0; r < rounds; r++)
        {
            switch (rng.Next(7))
            {
                case 0: // flip a few bytes
                    for (int k = 1 + rng.Next(8); k > 0 && b.Length > 0; k--)
                    {
                        b[rng.Next(b.Length)] = (byte)rng.Next(256);
                    }

                    break;
                case 1: // truncate
                    b = b.AsSpan(0, rng.Next(b.Length + 1)).ToArray();
                    break;
                case 2: // delete a chunk
                    if (b.Length > 2)
                    {
                        int at = rng.Next(b.Length), len = Math.Min(b.Length - at, 1 + rng.Next(400));
                        b = [.. b.AsSpan(0, at), .. b.AsSpan(at + len)];
                    }

                    break;
                case 3: // repeat a chunk
                    if (b.Length > 2 && b.Length < 1 << 20)
                    {
                        int at = rng.Next(b.Length), len = Math.Min(b.Length - at, 1 + rng.Next(400));
                        b = [.. b.AsSpan(0, at + len), .. b.AsSpan(at, len), .. b.AsSpan(at + len)];
                    }

                    break;
                case 4: // splice from another seed
                    {
                        byte[] o = corpus[rng.Next(corpus.Count)];
                        if (o.Length > 2 && b.Length > 2)
                        {
                            int from = rng.Next(o.Length), len = Math.Min(o.Length - from, 1 + rng.Next(2000)), at = rng.Next(b.Length);
                            b = [.. b.AsSpan(0, at), .. o.AsSpan(from, len), .. b.AsSpan(at)];
                        }
                    }

                    break;
                case 5: // change a digit (object numbers, lengths, offsets)
                    for (int k = 0; k < 20 && b.Length > 0; k++)
                    {
                        int at = rng.Next(b.Length);
                        if (b[at] is >= (byte)'0' and <= (byte)'9')
                        {
                            b[at] = (byte)('0' + rng.Next(10));
                            break;
                        }
                    }

                    break;
                default: // huge or negative numbers where a number sits
                    {
                        byte[] num = System.Text.Encoding.ASCII.GetBytes(Numbers[rng.Next(Numbers.Length)]);
                        int at = rng.Next(Math.Max(1, b.Length));
                        b = [.. b.AsSpan(0, Math.Min(at, b.Length)), .. num, .. b.AsSpan(Math.Min(at, b.Length))];
                    }

                    break;
            }
        }

        return b;
    }
}
