using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OneDiff.Oracles;

/// <summary>
/// Drives the reference tools (GNU diff, GNU patch, git, the diff-match-patch package) as black boxes over a corpus and reports,
/// per gate, how many cases passed and which failed. Run with <c>--help</c> for the options.
/// </summary>
internal static class Program
{
    private const string Usage = """
        OneDiff.Oracles run [options]

          --corpus DIR      corpus root (default $ONEDIFF_CORPORA, else $ONEDIFF_HOME/corpora, else ~/.cache/onediff/corpora)
          --oracles DIR     oracle install root (default $ONEDIFF_HOME/oracles, else ~/.cache/onediff/oracles)
          --out DIR         report and work directory (default ./oracle-out); results.jsonl there makes a rerun resume
          --gates LIST      comma-separated gate names (default all): min-edit, validity, roundtrip-unified, roundtrip-dmp,
                            accept-gnu-patch, accept-git-apply, dmp-interop, merge-file:merge, merge-file:diff3, merge-file:zdiff3
          --cores N         cases in flight at once (default 4)
          --seam NAME       onediff (the library; default) or oracle (answers from the oracles themselves, to test the gates)
          --source PREFIX   only corpus sources whose directory name starts with PREFIX
          --limit N         at most N cases per gate (the first N in corpus order)
          --timeout SEC     per oracle call (default 300)
          --fresh           discard earlier results in --out

        Writes report.json (deterministic: no timings), summary.txt and merge-mismatch/ under --out.
        Exit code 0 when no gate has a failure, 1 otherwise.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (args[0] != "run")
        {
            Console.Error.WriteLine("unknown command " + args[0]);
            return 2;
        }

        Dictionary<string, string> opt = Parse(args.Skip(1).ToArray());
        string home = Environment.GetEnvironmentVariable("ONEDIFF_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "onediff");
        string corpus = Full(opt.GetValueOrDefault("corpus") ?? Environment.GetEnvironmentVariable("ONEDIFF_CORPORA") ?? Path.Combine(home, "corpora"));
        string oracleRoot = Full(opt.GetValueOrDefault("oracles") ?? Path.Combine(home, "oracles"));
        string outDir = Full(opt.GetValueOrDefault("out") ?? "oracle-out");
        int cores = int.Parse(opt.GetValueOrDefault("cores") ?? "4", CultureInfo.InvariantCulture);
        int limit = int.Parse(opt.GetValueOrDefault("limit") ?? "0", CultureInfo.InvariantCulture);
        int timeout = int.Parse(opt.GetValueOrDefault("timeout") ?? "300", CultureInfo.InvariantCulture);
        string seamName = opt.GetValueOrDefault("seam") ?? "onediff";
        string? source = opt.GetValueOrDefault("source");
        string[]? wanted = opt.GetValueOrDefault("gates")?.Split(',', StringSplitOptions.RemoveEmptyEntries);

        Directory.CreateDirectory(outDir);
        if (opt.ContainsKey("fresh"))
        {
            foreach (string f in Directory.GetFiles(outDir, "results-*.jsonl"))
            {
                File.Delete(f);
            }

            string mm = Path.Combine(outDir, "merge-mismatch");
            if (Directory.Exists(mm))
            {
                Directory.Delete(mm, true);
            }
        }

        OracleTools tools = await OracleTools.OpenAsync(oracleRoot).ConfigureAwait(false);
        ITextCoreSeam seam = seamName switch
        {
            "onediff" => new OneDiffSeam(),
            "oracle" => new OracleSelfSeam(tools, Path.Combine(outDir, "tmp")),
            _ => throw new ArgumentException("unknown seam " + seamName),
        };
        Directory.CreateDirectory(Path.Combine(outDir, "tmp"));
        var ctx = new GateContext(tools, seam, outDir, outDir, TimeSpan.FromSeconds(timeout));

        List<Case> textCases = Corpus.TextCases(corpus, source);
        List<Case> mergeCases = Corpus.MergeCases(corpus, source);
        if (limit > 0)
        {
            textCases = textCases.Take(limit).ToList();
            mergeCases = mergeCases.Take(limit).ToList();
        }

        var gates = Gate.All.Where(g => wanted is null || wanted.Contains(g.Name)).ToList();
        string store = Path.Combine(outDir, $"results-{seamName}.jsonl");
        var done = LoadStore(store);
        await using var writer = new StreamWriter(store, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        var gate = new object();

        foreach (Gate g in gates)
        {
            if (g.Needs is { } need && !seam.Has(need))
            {
                Console.Error.WriteLine($"{g.Name}: pending (library side not wired)");
                continue;
            }

            List<Case> cases = g.MergeCases ? mergeCases : textCases;
            List<Case> todo = cases.Where(c => !done.ContainsKey(g.Name + "|" + c.Id)).ToList();
            Console.Error.WriteLine($"{g.Name}: {cases.Count} cases, {todo.Count} to run");
            int finished = 0;
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, cores) }, async (c, _) =>
            {
                Outcome outcome;
                try
                {
                    outcome = await g.RunAsync(ctx, c).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    outcome = Outcome.Fail($"exception {e.GetType().Name}: {MinEditGate.Trim(e.Message)}");
                }

                lock (gate)
                {
                    done[g.Name + "|" + c.Id] = outcome;
                    writer.WriteLine(JsonSerializer.Serialize(new Line(g.Name, c.Id, outcome.Status.ToString(), outcome.Detail)));
                    if (++finished % 500 == 0)
                    {
                        Console.Error.WriteLine($"{g.Name}: {finished}/{todo.Count}");
                    }
                }
            }).ConfigureAwait(false);
        }

        return WriteReport(outDir, corpus, tools, seamName, gates, textCases, mergeCases, done, seam);
    }

    private sealed record Line(string G, string C, string S, string? D);

    private static Dictionary<string, Outcome> LoadStore(string path)
    {
        var done = new Dictionary<string, Outcome>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return done;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement r = doc.RootElement;
            done[r.GetProperty("G").GetString()! + "|" + r.GetProperty("C").GetString()!] =
                new Outcome(Enum.Parse<Status>(r.GetProperty("S").GetString()!), r.GetProperty("D").GetString());
        }

        return done;
    }

    private static int WriteReport(
        string outDir,
        string corpus,
        OracleTools tools,
        string seamName,
        List<Gate> gates,
        List<Case> textCases,
        List<Case> mergeCases,
        Dictionary<string, Outcome> done,
        ITextCoreSeam seam)
    {
        const int MaxListed = 200;
        var summary = new StringBuilder();
        summary.AppendLine("oracles:");
        foreach (KeyValuePair<string, string> v in tools.Versions)
        {
            summary.AppendLine($"  {v.Key}: {v.Value}");
        }

        summary.AppendLine($"seam: {seamName}");
        string manifest = Path.Combine(corpus, "manifest.json");
        string? manifestHash = File.Exists(manifest) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifest))) : null;
        summary.AppendLine($"corpus manifest sha256: {manifestHash ?? "(none)"}");
        summary.AppendLine();

        bool anyFailure = false;
        using var stream = File.Create(Path.Combine(outDir, "report.json"));
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("seam", seamName);
            json.WriteString("corpusManifestSha256", manifestHash);
            json.WriteStartObject("oracles");
            foreach (KeyValuePair<string, string> v in tools.Versions)
            {
                json.WriteString(v.Key, v.Value);
            }

            json.WriteEndObject();
            json.WriteStartArray("gates");
            foreach (Gate g in gates)
            {
                json.WriteStartObject();
                json.WriteString("name", g.Name);
                if (g.Needs is { } need && !seam.Has(need))
                {
                    json.WriteString("status", "pending");
                    json.WriteString("waitingOn", need.ToString());
                    json.WriteEndObject();
                    summary.AppendLine($"{g.Name,-22} pending ({need})");
                    continue;
                }

                List<Case> cases = g.MergeCases ? mergeCases : textCases;
                var results = cases.Where(c => done.ContainsKey(g.Name + "|" + c.Id)).Select(c => (c.Id, O: done[g.Name + "|" + c.Id])).ToList();
                int pass = results.Count(r => r.O.Status == Status.Pass);
                int fail = results.Count(r => r.O.Status == Status.Fail);
                int skip = results.Count(r => r.O.Status == Status.Skip);
                anyFailure |= fail > 0;
                json.WriteString("status", fail > 0 ? "fail" : "pass");
                json.WriteNumber("cases", cases.Count);
                json.WriteNumber("passed", pass);
                json.WriteNumber("failed", fail);
                json.WriteNumber("skipped", skip);
                json.WriteStartArray("failures");
                foreach ((string id, Outcome o) in results.Where(r => r.O.Status == Status.Fail).Take(MaxListed))
                {
                    json.WriteStartObject();
                    json.WriteString("case", id);
                    json.WriteString("detail", o.Detail);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteStartArray("skips");
                foreach ((string id, Outcome o) in results.Where(r => r.O.Status == Status.Skip).Take(MaxListed))
                {
                    json.WriteStartObject();
                    json.WriteString("case", id);
                    json.WriteString("detail", o.Detail);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();

                summary.AppendLine($"{g.Name,-22} cases {cases.Count,6}  pass {pass,6}  fail {fail,6}  skip {skip,4}");
                foreach ((string id, Outcome o) in results.Where(r => r.O.Status == Status.Fail).Take(3))
                {
                    summary.AppendLine($"    {id}: {o.Detail}");
                }
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary.ToString());
        Console.Write(summary.ToString());
        return anyFailure ? 1 : 0;
    }

    private static string Full(string path) => Path.GetFullPath(path.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/')) : path);

    private static Dictionary<string, string> Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("unexpected argument " + args[i]);
            }

            string key = args[i][2..];
            if (key == "fresh")
            {
                map[key] = "1";
            }
            else
            {
                map[key] = args[++i];
            }
        }

        return map;
    }
}
