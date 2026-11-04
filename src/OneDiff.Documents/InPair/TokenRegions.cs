using OneDiff.Algorithms;

namespace OneDiff.Documents.InPair;

/// <summary>A changed stretch between two token sequences: old tokens [OldFrom, OldTo) became new tokens [NewFrom, NewTo).</summary>
internal readonly record struct TokenRegion(int OldFrom, int OldTo, int NewFrom, int NewTo);

/// <summary>
/// Turns the script of a token diff into changed regions and tidies them so they read as a person would mark them up:
/// insertions and deletions slide to word edges, and small equal stretches between larger edits are absorbed.
/// </summary>
internal static class TokenRegions
{
    private const int MaxMergePasses = 8;

    /// <summary>Diffs two sequences and returns the cleaned regions in order.</summary>
    public static List<TokenRegion> Compute(TokenSeq o, TokenSeq n, DiffAlgorithm algorithm, CancellationToken ct)
    {
        EditScript script = algorithm switch
        {
            DiffAlgorithm.Patience => PatienceDiff.Compute(o.Ids, n.Ids, ct),
            DiffAlgorithm.Histogram => HistogramDiff.Compute(o.Ids, n.Ids, ct),
            _ => MyersDiff.Compute(o.Ids, n.Ids, ct),
        };
        List<TokenRegion> regions = FromScript(script);
        Shift(regions, o, n);
        Merge(regions, o, n);
        return regions;
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

            int of = e.OldStart, ot = e.OldEnd, nf = e.NewStart, nt = e.NewEnd;
            if (e.Kind == EditKind.Delete && i + 1 < script.Count && script[i + 1].Kind == EditKind.Insert)
            {
                nt = script[i + 1].NewEnd;
                i++;
            }

            regions.Add(new TokenRegion(of, ot, nf, nt));
        }

        return regions;
    }

    /// <summary>
    /// Slides a pure insertion or deletion along the equal tokens around it, where the result is the same text, to the place
    /// that reads best: starting at a word and ending after its trailing space. This is the lossless shift: the new text and
    /// the old text are unchanged, only where the edit is drawn moves.
    /// </summary>
    private static void Shift(List<TokenRegion> regions, TokenSeq o, TokenSeq n)
    {
        for (int r = 0; r < regions.Count; r++)
        {
            TokenRegion g = regions[r];
            bool insert = g.OldFrom == g.OldTo, delete = g.NewFrom == g.NewTo;
            if (insert == delete)
            {
                continue;
            }

            // The tokens that carry the edit, and the room before and after it.
            TokenSeq t = insert ? n : o;
            int from = insert ? g.NewFrom : g.OldFrom, to = insert ? g.NewTo : g.OldTo;
            int lo = r > 0 ? (insert ? regions[r - 1].NewTo : regions[r - 1].OldTo) : 0;
            int hi = r + 1 < regions.Count ? (insert ? regions[r + 1].NewFrom : regions[r + 1].OldFrom) : t.Count;
            int[] ids = t.Ids;

            int back = 0;
            while (from - back > lo && ids[from - back - 1] == ids[to - back - 1])
            {
                back++;
            }

            int forward = 0;
            while (to + forward < hi && ids[from + forward] == ids[to + forward])
            {
                forward++;
            }

            if (back == 0 && forward == 0)
            {
                continue;
            }

            int best = 0, bestScore = int.MinValue;
            for (int d = -back; d <= forward; d++)
            {
                int score = Score(t, from + d, to + d);
                if (score >= bestScore)
                {
                    bestScore = score;
                    best = d;
                }
            }

            regions[r] = new TokenRegion(g.OldFrom + best, g.OldTo + best, g.NewFrom + best, g.NewTo + best);
        }
    }

    /// <summary>How well a stretch of tokens reads as an edit: it should start at a word and end with its space.</summary>
    private static int Score(TokenSeq t, int from, int to)
    {
        int score = 0;
        if (!t.IsSpace(from) && (from == 0 || !IsWordLike(t, from - 1)))
        {
            score += 4;
        }

        if (t.IsSpace(from))
        {
            score -= 2;
        }

        if (to == t.Count || t.IsSpace(to - 1))
        {
            score += 2;
        }

        if (to < t.Count && IsWordLike(t, to - 1) && IsWordLike(t, to))
        {
            score -= 4;
        }

        return score;
    }

    private static bool IsWordLike(TokenSeq t, int i) => t.WordBefore[i + 1] > t.WordBefore[i];

    /// <summary>
    /// Absorbs equal stretches that are smaller than the edits on both sides (Myers-style scripts pick up a stray "the" or ", " as
    /// an anchor between two rewritten phrases, which shreds the redline into alternating pieces). A stretch of no letters or digits
    /// of at most three characters goes the same way. Repeats until nothing changes, within a fixed number of passes; skipping a
    /// merge only leaves finer edits, never a wrong one.
    /// </summary>
    public static void Merge(List<TokenRegion> regions, TokenSeq o, TokenSeq n)
    {
        for (int pass = 0; pass < MaxMergePasses && regions.Count > 1; pass++)
        {
            bool changed = false;
            var next = new List<TokenRegion>(regions.Count);
            TokenRegion cur = regions[0];
            for (int i = 1; i < regions.Count; i++)
            {
                TokenRegion nx = regions[i];
                if (Absorbs(cur, nx, o, n))
                {
                    cur = new TokenRegion(cur.OldFrom, nx.OldTo, cur.NewFrom, nx.NewTo);
                    changed = true;
                }
                else
                {
                    next.Add(cur);
                    cur = nx;
                }
            }

            next.Add(cur);
            regions.Clear();
            regions.AddRange(next);
            if (!changed)
            {
                break;
            }
        }
    }

    private static bool Absorbs(TokenRegion a, TokenRegion b, TokenSeq o, TokenSeq n)
    {
        int eqWeight = o.NonSpaceBefore[b.OldFrom] - o.NonSpaceBefore[a.OldTo];
        if (eqWeight == 0)
        {
            return true;
        }

        int sizeA = Math.Max(o.NonSpaceBefore[a.OldTo] - o.NonSpaceBefore[a.OldFrom], n.NonSpaceBefore[a.NewTo] - n.NonSpaceBefore[a.NewFrom]);
        int sizeB = Math.Max(o.NonSpaceBefore[b.OldTo] - o.NonSpaceBefore[b.OldFrom], n.NonSpaceBefore[b.NewTo] - n.NonSpaceBefore[b.NewFrom]);
        if (eqWeight <= sizeA && eqWeight <= sizeB)
        {
            return true;
        }

        bool noWords = o.WordBefore[b.OldFrom] == o.WordBefore[a.OldTo];
        return noWords && eqWeight <= 3;
    }
}
