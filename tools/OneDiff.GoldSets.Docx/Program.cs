using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneOffice;
using RecursiveAI.OneOffice.Documents;

namespace OneDiff.GoldSets.Docx;

internal sealed record FileEntry(string Path, string Sha256);

internal sealed record DocxManifest(
    string Schema, int Docx, int Pairs, Dictionary<string, int> PairsBySource, Dictionary<string, int> DocxByVariant, int BlocksTotal, int BlocksLocated,
    int LabelsTotal, int LabelsOk, List<FileEntry> Files);

/// <summary>
/// The DOCX gold-set tool. <c>render</c> writes every pair of a gold set (and the real pairs) as two Word files with a block map;
/// <c>read</c> turns the tracked-changes files other tools wrote into the scorer's change files and measures how well their
/// accept-all and reject-all reproduce the two inputs; <c>bench-pairs</c> builds big pairs for timing.
/// </summary>
internal static class Program
{
    private const string Usage = """
        OneDiff.GoldSets.Docx render --out DIR [--data DIR] [--real DIR] [--jobs N] [--limit N]
        OneDiff.GoldSets.Docx read --tool NAME --tracked DIR --docx DIR --out DIR [--jobs N]
        OneDiff.GoldSets.Docx bench-pairs --data DIR --out DIR --lang xx --sizes 1,8,24

          render       DIR/docx/<id>.{old,new}.docx and <id>.blocks.json for every pair under --data (a gold set made by
                       OneDiff.GoldSets generate) and --real (<source>/<pair>/{old.txt,new.txt}), then manifest.json and
                       manifest.sha256 (the set hash). Every file is read back with OneOffice and its blocks and list labels checked.
          read         for every <id>.docx in --tracked (a tool's tracked-changes output for the pair <id>): DIR/<id>.changes.json
                       for 'GoldSets score --changes', and DIR/roundtrip.jsonl, one line per pair, with the result of accepting and
                       rejecting every revision (through OneOffice) compared with the two inputs in --docx.
          bench-pairs  joins N mixed pairs of one language into one big pair for timing: DIR/n<N>/old.docx and new.docx.
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
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
                "render" => Render(opt),
                "read" => ReadTracked(opt),
                "bench-pairs" => BenchPairs(opt),
                _ => Fail(Usage),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or JsonException or OfficeException)
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

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---- render --------------------------------------------------------------------------------------------------------

    private static List<GNode> ReadNodes(DocFile file)
    {
        var list = new List<GNode>();
        int i = 0;
        foreach (DocNode n in file.Nodes)
        {
            n.Attrs.TryGetValue("list.label", out string? label);
            int level = n.Attrs.TryGetValue("list.level", out string? l) ? int.Parse(l, CultureInfo.InvariantCulture) : 0;
            n.Attrs.TryGetValue("para.style", out string? style);
            var bold = new List<(int, int)>();
            foreach (DocRun r in n.Runs)
            {
                if (r.Format.ContainsKey("run.bold"))
                {
                    bold.Add((r.Start, r.Length));
                }
            }

            list.Add(new GNode(i++, n.Kind, n.Parent, n.Text, label ?? string.Empty, level, style ?? string.Empty, bold));
        }

        return list;
    }

    private static List<GNode> FromText(string text)
    {
        var list = new List<GNode> { new(0, "Section", -1, string.Empty, string.Empty, 0, string.Empty, []) };
        foreach (string para in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string t = string.Join(' ', para.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (t.Length > 0)
            {
                list.Add(new GNode(list.Count, "Paragraph", 0, t, string.Empty, 0, string.Empty, []));
            }
        }

        return list;
    }

    private static int Render(Dictionary<string, string> opt)
    {
        string outDir = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        int limit = int.Parse(opt.GetValueOrDefault("limit") ?? "0", CultureInfo.InvariantCulture);
        string dir = Path.Combine(outDir, "docx");
        Directory.CreateDirectory(dir);
        var work = new List<(string Id, string Source, string Lang, List<GNode> Old, List<GNode> New)>();
        if (opt.TryGetValue("data", out string? data))
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(data, "pairs"), "*.docs.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                var docs = GoldJson.Read<PairDocs>(File.ReadAllBytes(file));
                work.Add((docs.Id, "synth", docs.Lang, ReadNodes(docs.Old), ReadNodes(docs.New)));
            }
        }

        if (opt.TryGetValue("real", out string? real))
        {
            foreach (string src in Directory.EnumerateDirectories(real).OrderBy(x => x, StringComparer.Ordinal))
            {
                foreach (string pair in Directory.EnumerateDirectories(src).OrderBy(x => x, StringComparer.Ordinal))
                {
                    string o = Path.Combine(pair, "old.txt");
                    string n = Path.Combine(pair, "new.txt");
                    if (File.Exists(o) && File.Exists(n))
                    {
                        work.Add(($"{Path.GetFileName(src)}-{Path.GetFileName(pair)}", Path.GetFileName(src), "en", FromText(File.ReadAllText(o)), FromText(File.ReadAllText(n))));
                    }
                }
            }
        }

        if (limit > 0)
        {
            work = [.. work.Take(limit)];
        }

        var maps = new BlockMap[work.Count];
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, work.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            (string id, _, string lang, List<GNode> o, List<GNode> n) = work[i];
            Look look = DocxWriter.LookFor(id);
            try
            {
                BlockSide so = RenderSide(Path.Combine(dir, id + ".old.docx"), o, lang, look);
                BlockSide sn = RenderSide(Path.Combine(dir, id + ".new.docx"), n, lang, look);
                maps[i] = new BlockMap(id, lang, so, sn, look.Variant);
                File.WriteAllBytes(Path.Combine(dir, id + ".blocks.json"), JsonSerializer.SerializeToUtf8Bytes(maps[i], Json));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                errors.Add($"{id}: {e.GetType().Name}: {e.Message}");
            }
        });

        var files = new List<FileEntry>();
        foreach (string p in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
        {
            files.Add(new FileEntry("docx/" + Path.GetFileName(p), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))));
        }

        BlockMap[] ok = [.. maps.Where(m => m is not null)];
        var bySource = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach ((_, string source, _, _, _) in work)
        {
            bySource[source] = bySource.GetValueOrDefault(source) + 1;
        }

        var byVariant = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (BlockMap m in ok)
        {
            string key = "variant-" + m.Variant.ToString(CultureInfo.InvariantCulture);
            byVariant[key] = byVariant.GetValueOrDefault(key) + 2;
        }

        var manifest = new DocxManifest("onediff.docxgoldset/1", files.Count(f => f.Path.EndsWith(".docx", StringComparison.Ordinal)), ok.Length,
            bySource.ToDictionary(x => x.Key, x => x.Value), byVariant.ToDictionary(x => x.Key, x => x.Value),
            ok.Sum(m => m.Old.Total + m.New.Total), ok.Sum(m => m.Old.Located + m.New.Located),
            ok.Sum(m => m.Old.LabelsTotal + m.New.LabelsTotal), ok.Sum(m => m.Old.LabelsOk + m.New.LabelsOk), files);
        byte[] mb = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        File.WriteAllBytes(Path.Combine(outDir, "manifest.json"), mb);
        string hash = Convert.ToHexStringLower(SHA256.HashData(mb));
        File.WriteAllText(Path.Combine(outDir, "manifest.sha256"), hash + "\n");
        File.WriteAllLines(Path.Combine(outDir, "errors.txt"), errors.OrderBy(x => x, StringComparer.Ordinal));
        Console.WriteLine($"docx={manifest.Docx} pairs={manifest.Pairs} blocks located {manifest.BlocksLocated}/{manifest.BlocksTotal} list labels equal {manifest.LabelsOk}/{manifest.LabelsTotal} errors={errors.Count} set-sha256={hash}");
        foreach ((string k, int v) in bySource)
        {
            Console.WriteLine($"  source {k}: {v} pairs");
        }

        foreach ((string k, int v) in byVariant)
        {
            Console.WriteLine($"  {k}: {v} files");
        }

        foreach (string e in errors.OrderBy(x => x, StringComparer.Ordinal).Take(10))
        {
            Console.Error.WriteLine(e);
        }

        return errors.IsEmpty && manifest.BlocksLocated == manifest.BlocksTotal && manifest.LabelsOk == manifest.LabelsTotal ? 0 : 1;
    }

    private static BlockSide RenderSide(string path, List<GNode> nodes, string lang, Look look)
    {
        Rendered r = DocxWriter.Write(nodes, lang, look, path);
        (int located, int total, int labelsOk, int labelsTotal) = DocxWriter.Check(path, r.Blocks, nodes);
        return new BlockSide(r.Blocks, r.Paragraphs, located, total, labelsOk, labelsTotal);
    }

    // ---- bench pairs ---------------------------------------------------------------------------------------------------

    private static int BenchPairs(Dictionary<string, string> opt)
    {
        string data = opt.GetValueOrDefault("data") ?? throw new ArgumentException("--data is required");
        string outDir = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        string lang = opt.GetValueOrDefault("lang") ?? "en";
        int[] sizes = [.. (opt.GetValueOrDefault("sizes") ?? "1,8,24").Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
        string[] files = [.. Directory.EnumerateFiles(Path.Combine(data, "pairs"), lang + "-*-m.docs.json").OrderBy(x => x, StringComparer.Ordinal)];
        foreach (int n in sizes)
        {
            if (n > files.Length)
            {
                throw new ArgumentException($"only {files.Length} mixed pairs for {lang}");
            }

            List<GNode> o = [new(0, "Section", -1, string.Empty, string.Empty, 0, string.Empty, [])];
            List<GNode> nw = [new(0, "Section", -1, string.Empty, string.Empty, 0, string.Empty, [])];
            int edits = 0;
            for (int k = 0; k < n; k++)
            {
                var docs = GoldJson.Read<PairDocs>(File.ReadAllBytes(files[k]));
                Append(o, ReadNodes(docs.Old));
                Append(nw, ReadNodes(docs.New));
                edits += GoldJson.Read<PairLabels>(File.ReadAllBytes(files[k].Replace(".docs.json", ".labels.json", StringComparison.Ordinal))).Units.Count;
            }

            string dir = Path.Combine(outDir, "n" + n.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            Look look = DocxWriter.LookFor("bench-" + lang);
            Rendered ro = DocxWriter.Write(o, lang, look, Path.Combine(dir, "old.docx"));
            Rendered rn = DocxWriter.Write(nw, lang, look, Path.Combine(dir, "new.docx"));
            Console.WriteLine($"n{n}: {ro.Paragraphs} / {rn.Paragraphs} paragraphs, {edits} labelled changes, old {ro.Bytes.Length} bytes, new {rn.Bytes.Length} bytes");
        }

        return 0;
    }

    private static void Append(List<GNode> into, List<GNode> from)
    {
        int shift = into.Count - 1;
        foreach (GNode n in from.Skip(1))
        {
            into.Add(n with { Id = n.Id + shift, Parent = n.Parent == 0 ? 0 : n.Parent + shift });
        }
    }

    // ---- read ----------------------------------------------------------------------------------------------------------

    private sealed record RoundTrip(string Id, string Tool, bool Opened, bool AcceptParagraphs, bool RejectParagraphs, bool AcceptText, bool RejectText, int Revisions, int Changes, string? Error);

    /// <summary>The non-empty paragraph texts of a document, squashed (white space and soft hyphens do not count).</summary>
    private static List<string> ParagraphTexts(Document doc)
    {
        var list = new List<string>();
        foreach (Paragraph p in doc.Body.Paragraphs())
        {
            string t = Squash.Of(p.Text);
            if (t.Length > 0)
            {
                list.Add(t);
            }
        }

        return list;
    }

    /// <summary>
    /// Some tools type every <c>.xml</c> part as the main document through one default content type and give the main part no override of
    /// its own. That is valid OPC, but OneOffice 0.1 does not recognise such a package, so the override is added to the content-type
    /// list (nothing else is touched) before the file is read.
    /// </summary>
    internal static byte[] Openable(byte[] bytes)
    {
        try
        {
            using Document _ = Document.Open(new MemoryStream(bytes), leaveOpen: false);
            return bytes;
        }
        catch (OfficeException)
        {
            using var input = new MemoryStream();
            input.Write(bytes);
            using var zip = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Update);
            System.IO.Compression.ZipArchiveEntry entry = zip.GetEntry("[Content_Types].xml") ?? throw new InvalidDataException("no content-type list");
            string xml;
            using (var r = new StreamReader(entry.Open(), Encoding.UTF8))
            {
                xml = r.ReadToEnd();
            }

            const string main = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
            if (!xml.Contains("PartName=\"/word/document.xml\"", StringComparison.Ordinal))
            {
                xml = xml.Replace("</Types>", "<Override PartName=\"/word/document.xml\" ContentType=\"" + main + "\"/></Types>", StringComparison.Ordinal);
            }

            entry.Delete();
            System.IO.Compression.ZipArchiveEntry fresh = zip.CreateEntry("[Content_Types].xml");
            using (var w = new StreamWriter(fresh.Open(), new UTF8Encoding(false)))
            {
                w.Write(xml.TrimStart('\uFEFF'));
            }

            zip.Dispose();
            return input.ToArray();
        }
    }

    private static int ReadTracked(Dictionary<string, string> opt)
    {
        string tool = opt.GetValueOrDefault("tool") ?? throw new ArgumentException("--tool is required");
        string tracked = opt.GetValueOrDefault("tracked") ?? throw new ArgumentException("--tracked is required");
        string docx = opt.GetValueOrDefault("docx") ?? throw new ArgumentException("--docx is required");
        string outDir = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outDir);
        string[] ids = [.. Directory.EnumerateFiles(tracked, "*.docx").Select(p => Path.GetFileNameWithoutExtension(p)).OrderBy(x => x, StringComparer.Ordinal)];
        var results = new RoundTrip[ids.Length];
        Parallel.For(0, ids.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            string id = ids[i];
            try
            {
                byte[] bytes = Openable(File.ReadAllBytes(Path.Combine(tracked, id + ".docx")));
                using Document doc = Document.Open(new MemoryStream(bytes), leaveOpen: false);
                ToolOutput output = TrackedReader.Read(id, tool, doc);
                File.WriteAllBytes(Path.Combine(outDir, id + ".changes.json"), ToolOutput.Write(output));
                int revisions = doc.Revisions.Revisions.Count;

                using Document src0 = Document.Open(Path.Combine(docx, id + ".old.docx"));
                using Document src1 = Document.Open(Path.Combine(docx, id + ".new.docx"));
                List<string> wantOld = ParagraphTexts(src0);
                List<string> wantNew = ParagraphTexts(src1);

                // Accept and reject on copies, through OneOffice, and compare what is left with the two inputs.
                using Document acc = Document.Open(new MemoryStream(bytes), leaveOpen: false);
                acc.Revisions.AcceptAll();
                List<string> gotNew = ParagraphTexts(acc);
                using Document rej = Document.Open(new MemoryStream(bytes), leaveOpen: false);
                rej.Revisions.RejectAll();
                List<string> gotOld = ParagraphTexts(rej);
                results[i] = new RoundTrip(id, tool, true, gotNew.SequenceEqual(wantNew), gotOld.SequenceEqual(wantOld),
                    string.Concat(gotNew) == string.Concat(wantNew), string.Concat(gotOld) == string.Concat(wantOld), revisions, output.Changes.Count, null);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                results[i] = new RoundTrip(id, tool, false, false, false, false, false, 0, 0, e.GetType().Name + ": " + e.Message);
            }
        });

        File.WriteAllLines(Path.Combine(outDir, "roundtrip.jsonl"), results.Select(r => JsonSerializer.Serialize(r, Json)));
        Console.WriteLine($"{tool}: {results.Length} outputs, opened {results.Count(r => r.Opened)}, accept-all = new {results.Count(r => r.AcceptParagraphs)}, reject-all = old {results.Count(r => r.RejectParagraphs)} (paragraph sequences); text only {results.Count(r => r.AcceptText)} / {results.Count(r => r.RejectText)}");
        foreach (RoundTrip r in results.Where(r => r.Error is not null).Take(5))
        {
            Console.Error.WriteLine($"{r.Id}: {r.Error}");
        }

        return 0;
    }
}
