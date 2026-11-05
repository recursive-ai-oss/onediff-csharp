using System.Collections.Immutable;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Scoring;

namespace OneDiff.Documents.Tests.Alignments;

public class AlignmentEdgeCaseTests
{
    private static int Count(Alignment al, LinkKind kind) => al.Links.Count(l => l.Kind == kind);

    [Fact]
    public void IdenticalDocumentsMatchEverythingExactly()
    {
        CompareDocument a = Fx.Doc(Fx.Para(1), Fx.Para(2), Fx.Para(3));
        Alignment al = Fx.Align(a, Fx.Doc(Fx.Para(1), Fx.Para(2), Fx.Para(3)));
        Assert.Equal(a.Count, al.Links.Length);
        Assert.All(al.Links, l =>
        {
            Assert.Equal(LinkKind.Match, l.Kind);
            Assert.Equal(LinkOrigin.Exact, l.Origin);
        });
        Assert.Empty(Fx.Clean(a, Fx.Doc(Fx.Para(1), Fx.Para(2), Fx.Para(3))).Changes);
    }

    [Fact]
    public void EmptyDocumentsAlign()
    {
        CompareDocument empty = new CompareDocumentBuilder().Build();
        Assert.Empty(Fx.Align(empty, empty).Links);
        CompareDocument one = Fx.Doc(Fx.Para(1));
        Assert.Empty(Fx.Align(empty, one).Links);
        Assert.Empty(Fx.Align(one, empty).Links);
        Fx.Clean(empty, one);
        Fx.Clean(one, empty);
    }

    [Fact]
    public void FullyRewrittenDocumentKeepsTheSectionAndInventsNoMoves()
    {
        CompareDocument a = Fx.Doc(Fx.Para(1), Fx.Para(2), Fx.Para(3));
        CompareDocument b = Fx.Doc(Fx.Para(11), Fx.Para(12), Fx.Para(13));
        Alignment al = Fx.Align(a, b);
        Assert.Single(al.Links);
        Assert.Equal(NodeKind.Section, a[al.Links[0].OldNodes[0]].Kind);
        Fx.Clean(a, b);
    }

    [Fact]
    public void ReorderedSectionsAreOneVisibleMove()
    {
        CompareDocument Make(bool swap)
        {
            var bld = new CompareDocumentBuilder();
            string[][] secs = [[Fx.Para(1), Fx.Para(2), Fx.Para(3)], [Fx.Para(4), Fx.Para(5)], [Fx.Para(6), Fx.Para(7)]];
            foreach (int s in swap ? new[] { 2, 0, 1 } : [0, 1, 2])
            {
                bld.Begin(NodeKind.Section);
                foreach (string p in secs[s])
                {
                    bld.Add(NodeKind.Paragraph, new NodeContent { Text = p });
                }

                bld.End();
            }

            return bld.Build();
        }

        CompareDocument a = Make(false), b = Make(true);
        Alignment al = Fx.Align(a, b);
        Assert.Equal(a.Count, al.Links.Length);
        Assert.Equal(1, al.Links.Count(l => l.Kind == LinkKind.Move));
        Assert.Equal(NodeKind.Section, a[al.Links.Single(l => l.Kind == LinkKind.Move).OldNodes[0]].Kind);
        Fx.Clean(a, b);
    }

    [Fact]
    public void ClauseMovedAndEditedIsOneMoveWithItsEdit()
    {
        string moved = Fx.Para(50, 30);
        string movedEdited = moved.Replace(Fx.Word(5000 + 3), "changed", StringComparison.Ordinal) + " extra";
        CompareDocument a = Fx.Doc(Fx.Para(1), moved, Fx.Para(2), Fx.Para(3), Fx.Para(4), Fx.Para(5), Fx.Para(6), Fx.Para(7), Fx.Para(8));
        CompareDocument b = Fx.Doc(Fx.Para(1), Fx.Para(2), Fx.Para(3), Fx.Para(4), Fx.Para(5), Fx.Para(6), Fx.Para(7), Fx.Para(8), movedEdited);
        Alignment al = Fx.Align(a, b);
        AlignmentLink link = al.LinkOfOld(Fx.P(1))!;
        Assert.Equal(LinkKind.Move, link.Kind);
        Assert.Equal([Fx.P(8)], link.NewNodes.ToArray());
        Assert.Equal(1, Count(al, LinkKind.Move));
        Comparison c = Fx.Clean(a, b);
        Assert.Contains(c.Changes, x => x.Kind == ChangeKind.Move);
    }

    [Fact]
    public void HeavilyEditedMovedClauseIsAMoveWhenItsPartnerIsUnique()
    {
        string moved = Fx.Para(60, 30);
        string[] w = moved.Split(' ');
        // Every third word replaced: about 0.67 similar, under the move threshold.
        string edited = string.Join(' ', w.Select((x, i) => i % 3 == 0 ? "zz" + i : x));
        CompareDocument a = Fx.Doc(Fx.Para(1), moved, Fx.Para(2), Fx.Para(3), Fx.Para(4), Fx.Para(5), Fx.Para(6), Fx.Para(7));
        CompareDocument b = Fx.Doc(Fx.Para(1), Fx.Para(2), Fx.Para(3), Fx.Para(4), Fx.Para(5), Fx.Para(6), Fx.Para(7), edited);
        AlignmentLink link = Fx.Align(a, b).LinkOfOld(Fx.P(1))!;
        Assert.Equal(LinkKind.Move, link.Kind);
        Assert.Equal([Fx.P(7)], link.NewNodes.ToArray());
        Fx.Clean(a, b);
    }

    [Fact]
    public void TwoSimilarCandidatesAreNotGuessedAsAMove()
    {
        string moved = Fx.Para(60, 30);
        string[] w = moved.Split(' ');
        string One(int shift) => string.Join(' ', w.Select((x, i) => (i + shift) % 3 == 0 ? "zz" + i + shift : x));
        // Two new paragraphs far from the old one, equally similar to it: no way to tell which it became.
        CompareDocument a = Fx.Doc(Fx.Para(1), moved, Fx.Para(2), Fx.Para(3), Fx.Para(4), Fx.Para(5), Fx.Para(6));
        CompareDocument b = Fx.Doc(Fx.Para(1), Fx.Para(2), Fx.Para(3), Fx.Para(4), Fx.Para(5), Fx.Para(6), One(0), One(1));
        Assert.Null(Fx.Align(a, b).LinkOfOld(Fx.P(1)));
        Fx.Clean(a, b);
    }

    [Fact]
    public void ParagraphSplitIntoThree()
    {
        string whole = Fx.Para(7, 36);
        string[] words = whole.Split(' ');
        string[] parts = [string.Join(' ', words[..12]), string.Join(' ', words[12..24]), string.Join(' ', words[24..])];
        CompareDocument a = Fx.Doc(Fx.Para(1), whole, Fx.Para(2));
        CompareDocument b = Fx.Doc(Fx.Para(1), parts[0], parts[1], parts[2], Fx.Para(2));
        Alignment al = Fx.Align(a, b);
        AlignmentLink split = Assert.Single(al.Links, l => l.Kind == LinkKind.Split);
        Assert.Equal([Fx.P(1)], split.OldNodes.ToArray());
        Assert.Equal([Fx.P(1), Fx.P(2), Fx.P(3)], split.NewNodes.ToArray());
        Fx.Clean(a, b);
    }

    [Fact]
    public void ThreeParagraphsMergedIntoOne()
    {
        string[] parts = [Fx.Para(7, 12), Fx.Para(8, 12), Fx.Para(9, 12)];
        CompareDocument a = Fx.Doc(Fx.Para(1), parts[0], parts[1], parts[2], Fx.Para(2));
        CompareDocument b = Fx.Doc(Fx.Para(1), string.Join(' ', parts), Fx.Para(2));
        Alignment al = Fx.Align(a, b);
        AlignmentLink merge = Assert.Single(al.Links, l => l.Kind == LinkKind.Merge);
        Assert.Equal([Fx.P(1), Fx.P(2), Fx.P(3)], merge.OldNodes.ToArray());
        Assert.Equal([Fx.P(1)], merge.NewNodes.ToArray());
        Fx.Clean(a, b);
    }

    [Fact]
    public void RepeatedBoilerplateAlignsInOrderWithoutCrossing()
    {
        string boiler = "This section is intentionally left blank";
        CompareDocument a = Fx.Doc(boiler, Fx.Para(1), boiler, Fx.Para(2), boiler, boiler, Fx.Para(3), boiler);
        CompareDocument b = Fx.Doc(boiler, Fx.Para(1), boiler, Fx.Para(12), boiler, boiler, boiler, Fx.Para(3), boiler);
        Alignment al = Fx.Align(a, b);
        Assert.Equal(0, Count(al, LinkKind.Move));
        int[] oldBoiler = [Fx.P(0), Fx.P(2), Fx.P(4), Fx.P(5), Fx.P(7)];
        int[] mapped = oldBoiler.Select(o => al.LinkOfOld(o)!.NewNodes[0]).ToArray();
        Assert.Equal(mapped.OrderBy(x => x).ToArray(), mapped);
        Assert.Equal(oldBoiler.Length, mapped.Distinct().Count());
        Fx.Clean(a, b);
    }

    [Fact]
    public void TableWithInsertedRowAndColumnPairsRowsAndCellsExplicitly()
    {
        string[][] oldRows =
        [
            [Fx.Para(1, 5), Fx.Para(2, 5), Fx.Para(3, 5)],
            [Fx.Para(4, 5), Fx.Para(5, 5), Fx.Para(6, 5)],
            [Fx.Para(7, 5), Fx.Para(8, 5), Fx.Para(9, 5)],
        ];
        string[][] newRows =
        [
            [Fx.Para(1, 5), Fx.Para(20, 5), Fx.Para(2, 5), Fx.Para(3, 5)],
            [Fx.Para(4, 5), Fx.Para(21, 5), Fx.Para(5, 5), Fx.Para(6, 5)],
            [Fx.Para(22, 5), Fx.Para(23, 5), Fx.Para(24, 5), Fx.Para(25, 5)],
            [Fx.Para(7, 5), Fx.Para(26, 5), Fx.Para(8, 5), Fx.Para(9, 5)],
        ];
        CompareDocument a = Fx.TableDoc(oldRows, Fx.Para(30)), b = Fx.TableDoc(newRows, Fx.Para(30));
        Alignment al = Fx.Align(a, b);
        Assert.Equal(0, Count(al, LinkKind.Move));
        Assert.Empty(al.UnmatchedOld());
        // Every old node is matched; the new row and the new column cells are the only insertions: 1 row, 4 + 3 cells, each with a paragraph.
        Assert.Equal(b.Count - a.Count, al.UnmatchedNew().Count());
        Assert.All(a.Nodes.Where(x => x.Kind == NodeKind.TableRow), r =>
        {
            AlignmentLink l = al.LinkOfOld(r.Id)!;
            Assert.Equal(NodeKind.TableRow, b[l.NewNodes[0]].Kind);
        });
        Comparison c = Fx.Clean(a, b);
        Assert.DoesNotContain(c.Changes, x => x.Kind == ChangeKind.Move);
    }

    [Fact]
    public void TableOfRewrittenRowsStillPairsTheTableAndItsRows()
    {
        CompareDocument a = Fx.TableDoc([[Fx.Para(1, 5), Fx.Para(2, 5)], [Fx.Para(3, 5), Fx.Para(4, 5)]]);
        CompareDocument b = Fx.TableDoc([[Fx.Para(11, 5), Fx.Para(12, 5)], [Fx.Para(13, 5), Fx.Para(14, 5)]]);
        Alignment al = Fx.Align(a, b);
        foreach (NodeKind kind in new[] { NodeKind.Table, NodeKind.TableRow, NodeKind.TableCell })
        {
            foreach (Node x in a.Nodes.Where(x => x.Kind == kind))
            {
                Assert.NotNull(al.LinkOfOld(x.Id));
            }
        }

        Fx.Clean(a, b);
    }

    [Fact]
    public void CellWhoseTextWasRewrittenKeepsItsEntryAsOneReplace()
    {
        // "3 years" became "Net monthly": nothing in common, but it is the same cell of the same row, so it is one replace and not a
        // delete beside an insert.
        string[] l1 = ["Support", "3 years", "USD 25,000"];
        string[] l2 = ["Hosting", "14 days", "USD 18,000"];
        string[] n1 = ["Support", "Net monthly", "USD 25,000"];
        CompareDocument a = Fx.TableDoc([l1, l2], Fx.Para(1)), b = Fx.TableDoc([n1, l2], Fx.Para(1));
        Comparison c = Helpers.Compare(a, b);
        Assert.Contains(c.Changes, ch => ch.Kind == ChangeKind.Replace && ch.Old.Text == "3 years" && ch.New.Text == "Net monthly");
        Assert.DoesNotContain(c.Changes, ch => ch.Kind is ChangeKind.Insert or ChangeKind.Delete);
    }

    [Fact]
    public void CostCapGivesACoarserCompleteAlignmentWithADiagnostic()
    {
        string[] paras = Enumerable.Range(0, 200).Select(i => Fx.Para(i)).ToArray();
        string[] edited = paras.Select((p, i) => i % 2 == 0 ? p + " extra words here" : p).ToArray();
        CompareDocument a = Fx.Doc(paras), b = Fx.Doc(edited);
        CompareSettings tiny = CompareSettings.Legal with { CostCap = 50 };
        Alignment al = Fx.Align(a, b, tiny);
        Assert.Contains(al.Diagnostics, d => d.Code == "align.cap");
        Comparison c = Fx.Clean(a, b, tiny);
        Assert.Contains(c.Diagnostics, d => d.Code == "align.cap");
        // With room to work the edited paragraphs pair up.
        Alignment full = Fx.Align(a, b);
        Assert.DoesNotContain(full.Diagnostics, d => d.Code == "align.cap");
        Assert.True(full.Links.Length > al.Links.Length);
    }

    [Fact]
    public void PinnedLinksSurviveARerunAndUnpinnedOnesDoNot()
    {
        CompareDocument a = Fx.Doc(Fx.Para(1), Fx.Para(2), Fx.Para(3));
        CompareDocument b = Fx.Doc(Fx.Para(1), Fx.Para(2), Fx.Para(3));
        Alignment al = Fx.Align(a, b);
        // A reviewer says old paragraph 1 is new paragraph 2 (against the text): a pinned move.
        Alignment pinned = al.Pin(Fx.P(0), Fx.P(1));
        Alignment again = Stages.Align(a, b, null, CompareSettings.Legal, pinned);
        AlignmentLink pin = again.LinkOfOld(Fx.P(0))!;
        Assert.True(pin.Pinned);
        Assert.Equal([Fx.P(1)], pin.NewNodes.ToArray());
        Comparison c = Helpers.Clean(Stages.Verify(Stages.Classify(Stages.Group(Stages.DiffPairs(again)))));
        Assert.NotEmpty(c.Changes);
        Alignment released = again.Unpin(pin.Id);
        Alignment third = Stages.Align(a, b, null, CompareSettings.Legal, released);
        Assert.Equal(LinkOrigin.Exact, third.LinkOfOld(Fx.P(0))!.Origin);
        Assert.Equal([Fx.P(0)], third.LinkOfOld(Fx.P(0))!.NewNodes.ToArray());
    }

    private sealed class CountingScorer : IPairScorer
    {
        public int Singles;
        public int Batches;
        public int PairsSeen;
        public double Answer = 0.9;

        public double Score(Node oldNode, Node newNode)
        {
            Singles++;
            return Answer;
        }

        public void ScoreBatch(ReadOnlySpan<NodePair> pairs, Span<double> scores)
        {
            Batches++;
            PairsSeen += pairs.Length;
            for (int i = 0; i < pairs.Length; i++)
            {
                scores[i] = Answer;
            }
        }
    }

    [Fact]
    public void CustomScorerIsAskedInBatchesAndOnlyAboutAmbiguousPairs()
    {
        // One exact paragraph, one clearly edited (ambiguous), one wholly new.
        string edited = Fx.Para(2, 20);
        string[] w = edited.Split(' ');
        string changed = string.Join(' ', w.Take(12)) + " alpha beta gamma delta epsilon zeta eta theta";
        CompareDocument a = Fx.Doc(Fx.Para(1), edited, Fx.Para(3));
        CompareDocument b = Fx.Doc(Fx.Para(1), changed, Fx.Para(33));
        var scorer = new CountingScorer { Answer = 0.9 };
        Alignment al = Fx.Align(a, b, scorer: scorer);
        Assert.Equal(0, scorer.Singles);
        Assert.Equal(1, scorer.Batches);
        Assert.Equal(1, scorer.PairsSeen);
        Assert.Equal([Fx.P(1)], al.LinkOfOld(Fx.P(1))!.NewNodes.ToArray());

        // A scorer that says "not the same block" keeps them apart.
        var no = new CountingScorer { Answer = 0.0 };
        Alignment apart = Fx.Align(a, b, scorer: no);
        Assert.Null(apart.LinkOfOld(Fx.P(1)));
        Fx.Clean(a, b);
    }

    [Fact]
    public void SameInputsGiveTheSameLinksEveryTime()
    {
        CompareDocument a = Fx.Doc(Fx.Para(1), Fx.Para(2), Fx.Para(3), Fx.Para(2));
        CompareDocument b = Fx.Doc(Fx.Para(3), Fx.Para(2) + " tail", Fx.Para(1), Fx.Para(2));
        ImmutableArray<AlignmentLink> first = Fx.Align(a, b).Links;
        for (int i = 0; i < 5; i++)
        {
            ImmutableArray<AlignmentLink> again = Fx.Align(a, b).Links;
            Assert.Equal(first.Length, again.Length);
            for (int k = 0; k < first.Length; k++)
            {
                Assert.Equal(first[k].Kind, again[k].Kind);
                Assert.Equal(first[k].OldNodes.ToArray(), again[k].OldNodes.ToArray());
                Assert.Equal(first[k].NewNodes.ToArray(), again[k].NewNodes.ToArray());
            }
        }
    }
}
