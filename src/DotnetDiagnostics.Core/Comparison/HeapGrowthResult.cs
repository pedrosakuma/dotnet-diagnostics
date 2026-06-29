using DotnetDiagnostics.Core.Dump;

namespace DotnetDiagnostics.Core.Comparison;

/// <summary>
/// Output of <see cref="HeapGrowthDiff.Build"/>: a retention-aware live heap growth report
/// (issue #463). Carries provenance for both captures plus the per-type growth rows ranked by
/// absolute growth in the requested dimension.
/// </summary>
public sealed record HeapGrowthResult(
    string BaselineHandle,
    string CurrentHandle,
    int ProcessId,
    DateTimeOffset BaselineCapturedAt,
    DateTimeOffset CurrentCapturedAt,
    TimeSpan Elapsed,
    string RankBy,
    double MinDeltaPct,
    long BaselineHeapBytes,
    long CurrentHeapBytes,
    long TotalHeapGrowthBytes,
    int TotalGrowers,
    IReadOnlyList<HeapTypeGrowth> Growers,
    string Verdict)
{
    /// <summary>Diagnostic notes (cross-process comparison, missing retention paths, …).</summary>
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>
/// One managed type that grew between the baseline and current heap snapshots.
/// <see cref="RetentionPaths"/> is populated from the <c>current</c> snapshot when it was
/// captured with retention paths enabled — the "what's holding them" drill-down.
/// </summary>
public sealed record HeapTypeGrowth(
    string TypeFullName,
    string? ModuleName,
    long BaselineBytes,
    long CurrentBytes,
    long BytesDelta,
    double BytesDeltaPercent,
    long BaselineInstances,
    long CurrentInstances,
    long InstancesDelta,
    double InstancesDeltaPercent,
    bool IsNew)
{
    /// <summary>Cross-MCP type identity (mvid + token) for handoff to dotnet-assembly-mcp.</summary>
    public TypeIdentity? Identity { get; init; }

    /// <summary>Retention chains from the current snapshot whose target type matches this grower.</summary>
    public IReadOnlyList<RetentionPath>? RetentionPaths { get; init; }
}
