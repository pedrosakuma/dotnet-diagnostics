using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Comparison;

public sealed record HistoricalCaptureReference(string CaptureId, string ArtifactId);

public sealed record HistoricalComparisonRequest(
    HistoricalCaptureReference Baseline, HistoricalCaptureReference Candidate);

public sealed record HistoricalComparisonReason(string Code, string Side, string Field, string Message);
public sealed record HistoricalCompatibility(string Status, IReadOnlyList<HistoricalComparisonReason> Reasons);
public sealed record HistoricalCaptureIdentity(
    HistoricalCaptureReference Reference, string Kind, CaptureArtifactProvenance? Provenance,
    PortableCaptureSource? ClaimedPortableSource, string? SourceArtifactId, string? DerivedFrom,
    DateTimeOffset? WindowStart, TimeSpan? ReportedDuration, string WindowSemantics)
{
    public CpuSampling.CpuSampleEvidence? CpuEvidence { get; init; }
    public Dump.HeapSnapshotOrigin? HeapOrigin { get; init; }
    public Dump.DumpRuntimeInfo? HeapRuntime { get; init; }
}
public sealed record HistoricalSideQuality(
    CaptureQuality Capture, DurableCaptureRecordStreamInfo? RecordStream, IReadOnlyList<string> Notes)
{
    public Evidence.EvidenceQuality? Evidence { get; init; }
}
public sealed record HistoricalQuality(HistoricalSideQuality Left, HistoricalSideQuality Right);

/// <summary>Decimal arithmetic preserves signed 64-bit integer differences; relative delta is a ratio, not percent.</summary>
public sealed record HistoricalMetric(
    string Key, string? Unit, string SemanticsVersion, string Population, string Aggregation, string Normalization,
    decimal? LeftValue, decimal? RightValue, decimal? AbsoluteDelta, decimal? RelativeDelta, string? UnavailableReason)
{
    public decimal? LeftDenominator { get; init; }
    public decimal? RightDenominator { get; init; }
    public string? LeftUnit { get; init; }
    public string? RightUnit { get; init; }
    public string? LeftAggregation { get; init; }
    public string? RightAggregation { get; init; }
}

public sealed record HistoricalComparisonResult(
    string Schema, HistoricalCaptureIdentity Left, HistoricalCaptureIdentity Right,
    HistoricalCompatibility Compatibility, HistoricalQuality Quality, IReadOnlyList<HistoricalMetric> Metrics)
{
    public const string SchemaV1 = "dotnet-diagnostics/historical-comparison/v1";
}

public delegate ValueTask AuthorizeHistoricalCapture(
    CaptureInfo capture, CaptureArtifactInfo artifact, string view, CancellationToken cancellationToken);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(HistoricalComparisonRequest))]
[JsonSerializable(typeof(HistoricalComparisonResult))]
public sealed partial class HistoricalComparisonJsonContext : JsonSerializerContext;
