using DotnetDiagnosticsMcp.Core;

namespace DotnetDiagnosticsMcp.Core.Dump;

/// <summary>
/// Host-neutral drill-down engine for <see cref="HeapSnapshotArtifact"/> handles — the heap analogue
/// of <see cref="DotnetDiagnosticsMcp.Core.Collection.CollectionQueryDispatcher"/>. It renders the
/// <b>projection</b> views that need nothing but the already-walked snapshot (no live ClrMD attach, no
/// sensitive-value redactor, no authorization), so both the MCP server's <c>query_heap_snapshot</c>
/// tool and the standalone CLI <c>session</c> REPL (issue #300) share one implementation.
/// </summary>
/// <remarks>
/// Four heap views are deliberately <i>not</i> handled here and remain server-owned:
/// <c>object</c>/<c>gcroot</c>/<c>objsize</c> require a live ClrMD attach via <c>IDumpInspector</c>
/// plus the attach authorization guard, and <c>duplicate-strings</c> needs the server's
/// sensitive-value redactor + gate. For those, <see cref="Dispatch"/> reports
/// <see cref="HeapDispatchOutcome.ServerOnlyView"/> so the caller can route them appropriately (the
/// server falls through to its own handlers; the Core-only CLI surfaces a clear NotSupported envelope).
/// </remarks>
public static class HeapSnapshotQueryDispatcher
{
    // Views renderable purely from a HeapSnapshotArtifact (no attach / redactor / auth).
    private static readonly string[] Projection =
    {
        "top-types", "retention-paths", "roots-by-kind", "finalizer-queue", "fragmentation",
        "static-fields", "delegate-targets", "gchandles", "async",
    };

    private static readonly HashSet<string> ProjectionSet = new(Projection, StringComparer.Ordinal);

    // Views that require server-side capabilities (live ClrMD attach or the sensitive-value redactor).
    private static readonly HashSet<string> ServerOnly =
        new(StringComparer.Ordinal) { "object", "gcroot", "objsize", "duplicate-strings" };

    /// <summary>The view names this dispatcher can render from a snapshot alone (drill-down without re-walking).</summary>
    public static IReadOnlyList<string> ProjectionViews => Projection;

    /// <summary>
    /// Outcome union: <see cref="Result"/> is set for a projection view (including its own
    /// <c>InvalidArgument</c> failures); <see cref="ServerOnlyView"/> marks a known view that needs
    /// server-only capabilities; <see cref="UnknownView"/> marks an unrecognized view name.
    /// </summary>
    public readonly record struct HeapDispatchOutcome(
        DiagnosticResult<HeapSnapshotQueryResult>? Result,
        bool ServerOnlyView,
        bool UnknownView);

    /// <summary>
    /// Renders <paramref name="view"/> from <paramref name="snapshot"/>. <paramref name="view"/> is
    /// normalized (trim + lower-invariant) internally, so callers may pass the raw user value.
    /// </summary>
    public static HeapDispatchOutcome Dispatch(
        HeapSnapshotArtifact snapshot, string handle, string view, int topN, string? rankBy, string? typeFullName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = (view ?? string.Empty).Trim().ToLowerInvariant();

        if (ServerOnly.Contains(normalized))
        {
            return new HeapDispatchOutcome(null, ServerOnlyView: true, UnknownView: false);
        }

        if (!ProjectionSet.Contains(normalized))
        {
            return new HeapDispatchOutcome(null, ServerOnlyView: false, UnknownView: true);
        }

        // Mirror CollectionQueryDispatcher / the server preamble: any ranked view rejects topN < 1.
        if (topN < 1)
        {
            return new HeapDispatchOutcome(InvalidArg<HeapSnapshotQueryResult>(nameof(topN), "must be >= 1"), false, false);
        }

        var result = normalized switch
        {
            "top-types" => QueryTopTypes(snapshot, handle, topN, rankBy ?? "bytes"),
            "retention-paths" => QueryRetentionPaths(snapshot, handle, typeFullName, topN),
            "roots-by-kind" => QueryRootsByKind(snapshot, handle),
            "finalizer-queue" => QueryFinalizerQueue(snapshot, handle, topN),
            "fragmentation" => QueryFragmentation(snapshot, handle, topN),
            "static-fields" => QueryStaticFields(snapshot, handle, topN),
            "delegate-targets" => QueryDelegateTargets(snapshot, handle, topN),
            "gchandles" => QueryGcHandles(snapshot, handle),
            _ => QueryAsync(snapshot, handle, topN),
        };

        return new HeapDispatchOutcome(result, false, false);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryTopTypes(
        HeapSnapshotArtifact snapshot, string handle, int topN, string rankBy)
    {
        var normalizedRank = rankBy.Trim().ToLowerInvariant();
        IReadOnlyList<TypeStat> source = normalizedRank switch
        {
            "instances" => snapshot.TopTypesByInstances,
            "bytes" or "" => snapshot.TopTypesByBytes,
            _ => Array.Empty<TypeStat>(),
        };
        if (source.Count == 0 && normalizedRank is not ("instances" or "bytes" or ""))
        {
            return InvalidArg<HeapSnapshotQueryResult>(nameof(rankBy), $"must be 'bytes' or 'instances' (got '{rankBy}')");
        }

        var slice = source.Take(topN).ToArray();
        var origin = snapshot.Origin.ToString();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no recorded top types — heap walk produced 0 objects."
            : $"Returning {slice.Length} types ranked by {(normalizedRank == "instances" ? "instance count" : "retained bytes")} from snapshot '{handle}' ({origin}, captured {snapshot.CapturedAt:u}, pid {snapshot.ProcessId}). Top: `{slice[0].TypeFullName}` ({slice[0].TotalBytesPercent}% / {slice[0].InstanceCount:N0} instances).";

        var result = new HeapSnapshotQueryResult(handle, "top-types", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            TopTypes = slice,
            RankBy = normalizedRank.Length == 0 ? "bytes" : normalizedRank,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryRetentionPaths(
        HeapSnapshotArtifact snapshot, string handle, string? typeFullName, int topN)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.RetentionPaths is null || snapshot.RetentionPaths.Count == 0)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without retention paths.",
                new DiagnosticError("RetentionPathsMissing",
                    "Re-run inspect_dump or inspect_live_heap with includeRetentionPaths=true to populate the snapshot's retention data.",
                    handle),
                new NextActionHint("inspect_heap",
                    "Re-walk with includeRetentionPaths=true to populate retention chains for the top retained types.",
                    new Dictionary<string, object?> { ["processId"] = snapshot.ProcessId, ["includeRetentionPaths"] = true }));
        }

        IEnumerable<RetentionPath> filtered = snapshot.RetentionPaths;
        if (!string.IsNullOrWhiteSpace(typeFullName))
        {
            filtered = filtered.Where(p => p.TargetTypeFullName.Contains(typeFullName, StringComparison.OrdinalIgnoreCase));
        }

        var slice = filtered.Take(topN).ToArray();
        var summary = slice.Length == 0
            ? $"No retention paths in snapshot '{handle}' match filter '{typeFullName ?? "<none>"}'."
            : $"Returning {slice.Length} retention path(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top target: `{slice[0].TargetTypeFullName}` (chain depth {slice[0].Chain.Count}).";

        var result = new HeapSnapshotQueryResult(handle, "retention-paths", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            RetentionPaths = slice,
            FilterTypeFullName = typeFullName,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryRootsByKind(
        HeapSnapshotArtifact snapshot, string handle)
    {
        var origin = snapshot.Origin.ToString();
        var roots = snapshot.RootsByKind ?? Array.Empty<RootKindStat>();
        var summary = roots.Count == 0
            ? $"Snapshot '{handle}' has no recorded GC roots (heap walk produced 0 objects or root enumeration failed)."
            : $"Returning {roots.Count} root kind(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top: `{roots[0].RootKind}` — {roots[0].RootCount:N0} roots, {roots[0].DistinctTargetObjects:N0} distinct targets, {roots[0].DirectlyReferencedBytes:N0} bytes directly referenced.";
        var result = new HeapSnapshotQueryResult(handle, "roots-by-kind", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            RootsByKind = roots,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryFinalizerQueue(
        HeapSnapshotArtifact snapshot, string handle, int topN)
    {
        var origin = snapshot.Origin.ToString();
        var finalizable = snapshot.FinalizableObjectsByType ?? Array.Empty<FinalizableTypeStat>();
        var slice = finalizable.Take(topN).ToArray();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no objects waiting on the finalizer queue."
            : $"Returning {slice.Length} finalizable type(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top: `{slice[0].TypeFullName}` — {slice[0].InstanceCount:N0} instances, {slice[0].TotalBytes:N0} bytes. A growing finalizer queue is a classic memory-pressure smell.";
        var result = new HeapSnapshotQueryResult(handle, "finalizer-queue", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            FinalizableObjects = slice,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryFragmentation(
        HeapSnapshotArtifact snapshot, string handle, int topN)
    {
        var origin = snapshot.Origin.ToString();
        var segments = snapshot.Segments ?? Array.Empty<SegmentStat>();
        // Most fragmented first — only Gen2/LOH/POH free bytes count as actionable fragmentation;
        // ephemeral generations turn over too fast for it to matter.
        var ordered = segments
            .OrderByDescending(s => s.FreeBytes)
            .ThenByDescending(s => s.FreePercent)
            .Take(topN)
            .ToArray();
        var summary = ordered.Length == 0
            ? $"Snapshot '{handle}' has no recorded segments."
            : $"Returning {ordered.Length} segment(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Most fragmented: `{ordered[0].Generation}` segment @ 0x{ordered[0].Start:x} — {ordered[0].FreeBytes:N0}/{ordered[0].Length:N0} bytes free ({ordered[0].FreePercent}%).";
        var result = new HeapSnapshotQueryResult(handle, "fragmentation", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            Segments = ordered,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryStaticFields(
        HeapSnapshotArtifact snapshot, string handle, int topN)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.StaticFields is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without static-field walking.",
                new DiagnosticError("ViewNotCaptured", "Re-run inspect_dump / inspect_live_heap with includeStaticFields=true.", handle));
        }
        var slice = snapshot.StaticFields.Take(topN).ToArray();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no static reference fields with directly-referenced objects."
            : $"Returning {slice.Length} static field(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top retainer: `{slice[0].ContainingTypeFullName}.{slice[0].FieldName}` → `{slice[0].ValueTypeFullName ?? "<unknown>"}` ({slice[0].DirectlyReferencedBytes:N0} bytes).";
        var result = new HeapSnapshotQueryResult(handle, "static-fields", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            StaticFields = slice,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryDelegateTargets(
        HeapSnapshotArtifact snapshot, string handle, int topN)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.DelegateTargets is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without delegate-target aggregation.",
                new DiagnosticError("ViewNotCaptured", "Re-run inspect_dump / inspect_live_heap with includeDelegateTargets=true.", handle));
        }
        var slice = snapshot.DelegateTargets.Take(topN).ToArray();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no delegate targets (no MulticastDelegate instances detected)."
            : $"Returning {slice.Length} delegate target group(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top subscriber: `{slice[0].DeclaringTypeFullName}.{slice[0].MethodName}` (target=`{slice[0].TargetTypeFullName ?? "<static>"}`) — {slice[0].SubscriberCount:N0} subscription(s). High subscription counts on long-lived publishers are a classic event-handler-leak signal.";
        var result = new HeapSnapshotQueryResult(handle, "delegate-targets", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            DelegateTargets = slice,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryGcHandles(
        HeapSnapshotArtifact snapshot, string handle)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.GcHandles is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without GCHandle aggregation.",
                new DiagnosticError("ViewNotCaptured", "Re-run inspect_heap to capture the GCHandle table for this snapshot.", handle));
        }

        var view = snapshot.GcHandles;
        var busiest = view.ByKind.OrderByDescending(bucket => bucket.Count).ThenBy(bucket => bucket.Kind, StringComparer.Ordinal).FirstOrDefault();
        var summary = view.TotalHandles == 0
            ? $"Snapshot '{handle}' has no GCHandle entries."
            : $"Returning GCHandle aggregation from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) — {view.TotalHandles:N0} total handles. Busiest bucket: `{busiest?.Kind ?? "<none>"}` with {busiest?.Count ?? 0:N0} handle(s) retaining {busiest?.RetainedBytes ?? 0:N0} bytes across the immediate target objects.";

        if (view.Notes.Length > 0)
        {
            summary += $" Notes: {view.Notes[0]}";
        }

        var result = new HeapSnapshotQueryResult(handle, "gchandles", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            GcHandles = view,
        };

        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryAsync(
        HeapSnapshotArtifact snapshot, string handle, int topN)
    {
        var origin = snapshot.Origin.ToString();
        var asyncOperations = snapshot.AsyncOperations ?? Array.Empty<AsyncOperationStat>();
        var ordered = asyncOperations
            .OrderBy(op => op.ObservedOrder ?? long.MaxValue)
            .ThenByDescending(op => op.DirectSizeBytes)
            .Take(topN)
            .ToArray();
        var summary = ordered.Length == 0
            ? $"Snapshot '{handle}' has no pending async state machines."
            : $"Returning {ordered.Length} pending async operation(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). First pending state machine in heap-walk order: `{ordered[0].StateMachineTypeFullName}` (state {ordered[0].State}, awaiter `{ordered[0].AwaiterTypeFullName ?? "<unknown>"}`, async-stack depth {ordered[0].Stack?.Count ?? 0}).";
        var result = new HeapSnapshotQueryResult(handle, "async", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            AsyncOperations = ordered,
            SortedBy = ordered.Any(op => op.ObservedOrder.HasValue) ? "heap-order" : "direct-size",
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
