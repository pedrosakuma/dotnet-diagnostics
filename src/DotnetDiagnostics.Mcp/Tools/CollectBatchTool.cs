using System.ComponentModel;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.GatedCapture;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.Tools.Dispatch;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>
/// Fans out several <see cref="CollectSampleTool"/>/<see cref="CollectEventsTool"/> kinds
/// concurrently against the *same resolved process*, for the *same shared duration window*, in a
/// single call (issue #665 Part C). Exists to eliminate the process-exit race of issuing those
/// kinds as separate sequential calls against a short-lived process (test hosts, CLI batch jobs,
/// anything that may have exited by the time a second round-trip starts).
/// </summary>
/// <remarks>
/// <para>Deliberately a new, 17th tool rather than a bolt-on parameter on either existing tool —
/// see "Rejected alternatives" in
/// <c>docs/design/ephemeral-process-capture-design.md</c> Part C for why <c>alsoCollect</c> and
/// <c>kind="batch"</c> were rejected.</para>
/// <para>Each entry is dispatched by calling that kind's own existing, unmodified
/// <see cref="CollectSampleTool.CollectSample"/>/<see cref="CollectEventsTool.CollectEvents"/>
/// entry point directly — not a reimplementation — so every entry's <c>Data</c> shape is
/// byte-for-byte identical to calling that kind directly. That direct in-process call bypasses
/// only the outer <c>[McpServerTool]</c> authorization-filter boundary (a plain C# static call
/// never goes through the MCP SDK's attribute-based filter); this tool's own
/// <see cref="RequireAnyScopeAttribute"/> plus the explicit per-entry
/// <see cref="ToolDispatchGuards.RequireScope{TResult}"/> loop below re-establish the exact same
/// authorization boundary before any session opens, mirroring the precedent
/// <see cref="CollectEventsTool"/> already established for its own per-kind dispatch
/// (<c>CollectEventsTool.cs:238</c>).</para>
/// </remarks>
[McpServerToolType]
public sealed class CollectBatchTool
{
    internal const string ToolName = "collect_batch";
    internal const string ToolCollectSample = "collect_sample";
    internal const string ToolCollectEvents = "collect_events";
    private const string KindSweep = "sweep";

    /// <summary>
    /// The single primary scope every <c>collect_sample</c> kind eligible for batching requires
    /// (mirrors <see cref="CollectSampleTool"/>'s own outer <c>[RequireScope("eventpipe")]</c>).
    /// <c>method-params</c> is excluded from batching entirely (see <see cref="ValidateEntries"/>),
    /// so this is the only scope any collect_sample entry can ever need here.
    /// </summary>
    private const string CollectSampleRequiredScope = "eventpipe";

    internal static readonly IReadOnlyList<string> AllowedTools = new[] { ToolCollectSample, ToolCollectEvents };

    /// <summary>Bound on concurrent EventPipe/ETW sessions a single call may open against one
    /// process (resource-boundedness discipline, docs/resource-boundedness.md).</summary>
    internal const int MaxEntries = 4;

    [RequireAnyScope("read-counters", "eventpipe")]
    [McpServerTool(
        Name = ToolName,
        Title = "Run several bounded-time collectors in one call against one process",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Runs several collect_sample/collect_events kinds concurrently, against the same resolved " +
        "process, for the same shared duration window, inside a single call — eliminates the " +
        "process-exit race of issuing them as separate calls (short-lived test hosts, CLI batch " +
        "jobs). Each requested entry's response Data has exactly the same shape it would have if " +
        "called directly via collect_sample/collect_events (see docs/tool-reference.md for each " +
        "kind's payload) and gets its own independent query_snapshot-compatible handle where " +
        "applicable. kind='method-params' is not eligible for batching (security-sensitive; call " +
        "collect_sample directly for it).")]
    public static async Task<DiagnosticResult<CollectBatchReport>> CollectBatch(
        // DI services — the union of every dependency CollectSampleTool/CollectEventsTool take,
        // since each entry is dispatched by calling those tools' existing entry points directly.
        ICpuSampler cpuSampler,
        IOffCpuSampler offCpuSampler,
        EventPipeAllocationSampler allocationSampler,
        INativeAllocSampler nativeAllocSampler,
        IMethodParameterCaptureCollector methodParameterCollector,
        ICounterCollector counterCollector,
        IExceptionCollector exceptionCollector,
        ICrashGuardCollector crashGuardCollector,
        IGcCollector gcCollector,
        IGcDatasCollector gcDatasCollector,
        IActivityCollector activityCollector,
        IEventSourceCollector eventSourceCollector,
        IEventCatalogCollector eventCatalogCollector,
        ILogCollector logCollector,
        IJitCollector jitCollector,
        IThreadPoolCollector threadPoolCollector,
        IContentionCollector contentionCollector,
        IDbCollector dbCollector,
        IKestrelCollector kestrelCollector,
        INetworkingCollector networkingCollector,
        IInFlightRequestCollector inFlightRequestCollector,
        IStartupCollector startupCollector,
        IProcessResourcesCollector processResourcesCollector,
        IThresholdGatedCaptureCollector gatedCaptureCollector,
        IThreadSnapshotInspector threadSnapshotInspector,
        IDumpInspector dumpInspector,
        IProcessDumper processDumper,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        SecurityOptions securityOptions,
        EventSourceAllowlist allowlist,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory,
        [Description(
            "Which collectors to run, each naming an existing collect_sample/collect_events kind. " +
            "Between 1 and 4 entries. Duplicate {tool, kind} pairs and kind='method-params' are " +
            "rejected (security-sensitive; stays a single-purpose collect_sample call).")]
        IReadOnlyList<CollectBatchRequest>? requests,
        [Description("Operating system process id of the target .NET process. Resolved once and shared " +
            "by every requested entry (auto-selects the lone visible .NET process when omitted).")]
        int? processId = null,
        [Description("Shared duration of the collection window in seconds for every requested entry. " +
            "Must be >= 1. Defaults to 10. Individual entries cannot override this in v1 — call the " +
            "specific tool directly if one kind genuinely needs a different window.")]
        int durationSeconds = 10,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1)
        {
            return DiagnosticResult.Fail<CollectBatchReport>(
                "'durationSeconds' must be >= 1.",
                new DiagnosticError("InvalidArgument", "'durationSeconds' must be >= 1.", nameof(durationSeconds)));
        }

        if (!ValidateEntries(requests, out var canonicalEntries, out var validationFailure))
        {
            return validationFailure!;
        }

        // Pre-authorize every entry before opening any session — fail the whole call on the first
        // unauthorized entry (no partial start). This is the correctness gap the code review caught
        // in the rejected alsoCollect draft; it applies identically to a new tool.
        var principal = principalAccessor.Current;
        foreach (var entry in canonicalEntries)
        {
            var requiredScope = entry.Tool == ToolCollectSample
                ? CollectSampleRequiredScope
                : CollectEventsTool.GetRequiredScope(entry.Kind) ?? CollectSampleRequiredScope;

            if (!ToolDispatchGuards.RequireScope(
                    principal,
                    requiredScope,
                    () => $"{entry.Tool}(kind='{entry.Kind}') requires the '{requiredScope}' scope. " +
                          "collect_batch pre-authorizes every requested entry before opening any session.",
                    out DiagnosticResult<CollectBatchReport>? scopeFailure,
                    errorKind: "InsufficientScope"))
            {
                return scopeFailure!;
            }
        }

        // Resolve once so every entry targets the exact same pid even if the visible-process set
        // changes mid-call, and so ambiguity/nothing-visible surfaces exactly once.
        var resolved = await ProcessResolutionHelpers.ResolveContextAsync<CollectBatchReport>(
            resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        async Task<CollectBatchEntryResult> RunEntryAsync(string tool, string kind, CancellationToken ct)
        {
            try
            {
                if (tool == ToolCollectSample)
                {
                    var sampleResult = await CollectSampleTool.CollectSample(
                        cpuSampler,
                        offCpuSampler,
                        allocationSampler,
                        nativeAllocSampler,
                        methodParameterCollector,
                        handles,
                        resolver,
                        symbolServerAllowlist,
                        securityOptions,
                        principalAccessor,
                        loggerFactory,
                        kind: kind,
                        processId: pid,
                        durationSeconds: durationSeconds,
                        cancellationToken: ct).ConfigureAwait(false);
                    return Project(tool, kind, sampleResult);
                }

                var eventsResult = await CollectEventsTool.CollectEvents(
                    counterCollector,
                    exceptionCollector,
                    crashGuardCollector,
                    gcCollector,
                    gcDatasCollector,
                    activityCollector,
                    eventSourceCollector,
                    eventCatalogCollector,
                    logCollector,
                    jitCollector,
                    threadPoolCollector,
                    contentionCollector,
                    dbCollector,
                    kestrelCollector,
                    networkingCollector,
                    inFlightRequestCollector,
                    startupCollector,
                    processResourcesCollector,
                    gatedCaptureCollector,
                    cpuSampler,
                    threadSnapshotInspector,
                    dumpInspector,
                    processDumper,
                    resolver,
                    handles,
                    allowlist,
                    sensitiveGate,
                    principalAccessor,
                    kind: kind,
                    processId: pid,
                    durationSeconds: durationSeconds,
                    cancellationToken: ct).ConfigureAwait(false);
                return Project(tool, kind, eventsResult);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A single entry throwing (e.g. EventPipe session-start TimeoutException) must not
                // fault Task.WhenAll and discard the siblings' results.
                return new CollectBatchEntryResult(
                    tool,
                    kind,
                    $"{tool}(kind='{kind}') failed: {ex.Message}",
                    Data: null,
                    Handle: null,
                    HandleExpiresAt: null,
                    new DiagnosticError("CollectorFailed", ex.Message));
            }
        }

        var tasks = canonicalEntries
            .Select(entry => RunEntryAsync(entry.Tool, entry.Kind, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var report = new CollectBatchReport(pid, durationSeconds, results);
        var failureCount = results.Count(static r => r.Error is not null);
        var summary = failureCount == 0
            ? $"Batch over {durationSeconds}s against pid {pid}: {results.Length} entr{(results.Length == 1 ? "y" : "ies")} collected."
            : $"Batch over {durationSeconds}s against pid {pid}: {results.Length} entr{(results.Length == 1 ? "y" : "ies")} requested, {failureCount} failed.";

        return DiagnosticResult.Ok(report, summary);
    }

    /// <summary>
    /// Validates request shape before any session opens: no null entries, 1–<see cref="MaxEntries"/>
    /// entries, no duplicate <c>{tool, kind}</c> pairs, no <c>kind="method-params"</c> or
    /// <c>kind="sweep"</c>, and every <c>{tool, kind}</c> pair exists in that tool's own
    /// <c>AllowedKinds</c> — reusing <see cref="DiscriminatorDispatch"/>'s existing validation
    /// helper rather than inventing a second one.
    /// </summary>
    private static bool ValidateEntries(
        IReadOnlyList<CollectBatchRequest>? requests,
        out IReadOnlyList<(string Tool, string Kind)> canonicalEntries,
        out DiagnosticResult<CollectBatchReport>? failure)
    {
        canonicalEntries = Array.Empty<(string, string)>();

        if (requests is null || requests.Count == 0)
        {
            failure = DiagnosticResult.Fail<CollectBatchReport>(
                "'requests' must contain between 1 and 4 entries.",
                new DiagnosticError("InvalidArgument", "'requests' must contain between 1 and 4 entries.", nameof(requests)));
            return false;
        }

        if (requests.Count > MaxEntries)
        {
            failure = DiagnosticResult.Fail<CollectBatchReport>(
                $"'requests' must contain at most {MaxEntries} entries; got {requests.Count}.",
                new DiagnosticError("InvalidArgument", $"'requests' must contain at most {MaxEntries} entries; got {requests.Count}.", nameof(requests)));
            return false;
        }

        var entries = new List<(string Tool, string Kind)>(requests.Count);
        var seen = new HashSet<(string Tool, string Kind)>();

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var parameterPrefix = $"requests[{i}]";

            if (request is null)
            {
                failure = DiagnosticResult.Fail<CollectBatchReport>(
                    $"{parameterPrefix} must not be null.",
                    new DiagnosticError("InvalidArgument", $"{parameterPrefix} must not be null.", parameterPrefix));
                return false;
            }

            if (!DiscriminatorDispatch.TryValidate<CollectBatchReport>(
                    request.Tool, AllowedTools, $"{parameterPrefix}.tool", out var canonicalTool, out var toolFailure))
            {
                failure = toolFailure;
                return false;
            }

            var allowedKinds = canonicalTool == ToolCollectSample
                ? CollectSampleTool.AllowedKinds
                : CollectEventsTool.AllowedKinds;

            if (!DiscriminatorDispatch.TryValidate<CollectBatchReport>(
                    request.Kind, allowedKinds, $"{parameterPrefix}.kind", out var canonicalKind, out var kindFailure))
            {
                failure = kindFailure;
                return false;
            }

            if (canonicalTool == ToolCollectSample && canonicalKind == CollectSampleTool.KindMethodParams)
            {
                failure = DiagnosticResult.Fail<CollectBatchReport>(
                    $"{parameterPrefix}: kind='method-params' is not eligible for collect_batch (security-sensitive; call collect_sample directly for it).",
                    new DiagnosticError(
                        "InvalidArgument",
                        $"{parameterPrefix}: kind='method-params' is not eligible for collect_batch (security-sensitive; call collect_sample directly for it).",
                        $"{parameterPrefix}.kind"));
                return false;
            }

            if (canonicalTool == ToolCollectEvents && canonicalKind == KindSweep)
            {
                // sweep is itself a nested fan-out (SweepUseCase opens 4 concurrent EventPipe
                // sessions and enforces its own SweepUseCase.MinimumDurationSeconds floor), which
                // would silently break collect_batch's "one shared duration, <=MaxEntries sessions"
                // guarantees if allowed as an entry. Callers wanting a sweep should call
                // collect_events(kind="sweep") directly.
                failure = DiagnosticResult.Fail<CollectBatchReport>(
                    $"{parameterPrefix}: kind='sweep' is not eligible for collect_batch (it is itself a multi-session fan-out with its own duration floor; call collect_events(kind=\"sweep\") directly).",
                    new DiagnosticError(
                        "InvalidArgument",
                        $"{parameterPrefix}: kind='sweep' is not eligible for collect_batch (it is itself a multi-session fan-out with its own duration floor; call collect_events(kind=\"sweep\") directly).",
                        $"{parameterPrefix}.kind"));
                return false;
            }

            var pair = (canonicalTool, canonicalKind);
            if (!seen.Add(pair))
            {
                failure = DiagnosticResult.Fail<CollectBatchReport>(
                    $"{parameterPrefix}: duplicate entry for {canonicalTool}(kind='{canonicalKind}').",
                    new DiagnosticError(
                        "InvalidArgument",
                        $"{parameterPrefix}: duplicate entry for {canonicalTool}(kind='{canonicalKind}').",
                        $"{parameterPrefix}"));
                return false;
            }

            entries.Add(pair);
        }

        canonicalEntries = entries;
        failure = null;
        return true;
    }

    private static CollectBatchEntryResult Project<T>(string tool, string kind, DiagnosticResult<T> result)
        where T : class
    {
        var element = result.Data is not null
            ? JsonSerializer.SerializeToElement(result.Data, McpJsonUtilities.DefaultOptions)
            : (JsonElement?)null;
        return new CollectBatchEntryResult(tool, kind, result.Summary, element, result.Handle, result.HandleExpiresAt, result.Error);
    }
}

/// <param name="Tool">"collect_sample" or "collect_events".</param>
/// <param name="Kind">One of that tool's existing AllowedKinds (validated by reusing
/// CollectSampleTool.AllowedKinds / CollectEventsTool.AllowedKinds directly — no separate list to
/// drift out of sync). kind="method-params" is rejected (security-sensitive).</param>
public sealed record CollectBatchRequest(string Tool, string Kind);

/// <param name="ProcessId">The single resolved pid every entry ran against.</param>
/// <param name="DurationSeconds">The shared window every entry used.</param>
/// <param name="Results">One entry per requested {tool, kind}, in request order.</param>
public sealed record CollectBatchReport(
    int ProcessId,
    int DurationSeconds,
    IReadOnlyList<CollectBatchEntryResult> Results);

/// <param name="Tool">Echoes the request's Tool.</param>
/// <param name="Kind">Echoes the request's Kind.</param>
/// <param name="Summary">That entry's own DiagnosticResult&lt;T&gt;.Summary.</param>
/// <param name="Data">That entry's own DiagnosticResult&lt;T&gt;.Data, serialized generically —
/// heterogeneous per-kind payload types (CollectSampleEnvelope, CollectEventsEnvelope) can't share
/// one static C# type, so this is JsonElement rather than a typed field, serialized with the same
/// ModelContextProtocol.McpJsonUtilities.DefaultOptions the MCP SDK itself uses for structured
/// content — so the shape matches a direct call byte-for-byte. Every kind's shape is still fully
/// documented at docs/tool-reference.md for that kind; it just isn't statically declared here.
/// Null when this entry failed.</param>
/// <param name="Handle">That entry's own IDiagnosticHandleStore handle, if any — pass this to
/// query_snapshot exactly as if the entry's kind had been collected by a standalone call.</param>
/// <param name="HandleExpiresAt">Mirrors DiagnosticResult&lt;T&gt;.HandleExpiresAt for this entry.</param>
/// <param name="Error">Populated instead of Data/Handle when this one entry failed; other entries
/// are unaffected — a collect_batch call never fails outright just because one entry's target
/// exited mid-window.</param>
public sealed record CollectBatchEntryResult(
    string Tool,
    string Kind,
    string Summary,
    JsonElement? Data,
    string? Handle,
    DateTimeOffset? HandleExpiresAt,
    DiagnosticError? Error);
