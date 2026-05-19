namespace DotnetDiagnosticsMcp.Core.Dump;

/// <summary>
/// Inspects a .NET process heap and turns it into actionable JSON the LLM can drive
/// an investigation from. Two modes:
/// <list type="bullet">
///   <item><see cref="InspectAsync"/> — offline, reads a previously-captured dump file (cheap on the target).</item>
///   <item><see cref="InspectLiveAsync"/> — attaches to a live PID via ClrMD without writing a dump (no I/O, but suspends the target during the walk).</item>
/// </list>
/// Both paths share the same walker so the resulting top-N type lists and retention paths are directly comparable.
/// </summary>
public interface IDumpInspector
{
    /// <summary>
    /// Walks the heap in <paramref name="dumpFilePath"/> and returns aggregated statistics
    /// (top types by retained bytes / instance count) plus runtime/heap metadata. Optionally
    /// resolves a short retention path for the top-K types when
    /// <see cref="DumpInspectionOptions.IncludeRetentionPaths"/> is set.
    /// </summary>
    Task<DumpInspection> InspectAsync(
        string dumpFilePath,
        DumpInspectionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches to a live .NET process via ClrMD and walks its managed heap without writing
    /// a dump file. The same UID constraint as the diagnostic socket applies. The target is
    /// suspended for the duration of the heap walk (typically sub-second for ≤ ~200 MB, can
    /// reach a few seconds for multi-GB heaps).
    /// </summary>
    Task<LiveHeapInspection> InspectLiveAsync(
        int processId,
        DumpInspectionOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Caller-tunable knobs for <see cref="IDumpInspector.InspectAsync"/>.</summary>
/// <param name="TopTypes">Number of types to return in each top-N list (bytes / instances). Defaults to 20.</param>
/// <param name="IncludeRetentionPaths">When true, walk a short GC root chain for each of the top-K types by bytes. Off by default (more expensive than the basic walk).</param>
/// <param name="RetentionPathLimit">When retention paths are enabled, cap the depth of each chain (defaults to 8 frames).</param>
public sealed record DumpInspectionOptions(
    int TopTypes = 20,
    bool IncludeRetentionPaths = false,
    int RetentionPathLimit = 8);

/// <summary>Output of <see cref="IDumpInspector.InspectAsync"/>.</summary>
public sealed record DumpInspection(
    string FilePath,
    long FileSizeBytes,
    DumpRuntimeInfo Runtime,
    DumpHeapSummary Heap,
    IReadOnlyList<TypeStat> TopTypesByBytes,
    IReadOnlyList<TypeStat> TopTypesByInstances,
    IReadOnlyList<RetentionPath>? RetentionPaths = null,
    IReadOnlyList<string>? Warnings = null);

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
    IReadOnlyList<string>? Warnings = null);

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
