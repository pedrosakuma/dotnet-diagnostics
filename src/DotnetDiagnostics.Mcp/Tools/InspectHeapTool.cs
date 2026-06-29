using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Tools.Dispatch;
using DotnetDiagnostics.Mcp.Diagnostics;
using DotnetDiagnostics.Mcp.Security;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>
/// #206 — unified <c>inspect_heap</c> tool that merges the legacy
/// <c>inspect_dump</c> + <c>inspect_live_heap</c> pair behind a single
/// <c>source=live|dump</c> discriminator. Both backends already produce the same
/// <see cref="HeapSnapshotArtifact"/> consumed by <c>query_heap_snapshot</c> — this tool
/// is the public consolidation of that "split collector, unified drilldown" pattern.
/// </summary>
/// <remarks>
/// <para>The implementation is a thin dispatcher: after validating the discriminator and the
/// mutual-exclusion contract between <c>processId</c> and <c>dumpFilePath</c>, it delegates
/// to the existing static methods on <see cref="DiagnosticTools"/>. Delegation preserves
/// every security gate the underlying collectors enforce — symbol-server SSRF, ptrace error
/// translation, scope-stamped audit emission.</para>
/// <para>Lives in its own class with its own <c>[McpServerToolType]</c> attribute, by design
/// of issue #206 — minimizes merge conflicts with parallel sub-issues editing
/// <c>DiagnosticTools.cs</c>.</para>
/// </remarks>
[McpServerToolType]
public sealed class InspectHeapTool
{
    internal const string ToolName = "inspect_heap";
    internal const string SourceLive = "live";
    internal const string SourceDump = "dump";
    internal const string SourceGcDump = "gcdump";

    private static readonly IReadOnlyList<string> AllowedSources = new[] { SourceLive, SourceDump, SourceGcDump };

    // Static gate is `heap-read` only — the minimum scope shared by both backends.
    // `source="live"` additionally requires `ptrace` at runtime (see below) so that
    // least-privilege tokens scoped to dump inspection alone are not denied by the
    // shared canonical entry point. `inspect_live_heap` still carries the static
    // `[RequireScope("heap-read", "ptrace")]` gate; the runtime check here mirrors
    // that semantic for the live branch of `inspect_heap`.
    [RequireScope("heap-read")]
    [McpServerTool(
        Name = ToolName,
        Title = "Inspect a managed heap (live process or dump file)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true,
        // Issue #425 — a ClrMD heap walk can be long (large heaps) and suspends a live target;
        // promote to an MCP Task so the client owns progress + cancellation uniformly with
        // collect_sample / collect_events. Optional: synchronous walks still work for older clients.
        TaskSupport = ToolTaskSupport.Optional)]
    [Description(
        "Walks the managed heap and returns aggregated runtime/heap totals plus top types by " +
        "retained bytes and instance count. Each TypeStat carries a TypeIdentity (ModuleVersionId + " +
        "MetadataToken) ready to hand off verbatim to dotnet-assembly-mcp's get_type. The " +
        "`source` discriminator selects the backend: " +
        "`source=\"live\"` attaches to a live .NET process via ClrMD (requires same UID as the " +
        "target plus CAP_SYS_PTRACE on Linux; the target is suspended for the duration of the walk) " +
        "and requires `processId` (auto-resolved when only one .NET process is reachable); " +
        "`source=\"dump\"` walks a previously-captured WithHeap/Full dump offline (read-only, no " +
        "ptrace) and requires `dumpFilePath`. Mini and Triage dumps return runtime metadata only. " +
        "`source=\"gcdump\"` triggers an induced GC heap snapshot over EventPipe (the dotnet-gcdump " +
        "mechanism — production-safe: no ptrace, no ClrMD attach, no dump file) and targets a live " +
        "`processId` (auto-resolved); it returns per-type byte/instance totals but ClrMD-only views " +
        "(GC handles, static fields, delegate targets, segment layout) stay empty. " +
        "Live and dump invocations both produce the same `HeapSnapshotArtifact`, addressable via " +
        "`query_heap_snapshot(handle, view, …)` for retention paths, static-field roots, task/timer leak candidates, AssemblyLoadContext leak candidates, GCHandle table aggregation, finalizer " +
        "queue and other drilldown views without re-walking. Live-origin handles are evicted when " +
        "the target PID exits; dump-origin handles are retained until their TTL elapses. Supersedes " +
        "the deprecated `inspect_dump` and `inspect_live_heap` tools (#206).")]
    public static async Task<DiagnosticResult<object>> InspectHeap(
        IDumpInspector inspector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        IGcDumpHeapSnapshotCollector gcDumpCollector,
        [Description("Backend discriminator. `live` attaches to a running .NET process via ClrMD (requires CAP_SYS_PTRACE on Linux); `dump` walks a previously-captured dump file offline; `gcdump` captures a GC heap snapshot over EventPipe (no ptrace, no dump file — production-safe).")] string source,
        [Description("Operating system process id of the target .NET process. Required for `source=\"live\"` (auto-resolved when only one .NET process is reachable); forbidden for `source=\"dump\"`.")] int? processId = null,
        [Description("Absolute path to a previously-captured .dmp file. Required for `source=\"dump\"`; forbidden for `source=\"live\"`.")] string? dumpFilePath = null,
        [Description("Number of types to return in each top-N list (bytes / instances). Defaults to 20.")] int topTypes = 20,
        [Description("When true, walks a short GC retention chain for the top retained types. Off by default — slower; for `source=\"live\"` this also lengthens the suspend window.")] bool includeRetentionPaths = false,
        [Description("Cap on retention-chain depth when retention paths are enabled. Defaults to 8.")] int retentionPathLimit = 8,
        [Description("When true, enumerate every loaded type's static reference fields ranked by directly-referenced object size — surfaces 'singleton that grew forever' leaks. Off by default; adds an extra pass over AppDomains × Modules × Types.")] bool includeStaticFields = false,
        [Description("When true, detect MulticastDelegate instances during the heap walk and group their invocation list by (target type, method) — surfaces 'event handler never unsubscribed' leaks. Cheap (folded into the existing heap pass).")] bool includeDelegateTargets = false,
        [Description("When true, hash every System.String during the heap walk and rank by aggregate retained bytes — surfaces missing string-interning. Cheap (folded into the existing heap pass) but allocates one hash per unique string.")] bool includeDuplicateStrings = false,
        [Description("Optional NT_SYMBOL_PATH-style search path reserved for symbol-resolving heap drilldowns. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        [Description("`source=\"gcdump\"` only. If true, persists the raw GC heap-dump .nettrace under the artifact root and returns its relative path so it can be fetched with get_bytes(kind='trace') for offline analysis. Defaults to false; ignored by `live` and `dump`.")] bool exportTrace = false,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        RequestContext<CallToolRequestParams>? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (!DiscriminatorDispatch.TryValidate<object>(
                source, AllowedSources, nameof(source), out var canonical, out var discriminatorFailure))
        {
            return discriminatorFailure!;
        }

        var hasExplicitPid = processId.HasValue && processId.Value != 0;
        var hasDumpPath = !string.IsNullOrWhiteSpace(dumpFilePath);

        if (canonical == SourceLive)
        {
            if (hasDumpPath)
            {
                return InvalidArgument(nameof(dumpFilePath),
                    "source='live' forbids dumpFilePath. Drop dumpFilePath or set source='dump'.");
            }

            // The live backend attaches via ptrace and must keep that requirement even though
            // the canonical entry point's static gate is `heap-read` only (see class comment).
            // We use HasScope (which honours the wildcard / root principal) so root tokens still
            // work locally; dedicated bearers must hold the literal `ptrace` scope.
            var principal = principalAccessor.Current;
            if (principal is not null && !principal.HasScope("ptrace"))
            {
                return DiagnosticResult.Fail<object>(
                    $"forbidden: tool '{ToolName}' with source='live' requires scope 'ptrace'.",
                    new DiagnosticError(
                        "Forbidden",
                        $"tool '{ToolName}' with source='live' requires scope 'ptrace'.",
                        "ptrace"),
                    new NextActionHint(ToolName,
                        "Either retry with source='dump' (offline; needs only 'heap-read'), or have the operator issue a bearer that also grants 'ptrace'.",
                        new Dictionary<string, object?>
                        {
                            ["source"] = SourceDump,
                        }));
            }
        }
        else if (canonical == SourceGcDump)
        {
            // gcdump uses EventPipe (no ptrace, no dump file) — needs only heap-read, like dump,
            // but targets a live PID like live. Auto-resolved when only one .NET process is visible.
            if (hasDumpPath)
            {
                return InvalidArgument(nameof(dumpFilePath),
                    "source='gcdump' forbids dumpFilePath — it captures from a live process over EventPipe. Drop dumpFilePath.");
            }
        }
        else
        {
            // source = "dump"
            if (hasExplicitPid)
            {
                return InvalidArgument(nameof(processId),
                    "source='dump' forbids processId. Drop processId or set source='live'.");
            }
            if (!hasDumpPath)
            {
                return InvalidArgument(nameof(dumpFilePath),
                    "source='dump' requires dumpFilePath.");
            }
        }

        // Issue #425 — a heap walk has no a-priori duration, so emit an indeterminate progress
        // heartbeat and honour MCP-native cancellation (notifications/cancelled) uniformly with the
        // other long-running collectors. The walk can be promoted to an MCP Task (TaskSupport=Optional).
        try
        {
            return await CollectionProgressTicker.RunIndeterminateAsync(
                requestContext,
                $"inspect_heap:{canonical}",
                TimeSpan.FromSeconds(1),
                async ct =>
                {
                    if (canonical == SourceLive)
                    {
                        var live = await DiagnosticTools.InspectLiveHeap(
                            inspector,
                            handles,
                            resolver,
                            symbolServerAllowlist,
                            principalAccessor,
                            processId,
                            topTypes,
                            includeRetentionPaths,
                            retentionPathLimit,
                            includeStaticFields,
                            includeDelegateTargets,
                            includeDuplicateStrings,
                            symbolPath,
                            deprecation,
                            ct).ConfigureAwait(false);
                        return AsObjectEnvelope(live);
                    }

                    if (canonical == SourceGcDump)
                    {
                        var gc = await DiagnosticTools.InspectGcDump(
                            gcDumpCollector,
                            handles,
                            resolver,
                            processId,
                            topTypes,
                            timeout: null,
                            exportTrace,
                            ct).ConfigureAwait(false);
                        return AsObjectEnvelope(gc);
                    }

                    var dump = await DiagnosticTools.InspectDump(
                        inspector,
                        handles,
                        symbolServerAllowlist,
                        principalAccessor,
                        dumpFilePath!,
                        topTypes,
                        includeRetentionPaths,
                        retentionPathLimit,
                        includeStaticFields,
                        includeDelegateTargets,
                        includeDuplicateStrings,
                        symbolPath,
                        deprecation,
                        ct).ConfigureAwait(false);
                    return AsObjectEnvelope(dump);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DiagnosticResult<object>(
                $"inspect_heap(source='{canonical}') cancelled by the client before the heap walk completed. " +
                "No snapshot was retained — restart the inspection to capture data.",
                Array.Empty<NextActionHint>())
            {
                Cancelled = true,
            };
        }
    }

    /// <summary>
    /// Projects a typed <see cref="DiagnosticResult{T}"/> into the polymorphic
    /// <c>DiagnosticResult&lt;object&gt;</c> shape <c>inspect_heap</c> exposes, preserving every
    /// envelope field (summary, hints, error, handle, expiration, resolved process). The
    /// runtime type of <c>Data</c> is the legacy payload (<see cref="LiveHeapInspection"/> or
    /// <see cref="DumpInspection"/>) — System.Text.Json serializes polymorphically on
    /// <c>object</c> properties (default since .NET 6), so the resulting JSON is byte-equal
    /// to the legacy envelope. This is what makes the compatibility-envelope assertion in
    /// <c>InspectHeapCompatibilityTests</c> pass.
    /// </summary>
    private static DiagnosticResult<object> AsObjectEnvelope<T>(DiagnosticResult<T> source) where T : class
    {
        return new DiagnosticResult<object>(source.Summary, source.Hints, source.Error)
        {
            Data = source.Data,
            Handle = source.Handle,
            HandleExpiresAt = source.HandleExpiresAt,
            ResolvedProcess = source.ResolvedProcess,
        };
    }

    private static DiagnosticResult<object> InvalidArgument(string parameterName, string requirement)
    {
        var message = $"Argument '{parameterName}' {requirement}";
        return DiagnosticResult.Fail<object>(
            message,
            new DiagnosticError("InvalidArgument", message, parameterName),
            new NextActionHint(ToolName,
                "Re-issue inspect_heap with valid arguments. `source='live'` requires processId (or auto-select) and forbids dumpFilePath; `source='dump'` requires dumpFilePath and forbids processId.",
                new Dictionary<string, object?>
                {
                    ["source"] = SourceLive + "|" + SourceDump,
                }));
    }
}
