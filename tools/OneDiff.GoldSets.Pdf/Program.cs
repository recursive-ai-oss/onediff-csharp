using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneDiff.Documents;
using OneDiff.Documents.Serialization;
using OneDiff.Documents.Model;
using OneDiff.Pdf;
using OnePdf;
using OnePdf.Hyphenation;
using OnePdf.Layout;
using OnePdf.Text;

namespace OneDiff.GoldSets.Pdf;

/// <summary>A node of a document to render: ids are positions, as in the gold set's own files.</summary>
internal sealed record N(int Id, string Kind, int Parent, string Text, string Label, string Style, List<(int Start, int Len)> Bold);

internal sealed record Located(int Node, string Kind, string Label, string Text, int Page);

internal sealed record SideInfo(List<Located> Blocks, int Pages, int Located, int Total);

internal sealed record BlockMap(string Id, string Lang, int Variant, SideInfo Old, SideInfo New);

internal sealed record FileEntry(string Path, string Sha256);

internal sealed record PdfManifest(string Schema, int Pdfs, int Pairs, Dictionary<string, int> PairsBySource, Dictionary<string, int> PdfsByVariant, int BlocksTotal, int BlocksLocated, List<FileEntry> Files);

/// <summary>
/// Renders gold-set documents to PDF through OnePDF's layout engine: one to two columns, headers and footers with page numbers,
/// hyphenated and justified text, tables, and CJK and Arabic text. After rendering, the text of each PDF is extracted again
/// and every block of the document is located in it, which is the check that labels survive rendering.
/// </summary>
internal static class Program
{
    private const string Usage = """
        OneDiff.GoldSets.Pdf render --out DIR [--data DIR] [--real DIR] [--fonts DIR[,DIR]] [--jobs N] [--limit N]
        OneDiff.GoldSets.Pdf compare --pdf DIR --out DIR [--every N] [--limit N] [--jobs N]
        OneDiff.GoldSets.Pdf cross --mode dp|pd|dd|pp --pdf DIR --docx DIR --out DIR [--settings legal|textonly] [--every N] [--limit N] [--jobs N]

          --data   a gold-set directory made by OneDiff.GoldSets generate (renders pairs/<id>.docs.json)
          --real   fetched real pairs: <source>/<pair>/{old.txt,new.txt}
          writes DIR/pdf/<id>.{old,new}.pdf and <id>.blocks.json, then manifest.json and manifest.sha256

          cross compares a Word file with a PDF of the same pair: dp reads the old side from DIR/<id>.old.docx and the new side
          from the PDF, pd the other way round (dd reads both sides from Word files and pp from PDFs, for reference). It writes OUT/<id>.cmp.json and OUT/<id>.blocks.json, a block map with the Word
          file's side taken from the Word set's map, so that `OneDiff.GoldSets score --cmp OUT --blocks OUT` scores it. Only ids in both sets.

          compare reads each DIR/<id>.{old,new}.pdf through the PDF source, compares with the default settings and writes
          OUT/<id>.cmp.json, which `OneDiff.GoldSets score --cmp OUT --blocks DIR` maps onto the gold nodes (--every keeps every Nth pair)
        """;

    private static readonly string[] DefaultFontDirs = ["/usr/share/fonts/opentype/noto", "/usr/share/fonts/truetype/noto"];

    private static int Compare(string[] args)
    {
        var opt = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i++)
        {
            opt[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        string pdfDir = Path.GetFullPath(opt.GetValueOrDefault("pdf") ?? throw new ArgumentException("--pdf is required"));
        string outDir = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        int every = int.Parse(opt.GetValueOrDefault("every") ?? "1", CultureInfo.InvariantCulture);
        int limit = int.Parse(opt.GetValueOrDefault("limit") ?? "0", CultureInfo.InvariantCulture);
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outDir);
        string[] ids = [.. Directory.EnumerateFiles(pdfDir, "*.old.pdf").Select(f => Path.GetFileName(f)[..^".old.pdf".Length]).OrderBy(x => x, StringComparer.Ordinal)
            .Where((_, i) => i % every == 0)];
        if (limit > 0)
        {
            ids = [.. ids.Take(limit)];
        }

        int failed = 0;
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = jobs }, id =>
        {
            try
            {
                var source = new PdfDocumentSource();
                CompareDocument o = source.Read(DocumentInput.FromBytes(File.ReadAllBytes(Path.Combine(pdfDir, id + ".old.pdf")), id + ".old.pdf"));
                CompareDocument n = source.Read(DocumentInput.FromBytes(File.ReadAllBytes(Path.Combine(pdfDir, id + ".new.pdf")), id + ".new.pdf"));
                File.WriteAllBytes(Path.Combine(outDir, id + ".cmp.json"), ComparisonJson.Write(Comparison.Of(o, n)));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Interlocked.Increment(ref failed);
                Console.Error.WriteLine($"{id}: {e.GetType().Name}: {e.Message}");
            }
        });
        Console.WriteLine($"compared {ids.Length} pairs, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static int Cross(string[] args)
    {
        var opt = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i++)
        {
            opt[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        string mode = opt.GetValueOrDefault("mode") ?? throw new ArgumentException("--mode dp|pd is required");
        if (mode is not ("dp" or "pd" or "dd" or "pp"))
        {
            throw new ArgumentException("--mode is dp, pd, dd or pp");
        }

        bool oldWord = mode is "dp" or "dd", newWord = mode is "pd" or "dd";

        string pdfDir = Path.GetFullPath(opt.GetValueOrDefault("pdf") ?? throw new ArgumentException("--pdf is required"));
        string docxDir = Path.GetFullPath(opt.GetValueOrDefault("docx") ?? throw new ArgumentException("--docx is required"));
        string outDir = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        int every = int.Parse(opt.GetValueOrDefault("every") ?? "1", CultureInfo.InvariantCulture);
        int limit = int.Parse(opt.GetValueOrDefault("limit") ?? "0", CultureInfo.InvariantCulture);
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        CompareSettings settings = (opt.GetValueOrDefault("settings") ?? "legal") switch
        {
            "legal" => CompareSettings.Legal,
            "textonly" => CompareSettings.TextOnly,
            _ => throw new ArgumentException("--settings is legal or textonly"),
        };
        Directory.CreateDirectory(outDir);
        string[] ids = [.. Directory.EnumerateFiles(pdfDir, "*.old.pdf").Select(f => Path.GetFileName(f)[..^".old.pdf".Length])
            .Where(id => File.Exists(Path.Combine(docxDir, id + ".old.docx")) && File.Exists(Path.Combine(docxDir, id + ".blocks.json")) && File.Exists(Path.Combine(pdfDir, id + ".blocks.json")))
            .OrderBy(x => x, StringComparer.Ordinal).Where((_, i) => i % every == 0)];
        if (limit > 0)
        {
            ids = [.. ids.Take(limit)];
        }

        int failed = 0;
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = jobs }, id =>
        {
            try
            {
                var pdf = new PdfDocumentSource();
                var word = new OneDiff.Word.WordDocumentSource();
                CompareDocument o = oldWord
                    ? word.Read(DocumentInput.FromBytes(File.ReadAllBytes(Path.Combine(docxDir, id + ".old.docx")), id + ".old.docx"))
                    : pdf.Read(DocumentInput.FromBytes(File.ReadAllBytes(Path.Combine(pdfDir, id + ".old.pdf")), id + ".old.pdf"));
                CompareDocument n = newWord
                    ? word.Read(DocumentInput.FromBytes(File.ReadAllBytes(Path.Combine(docxDir, id + ".new.docx")), id + ".new.docx"))
                    : pdf.Read(DocumentInput.FromBytes(File.ReadAllBytes(Path.Combine(pdfDir, id + ".new.pdf")), id + ".new.pdf"));
                File.WriteAllBytes(Path.Combine(outDir, id + ".cmp.json"), ComparisonJson.Write(Comparison.Of(o, n, settings)));

                // The gold node ids are the source documents' own, so a side's block map is the same whichever rendering it comes from.
                JsonNode pdfMap = JsonNode.Parse(File.ReadAllBytes(Path.Combine(pdfDir, id + ".blocks.json")))!;
                JsonNode docxMap = JsonNode.Parse(File.ReadAllBytes(Path.Combine(docxDir, id + ".blocks.json")))!;
                if (oldWord)
                {
                    pdfMap["old"] = docxMap["old"]!.DeepClone();
                }

                if (newWord)
                {
                    pdfMap["new"] = docxMap["new"]!.DeepClone();
                }

                File.WriteAllText(Path.Combine(outDir, id + ".blocks.json"), pdfMap.ToJsonString());
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Interlocked.Increment(ref failed);
                Console.Error.WriteLine($"{id}: {e.GetType().Name}: {e.Message}");
            }
        });
        Console.WriteLine($"compared {ids.Length} cross pairs ({mode}), {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "compare")
        {
            return Compare(args);
        }

        if (args.Length > 0 && args[0] == "cross")
        {
            return Cross(args);
        }

        if (args.Length == 0 || args[0] != "render")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 0 : 2;
        }

        var opt = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i++)
        {
            opt[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        string outDir = Path.GetFullPath(opt.GetValueOrDefault("out") ?? throw new ArgumentException("--out is required"));
        string[] fontDirs = opt.TryGetValue("fonts", out string? f) ? f.Split(',') : DefaultFontDirs;
        int jobs = int.Parse(opt.GetValueOrDefault("jobs") ?? "4", CultureInfo.InvariantCulture);
        int limit = int.Parse(opt.GetValueOrDefault("limit") ?? "0", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.Combine(outDir, "pdf"));

        var work = new List<(string Id, string Source, string Lang, List<N> Old, List<N> New)>();
        if (opt.TryGetValue("data", out string? data))
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(data, "pairs"), "*.docs.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file));
                string lang = doc.RootElement.GetProperty("lang").GetString()!;
                work.Add((doc.RootElement.GetProperty("id").GetString()!, "synth", lang, ReadNodes(doc.RootElement.GetProperty("old")), ReadNodes(doc.RootElement.GetProperty("new"))));
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
            work = work.Take(limit).ToList();
        }

        var maps = new BlockMap[work.Count];
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, work.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            (string id, _, string lang, List<N> o, List<N> n) = work[i];
            int variant = Variant(id);
            try
            {
                SideInfo so = RenderSide(Path.Combine(outDir, "pdf", id + ".old.pdf"), o, lang, variant, fontDirs);
                SideInfo sn = RenderSide(Path.Combine(outDir, "pdf", id + ".new.pdf"), n, lang, variant, fontDirs);
                maps[i] = new BlockMap(id, lang, variant, so, sn);
                File.WriteAllBytes(Path.Combine(outDir, "pdf", id + ".blocks.json"), JsonSerializer.SerializeToUtf8Bytes(maps[i], MapOptions));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                errors.Add($"{id}: {e.GetType().Name}: {e.Message}");
            }
        });

        var files = new List<FileEntry>();
        foreach (string p in Directory.EnumerateFiles(Path.Combine(outDir, "pdf")).OrderBy(x => x, StringComparer.Ordinal))
        {
            files.Add(new FileEntry("pdf/" + Path.GetFileName(p), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))));
        }

        BlockMap[] ok = [.. maps.Where(m => m is not null)];
        var bySource = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach ((string id, string source, _, _, _) in work)
        {
            bySource[source] = bySource.GetValueOrDefault(source) + 1;
        }

        var byVariant = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (BlockMap m in ok)
        {
            string key = "variant-" + m.Variant;
            byVariant[key] = byVariant.GetValueOrDefault(key) + 2;
        }

        var manifest = new PdfManifest("onediff.pdfgoldset/1", files.Count(x => x.Path.EndsWith(".pdf", StringComparison.Ordinal)), ok.Length,
            bySource.ToDictionary(x => x.Key, x => x.Value), byVariant.ToDictionary(x => x.Key, x => x.Value),
            ok.Sum(m => m.Old.Total + m.New.Total), ok.Sum(m => m.Old.Located + m.New.Located), files);
        byte[] mb = JsonSerializer.SerializeToUtf8Bytes(manifest, MapOptions);
        File.WriteAllBytes(Path.Combine(outDir, "manifest.json"), mb);
        string hash = Convert.ToHexStringLower(SHA256.HashData(mb));
        File.WriteAllText(Path.Combine(outDir, "manifest.sha256"), hash + "\n");
        File.WriteAllLines(Path.Combine(outDir, "errors.txt"), errors.OrderBy(x => x, StringComparer.Ordinal));
        Console.WriteLine($"pdfs={manifest.Pdfs} pairs={manifest.Pairs} blocks located {manifest.BlocksLocated}/{manifest.BlocksTotal} errors={errors.Count} set-sha256={hash}");
        foreach ((string k, int v) in bySource)
        {
            Console.WriteLine($"  source {k}: {v} pairs");
        }

        foreach ((string k, int v) in byVariant)
        {
            Console.WriteLine($"  {k}: {v} pdfs");
        }

        foreach (string e in errors.OrderBy(x => x, StringComparer.Ordinal).Take(10))
        {
            Console.Error.WriteLine(e);
        }

        return errors.IsEmpty ? 0 : 1;
    }

    private static readonly JsonSerializerOptions MapOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A layout is chosen from the pair's id, so old and new look alike and a rerun gives the same files.</summary>
    private static int Variant(string id) => SHA256.HashData(Encoding.UTF8.GetBytes(id))[0] % 4;

    private static List<N> ReadNodes(JsonElement file)
    {
        var list = new List<N>();
        int i = 0;
        foreach (JsonElement n in file.GetProperty("nodes").EnumerateArray())
        {
            var bold = new List<(int, int)>();
            foreach (JsonElement r in n.GetProperty("runs").EnumerateArray())
            {
                if (r.GetProperty("format").TryGetProperty("run.bold", out _))
                {
                    bold.Add((r.GetProperty("start").GetInt32(), r.GetProperty("length").GetInt32()));
                }
            }

            JsonElement attrs = n.GetProperty("attrs");
            list.Add(new N(i++, n.GetProperty("kind").GetString()!, n.GetProperty("parent").GetInt32(), n.GetProperty("text").GetString()!,
                attrs.TryGetProperty("list.label", out JsonElement l) ? l.GetString()! : "",
                attrs.TryGetProperty("para.style", out JsonElement s) ? s.GetString()! : "", bold));
        }

        return list;
    }

    /// <summary>Real documents are plain text: paragraphs are runs of non-blank lines.</summary>
    private static List<N> FromText(string text)
    {
        var list = new List<N> { new(0, "Section", -1, "", "", "", []) };
        foreach (string para in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string t = string.Join(' ', para.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (t.Length > 0)
            {
                list.Add(new N(list.Count, "Paragraph", 0, t, "", "", []));
            }
        }

        return list;
    }

    // ---- layout ----------------------------------------------------------------------------------------------------

    private static SideInfo RenderSide(string path, List<N> nodes, string lang, int variant, string[] fontDirs)
    {
        // Small pages and generous type, so paragraphs run across page and column breaks.
        (double w, double h) = variant switch { 0 or 1 or 3 => (419.53, 595.28), _ => (595.28, 841.89) };
        double margin = variant switch { 0 => 44, 1 => 28, 2 => 80, _ => 34 };
        double size = variant switch { 0 => 10.5, 1 => 8.5, 2 => 12, _ => 9.5 };
        bool columns = variant is 1 or 3;
        bool justify = variant is 0 or 1 or 3;
        bool header = variant != 0;
        bool rtl = lang == "ar";
        IHyphenator hyph = Hyphenators.Auto();
        string title = nodes.FirstOrDefault(x => x.Style == "Title")?.Text ?? nodes.Skip(1).FirstOrDefault()?.Text[..Math.Min(40, nodes.Skip(1).First().Text.Length)] ?? "";

        void Para(Slot slot, N n, bool bold, double fs, bool center)
        {
            slot.Text(t =>
            {
                t.FontSize(fs);
                t.Language(lang);
                t.Hyphenator(hyph);
                if (center)
                {
                    t.AlignCenter();
                }
                else if (justify && !bold)
                {
                    t.Justify();
                }
                else
                {
                    t.AlignStart();
                }

                if (n.Label.Length > 0)
                {
                    t.Span(n.Label + " ").Bold();
                }

                int pos = 0;
                foreach ((int start, int len) in n.Bold.OrderBy(b => b.Start))
                {
                    if (start > pos)
                    {
                        Emit(t, n.Text[pos..start], bold);
                    }

                    Emit(t, n.Text.Substring(start, len), true);
                    pos = start + len;
                }

                if (pos < n.Text.Length)
                {
                    Emit(t, n.Text[pos..], bold);
                }
            });
        }

        void Item(Slot slot, N n)
        {
            switch (n.Kind)
            {
                case "Table":
                    slot.Table(table =>
                    {
                        List<N> rows = nodes.Where(x => x.Parent == n.Id).ToList();
                        int cols = Math.Max(1, rows.Count == 0 ? 1 : nodes.Count(x => x.Parent == rows[0].Id));
                        table.Columns(c =>
                        {
                            for (int i = 0; i < cols; i++)
                            {
                                c.Relative(i == 0 ? 2 : 1);
                            }
                        });
                        table.CellBorder(0.5, Colors.LightGrey);
                        table.CellPadding(3);
                        for (int r = 0; r < rows.Count; r++)
                        {
                            bool head = r == 0;
                            Action<TableRowDescriptor> define = row =>
                            {
                                foreach (N cell in nodes.Where(x => x.Parent == rows[r].Id))
                                {
                                    N? p = nodes.FirstOrDefault(x => x.Parent == cell.Id);
                                    if (p is not null)
                                    {
                                        Para(row.Cell(), p, head, size - 0.5, false);
                                    }
                                }
                            };
                            if (head)
                            {
                                table.HeaderRow(define);
                            }
                            else
                            {
                                table.Row(define);
                            }
                        }
                    });
                    break;
                case "Paragraph" or "ListItem":
                    bool isTitle = n.Style == "Title";
                    bool isHeading = n.Style == "Heading1";
                    Para(slot, n, isTitle || isHeading, isTitle ? size + 6 : isHeading ? size + 1.5 : size, isTitle);
                    break;
                default:
                    break;
            }
        }

        // Top-level children of the root; table internals are rendered with their table.
        List<N> top = nodes.Where(x => x.Parent == 0).ToList();
        var doc = LayoutDocument.Compose(d =>
        {
            d.Language(lang);
            d.Title(title.Length > 0 ? title : "Document");
            AddFaces(d.Fonts, lang, fontDirs);
            if (rtl)
            {
                d.RightToLeft();
            }

            d.Page(p =>
            {
                p.Size(w, h);
                p.Margin(margin);
                if (rtl)
                {
                    p.RightToLeft();
                }

                if (header)
                {
                    p.Header().Text(title).FontSize(7.5);
                }

                p.Footer().AlignCenter().Text(t =>
                {
                    t.FontSize(8);
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages().ReservedDigits(2);
                });
                if (columns)
                {
                    p.Content().MultiColumn(c =>
                    {
                        c.Count(2).Gap(14).Spacing(5);
                        foreach (N n in top)
                        {
                            Item(c.Item(), n);
                        }
                    });
                }
                else
                {
                    p.Content().Column(c =>
                    {
                        c.Spacing(5);
                        foreach (N n in top)
                        {
                            Item(c.Item(), n);
                        }
                    });
                }
            });
        });
        // The file id is taken from the content's name, so the same input gives the same bytes.
        byte[] bytes = doc.ToBytes(new PdfDocumentWriterOptions { DocumentId = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(path)))[..16] });
        File.WriteAllBytes(path, bytes);
        return Locate(bytes, nodes);
    }

    /// <summary>Registers the script's font (the Latin faces are already there) as a fallback family.</summary>
    private static void AddFaces(TextFontRegistry fonts, string lang, string[] dirs)
    {
        (string File, string Family, int Face)? need = lang switch
        {
            "zh" => ("NotoSansCJK-Regular.ttc", "Noto CJK", 2),
            "ja" => ("NotoSansCJK-Regular.ttc", "Noto CJK", 0),
            "ar" => ("NotoSansArabic-Regular.ttf", "Noto Arabic", 0),
            _ => null,
        };
        if (need is null)
        {
            return;
        }

        string? path = dirs.Select(d => Path.Combine(d, need.Value.File)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(need.Value.File + " not found in the font directories");
        fonts.Add(File.ReadAllBytes(path), need.Value.Family, faceIndex: need.Value.Face);
        fonts.FallbackFamilies.Add(need.Value.Family);
    }

    private static void Emit(TextDescriptor t, string s, bool bold)
    {
        if (s.Length == 0)
        {
            return;
        }

        TextSpanDescriptor span = t.Span(s);
        if (bold)
        {
            span.Bold();
        }
    }

    // ---- re-extraction ---------------------------------------------------------------------------------------------

    private static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (!char.IsWhiteSpace(c) && c != '\u00AD' && c != '\u200E' && c != '\u200F')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Extracts the PDF's text again and finds each content block of the document in it, in order, noting its page.</summary>
    private static SideInfo Locate(byte[] pdfBytes, List<N> nodes)
    {
        using PdfDocument pdf = PdfDocument.Open(pdfBytes);
        IReadOnlyList<PdfPageText> pages = pdf.ExtractText(new PdfTextOptions { JoinHyphenatedWords = true, ExcludeHeadersAndFooters = true });
        var hay = new StringBuilder();
        var starts = new List<int>();
        foreach (PdfPageText p in pages)
        {
            starts.Add(hay.Length);
            hay.Append(Squash(p.Text));
        }

        string text = hay.ToString();
        int PageOf(int idx)
        {
            int page = 0;
            for (int i = 0; i < starts.Count; i++)
            {
                if (starts[i] <= idx)
                {
                    page = i;
                }
            }

            return page;
        }

        var blocks = new List<Located>();
        int cursor = 0;
        int located = 0;
        int total = 0;
        foreach (N n in nodes.Where(x => x.Kind is "Paragraph" or "ListItem" && x.Text.Length > 0))
        {
            total++;
            string needle = Squash(n.Text);
            int idx = text.IndexOf(needle, cursor, StringComparison.Ordinal);
            if (idx < 0)
            {
                idx = text.IndexOf(needle, StringComparison.Ordinal);
            }
            else
            {
                cursor = idx + needle.Length;
            }

            if (idx >= 0)
            {
                located++;
            }

            blocks.Add(new Located(n.Id, n.Kind, n.Label, n.Text, idx >= 0 ? PageOf(idx) : -1));
        }

        return new SideInfo(blocks, pages.Count, located, total);
    }
}
