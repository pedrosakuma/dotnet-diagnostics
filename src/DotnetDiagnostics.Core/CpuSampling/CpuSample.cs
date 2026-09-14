using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>The mechanism that produced CPU stack observations.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CpuSampleBackend>))]
public enum CpuSampleBackend
{
    LegacyUnknown,
    EventPipeSampleProfiler,
    LinuxPerf,
    WindowsEtw,
}

/// <summary>The strongest scheduler-state conclusion supported by a CPU sample.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CpuSampleEvidenceKind>))]
public enum CpuSampleEvidenceKind
{
    LegacyUnknown,
    StackFrequencyWithHeuristicWaits,
    OsOnCpuSamples,
}

/// <summary>
/// Capture-wide provenance and scheduler-state semantics for CPU stack observations.
/// A null value on a deserialized artifact means legacy-unknown and must not be interpreted
/// as measured on-CPU evidence.
/// </summary>
public sealed record CpuSampleEvidence(
    string Schema,
    CpuSampleBackend Backend,
    CpuSampleEvidenceKind Kind,
    string Observation,
    IReadOnlyList<string> Limitations)
{
    public const string SchemaV1 = "dotnet-diagnostics/cpu-sample-evidence/v1";

    public static CpuSampleEvidence EventPipeSampleProfiler { get; } = new(
        SchemaV1,
        CpuSampleBackend.EventPipeSampleProfiler,
        CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits,
        "Periodic EventPipe managed stack observations; sample frequency is not measured CPU time or OS scheduler state.",
        [
            "Known wait-frame matches are name-based heuristic indications, not scheduler-confirmed off-CPU observations.",
            "Unmatched, unresolved, and native leaf frames have unknown scheduler state.",
        ]);

    public static CpuSampleEvidence LinuxPerfOnCpu { get; } = new(
        SchemaV1,
        CpuSampleBackend.LinuxPerf,
        CpuSampleEvidenceKind.OsOnCpuSamples,
        "Linux perf on-CPU sampling observations.",
        ["Sample counts estimate where on-CPU observations occurred; they are not elapsed CPU time."]);

    public static CpuSampleEvidence WindowsEtwOnCpu { get; } = new(
        SchemaV1,
        CpuSampleBackend.WindowsEtw,
        CpuSampleEvidenceKind.OsOnCpuSamples,
        "Windows kernel ETW profile-interrupt on-CPU observations.",
        ["Sample counts estimate where on-CPU observations occurred; they are not elapsed CPU time."]);
}

/// <summary>A single resolved frame within a CPU sample stack.</summary>
public sealed record SampledFrame(string Module, string Method);

/// <summary>
/// Split of a method's <em>self/exclusive</em> samples by scheduler-state evidence.
/// <see cref="RunningSamples"/> is reserved for OS-backed on-CPU observations,
/// <see cref="WaitingSamples"/> contains name-based wait heuristics, and
/// <see cref="UnknownSamples"/> preserves observations whose scheduler state is not established.
/// Use the enclosing capture's <see cref="CpuSampleEvidence"/> before interpreting legacy data.
/// </summary>
public sealed record SelfSampleBreakdown(
    long RunningSamples,
    long WaitingSamples,
    long UnknownSamples = 0);

/// <summary>A hotspot is a frame ranked by how often it appeared in CPU samples.</summary>
public sealed record Hotspot(
    SampledFrame Frame,
    long InclusiveSamples,
    long ExclusiveSamples,
    DotnetDiagnostics.Core.Memory.MethodIdentity? Identity = null)
{
    /// <summary>
    /// Optional evidence split of this hotspot's <see cref="ExclusiveSamples"/> into on-CPU,
    /// heuristic-wait, and unknown observations. Populated for CPU-sample backends; omitted for
    /// non-CPU call-tree consumers such as allocation/native-alloc drilldowns.
    /// </summary>
    public SelfSampleBreakdown? SelfSamples { get; init; }
}

/// <summary>Per-phase elapsed timings for a CPU sampling pass.</summary>
public sealed record CpuSampleTimings(
    TimeSpan CaptureDuration,
    TimeSpan SymbolicationDuration,
    TimeSpan SourceLineResolutionDuration,
    TimeSpan AggregationDuration,
    TimeSpan TotalDuration)
{
    public static CpuSampleTimings Empty { get; } = new(
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero);

    /// <summary>
    /// Time spent starting the capture session before the requested sampling window begins. For
    /// CoreCLR EventPipe this isolates the EventPipe arm/start overhead from the capture window.
    /// </summary>
    public TimeSpan SessionStartDuration { get; init; }

    /// <summary>
    /// Time spent after the capture window closing and draining the session/trace stream.
    /// </summary>
    public TimeSpan SessionDrainDuration { get; init; }

    /// <summary>
    /// Optional post-capture ClrMD enrichment time used to recover closed generic method
    /// instantiations for the hottest managed frames.
    /// </summary>
    public TimeSpan MethodInstantiationResolutionDuration { get; init; }
}

/// <summary>Aggregated CPU sample over a window.</summary>
public sealed record CpuSample(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalSamples,
    IReadOnlyList<Hotspot> TopHotspots)
{
    /// <summary>
    /// Capture-wide backend and scheduler-state evidence semantics. Null means a legacy artifact
    /// whose running/waiting fields cannot be assigned stronger semantics safely.
    /// </summary>
    public CpuSampleEvidence? Evidence { get; init; }

    /// <summary>
    /// Overall split of sampled leaf/self observations. Interpret it together with
    /// <see cref="Evidence"/>; counts always preserve unknown observations.
    /// </summary>
    public SelfSampleBreakdown? SelfSamples { get; init; }

    /// <summary>Bounded capture and symbol-resolution degradation notes.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Aggregate symbol-resolution quality of <see cref="TopHotspots"/>. Always populated for
    /// OS-backed samples by the perf/ETW samplers; <c>null</c> for CoreCLR EventPipe samples
    /// since that path resolves managed methods via TraceEvent and the concept does not apply
    /// uniformly. See #29 / #35 — surfacing this avoids forcing the consumer to
    /// drill into the trace artifact just to know whether demangling succeeded.
    /// </summary>
    public NativeAotSymbolDemangler.SymbolSource? SymbolSource { get; init; }

    /// <summary>
    /// The single hottest method by <b>self-time</b> (exclusive samples) across the whole merged call
    /// tree — computed before <see cref="TopHotspots"/> is capped by inclusive rank. It identifies
    /// the most frequent exclusive leaf observation, not necessarily consumed CPU. Interpret it
    /// with <see cref="Evidence"/> and <see cref="Hotspot.SelfSamples"/>. <c>null</c> when no
    /// attributable exclusive leaf exists or the sampler does not compute it.
    /// </summary>
    public Hotspot? TopSelfTime { get; init; }

    /// <summary>
    /// Internal-only measured on-CPU self leader used by CPU signal generation. It is populated
    /// only when <see cref="Evidence"/> establishes OS-backed on-CPU sampling.
    /// </summary>
    [JsonIgnore]
    public Hotspot? TopRunningSelfTime { get; init; }

    /// <summary>
    /// Per-phase elapsed timings for capture and post-processing. Populated by the shipping CPU
    /// sampler backends; synthetic/test instances default to all-zero timings.
    /// </summary>
    public CpuSampleTimings Timings { get; init; } = CpuSampleTimings.Empty;
}
