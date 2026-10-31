using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.ProcessDiscovery;
using System.Runtime.InteropServices;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>Identifies which heap an allocation was placed on.</summary>
public enum HeapKind
{
    /// <summary>Small-object heap (SOH) — typical for allocations below ~85 KB.</summary>
    Small,

    /// <summary>Large-object heap (LOH) or pinned-object heap (POH) — allocations at or above ~85 KB.</summary>
    Large,
}

/// <summary>A single type observed during allocation sampling, with aggregated byte and event counts.</summary>
public sealed record AllocatedType(
    string TypeName,
    long TotalBytes,
    long EventCount,
    HeapKind DominantKind,
    TypeIdentity? Identity = null);

/// <summary>
/// A single allocation <em>call site</em> — the leaf (innermost) frame of the sampled
/// allocation stack — with the bytes and event count attributed to it. This is the byte-weighted
/// allocation analogue of CPU <see cref="Hotspot"/> exclusive (self) cost: it answers
/// <em>"which code path allocated the most"</em>, complementing <see cref="AllocatedType"/>'s
/// <em>"what type was allocated"</em>. Attribution is leaf-exact; the full root→leaf path remains
/// available in the call-tree artifact behind the sample handle.
/// </summary>
public sealed record AllocationSite(
    SampledFrame Frame,
    long TotalBytes,
    long EventCount,
    HeapKind DominantKind,
    DotnetDiagnostics.Core.Memory.MethodIdentity? Identity = null);

/// <summary>
/// Summary of an allocation sampling pass: top-N types by allocated bytes and by event count,
/// plus totals for the entire window.
/// </summary>
/// <remarks>
/// Backed by <c>GCAllocationTick</c> events from <c>Microsoft-Windows-DotNETRuntime</c>
/// (GCKeyword=0x1, Verbose), which fire roughly every 100 KB of total managed allocations
/// and carry the TypeName of the most recently allocated object.
/// <b>CoreCLR</b>: TypeName is fully populated.
/// <b>NativeAOT</b>: TypeName is empty — the event fires but the runtime does not populate
/// the TypeName field; all events roll up under <c>&lt;unknown&gt;</c>.
/// </remarks>
public sealed record AllocationSample(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalEvents,
    long TotalBytes,
    IReadOnlyList<AllocatedType> TopByBytes,
    IReadOnlyList<AllocatedType> TopByCount)
{
    /// <summary>
    /// Top allocation call sites by attributed bytes — the <em>origin</em> of the allocations,
    /// not just their type. Each entry is the leaf (innermost) frame of a sampled allocation stack
    /// with its bytes/events; the byte-weighted analogue of CPU exclusive cost. Empty when no
    /// allocation stacks were captured (e.g. NativeAOT native-only frames may still resolve to
    /// addresses). For the full root→leaf path, drill into the call-tree artifact via the handle.
    /// </summary>
    public IReadOnlyList<AllocationSite> TopBySite { get; init; } = Array.Empty<AllocationSite>();
}

/// <summary>
/// Combined result of an allocation sampling pass: the compact <see cref="AllocationSample"/>
/// summary returned to the LLM, and a heavier <see cref="CpuSampleTraceArtifact"/> retained
/// under a handle for drill-down queries via <c>query_snapshot(view="call-tree")</c>.
/// </summary>
public sealed record AllocationSampleResult(AllocationSample Summary, CpuSampleTraceArtifact Artifact)
{
    public CpuSampleTraceArtifact? EstimatedBytesArtifact { get; init; }
}

public sealed record AllocationSampleArtifact(AllocationSample Summary, CpuSampleTraceArtifact TraceArtifact)
{
    /// <summary>Byte-weighted allocation-tick tree; absent in historical artifacts that only retained tick counts.</summary>
    public CpuSampleTraceArtifact? EstimatedBytesTrace { get; init; }

    /// <summary>
    /// Additive capture identity used by bounded multi-capture reproducibility analysis. Historical
    /// artifacts omit this block and remain readable; callers must not reconstruct it from the
    /// current process or host.
    /// </summary>
    public AllocationReproducibilityCaptureMetadata? Reproducibility { get; init; }
}

/// <summary>
/// Compatibility and provenance metadata captured at the same time as an allocation profile.
/// Process and capture identities are provenance only; reproducibility compatibility is evaluated
/// from the runtime, backend, quantity semantics and capture settings.
/// </summary>
public sealed record AllocationReproducibilityCaptureMetadata(
    string Schema,
    string RuntimeFlavor,
    string? RuntimeVersion,
    string OperatingSystem,
    string Architecture,
    string Backend,
    string Quantity,
    string Unit,
    string Aggregation,
    string Normalization,
    int RequestedDurationSeconds,
    int RequestedTopN,
    DateTimeOffset? ProcessStartedAt)
{
    public const string SchemaV1 = "dotnet-diagnostics/allocation-reproducibility-capture/v1";
    public const string EventPipeBackend = "eventpipe-gc-allocation-tick";

    public static AllocationReproducibilityCaptureMetadata Create(
        ProcessContext? context,
        int processId,
        int requestedDurationSeconds,
        int requestedTopN)
        => new(
            SchemaV1,
            context?.Runtime.ToString() ?? "Unknown",
            context?.RuntimeVersion,
            System.OperatingSystem.IsWindows() ? "windows"
                : System.OperatingSystem.IsLinux() ? "linux"
                : System.OperatingSystem.IsMacOS() ? "macos"
                : "unknown",
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            EventPipeBackend,
            "gc-allocation-tick-estimated-bytes",
            "estimated-bytes",
            "capture-total",
            "none",
            requestedDurationSeconds,
            requestedTopN,
            ProcessLifetime.TryReadStart(processId));
}
