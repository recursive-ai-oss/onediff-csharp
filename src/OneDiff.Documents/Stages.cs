using System.Collections.Immutable;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Scoring;
using OneDiff.Documents.Verification;

namespace OneDiff.Documents;

/// <summary>
/// The five stages of a comparison as pure functions over immutable values: <see cref="Align"/>, <see cref="DiffPairs"/>,
/// <see cref="Group"/>, <see cref="Classify"/> and <see cref="Verify"/>. Each reads the settings (and with them the cost cap)
/// from the value it is given, observes the cancellation token, and is safe to call from many threads on shared inputs. A stage
/// that hits the cost cap returns a coarser result that is still complete, with a diagnostic; nothing here throws for a cap.
/// The implementations in this class are the reference stages; the values they exchange are the contract, so any stage can be
/// replaced by one that returns the same types, and <see cref="Verify"/> checks whatever it gets.
/// <code>
/// Alignment al = Stages.Align(a, b);
/// al = al.Pin(oldNode, newNode);                    // a reviewer's correction
/// Comparison c = Stages.Verify(Stages.Classify(Stages.Group(Stages.DiffPairs(al))));
/// </code>
/// </summary>
public static class Stages
{
    /// <summary>
    /// Pairs the nodes of two documents: equal subtrees in order first, then equal subtrees moved elsewhere, then edited text
    /// blocks (found through a shingle index, judged by the scorer where the cheap measure is unsure, chosen by a global
    /// assignment), splits and merges, and finally tables, rows, cells, sections and notes by how much of their content matched
    /// and by position. The result is normalized (matches and moves are settled). The work is near-linear in the size of the
    /// documents; past <see cref="CompareSettings.CostCap"/> the search for edited blocks is skipped and a diagnostic
    /// (<c>align.cap</c>) says so, leaving a complete but coarser alignment.
    /// </summary>
    /// <param name="oldDocument">The old document.</param>
    /// <param name="newDocument">The new document.</param>
    /// <param name="scorer">Judges candidate pairs of text blocks the cheap measure cannot settle, in batches; <see cref="RulesPairScorer"/> when null.</param>
    /// <param name="settings">Thresholds, hidden categories and the cost cap; <see cref="CompareSettings.Legal"/> when null.</param>
    /// <param name="seed">An earlier alignment of the same two documents whose pinned links are kept; the rest is aligned afresh.</param>
    /// <param name="cancellationToken">Observed during the search; cancellation throws <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="ArgumentException">The settings are invalid, or <paramref name="seed"/> belongs to other documents.</exception>
    public static Alignment Align(
        CompareDocument oldDocument,
        CompareDocument newDocument,
        IPairScorer? scorer = null,
        CompareSettings? settings = null,
        Alignment? seed = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldDocument);
        ArgumentNullException.ThrowIfNull(newDocument);
        if (oldDocument.Kind != newDocument.Kind)
        {
            throw new ArgumentException("documents of different kinds cannot be compared");
        }

        return BlockAligner.Run(oldDocument, newDocument, scorer, settings ?? CompareSettings.Default, seed, cancellationToken);
    }

    /// <summary>
    /// Finds the edits inside every link of the alignment: a word-level diff of the compared text, formatting differences of equal
    /// text and attribute differences, plus the ledger of differences the settings hide. Over the cost cap a link is diffed coarsely.
    /// </summary>
    /// <param name="alignment">The alignment; its settings apply.</param>
    /// <param name="cancellationToken">Observed per link and during diffs.</param>
    public static PairDiffs DiffPairs(Alignment alignment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alignment);
        return ReferenceDiff.Run(alignment, false, cancellationToken);
    }

    /// <summary>
    /// Turns edits into changes and changes into display groups. The reference grouping makes one change per diff region and one
    /// group per change. Grouping only arranges; it never alters what a change says.
    /// </summary>
    /// <param name="diffs">The output of <see cref="DiffPairs"/>.</param>
    /// <param name="cancellationToken">Observed per link.</param>
    public static ChangeSet Group(PairDiffs diffs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diffs);
        return ReferenceGrouper.Group(diffs, cancellationToken);
    }

    /// <summary>Records who decided each change. The reference classification is plain: kinds stay as grouped, every decision is the reference rule at score 1, nothing is typed.</summary>
    /// <param name="changes">The output of <see cref="Group"/>.</param>
    /// <param name="cancellationToken">Observed per change.</param>
    public static ChangeSet Classify(ChangeSet changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return ReferenceGrouper.Classify(changes, cancellationToken);
    }

    /// <summary>
    /// Proves the changes complete and returns the <see cref="Comparison"/>. The checks do not trust any earlier stage: node
    /// accounting, replay of the edits in both directions over the compared content, parents and sibling order, sides and anchors,
    /// the grouping, and the ledger. When a check fails the result falls back stepwise to a coarser but complete grouping, each
    /// step checked again: 1, the reference stages on the same alignment; 2, every changed block replaced as a whole; 3, nothing
    /// aligned. The failures and the fallback taken are diagnostics, and <see cref="VerificationResult.FallbackLevel"/> records
    /// the level.
    /// </summary>
    /// <param name="changes">The output of <see cref="Classify"/> or of a custom stage.</param>
    /// <param name="cancellationToken">Observed throughout.</param>
    /// <exception cref="ComparisonVerificationException">A check failed and the settings have <see cref="CompareSettings.ThrowOnFailure"/> set, or even the coarsest result failed (a defect in the documents themselves).</exception>
    public static Comparison Verify(ChangeSet changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Alignment al = changes.Alignment;
        List<VerificationFailure> failures = Verifier.Check(al, changes.Changes, changes.Groups, changes.Ledger, cancellationToken);
        if (failures.Count == 0)
        {
            return Build(changes, 0, 0, changes.Diagnostics);
        }

        if (al.Settings.ThrowOnFailure)
        {
            throw new ComparisonVerificationException(failures);
        }

        var diagnostics = new List<Diagnostic>(changes.Diagnostics);
        AddFailures(diagnostics, failures, 0);
        int firstCount = failures.Count;
        for (int level = 1; level <= 3; level++)
        {
            ChangeSet fallback = level switch
            {
                1 => Reference(al, false, cancellationToken),
                2 => Reference(al, true, cancellationToken),
                _ => Reference(Alignment.Create(al.Old, al.New, al.Settings, [], al.Diagnostics), true, cancellationToken),
            };
            List<VerificationFailure> again = Verifier.Check(fallback.Alignment, fallback.Changes, fallback.Groups, fallback.Ledger, cancellationToken);
            if (again.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "verify.fallback",
                    level switch
                    {
                        1 => "verification failed; the changes were regrouped with the reference stages",
                        2 => "verification failed; each changed block is shown as a whole replacement",
                        _ => "verification failed; nothing is aligned, so every block is shown as deleted and inserted",
                    }));
                return Build(fallback, level, firstCount, diagnostics);
            }

            AddFailures(diagnostics, again, level);
            failures = again;
        }

        throw new ComparisonVerificationException(failures);
    }

    private static ChangeSet Reference(Alignment alignment, bool coarse, CancellationToken ct)
    {
        PairDiffs diffs = ReferenceDiff.Run(alignment, coarse, ct);
        return ReferenceGrouper.Classify(ReferenceGrouper.Group(diffs, ct), ct);
    }

    private static void AddFailures(List<Diagnostic> diagnostics, List<VerificationFailure> failures, int level)
    {
        foreach (VerificationFailure f in failures)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "verify." + f.Code, level == 0 ? f.Message : $"(fallback level {level}) {f.Message}"));
        }
    }

    private static Comparison Build(ChangeSet set, int level, int failureCount, IEnumerable<Diagnostic> diagnostics)
    {
        CompareDocument o = set.Alignment.Old, n = set.Alignment.New;
        var provenance = new Provenance(Comparison.LibraryVersion, 1, o.Name, n.Name, o.SourceHash, n.SourceHash);
        return new Comparison(
            set.Alignment,
            set.Changes,
            set.Groups,
            set.Ledger,
            [.. diagnostics],
            provenance,
            new VerificationResult(true, level, failureCount));
    }
}
