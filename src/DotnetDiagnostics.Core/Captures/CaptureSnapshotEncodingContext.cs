using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuEfficiency;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.NativeLockContention;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.ThreadPool;

namespace DotnetDiagnostics.Core.Captures;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CounterSnapshot))]
[JsonSerializable(typeof(ExceptionSnapshot))]
[JsonSerializable(typeof(CrashGuardSnapshot))]
[JsonSerializable(typeof(GcSummary))]
[JsonSerializable(typeof(GcDatasSnapshot))]
[JsonSerializable(typeof(EventSourceCapture))]
[JsonSerializable(typeof(EventCatalogSnapshot))]
[JsonSerializable(typeof(ActivityCapture))]
[JsonSerializable(typeof(LogSnapshot))]
[JsonSerializable(typeof(JitSnapshot))]
[JsonSerializable(typeof(ThreadPoolEventSnapshot))]
[JsonSerializable(typeof(ContentionSnapshot))]
[JsonSerializable(typeof(DbSnapshot))]
[JsonSerializable(typeof(KestrelSnapshot))]
[JsonSerializable(typeof(NetworkingSnapshot))]
[JsonSerializable(typeof(InFlightRequestSnapshot))]
[JsonSerializable(typeof(StartupSnapshot))]
[JsonSerializable(typeof(CpuSampleTraceArtifact))]
[JsonSerializable(typeof(AllocationSampleArtifact))]
[JsonSerializable(typeof(OffCpuSnapshotArtifact))]
[JsonSerializable(typeof(NativeLockContentionArtifact))]
[JsonSerializable(typeof(CpuEfficiencySample))]
[JsonSerializable(typeof(MethodParameterCaptureArtifact))]
[JsonSerializable(typeof(ThreadSnapshotEncoding))]
[JsonSerializable(typeof(HeapSnapshotArtifact))]
[JsonSerializable(typeof(RequestsNowSnapshot))]
[JsonSerializable(typeof(List<CallTreeSnapshotRow>))]
[JsonSerializable(typeof(List<SymbolSnapshotRow<SourceLocation>>))]
[JsonSerializable(typeof(List<SymbolSnapshotRow<MethodIdentity>>))]
internal sealed partial class CaptureSnapshotEncodingContext : JsonSerializerContext;
