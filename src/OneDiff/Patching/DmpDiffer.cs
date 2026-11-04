using System.Text;
using OneDiff.Cleanup;
using OneDiff.Tokenization;

namespace OneDiff.Patching;

/// <summary>
/// The character-level diff the patch code works on. Common ends are trimmed, long texts are first compared line by line (so the
/// character search only sees the lines that changed), and the result is in UTF-16 units but never splits a surrogate pair.
/// </summary>
internal static class DmpDiffer
{
    // Below this many units on both sides the character search runs directly.
    private const int LineModeThreshold = 100;

    public static List<DmpDiff> Compute(string a, string b, CancellationToken cancellationToken = default)
    {
        var result = new List<DmpDiff>();
        Compute(a, b, true, result, cancellationToken);
        return result;
    }

    /// <summary>
    /// The diff <c>Make</c> builds patches from: <see cref="Compute(string, string, CancellationToken)"/> followed by semantic and then
    /// efficiency cleanup when there is more than one change to tidy (the order and the "more than an equality and one change" test follow
    /// the diff-match-patch wiki's description of making patches from two texts). The edit cost is 4, the wiki's default.
    /// </summary>
    public static List<DmpDiff> ComputeForPatch(string a, string b, CancellationToken cancellationToken = default)
    {
        List<DmpDiff> diffs = Compute(a, b, cancellationToken);
        return diffs.Count > 2 ? CleanUp(a, b, diffs) : diffs;
    }

    private static List<DmpDiff> CleanUp(string a, string b, List<DmpDiff> diffs)
    {
        // The diff's new side, and its script counted in code points (the cleanup's character tokens).
        var newText = new StringBuilder(b.Length);
        var builder = new EditScriptBuilder();
        foreach (DmpDiff d in diffs)
        {
            int n = CodePointCount(d.Text);
            switch (d.Operation)
            {
                case DmpOperation.Equal: builder.Equal(n); newText.Append(d.Text); break;
                case DmpOperation.Delete: builder.Delete(n); break;
                default: builder.Insert(n); newText.Append(d.Text); break;
            }
        }

        string oldString = a;
        string newString = newText.ToString();
        TokenRange[]? oldRanges = HasSurrogate(oldString) ? Tokenizer.Tokenize(oldString, TokenKind.Character) : null;
        TokenRange[]? newRanges = HasSurrogate(newString) ? Tokenizer.Tokenize(newString, TokenKind.Character) : null;
        var textDiff = new TextDiff(oldString, newString, TokenKind.Character, oldRanges, newRanges, builder.ToEditScript());
        TextDiff cleaned = DiffCleanup.Efficiency(DiffCleanup.Semantic(textDiff), 4);
        if (ReferenceEquals(cleaned.Script, textDiff.Script))
        {
            return diffs;
        }

        var result = new List<DmpDiff>(cleaned.Script.Count);
        foreach (Edit e in cleaned.Script)
        {
            switch (e.Kind)
            {
                case EditKind.Equal: Add(result, DmpOperation.Equal, cleaned.OldSpan(e).ToString()); break;
                case EditKind.Delete: Add(result, DmpOperation.Delete, cleaned.OldSpan(e).ToString()); break;
                default: Add(result, DmpOperation.Insert, cleaned.NewSpan(e).ToString()); break;
            }
        }

        return result;
    }

    private static bool HasSurrogate(string s) => s.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF');

    private static int CodePointCount(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }

            n++;
        }

        return n;
    }

    private static void Compute(string a, string b, bool lineMode, List<DmpDiff> output, CancellationToken ct)
    {
        int prefix = CommonPrefix(a, b);
        int suffix = CommonSuffix(a, b, prefix);
        Add(output, DmpOperation.Equal, a.Substring(0, prefix));
        string ma = a.Substring(prefix, a.Length - prefix - suffix);
        string mb = b.Substring(prefix, b.Length - prefix - suffix);

        if (ma.Length == 0)
        {
            Add(output, DmpOperation.Insert, mb);
        }
        else if (mb.Length == 0)
        {
            Add(output, DmpOperation.Delete, ma);
        }
        else if (lineMode && ma.Length > LineModeThreshold && mb.Length > LineModeThreshold && TryLineMode(ma, mb, output, ct))
        {
            // done
        }
        else
        {
            CharMode(ma, mb, output, ct);
        }

        Add(output, DmpOperation.Equal, a.Substring(a.Length - suffix));
    }

    private static bool TryLineMode(string a, string b, List<DmpDiff> output, CancellationToken ct)
    {
        List<string> la = PatchLines.Split(a);
        List<string> lb = PatchLines.Split(b);
        EditScript script = Diff.Sequences<string>(la.ToArray(), lb.ToArray(), DiffAlgorithm.Myers, StringComparer.Ordinal, ct);
        bool anyEqual = false;
        foreach (Edit e in script.Edits)
        {
            if (e.Kind == EditKind.Equal)
            {
                anyEqual = true;
                break;
            }
        }

        if (!anyEqual)
        {
            return false;
        }

        // Replace blocks (a delete followed by an insert) get a character diff of their own.
        ReadOnlySpan<Edit> edits = script.Edits;
        for (int i = 0; i < edits.Length; i++)
        {
            Edit e = edits[i];
            switch (e.Kind)
            {
                case EditKind.Equal:
                    Add(output, DmpOperation.Equal, Join(la, e.OldStart, e.OldLength));
                    break;
                case EditKind.Delete:
                    if (i + 1 < edits.Length && edits[i + 1].Kind == EditKind.Insert)
                    {
                        Compute(Join(la, e.OldStart, e.OldLength), Join(lb, edits[i + 1].NewStart, edits[i + 1].NewLength), false, output, ct);
                        i++;
                    }
                    else
                    {
                        Add(output, DmpOperation.Delete, Join(la, e.OldStart, e.OldLength));
                    }

                    break;
                default:
                    Add(output, DmpOperation.Insert, Join(lb, e.NewStart, e.NewLength));
                    break;
            }
        }

        return true;
    }

    private static void CharMode(string a, string b, List<DmpDiff> output, CancellationToken ct)
    {
        int[] ta = CodePoints(a, out int[] unitsA);
        int[] tb = CodePoints(b, out int[] unitsB);
        EditScript script = Diff.Sequences<int>(ta, tb, DiffAlgorithm.Myers, null, ct);
        // Token index -> unit offset, so each run can be cut from the strings.
        int[] offA = Offsets(unitsA);
        int[] offB = Offsets(unitsB);
        foreach (Edit e in script.Edits)
        {
            switch (e.Kind)
            {
                case EditKind.Equal:
                    Add(output, DmpOperation.Equal, a.Substring(offA[e.OldStart], offA[e.OldEnd] - offA[e.OldStart]));
                    break;
                case EditKind.Delete:
                    Add(output, DmpOperation.Delete, a.Substring(offA[e.OldStart], offA[e.OldEnd] - offA[e.OldStart]));
                    break;
                default:
                    Add(output, DmpOperation.Insert, b.Substring(offB[e.NewStart], offB[e.NewEnd] - offB[e.NewStart]));
                    break;
            }
        }
    }

    // One token per code point; an unpaired surrogate is a token of its own.
    private static int[] CodePoints(string s, out int[] units)
    {
        var tokens = new List<int>(s.Length);
        var lens = new List<int>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                tokens.Add(char.ConvertToUtf32(c, s[i + 1]));
                lens.Add(2);
                i++;
            }
            else
            {
                tokens.Add(c);
                lens.Add(1);
            }
        }

        units = lens.ToArray();
        return tokens.ToArray();
    }

    private static int[] Offsets(int[] lengths)
    {
        var off = new int[lengths.Length + 1];
        for (int i = 0; i < lengths.Length; i++)
        {
            off[i + 1] = off[i] + lengths[i];
        }

        return off;
    }

    private static string Join(List<string> lines, int start, int count)
    {
        if (count == 1)
        {
            return lines[start];
        }

        return string.Concat(lines.GetRange(start, count));
    }

    private static int CommonPrefix(string a, string b)
    {
        int n = a.AsSpan().CommonPrefixLength(b);
        // A prefix that ends after a high surrogate would split a pair (its low half is not common).
        if (n > 0 && char.IsHighSurrogate(a[n - 1]))
        {
            n--;
        }

        return n;
    }

    private static int CommonSuffix(string a, string b, int prefix)
    {
        int max = Math.Min(a.Length, b.Length) - prefix;
        int n = 0;
        while (n < max && a[a.Length - 1 - n] == b[b.Length - 1 - n])
        {
            n++;
        }

        // Do not start inside a surrogate pair.
        if (n > 0 && char.IsLowSurrogate(a[a.Length - n]))
        {
            n--;
        }

        return n;
    }

    private static void Add(List<DmpDiff> output, DmpOperation op, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        int last = output.Count - 1;
        if (last >= 0 && output[last].Operation == op)
        {
            output[last] = new DmpDiff(op, output[last].Text + text);
            return;
        }

        // Deletions come before insertions between two equalities.
        if (op == DmpOperation.Delete && last >= 0 && output[last].Operation == DmpOperation.Insert)
        {
            if (last >= 1 && output[last - 1].Operation == DmpOperation.Delete)
            {
                output[last - 1] = new DmpDiff(op, output[last - 1].Text + text);
            }
            else
            {
                output.Insert(last, new DmpDiff(op, text));
            }

            return;
        }

        output.Add(new DmpDiff(op, text));
    }
}
