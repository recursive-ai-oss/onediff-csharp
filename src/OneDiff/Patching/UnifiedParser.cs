using System.Diagnostics.CodeAnalysis;

namespace OneDiff.Patching;

/// <summary>
/// Reads unified diff text. Every number is checked before use, nothing is allocated from a number in the input (hunk lines are
/// collected as they are read), and the work is linear in the input.
/// </summary>
internal static class UnifiedParser
{
    // Lines git writes before ---/+++ for one file; kept verbatim as the extended header (git's diff documentation, "Generating patch text").
    private static readonly string[] ExtendedPrefixes =
    [
        "diff --git ", "diff -", "index ", "old mode ", "new mode ", "deleted file mode ", "new file mode ",
        "copy from ", "copy to ", "rename from ", "rename to ", "similarity index ", "dissimilarity index ",
    ];

    public static bool TryParse(string text, [NotNullWhen(true)] out UnifiedPatch? patch, out PatchParseError error)
    {
        patch = null;
        error = default;
        if (text.Length > UnifiedPatch.MaxInputLength)
        {
            error = new PatchParseError(PatchErrorKind.TooLarge, 1, 0, "The patch text is too large.");
            return false;
        }

        var reader = new LineReader(text);
        var files = new List<UnifiedFilePatch>();
        var pendingHeader = new List<string>();

        while (reader.HasLine)
        {
            ReadOnlySpan<char> line = reader.Current;
            if (StartsWith(line, "--- ") && reader.HasNext && StartsWith(reader.Next, "+++ "))
            {
                ParseName(line[4..], out string oldName, out string? oldStamp);
                reader.Advance();
                ParseName(reader.Current[4..], out string newName, out string? newStamp);
                reader.Advance();

                var hunks = new List<UnifiedHunk>();
                while (reader.HasLine && StartsWith(reader.Current, "@@"))
                {
                    if (!TryParseHunk(ref reader, out UnifiedHunk? hunk, out error))
                    {
                        return false;
                    }

                    hunks.Add(hunk);
                }

                files.Add(new UnifiedFilePatch(oldName, newName, hunks, oldStamp, newStamp, pendingHeader));
                pendingHeader.Clear();
                continue;
            }

            if (StartsWith(line, "--- "))
            {
                error = new PatchParseError(PatchErrorKind.MissingNewFileHeader, reader.LineNumber, reader.Offset, "A '---' line is not followed by a '+++' line.");
                return false;
            }

            if (StartsWith(line, "@@"))
            {
                error = new PatchParseError(PatchErrorKind.UnexpectedLine, reader.LineNumber, reader.Offset, "A hunk header appears outside a file's hunks.");
                return false;
            }

            if (StartsWithAny(line, ExtendedPrefixes))
            {
                // A new "diff" line starts a new file's header; anything gathered for a file that never got ---/+++ is dropped.
                if (StartsWith(line, "diff "))
                {
                    pendingHeader.Clear();
                }

                pendingHeader.Add(TrimCr(line).ToString());
            }

            reader.Advance();
        }

        if (files.Count == 0 && !IsBlank(text))
        {
            error = new PatchParseError(PatchErrorKind.NoPatchFound, 1, 0, "No unified diff was found in the text.");
            return false;
        }

        patch = new UnifiedPatch(files);
        return true;
    }

    private static bool TryParseHunk(ref LineReader reader, [NotNullWhen(true)] out UnifiedHunk? hunk, out PatchParseError error)
    {
        hunk = null;
        int headerLine = reader.LineNumber;
        int headerOffset = reader.Offset;
        ReadOnlySpan<char> header = TrimCr(reader.Current);
        if (!TryParseHeader(header, out int oldStart, out int oldCount, out int newStart, out int newCount, out string? section, out PatchErrorKind kind))
        {
            string message = kind == PatchErrorKind.NumberTooLarge ? "A number in the hunk header is too large." : "The hunk header is malformed.";
            error = new PatchParseError(kind, headerLine, headerOffset, message);
            return false;
        }

        reader.Advance();
        var lines = new List<UnifiedLine>();
        int seenOld = 0;
        int seenNew = 0;
        while (seenOld < oldCount || seenNew < newCount)
        {
            if (!reader.HasLine)
            {
                error = new PatchParseError(PatchErrorKind.TruncatedHunk, reader.LineNumber, reader.Offset, "The patch ends inside a hunk.");
                return false;
            }

            ReadOnlySpan<char> raw = reader.CurrentWithTerminator;
            char prefix = raw.Length == 0 || raw[0] == '\n' ? ' ' : raw[0];
            UnifiedLineKind lineKind;
            ReadOnlySpan<char> body;
            if (raw.Length == 0 || raw[0] == '\n')
            {
                // Some tools strip the single space from an empty context line; the patch manual says to accept it.
                lineKind = UnifiedLineKind.Context;
                body = raw;
            }
            else
            {
                switch (prefix)
                {
                    case ' ':
                        lineKind = UnifiedLineKind.Context;
                        break;
                    case '-':
                        lineKind = UnifiedLineKind.Delete;
                        break;
                    case '+':
                        lineKind = UnifiedLineKind.Insert;
                        break;
                    default:
                        error = new PatchParseError(PatchErrorKind.UnexpectedLine, reader.LineNumber, reader.Offset, "A hunk line starts with something other than a space, '-' or '+'.");
                        return false;
                }

                body = raw[1..];
            }

            if (lineKind != UnifiedLineKind.Insert)
            {
                seenOld++;
            }

            if (lineKind != UnifiedLineKind.Delete)
            {
                seenNew++;
            }

            if (seenOld > oldCount || seenNew > newCount)
            {
                error = new PatchParseError(PatchErrorKind.UnexpectedLine, reader.LineNumber, reader.Offset, "The hunk has more lines than its header announces.");
                return false;
            }

            reader.Advance();

            // "\ No newline at end of file" says the line before has no terminator.
            string lineText = body.ToString();
            if (reader.HasLine && reader.Current.Length > 0 && reader.Current[0] == '\\')
            {
                if (lineText.EndsWith('\n'))
                {
                    lineText = lineText[..^1];
                }

                reader.Advance();
            }

            lines.Add(new UnifiedLine(lineKind, lineText));
        }

        // A marker may also follow the last line when the counts are already satisfied; it was consumed above.
        if ((oldStart == 0 && oldCount > 0) || (newStart == 0 && newCount > 0))
        {
            error = new PatchParseError(PatchErrorKind.InvalidHunkHeader, headerLine, headerOffset, "A hunk start of zero needs a count of zero.");
            return false;
        }

        hunk = new UnifiedHunk(oldStart, newStart, lines, section);
        error = default;
        return true;
    }

    // "@@ -a[,b] +c[,d] @@[ section]" (GNU diffutils manual, "Detailed Unified"). A missing count is one.
    private static bool TryParseHeader(ReadOnlySpan<char> h, out int oldStart, out int oldCount, out int newStart, out int newCount, out string? section, out PatchErrorKind kind)
    {
        oldStart = oldCount = newStart = newCount = 0;
        section = null;
        kind = PatchErrorKind.InvalidHunkHeader;
        int p = 4; // past "@@ -"
        if (!ReadRange(h, ref p, out oldStart, out oldCount, ref kind))
        {
            return false;
        }

        if (p + 1 >= h.Length || h[p] != ' ' || h[p + 1] != '+')
        {
            return false;
        }

        p += 2;
        if (!ReadRange(h, ref p, out newStart, out newCount, ref kind))
        {
            return false;
        }

        if (p + 1 >= h.Length || h[p] != ' ' || h[p + 1] != '@')
        {
            return false;
        }

        p += 2;
        if (p >= h.Length || h[p] != '@')
        {
            return false;
        }

        p++;
        if (p < h.Length)
        {
            if (h[p] != ' ')
            {
                return false;
            }

            section = h[(p + 1)..].ToString();
        }

        return true;
    }

    private static bool ReadRange(ReadOnlySpan<char> h, ref int p, out int start, out int count, ref PatchErrorKind kind)
    {
        count = 1;
        if (!ReadNumber(h, ref p, out start, ref kind))
        {
            return false;
        }

        if (p < h.Length && h[p] == ',')
        {
            p++;
            if (!ReadNumber(h, ref p, out count, ref kind))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReadNumber(ReadOnlySpan<char> h, ref int p, out int value, ref PatchErrorKind kind)
    {
        value = 0;
        int begin = p;
        long acc = 0;
        while (p < h.Length && h[p] is >= '0' and <= '9')
        {
            acc = (acc * 10) + (h[p] - '0');
            if (acc > int.MaxValue / 2)
            {
                // Half the range keeps start + count sums inside an int for every later computation.
                kind = PatchErrorKind.NumberTooLarge;
                return false;
            }

            p++;
        }

        if (p == begin)
        {
            return false;
        }

        value = (int)acc;
        return true;
    }

    // "--- name[<TAB>timestamp]"; git quotes names that contain unusual bytes (git's diff documentation, "Generating patch text").
    private static void ParseName(ReadOnlySpan<char> rest, out string name, out string? timestamp)
    {
        rest = TrimCr(rest);
        timestamp = null;
        int tab = rest.IndexOf('\t');
        ReadOnlySpan<char> n = rest;
        if (tab >= 0)
        {
            n = rest[..tab];
            timestamp = rest[(tab + 1)..].ToString();
        }

        name = n.Length >= 2 && n[0] == '"' && n[^1] == '"' ? Unquote(n[1..^1]) : n.ToString();
    }

    private static string Unquote(ReadOnlySpan<char> s)
    {
        var bytes = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                AppendUtf8(bytes, c);
                continue;
            }

            char e = s[++i];
            switch (e)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case 'v': bytes.Add(11); break;
                case >= '0' and <= '3' when i + 2 < s.Length && s[i + 1] is >= '0' and <= '7' && s[i + 2] is >= '0' and <= '7':
                    bytes.Add((byte)(((e - '0') << 6) | ((s[i + 1] - '0') << 3) | (s[i + 2] - '0')));
                    i += 2;
                    break;
                default: AppendUtf8(bytes, e); break;
            }
        }

        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void AppendUtf8(List<byte> bytes, char c)
    {
        if (c < 0x80)
        {
            bytes.Add((byte)c);
        }
        else
        {
            Span<byte> buf = stackalloc byte[4]; // one char encodes to at most 3 bytes (a lone surrogate becomes U+FFFD)
            int n = System.Text.Encoding.UTF8.GetBytes([c], buf);
            for (int k = 0; k < n; k++)
            {
                bytes.Add(buf[k]);
            }
        }
    }

    private static bool StartsWith(ReadOnlySpan<char> s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal);

    private static bool StartsWithAny(ReadOnlySpan<char> s, string[] prefixes)
    {
        foreach (string p in prefixes)
        {
            if (s.StartsWith(p, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlySpan<char> TrimCr(ReadOnlySpan<char> s) => s.Length > 0 && s[^1] == '\r' ? s[..^1] : s;

    private static bool IsBlank(string s)
    {
        foreach (char c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Walks the text line by line without copying; lines end at '\n' only.</summary>
    private ref struct LineReader
    {
        private readonly string text;
        private int start;
        private int end;
        private int next;

        public LineReader(string text)
        {
            this.text = text;
            LineNumber = 1;
            start = 0;
            Locate();
        }

        public int LineNumber { get; private set; }

        public readonly int Offset => start;

        public readonly bool HasLine => start < text.Length;

        /// <summary>The current line without its '\n'.</summary>
        public readonly ReadOnlySpan<char> Current => text.AsSpan(start, end - start);

        /// <summary>The current line with its '\n' when it has one. At the end of the text, the empty span.</summary>
        public readonly ReadOnlySpan<char> CurrentWithTerminator => text.AsSpan(start, next - start);

        public readonly bool HasNext => next < text.Length;

        public readonly ReadOnlySpan<char> Next
        {
            get
            {
                int nl = text.AsSpan(next).IndexOf('\n');
                int e = nl < 0 ? text.Length : next + nl;
                return text.AsSpan(next, e - next);
            }
        }

        public void Advance()
        {
            start = next;
            LineNumber++;
            Locate();
        }

        private void Locate()
        {
            if (start >= text.Length)
            {
                end = next = text.Length;
                return;
            }

            int nl = text.AsSpan(start).IndexOf('\n');
            if (nl < 0)
            {
                end = next = text.Length;
            }
            else
            {
                end = start + nl;
                next = end + 1;
            }
        }
    }
}
