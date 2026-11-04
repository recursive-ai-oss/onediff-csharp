using System.Text;

namespace OneDiff.Patching;

public sealed partial class DmpPatcher
{
    /// <summary>
    /// Applies patches to <paramref name="text"/>, placing each where its old text fits best (see <see cref="Match"/>), and reports
    /// for each whether it was applied. Patches that cannot be placed are skipped; nothing is thrown for a conflict.
    /// </summary>
    /// <remarks>
    /// The procedure is the wiki's <c>patch_apply</c>: pad the text so patches at its ends have context; split patches longer than
    /// <see cref="MatchMaxBits"/> so each piece can be matched; for each patch, find its old text near where it expects it (shifted by
    /// the drift of the patches before it); apply it as a diff between the expected and the found text, so edits land correctly even
    /// when the surroundings changed. Cost is proportional to the text length times the number of patches.
    /// </remarks>
    /// <param name="patches">The patches to apply, in order.</param>
    /// <param name="text">The text to patch.</param>
    /// <param name="cancellationToken">Observed while diffing found against expected text.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public DmpApplyResult Apply(IReadOnlyList<DmpPatch> patches, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(text);
        if (patches.Count == 0)
        {
            return new DmpApplyResult(text, []);
        }

        var work = new List<DmpPatch>(patches.Count);
        for (int i = 0; i < patches.Count; i++)
        {
            DmpPatch c = patches[i].Clone();
            c.Origin = i;
            work.Add(c);
        }

        string padding = MakePadding();
        text = string.Concat(padding, text, padding);
        AddPadding(work, padding);
        SplitMax(work);

        var outcomes = new DmpPatchResult[patches.Count];
        for (int i = 0; i < outcomes.Length; i++)
        {
            outcomes[i] = new DmpPatchResult(DmpPatchOutcome.Applied, -1, 0);
        }

        // Patches are positioned in the text as it stands after the earlier ones; delta is how far reality has drifted from that.
        int delta = 0;
        bool codePoints = offsetUnit == DmpOffsetUnit.CodePoint;
        foreach (DmpPatch patch in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int expectedStart = patch.Start2 + delta; // in OffsetUnit
            int expected = codePoints ? UnitIndex(text, expectedStart) : expectedStart;
            string text1 = patch.OldText();
            int startLoc;
            int endLoc = -1;
            if (text1.Length > matchMaxBits)
            {
                // Too long for one search: find its first and last stretch and require them in order.
                startLoc = Match(text, text1.Substring(0, matchMaxBits), expected);
                if (startLoc != -1)
                {
                    endLoc = Match(text, text1.Substring(text1.Length - matchMaxBits), expected + text1.Length - matchMaxBits);
                    if (endLoc == -1 || startLoc >= endLoc)
                    {
                        startLoc = -1;
                    }
                }
            }
            else
            {
                startLoc = Match(text, text1, expected);
            }

            if (startLoc == -1)
            {
                Fail(outcomes, patch, DmpPatchOutcome.NotFound);
                delta -= Measure(patch.NewText()) - Measure(patch.OldText());
                continue;
            }

            delta = (codePoints ? CodePointIndex(text, startLoc) : startLoc) - expectedStart;
            int foundEnd = endLoc == -1 ? Math.Min(startLoc + text1.Length, text.Length) : Math.Min(endLoc + matchMaxBits, text.Length);
            string text2 = text.Substring(startLoc, foundEnd - startLoc);
            if (string.Equals(text1, text2, StringComparison.Ordinal))
            {
                text = string.Concat(text.AsSpan(0, startLoc), patch.NewText(), text.AsSpan(startLoc + text1.Length));
                Succeed(outcomes, patch, Math.Max(0, startLoc - padding.Length), delta);
                continue;
            }

            List<DmpDiff> diffs = DmpDiffer.Compute(text1, text2, cancellationToken);
            if (text1.Length > matchMaxBits && Levenshtein(diffs) / (double)text1.Length > patchDeleteThreshold)
            {
                // A big deletion over text that has changed a lot: deleting it blindly could remove something else.
                Fail(outcomes, patch, DmpPatchOutcome.TooDifferent);
                continue;
            }

            // Walk the patch's own diffs, translating each position in the expected text to the found text.
            var sb = new StringBuilder(text);
            int index1 = 0;
            int shift = 0;
            foreach (DmpDiff d in patch.MutableDiffs)
            {
                if (d.Operation != DmpOperation.Equal)
                {
                    int index2 = XIndex(diffs, index1);
                    if (d.Operation == DmpOperation.Insert)
                    {
                        sb.Insert(startLoc + index2 + shift, d.Text);
                        shift += d.Text.Length;
                    }
                    else
                    {
                        int endIndex2 = XIndex(diffs, index1 + d.Text.Length);
                        sb.Remove(startLoc + index2 + shift, endIndex2 - index2);
                        shift -= endIndex2 - index2;
                    }
                }

                if (d.Operation != DmpOperation.Insert)
                {
                    index1 += d.Text.Length;
                }
            }

            text = sb.ToString();
            Succeed(outcomes, patch, Math.Max(0, startLoc - padding.Length), delta);
        }

        // A hostile patch can delete the padding itself (it is ordinary text to the matcher), so clamp rather than trust the lengths.
        int from = Math.Min(padding.Length, text.Length);
        int to = Math.Max(from, text.Length - padding.Length);
        return new DmpApplyResult(text.Substring(from, to - from), outcomes);
    }

    private static void Fail(DmpPatchResult[] outcomes, DmpPatch piece, DmpPatchOutcome why)
    {
        // A split patch fails as a whole if any piece fails; the first failure names the reason.
        if (outcomes[piece.Origin].Outcome == DmpPatchOutcome.Applied)
        {
            outcomes[piece.Origin] = new DmpPatchResult(why, -1, 0);
        }
    }

    private static void Succeed(DmpPatchResult[] outcomes, DmpPatch piece, int location, int drift)
    {
        DmpPatchResult cur = outcomes[piece.Origin];
        if (cur.Outcome == DmpPatchOutcome.Applied && cur.Location == -1)
        {
            outcomes[piece.Origin] = new DmpPatchResult(DmpPatchOutcome.Applied, location, drift);
        }
    }

    // Characters used to pad the ends of the text. Control characters 1..margin, as in the wiki's description; they sit where no real
    // text is, so patches at the ends get context that cannot be mistaken for content.
    private string MakePadding()
    {
        var sb = new StringBuilder(patchMargin);
        for (int i = 1; i <= patchMargin; i++)
        {
            sb.Append((char)i);
        }

        return sb.ToString();
    }

    private static void AddPadding(List<DmpPatch> patches, string padding)
    {
        int len = padding.Length;
        foreach (DmpPatch p in patches)
        {
            p.Start1 += len;
            p.Start2 += len;
        }

        DmpPatch first = patches[0];
        List<DmpDiff> fd = first.MutableDiffs;
        if (fd.Count == 0 || fd[0].Operation != DmpOperation.Equal)
        {
            fd.Insert(0, new DmpDiff(DmpOperation.Equal, padding));
            first.Start1 -= len;
            first.Start2 -= len;
            first.Length1 += len;
            first.Length2 += len;
        }
        else if (len > fd[0].Text.Length)
        {
            int extra = len - fd[0].Text.Length;
            fd[0] = new DmpDiff(DmpOperation.Equal, string.Concat(padding.AsSpan(fd[0].Text.Length), fd[0].Text));
            first.Start1 -= extra;
            first.Start2 -= extra;
            first.Length1 += extra;
            first.Length2 += extra;
        }

        DmpPatch last = patches[^1];
        List<DmpDiff> ld = last.MutableDiffs;
        if (ld.Count == 0 || ld[^1].Operation != DmpOperation.Equal)
        {
            ld.Add(new DmpDiff(DmpOperation.Equal, padding));
            last.Length1 += len;
            last.Length2 += len;
        }
        else if (len > ld[^1].Text.Length)
        {
            int extra = len - ld[^1].Text.Length;
            ld[^1] = new DmpDiff(DmpOperation.Equal, string.Concat(ld[^1].Text, padding.AsSpan(0, extra)));
            last.Length1 += extra;
            last.Length2 += extra;
        }
    }

    // Cuts patches whose old text is longer than MatchMaxBits into pieces the search can take, each carrying a margin of context
    // from its neighbours, and passing a long deletion through whole (the wiki's description of patch_splitMax).
    private void SplitMax(List<DmpPatch> patches)
    {
        int size = matchMaxBits;
        for (int x = 0; x < patches.Count; x++)
        {
            if (patches[x].Length1 <= size)
            {
                continue;
            }

            DmpPatch big = patches[x];
            patches.RemoveAt(x--);
            var queue = new List<DmpDiff>(big.MutableDiffs);
            int head = 0;
            int start1 = big.Start1;
            int start2 = big.Start2;
            string pre = string.Empty;
            int insertAt = x + 1;
            while (head < queue.Count)
            {
                var piece = new DmpPatch { Origin = big.Origin };
                bool empty = true;
                piece.Start1 = start1 - Measure(pre);
                piece.Start2 = start2 - Measure(pre);
                if (pre.Length > 0)
                {
                    piece.Length1 = piece.Length2 = pre.Length;
                    piece.MutableDiffs.Add(new DmpDiff(DmpOperation.Equal, pre));
                }

                while (head < queue.Count && piece.Length1 < size - patchMargin)
                {
                    DmpDiff d = queue[head];
                    if (d.Operation == DmpOperation.Insert)
                    {
                        piece.Length2 += d.Text.Length;
                        start2 += Measure(d.Text);
                        piece.MutableDiffs.Add(d);
                        head++;
                        empty = false;
                    }
                    else if (d.Operation == DmpOperation.Delete && piece.MutableDiffs.Count == 1 && piece.MutableDiffs[0].Operation == DmpOperation.Equal && d.Text.Length > 2 * size)
                    {
                        // A deletion longer than two searches goes in one piece; it is checked by its ends.
                        piece.Length1 += d.Text.Length;
                        start1 += Measure(d.Text);
                        empty = false;
                        piece.MutableDiffs.Add(d);
                        head++;
                    }
                    else
                    {
                        string take = d.Text.Substring(0, Math.Min(d.Text.Length, size - piece.Length1 - patchMargin));
                        take = KeepPairs(d.Text, take);
                        piece.Length1 += take.Length;
                        start1 += Measure(take);
                        if (d.Operation == DmpOperation.Equal)
                        {
                            piece.Length2 += take.Length;
                            start2 += Measure(take);
                        }
                        else
                        {
                            empty = false;
                        }

                        piece.MutableDiffs.Add(new DmpDiff(d.Operation, take));
                        if (take.Length == d.Text.Length)
                        {
                            head++;
                        }
                        else
                        {
                            queue[head] = new DmpDiff(d.Operation, d.Text.Substring(take.Length));
                        }
                    }
                }

                string new2 = piece.NewText();
                pre = new2.Length > patchMargin ? new2.Substring(new2.Length - patchMargin) : new2;
                pre = TrimLeadingLow(pre, new2);

                // The next patch's first stretch of old text is this one's trailing context.
                var rest = new StringBuilder();
                for (int k = head; k < queue.Count && rest.Length < patchMargin; k++)
                {
                    if (queue[k].Operation != DmpOperation.Insert)
                    {
                        rest.Append(queue[k].Text);
                    }
                }

                string post = rest.Length > patchMargin ? rest.ToString(0, patchMargin) : rest.ToString();
                if (post.Length > 0)
                {
                    piece.Length1 += post.Length;
                    piece.Length2 += post.Length;
                    if (piece.MutableDiffs.Count > 0 && piece.MutableDiffs[^1].Operation == DmpOperation.Equal)
                    {
                        piece.MutableDiffs[^1] = new DmpDiff(DmpOperation.Equal, piece.MutableDiffs[^1].Text + post);
                    }
                    else
                    {
                        piece.MutableDiffs.Add(new DmpDiff(DmpOperation.Equal, post));
                    }
                }

                if (!empty)
                {
                    patches.Insert(insertAt++, piece);
                }
            }

            x = insertAt - 1; // continue after the pieces
        }
    }

    // Length of a text in the unit the patches' offsets use.
    private int Measure(string text)
    {
        if (offsetUnit != DmpOffsetUnit.CodePoint)
        {
            return text.Length;
        }

        return HasSurrogate(text) ? text.Length - PairCount(text, text.Length) : text.Length;
    }

    private static int PairCount(string text, int units)
    {
        int pairs = 0;
        for (int i = 1; i < units; i++)
        {
            if (char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1]))
            {
                pairs++;
                i++;
            }
        }

        return pairs;
    }

    // Code point offset -> UTF-16 index (clamped to the text), and back.
    private static int UnitIndex(string text, int codePoints)
    {
        if (!HasSurrogate(text))
        {
            return Math.Clamp(codePoints, 0, text.Length);
        }

        int i = 0;
        for (int seen = 0; seen < codePoints && i < text.Length; seen++)
        {
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
        }

        return i;
    }

    private static int CodePointIndex(string text, int unitIndex) => HasSurrogate(text) ? unitIndex - PairCount(text, Math.Min(unitIndex, text.Length)) : unitIndex;

    // Do not cut a surrogate pair when taking a prefix of a run.
    private static string KeepPairs(string whole, string take)
    {
        if (take.Length > 0 && take.Length < whole.Length && char.IsHighSurrogate(take[^1]) && char.IsLowSurrogate(whole[take.Length]))
        {
            return whole.Substring(0, take.Length + 1);
        }

        return take;
    }

    private static string TrimLeadingLow(string pre, string whole)
    {
        if (pre.Length > 0 && pre.Length < whole.Length && char.IsLowSurrogate(pre[0]))
        {
            return whole.Substring(whole.Length - pre.Length - 1);
        }

        return pre;
    }

    // Edit distance implied by a diff: within each stretch between equalities, the larger of the inserted and deleted amounts.
    private static int Levenshtein(List<DmpDiff> diffs)
    {
        int total = 0;
        int ins = 0;
        int del = 0;
        foreach (DmpDiff d in diffs)
        {
            switch (d.Operation)
            {
                case DmpOperation.Insert:
                    ins += d.Text.Length;
                    break;
                case DmpOperation.Delete:
                    del += d.Text.Length;
                    break;
                default:
                    total += Math.Max(ins, del);
                    ins = del = 0;
                    break;
            }
        }

        return total + Math.Max(ins, del);
    }

    // Translates an offset in the old text of a diff to the same place in its new text; inside a deletion it gives the deletion's start.
    private static int XIndex(List<DmpDiff> diffs, int loc)
    {
        int chars1 = 0;
        int chars2 = 0;
        int lastChars1 = 0;
        int lastChars2 = 0;
        DmpDiff? hit = null;
        foreach (DmpDiff d in diffs)
        {
            if (d.Operation != DmpOperation.Insert)
            {
                chars1 += d.Text.Length;
            }

            if (d.Operation != DmpOperation.Delete)
            {
                chars2 += d.Text.Length;
            }

            if (chars1 > loc)
            {
                hit = d;
                break;
            }

            lastChars1 = chars1;
            lastChars2 = chars2;
        }

        if (hit is { Operation: DmpOperation.Delete })
        {
            return lastChars2;
        }

        return lastChars2 + (loc - lastChars1);
    }
}
