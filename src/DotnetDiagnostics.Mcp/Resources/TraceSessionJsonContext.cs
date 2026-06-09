using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Mcp.Resources;

/// <summary>
/// Concrete projection record for the <c>trace://session/{handle}</c> resource so the
/// payload can be serialized without reflection under <c>PublishTrimmed</c>.
/// </summary>
internal sealed record CpuSampleSessionPayload(
    string Kind,
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalSamples,
    CallTreeNode Root);

internal sealed record UnknownSessionPayload(string Kind, string Error);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CpuSampleSessionPayload))]
[JsonSerializable(typeof(UnknownSessionPayload))]
[JsonSerializable(typeof(CallTreeNode))]
[JsonSerializable(typeof(SampledFrame))]
[JsonSerializable(typeof(MethodIdentity))]
[JsonSerializable(typeof(GenericInstantiation))]
[JsonSerializable(typeof(SourceLocation))]
internal sealed partial class TraceSessionJsonContext : JsonSerializerContext;
