namespace DotnetDiagnostics.Core.Dump;

/// <summary>
/// Collects a managed-heap snapshot over EventPipe — the same mechanism <c>dotnet-gcdump</c> uses —
/// without writing a process dump or attaching ClrMD/ptrace to the target. This is the
/// production-safe alternative to <see cref="IDumpInspector.InspectAsync"/>
/// (<c>source=dump</c>) and <see cref="IDumpInspector.InspectLiveAsync"/> (<c>source=live</c>):
/// no <c>CAP_SYS_PTRACE</c>, no suspension, no dump file on disk.
/// <para>
/// The trade-off is fidelity. A GC heap snapshot yields per-type instance counts and byte totals
/// (so <see cref="HeapSnapshotArtifact.TopTypesByBytes"/> / <see cref="HeapSnapshotArtifact.TopTypesByInstances"/>
/// are populated) but cannot answer the ClrMD-only questions — GC handles, static fields,
/// delegate targets, segment/generation layout and finalizable types stay <c>null</c>.
/// </para>
/// </summary>
public interface IGcDumpHeapSnapshotCollector
{
    /// <summary>
    /// Triggers an induced GC heap dump on the live process and returns the resulting
    /// <see cref="HeapSnapshotArtifact"/> with <see cref="HeapSnapshotOrigin.GcDump"/>.
    /// </summary>
    Task<HeapSnapshotArtifact> CollectAsync(
        int processId,
        GcDumpOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Caller-tunable knobs for <see cref="IGcDumpHeapSnapshotCollector.CollectAsync"/>.</summary>
/// <param name="TopTypes">Number of types projected into the inline summary lists. Defaults to 20.</param>
/// <param name="SnapshotTopTypes">Number of types retained in the snapshot for drilldown. Should be ≥ <paramref name="TopTypes"/>. Defaults to 200.</param>
/// <param name="Timeout">Upper bound on the whole dump. The induced GC plus heap walk usually finishes in a few seconds; defaults to 30s.</param>
/// <param name="ExportTrace">When true, the raw GC heap-dump <c>.nettrace</c> is persisted under the artifact root and its relative path surfaced on <see cref="HeapSnapshotArtifact.TracePath"/> (issue #445). Defaults to false.</param>
public sealed record GcDumpOptions(
    int TopTypes = 20,
    int SnapshotTopTypes = 200,
    TimeSpan? Timeout = null,
    bool ExportTrace = false);
