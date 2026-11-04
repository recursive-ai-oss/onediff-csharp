namespace OneDiff.Algorithms;

/// <summary>The work spent so far and the most allowed; shared by every search that belongs to one comparison.</summary>
internal sealed class Budget(long cap)
{
    public const long DefaultCap = 1L << 31;

    public long Cap { get; } = cap;

    public long Work;

    public bool Exceeded;

    public bool IsSpent => Work > Cap;

    public DiffDiagnostic ToDiagnostic() => new(Exceeded, Work);

    public static void CheckCap(long maxWork) => ArgumentOutOfRangeException.ThrowIfNegative(maxWork);
}
