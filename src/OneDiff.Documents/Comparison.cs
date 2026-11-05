using System.Collections.Immutable;
using OneDiff.Documents.Ledger;
using OneDiff.Documents.Model;
using OneDiff.Documents.Scoring;
using OneDiff.Documents.Pipeline;
using OneDiff.Documents.Verification;

namespace OneDiff.Documents;

/// <summary>Where a comparison came from, enough to tell whether two comparisons are of the same inputs under the same library.</summary>
/// <param name="LibraryVersion">The version of this library that produced the comparison.</param>
/// <param name="SchemaVersion">The version of the JSON schema this value is written in.</param>
/// <param name="OldName">The old document's display name.</param>
/// <param name="NewName">The new document's display name.</param>
/// <param name="OldHash">The SHA-256 (hex) of the old input, or empty for a document built by hand.</param>
/// <param name="NewHash">The SHA-256 (hex) of the new input, or empty.</param>
public sealed record Provenance(string LibraryVersion, int SchemaVersion, string OldName, string NewName, string OldHash, string NewHash);

/// <summary>How verification went.</summary>
/// <param name="Passed">True when the comparison passed every check (possibly after falling back) <b>in this process</b>: it is false for a comparison read from JSON with verification off, because the stored claim was not checked here.</param>
/// <param name="FallbackLevel">0: the stages' own result was complete. 1: regrouped with the reference stages on the same alignment. 2: each changed block replaced as a whole. 3: nothing aligned; every block deleted and inserted.</param>
/// <param name="FailureCount">How many checks failed before the result that was kept (0 when no fallback ran).</param>
/// <param name="VerifiedHere">True when the checks ran in this process on this value: always for a comparison the stages made; for one read from JSON, only when the reader was asked to verify (the default). False means <see cref="Passed"/> says nothing about the content: call <see cref="Comparison.Check"/>, or read the JSON again with verification.</param>
public sealed record VerificationResult(bool Passed, int FallbackLevel, int FailureCount, bool VerifiedHere = true);

/// <summary>
/// The result of comparing two documents: both compare models, the alignment between them, the atomic changes, the display groups
/// over them, the ledger of hidden differences, diagnostics and provenance. It is complete by construction: old with the
/// changes is new and new without them is old (over the compare model as the settings project it), and every other
/// difference is in the ledger. <see cref="Check"/> verifies that again at any time. Immutable.
/// </summary>
public sealed partial class Comparison
{
    /// <summary>The library version written into provenance.</summary>
    public const string LibraryVersion = BuildVersion.Value;

    internal Comparison(
        Alignment alignment,
        ImmutableArray<Change> changes,
        ImmutableArray<ChangeGroup> groups,
        ChangeLedger ledger,
        ImmutableArray<Diagnostic> diagnostics,
        Provenance provenance,
        VerificationResult verification)
    {
        Alignment = alignment;
        Changes = changes;
        Groups = groups;
        Ledger = ledger;
        Diagnostics = diagnostics;
        Provenance = provenance;
        Verification = verification;
    }

    /// <summary>The old document.</summary>
    public CompareDocument Old => Alignment.Old;

    /// <summary>The new document.</summary>
    public CompareDocument New => Alignment.New;

    /// <summary>The settings the comparison was made under.</summary>
    public CompareSettings Settings => Alignment.Settings;

    /// <summary>The alignment that was kept (after any fallback).</summary>
    public Alignment Alignment { get; }

    /// <summary>The atomic changes in comparison order; each has a unique id.</summary>
    public ImmutableArray<Change> Changes { get; }

    /// <summary>The display groups; every change is in exactly one.</summary>
    public ImmutableArray<ChangeGroup> Groups { get; }

    /// <summary>The differences the settings hide, by category.</summary>
    public ChangeLedger Ledger { get; }

    /// <summary>Everything the stages and verification had to say: caps hit, failures recovered from, fallbacks taken.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>Inputs and library version.</summary>
    public Provenance Provenance { get; }

    /// <summary>How verification went.</summary>
    public VerificationResult Verification { get; }

    /// <summary>Compares two documents with the reference stages: align, diff pairs, group, classify, verify.</summary>
    /// <param name="oldDocument">The old document.</param>
    /// <param name="newDocument">The new document.</param>
    /// <param name="settings">What to show and hide; <see cref="CompareSettings.Legal"/> when null.</param>
    /// <param name="scorer">Scores ambiguous pairs; word overlap when null.</param>
    /// <param name="cancellationToken">Observed between stages and inside them.</param>
    /// <exception cref="ComparisonVerificationException">Verification failed and the settings ask for an exception instead of a fallback.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static Comparison Of(CompareDocument oldDocument, CompareDocument newDocument, CompareSettings? settings = null, IPairScorer? scorer = null, CancellationToken cancellationToken = default)
    {
        Alignment a = Stages.Align(oldDocument, newDocument, scorer, settings, null, cancellationToken);
        return Stages.Verify(Stages.Classify(Stages.Group(Stages.DiffPairs(a, cancellationToken), cancellationToken), cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Re-runs the completeness checks on this comparison (for example after reading it from JSON with <c>verify: false</c>). The
    /// comparison is immutable: this returns the failures and leaves <see cref="Verification"/> as it was.
    /// </summary>
    /// <returns>The failures; empty when everything holds.</returns>
    public ImmutableArray<VerificationFailure> Check(CancellationToken cancellationToken = default) =>
        [.. Verifier.Check(Alignment, Changes, Groups, Ledger, cancellationToken)];
}
