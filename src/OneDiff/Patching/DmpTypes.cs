using System.Text;

namespace OneDiff.Patching;

/// <summary>What a diff-match-patch diff does.</summary>
public enum DmpOperation : byte
{
    /// <summary>Text present in both versions.</summary>
    Equal = 0,

    /// <summary>Text present in the old version only.</summary>
    Delete = 1,

    /// <summary>Text present in the new version only.</summary>
    Insert = 2,
}

/// <summary>One run of a character-level diff.</summary>
/// <param name="Operation">What the run does.</param>
/// <param name="Text">The text of the run.</param>
public readonly record struct DmpDiff(DmpOperation Operation, string Text);

/// <summary>
/// One patch of diff-match-patch patch text: a run of diffs with its place in both texts. Offsets and lengths count UTF-16 code units.
/// </summary>
public sealed class DmpPatch
{
    internal int Origin { get; set; }

    internal DmpPatch()
    {
    }

    /// <summary>Creates a patch.</summary>
    /// <param name="start1">Zero-based offset in the old text where the patch starts.</param>
    /// <param name="start2">Zero-based offset in the new text where the patch starts.</param>
    /// <param name="diffs">The diffs; lengths are derived from them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diffs"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A start is negative.</exception>
    public DmpPatch(int start1, int start2, IEnumerable<DmpDiff> diffs)
    {
        ArgumentNullException.ThrowIfNull(diffs);
        ArgumentOutOfRangeException.ThrowIfNegative(start1);
        ArgumentOutOfRangeException.ThrowIfNegative(start2);
        Start1 = start1;
        Start2 = start2;
        MutableDiffs.AddRange(diffs);
        foreach (DmpDiff d in MutableDiffs)
        {
            ArgumentNullException.ThrowIfNull(d.Text);
            if (d.Operation != DmpOperation.Insert)
            {
                Length1 += d.Text.Length;
            }

            if (d.Operation != DmpOperation.Delete)
            {
                Length2 += d.Text.Length;
            }
        }
    }

    internal List<DmpDiff> MutableDiffs { get; } = [];

    /// <summary>The diffs in order.</summary>
    public IReadOnlyList<DmpDiff> Diffs => MutableDiffs;

    /// <summary>Zero-based offset in the old text.</summary>
    public int Start1 { get; internal set; }

    /// <summary>Zero-based offset in the new text.</summary>
    public int Start2 { get; internal set; }

    /// <summary>Length of the old text the patch covers (equal and deleted text).</summary>
    public int Length1 { get; internal set; }

    /// <summary>Length of the new text the patch covers (equal and inserted text).</summary>
    public int Length2 { get; internal set; }

    /// <summary>The old text the patch expects: equal and deleted runs joined.</summary>
    public string OldText()
    {
        var sb = new StringBuilder(Length1);
        foreach (DmpDiff d in MutableDiffs)
        {
            if (d.Operation != DmpOperation.Insert)
            {
                sb.Append(d.Text);
            }
        }

        return sb.ToString();
    }

    /// <summary>The new text the patch leaves: equal and inserted runs joined.</summary>
    public string NewText()
    {
        var sb = new StringBuilder(Length2);
        foreach (DmpDiff d in MutableDiffs)
        {
            if (d.Operation != DmpOperation.Delete)
            {
                sb.Append(d.Text);
            }
        }

        return sb.ToString();
    }

    internal DmpPatch Clone()
    {
        var c = new DmpPatch { Start1 = Start1, Start2 = Start2, Length1 = Length1, Length2 = Length2, Origin = Origin };
        c.MutableDiffs.AddRange(MutableDiffs);
        return c;
    }

    // Header coordinates, as the wiki's "Unidiff" page describes: one-based starts, a length of one is left out, and a length of zero
    // keeps the start unadjusted (it names the place between characters).
    internal void AppendTo(StringBuilder sb)
    {
        sb.Append("@@ -");
        AppendCoords(sb, Start1, Length1);
        sb.Append(" +");
        AppendCoords(sb, Start2, Length2);
        sb.Append(" @@\n");
        foreach (DmpDiff d in MutableDiffs)
        {
            sb.Append(d.Operation switch { DmpOperation.Insert => '+', DmpOperation.Delete => '-', _ => ' ' });
            DmpText.Encode(sb, d.Text);
            sb.Append('\n');
        }
    }

    private static void AppendCoords(StringBuilder sb, int start, int length)
    {
        if (length == 0)
        {
            sb.Append(start).Append(",0");
        }
        else if (length == 1)
        {
            sb.Append(start + 1);
        }
        else
        {
            sb.Append(start + 1).Append(',').Append(length);
        }
    }
}

/// <summary>What happened to one patch.</summary>
public enum DmpPatchOutcome
{
    /// <summary>The patch was applied (possibly away from its stated place, or to text that had drifted).</summary>
    Applied,

    /// <summary>Its text could not be located well enough, so it was skipped.</summary>
    NotFound,

    /// <summary>It was located, but the text there differs from what it expects by more than the delete threshold allows.</summary>
    TooDifferent,
}

/// <summary>The outcome of one patch.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Location">Offset in the patched text where the patch's text was found, or -1.</param>
/// <param name="Drift">How far from the expected place it was found (zero when not applied).</param>
public readonly record struct DmpPatchResult(DmpPatchOutcome Outcome, int Location, int Drift)
{
    /// <summary>True when the patch was applied.</summary>
    public bool Applied => Outcome == DmpPatchOutcome.Applied;
}

/// <summary>The result of applying diff-match-patch patches. A patch that cannot be placed is reported here, never thrown.</summary>
public sealed class DmpApplyResult
{
    internal DmpApplyResult(string text, DmpPatchResult[] patches)
    {
        Text = text;
        Patches = patches;
    }

    /// <summary>The patched text. Patches that failed leave their place unchanged.</summary>
    public string Text { get; }

    /// <summary>One entry per patch passed in, in order. Patches are counted as given, before any are split for matching.</summary>
    public IReadOnlyList<DmpPatchResult> Patches { get; }

    /// <summary>True when every patch was applied.</summary>
    public bool Success => Patches.All(p => p.Applied);
}
