using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.Comparison;

/// <summary>
/// System.Text.Json source-generated metadata for the <see cref="ComparableSnapshot"/> graph,
/// so persisted snapshots serialize under <c>PublishTrimmed</c> / <c>PublishAot</c> without
/// reflection. Enums are written as strings for stable, human-readable JSON.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ComparableSnapshot))]
[JsonSerializable(typeof(SnapshotJourneyDiff))]
[JsonSerializable(typeof(MetricSeries))]
[JsonSerializable(typeof(KeyMatrixRow))]
[JsonSerializable(typeof(PairwiseJourney))]
[JsonSerializable(typeof(PairwiseComparison))]
[JsonSerializable(typeof(DispersionStats))]
[JsonSerializable(typeof(MetricValue))]
[JsonSerializable(typeof(MetricDefinition))]
[JsonSerializable(typeof(ComparableRow))]
[JsonSerializable(typeof(ComparableKey))]
[JsonSerializable(typeof(InvestigationProvenance))]
[JsonSerializable(typeof(BuildProvenance))]
[JsonSerializable(typeof(ContainerProvenance))]
public sealed partial class ComparableSnapshotJsonContext : JsonSerializerContext;
