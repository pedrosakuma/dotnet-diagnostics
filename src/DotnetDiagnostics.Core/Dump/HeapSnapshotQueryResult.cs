using DotnetDiagnostics.Core.Evidence;

namespace DotnetDiagnostics.Core.Dump;

/// <summary>
/// Typed payload returned by the heap views of <c>query_snapshot</c>. Carries the slice requested
/// by the LLM (top-N types OR retention paths matching a filter) along with provenance fields
/// (origin, pid, captured-at) so the model can reason about freshness without a second roundtrip.
/// </summary>
public sealed record HeapSnapshotQueryResult(
    string Handle,
    string View,
    string Origin,
    int ProcessId,
    DateTimeOffset CapturedAt)
{
    /// <summary>Structured capture limitations plus any projection applied by this query.</summary>
    public EvidenceQuality? Quality { get; init; }
    /// <summary>Echoes the exact object address targeted by address-based heap queries.</summary>
    public ulong? Address { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"top-types"</c>.</summary>
    public IReadOnlyList<TypeStat>? TopTypes { get; init; }
    /// <summary>Echoes the ranking used for <c>top-types</c> queries: <c>"bytes"</c> or <c>"instances"</c>.</summary>
    public string? RankBy { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"retention-paths"</c>.</summary>
    public IReadOnlyList<RetentionPath>? RetentionPaths { get; init; }
    /// <summary>Echoes the substring filter applied to retention-path queries, if any.</summary>
    public string? FilterTypeFullName { get; init; }
    /// <summary>Total retention paths matching the filter in the full artifact.</summary>
    public int? TotalRetentionPaths { get; init; }
    /// <summary>Matching retention paths omitted from this bounded inline projection.</summary>
    public int? OmittedRetentionPaths { get; init; }
    /// <summary>Maximum root/retainer frames returned per path. The handle keeps the complete chain.</summary>
    public int? RetentionFrameLimit { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"roots-by-kind"</c>.</summary>
    public IReadOnlyList<RootKindStat>? RootsByKind { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"finalizer-queue"</c>.</summary>
    public IReadOnlyList<FinalizableTypeStat>? FinalizableObjects { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"fragmentation"</c>.</summary>
    public IReadOnlyList<SegmentStat>? Segments { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"static-fields"</c>.</summary>
    public IReadOnlyList<StaticFieldStat>? StaticFields { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"delegate-targets"</c>.</summary>
    public IReadOnlyList<DelegateTargetStat>? DelegateTargets { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"duplicate-strings"</c>.</summary>
    public IReadOnlyList<DuplicateStringStat>? DuplicateStrings { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"retained-exceptions"</c>.</summary>
    public IReadOnlyList<RetainedExceptionTypeStat>? RetainedExceptions { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"gchandles"</c>.</summary>
    public GcHandlesView? GcHandles { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"object"</c>.</summary>
    public HeapObjectInspection? ObjectDetails { get; init; }
    /// <summary>Exact type identity requested by the <c>"type-instances"</c> view.</summary>
    public TypeIdentity? RequestedTypeIdentity { get; init; }
    /// <summary>Bounded object rows returned by the <c>"type-instances"</c> view.</summary>
    public IReadOnlyList<HeapTypeInstance>? TypeInstances { get; init; }
    /// <summary>Requested maximum number of instances in this page.</summary>
    public int? InstancePageSize { get; init; }
    /// <summary>Number of heap objects visited while producing this page.</summary>
    public int? ScannedObjects { get; init; }
    /// <summary>True only when this query reached the end of the heap.</summary>
    public bool? InstanceEnumerationComplete { get; init; }
    /// <summary>Opaque handle/type-bound continuation cursor for a later page.</summary>
    public string? NextCursor { get; init; }
    /// <summary>When the snapshot origin was re-opened to enumerate this page.</summary>
    public DateTimeOffset? EnumeratedAt { get; init; }
    /// <summary>Whether paging is stable offline or best-effort across independent live re-reads.</summary>
    public string? InstanceConsistency { get; init; }
    /// <summary>Enumeration-specific limits and live-stability warnings.</summary>
    public IReadOnlyList<string>? InstanceWarnings { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"gcroot"</c>.</summary>
    public HeapGcRootInspection? GcRoot { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"objsize"</c>.</summary>
    public HeapObjectSizeInspection? ObjectSize { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"async"</c>.</summary>
    public IReadOnlyList<AsyncOperationStat>? AsyncOperations { get; init; }
    /// <summary>Echoes the ranking used for <c>async</c> queries: <c>"heap-order"</c> or <c>"direct-size"</c>.</summary>
    public string? SortedBy { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"timers"</c>.</summary>
    public TaskTimerLeakView? Timers { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"alc"</c>.</summary>
    public AssemblyLoadContextLeakView? AssemblyLoadContexts { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"com-wrappers"</c>.</summary>
    public ComWrappersView? ComWrappers { get; init; }
    /// <summary>Populated when <see cref="View"/> is <c>"heap-integrity"</c>.</summary>
    public HeapIntegrityView? HeapIntegrity { get; init; }
}
