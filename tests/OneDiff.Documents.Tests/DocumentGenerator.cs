using System.Collections.Immutable;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Tests;

/// <summary>A mutable description of a flowing document that random edits work on, and that builds into a compare model.</summary>
internal sealed class GNode
{
    public NodeKind Kind;
    public string Text = "";
    public List<(int Word, int Count, FormatSet Format)> Runs = [];
    public FormatSet Attributes = FormatSet.Empty;
    public List<GNode> Children = [];

    public GNode Clone() => new()
    {
        Kind = Kind,
        Text = Text,
        Runs = [.. Runs],
        Attributes = Attributes,
        Children = [.. Children.Select(c => c.Clone())],
    };
}

internal static class DocumentGenerator
{
    private static readonly string[] Words =
    [
        "the", "supplier", "shall", "deliver", "goods", "within", "thirty", "days", "of", "order", "payment", "is", "due", "sixty",
        "after", "receipt", "invoice", "either", "party", "may", "terminate", "this", "agreement", "notice", "breach", "governed",
        "laws", "state", "New", "York", "tenant", "landlord", "rent", "monthly", "advance", "insured", "fire", "building", "keep",
        "pay", "and", "or", "not", "all", "any", "each", "such", "other", "from", "to", "by", "with", "\u201Cquoted\u201D", "it\u2019s",
        "a\u2014b", "42", "3.5%", "USD", "Section", "4.2",
    ];

    private static readonly string[] Styles = ["Normal", "Heading1", "Quote", "Title"];

    public static string RandomText(Random r, int min = 3, int max = 14)
    {
        int n = r.Next(min, max + 1);
        var words = new string[n];
        for (int i = 0; i < n; i++)
        {
            words[i] = Words[r.Next(Words.Length)];
        }

        return string.Join(r.Next(12) == 0 ? "  " : " ", words);
    }

    public static GNode RandomParagraph(Random r)
    {
        var p = new GNode { Kind = r.Next(6) == 0 ? NodeKind.ListItem : NodeKind.Paragraph, Text = RandomText(r) };
        if (p.Kind == NodeKind.ListItem)
        {
            p.Attributes = FormatSet.Of(FormatKeys.ListLabel, $"{r.Next(1, 9)}.{r.Next(1, 9)}", FormatKeys.ListLevel, "1");
        }
        else if (r.Next(4) == 0)
        {
            p.Attributes = FormatSet.Of(FormatKeys.ParagraphStyle, Styles[r.Next(Styles.Length)]);
        }

        int runs = r.Next(3);
        for (int i = 0; i < runs; i++)
        {
            p.Runs.Add((r.Next(0, 8), r.Next(1, 4), r.Next(3) == 0 ? FormatSet.Of(FormatKeys.Italic, "1") : FormatSet.Of(FormatKeys.Bold, "1")));
        }

        if (r.Next(10) == 0)
        {
            p.Children.Add(new GNode { Kind = NodeKind.Field, Text = RandomText(r, 1, 2), Attributes = FormatSet.Of(FormatKeys.FieldCode, "REF _Ref" + r.Next(100)) });
        }

        return p;
    }

    public static GNode Random(Random r)
    {
        var root = new GNode { Kind = NodeKind.Section };
        int blocks = r.Next(0, 14);
        for (int i = 0; i < blocks; i++)
        {
            int roll = r.Next(100);
            if (roll < 70)
            {
                root.Children.Add(RandomParagraph(r));
            }
            else if (roll < 82)
            {
                var table = new GNode { Kind = NodeKind.Table };
                for (int row = r.Next(1, 4); row > 0; row--)
                {
                    var tr = new GNode { Kind = NodeKind.TableRow };
                    for (int cell = r.Next(1, 4); cell > 0; cell--)
                    {
                        var tc = new GNode { Kind = NodeKind.TableCell };
                        for (int p = r.Next(1, 3); p > 0; p--)
                        {
                            tc.Children.Add(RandomParagraph(r));
                        }

                        tr.Children.Add(tc);
                    }

                    table.Children.Add(tr);
                }

                root.Children.Add(table);
            }
            else if (roll < 88)
            {
                var fn = new GNode { Kind = NodeKind.Footnote };
                fn.Children.Add(RandomParagraph(r));
                root.Children.Add(fn);
            }
            else if (roll < 93)
            {
                var c = new GNode { Kind = NodeKind.Comment };
                c.Children.Add(RandomParagraph(r));
                root.Children.Add(c);
            }
            else if (roll < 96)
            {
                root.Children.Add(new GNode { Kind = NodeKind.Image, Attributes = FormatSet.Of(FormatKeys.ContentHash, r.Next(5).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)) });
            }
            else
            {
                root.Children.Add(new GNode { Kind = NodeKind.Property, Text = RandomText(r, 1, 3), Attributes = FormatSet.Of(FormatKeys.PropertyName, "p" + r.Next(3)) });
            }
        }

        return root;
    }

    public static IEnumerable<GNode> All(GNode n)
    {
        yield return n;
        foreach (GNode c in n.Children)
        {
            foreach (GNode x in All(c))
            {
                yield return x;
            }
        }
    }

    public static CompareDocument Build(GNode root, string name = "")
    {
        var b = new CompareDocumentBuilder(DocumentKind.Flowing).WithMetadata(name);
        Emit(b, root);
        b.AddOpaque("custom/data", "00");
        return b.Build();
    }

    public static CompareDocument BuildWithOpaque(GNode root, string hash)
    {
        var b = new CompareDocumentBuilder(DocumentKind.Flowing);
        Emit(b, root);
        b.AddOpaque("custom/data", hash);
        return b.Build();
    }

    private static void Emit(CompareDocumentBuilder b, GNode n)
    {
        NormalizedText norm = TextNormalization.Normalize(n.Text, [ControlCharacterNormalizer.Instance, TypographicNormalizer.Instance]);
        var runs = new List<FormatRun>();
        if (norm.Text.Length > 0)
        {
            // Word-aligned runs over the normalized text; indices past the end are clamped.
            var starts = new List<int>();
            for (int i = 0; i < norm.Text.Length; i++)
            {
                if (i == 0 || norm.Text[i - 1] == ' ')
                {
                    starts.Add(i);
                }
            }

            int last = 0;
            foreach ((int word, int count, FormatSet fmt) in n.Runs.OrderBy(x => x.Word))
            {
                if (word >= starts.Count)
                {
                    continue;
                }

                int s = Math.Max(starts[word], last);
                int endWord = Math.Min(word + count, starts.Count);
                int e = endWord >= starts.Count ? norm.Text.Length : starts[endWord];
                if (e > s)
                {
                    runs.Add(new FormatRun(s, e - s, fmt));
                    last = e;
                }
            }
        }

        var content = new NodeContent
        {
            Text = norm.Text,
            Folds = norm.Folds,
            Runs = [.. runs],
            Attributes = n.Attributes,
            Anchor = Anchor.ForNode("g:" + n.Kind),
        };
        if (n.Children.Count == 0)
        {
            b.Add(n.Kind, content);
            return;
        }

        b.Begin(n.Kind, content);
        foreach (GNode c in n.Children)
        {
            Emit(b, c);
        }

        b.End();
    }

    /// <summary>Applies one random edit; returns its name.</summary>
    public static string Mutate(GNode root, Random r)
    {
        List<GNode> all = All(root).ToList();
        List<GNode> notRoot = all.Skip(1).ToList();
        List<GNode> texty = all.Where(x => x.Text.Length > 0 && x.Kind is NodeKind.Paragraph or NodeKind.ListItem or NodeKind.Field).ToList();
        List<GNode> containers = all.Where(x => x.Kind is NodeKind.Section or NodeKind.TableCell or NodeKind.Footnote or NodeKind.Comment).ToList();
        switch (r.Next(14))
        {
            case 0:
            {
                GNode c = containers[r.Next(containers.Count)];
                c.Children.Insert(r.Next(c.Children.Count + 1), RandomParagraph(r));
                return "insert";
            }

            case 1 when notRoot.Count > 0:
            {
                GNode victim = notRoot[r.Next(notRoot.Count)];
                foreach (GNode x in all)
                {
                    x.Children.Remove(victim);
                }

                return "delete";
            }

            case 2 when notRoot.Count > 0:
            {
                GNode mover = notRoot[r.Next(notRoot.Count)];
                HashSet<GNode> inside = [.. All(mover)];
                List<GNode> targets = containers.Where(x => !inside.Contains(x)).ToList();
                if (targets.Count == 0)
                {
                    return "noop";
                }

                foreach (GNode x in all)
                {
                    x.Children.Remove(mover);
                }

                GNode t = targets[r.Next(targets.Count)];
                t.Children.Insert(r.Next(t.Children.Count + 1), mover);
                return "move";
            }

            case 3 when texty.Count > 0:
            {
                GNode p = texty[r.Next(texty.Count)];
                string[] words = p.Text.Split(' ');
                if (words.Length < 4)
                {
                    return "noop";
                }

                int cut = r.Next(1, words.Length - 1);
                GNode second = new() { Kind = p.Kind, Text = string.Join(' ', words[cut..]), Attributes = p.Attributes };
                p.Text = string.Join(' ', words[..cut]);
                GNode? parent = all.FirstOrDefault(x => x.Children.Contains(p));
                if (parent is null)
                {
                    return "noop";
                }

                parent.Children.Insert(parent.Children.IndexOf(p) + 1, second);
                return "split";
            }

            case 4:
            {
                List<GNode> pars = all.Where(x => x.Children.Count > 1).ToList();
                if (pars.Count == 0)
                {
                    return "noop";
                }

                GNode parent = pars[r.Next(pars.Count)];
                int i = r.Next(parent.Children.Count - 1);
                GNode a = parent.Children[i], bb = parent.Children[i + 1];
                if (a.Kind == bb.Kind && a.Kind is NodeKind.Paragraph && a.Children.Count == 0 && bb.Children.Count == 0)
                {
                    a.Text += " " + bb.Text;
                    parent.Children.RemoveAt(i + 1);
                    return "merge";
                }

                return "noop";
            }

            case 5 or 6 or 7 when texty.Count > 0:
            {
                GNode p = texty[r.Next(texty.Count)];
                List<string> words = [.. p.Text.Split(' ')];
                int i = r.Next(words.Count);
                switch (r.Next(3))
                {
                    case 0:
                        words[i] = Words[r.Next(Words.Length)];
                        break;
                    case 1:
                        words.Insert(i, Words[r.Next(Words.Length)]);
                        break;
                    default:
                        if (words.Count > 1)
                        {
                            words.RemoveAt(i);
                        }

                        break;
                }

                p.Text = string.Join(' ', words);
                return "wordedit";
            }

            case 8 when texty.Count > 0:
            {
                GNode p = texty[r.Next(texty.Count)];
                p.Runs.Add((r.Next(0, 6), r.Next(1, 3), FormatSet.Of(FormatKeys.Bold, "1", FormatKeys.Color, "#" + r.Next(3) + "00000")));
                return "format";
            }

            case 9 when texty.Count > 0:
            {
                GNode p = texty[r.Next(texty.Count)];
                p.Attributes = p.Attributes.With(FormatKeys.ParagraphStyle, Styles[r.Next(Styles.Length)]);
                return "attribute";
            }

            case 10 when texty.Count > 0:
            {
                GNode p = texty[r.Next(texty.Count)];
                p.Text = p.Text.Replace("the", "\u201Cthe\u201D", StringComparison.Ordinal).Replace(" ", "  ", StringComparison.Ordinal);
                return "typography";
            }

            case 11:
            {
                GNode? img = all.FirstOrDefault(x => x.Kind == NodeKind.Image);
                if (img is not null)
                {
                    img.Attributes = FormatSet.Of(FormatKeys.ContentHash, r.Next(5).ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
                }

                return "image";
            }

            case 12:
            {
                GNode? c = all.FirstOrDefault(x => x.Kind is NodeKind.Comment or NodeKind.Property);
                if (c is not null)
                {
                    GNode target = c.Children.Count > 0 ? c.Children[0] : c;
                    target.Text = RandomText(r);
                }

                return "hidden";
            }

            default:
            {
                GNode c = containers[r.Next(containers.Count)];
                c.Children.Add(new GNode { Kind = NodeKind.Paragraph, Text = RandomText(r) });
                return "append";
            }
        }
    }
}
