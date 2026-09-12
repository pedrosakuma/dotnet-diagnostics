namespace DotnetDiagnostics.Core.Dump;

using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Evidence;

/// <summary>
/// Collects a managed-heap snapshot over EventPipe — the same mechanism <c>dotnet-gcdump</c> uses —
/// without writing a process dump or attaching ClrMD/ptrace to the target. This is the
/// ptrace-free alternative to <see cref="IDumpInspector.InspectAsync"/>
/// (<c>source=dump</c>) and <see cref="IDumpInspector.InspectLiveAsync"/> (<c>source=live</c>):
/// no <c>CAP_SYS_PTRACE</c> and no dump file on disk. The runtime does induce a blocking Gen2 GC,
/// so the target can pause while the heap snapshot is produced.
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
    bool ExportTrace = false)
{
    /// <summary>
    /// Runtime flavor of the target. gcdump is a CoreCLR-only capability: on <see cref="RuntimeFlavor.NativeAot"/>
    /// the collector refuses with <see cref="NotSupportedException"/> before opening a session, because requesting
    /// the GCHeapSnapshot EventPipe keyword crashes .NET 10 NativeAOT targets (issue #471). Kept as an init-only
    /// property (rather than a positional parameter) so the existing constructor ABI is preserved. Defaults to
    /// <see cref="RuntimeFlavor.CoreClr"/> so existing callers are unaffected.
    /// </summary>
    public RuntimeFlavor Runtime { get; init; } = RuntimeFlavor.CoreClr;
}

/// <summary>Builds bounded evidence-quality metadata for EventPipe gcdump captures and projections.</summary>
public static class GcDumpEvidence
{
    /// <summary>
    /// Returns gcdump quality metadata, mapping a legacy gcdump to unknown. ClrMD live/dump
    /// captures are outside this policy boundary and retain their existing null metadata.
    /// </summary>
    public static EvidenceQuality? GetApplicableQuality(HeapSnapshotArtifact snapshot)
        => snapshot.Origin == HeapSnapshotOrigin.GcDump
            ? snapshot.Quality ?? EvidenceQuality.LegacyUnknown
            : snapshot.Quality;

    public static bool SupportsAbsence(HeapSnapshotArtifact snapshot)
        => snapshot.Origin != HeapSnapshotOrigin.GcDump
            || snapshot.Quality?.Conclusions.AbsenceOrExhaustiveCounts
                == EvidenceConclusionSupport.Supported;

    public static bool SupportsRegression(HeapSnapshotArtifact snapshot)
        => snapshot.Origin != HeapSnapshotOrigin.GcDump
            || snapshot.Quality?.Conclusions.RegressionOrHealthyControl
                == EvidenceConclusionSupport.Supported;

    internal static EvidenceQuality BuildQuality(
        long nodeCount,
        int typeCount,
        int missingTypeNameCount,
        int retainedTypeCount,
        GcDumpCaptureStatus status)
    {
        var limitations = new List<EvidenceLimitation>(8)
        {
            new(
                EvidenceLimitationCategory.MechanismUnobservable,
                "eventpipe-loss",
                null,
                "The gcdump EventPipe reader does not expose a reliable lost-event count; transport loss cannot be excluded."),
            new(
                EvidenceLimitationCategory.MechanismUnavailable,
                "object-graph",
                null,
                "This artifact contains observed per-type node and byte totals, not object edges or roots; graph completeness and retention paths are unavailable."),
            new(
                EvidenceLimitationCategory.MechanismUnavailable,
                "heap-properties",
                null,
                "GC handles, static fields, delegate targets, finalizable objects, segment layout, and ClrMD-only object views are unavailable from gcdump."),
        };

        if (status.TimedOut)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.CaptureWindow,
                "timeout",
                null,
                "The collection timeout elapsed before all completion signals were observed; retained type totals may be partial."));
        }
        if (!status.GcStopObserved)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.CaptureWindow,
                "gc-stop",
                null,
                "The induced blocking GC stop was not observed; this does not identify whether startup, workload timing, timeout, or stream failure was responsible."));
        }
        if (!status.EventStreamCompleted)
        {
            limitations.Add(new(
                status.ReaderFailed ? EvidenceLimitationCategory.ProcessingFailure : EvidenceLimitationCategory.CaptureWindow,
                "event-stream",
                status.ReaderFailed ? 1 : null,
                status.ReaderFailed
                    ? "The EventPipe reader failed after partial per-type evidence may have been retained."
                    : "The EventPipe stream did not complete cleanly before collection ended; retained per-type evidence may be partial."));
        }
        if (nodeCount == 0)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.CaptureWindow,
                "heap-nodes",
                null,
                "No heap nodes were observed in this capture window; this does not establish an empty heap or a startup cause."));
        }
        if (missingTypeNameCount > 0)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.MechanismUnavailable,
                "type-names",
                missingTypeNameCount,
                "Some observed type ids had no name in the type stream and are represented by hexadecimal ids."));
        }

        var omittedTypes = Math.Max(0, typeCount - retainedTypeCount);
        if (omittedTypes > 0)
        {
            limitations.Add(new(
                EvidenceLimitationCategory.OutputProjection,
                "snapshot-top-types",
                omittedTypes,
                "Per-type aggregation covered the observed stream, but lower-ranked types were omitted from the bounded snapshot rankings."));
        }

        if (status.TraceExportRequested && !status.TraceExportCompleted)
        {
            limitations.Add(new(
                status.ReaderFailed ? EvidenceLimitationCategory.ProcessingFailure : EvidenceLimitationCategory.CaptureWindow,
                "trace-export",
                null,
                "Raw trace export was requested but did not complete, so no trace reference was published."));
        }

        var positive = nodeCount > 0
            ? EvidenceConclusionSupport.Supported
            : EvidenceConclusionSupport.NotEstablished;
        return new EvidenceQuality(
            EvidenceQuality.SchemaV1,
            limitations,
            new EvidenceConclusionPolicy(
                positive,
                EvidenceConclusionSupport.Inconclusive,
                EvidenceConclusionSupport.Inconclusive));
    }

    public static EvidenceQuality WithProjection(
        EvidenceQuality? quality,
        string scope,
        long omittedCount,
        string detail)
    {
        var source = quality ?? EvidenceQuality.LegacyUnknown;
        if (omittedCount <= 0)
        {
            return source;
        }

        var limitations = source.Limitations
            .Where(l => l.Category != EvidenceLimitationCategory.OutputProjection
                || !string.Equals(l.Scope, scope, StringComparison.Ordinal))
            .Append(new EvidenceLimitation(
                EvidenceLimitationCategory.OutputProjection,
                scope,
                omittedCount,
                detail))
            .ToArray();
        return source with { Limitations = limitations };
    }

    public static EvidenceQuality? WithApplicableProjection(
        HeapSnapshotArtifact snapshot,
        string scope,
        long omittedCount,
        string detail)
    {
        var quality = GetApplicableQuality(snapshot);
        return quality is null ? null : WithProjection(quality, scope, omittedCount, detail);
    }
}
