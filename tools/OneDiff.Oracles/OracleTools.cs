using System.Text;

namespace OneDiff.Oracles;

/// <summary>The oracle binaries, found under one install root.</summary>
internal sealed class OracleTools
{
    private OracleTools(string root)
    {
        string bin = Path.Combine(root, "env", "bin");
        Git = Path.Combine(bin, "git");
        Diff = Path.Combine(bin, "diff");
        Diff3 = Path.Combine(bin, "diff3");
        Patch = Path.Combine(bin, "patch");
        Python = Path.Combine(root, "dmp-venv", "bin", "python");
        DmpHelper = Path.Combine(AppContext.BaseDirectory, "dmp_oracle.py");
    }

    public string Git { get; }

    public string Diff { get; }

    public string Diff3 { get; }

    public string Patch { get; }

    public string Python { get; }

    public string DmpHelper { get; }

    /// <summary>First line of each tool's own version output, in a fixed order.</summary>
    public SortedDictionary<string, string> Versions { get; } = new(StringComparer.Ordinal);

    public static async Task<OracleTools> OpenAsync(string root)
    {
        var tools = new OracleTools(root);
        foreach ((string name, string exe) in new[] { ("git", tools.Git), ("diff", tools.Diff), ("diff3", tools.Diff3), ("patch", tools.Patch), ("python", tools.Python) })
        {
            if (!File.Exists(exe))
            {
                throw new FileNotFoundException($"oracle '{name}' not found; run tools/install-oracles.sh (looked for {exe})");
            }
        }

        tools.Versions["git"] = FirstLine(await Proc.RunAsync(tools.Git, ["--version"], null, TimeSpan.FromSeconds(30)));
        tools.Versions["diff"] = FirstLine(await Proc.RunAsync(tools.Diff, ["--version"], null, TimeSpan.FromSeconds(30)));
        tools.Versions["diff3"] = FirstLine(await Proc.RunAsync(tools.Diff3, ["--version"], null, TimeSpan.FromSeconds(30)));
        tools.Versions["patch"] = FirstLine(await Proc.RunAsync(tools.Patch, ["--version"], null, TimeSpan.FromSeconds(30)));
        ProcResult dmp = await Proc.RunAsync(tools.Python, [tools.DmpHelper, "version"], null, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        tools.Versions["diff-match-patch"] = FirstLine(dmp);
        return tools;
    }

    private static string FirstLine(ProcResult r)
    {
        string text = Encoding.UTF8.GetString(r.Stdout);
        int nl = text.IndexOf('\n', StringComparison.Ordinal);
        return (nl < 0 ? text : text[..nl]).Trim();
    }
}
