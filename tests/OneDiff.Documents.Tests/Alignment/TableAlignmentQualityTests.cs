using System.Globalization;
using System.Text;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Tests.Alignments;

public class TableAlignmentQualityTests
{
    // Random tables with rows and columns inserted and deleted and some cells edited. A surviving row or cell must be linked to
    // its own counterpart (same row, same cell), never to a neighbour.
    [Fact]
    public void SurvivingRowsAndCellsKeepTheirIdentity()
    {
        var okMine = new int[2];
        var totalMine = new int[2];
        var wrongMine = new int[2];
        var okRef = new int[2];
        var wrongRef = new int[2];
        for (int seed = 1; seed <= 200; seed++)
        {
            var r = new Random(seed);
            int rows = r.Next(3, 9), cols = r.Next(2, 6);
            string[][] grid = new string[rows][];
            for (int i = 0; i < rows; i++)
            {
                grid[i] = new string[cols];
                for (int j = 0; j < cols; j++)
                {
                    grid[i][j] = Fx.Para((seed * 100) + (i * 10) + j, r.Next(1, 7));
                }
            }

            // The new table: old row/column numbers kept in order, some dropped, some new ones (-1) inserted.
            List<int> rowMap = Enumerable.Range(0, rows).Where(_ => r.Next(6) != 0).ToList();
            List<int> colMap = Enumerable.Range(0, cols).Where(_ => r.Next(6) != 0 || cols < 3).ToList();
            for (int k = r.Next(0, 3); k > 0; k--)
            {
                rowMap.Insert(r.Next(rowMap.Count + 1), -1);
            }

            for (int k = r.Next(0, 3); k > 0; k--)
            {
                colMap.Insert(r.Next(colMap.Count + 1), -1);
            }

            if (rowMap.Count == 0 || colMap.Count == 0)
            {
                continue;
            }

            string[][] next = rowMap.Select(ro => colMap.Select(co =>
            {
                if (ro < 0 || co < 0)
                {
                    return Fx.Para((seed * 100) + 90 + r.Next(9), r.Next(1, 7)) + " new" + r.Next(1000).ToString(CultureInfo.InvariantCulture);
                }

                return r.Next(8) == 0 ? grid[ro][co] + " edited" : grid[ro][co];
            }).ToArray()).ToArray();

            CompareDocument a = Fx.TableDoc(grid, Fx.Para(seed, 5)), b = Fx.TableDoc(next, Fx.Para(seed, 5));
            Alignment mine = Stages.Align(a, b);
            Alignment reference = ReferenceAligner.Run(a, b, null, CompareSettings.Default, null, CancellationToken.None);
            var scratch = new int[2];
            for (int ri = 0; ri < rowMap.Count; ri++)
            {
                if (rowMap[ri] < 0)
                {
                    continue;
                }

                int oldRow = RowId(a, rowMap[ri]), newRow = RowId(b, ri);
                Score(mine, oldRow, newRow, 0, okMine, wrongMine, totalMine);
                Score(reference, oldRow, newRow, 0, okRef, wrongRef, scratch);
                for (int ci = 0; ci < colMap.Count; ci++)
                {
                    if (colMap[ci] < 0)
                    {
                        continue;
                    }

                    int oc = a.ChildrenOf(oldRow)[colMap[ci]], nc = b.ChildrenOf(newRow)[ci];
                    Score(mine, oc, nc, 1, okMine, wrongMine, totalMine);
                    Score(reference, oc, nc, 1, okRef, wrongRef, scratch);
                }
            }

            Helpers.Clean(Comparison.Of(a, b));
        }

        string[] names = ["rows", "cells"];
        var report = new StringBuilder();
        for (int k = 0; k < 2; k++)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{names[k],-6} surviving={totalMine[k]} block aligner correct={okMine[k]} wrong={wrongMine[k]} | reference correct={okRef[k]} wrong={wrongRef[k]}");
        }

        string? path = Environment.GetEnvironmentVariable("ONEDIFF_ALIGN_REPORT");
        if (path is not null)
        {
            File.WriteAllText(path + ".tables", report.ToString());
        }

        for (int k = 0; k < 2; k++)
        {
            Assert.True(okMine[k] >= okRef[k], report.ToString());
            Assert.True(okMine[k] >= totalMine[k] * 0.99, report.ToString());
        }
    }

    private static int RowId(CompareDocument d, int row) => d.ChildrenOf(d.ChildrenOf(0)[0])[row];

    private static void Score(Alignment al, int oldNode, int newNode, int slot, int[] ok, int[] wrong, int[] total)
    {
        total[slot]++;
        AlignmentLink? l = al.LinkOfOld(oldNode);
        if (l is not null && l.NewNodes.Length == 1 && l.NewNodes[0] == newNode)
        {
            ok[slot]++;
        }
        else if (l is not null)
        {
            wrong[slot]++;
        }
    }
}
