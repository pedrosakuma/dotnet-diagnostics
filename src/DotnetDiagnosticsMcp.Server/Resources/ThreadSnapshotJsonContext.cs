using System.Text.Json.Serialization;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.Threads;

namespace DotnetDiagnosticsMcp.Server.Resources;

internal sealed record ThreadSnapshotErrorPayload(string Kind, string Error);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ThreadSnapshotArtifact))]
[JsonSerializable(typeof(ThreadSnapshotErrorPayload))]
[JsonSerializable(typeof(ManagedThread))]
[JsonSerializable(typeof(ManagedStackFrame))]
[JsonSerializable(typeof(MonitorLockState))]
[JsonSerializable(typeof(MethodIdentity))]
[JsonSerializable(typeof(GenericInstantiation))]
internal sealed partial class ThreadSnapshotJsonContext : JsonSerializerContext;
