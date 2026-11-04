namespace OneDiff.Patching;

/// <summary>Cuts text into lines the way unified diffs count them.</summary>
public static class PatchLines
{
    /// <summary>
    /// Splits <paramref name="text"/> at every <c>'\n'</c>. Each element keeps its terminator, so joining the elements gives the
    /// text back byte for byte. A final line without a terminator is kept as it is. A carriage return is ordinary content:
    /// CRLF lines end in <c>"\r\n"</c> and a lone <c>'\r'</c> does not end a line (the unified format counts newlines only).
    /// Empty text gives no lines.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static List<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.AsSpan(start).IndexOf('\n');
            int end = nl < 0 ? text.Length : start + nl + 1;
            lines.Add(text.Substring(start, end - start));
            start = end;
        }

        return lines;
    }
}
