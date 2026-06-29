using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Host-neutral managed-heap inspection use cases (issue #288 PR3b). Owns the full
/// <see cref="DiagnosticResult{T}"/> orchestration — symbol-path validation, attach guarding,
/// ClrMD heap walk, snapshot-handle registration, summary text and the cross-MCP drilldown hint —
/// for the offline <c>inspect_dump</c> and the live <c>inspect_live_heap</c> paths. Depends on Core
/// abstractions only and carries no MCP/transport knowledge, so both the MCP <c>inspect_heap</c>
/// tool and the standalone <c>dotnet-diagnostics inspect-heap</c> CLI share one behavior.
/// </summary>
/// <remarks>
/// The MCP Server keeps thin <c>DiagnosticTools.InspectDump</c> / <c>InspectLiveHeap</c> wrappers
/// (preserving their signatures + the tool-layer scope re-check) that forward here, so the existing
/// envelope byte-compat tests keep passing. The transport seams mirror PR3a: instead of the Server
/// <c>IPrincipalAccessor</c> / <c>LegacyDiagnosticsFlagDeprecation</c> these take a precomputed
/// <c>principalAllowsSymbolsRemote</c> bool and an <see cref="ISymbolServerDeprecationSink"/>.
/// </remarks>
public static class HeapInspectionUseCases
{
    /// <summary>Handle-store kind tag for a registered heap snapshot (dump or live).</summary>
    public const string HeapSnapshotKind = "heap-snapshot";

    /// <summary>Time-to-live for a registered heap-snapshot drilldown handle.</summary>
    public static readonly TimeSpan HeapSnapshotHandleTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Walks the managed heap of a previously-captured WithHeap/Full dump (offline, read-only).
    /// </summary>
    public static async Task<DiagnosticResult<DumpInspection>> InspectDump(
        IDumpInspector inspector,
        IDiagnosticHandleStore handles,
        SymbolServerAllowlist symbolServerAllowlist,
        bool principalAllowsSymbolsRemote,
        string dumpFilePath,
        int topTypes = 20,
        bool includeRetentionPaths = false,
        int retentionPathLimit = 8,
        bool includeStaticFields = false,
        bool includeDelegateTargets = false,
        bool includeDuplicateStrings = false,
        string? symbolPath = null,
        ISymbolServerDeprecationSink? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        // B4 / issue #165 / M3: same SSRF guard as collect_cpu_sample.
        var symbolDenial = SymbolPathValidation.Validate<DumpInspection>(symbolServerAllowlist, symbolPath, principalAllowsSymbolsRemote, deprecation);
        if (symbolDenial is not null) return symbolDenial;

        return await AttachGuard.GuardAttachAsync("inspect_dump", processId: null, async () =>
        {
            var snapshot = await inspector.InspectAsync(
                dumpFilePath,
                new DumpInspectionOptions(
                    TopTypes: topTypes,
                    IncludeRetentionPaths: includeRetentionPaths,
                    RetentionPathLimit: retentionPathLimit,
                    IncludeStaticFields: includeStaticFields,
                    IncludeDelegateTargets: includeDelegateTargets,
                    IncludeDuplicateStrings: includeDuplicateStrings,
                    SymbolPath: symbolPath),
                cancellationToken).ConfigureAwait(false);

            var handle = handles.Register(snapshot.ProcessId, HeapSnapshotKind, snapshot, HeapSnapshotHandleTtl, evictWhenProcessExits: false);
            var inspection = snapshot.ToDumpInspection(topTypes, handle.Id);

            var topByBytes = inspection.TopTypesByBytes;
            var summary = topByBytes.Count == 0
                ? $"Inspected {dumpFilePath} — runtime {inspection.Runtime.Name} {inspection.Runtime.Version}, heap walk produced no objects. Snapshot handle: `{handle.Id}`."
                : $"Inspected {dumpFilePath} — heap {inspection.Heap.TotalBytes:N0} bytes; top retained type: `{topByBytes[0].TypeFullName}` ({topByBytes[0].TotalBytesPercent}% / {topByBytes[0].InstanceCount:N0} instances). Snapshot handle: `{handle.Id}`.";

            var hint = BuildHeapDrilldownHint(handle.Id, topByBytes);
            return hint is null
                ? DiagnosticResult.Ok(inspection, summary)
                : DiagnosticResult.Ok(inspection, summary, hint);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches to a live .NET process via ClrMD and walks its managed heap without writing a dump.
    /// </summary>
    public static async Task<DiagnosticResult<LiveHeapInspection>> InspectLiveHeap(
        IDumpInspector inspector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        bool principalAllowsSymbolsRemote,
        int? processId = null,
        int topTypes = 20,
        bool includeRetentionPaths = false,
        int retentionPathLimit = 8,
        bool includeStaticFields = false,
        bool includeDelegateTargets = false,
        bool includeDuplicateStrings = false,
        string? symbolPath = null,
        ISymbolServerDeprecationSink? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        // B4 / issue #165 / M3: same SSRF guard as collect_cpu_sample.
        var symbolDenial = SymbolPathValidation.Validate<LiveHeapInspection>(symbolServerAllowlist, symbolPath, principalAllowsSymbolsRemote, deprecation);
        if (symbolDenial is not null) return symbolDenial;

        var resolved = await ResolveContextAsync<LiveHeapInspection>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        return await AttachGuard.GuardAttachAsync("inspect_live_heap", pid, async () =>
        {
            var snapshot = await inspector.InspectLiveAsync(
                pid,
                new DumpInspectionOptions(
                    TopTypes: topTypes,
                    IncludeRetentionPaths: includeRetentionPaths,
                    RetentionPathLimit: retentionPathLimit,
                    IncludeStaticFields: includeStaticFields,
                    IncludeDelegateTargets: includeDelegateTargets,
                    IncludeDuplicateStrings: includeDuplicateStrings,
                    SymbolPath: symbolPath),
                cancellationToken).ConfigureAwait(false);

            var handle = handles.Register(pid, HeapSnapshotKind, snapshot, HeapSnapshotHandleTtl);
            var inspection = snapshot.ToLiveHeapInspection(topTypes, handle.Id);

            var topByBytes = inspection.TopTypesByBytes;
            var summary = topByBytes.Count == 0
                ? $"Attached to pid {pid} for {inspection.SuspendDuration.TotalMilliseconds:N0} ms — runtime {inspection.Runtime.Name} {inspection.Runtime.Version}, heap walk produced no objects. Snapshot handle: `{handle.Id}`."
                : $"Attached to pid {pid} for {inspection.SuspendDuration.TotalMilliseconds:N0} ms — heap {inspection.Heap.TotalBytes:N0} bytes; top retained type: `{topByBytes[0].TypeFullName}` ({topByBytes[0].TotalBytesPercent}% / {topByBytes[0].InstanceCount:N0} instances). Snapshot handle: `{handle.Id}`.";

            var hint = BuildHeapDrilldownHint(handle.Id, topByBytes);
            var result = hint is null
                ? DiagnosticResult.Ok(inspection, summary)
                : DiagnosticResult.Ok(inspection, summary, hint);
            return WithContext(result, ctx);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures a managed-heap snapshot over EventPipe (the dotnet-gcdump mechanism) — no ptrace,
    /// no ClrMD attach, no dump file. Registers the same <c>heap-snapshot</c> handle so the
    /// <c>query_snapshot</c> drilldown views work unchanged; ClrMD-only views stay empty.
    /// </summary>
    public static async Task<DiagnosticResult<LiveHeapInspection>> InspectGcDump(
        IGcDumpHeapSnapshotCollector collector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        int? processId = null,
        int topTypes = 20,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveContextAsync<LiveHeapInspection>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        var snapshot = await collector.CollectAsync(
            pid,
            new GcDumpOptions(TopTypes: topTypes, Timeout: timeout),
            cancellationToken).ConfigureAwait(false);

        var handle = handles.Register(pid, HeapSnapshotKind, snapshot, HeapSnapshotHandleTtl);
        var inspection = snapshot.ToLiveHeapInspection(topTypes, handle.Id);

        var topByBytes = inspection.TopTypesByBytes;
        var summary = topByBytes.Count == 0
            ? $"gcdump of pid {pid} ({inspection.SuspendDuration.TotalMilliseconds:N0} ms) produced no objects. Snapshot handle: `{handle.Id}`."
            : $"gcdump of pid {pid} ({inspection.SuspendDuration.TotalMilliseconds:N0} ms) — heap {inspection.Heap.TotalBytes:N0} bytes; top type: `{topByBytes[0].TypeFullName}` ({topByBytes[0].TotalBytesPercent}% / {topByBytes[0].InstanceCount:N0} instances). Snapshot handle: `{handle.Id}`.";

        var hint = BuildHeapDrilldownHint(handle.Id, topByBytes);
        var result = hint is null
            ? DiagnosticResult.Ok(inspection, summary)
            : DiagnosticResult.Ok(inspection, summary, hint);
        return WithContext(result, ctx);
    }

    private static NextActionHint? BuildHeapDrilldownHint(string handle, IReadOnlyList<TypeStat> topByBytes)
    {
        // Prefer the cross-MCP handoff to dotnet-assembly-mcp when a type identity is available —
        // that pivots the LLM from "what is retained" to "what's the type definition / methods".
        var topWithHandoff = topByBytes.FirstOrDefault(t => t.Identity is { ModuleVersionId: not null, MetadataToken: not null });
        if (topWithHandoff is { Identity: { } id })
        {
            return new NextActionHint(
                "dotnet-assembly-mcp.get_method",
                $"Pivot to assembly inspection for the top retained type `{id.TypeFullName}` via the (mvid, token) handoff. " +
                $"Use query_heap_snapshot(handle=\"{handle}\", view=\"retention-paths\") to expand retention without re-walking.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = id.ModuleVersionId,
                    ["metadataToken"] = id.MetadataToken,
                    ["typeFullName"] = id.TypeFullName,
                    ["assemblyPathHint"] = id.ModulePath,
                });
        }

        // Fallback: at least point at the local drilldown tool.
        return new NextActionHint(
            "query_snapshot",
            "Drill into the snapshot (e.g. richer top-N, retention paths filtered by type) without re-walking the heap.",
            new Dictionary<string, object?>
            {
                ["handle"] = handle,
                ["view"] = "top-types",
            });
    }
}
