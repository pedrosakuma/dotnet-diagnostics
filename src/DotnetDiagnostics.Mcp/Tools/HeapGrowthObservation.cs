using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Evidence;

namespace DotnetDiagnostics.Mcp.Tools;

internal sealed record HeapGrowthObservation(
    string Schema,
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
    IReadOnlyList<HeapTypeGrowth> Growers)
{
    public const string SchemaV2 = "dotnet-diagnostics/mcp-heap-growth-observation/v2";

    public IReadOnlyList<string>? Notes { get; init; }

    public EvidenceQuality? BaselineQuality { get; init; }

    public EvidenceQuality? CurrentQuality { get; init; }

    public static HeapGrowthObservation From(HeapGrowthResult growth)
    {
        ArgumentNullException.ThrowIfNull(growth);

        return new HeapGrowthObservation(
            SchemaV2,
            growth.BaselineHandle,
            growth.CurrentHandle,
            growth.ProcessId,
            growth.BaselineCapturedAt,
            growth.CurrentCapturedAt,
            growth.Elapsed,
            growth.RankBy,
            growth.MinDeltaPct,
            growth.BaselineHeapBytes,
            growth.CurrentHeapBytes,
            growth.TotalHeapGrowthBytes,
            growth.TotalGrowers,
            growth.Growers)
        {
            Notes = growth.Notes,
            BaselineQuality = growth.BaselineQuality,
            CurrentQuality = growth.CurrentQuality,
        };
    }
}
