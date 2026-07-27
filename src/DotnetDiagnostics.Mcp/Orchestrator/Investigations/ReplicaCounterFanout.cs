using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.ReplicaCounters;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Orchestrator fan-out for cross-replica counter skew (Wave B2, issue #448). Enumerates the
/// caller's <see cref="InvestigationState.Active"/> investigation handles, runs a bounded
/// <c>collect_events(kind="counters")</c> against each attached Pod through the investigation
/// proxy <em>simultaneously</em>, parses each per-Pod <see cref="CounterSnapshot"/>, then hands
/// the readings to the pure <see cref="ReplicaCounterSkewAnalyzer"/> to surface the outlier replica.
/// </summary>
/// <remarks>
/// Mirrors <see cref="DistributedTraceCorrelator"/>: one bounded fan-out per call, no server-side
/// persistence and no daemon. Per-Pod failures are isolated (one bad replica does not sink the
/// comparison); they surface as <see cref="FanoutResult.PodErrors"/>. Distinct from
/// <c>compare_to_baseline</c>, which contrasts pre-collected serial snapshots — this is live and
/// simultaneous.
/// </remarks>
internal static class ReplicaCounterFanout
{
    private static readonly TimeSpan DefaultSelectorResolutionTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal sealed record FanoutResult(
        ReplicaCounterSkew? Skew,
        int AttachedActivePods,
        IReadOnlyList<string> PodErrors);

    private sealed record ProcessResolution(
        InvestigationHandle Handle,
        bool Succeeded,
        int? ProcessId,
        string Failure);

    internal static Task<FanoutResult> CompareAsync(
        IInvestigationStore store,
        IInvestigationProxyClient proxy,
        BearerPrincipal? callerPrincipal,
        IReadOnlyList<string>? investigationHandleIds,
        int durationSeconds,
        int intervalSeconds,
        CancellationToken cancellationToken)
        => CompareAsync(
            store,
            proxy,
            callerPrincipal,
            investigationHandleIds,
            durationSeconds,
            intervalSeconds,
            DefaultSelectorResolutionTimeout,
            cancellationToken);

    internal static async Task<FanoutResult> CompareAsync(
        IInvestigationStore store,
        IInvestigationProxyClient proxy,
        BearerPrincipal? callerPrincipal,
        IReadOnlyList<string>? investigationHandleIds,
        int durationSeconds,
        int intervalSeconds,
        TimeSpan selectorResolutionTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(selectorResolutionTimeout, TimeSpan.Zero);

        var errors = new List<string>();
        var handles = ResolveHandles(store, callerPrincipal, investigationHandleIds, errors);
        var readings = new List<ReplicaCounterReading>(handles.Length);
        var arguments = BuildCountersArguments(durationSeconds, intervalSeconds);

        // Phase 1 resolves every transport-neutral selector concurrently. No collection starts
        // until every resolution has completed or failed, otherwise slow Pod-local discovery
        // shifts that replica's EventPipe window later than its peers.
        var resolutionTasks = handles
            .Select(handle => ResolveAsync(proxy, handle, selectorResolutionTimeout, cancellationToken))
            .ToArray();
        var resolutions = await Task.WhenAll(resolutionTasks).ConfigureAwait(false);

        var resolved = new List<ProcessResolution>(resolutions.Length);
        foreach (var resolution in resolutions)
        {
            if (!resolution.Succeeded)
            {
                errors.Add($"Pod '{resolution.Handle.PodName}' (handle {resolution.Handle.HandleId}): {resolution.Failure}");
                continue;
            }

            resolved.Add(resolution);
        }

        // Phase 2 is the common barrier: only after all selector lookups finish do we create every
        // collection task. Its deadline starts after resolution so one hung selector cannot consume
        // the requested EventPipe window for healthy replicas. Task.WhenAll preserves ordering.
        var collectionTimeoutSeconds = durationSeconds + 30;
        var collectionDeadline = CreateDeadline(TimeSpan.FromSeconds(collectionTimeoutSeconds));
        var collectionTasks = resolved
            .Select(resolution => CollectAsync(
                proxy,
                resolution,
                arguments,
                collectionDeadline,
                collectionTimeoutSeconds,
                cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(collectionTasks).ConfigureAwait(false);

        foreach (var (handle, snapshot, failure) in results)
        {
            if (snapshot is null)
            {
                errors.Add($"Pod '{handle.PodName}' (handle {handle.HandleId}): {failure}");
                continue;
            }

            readings.Add(ReplicaCounterSkewAnalyzer.Project(handle.PodName, snapshot));
        }

        if (readings.Count == 0)
        {
            return new FanoutResult(null, handles.Length, errors);
        }

        var skew = ReplicaCounterSkewAnalyzer.Analyze(readings);
        return new FanoutResult(skew, handles.Length, errors);
    }

    private static async Task<ProcessResolution> ResolveAsync(
        IInvestigationProxyClient proxy,
        InvestigationHandle handle,
        TimeSpan selectorResolutionTimeout,
        CancellationToken cancellationToken)
    {
        if (handle.ProcessSelector is null)
        {
            return new ProcessResolution(handle, true, null, string.Empty);
        }

        using var perPodCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perPodCts.CancelAfter(selectorResolutionTimeout);
        try
        {
            var (processId, failure) = await ResolveProcessIdAsync(
                proxy,
                handle,
                handle.ProcessSelector,
                perPodCts.Token).ConfigureAwait(false);
            return processId is null
                ? new ProcessResolution(handle, false, null, failure)
                : new ProcessResolution(handle, true, processId, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProcessResolution(
                handle,
                false,
                null,
                $"process selection timed out after {selectorResolutionTimeout.TotalSeconds:0.###}s");
        }
        catch (Exception ex)
        {
            return new ProcessResolution(handle, false, null, ex.Message);
        }
    }

    private static async Task<(InvestigationHandle Handle, CounterSnapshot? Snapshot, string Failure)> CollectAsync(
        IInvestigationProxyClient proxy,
        ProcessResolution resolution,
        Dictionary<string, JsonElement> arguments,
        long deadline,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var handle = resolution.Handle;
        // The proxy transport sets an infinite HttpClient timeout, so a single stuck port-forward
        // would hang Task.WhenAll forever. Bound each pod to its collection window + slack and turn
        // a hung pod into a per-pod error, never a fan-out-wide hang. Caller cancellation still wins.
        var remaining = GetRemaining(deadline);
        if (remaining <= TimeSpan.Zero)
        {
            return (handle, null, $"timed out after {timeoutSeconds}s");
        }

        using var perPodCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perPodCts.CancelAfter(remaining);
        try
        {
            var countersArguments = arguments;
            if (resolution.ProcessId is { } processId)
            {
                countersArguments = new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal)
                {
                    ["processId"] = JsonSerializer.SerializeToElement(processId),
                };
            }

            var request = new CallToolRequestParams { Name = "collect_events", Arguments = countersArguments };
            var result = await proxy.CallToolAsync(handle, request, perPodCts.Token).ConfigureAwait(false);
            var snapshot = TryExtractSnapshot(result, out var failure);
            return (handle, snapshot, failure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (handle, null, $"timed out after {timeoutSeconds}s");
        }
        catch (Exception ex)
        {
            return (handle, null, ex.Message);
        }
    }

    private static long CreateDeadline(TimeSpan timeout)
        => Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

    private static TimeSpan GetRemaining(long deadline)
    {
        var remainingTicks = deadline - Stopwatch.GetTimestamp();
        return remainingTicks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
    }

    private static async Task<(int? ProcessId, string Failure)> ResolveProcessIdAsync(
        IInvestigationProxyClient proxy,
        InvestigationHandle handle,
        InvestigationProcessSelector selector,
        CancellationToken cancellationToken)
    {
        var request = new CallToolRequestParams
        {
            Name = "inspect_process",
            Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["view"] = JsonSerializer.SerializeToElement("list"),
            },
        };
        var result = await proxy.CallToolAsync(handle, request, cancellationToken).ConfigureAwait(false);
        var processes = TryExtractProcesses(result, out var failure);
        if (processes is null)
        {
            return (null, failure);
        }

        var matches = processes.Where(selector.Matches).OrderBy(p => p.ProcessId).ToArray();
        if (matches.Length == 1)
        {
            return (matches[0].ProcessId, string.Empty);
        }

        var description = selector.Describe();
        if (matches.Length == 0)
        {
            return (null, $"process selector ({description}) matched no visible .NET process.");
        }

        return (null,
            $"process selector ({description}) is ambiguous; matched PIDs " +
            $"{string.Join(", ", matches.Select(p => p.ProcessId))}.");
    }

    private static Dictionary<string, JsonElement> BuildCountersArguments(int durationSeconds, int intervalSeconds)
        => new(StringComparer.Ordinal)
        {
            ["kind"] = JsonSerializer.SerializeToElement("counters"),
            ["durationSeconds"] = JsonSerializer.SerializeToElement(durationSeconds),
            ["intervalSeconds"] = JsonSerializer.SerializeToElement(intervalSeconds),
            ["depth"] = JsonSerializer.SerializeToElement("raw"),
        };

    private static CounterSnapshot? TryExtractSnapshot(CallToolResult result, out string failure)
    {
        var json = TryGetResultJson(result, "collect_events", out failure);
        if (json is null)
        {
            return null;
        }

        DiagnosticResult<CollectEventsEnvelope>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DiagnosticResult<CollectEventsEnvelope>>(json, DeserializeOptions);
        }
        catch (JsonException ex)
        {
            failure = $"could not parse pod-local collect_events response: {ex.Message}";
            return null;
        }

        if (envelope is null)
        {
            failure = "pod-local collect_events response deserialized to null.";
            return null;
        }

        if (envelope.Error is not null)
        {
            failure = $"pod-local collect_events failed: {envelope.Summary}";
            return null;
        }

        if (envelope.Data?.Counters is not { } snapshot)
        {
            failure = "pod-local collect_events(kind=counters) returned no counter snapshot.";
            return null;
        }

        failure = string.Empty;
        return snapshot;
    }

    private static IReadOnlyList<DotnetProcess>? TryExtractProcesses(
        CallToolResult result,
        out string failure)
    {
        var json = TryGetResultJson(result, "inspect_process", out failure);
        if (json is null)
        {
            return null;
        }

        DiagnosticResult<InspectProcessReport>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DiagnosticResult<InspectProcessReport>>(json, DeserializeOptions);
        }
        catch (JsonException ex)
        {
            failure = $"could not parse pod-local inspect_process response: {ex.Message}";
            return null;
        }

        if (envelope is null)
        {
            failure = "pod-local inspect_process response deserialized to null.";
            return null;
        }

        if (envelope.Error is not null)
        {
            failure = $"pod-local inspect_process failed: {envelope.Summary}";
            return null;
        }

        if (envelope.Data?.List is not { } processes)
        {
            failure = "pod-local inspect_process(view=list) returned no process list.";
            return null;
        }

        failure = string.Empty;
        return processes;
    }

    private static string? TryGetResultJson(
        CallToolResult result,
        string toolName,
        out string failure)
    {
        if (result.StructuredContent is { } structured)
        {
            failure = string.Empty;
            return structured.GetRawText();
        }

        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        if (text is null)
        {
            failure = $"pod-local {toolName} returned neither structured content nor a text block.";
            return null;
        }

        failure = string.Empty;
        return text.Text;
    }

    private static InvestigationHandle[] ResolveHandles(
        IInvestigationStore store,
        BearerPrincipal? callerPrincipal,
        IReadOnlyList<string>? investigationHandleIds,
        List<string> errors)
    {
        if (investigationHandleIds is null)
        {
            return store.Snapshot()
                .Where(h => h.State == InvestigationState.Active && InvestigationOwnership.IsOwnedBy(h, callerPrincipal))
                .OrderBy(h => h.HandleId, StringComparer.Ordinal)
                .ToArray();
        }

        if (investigationHandleIds.Count == 0)
        {
            return Array.Empty<InvestigationHandle>();
        }

        var handles = new List<InvestigationHandle>(investigationHandleIds.Count);
        foreach (var handleId in investigationHandleIds.Distinct(StringComparer.Ordinal))
        {
            var handle = store.GetById(handleId);
            if (handle is null)
            {
                errors.Add($"Handle '{handleId}' is unknown.");
                continue;
            }

            if (handle.State != InvestigationState.Active)
            {
                errors.Add($"Handle '{handleId}' is {handle.State} and cannot participate in replica_counters fan-out.");
                continue;
            }

            if (!InvestigationOwnership.IsOwnedBy(handle, callerPrincipal))
            {
                errors.Add($"Handle '{handleId}' is owned by a different bearer identity.");
                continue;
            }

            handles.Add(handle);
        }

        return handles.ToArray();
    }
}
