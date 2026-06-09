using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Comparison;

namespace DotnetDiagnostics.Mcp.Resources;

internal sealed record JourneyDiffResourceErrorPayload(string Kind, string Error);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SnapshotJourneyDiff))]
[JsonSerializable(typeof(MetricSeries))]
[JsonSerializable(typeof(KeyMatrixRow))]
[JsonSerializable(typeof(PairwiseJourney))]
[JsonSerializable(typeof(PairwiseComparison))]
[JsonSerializable(typeof(DispersionStats))]
[JsonSerializable(typeof(MetricDefinition))]
[JsonSerializable(typeof(ComparableKey))]
[JsonSerializable(typeof(JourneyDiffResourceErrorPayload))]
internal sealed partial class JourneyDiffResourceJsonContext : JsonSerializerContext;
