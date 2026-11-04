using System.Diagnostics;

namespace OneDiff.Oracles;

/// <summary>Output of one child process.</summary>
internal sealed record ProcResult(int ExitCode, byte[] Stdout, byte[] Stderr, bool TimedOut)
{
    public string StdoutText => System.Text.Encoding.UTF8.GetString(Stdout);

    public string StderrText => System.Text.Encoding.UTF8.GetString(Stderr);
}

/// <summary>Runs oracle binaries. They are only ever executed, never inspected.</summary>
internal static class Proc
{
    public static async Task<ProcResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        string? workDir,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        // A neutral environment so the same inputs give the same bytes on every machine.
        info.Environment["LC_ALL"] = "C";
        info.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env is not null)
        {
            foreach (KeyValuePair<string, string> kv in env)
            {
                info.Environment[kv.Key] = kv.Value;
            }
        }

        using var process = new Process { StartInfo = info };
        process.Start();
        process.StandardInput.Close();
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        Task copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        Task copyErr = process.StandardError.BaseStream.CopyToAsync(stderr);

        using var cts = new CancellationTokenSource(timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(copyOut, copyErr).ConfigureAwait(false);
        return new ProcResult(timedOut ? -1 : process.ExitCode, stdout.ToArray(), stderr.ToArray(), timedOut);
    }

    /// <summary>Synchronous form for the self-check seam, which is called from synchronous code.</summary>
    public static ProcResult Run(string exe, IEnumerable<string> args, string? workDir, TimeSpan timeout, IReadOnlyDictionary<string, string>? env = null)
        => RunAsync(exe, args, workDir, timeout, env).GetAwaiter().GetResult();
}
