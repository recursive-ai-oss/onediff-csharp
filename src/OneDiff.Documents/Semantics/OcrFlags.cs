using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Semantics;

/// <summary>
/// Finds the lowest recognition confidence among the words a change's edits touch. A word that an OCR source read carries its
/// confidence in the <see cref="AnchorKind.OcrWords"/> anchor of its segment; the anchor of just the edited range of each side is
/// resolved the way verification resolves a side's anchor, so words next to the edit that merely sit in the same unit do not count.
/// A whole block that was inserted or deleted has no edits, and then every word of its side counts.
/// </summary>
internal static class OcrFlags
{
    /// <summary>The lowest confidence over the edits of <paramref name="change"/> on both sides, or null when no touched word has one.</summary>
    public static double? LowestConfidence(Change change, Alignment alignment)
    {
        if (change.Kind is ChangeKind.Move or ChangeKind.Format or ChangeKind.Attribute)
        {
            return null;
        }

        // Documents that were not read by an OCR source have no such anchors: skip the projection work for them.
        bool oldOcr = change.Old.IsPresent && change.Old.Anchor.Kind == AnchorKind.OcrWords;
        bool newOcr = change.New.IsPresent && change.New.Anchor.Kind == AnchorKind.OcrWords;
        if (!oldOcr && !newOcr)
        {
            return null;
        }

        double? lowest = null;
        if (change.Edits.IsDefaultOrEmpty)
        {
            // A whole inserted or deleted block has no edits: every word of it differs.
            return Lowest(Lowest(null, change.Old.Anchor), change.New.Anchor);
        }

        if (oldOcr)
        {
            ProjectedGroup g = ProjectedGroup.Create(alignment.Old, change.Old.Nodes, alignment.Settings);
            foreach (RegionEdit e in change.Edits)
            {
                if (e.OldLength > 0)
                {
                    lowest = Lowest(lowest, g.ResolveAnchor(alignment.Old, e.OldStart, e.OldLength));
                }
            }
        }

        if (newOcr)
        {
            ProjectedGroup g = ProjectedGroup.Create(alignment.New, change.New.Nodes, alignment.Settings);
            foreach (RegionEdit e in change.Edits)
            {
                if (e.NewLength > 0)
                {
                    lowest = Lowest(lowest, g.ResolveAnchor(alignment.New, e.NewStart, e.NewLength));
                }
            }
        }

        return lowest;
    }

    private static double? Lowest(double? so, Anchor a) =>
        a.Kind == AnchorKind.OcrWords && a.Confidence is double c ? (so is double m ? Math.Min(m, c) : c) : so;
}
