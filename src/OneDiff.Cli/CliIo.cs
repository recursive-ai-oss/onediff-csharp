using System.Text;

namespace OneDiff.Cli;

/// <summary>A problem with how the command was used or with its inputs; reported on stderr and ends the command with its "trouble" exit code.</summary>
internal sealed class CliException(string message) : Exception(message);

/// <summary>The three standard streams a command works with, so commands can run against buffers as well as the console.</summary>
internal sealed class CliIo(Stream stdin, Stream stdout, Stream stderr)
{
    private byte[]? stdinBytes;

    public Stream Stdout { get; } = stdout;

    public Stream Stderr { get; } = stderr;

    /// <summary>Reads a whole file, or standard input for <c>-</c> (which can be read once; later reads see it again).</summary>
    public byte[] Read(string path)
    {
        if (path == "-")
        {
            if (stdinBytes is null)
            {
                using var buffer = new MemoryStream();
                stdin.CopyTo(buffer);
                stdinBytes = buffer.ToArray();
            }

            return stdinBytes;
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"{path}: {e.Message}");
        }
    }

    /// <summary>Writes to a file, or to standard output when <paramref name="path"/> is null or <c>-</c>.</summary>
    public void Write(string? path, byte[] bytes)
    {
        if (path is null or "-")
        {
            Stdout.Write(bytes);
            Stdout.Flush();
            return;
        }

        try
        {
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"{path}: {e.Message}");
        }
    }

    public void Info(string line)
    {
        Stderr.Write(Encoding.UTF8.GetBytes(line + "\n"));
        Stderr.Flush();
    }
}

/// <summary>
/// Turns input bytes into text by looking at them: strict UTF-8 when every input is valid UTF-8, otherwise Latin-1 for all of them,
/// which maps each byte to one char and back so a legacy-encoded file passes through a line diff or merge unchanged.
/// </summary>
internal sealed class TextEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8 = new(false);

    private TextEncoding(Encoding encoding) => Encoding = encoding;

    public Encoding Encoding { get; }

    /// <summary>Decodes every input with one encoding, chosen by the content of all of them.</summary>
    public static (TextEncoding Encoding, string[] Texts) Decode(params byte[][] inputs)
    {
        try
        {
            var texts = new string[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                texts[i] = StrictUtf8.GetString(inputs[i]);
            }

            return (new TextEncoding(Utf8), texts);
        }
        catch (DecoderFallbackException)
        {
            return (new TextEncoding(Encoding.Latin1), inputs.Select(Encoding.Latin1.GetString).ToArray());
        }
    }

    /// <summary>A file with a NUL byte in its first 8000 bytes is treated as binary, as GNU diff does.</summary>
    public static bool IsBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0);

    public static byte[] Utf8Bytes(string text) => Utf8.GetBytes(text);

    public byte[] Bytes(string text) => Encoding.GetBytes(text);
}
