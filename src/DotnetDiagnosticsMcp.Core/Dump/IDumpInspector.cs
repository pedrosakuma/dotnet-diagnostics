namespace DotnetDiagnosticsMcp.Core.Dump;

/// <summary>
/// Inspects a .NET process heap and turns it into actionable JSON the LLM can drive
/// an investigation from. Two modes:
/// <list type="bullet">
///   <item><see cref="InspectAsync"/> — offline, reads a previously-captured dump file (cheap on the target).</item>
///   <item><see cref="InspectLiveAsync"/> — attaches to a live PID via ClrMD without writing a dump (no I/O, but suspends the target during the walk).</item>
/// </list>
/// Both paths share the same walker so the resulting top-N type lists and retention paths are directly comparable.
/// Both also produce a <see cref="HeapSnapshotArtifact"/> internally; tools register that aggregate in the
/// drilldown handle store so the LLM can ask follow-up questions (richer top-N, retention by type, …) without
/// paying the walk cost again.
/// </summary>
public interface IDumpInspector
{
    /// <summary>
    /// Walks the heap in <paramref name="dumpFilePath"/> and returns the full
    /// <see cref="HeapSnapshotArtifact"/>. Tools typically register the artifact in a handle store
    /// and project a bounded summary to the LLM.
    /// </summary>
    Task<HeapSnapshotArtifact> InspectAsync(
        string dumpFilePath,
        DumpInspectionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches to a live .NET process via ClrMD and walks its managed heap without writing
    /// a dump file. The same UID constraint as the diagnostic socket applies. The target is
    /// suspended for the duration of the heap walk (typically sub-second for ≤ ~200 MB, can
    /// reach a few seconds for multi-GB heaps).
    /// </summary>
    Task<HeapSnapshotArtifact> InspectLiveAsync(
        int processId,
        DumpInspectionOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Caller-tunable knobs for <see cref="IDumpInspector.InspectAsync"/>.</summary>
/// <param name="TopTypes">Number of types to project into the inline summary. The snapshot
/// retains a richer set (<see cref="SnapshotTopTypes"/>) so drilldown queries can expand later.</param>
/// <param name="SnapshotTopTypes">Number of types retained inside the snapshot for later drilldown.
/// Should be ≥ <paramref name="TopTypes"/>. Defaults to 200.</param>
/// <param name="IncludeRetentionPaths">When true, walk a short GC root chain for each of the top-K types by bytes. Off by default (more expensive than the basic walk).</param>
/// <param name="RetentionPathLimit">When retention paths are enabled, cap the depth of each chain (defaults to 8 frames).</param>
/// <param name="SnapshotRetentionPathTargets">Number of distinct types for which to compute retention paths
/// when <paramref name="IncludeRetentionPaths"/> is set. Defaults to 10.</param>
public sealed record DumpInspectionOptions(
    int TopTypes = 20,
    int SnapshotTopTypes = 200,
    bool IncludeRetentionPaths = false,
    int RetentionPathLimit = 8,
    int SnapshotRetentionPathTargets = 10);

/// <summary>Where a <see cref="HeapSnapshotArtifact"/> came from.</summary>
public enum HeapSnapshotOrigin
{
    /// <summary>Snapshot captured by reading a previously-written dump file.</summary>
    Dump,
    /// <summary>Snapshot captured by attaching to a live process via ClrMD.</summary>
    Live,
}

/// <summary>
/// Canonical heap snapshot produced once per walk and registered in the drilldown handle store.
/// Both <c>inspect_dump</c> and <c>inspect_live_heap</c> emit the same shape, so downstream
/// drilldown queries (<c>query_heap_snapshot</c>, <c>heap://snapshot/{handle}</c> Resource) do not
/// need to know how it was collected — that's the "split collector, unified drilldown" pattern.
/// </summary>
public sealed record HeapSnapshotArtifact(
    HeapSnapshotOrigin Origin,
    int ProcessId,
    DateTimeOffset CapturedAt,
    TimeSpan WalkDuration,
    DumpRuntimeInfo Runtime,
    DumpHeapSummary Heap,
    IReadOnlyList<TypeStat> TopTypesByBytes,
    IReadOnlyList<TypeStat> TopTypesByInstances)
{
    /// <summary>Path to the originating dump file when <see cref="Origin"/> is <see cref="HeapSnapshotOrigin.Dump"/>; <c>null</c> for live captures.</summary>
    public string? DumpFilePath { get; init; }
    /// <summary>On-disk size of the originating dump file; <c>null</c> for live captures.</summary>
    public long? DumpFileSizeBytes { get; init; }
    /// <summary>Retention paths walked for the top-N retained types (gated by <see cref="DumpInspectionOptions.IncludeRetentionPaths"/>).</summary>
    public IReadOnlyList<RetentionPath>? RetentionPaths { get; init; }
    /// <summary>Diagnostic warnings emitted during the walk (degraded data, ClrMD limitations, …).</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>Output of <see cref="IDumpInspector.InspectAsync"/> projected for inline tool consumption.</summary>
public sealed record DumpInspection(
    string FilePath,
    long FileSizeBytes,
    DumpRuntimeInfo Runtime,
    DumpHeapSummary Heap,
    IReadOnlyList<TypeStat> TopTypesByBytes,
    IReadOnlyList<TypeStat> TopTypesByInstances,
    IReadOnlyList<RetentionPath>? RetentionPaths = null,
    IReadOnlyList<string>? Warnings = null)
{
    /// <summary>Drilldown handle for follow-up queries; <c>null</c> when the inspector was invoked outside the MCP tool layer.</summary>
    public string? Handle { get; init; }
}

/// <summary>
/// Output of <see cref="IDumpInspector.InspectLiveAsync"/>. Mirrors <see cref="DumpInspection"/>
/// but reports process identity and the wall-clock time during which the target was suspended,
/// not a file path/size.
/// </summary>
public sealed record LiveHeapInspection(
    int ProcessId,
    TimeSpan SuspendDuration,
    DumpRuntimeInfo Runtime,
    DumpHeapSummary Heap,
    IReadOnlyList<TypeStat> TopTypesByBytes,
    IReadOnlyList<TypeStat> TopTypesByInstances,
    IReadOnlyList<RetentionPath>? RetentionPaths = null,
    IReadOnlyList<string>? Warnings = null)
{
    /// <summary>Drilldown handle for follow-up queries; <c>null</c> when the inspector was invoked outside the MCP tool layer.</summary>
    public string? Handle { get; init; }
}

public sealed record DumpRuntimeInfo(
    string Name,
    string Version,
    string Architecture,
    bool IsServerGC,
    int HeapCount);

public sealed record DumpHeapSummary(
    long TotalBytes,
    long Gen0Bytes,
    long Gen1Bytes,
    long Gen2Bytes,
    long LargeObjectHeapBytes,
    long PinnedObjectHeapBytes,
    long CommittedBytes);

/// <summary>
/// Aggregated heap statistic for a single managed type. <see cref="Identity"/> is the
/// cross-MCP handoff payload so the LLM can pivot from "what's retained" to
/// "what's the type definition" via <c>dotnet-assembly-mcp</c>.
/// </summary>
public sealed record TypeStat(
    string TypeFullName,
    string? ModuleName,
    long InstanceCount,
    long TotalBytes,
    double TotalBytesPercent,
    TypeIdentity? Identity = null);

/// <summary>
/// Canonical, machine-readable identity of a managed type observed in a dump
/// (issue #12 — pairs with <see cref="DotnetDiagnosticsMcp.Core.Memory.MethodIdentity"/>).
/// The <c>(ModuleVersionId, MetadataToken)</c> pair round-trips to a single
/// <c>TypeDefinition</c> (table 0x02) regardless of name mangling.
/// </summary>
public sealed record TypeIdentity(string TypeFullName)
{
    public string? ModuleName { get; init; }
    public string? ModulePath { get; init; }
    public Guid? ModuleVersionId { get; init; }
    public int? MetadataToken { get; init; }
}

/// <summary>
/// A short GC retention chain "root → … → instance" for one of the top retained types.
/// Useful for answering "why is this leak alive?" without manual <c>!gcroot</c> in WinDbg.
/// </summary>
public sealed record RetentionPath(
    string TargetTypeFullName,
    ulong TargetObjectAddress,
    IReadOnlyList<RetentionFrame> Chain,
    bool Truncated);

public sealed record RetentionFrame(
    string TypeFullName,
    ulong ObjectAddress)
{
    public string? RootKind { get; init; }
}
