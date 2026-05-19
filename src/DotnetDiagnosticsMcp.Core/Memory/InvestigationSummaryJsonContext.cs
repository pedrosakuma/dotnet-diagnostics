using System.Text.Json.Serialization;

namespace DotnetDiagnosticsMcp.Core.Memory;

/// <summary>
/// System.Text.Json source-generated metadata for the investigation summary graph.
/// Used by <see cref="InvestigationSummaryExporter"/> so the summary can be serialized
/// under <c>PublishTrimmed</c> / <c>PublishAot</c> without falling back to reflection.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InvestigationSummary))]
[JsonSerializable(typeof(InvestigationProvenance))]
[JsonSerializable(typeof(BuildProvenance))]
[JsonSerializable(typeof(ContainerProvenance))]
[JsonSerializable(typeof(InvestigationFindings))]
[JsonSerializable(typeof(HotspotSummary))]
[JsonSerializable(typeof(SymbolRef))]
[JsonSerializable(typeof(SourceLocation))]
[JsonSerializable(typeof(MethodIdentity))]
[JsonSerializable(typeof(GenericInstantiation))]
[JsonSerializable(typeof(InvestigationFixTarget))]
public sealed partial class InvestigationSummaryJsonContext : JsonSerializerContext;
