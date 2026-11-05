using System.Globalization;
using OneDiff.Documents.Model;
using OneDiff.Documents.Serialization;
using static OneDiff.Documents.Tests.Helpers;

namespace OneDiff.Documents.Tests;

/// <summary>
/// Random numbered contracts with a defined term, references that follow their targets, and values, edited by inserting, deleting
/// and moving clauses, renaming the term everywhere and editing values. Every comparison must be complete with no fallback, the groups
/// must partition the changes, and the JSON must round-trip byte for byte. <c>ONEDIFF_SEM_CASES</c> and <c>ONEDIFF_SEM_SEED</c> size
/// and seed the sweep.
/// </summary>
public class SemanticPropertyTests
{
    private static readonly string[] Terms = ["Services", "Deliverables", "Platform", "Software"];

    private sealed class Clause
    {
        public int Uid;
        public string Pattern = "";
        public int RefUid = -1;
        public string Value = "";
    }

    private sealed class Contract
    {
        public string Term = "Services";
        public List<Clause> Clauses = [];
        public int NextUid;

        public Contract Clone() => new() { Term = Term, NextUid = NextUid, Clauses = [.. Clauses.Select(c => new Clause { Uid = c.Uid, Pattern = c.Pattern, RefUid = c.RefUid, Value = c.Value })] };
    }

    private static readonly string[] Patterns =
    [
        "The Customer shall pay each invoice for the {T} within {V} of receipt.",
        "The fee for the {T} is {V}, payable in advance, as set out in Section {R}.",
        "Either party may terminate this Agreement by giving {V} written notice, subject to Section {R}.",
        "The {T} shall conform to the specification for {V} from delivery.",
        "Neither party may assign this Agreement without consent; see Section {R}.",
    ];

    private static readonly string[] Values = ["30 days", "60 days", "USD 5,000", "USD 12,500", "12 %", "15 %", "1 March 2027", "2 April 2027", "10 business days"];

    private static Clause NewClause(Contract c, Random r) => new() { Uid = c.NextUid++, Pattern = Patterns[r.Next(Patterns.Length)], Value = Values[r.Next(Values.Length)] };

    private static Contract Generate(Random r)
    {
        var c = new Contract { Term = Terms[r.Next(Terms.Length)] };
        for (int i = r.Next(6, 22); i > 0; i--)
        {
            c.Clauses.Add(NewClause(c, r));
        }

        foreach (Clause cl in c.Clauses)
        {
            cl.RefUid = c.Clauses[r.Next(c.Clauses.Count)].Uid;
        }

        return c;
    }

    private static void Mutate(Contract c, Random r)
    {
        switch (r.Next(7))
        {
            case 0:
                c.Clauses.Insert(r.Next(c.Clauses.Count + 1), NewClause(c, r));
                break;
            case 1 when c.Clauses.Count > 3:
                c.Clauses.RemoveAt(r.Next(c.Clauses.Count));
                break;
            case 2 when c.Clauses.Count > 3:
                Clause m = c.Clauses[r.Next(c.Clauses.Count)];
                c.Clauses.Remove(m);
                c.Clauses.Insert(r.Next(c.Clauses.Count + 1), m);
                break;
            case 3:
                c.Term = Terms[(Array.IndexOf(Terms, c.Term) + 1 + r.Next(Terms.Length - 1)) % Terms.Length];
                break;
            case 4:
                c.Clauses[r.Next(c.Clauses.Count)].Value = Values[r.Next(Values.Length)];
                break;
            case 5:
                c.Clauses[r.Next(c.Clauses.Count)].Pattern = Patterns[r.Next(Patterns.Length)];
                break;
            default:
                c.Clauses[r.Next(c.Clauses.Count)].RefUid = c.Clauses[r.Next(c.Clauses.Count)].Uid;
                break;
        }
    }

    private static CompareDocument Build(Contract c)
    {
        var b = new CompareDocumentBuilder(DocumentKind.Flowing);
        b.Add(NodeKind.ListItem, new NodeContent { Text = $"\"{c.Term}\" means the services described in Schedule 1.", Attributes = FormatSet.Of(FormatKeys.ListLabel, "1.1") });
        var labels = new Dictionary<int, string>();
        for (int i = 0; i < c.Clauses.Count; i++)
        {
            labels[c.Clauses[i].Uid] = $"{2 + i / 5}.{1 + i % 5}";
        }

        for (int i = 0; i < c.Clauses.Count; i++)
        {
            Clause cl = c.Clauses[i];
            string target = labels.GetValueOrDefault(cl.RefUid, "1.1");
            string text = cl.Pattern.Replace("{T}", c.Term, StringComparison.Ordinal).Replace("{V}", cl.Value, StringComparison.Ordinal).Replace("{R}", target, StringComparison.Ordinal);
            b.Add(NodeKind.ListItem, new NodeContent { Text = text, Attributes = FormatSet.Of(FormatKeys.ListLabel, labels[cl.Uid]) });
        }

        return b.Build();
    }

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out int n) ? n : fallback;

    [Fact]
    public void RandomContractsAreCompleteWithGroupsThatPartitionTheChanges()
    {
        int cases = Setting("ONEDIFF_SEM_CASES", 300), seed0 = Setting("ONEDIFF_SEM_SEED", 1);
        int cascades = 0, renames = 0;
        for (int k = 0; k < cases; k++)
        {
            var r = new Random(seed0 + k);
            Contract a = Generate(r), b = a.Clone();
            for (int m = r.Next(1, 7); m > 0; m--)
            {
                Mutate(b, r);
            }

            CompareDocument oldDoc = Build(a), newDoc = Build(b);
            foreach (CompareSettings settings in new[] { CompareSettings.Legal, CompareSettings.Strict with { ThrowOnFailure = false, CollapseCascades = true, TypedChanges = true } })
            {
                Comparison c = Comparison.Of(oldDoc, newDoc, settings);
                Assert.True(c.Verification.Passed, $"case {seed0 + k}");
                Assert.True(c.Verification.FallbackLevel == 0, $"case {seed0 + k} fell back to level {c.Verification.FallbackLevel}");
                Assert.Empty(c.Check());
                Assert.Equal(c.Changes.Length, c.Groups.Sum(g => g.ChangeIds.Length));
                cascades += c.Groups.Count(g => g.Kind == GroupKind.Cascade);
                renames += c.Groups.Count(g => g.Kind == GroupKind.Rename);
                foreach (ChangeGroup g in c.Groups.Where(g => g.Kind != GroupKind.Region))
                {
                    Assert.All(g.ChangeIds, id =>
                    {
                        ChangeType t = c.Changes.First(x => x.Id == id).Type;
                        Assert.True(g.Kind == GroupKind.Cascade ? t is ChangeType.Numbering or ChangeType.Reference : t == ChangeType.DefinedTerm, $"case {seed0 + k}: {t} in a {g.Kind} group");
                    });
                }

                if (k % 7 == 0)
                {
                    byte[] json = ComparisonJson.Write(c);
                    Assert.Equal(json, ComparisonJson.Write(ComparisonJson.Read(json)));
                    Assert.Equal(json, ComparisonJson.Write(Comparison.Of(oldDoc, newDoc, settings)));
                }
            }
        }

        if (cases >= 300)
        {
            Assert.True(cascades > 0 && renames > 0, $"the sweep never produced a cascade ({cascades}) or a rename ({renames})");
        }
    }
}
