using System.Collections.Immutable;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Scoring;
using OneDiff.Documents.Verification;

namespace OneDiff.Documents.Combine;

/// <summary>One step of a version chain: the comparison of version <c>Index</c> with the next.</summary>
/// <param name="Index">The step's number, from 0: step 0 compares the first version with the second.</param>
/// <param name="FromName">The earlier version's name.</param>
/// <param name="ToName">The later version's name.</param>
/// <param name="Comparison">The verified comparison of the two.</param>
public sealed record ChainStep(int Index, string FromName, string ToName, Comparison Comparison);

/// <summary>
/// A version chain v1 → v2 → … → vN: one verified <see cref="Comparison"/> per step, so every change belongs to the step that
/// made it, and the net comparison v1 → vN, itself verified, with <see cref="StepsOf"/> saying which steps touched each net
/// change. The steps compose: step <c>i</c>'s new document is step <c>i + 1</c>'s old document, the very same object. Immutable.
/// </summary>
public sealed class ComparisonChain
{
    private readonly ImmutableArray<Dictionary<int, int[]>> forward;
    private readonly ImmutableArray<Dictionary<int, int[]>> backward;

    private ComparisonChain(ImmutableArray<CompareDocument> versions, ImmutableArray<ChainStep> steps, Comparison net, ImmutableArray<Diagnostic> diagnostics)
    {
        Versions = versions;
        Steps = steps;
        Net = net;
        Diagnostics = diagnostics;
        var f = ImmutableArray.CreateBuilder<Dictionary<int, int[]>>();
        var b = ImmutableArray.CreateBuilder<Dictionary<int, int[]>>();
        foreach (ChainStep s in steps)
        {
            var fw = new Dictionary<int, List<int>>();
            var bw = new Dictionary<int, List<int>>();
            foreach (AlignmentLink l in s.Comparison.Alignment.Links)
            {
                foreach (int o in l.OldNodes)
                {
                    if (!fw.TryGetValue(o, out List<int>? x))
                    {
                        fw[o] = x = [];
                    }

                    x.AddRange(l.NewNodes);
                }

                foreach (int n in l.NewNodes)
                {
                    if (!bw.TryGetValue(n, out List<int>? x))
                    {
                        bw[n] = x = [];
                    }

                    x.AddRange(l.OldNodes);
                }
            }

            f.Add(fw.ToDictionary(p => p.Key, p => p.Value.ToArray()));
            b.Add(bw.ToDictionary(p => p.Key, p => p.Value.ToArray()));
        }

        forward = f.ToImmutable();
        backward = b.ToImmutable();
    }

    /// <summary>The versions, oldest first, at least two.</summary>
    public ImmutableArray<CompareDocument> Versions { get; }

    /// <summary>The steps: version <c>i</c> → version <c>i + 1</c>.</summary>
    public ImmutableArray<ChainStep> Steps { get; }

    /// <summary>The net comparison of the first version with the last.</summary>
    public Comparison Net { get; }

    /// <summary>What the step comparisons and the net comparison had to say.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>Compares each version with the next and the first with the last, with the reference stages.</summary>
    /// <param name="versions">The versions, oldest first; at least two. The same document object may not appear twice.</param>
    /// <param name="settings">What to show and hide; <see cref="CompareSettings.Legal"/> when null.</param>
    /// <param name="scorer">Scores ambiguous pairs; word overlap when null.</param>
    /// <param name="cancellationToken">Observed between and inside the comparisons.</param>
    /// <exception cref="ArgumentException">There are fewer than two versions.</exception>
    /// <exception cref="ComparisonVerificationException">A comparison failed verification and the settings ask for an exception.</exception>
    public static ComparisonChain Of(IReadOnlyList<CompareDocument> versions, CompareSettings? settings = null, IPairScorer? scorer = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (versions.Count < 2)
        {
            throw new ArgumentException("A chain needs at least two versions.", nameof(versions));
        }

        var steps = ImmutableArray.CreateBuilder<ChainStep>();
        var diagnostics = new List<Diagnostic>();
        for (int i = 0; i + 1 < versions.Count; i++)
        {
            Comparison c = Comparison.Of(versions[i], versions[i + 1], settings, scorer, cancellationToken);
            steps.Add(new ChainStep(i, versions[i].Name, versions[i + 1].Name, c));
            foreach (Diagnostic d in c.Diagnostics)
            {
                diagnostics.Add(d with { Code = $"step{i}.{d.Code}" });
            }
        }

        Comparison net = steps.Count == 1 ? steps[0].Comparison : Comparison.Of(versions[0], versions[^1], settings, scorer, cancellationToken);
        return new ComparisonChain([.. versions], steps.ToImmutable(), net, [.. diagnostics]);
    }

    /// <summary>
    /// Re-runs the checks: every step and the net comparison are complete, each step's new document is the next step's old
    /// document, and the net comparison runs from the first version to the last.
    /// </summary>
    /// <returns>The failures; empty when everything holds.</returns>
    public ImmutableArray<VerificationFailure> Check(CancellationToken cancellationToken = default)
    {
        var f = new List<VerificationFailure>();
        for (int i = 0; i < Steps.Length; i++)
        {
            Comparison c = Steps[i].Comparison;
            if (!ReferenceEquals(c.Old, Versions[i]) || !ReferenceEquals(c.New, Versions[i + 1]))
            {
                f.Add(new VerificationFailure("H001", $"step {i} does not compare versions {i} and {i + 1}"));
            }

            foreach (VerificationFailure v in c.Check(cancellationToken))
            {
                f.Add(new VerificationFailure("H002", $"step {i}: {v.Code} {v.Message}"));
            }
        }

        if (!ReferenceEquals(Net.Old, Versions[0]) || !ReferenceEquals(Net.New, Versions[^1]))
        {
            f.Add(new VerificationFailure("H003", "the net comparison does not run from the first version to the last"));
        }

        foreach (VerificationFailure v in Net.Check(cancellationToken))
        {
            f.Add(new VerificationFailure("H004", $"net: {v.Code} {v.Message}"));
        }

        return [.. f];
    }

    /// <summary>
    /// The steps that touched a change of the net comparison, ascending. A step touches it when one of its changes concerns a
    /// block that the net change's old blocks became (followed forward through the steps' alignments) or that the net change's
    /// new blocks came from (followed backward), when one of those blocks is deleted or created in that step (a block deleted
    /// with its parent has no change of its own). The grain is the block: two edits to one paragraph in different steps are
    /// both attributed to both steps, and the steps' own comparisons say which step made which edit.
    /// </summary>
    /// <param name="netChangeId">A change id of <see cref="Net"/>.</param>
    /// <exception cref="ArgumentException">No such change.</exception>
    public ImmutableArray<int> StepsOf(int netChangeId)
    {
        Change? net = Net.Changes.FirstOrDefault(c => c.Id == netChangeId) ?? throw new ArgumentException("No such net change.", nameof(netChangeId));
        var touched = new SortedSet<int>();
        if (net.Old.IsPresent)
        {
            HashSet<int> set = [.. net.Old.Nodes];
            for (int s = 0; s < Steps.Length && set.Count > 0; s++)
            {
                if (set.Any(n => !forward[s].ContainsKey(n)) || Steps[s].Comparison.Changes.Any(c => c.Old.IsPresent && c.Old.Nodes.Any(set.Contains)))
                {
                    touched.Add(s);
                }

                set = Map(set, forward[s]);
            }
        }

        if (net.New.IsPresent)
        {
            HashSet<int> set = [.. net.New.Nodes];
            for (int s = Steps.Length - 1; s >= 0 && set.Count > 0; s--)
            {
                if (set.Any(n => !backward[s].ContainsKey(n)) || Steps[s].Comparison.Changes.Any(c => c.New.IsPresent && c.New.Nodes.Any(set.Contains)))
                {
                    touched.Add(s);
                }

                set = Map(set, backward[s]);
            }
        }

        if (net.Kind == ChangeKind.Move)
        {
            // Moving one block changes the relative order of the others, so the net comparison may show a different block as moved
            // than the step did: every step that moved anything is a candidate.
            for (int s = 0; s < Steps.Length; s++)
            {
                if (Steps[s].Comparison.Changes.Any(c => c.Kind == ChangeKind.Move))
                {
                    touched.Add(s);
                }
            }
        }

        if (touched.Count == 0)
        {
            // The net comparison paired interchangeable blocks (say, empty cells) differently from the steps, so no lineage reaches
            // a change: the steps that made a change of the same kind are the candidates, and failing that, any step that changed anything.
            for (int s = 0; s < Steps.Length; s++)
            {
                if (Steps[s].Comparison.Changes.Any(c => c.Kind == net.Kind))
                {
                    touched.Add(s);
                }
            }

            if (touched.Count == 0)
            {
                for (int s = 0; s < Steps.Length; s++)
                {
                    if (!Steps[s].Comparison.Changes.IsEmpty || Steps[s].Comparison.Alignment.Links.Any(l => !l.OldNodes.SequenceEqual(l.NewNodes)))
                    {
                        touched.Add(s);
                    }
                }
            }
        }

        return [.. touched];
    }

    private static HashSet<int> Map(HashSet<int> from, Dictionary<int, int[]> map)
    {
        var to = new HashSet<int>();
        foreach (int n in from)
        {
            if (map.TryGetValue(n, out int[]? t))
            {
                foreach (int x in t)
                {
                    to.Add(x);
                }
            }
        }

        return to;
    }

    /// <summary>Writes the chain's change report page or its JSON form, by the extension: <c>.html</c> or <c>.json</c>.</summary>
    /// <param name="path">The output file; replaced if it exists. The directory must exist.</param>
    /// <exception cref="NotSupportedException">The extension is neither <c>.html</c>, <c>.htm</c> nor <c>.json</c>.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public void SaveAs(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string ext = Path.GetExtension(path);
        if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllBytes(path, CombineJson.Write(this));
        }
        else if (string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, ChangeReport.From(this).ToHtml(), new System.Text.UTF8Encoding(false));
        }
        else
        {
            throw new NotSupportedException($"No chain output form for '{ext}'; .html and .json are built in.");
        }
    }
}
