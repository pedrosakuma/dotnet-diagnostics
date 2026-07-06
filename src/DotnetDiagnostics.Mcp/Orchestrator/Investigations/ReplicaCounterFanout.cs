using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.ReplicaCounters;
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
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal sealed record FanoutResult(
        ReplicaCounterSkew? Skew,
        int AttachedActivePods,
        IReadOnlyList<string> PodErrors);

    internal static async Task<FanoutResult> CompareAsync(
        IInvestigationStore store,
        IInvestigationProxyClient proxy,
        string? callerBearerName,
        IReadOnlyList<string>? investigationHandleIds,
        int durationSeconds,
        int intervalSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(proxy);

        var errors = new List<string>();
        var handles = ResolveHandles(store, callerBearerName, investigationHandleIds, errors);
        var readings = new List<ReplicaCounterReading>(handles.Length);
        var arguments = BuildCountersArguments(durationSeconds, intervalSeconds);

        // Simultaneous fan-out: dispatch all per-Pod collections concurrently so the windows overlap
        // (replica skew is only meaningful when sampled at the same wall-clock moment).
        var tasks = handles.Select(handle => CollectAsync(proxy, handle, arguments, durationSeconds, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

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

    private static async Task<(InvestigationHandle Handle, CounterSnapshot? Snapshot, string Failure)> CollectAsync(
        IInvestigationProxyClient proxy,
        InvestigationHandle handle,
        Dictionary<string, JsonElement> arguments,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        // The proxy transport sets an infinite HttpClient timeout, so a single stuck port-forward
        // would hang Task.WhenAll forever. Bound each pod to its collection window + slack and turn
        // a hung pod into a per-pod error, never a fan-out-wide hang. Caller cancellation still wins.
        using var perPodCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perPodCts.CancelAfter(TimeSpan.FromSeconds(durationSeconds + 30));
        try
        {
            var request = new CallToolRequestParams { Name = "collect_events", Arguments = arguments };
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
            return (handle, null, $"timed out after {durationSeconds + 30}s");
        }
        catch (Exception ex)
        {
            return (handle, null, ex.Message);
        }
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
        string json;
        if (result.StructuredContent is { } structured)
        {
            json = structured.GetRawText();
        }
        else
        {
            var text = result.Content.OfType<TextContentBlock>().FirstOrDefault();
            if (text is null)
            {
                failure = "pod-local collect_events returned neither structured content nor a text block.";
                return null;
            }

            json = text.Text;
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

    private static bool IsOwnedByCaller(InvestigationHandle handle, string? callerBearerName)
    {
        if (handle.OwnerBearerName is null)
        {
            return true;
        }

        return string.Equals(handle.OwnerBearerName, callerBearerName, StringComparison.Ordinal);
    }

    private static InvestigationHandle[] ResolveHandles(
        IInvestigationStore store,
        string? callerBearerName,
        IReadOnlyList<string>? investigationHandleIds,
        List<string> errors)
    {
        if (investigationHandleIds is null)
        {
            return store.Snapshot()
                .Where(h => h.State == InvestigationState.Active && IsOwnedByCaller(h, callerBearerName))
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

            if (!IsOwnedByCaller(handle, callerBearerName))
            {
                errors.Add($"Handle '{handleId}' is owned by a different bearer identity.");
                continue;
            }

            handles.Add(handle);
        }

        return handles.ToArray();
    }
}
