using System.Globalization;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Tests.Alignments;

/// <summary>The kinds of correspondence the quality measure scores.</summary>
internal enum Gold
{
    Same,
    Edited,
    Moved,
    Split,
    Merged,
    Unrelated,
}

/// <summary>A gold link: old paragraph indices, new paragraph indices, and what happened.</summary>
internal sealed record GoldLink(Gold Kind, int[] Old, int[] New)
{
    public string Id => $"{string.Join(',', Old)}>{string.Join(',', New)}";
}

/// <summary>
/// Generates a document pair with exact labels: each old paragraph is kept, edited, deleted, moved far (and maybe edited),
/// split in two or three, or merged with its neighbours; new paragraphs are inserted; some boilerplate repeats. Paragraphs are
/// made of invented words, so unrelated paragraphs share nothing and the labels are unambiguous.
/// </summary>
internal static class LabelledPairs
{
    public static (CompareDocument Old, CompareDocument New, List<GoldLink> Gold, int Unrelated) Make(int seed, int paragraphs = 40, bool hard = false, double changeRate = 1.0)
    {
        var r = new Random(seed);
        string[] skeleton = Enumerable.Range(0, 22).Select(_ => Common(r)).ToArray();
        string boiler = "This page is intentionally left blank for the avoidance of doubt";
        var oldText = new List<string>();
        for (int i = 0; i < paragraphs; i++)
        {
            oldText.Add(r.Next(12) == 0 ? boiler
                : !hard ? Fx.Para((seed * 1000) + i, r.Next(14, 50))
                : r.Next(4) == 0 ? Templated(skeleton, r)
                : string.Join(' ', Enumerable.Range(0, r.Next(14, 50)).Select(_ => Common(r))));
        }

        // Decide each old paragraph's fate. Splits and merges use neighbours that are otherwise untouched.
        var fate = new string[paragraphs];
        for (int i = 0; i < paragraphs; i++)
        {
            int roll = r.Next(100);
            fate[i] = oldText[i] == boiler || r.NextDouble() >= changeRate ? "same" : roll switch
            {
                < 50 => "same",
                < 72 => "edit",
                < 80 => "delete",
                < 85 => "move",
                < 88 => "moveedit",
                < 94 => "split",
                _ => "merge",
            };
        }

        for (int i = 0; i < paragraphs; i++)
        {
            if (fate[i] == "merge")
            {
                // A merge takes this paragraph and the next two (all must be plain, long enough, not boilerplate).
                bool ok = i + 2 < paragraphs && Enumerable.Range(i, 3).All(k => (k == i || fate[k] == "same") && oldText[k] != boiler && oldText[k].Split(' ').Length >= 12);
                if (!ok)
                {
                    fate[i] = "same";
                }
                else
                {
                    fate[i + 1] = fate[i + 2] = "merged";
                }
            }

            if (fate[i] == "split" && oldText[i].Split(' ').Length < 24)
            {
                fate[i] = "edit";
            }
        }

        var gold = new List<GoldLink>();
        var newText = new List<string>();
        var moves = new List<(int Old, string Text)>();
        for (int i = 0; i < paragraphs; i++)
        {
            if (r.NextDouble() * 100 < 7 * changeRate)
            {
                newText.Add(hard ? Templated(skeleton, r) : Fx.Para((seed * 1000) + 500 + newText.Count, 20));
            }

            switch (fate[i])
            {
                case "same":
                    gold.Add(new GoldLink(Gold.Same, [i], [newText.Count]));
                    newText.Add(oldText[i]);
                    break;
                case "edit":
                    gold.Add(new GoldLink(Gold.Edited, [i], [newText.Count]));
                    newText.Add(Edit(oldText[i], r, hard));
                    break;
                case "move":
                    moves.Add((i, oldText[i]));
                    break;
                case "moveedit":
                    moves.Add((i, Edit(oldText[i], r, hard)));
                    break;
                case "split":
                {
                    string[] w = oldText[i].Split(' ');
                    int parts = r.Next(2, 4);
                    var idx = new List<int>();
                    for (int p = 0; p < parts; p++)
                    {
                        idx.Add(newText.Count);
                        newText.Add(string.Join(' ', w[(p * w.Length / parts)..((p + 1) * w.Length / parts)]));
                    }

                    gold.Add(new GoldLink(Gold.Split, [i], [.. idx]));
                    break;
                }

                case "merge":
                    gold.Add(new GoldLink(Gold.Merged, [i, i + 1, i + 2], [newText.Count]));
                    newText.Add(string.Join(' ', oldText[i], oldText[i + 1], oldText[i + 2]));
                    break;
                case "merged":
                case "delete":
                    break;
            }
        }

        // Moved paragraphs go to a position at least a quarter of the document away.
        foreach ((int from, string text) in moves)
        {
            int target = newText.Count == 0 ? 0 : (int)(((long)from + (paragraphs / 4) + r.Next(paragraphs / 2)) % (newText.Count + 1));
            newText.Insert(target, text);
            for (int g = 0; g < gold.Count; g++)
            {
                if (gold[g].New.Any(x => x >= target))
                {
                    gold[g] = gold[g] with { New = gold[g].New.Select(x => x >= target ? x + 1 : x).ToArray() };
                }
            }

            gold.Add(new GoldLink(Gold.Moved, [from], [target]));
        }

        var linkedOld = new HashSet<int>(gold.SelectMany(g => g.Old));
        var linkedNew = new HashSet<int>(gold.SelectMany(g => g.New));
        int unrelated = (paragraphs - linkedOld.Count) + (newText.Count - linkedNew.Count);
        foreach (int i in Enumerable.Range(0, paragraphs).Where(i => !linkedOld.Contains(i)))
        {
            gold.Add(new GoldLink(Gold.Unrelated, [i], []));
        }

        foreach (int j in Enumerable.Range(0, newText.Count).Where(j => !linkedNew.Contains(j)))
        {
            gold.Add(new GoldLink(Gold.Unrelated, [], [j]));
        }

        return (Fx.Doc([.. oldText]), Fx.Doc([.. newText]), gold, unrelated);
    }

    // A word from a small vocabulary with a Zipf-like spread, so unrelated paragraphs share many words, as real prose does.
    private static string Common(Random r) => Fx.Word((int)(Math.Pow(r.NextDouble(), 3) * 300));

    // A clause built on a skeleton shared by many clauses, with some slots filled differently: near-duplicates that are not the same block.
    private static string Templated(string[] skeleton, Random r) =>
        string.Join(' ', skeleton.Select(w => r.Next(100) < 35 ? Common(r) : w));

    // Replace about one word in six (one in three when hard), and sometimes add a few.
    private static string Edit(string text, Random r, bool hard = false)
    {
        List<string> w = [.. text.Split(' ')];
        for (int k = 0; k < w.Count; k++)
        {
            if (r.Next(hard ? 3 : 7) == 0)
            {
                w[k] = "rev" + r.Next(10000).ToString(CultureInfo.InvariantCulture);
            }
        }

        if (r.Next(3) == 0)
        {
            w.Insert(r.Next(w.Count), "additionally");
        }

        string s = string.Join(' ', w);
        return s == text ? text + " also" : s;
    }

    /// <summary>The links of an alignment as gold-comparable records (paragraph index = node id - 1).</summary>
    public static List<GoldLink> Predicted(Alignment al)
    {
        var result = new List<GoldLink>();
        foreach (AlignmentLink l in al.Links)
        {
            if (al.Old[l.OldNodes[0]].Kind != NodeKind.Paragraph)
            {
                continue;
            }

            int[] o = l.OldNodes.Select(x => x - 1).ToArray(), n = l.NewNodes.Select(x => x - 1).ToArray();
            Gold kind = l.Kind switch
            {
                LinkKind.Split => Gold.Split,
                LinkKind.Merge => Gold.Merged,
                LinkKind.Move => Gold.Moved,
                _ => string.Equals(al.Old[l.OldNodes[0]].Text, al.New[l.NewNodes[0]].Text, StringComparison.Ordinal) ? Gold.Same : Gold.Edited,
            };
            result.Add(new GoldLink(kind, o, n));
        }

        var linkedOld = new HashSet<int>(result.SelectMany(g => g.Old));
        var linkedNew = new HashSet<int>(result.SelectMany(g => g.New));
        for (int i = 1; i < al.Old.Count; i++)
        {
            if (!linkedOld.Contains(i - 1))
            {
                result.Add(new GoldLink(Gold.Unrelated, [i - 1], []));
            }
        }

        for (int j = 1; j < al.New.Count; j++)
        {
            if (!linkedNew.Contains(j - 1))
            {
                result.Add(new GoldLink(Gold.Unrelated, [], [j - 1]));
            }
        }

        return result;
    }
}

/// <summary>Counts of true positives, false positives and false negatives per gold kind.</summary>
internal sealed class Tally
{
    public readonly int[] Tp = new int[6];
    public readonly int[] Fp = new int[6];
    public readonly int[] Fn = new int[6];

    public void Add(List<GoldLink> gold, List<GoldLink> predicted)
    {
        var goldSet = new HashSet<(Gold, string)>(gold.Select(g => (g.Kind, g.Id)));
        var predSet = new HashSet<(Gold, string)>(predicted.Select(g => (g.Kind, g.Id)));
        foreach ((Gold k, string id) in predSet)
        {
            if (goldSet.Contains((k, id)))
            {
                Tp[(int)k]++;
            }
            else
            {
                Fp[(int)k]++;
            }
        }

        foreach ((Gold k, string id) in goldSet)
        {
            if (!predSet.Contains((k, id)))
            {
                Fn[(int)k]++;
            }
        }
    }

    public double Precision(Gold k) => Tp[(int)k] + Fp[(int)k] == 0 ? 1.0 : (double)Tp[(int)k] / (Tp[(int)k] + Fp[(int)k]);

    public double Recall(Gold k) => Tp[(int)k] + Fn[(int)k] == 0 ? 1.0 : (double)Tp[(int)k] / (Tp[(int)k] + Fn[(int)k]);

    public double F1(Gold k)
    {
        double p = Precision(k), r = Recall(k);
        return p + r == 0 ? 0 : 2 * p * r / (p + r);
    }
}
