using System.Reflection;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Running;

namespace OneDiff.Bench;

/// <summary>Parts of the library the rows depend on.</summary>
internal enum Feature
{
    /// <summary>Cleanup (semantic and efficiency).</summary>
    Cleanup,

    /// <summary>Patches in diff-match-patch's format.</summary>
    Patches,
}

/// <summary>
/// What the library has today. A row that needs a missing feature is not run and shows as pending in the table; to switch it on,
/// write the library side of its benchmark method and set the flag. Rows of algorithms whose real implementation has not landed
/// still run (to exercise the harness) but are marked placeholders and never count toward a verdict.
/// </summary>
internal static class Availability
{
    /// <summary>True once the Myers, patience and histogram implementations are the real ones, not the prefix/suffix stand-in.</summary>
    public static bool AlgorithmsAreReal => true;

    public static bool Cleanup => true;

    public static bool Patches => false;

    public static bool Has(Feature feature) => feature switch
    {
        Feature.Cleanup => Cleanup,
        Feature.Patches => Patches,
        _ => false,
    };

    public static string Describe(Feature feature) => feature switch
    {
        Feature.Cleanup => "cleanup",
        Feature.Patches => "patches",
        _ => feature.ToString(),
    };
}

/// <summary>Marks a benchmark method as needing a library feature that may not exist yet.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class NeedsAttribute(Feature feature) : Attribute
{
    public Feature Feature { get; } = feature;
}

/// <summary>Leaves out benchmark methods whose library feature is not available yet.</summary>
internal sealed class AvailabilityFilter : IFilter
{
    public bool Predicate(BenchmarkCase benchmarkCase)
    {
        NeedsAttribute? needs = benchmarkCase.Descriptor.WorkloadMethod.GetCustomAttribute<NeedsAttribute>();
        return needs is null || Availability.Has(needs.Feature);
    }
}
