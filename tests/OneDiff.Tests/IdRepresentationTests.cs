namespace OneDiff.Tests;

public class IdRepresentationTests
{
    // The same equality structure written with different ids must give the same script: dense in first-seen order, shuffled dense,
    // sparse, and negative. (Diff.Characters feeds code points; Diff.Sequences feeds interned ids.)
    [Theory]
    [InlineData(DiffAlgorithm.Myers)]
    [InlineData(DiffAlgorithm.Patience)]
    [InlineData(DiffAlgorithm.Histogram)]
    public void ScriptDoesNotDependOnHowIdsAreWritten(DiffAlgorithm algorithm)
    {
        var random = new Random(77);
        for (int round = 0; round < 1500; round++)
        {
            int alphabet = random.Next(1, 12);
            int[] a = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(alphabet)).ToArray();
            int[] b = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(alphabet)).ToArray();
            EditScript baseline = Run(a, b, algorithm);
            int[] sparse = Enumerable.Range(0, alphabet).Select(i => 100_000 + (i * 7919 % 1013) * 1000 + i).ToArray();
            int[] negative = Enumerable.Range(0, alphabet).Select(i => -1 - i * 3).ToArray();
            int[] shuffled = Enumerable.Range(0, alphabet).OrderBy(_ => random.Next()).ToArray();
            foreach (int[] map in new[] { sparse, negative, shuffled })
            {
                EditScript other = Run(a.Select(x => map[x]).ToArray(), b.Select(x => map[x]).ToArray(), algorithm);
                Assert.True(baseline.Edits.ToArray().SequenceEqual(other.Edits.ToArray()), $"{algorithm}: [{string.Join(",", a)}] vs [{string.Join(",", b)}]");
            }
        }
    }

    [Fact]
    public void CharactersAgreeWithSequencesOnTheRecordedRepro()
    {
        string o = ".\U0001F600e\u0301\ngammaabxx\nbetagammaalpha\U0001F600 xx\r\nab";
        string n = ".\U0001F600e\u0301\ngammaabxx\n\U0001F600e\u0301\r\nab";
        int[] a = o.EnumerateRunes().Select(r => r.Value).ToArray();
        int[] b = n.EnumerateRunes().Select(r => r.Value).ToArray();
        EditScript byCodePoints = Algorithms.MyersDiff.Compute(a, b);
        EditScript bySequences = Diff.Sequences<int>(a, b);
        EditScript byText = Diff.Characters(o, n).Script;
        Assert.True(byCodePoints.Edits.ToArray().SequenceEqual(bySequences.Edits.ToArray()));
        Assert.True(byCodePoints.Edits.ToArray().SequenceEqual(byText.Edits.ToArray()));
    }

    private static EditScript Run(int[] a, int[] b, DiffAlgorithm algorithm) => algorithm switch
    {
        DiffAlgorithm.Myers => Algorithms.MyersDiff.Compute(a, b),
        DiffAlgorithm.Patience => Algorithms.PatienceDiff.Compute(a, b),
        _ => Algorithms.HistogramDiff.Compute(a, b),
    };
}
