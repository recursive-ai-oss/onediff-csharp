using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;
using OneDiff.Documents.Verification;
using OneDiff.Ocr;
using OneDiff.Pdf;
using OneImage;
using OneImage.Pdf;
using OnePdf;

namespace OneDiff.GoldSets.Scan;

internal sealed record Degradation(double Dpi, double Sigma, double SkewDegrees, double NoiseDensity, string Codec, int Quality);

internal sealed record FileEntry(string Path, string Sha256);

internal sealed record ScanManifest(
    string Schema, string Source, string SourceSetSha256, string PdfSetSha256, ulong Seed, int Pairs, int Pdfs, Dictionary<string, Degradation> Degradations, List<FileEntry> Files);

/// <summary>
/// The scanned-page gold set and its gates. <c>build</c> takes rendered pairs of a labelled set, draws every page to pixels, degrades
/// the pixels with seeded noise (blur, skew within 2 degrees, salt and pepper, JPEG compression, 200 or 300 dpi) and writes image-only
/// PDFs. <c>compare</c> reads scans (or the digital originals, for the side-by-side rows) and writes the comparisons the scorer maps
/// onto the labels. <c>gate</c> runs the exact checks.
/// </summary>
internal static class Program
{
    private const string Usage = """
        OneDiff.GoldSets.Scan build --pdf DIR --data DIR --out DIR [--langs en,de,fr,es] [--seed N] [--jobs N] [--limit N]
        OneDiff.GoldSets.Scan manifest --pdf DIR --data DIR --out DIR
        OneDiff.GoldSets.Scan compare --set DIR --out DIR [--source ocr|pdf] [--settings legal|strict] [--ids FILE] [--from N --count N] [--jobs N]
        OneDiff.GoldSets.Scan gate --set DIR --out FILE [--ids FILE] [--from N --count N | --every N] [--determinism-every N] [--digest FILE] [--jobs N]

          build     for each pair of the labelled set DATA whose id starts with one of the languages, reads PDF/<id>.{old,new}.pdf (the rendered
                    pair), draws the pages, degrades them and writes OUT/pdf/<id>.{old,new}.pdf with the pair's block map beside them
          manifest  hashes everything under OUT/pdf and writes OUT/manifest.json and manifest.sha256
          compare   reads SET/pdf/<id>.{old,new}.pdf with the OCR source (or the PDF source, on the digital originals) and writes OUT/<id>.cmp.json
          gate      Legal and Strict comparisons of each pair: Verify passes with no fallback, the change list re-checks, the marks on the
                    new and old pages lie over the changed words, and (on a sample) a second run gives identical bytes
        """;

    private static readonly JsonSerializerOptions ManifestOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static readonly string[] Sides = ["old", "new"];

    private static readonly string[] DefaultLangs = ["en", "de", "fr", "es"];

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var opt = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i++)
        {
            opt[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        try
        {
            return args[0] switch
            {
                "build" => Build(opt),
                "manifest" => WriteManifest(opt),
                "compare" => Compare(opt),
                "gate" => Gate(opt),
                "dump" => Dump(opt),
                _ => Fail("unknown command " + args[0]),
            };
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidDataException or JsonException or KeyNotFoundException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static string Req(Dictionary<string, string> opt, string key) => opt.GetValueOrDefault(key) ?? throw new ArgumentException("--" + key + " is required");

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string[] PairIds(string dataDir, string[] langs) =>
        [.. Directory.EnumerateFiles(Path.Combine(dataDir, "pairs"), "*.labels.json")
            .Select(p => Path.GetFileName(p)[..^".labels.json".Length])
            .Where(id => langs.Any(l => id.StartsWith(l + "-", StringComparison.Ordinal)))
            .OrderBy(x => x, StringComparer.Ordinal)];

    // ---- build ----

    /// <summary>A seeded generator (SplitMix64), so the degradation of a file depends on its name and the seed alone.</summary>
    private sealed class Rng(ulong seed)
    {
        private ulong state = seed;

        public ulong Next()
        {
            ulong z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public double Uniform() => (Next() >> 11) * (1.0 / (1UL << 53));

        public int Below(int n) => (int)(Next() % (ulong)n);
    }

    internal static Degradation Choose(string key, ulong seed)
    {
        var rng = new Rng(seed ^ BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0));
        double dpi = rng.Below(2) == 0 ? 200 : 300;
        double sigma = new[] { 0.0, 0.0, 0.7, 1.2 }[rng.Below(4)] * dpi / 300;
        double skew = Math.Round((rng.Uniform() * 4) - 2, 2);
        double noise = new[] { 0.0, 0.0, 0.002, 0.006 }[rng.Below(4)];
        int c = rng.Below(5);
        string codec = c < 2 ? "png" : "jpeg";
        int quality = c switch { 2 => 90, 3 => 75, 4 => 55, _ => 0 };
        return new Degradation(dpi, sigma, skew, noise, codec, quality);
    }

    private static byte[] Degrade(byte[] pdf, Degradation d, ulong seed, string key)
    {
        var load = new LoadOptions { Configuration = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = d.Dpi })) };
        Image doc = Image.Load(pdf, load);
        var rng = new Rng(seed ^ 0xA5A5A5A5UL ^ BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(key + "#noise")), 8));
        using PdfDocument outDoc = PdfDocument.CreateEmpty();
        for (int p = 0; p < doc.Frames.Count; p++)
        {
            Image page = doc.Frames[p].ToImage();
            if (d.Sigma > 0)
            {
                page = page.GaussianBlur(d.Sigma);
            }

            if (d.SkewDegrees != 0)
            {
                page = page.Rotate(d.SkewDegrees, expand: false, background: new Rgba32(255, 255, 255, 255));
            }

            int w = page.Frames[0].Width, h = page.Frames[0].Height;
            if (d.NoiseDensity > 0)
            {
                Rgba32[] px = page.Frames[0].ToArray<Rgba32>();
                for (int i = 0; i < px.Length; i++)
                {
                    if (rng.Uniform() < d.NoiseDensity)
                    {
                        byte v = rng.Below(2) == 0 ? (byte)0 : (byte)255;
                        px[i] = new Rgba32(v, v, v, 255);
                    }
                }

                page = Image.FromPixels<Rgba32>(px, w, h);
            }

            using var ms = new MemoryStream();
            if (d.Codec == "png")
            {
                page.Save(ms, PngFormat.Instance);
            }
            else
            {
                page.Save(ms, JpegFormat.Instance, new SaveOptions { EncoderOptions = new JpegEncoderOptions { Quality = d.Quality } });
            }

            PdfCreatedImage image = d.Codec == "png" ? outDoc.AddPngImage(ms.ToArray()) : outDoc.AddJpegImage(ms.ToArray());
            double wp = w * 72.0 / d.Dpi, hp = h * 72.0 / d.Dpi;
            PdfPage outPage = outDoc.InsertBlankPage(p, wp, hp);
            using PdfPageContentBuilder b = outPage.BeginContent();
            b.DrawImage(image, 0, 0, wp, hp);
            b.Commit();
        }

        using var output = new MemoryStream();
        outDoc.Save(output, new PdfSaveOptions { DocumentId = SHA256.HashData(Encoding.UTF8.GetBytes(key))[..16] });
        return output.ToArray();
    }

    private static int Build(Dictionary<string, string> opt)
    {
        string pdfDir = Path.GetFullPath(Req(opt, "pdf")), data = Path.GetFullPath(Req(opt, "data")), outDir = Path.GetFullPath(Req(opt, "out"));
        string[] langs = opt.TryGetValue("langs", out string? l) ? l.Split(',') : DefaultLangs;
        ulong seed = ulong.Parse(opt.GetValueOrDefault("seed") ?? "20261104", CultureInfo.InvariantCulture);
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        int limit = int.Parse(opt.GetValueOrDefault("limit") ?? "0", CultureInfo.InvariantCulture);
        string[] ids = PairIds(data, langs);
        if (limit > 0)
        {
            ids = [.. ids.Take(limit)];
        }

        Directory.CreateDirectory(Path.Combine(outDir, "pdf"));
        int done = 0;
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = jobs }, id =>
        {
            foreach (string side in Sides)
            {
                string target = Path.Combine(outDir, "pdf", $"{id}.{side}.pdf");
                if (File.Exists(target))
                {
                    continue;
                }

                string key = $"{id}.{side}";
                Degradation chosen = opt.ContainsKey("clean") ? new Degradation(300, 0, 0, 0, "png", 0) : Choose(key, seed);
                byte[] scan = Degrade(File.ReadAllBytes(Path.Combine(pdfDir, "pdf", $"{id}.{side}.pdf")), chosen, seed, key);
                File.WriteAllBytes(target + ".tmp", scan);
                File.Move(target + ".tmp", target);
            }

            File.Copy(Path.Combine(pdfDir, "pdf", id + ".blocks.json"), Path.Combine(outDir, "pdf", id + ".blocks.json"), overwrite: true);
            Interlocked.Increment(ref done);
        });
        Console.WriteLine($"built {done} pairs of {ids.Length}");
        return 0;
    }

    private static int WriteManifest(Dictionary<string, string> opt)
    {
        string pdfDir = Path.GetFullPath(Req(opt, "pdf")), data = Path.GetFullPath(Req(opt, "data")), outDir = Path.GetFullPath(Req(opt, "out"));
        ulong seed = ulong.Parse(opt.GetValueOrDefault("seed") ?? "20261104", CultureInfo.InvariantCulture);
        string[] ids = [.. Directory.EnumerateFiles(Path.Combine(outDir, "pdf"), "*.old.pdf").Select(p => Path.GetFileName(p)[..^".old.pdf".Length]).OrderBy(x => x, StringComparer.Ordinal)];
        var files = new List<FileEntry>();
        var degradations = new Dictionary<string, Degradation>();
        foreach (string id in ids)
        {
            foreach (string name in (string[])[$"{id}.old.pdf", $"{id}.new.pdf", $"{id}.blocks.json"])
            {
                files.Add(new FileEntry("pdf/" + name, Hex(File.ReadAllBytes(Path.Combine(outDir, "pdf", name)))));
            }

            degradations[id + ".old"] = Choose(id + ".old", seed);
            degradations[id + ".new"] = Choose(id + ".new", seed);
        }

        string dataHash = File.ReadAllText(Path.Combine(data, "manifest.sha256")).Trim();
        string pdfHash = File.ReadAllText(Path.Combine(pdfDir, "manifest.sha256")).Trim();
        var manifest = new ScanManifest("onediff.scangoldset/1", "synth-v1 pairs, Latin-script languages", dataHash, pdfHash, seed, ids.Length, ids.Length * 2, degradations, files);
        byte[] mb = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestOptions);
        File.WriteAllBytes(Path.Combine(outDir, "manifest.json"), mb);
        File.WriteAllText(Path.Combine(outDir, "manifest.sha256"), Hex(mb) + "\n");
        Console.WriteLine($"pairs={ids.Length} set-sha256={Hex(mb)}");
        return 0;
    }

    // ---- reading ----

    private static string[] Ids(Dictionary<string, string> opt, string dir)
    {
        string[] ids = [.. Directory.EnumerateFiles(dir, "*.old.pdf").Select(p => Path.GetFileName(p)[..^".old.pdf".Length]).OrderBy(x => x, StringComparer.Ordinal)];
        if (opt.TryGetValue("ids", out string? file))
        {
            var want = new HashSet<string>(File.ReadAllLines(file), StringComparer.Ordinal);
            ids = [.. ids.Where(want.Contains)];
        }

        if (opt.TryGetValue("every", out string? every))
        {
            int n = int.Parse(every, CultureInfo.InvariantCulture);
            ids = [.. ids.Where((_, i) => i % n == 0)];
        }

        int from = int.Parse(opt.GetValueOrDefault("from") ?? "0", CultureInfo.InvariantCulture);
        int count = int.Parse(opt.GetValueOrDefault("count") ?? "0", CultureInfo.InvariantCulture);
        ids = [.. ids.Skip(from)];
        return count > 0 ? [.. ids.Take(count)] : ids;
    }

    private static IDocumentSource SourceOf(string name) => name switch
    {
        "pdf" => new PdfDocumentSource(),
        "ocr-tables" => new OcrDocumentSource(new OcrSourceOptions { ReadTables = true }),
        "ocr-flat" => new OcrDocumentSource(new OcrSourceOptions { ReadListItems = false }),
        _ => new OcrDocumentSource(),
    };

    private static int Compare(Dictionary<string, string> opt)
    {
        string set = Path.GetFullPath(Req(opt, "set")), outDir = Path.GetFullPath(Req(opt, "out"));
        string dir = Directory.Exists(Path.Combine(set, "pdf")) ? Path.Combine(set, "pdf") : set;
        string source = opt.GetValueOrDefault("source") ?? "ocr";
        CompareSettings settings = opt.GetValueOrDefault("settings") == "strict" ? CompareSettings.Strict : CompareSettings.Legal;
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        string[] ids = Ids(opt, dir);
        Directory.CreateDirectory(outDir);
        int failed = 0;
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = jobs }, id =>
        {
            try
            {
                IDocumentSource src = SourceOf(source);
                CompareDocument o = src.Read(DocumentInput.FromBytes(File.ReadAllBytes(Path.Combine(dir, id + ".old.pdf")), id + ".old.pdf"));
                CompareDocument n = src.Read(DocumentInput.FromBytes(File.ReadAllBytes(Path.Combine(dir, id + ".new.pdf")), id + ".new.pdf"));
                File.WriteAllBytes(Path.Combine(outDir, id + ".cmp.json"), ComparisonJson.Write(Comparison.Of(o, n, settings)));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Interlocked.Increment(ref failed);
                Console.Error.WriteLine($"{id}: {e.GetType().Name}: {e.Message}");
            }
        });
        Console.WriteLine($"compared {ids.Length} pairs with the {source} source, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Prints the nodes one side of a pair reads as, with each node's lowest word confidence, beside the labelled block texts (for diagnosis).</summary>
    private static int Dump(Dictionary<string, string> opt)
    {
        string set = Path.GetFullPath(Req(opt, "set"));
        string dir = Directory.Exists(Path.Combine(set, "pdf")) ? Path.Combine(set, "pdf") : set;
        string id = Req(opt, "id"), side = opt.GetValueOrDefault("side") ?? "old";
        CompareDocument d = SourceOf(opt.GetValueOrDefault("source") ?? "ocr").Read(DocumentInput.FromBytes(File.ReadAllBytes(Path.Combine(dir, $"{id}.{side}.pdf")), id));
        foreach (Node n in d.Nodes.Where(static n => n.Kind is NodeKind.Paragraph or NodeKind.ListItem or NodeKind.Header or NodeKind.Footer))
        {
            Console.WriteLine($"{n.Id} {n.Kind} conf={(n.Anchor.Confidence is double c ? c.ToString("0.00", CultureInfo.InvariantCulture) : "-")} [{n.Text}]");
        }

        Console.WriteLine("---- gold blocks");
        using JsonDocument j = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, id + ".blocks.json")));
        foreach (JsonElement b in j.RootElement.GetProperty(side).GetProperty("blocks").EnumerateArray())
        {
            Console.WriteLine($"{b.GetProperty("node").GetInt32()} {b.GetProperty("kind").GetString()} p{b.GetProperty("page").GetInt32()} [{b.GetProperty("label").GetString()}{b.GetProperty("text").GetString()}]");
        }

        return 0;
    }

    // ---- gate ----

    private static int Gate(Dictionary<string, string> opt)
    {
        string set = Path.GetFullPath(Req(opt, "set")), outFile = Path.GetFullPath(Req(opt, "out"));
        string dir = Directory.Exists(Path.Combine(set, "pdf")) ? Path.Combine(set, "pdf") : set;
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        int detEvery = int.Parse(opt.GetValueOrDefault("determinism-every") ?? "0", CultureInfo.InvariantCulture);
        string[] ids = Ids(opt, dir);
        var failures = new ConcurrentBag<string>();
        var digests = new ConcurrentDictionary<string, string>();
        long changes = 0, flagged = 0, marks = 0, pairs = 0, determinism = 0;
        Parallel.For(0, ids.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            string id = ids[i];
            try
            {
                byte[] oldPdf = File.ReadAllBytes(Path.Combine(dir, id + ".old.pdf")), newPdf = File.ReadAllBytes(Path.Combine(dir, id + ".new.pdf"));
                GateResult r = CheckPair(id, oldPdf, newPdf, failures);
                Interlocked.Add(ref changes, r.Changes);
                Interlocked.Add(ref flagged, r.Flagged);
                Interlocked.Add(ref marks, r.Marks);
                Interlocked.Increment(ref pairs);
                digests[id] = r.Digest;
                if (detEvery > 0 && i % detEvery == 0)
                {
                    GateResult again = CheckPair(id, oldPdf, newPdf, failures);
                    Interlocked.Increment(ref determinism);
                    if (again.Digest != r.Digest)
                    {
                        failures.Add($"{id}: determinism: two runs gave different bytes");
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failures.Add($"{id}: {e.GetType().Name}: {e.Message}");
            }
        });
        File.WriteAllLines(outFile + ".failures", failures.Order(StringComparer.Ordinal));
        if (opt.TryGetValue("digest", out string? digestFile))
        {
            File.WriteAllLines(digestFile, digests.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}"));
        }

        string summary = string.Create(CultureInfo.InvariantCulture, $"pairs={ids.Length} ran={pairs} changes(legal+strict)={changes} flagged={flagged} marks={marks} determinism-rechecked={determinism} failures={failures.Count}");
        File.WriteAllText(outFile, summary + "\n");
        Console.WriteLine(summary);
        return failures.IsEmpty ? 0 : 1;
    }

    private sealed record GateResult(long Changes, long Flagged, long Marks, string Digest);

    private static GateResult CheckPair(string id, byte[] oldPdf, byte[] newPdf, ConcurrentBag<string> failures)
    {
        var source = new OcrDocumentSource();
        CompareDocument o = source.Read(DocumentInput.FromBytes(oldPdf, id + ".old.pdf"));
        CompareDocument n = source.Read(DocumentInput.FromBytes(newPdf, id + ".new.pdf"));
        long changes = 0, flagged = 0, marks = 0;
        var digest = new StringBuilder();
        foreach (CompareSettings settings in new[] { CompareSettings.Legal, CompareSettings.Strict })
        {
            string tag = $"{id} {settings.Name}";
            Comparison c;
            try
            {
                c = Comparison.Of(o, n, settings);
            }
            catch (ComparisonVerificationException e)
            {
                failures.Add($"{tag}: verification threw: {e.Message}");
                continue;
            }

            if (!c.Verification.Passed || c.Verification.FallbackLevel != 0)
            {
                failures.Add($"{tag}: verify passed={c.Verification.Passed} fallback={c.Verification.FallbackLevel}");
            }

            foreach (VerificationFailure problem in c.Check().Take(3))
            {
                failures.Add($"{tag}: check: {problem.Code} {problem.Message}");
            }

            changes += c.Changes.Length;
            flagged += c.Changes.Count(static x => x.PossibleOcrError);
            if (c.Changes.Any(static x => x.PossibleOcrError && x.OcrConfidence is null))
            {
                failures.Add($"{tag}: a change is flagged without a confidence");
            }

            // The JSON carries the flag and the confidence: read it back and compare.
            byte[] json = ComparisonJson.Write(c);
            Comparison back = ComparisonJson.Read(json);
            if (!back.Changes.Select(static x => (x.PossibleOcrError, x.OcrConfidence)).SequenceEqual(c.Changes.Select(static x => (x.PossibleOcrError, x.OcrConfidence))))
            {
                failures.Add($"{tag}: the JSON does not carry the OCR flags back");
            }

            digest.Append(Hex(json));
            foreach ((bool isNew, byte[] pdf) in new[] { (true, newPdf), (false, oldPdf) })
            {
                using var ms = new MemoryStream();
                PdfMarkupResult result = isNew ? PdfRedline.OnPages.MarkNewDocument(c, pdf, ms) : PdfRedline.OnPages.MarkOldDocument(c, pdf, ms);
                byte[] marked = ms.ToArray();
                digest.Append(Hex(marked));
                marks += result.Marks.Length;
                foreach (string problem in MarkProblems(c, isNew ? c.New : c.Old, isNew, marked, result))
                {
                    failures.Add($"{tag}: {(isNew ? "new" : "old")} marks: {problem}");
                }
            }
        }

        return new GateResult(changes, flagged, marks, Hex(Encoding.ASCII.GetBytes(digest.ToString())));
    }

    /// <summary>
    /// Reads the marked pages back and checks that what lies under each mark is the changed text: the recognized tokens whose centres
    /// are inside the annotation's quadrilaterals, in order, spell the side of the change that is marked.
    /// </summary>
    private static IEnumerable<string> MarkProblems(Comparison c, CompareDocument doc, bool isNew, byte[] marked, PdfMarkupResult result)
    {
        foreach (PdfSkippedChange s in result.Skipped)
        {
            if (!s.Reason.Contains("container", StringComparison.Ordinal))
            {
                yield return $"change {s.ChangeId} was not marked: {s.Reason}";
            }
        }

        using PdfDocument pdf = PdfDocument.Open(marked);
        var rectsByChange = new Dictionary<int, List<(int Page, double L, double B, double R, double T)>>();
        for (int p = 0; p < pdf.PageCount; p++)
        {
            PdfPage page = pdf.Pages[p];
            PdfRectangle box = page.CropBox.Intersect(page.MediaBox) ?? page.MediaBox;
            foreach (PdfAnnotation a in page.Annotations)
            {
                if (a.Subtype is "Popup" or "Link" || a.Name is null || !a.Name.StartsWith("onediff-c", StringComparison.Ordinal))
                {
                    continue;
                }

                int change = int.Parse(a.Name["onediff-c".Length..a.Name.IndexOf("-p", StringComparison.Ordinal)], CultureInfo.InvariantCulture);
                IReadOnlyList<double> qp = a.QuadPoints;
                if (!rectsByChange.TryGetValue(change, out var list))
                {
                    rectsByChange[change] = list = [];
                }

                for (int i = 0; i + 7 < qp.Count; i += 8)
                {
                    // Unrotated pages: the user space origin is the crop box corner.
                    double[] xs = [qp[i] - box.Left, qp[i + 2] - box.Left, qp[i + 4] - box.Left, qp[i + 6] - box.Left];
                    double[] ys = [qp[i + 1] - box.Bottom, qp[i + 3] - box.Bottom, qp[i + 5] - box.Bottom, qp[i + 7] - box.Bottom];
                    list.Add((p, xs.Min(), ys.Min(), xs.Max(), ys.Max()));
                }
            }
        }

        foreach (PdfMark m in result.Marks)
        {
            if (m.Kind == PdfMarkupKind.Caret || m.QuadCount == 0)
            {
                continue;
            }

            Change ch = c.Changes.First(x => x.Id == m.ChangeId);
            string expected = Squash((isNew ? ch.New : ch.Old).Text);
            if (!rectsByChange.TryGetValue(m.ChangeId, out var rects))
            {
                yield return $"change {m.ChangeId} has a mark ({m.Name}) that cannot be read back";
                continue;
            }

            var under = new StringBuilder();
            foreach (Node node in doc.Nodes)
            {
                foreach (AnchoredSpan seg in node.Segments)
                {
                    if (seg.Anchor.Kind != AnchorKind.OcrWords || seg.Anchor.Quads.IsDefaultOrEmpty)
                    {
                        continue;
                    }

                    Quad q = seg.Anchor.Quads[0];
                    double cx = (q.X1 + q.X2 + q.X3 + q.X4) / 4, cy = (q.Y1 + q.Y2 + q.Y3 + q.Y4) / 4;
                    foreach (var r in rects)
                    {
                        if (r.Page == q.Page && cx >= r.L - 0.01 && cx <= r.R + 0.01 && cy >= r.B - 0.01 && cy <= r.T + 0.01)
                        {
                            under.Append(node.Text.AsSpan(seg.Start, seg.Length));
                            break;
                        }
                    }
                }
            }

            string actual = Squash(under.ToString());
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                yield return $"change {m.ChangeId} ({m.Kind}): under the mark \"{Trim(actual)}\", changed text \"{Trim(expected)}\"";
            }
        }
    }

    private static string Squash(string s) => string.Concat(s.Where(static ch => !char.IsWhiteSpace(ch)));

    private static string Trim(string s) => s.Length <= 60 ? s : s[..60] + "...";
}
