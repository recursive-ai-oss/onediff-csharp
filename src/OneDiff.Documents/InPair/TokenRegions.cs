using OneDiff.Algorithms;
using OneDiff.Cleanup;
using OneDiff.Tokenization;

namespace OneDiff.Documents.InPair;

/// <summary>A changed stretch between two token sequences: old tokens [OldFrom, OldTo) became new tokens [NewFrom, NewTo).</summary>
internal readonly record struct TokenRegion(int OldFrom, int OldTo, int NewFrom, int NewTo);

/// <summary>
/// Diffs two UAX #29 word sequences, tidies the script with the text core's semantic cleanup (small equal stretches between larger
/// edits are absorbed, lone edits slide to word edges) and returns the changed regions.
/// </summary>
internal static class TokenRegions
{
    /// <summary>Diffs two sequences and returns the cleaned regions in order.</summary>
    public static List<TokenRegion> Compute(TokenSeq o, TokenSeq n, DiffAlgorithm algorithm, CancellationToken ct)
    {
        EditScript script = algorithm switch
        {
            DiffAlgorithm.Patience => PatienceDiff.Compute(o.Ids, n.Ids, ct),
            DiffAlgorithm.Histogram => HistogramDiff.Compute(o.Ids, n.Ids, ct),
            _ => MyersDiff.Compute(o.Ids, n.Ids, ct),
        };
        TextDiff diff = TextDiff.Create(o.Text, n.Text, TokenKind.Word, o.Ranges, n.Ranges, script);
        return FromScript(DiffCleanup.Semantic(diff).Script);
    }

    /// <summary>A delete directly followed by an insert is one region; the canonical script has nothing else adjacent.</summary>
    public static List<TokenRegion> FromScript(EditScript script)
    {
        var regions = new List<TokenRegion>();
        for (int i = 0; i < script.Count; i++)
        {
            Edit e = script[i];
            if (e.Kind == EditKind.Equal)
            {
                continue;
            }

            int nt = e.NewEnd;
            if (e.Kind == EditKind.Delete && i + 1 < script.Count && script[i + 1].Kind == EditKind.Insert)
            {
                nt = script[i + 1].NewEnd;
                i++;
            }

            regions.Add(new TokenRegion(e.OldStart, e.OldEnd, e.NewStart, nt));
        }

        return regions;
    }
}
