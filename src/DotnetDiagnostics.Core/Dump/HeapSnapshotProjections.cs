namespace DotnetDiagnostics.Core.Dump;

/// <summary>
/// Projections from the canonical <see cref="HeapSnapshotArtifact"/> aggregate into the
/// bounded shapes the MCP tools return inline. The snapshot itself stays in the handle
/// store for drilldown queries (<c>query_heap_snapshot</c> + <c>heap://snapshot/{handle}</c>).
/// </summary>
public static class HeapSnapshotProjections
{
    /// <summary>
    /// Projects the artifact into the dump-style summary (<see cref="DumpInspection"/>) returned
    /// by <c>inspect_dump</c>. Caps the top-N lists at <paramref name="inlineTopTypes"/> so the
    /// inline response stays bounded regardless of the richer snapshot caps.
    /// </summary>
    public static DumpInspection ToDumpInspection(this HeapSnapshotArtifact snapshot, int inlineTopTypes, string? handle = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inlineTopTypes);

        return new DumpInspection(
            FilePath: snapshot.DumpFilePath ?? string.Empty,
            FileSizeBytes: snapshot.DumpFileSizeBytes ?? 0,
            Runtime: snapshot.Runtime,
            Heap: snapshot.Heap,
            TopTypesByBytes: snapshot.TopTypesByBytes.Take(inlineTopTypes).ToArray(),
            TopTypesByInstances: snapshot.TopTypesByInstances.Take(inlineTopTypes).ToArray(),
            RetentionPaths: snapshot.RetentionPaths,
            Warnings: snapshot.Warnings)
        {
            Handle = handle,
        };
    }

    /// <summary>
    /// Projects the artifact into the live-style summary (<see cref="LiveHeapInspection"/>)
    /// returned by <c>inspect_live_heap</c>.
    /// </summary>
    public static LiveHeapInspection ToLiveHeapInspection(this HeapSnapshotArtifact snapshot, int inlineTopTypes, string? handle = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inlineTopTypes);

        return new LiveHeapInspection(
            ProcessId: snapshot.ProcessId,
            SuspendDuration: snapshot.WalkDuration,
            Runtime: snapshot.Runtime,
            Heap: snapshot.Heap,
            TopTypesByBytes: snapshot.TopTypesByBytes.Take(inlineTopTypes).ToArray(),
            TopTypesByInstances: snapshot.TopTypesByInstances.Take(inlineTopTypes).ToArray(),
            RetentionPaths: snapshot.RetentionPaths,
            Warnings: snapshot.Warnings)
        {
            Handle = handle,
        };
    }
}
