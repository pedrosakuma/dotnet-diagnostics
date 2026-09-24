using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.CpuEfficiency;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Threads;

namespace DotnetDiagnostics.Core.CaptureRecording;

/// <summary>
/// Projects self-contained retained rows, never source occurrences or a reconstructed heap graph.
/// No reattachment, symbol lookup, dump access, or runtime type activation is performed.
/// </summary>
/// <remarks>
/// Supports thread, heap, requests-now and whole-window CPU-efficiency artifacts.
/// Categories beginning with snapshot. must never be counted as source samples.
/// Nested row data is bounded at 64 KiB and explicitly marked when omitted; compatibility
/// artifacts and their quality/notes remain untouched. Native-only drilldowns remain dependencies.
/// </remarks>
internal static class SnapshotObservationProjection
{
    internal static bool Supports(string kind) => kind is "thread-snapshot" or "heap-snapshot" or "cpu-efficiency-sample" or "requests-now";

    internal static void Emit(string kind, object artifact, ICaptureObservationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        // Validation and view claims must remain identical to the compatibility codec.
        var views = CaptureArtifactCodec.GetSupportedSnapshotViews(kind, artifact);
        switch (kind, artifact)
        {
            case ("thread-snapshot", ThreadSnapshotArtifact threads):
                Metadata(sink, kind, threads.CapturedAt, threads.ProcessId, threads.Origin.ToString(), views);
                EmitThreads(threads, sink);
                break;
            case ("heap-snapshot", HeapSnapshotArtifact heap):
                Metadata(sink, kind, heap.CapturedAt, heap.ProcessId, heap.Origin.ToString(), views);
                EmitHeap(heap, sink);
                break;
            case ("cpu-efficiency-sample", CpuEfficiencySample efficiency):
                Metadata(sink, kind, efficiency.StartedAt, efficiency.ProcessId, efficiency.Backend, views);
                Row(sink, "cpu-efficiency.window", efficiency.StartedAt, null, efficiency.Backend, efficiency);
                break;
            case ("requests-now", RequestsNowSnapshot requests):
                Metadata(sink, kind, requests.CapturedAt, requests.ProcessId, "eventpipe-window-retained", views);
                foreach (var request in requests.Requests)
                    Row(sink, "requests-now.request", requests.CapturedAt, request.ThreadId, request.Endpoint, request,
                        [CaptureObservationField.String("sourceClock", "eventpipe-relative-milliseconds")]);
                break;
            default:
                throw new NotSupportedException($"No retained-row projection for capture kind '{kind}' and type '{artifact?.GetType().Name}'.");
        }
    }

    private static void Metadata(ICaptureObservationSink sink, string kind, DateTimeOffset time, int pid, string origin, IReadOnlyList<string> views)
        => sink.TryAppend(new CaptureObservation("snapshot." + kind + ".metadata", time, null, kind,
        [
            CaptureObservationField.Bool("sourceOccurrence", false),
            CaptureObservationField.Bool("derivedRetainedRow", true),
            CaptureObservationField.String("provenance", "registered-artifact"),
            CaptureObservationField.String("timestampMeaning", "snapshot-window-not-occurrence"),
            CaptureObservationField.Int64("processId", pid),
            CaptureObservationField.String("origin", origin),
            CaptureObservationField.String("availableViews", string.Join(",", views)),
            CaptureObservationField.Bool("fullObjectGraph", false),
        ]));

    private static void EmitThreads(ThreadSnapshotArtifact snapshot, ICaptureObservationSink sink)
    {
        foreach (var thread in snapshot.Threads)
        {
            // Frames are separate indexed rows, not duplicated in every thread row.
            Row(sink, "thread.thread", snapshot.CapturedAt, thread.OSThreadId, thread.TopFrameMethod, thread with { Frames = [] },
            [
                CaptureObservationField.Bool("IsContendedLockOwner", thread.IsContendedLockOwner),
                CaptureObservationField.Bool("IsLockWaiter", thread.IsLockWaiter),
                CaptureObservationField.Bool("IsDeadlockCandidate", thread.IsDeadlockCandidate),
            ]);
            for (var i = 0; i < thread.Frames.Count; i++)
                Row(sink, "thread.frame", snapshot.CapturedAt, thread.OSThreadId, thread.Frames[i].DisplayName, thread.Frames[i],
                    [CaptureObservationField.Int64("frameIndex", i)]);
        }
        foreach (var monitor in snapshot.Locks)
            Row(sink, "thread.lock", snapshot.CapturedAt, monitor.OwnerOSThreadId, monitor.ObjectTypeFullName, monitor);
        if (snapshot.ThreadPool is { } pool)
            Row(sink, "thread.pool", snapshot.CapturedAt, null, "thread-pool", pool);
    }

    private static void EmitHeap(HeapSnapshotArtifact snapshot, ICaptureObservationSink sink)
    {
        var time = snapshot.CapturedAt;
        Row(sink, "heap.summary", time, null, "retained-heap-summary", snapshot.Heap);
        foreach (var type in snapshot.TopTypesByBytes)
            Row(sink, "heap.type-by-bytes", time, null, type.TypeFullName, type);
        foreach (var type in snapshot.TopTypesByInstances)
            Row(sink, "heap.type-by-instances", time, null, type.TypeFullName, type);
        foreach (var path in snapshot.RetentionPaths ?? [])
            Row(sink, "heap.retention-path", time, null, path.TargetTypeFullName, path);
        foreach (var root in snapshot.RootsByKind ?? [])
            Row(sink, "heap.root-kind", time, null, root.RootKind, root);
        foreach (var type in snapshot.FinalizableObjectsByType ?? [])
            Row(sink, "heap.finalizable-type", time, null, type.TypeFullName, type);
        foreach (var segment in snapshot.Segments ?? [])
            Row(sink, "heap.segment", time, null, segment.Kind, segment);
        foreach (var field in snapshot.StaticFields ?? [])
            Row(sink, "heap.static-field", time, null, field.FieldName, field);
        foreach (var target in snapshot.DelegateTargets ?? [])
            Row(sink, "heap.delegate-target", time, null, target.MethodName, target);
        foreach (var operation in snapshot.AsyncOperations ?? [])
            Row(sink, "heap.async-operation", time, null, operation.StateMachineTypeFullName, operation);
        if (snapshot.GcHandles is { } handles)
        {
            foreach (var bucket in handles.ByKind)
            {
                Row(sink, "heap.gc-handle-bucket", time, null, bucket.Kind, bucket with { TopTypes = [] });
                foreach (var type in bucket.TopTypes)
                    Row(sink, "heap.gc-handle-type", time, null, type.TypeFullName, type,
                        [CaptureObservationField.String("handleKind", bucket.Kind)]);
            }
        }
        if (snapshot.Timers is { } timers)
        {
            Row(sink, "heap.timer-task-summary", time, null, "timer-task-summary",
                timers with { TimersByCallback = [], TasksByType = [], TaskCompletionSourcesByType = [] });
            foreach (var timer in timers.TimersByCallback)
                Row(sink, "heap.timer-callback", time, null, timer.MethodName, timer);
            foreach (var task in timers.TasksByType)
                Row(sink, "heap.task-type", time, null, task.TypeFullName, task);
            foreach (var task in timers.TaskCompletionSourcesByType)
                Row(sink, "heap.task-completion-source-type", time, null, task.TypeFullName, task);
        }
        if (snapshot.AssemblyLoadContexts is { } contexts)
        {
            Row(sink, "heap.assembly-load-context-summary", time, null, "assembly-load-context-summary", contexts with { Contexts = [] });
            foreach (var context in contexts.Contexts)
            {
                Row(sink, "heap.assembly-load-context", time, null, context.Name, context with { Assemblies = [] });
                foreach (var assembly in context.Assemblies)
                    Row(sink, "heap.assembly", time, null, assembly.AssemblyName, assembly,
                        [CaptureObservationField.String("contextAddress", context.Address.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
            }
        }
        // Duplicate strings, object/gcroot/objsize drilldowns still require native.dump;
        // selected paths and aggregate types do not constitute that dependency.
    }

    private static void Row<T>(ICaptureObservationSink sink, string category, DateTimeOffset time, long? threadId,
        string? name, T row, IReadOnlyList<CaptureObservationField>? additional = null)
    {
        var fields = new List<CaptureObservationField>
        {
            CaptureObservationField.Bool("sourceOccurrence", false),
            CaptureObservationField.Bool("derivedRetainedRow", true),
            CaptureObservationField.String("provenance", "registered-artifact"),
            CaptureObservationField.String("timestampMeaning", "snapshot-window-not-occurrence"),
        };
        if (additional is not null) fields.AddRange(additional);
        // Every caller above fixes T to a codec-owned DTO. Source-generated metadata prevents
        // arbitrary reflection serialization and the writer bounds nested structured fields.
        var info = (JsonTypeInfo<T>?)CaptureSnapshotEncodingContext.Default.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"No allowlisted row metadata for {typeof(T).Name}.");
        using var buffer = new BoundedSnapshotEncoding(SamplerObservationProjection.MaximumStructuredBytes);
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            JsonSerializer.Serialize(writer, row, info);
            writer.Flush();
            using var json = JsonDocument.Parse(buffer.WrittenSpan.ToArray());
            foreach (var property in json.RootElement.EnumerateObject())
            {
                var value = property.Value;
                fields.Add(value.ValueKind switch
                {
                    JsonValueKind.Null => CaptureObservationField.Null(property.Name),
                    JsonValueKind.String => CaptureObservationField.String(property.Name, value.GetString()),
                    JsonValueKind.True => CaptureObservationField.Bool(property.Name, true),
                    JsonValueKind.False => CaptureObservationField.Bool(property.Name, false),
                    JsonValueKind.Number when value.TryGetInt64(out var integer) => CaptureObservationField.Int64(property.Name, integer),
                    JsonValueKind.Number when !value.TryGetUInt64(out _) => CaptureObservationField.Double(property.Name, value.GetDouble()),
                    _ => CaptureObservationField.String(property.Name, value.GetRawText()),
                });
            }
            fields.Add(CaptureObservationField.Bool("structuredOmitted", false));
        }
        catch (InvalidDataException)
        {
            fields.Add(CaptureObservationField.Bool("structuredOmitted", true));
        }
        sink.TryAppend(new CaptureObservation("snapshot." + category, time, threadId, name, fields));
    }
}
