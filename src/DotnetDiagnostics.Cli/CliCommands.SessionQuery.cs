using System.Globalization;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

internal static partial class CliCommands
{
    private static CliCommandResult Query()
    {
        var result = DiagnosticResult.Fail<object>(
            "The 'query' drill-down command needs a live session. Start one with 'dotnet-diagnostics session'.",
            new DiagnosticError(
                "NotSupported",
                "Drill-down handles are scoped to a live session. The one-shot CLI is stateless, so a handle "
                + "from a previous command no longer exists. Start the interactive REPL with 'dotnet-diagnostics "
                + "session': there, a 'collect' (or 'inspect-heap' / 'dump') issues a handle you can drill into "
                + "with 'query --handle <id> --view <view>' in the same session. For a one-shot answer instead, "
                + "re-run the originating command with --depth detail (or --json) to get the full result inline.",
                "one-shot-cli"),
            new NextActionHint("session", "Start the interactive session REPL, then collect and query --handle <id> --view <view> there."),
            new NextActionHint("collect", "Or re-run the originating command with --depth detail (or --json) to get the full result inline."));

        return BuildResult<object>(result, static (_, _) => { });
    }

    /// <summary>
    /// Dispatcher views the <c>session</c> <c>query</c> path cannot render yet because they correlate a
    /// second collected artifact the session has no way to supply (currently only the activities
    /// <c>gc-overlay</c>, which needs a GC handle). They are hidden from the advertised view list and
    /// rejected with a clear <c>NotSupportedInSession</c> rather than the dispatcher's confusing
    /// "missing correlate" <c>InvalidArgument</c>.
    /// </summary>
    private static readonly HashSet<string> SessionExcludedViews =
        new(StringComparer.OrdinalIgnoreCase) { "gc-overlay" };

    /// <summary>
    /// Handle kinds backing a <see cref="CpuSampleTraceArtifact"/> (directly, or wrapped in an
    /// <see cref="AllocationSampleArtifact"/>) whose session drill-down is the host-neutral
    /// <c>call-tree</c> view. Keep in sync with the server's <c>cpu-sample</c> /
    /// <c>allocation-sample</c> / <c>native-alloc-sample</c> handle registrations.
    /// </summary>
    private static readonly HashSet<string> CpuSampleSessionKinds =
        new(StringComparer.Ordinal) { "cpu-sample", "allocation-sample", "native-alloc-sample" };

    /// <summary>
    /// Handle kind backing a <see cref="ThreadSnapshotArtifact"/> whose session drill-down is served by
    /// the host-neutral <see cref="ThreadSnapshotQueryDispatcher"/>. Keep in sync with the server's
    /// <c>collect_thread_snapshot</c> handle registration (<c>thread-snapshot</c>).
    /// </summary>
    private const string ThreadSnapshotSessionKind = "thread-snapshot";

    /// <summary>
    /// Handle kind backing an <see cref="OffCpuSnapshotArtifact"/> whose session drill-down is served by
    /// the host-neutral <see cref="OffCpuQueryDispatcher"/>. Keep in sync with the server's
    /// <c>collect_off_cpu_sample</c> handle registration (<c>off-cpu-snapshot</c>).
    /// </summary>
    private const string OffCpuSessionKind = "off-cpu-snapshot";

    /// <summary>
    /// All thread-snapshot views available in the session REPL: the nine purely artifact-based
    /// <see cref="ThreadSnapshotQueryDispatcher.SessionViews"/> plus <c>frame-vars</c>, which
    /// re-opens the snapshot origin via ClrMD to walk one thread's local variables and parameters.
    /// </summary>
    private static readonly IReadOnlyList<string> ThreadSnapshotAllSessionViews =
        [.. ThreadSnapshotQueryDispatcher.SessionViews, "frame-vars"];

    /// <summary>
    /// The subset of <see cref="CollectionQueryDispatcher.ViewsFor(string)"/> that the session
    /// <c>query</c> path can actually render for <paramref name="kind"/> — i.e. minus
    /// <see cref="SessionExcludedViews"/>. Used both to advertise valid views after a collect and to
    /// list them in the unknown-view error, so the two never drift.
    /// </summary>
    public static IReadOnlyList<string> SessionViewsFor(string kind)
    {
        if (kind == HeapInspectionUseCases.HeapSnapshotKind)
        {
            return HeapSnapshotQueryDispatcher.ProjectionViews;
        }

        if (CpuSampleSessionKinds.Contains(kind))
        {
            return CpuSampleQueryDispatcher.SessionViews;
        }

        if (kind == ThreadSnapshotSessionKind)
        {
            return ThreadSnapshotAllSessionViews;
        }

        if (kind == OffCpuSessionKind)
        {
            return OffCpuQueryDispatcher.SessionViews;
        }

        if (kind == CollectionHandleKinds.EventCatalog)
        {
            return EventCatalogQueryDispatcher.SessionViews;
        }

        if (kind == CollectionHandleKinds.GcDatas)
        {
            return GcDatasQueryDispatcher.SessionViews;
        }

        var all = CollectionQueryDispatcher.ViewsFor(kind);
        var result = new List<string>(all.Count);
        foreach (var view in all)
        {
            if (!SessionExcludedViews.Contains(view))
            {
                result.Add(view);
            }
        }

        return result;
    }

    /// <summary>
    /// JSON used to pretty-print a <see cref="CollectionQueryResult.Payload"/> in the <c>session</c>
    /// REPL's human render so the user sees the drill-down data without re-typing <c>--json</c>.
    /// </summary>
    private static readonly JsonSerializerOptions QueryJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The <c>query</c> drill-down command <b>inside the stateful <c>session</c> REPL</b> (issue #300).
    /// Unlike the one-shot <see cref="Query()"/> (which returns <c>NotSupported</c> because no handle
    /// store survives the process), the REPL keeps the shared <see cref="IDiagnosticHandleStore"/>
    /// alive, so a handle published by an earlier <c>collect</c> can be re-rendered under a different
    /// view via <see cref="CollectionQueryDispatcher"/> — with no re-collection. Only the 10 collection
    /// kinds are supported here; heap/cpu/thread drill-down routing still lives in the MCP server
    /// (deferred to a follow-up PR) and yields a clear <c>NotSupportedInSession</c> envelope.
    /// </summary>
    public static async Task<CliCommandResult> QuerySession(
        IServiceProvider services, CliOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Handle))
        {
            return Fail("query: --handle <id> is required.", "InvalidArgument",
                "Pass the handle printed after a collect command, e.g. query --handle <id> --view <view>.");
        }

        var store = services.GetRequiredService<IDiagnosticHandleStore>();
        var lookup = store.TryGetWithKind(options.Handle);
        if (lookup is null)
        {
            return Fail($"query: handle '{options.Handle}' is unknown or expired.", "NotFound",
                "Handles are evicted when they expire or when the target process exits. Re-run the originating collect command to get a fresh handle.");
        }

        var kind = lookup.Value.Kind;

        // Heap snapshot handles drill down through the host-neutral HeapSnapshotQueryDispatcher (#300):
        // the projection views render from the walked snapshot alone (no ClrMD runtime, no
        // sensitive-value redactor), which is exactly the subset a stateless session can serve. The
        // address-addressed views need a ClrMD runtime: for a dump-origin handle the session re-opens
        // the dump file (Core-only, no live attach) to serve `gcroot`/`object` (#464); `objsize` and the
        // sensitive `duplicate-strings` view, plus all live-origin attaches, stay server-only.
        if (kind == HeapInspectionUseCases.HeapSnapshotKind)
        {
            return await QueryHeapSession(services, options, lookup.Value.Artifact, cancellationToken).ConfigureAwait(false);
        }

        // CPU / allocation / native-alloc sample handles drill down through the host-neutral
        // CpuSampleQueryDispatcher (#300): the merged call-tree renders from the collected trace alone.
        // The `diff` view stays server-only (it correlates a second baseline handle).
        if (CpuSampleSessionKinds.Contains(kind))
        {
            return QueryCpuSampleSession(options, lookup.Value.Artifact);
        }

        // Thread-snapshot handles drill down through the host-neutral ThreadSnapshotQueryDispatcher
        // (#300): most views render from the captured artifact alone — no live ClrMD attach. The
        // frame-vars view re-opens the origin via ClrMD to resolve one thread's local variables
        // (#487) and is therefore async.
        if (kind == ThreadSnapshotSessionKind)
        {
            return await QueryThreadSnapshotSessionAsync(services, options, lookup.Value.Artifact, cancellationToken).ConfigureAwait(false);
        }

        // Off-CPU handles drill down through the host-neutral OffCpuQueryDispatcher (#300): topStacks,
        // byThread and stack all re-project the captured artifact — no perf re-run.
        if (kind == OffCpuSessionKind)
        {
            return QueryOffCpuSession(options, lookup.Value.Artifact);
        }

        if (kind == CollectionHandleKinds.EventCatalog)
        {
            return QueryEventCatalogSession(options, lookup.Value.Artifact);
        }

        // DATAS handles drill down through the host-neutral GcDatasQueryDispatcher (#315): overview,
        // tuning, samples and gen2 all re-project the captured snapshot — no EventPipe re-run.
        if (kind == CollectionHandleKinds.GcDatas)
        {
            return QueryGcDatasSession(options, lookup.Value.Artifact);
        }

        var allowedViews = CollectionQueryDispatcher.ViewsFor(kind);
        if (allowedViews.Count == 0)
        {
            return Fail($"query: drill-down for '{kind}' handles is not available in the session yet.", "NotSupportedInSession",
                "Heap / CPU / thread drill-down routing still lives in the MCP server; re-run the originating command (e.g. inspect-heap) with the inline flags you need.");
        }

        // Some dispatcher views correlate a second collected artifact (e.g. activities gc-overlay needs
        // a GC handle) that the session can't supply yet — reject them with a clear message instead of
        // letting the dispatcher fail with a confusing "missing correlate" InvalidArgument.
        if (!string.IsNullOrWhiteSpace(options.View) && SessionExcludedViews.Contains(options.View))
        {
            return Fail($"query: view '{options.View}' for a '{kind}' handle is not available in the session yet.", "NotSupportedInSession",
                "This view correlates two collected artifacts, which the session cannot supply yet; re-run the originating command with the inline flags you need.");
        }

        var topN = options.TopTypes ?? 50;
        var outcome = CollectionQueryDispatcher.Dispatch(kind, options.View, lookup.Value.Artifact, topN);

        if (outcome.Result is { } queryResult)
        {
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"query: {queryResult.Kind} view={queryResult.View} pid={queryResult.ProcessId}");
            var ok = DiagnosticResult.Ok(queryResult, summary);
            return BuildResult<CollectionQueryResult>(ok, static (sb, qr) =>
            {
                sb.AppendLine();
                sb.AppendLine(JsonSerializer.Serialize(qr.Payload, qr.Payload.GetType(), QueryJsonOptions));
            });
        }

        if (outcome.UnknownView is { } badView)
        {
            var sessionViews = SessionViewsFor(kind);
            var views = sessionViews.Count > 0 ? string.Join(", ", sessionViews) : string.Join(", ", allowedViews);
            return Fail($"query: unknown view '{badView}' for a '{kind}' handle.", "InvalidArgument",
                $"Valid views: {views}.");
        }

        if (outcome.InvalidArgument is { } invalid)
        {
            return Fail($"query: {invalid}.", "InvalidArgument",
                "Adjust the argument and retry, e.g. --top-types 20.");
        }

        // UnknownKind here means the stored artifact's runtime type did not match the handle kind.
        return Fail($"query: handle '{options.Handle}' could not be rendered as '{kind}'.", "InvalidArgument",
            "The stored artifact type did not match its handle kind; re-run the originating collect command.");
    }

    /// <summary>
    /// Renders a heap-snapshot drill-down inside the <c>session</c> REPL via the host-neutral
    /// <see cref="HeapSnapshotQueryDispatcher"/>. Projection views (<c>top-types</c>,
    /// <c>retention-paths</c>, …) render from the walked snapshot. The address-addressed
    /// <c>gcroot</c>/<c>object</c> views are served for <b>dump-origin</b> handles by re-opening the
    /// dump file through <see cref="IDumpInspector"/> (ClrMD walks GC roots on a dump DataTarget
    /// exactly like a live one — #464); <c>objsize</c>, <c>duplicate-strings</c>, and every
    /// live-origin attach stay server-only and yield a clear <c>NotSupportedInSession</c> envelope.
    /// </summary>
    private static async Task<CliCommandResult> QueryHeapSession(
        IServiceProvider services, CliOptions options, object artifact, CancellationToken cancellationToken)
    {
        if (artifact is not HeapSnapshotArtifact heap)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as a heap snapshot.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run inspect-heap to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? "top-types" : options.View;
        var topN = options.TopTypes ?? 50;
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(heap, options.Handle!, view, topN, options.RankBy, options.TypeFilter);

        if (outcome.Result is { } heapResult)
        {
            return BuildResult<HeapSnapshotQueryResult>(heapResult, static (sb, qr) =>
            {
                sb.AppendLine();
                sb.AppendLine(JsonSerializer.Serialize(qr, QueryJsonOptions));
            });
        }

        if (outcome.ServerOnlyView)
        {
            var normalized = view.Trim().ToLowerInvariant();

            // #464: gcroot/object over a dump-origin snapshot need no live attach — re-open the dump
            // file with ClrMD (Core-only) and walk roots / read the object against the dump DataTarget.
            if ((normalized == "gcroot" || normalized == "object") && heap.Origin == HeapSnapshotOrigin.Dump)
            {
                return await QueryHeapDumpDrilldown(services, options, heap, normalized, cancellationToken).ConfigureAwait(false);
            }

            var detail = normalized == "duplicate-strings"
                ? "The 'duplicate-strings' view exposes raw string previews behind the server's sensitive-value policy, which the standalone CLI cannot enforce; run the MCP server if you need it."
                : "The 'object', 'gcroot' and 'objsize' views need a ClrMD runtime: 'gcroot'/'object' are served in-session for dump-origin handles, but a live attach (and 'objsize') require the MCP server's query_heap_snapshot tool.";
            return Fail($"query: view '{view}' for a heap snapshot is not available in the session yet.", "NotSupportedInSession", detail);
        }

        // outcome.UnknownView
        return Fail($"query: unknown view '{view}' for a heap snapshot.", "InvalidArgument",
            $"Valid views: {string.Join(", ", HeapSnapshotQueryDispatcher.ProjectionViews)}.");
    }

    /// <summary>
    /// Serves the address-addressed <c>gcroot</c>/<c>object</c> heap views for a <b>dump-origin</b>
    /// snapshot inside the session (#464). The injected <see cref="IDumpInspector"/> re-opens the dump
    /// file recorded on the artifact (no live attach) and walks the GC-root chain or reads the managed
    /// object. The <c>object</c> view never emits raw string/field/array values — the Core-only CLI has
    /// no sensitive-value gate, so previews are replaced with the metadata-only placeholder, matching
    /// the MCP server's redacted default.
    /// </summary>
    private static async Task<CliCommandResult> QueryHeapDumpDrilldown(
        IServiceProvider services, CliOptions options, HeapSnapshotArtifact heap, string view, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Address))
        {
            return Fail($"query: --address <addr> is required for view '{view}'.", "InvalidArgument",
                "Pass the managed object address (decimal or 0x-hex), e.g. query --handle <id> --view gcroot --address 0x1f2a3b40.");
        }

        if (!TryParseAddress(options.Address, out var address))
        {
            return Fail($"query: '--address {options.Address}' is not a valid object address.", "InvalidArgument",
                "Use a non-zero decimal or 0x-prefixed hex address taken from a retention-paths / top-types row.");
        }

        var inspector = services.GetRequiredService<IDumpInspector>();
        DiagnosticResult<HeapSnapshotQueryResult> result;
        try
        {
            result = view == "gcroot"
                ? BuildGcRootResult(heap, options.Handle!, await inspector.InspectGcRootAsync(heap, address, cancellationToken).ConfigureAwait(false))
                : BuildObjectResult(heap, options.Handle!, await inspector.InspectObjectAsync(heap, address, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"query: view '{view}' could not be served from the dump.", "InspectionFailed", ex.Message);
        }

        return BuildResult<HeapSnapshotQueryResult>(result, static (sb, qr) =>
        {
            sb.AppendLine();
            sb.AppendLine(JsonSerializer.Serialize(qr, QueryJsonOptions));
        });
    }

    /// <summary>Parses a managed object address (decimal or <c>0x</c>-hex); rejects zero.</summary>
    private static bool TryParseAddress(string value, out ulong result)
    {
        result = 0;
        var s = value.Trim();
        var parsed = (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result)
            : ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result));
        return parsed && result != 0;
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> BuildGcRootResult(
        HeapSnapshotArtifact heap, string handle, HeapGcRootInspection inspection)
    {
        var origin = heap.Origin.ToString();
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"query: gcroot view origin={origin} pid={heap.ProcessId} address=0x{inspection.Address:x} type={inspection.TypeFullName} frames={inspection.Chain.Count}{(inspection.Truncated ? " (truncated by BFS/depth caps)" : string.Empty)}");
        var result = new HeapSnapshotQueryResult(handle, "gcroot", origin, heap.ProcessId, heap.CapturedAt)
        {
            Address = inspection.Address,
            GcRoot = inspection,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> BuildObjectResult(
        HeapSnapshotArtifact heap, string handle, HeapObjectInspection inspection)
    {
        var origin = heap.Origin.ToString();
        var redacted = RedactObjectPreviews(inspection);
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"query: object view origin={origin} pid={heap.ProcessId} address=0x{redacted.Address:x} type={redacted.TypeFullName} size={redacted.Size} (string/field previews redacted — no session sensitive-value gate)");
        var result = new HeapSnapshotQueryResult(handle, "object", origin, heap.ProcessId, heap.CapturedAt)
        {
            Address = redacted.Address,
            ObjectDetails = redacted,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    /// <summary>
    /// Replaces every string / field / array-element preview with the metadata-only placeholder so the
    /// Core-only session never surfaces raw heap content (it holds no sensitive-value gate). Object
    /// shape — type, size, generation, segment, array length, field names/types — is preserved.
    /// </summary>
    private static HeapObjectInspection RedactObjectPreviews(HeapObjectInspection inspection)
    {
        IReadOnlyList<HeapObjectField>? fields = inspection.Fields;
        if (fields is { Count: > 0 })
        {
            var redacted = new List<HeapObjectField>(fields.Count);
            foreach (var f in fields)
            {
                redacted.Add(new HeapObjectField(f.Name, f.TypeFullName, SensitiveDataRedactor.MetadataOnlyPlaceholder)
                {
                    ObjectAddress = f.ObjectAddress,
                    ReferencedTypeFullName = f.ReferencedTypeFullName,
                });
            }

            fields = redacted;
        }

        IReadOnlyList<HeapArrayElement>? array = inspection.ArraySample;
        if (array is { Count: > 0 })
        {
            var redacted = new List<HeapArrayElement>(array.Count);
            foreach (var a in array)
            {
                redacted.Add(new HeapArrayElement(a.Index, a.TypeFullName, SensitiveDataRedactor.MetadataOnlyPlaceholder)
                {
                    ObjectAddress = a.ObjectAddress,
                    ReferencedTypeFullName = a.ReferencedTypeFullName,
                });
            }

            array = redacted;
        }

        return new HeapObjectInspection(inspection.Address, inspection.TypeFullName, inspection.Size, inspection.SegmentKind, inspection.Generation)
        {
            IsArray = inspection.IsArray,
            ArrayLength = inspection.ArrayLength,
            ArraySample = array,
            IsString = inspection.IsString,
            StringValue = inspection.IsString ? SensitiveDataRedactor.MetadataOnlyPlaceholder : inspection.StringValue,
            StringValueTruncated = inspection.StringValueTruncated,
            Fields = fields,
            Warnings = inspection.Warnings,
        };
    }

    /// <summary>
    /// Renders a CPU / allocation / native-alloc sample drill-down inside the <c>session</c> REPL via the
    /// host-neutral <see cref="CpuSampleQueryDispatcher"/>. Only the <c>call-tree</c> view is served; the
    /// <c>diff</c> view stays server-only (it correlates a baseline handle) and yields a clear
    /// <c>NotSupportedInSession</c> envelope.
    /// </summary>
    private static CliCommandResult QueryCpuSampleSession(CliOptions options, object artifact)
    {
        var trace = CpuSampleQueryDispatcher.ResolveTrace(artifact);
        if (trace is null)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as a CPU/allocation sample.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run the originating collect command to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? CpuSampleQueryDispatcher.CallTreeView : options.View;
        var normalized = view.Trim().ToLowerInvariant();

        if (normalized == "diff")
        {
            return Fail($"query: view '{view}' for a CPU sample handle is not available in the session yet.", "NotSupportedInSession",
                "The 'diff' view correlates a baseline handle the session cannot supply; run the MCP server's query_snapshot(view='diff') with a baselineHandle.");
        }

        var handle = options.Handle!;
        var topN = options.Top ?? CpuSampleQueryDispatcher.DefaultTopN;

        switch (normalized)
        {
            case CpuSampleQueryDispatcher.TopMethodsView:
                return BuildResult(CpuSampleQueryDispatcher.RenderTopMethods(trace, handle, options.RankBy, topN), SerializeQuery);
            case CpuSampleQueryDispatcher.ByModuleView:
                return BuildResult(CpuSampleQueryDispatcher.RenderByModule(trace, handle, topN), SerializeQuery);
            case CpuSampleQueryDispatcher.ByNamespaceView:
                return BuildResult(CpuSampleQueryDispatcher.RenderByNamespace(trace, handle, topN), SerializeQuery);
            case CpuSampleQueryDispatcher.HotPathView:
                return BuildResult(CpuSampleQueryDispatcher.RenderHotPath(trace, handle, options.Threshold ?? CpuSampleQueryDispatcher.DefaultHotPathThresholdPercent), SerializeQuery);
            case CpuSampleQueryDispatcher.CallerCalleeView:
                return BuildResult(CpuSampleQueryDispatcher.RenderCallerCallee(trace, handle, options.RootMethodFilter, topN), SerializeQuery);
            case CpuSampleQueryDispatcher.CallTreeView:
                break;
            default:
                return Fail($"query: unknown view '{view}' for a CPU sample handle.", "InvalidArgument",
                    $"Valid views: {string.Join(", ", CpuSampleQueryDispatcher.SessionViews)}.");
        }

        var maxDepth = options.MaxDepth ?? 8;
        var maxNodes = options.MaxNodes ?? 200;
        var result = CpuSampleQueryDispatcher.RenderCallTree(trace, handle, options.RootMethodFilter, maxDepth, maxNodes);

        return BuildResult<CallTreeView>(result, SerializeQuery);
    }

    private static void SerializeQuery<T>(StringBuilder sb, T payload)
    {
        sb.AppendLine();
        sb.AppendLine(JsonSerializer.Serialize(payload, QueryJsonOptions));
    }

    /// <summary>
    /// Renders an event-catalog drill-down inside the <c>session</c> REPL via the host-neutral
    /// <see cref="EventCatalogQueryDispatcher"/>. Occurrence samples are metadata-only; payload values
    /// are never captured by the catalog collector.
    /// </summary>
    private static CliCommandResult QueryEventCatalogSession(CliOptions options, object artifact)
    {
        if (artifact is not EventCatalogSnapshot snapshot)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as an event catalog.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run collect --kind catalog to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? EventCatalogQueryDispatcher.CatalogView : options.View;
        if (!EventCatalogQueryDispatcher.IsKnownView(view))
        {
            return Fail($"query: unknown view '{view}' for an event-catalog handle.", "InvalidArgument",
                $"Valid views: {string.Join(", ", EventCatalogQueryDispatcher.SessionViews)}.");
        }

        var topN = options.Top ?? options.TopTypes ?? EventCatalogQueryDispatcher.DefaultTopN;
        var result = EventCatalogQueryDispatcher.Render(
            snapshot,
            options.Handle!,
            view,
            topN,
            options.ProviderFilter,
            options.RootMethodFilter);

        return BuildResult<object>(result, SerializeQuery);
    }

    /// <summary>
    /// Renders a DATAS drill-down inside the <c>session</c> REPL via the host-neutral
    /// <see cref="GcDatasQueryDispatcher"/>. The overview, tuning, samples and gen2 views all render
    /// from the captured snapshot alone — no EventPipe re-run. The <c>tuning</c> view honours
    /// <c>--changes-only</c>.
    /// </summary>
    private static CliCommandResult QueryGcDatasSession(CliOptions options, object artifact)
    {
        if (artifact is not GcDatasSnapshot snapshot)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as a DATAS snapshot.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run collect --kind datas to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? GcDatasQueryDispatcher.OverviewView : options.View;
        if (!GcDatasQueryDispatcher.IsKnownView(view))
        {
            return Fail($"query: unknown view '{view}' for a DATAS handle.", "InvalidArgument",
                $"Valid views: {string.Join(", ", GcDatasQueryDispatcher.SessionViews)}.");
        }

        var topN = options.Top ?? options.TopTypes ?? GcDatasQueryDispatcher.DefaultTopN;
        var result = GcDatasQueryDispatcher.Render(
            snapshot,
            options.Handle!,
            view,
            topN,
            options.ChangesOnly);

        return BuildResult<object>(result, SerializeQuery);
    }

    /// <summary>
    /// Renders a thread-snapshot drill-down inside the <c>session</c> REPL. The nine artifact-based
    /// views (<see cref="ThreadSnapshotQueryDispatcher.SessionViews"/>) render purely from the
    /// captured snapshot via the host-neutral <see cref="ThreadSnapshotQueryDispatcher"/>. The
    /// <c>frame-vars</c> view (#487) re-opens the snapshot origin via <see cref="IFrameVariableResolver"/>
    /// (ClrMD, same ptrace/dump-read footprint as the original snapshot) and returns the object-typed
    /// locals/parameters on each managed frame of the specified thread.
    /// </summary>
    private static async Task<CliCommandResult> QueryThreadSnapshotSessionAsync(
        IServiceProvider services, CliOptions options, object artifact, CancellationToken cancellationToken)
    {
        if (artifact is not ThreadSnapshotArtifact snapshot)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as a thread snapshot.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run the originating collect command to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? "top-blocked" : options.View;

        if (string.Equals(view, "frame-vars", StringComparison.OrdinalIgnoreCase))
        {
            return await QueryFrameVarsAsync(services, options, snapshot, cancellationToken).ConfigureAwait(false);
        }

        var topN = options.TopTypes ?? 50;
        var framesToHash = options.FramesToHash ?? 20;
        var minCount = options.MinCount ?? 1;
        var result = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, options.Handle!, view, options.ThreadId, topN, framesToHash, minCount);

        return BuildResult<ThreadSnapshotQueryResult>(result, static (sb, qr) =>
        {
            sb.AppendLine();
            sb.AppendLine(JsonSerializer.Serialize(qr, QueryJsonOptions));
        });
    }

    /// <summary>
    /// Re-opens the snapshot origin via <see cref="IFrameVariableResolver"/> (ClrMD) and renders the
    /// object-typed locals/parameters of the specified managed thread. Requires <c>--thread-id</c>.
    /// </summary>
    private static async Task<CliCommandResult> QueryFrameVarsAsync(
        IServiceProvider services, CliOptions options, ThreadSnapshotArtifact snapshot, CancellationToken cancellationToken)
    {
        if (options.ThreadId is null)
        {
            return Fail(
                "--thread-id (ManagedThreadId) is required for view 'frame-vars'.",
                "InvalidArgument",
                "Obtain the ManagedThreadId from view='threads-summary', then re-run: query --handle <id> --view frame-vars --thread-id <id>.");
        }

        // Guard against PID reuse / drift: the requested thread must have been present in the
        // captured snapshot, otherwise we'd resolve frames from whatever now owns that PID.
        if (!snapshot.Threads.Any(t => t.ManagedThreadId == options.ThreadId.Value))
        {
            return Fail(
                $"Managed thread {options.ThreadId.Value} was not present in the captured snapshot; re-capture before inspecting frame variables.",
                "ThreadNotInSnapshot",
                "Use view='threads-summary' to list the ManagedThreadIds actually captured in this snapshot.");
        }

        var resolver = services.GetRequiredService<IFrameVariableResolver>();
        FrameVariablesResult frameVars;
        try
        {
            frameVars = await resolver.ResolveAsync(
                snapshot, options.ThreadId.Value, includeSensitiveValues: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"frame-vars: {ex.Message}", "FrameVarsFailed",
                "Frame variable resolution failed. Ensure the target process is still running (live origin) or the dump file is accessible (dump origin). Value-type locals are not enumerable via ClrMD.");
        }

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"frame-vars: {frameVars.Frames.Count} frame(s) for managed thread {frameVars.ManagedThreadId} (OS tid {frameVars.OSThreadId}).");
        var ok = DiagnosticResult.Ok(frameVars, summary);
        return BuildResult<FrameVariablesResult>(ok, static (sb, r) =>
        {
            sb.AppendLine();
            sb.AppendLine(JsonSerializer.Serialize(r, QueryJsonOptions));
        });
    }

    /// <summary>
    /// Renders an off-CPU drill-down inside the <c>session</c> REPL via the host-neutral
    /// <see cref="OffCpuQueryDispatcher"/>. Because the server's original switch silently treats an
    /// unknown view as <c>topStacks</c>, this helper validates the view name up front and returns a
    /// clear <c>InvalidArgument</c> for the CLI operator (a host-specific UX choice; the shared
    /// dispatcher keeps the server's fall-through behavior).
    /// </summary>
    private static CliCommandResult QueryOffCpuSession(CliOptions options, object artifact)
    {
        if (artifact is not OffCpuSnapshotArtifact snapshot)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as an off-CPU snapshot.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run the originating collect command to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? "topStacks" : options.View;
        var normalized = view.Trim().ToLowerInvariant();
        if (normalized is not ("topstacks" or "bythread" or "stack"))
        {
            return Fail($"query: unknown view '{view}' for an off-CPU snapshot.", "InvalidArgument",
                $"Valid views: {string.Join(", ", OffCpuQueryDispatcher.SessionViews)}.");
        }

        var topN = options.TopTypes ?? 25;
        var result = OffCpuQueryDispatcher.Dispatch(snapshot, view, topN, options.StackRank);

        return BuildResult<OffCpuQueryView>(result, static (sb, qr) =>
        {
            sb.AppendLine();
            sb.AppendLine(JsonSerializer.Serialize(qr, QueryJsonOptions));
        });
    }

    private static CliCommandResult Fail(string summary, string errorKind, string detail)
    {
        var result = DiagnosticResult.Fail<object>(
            summary,
            new DiagnosticError(errorKind, summary, detail));
        return BuildResult<object>(result, static (_, _) => { });
    }

}
