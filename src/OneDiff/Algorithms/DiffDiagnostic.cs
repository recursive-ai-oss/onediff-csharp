namespace OneDiff.Algorithms;

/// <summary>
/// What an algorithm reports about its own run. Every script is valid whatever this says; the diagnostic tells the caller
/// whether the script is the best the algorithm can give or a coarser one produced because the work cap was reached.
/// </summary>
/// <param name="CostCapReached">
/// True when the search stopped early because it would have spent more than the allowed work. The script is still valid
/// (it turns the old sequence into the new one) but, for Myers, no longer guaranteed minimal: the parts of the sequences
/// that were not searched are reported as one deletion followed by one insertion.
/// </param>
/// <param name="Work">
/// Work spent, in edit-graph steps: one per diagonal visited plus one per item matched while following a snake
/// (Myers 1986), one per item scanned when the anchor-based algorithms look for anchors, and one per item of the shared head and tail
/// that is trimmed before the search (which is not charged against the cap). An estimate of cost, not a time; it is zero when there was nothing
/// to compare (an empty side) or the diff was not computed (<see cref="TextDiff.Create"/>).
/// </param>
public readonly record struct DiffDiagnostic(bool CostCapReached, long Work);
