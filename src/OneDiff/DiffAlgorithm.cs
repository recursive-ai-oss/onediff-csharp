namespace OneDiff;

/// <summary>Which algorithm finds the alignment between two sequences.</summary>
public enum DiffAlgorithm
{
    /// <summary>Myers' O(ND) algorithm in linear space: a minimal edit script (fewest inserted plus deleted items). The default.</summary>
    Myers = 0,

    /// <summary>Patience diff: anchors on items that occur exactly once on each side, which tends to read better on code and prose.</summary>
    Patience = 1,

    /// <summary>Histogram diff: anchors on the least frequent common items; patience's idea made robust to repeated items.</summary>
    Histogram = 2,
}
