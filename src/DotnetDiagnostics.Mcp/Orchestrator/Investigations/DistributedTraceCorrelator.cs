using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.DistributedTrace;
using DotnetDiagnostics.Mcp.Tools;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Orchestrator fan-out for distributed W3C trace correlation (Phase 13 / G3, issue #437).
/// Enumerates the caller's <see cref="InvestigationState.Active"/> investigation handles, runs a
/// bounded <c>collect_events(kind="activities")</c> against each attached Pod through the
/// investigation proxy, then hands the per-Pod captures to the pure
/// <see cref="DistributedTraceStitcher"/> to produce one stitched cross-replica timeline.
/// </summary>
/// <remarks>
/// Bounded + client-owned: one synchronous fan-out per call, no server-side persistence and no
/// daemon — consistent with the stateless-server boundary. Per-Pod failures are isolated (one bad
/// replica does not sink the whole correlation); they surface as <see cref="FanoutResult.PodErrors"/>.
/// </remarks>
internal static class DistributedTraceCorrelator
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal sealed record FanoutResult(
        DistributedTraceTimeline? Timeline,
        int AttachedActivePods,
        IReadOnlyList<string> PodErrors);

    internal static async Task<FanoutResult> CorrelateAsync(
        IInvestigationStore store,
        IInvestigationProxyClient proxy,
        string? callerSessionId,
        string traceId,
        int durationSeconds,
        int maxActivities,
        IReadOnlyList<string>? sources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(proxy);

        var handles = store.Snapshot()
            .Where(h => h.State == InvestigationState.Active && IsOwnedByCaller(h, callerSessionId))
            .ToArray();

        var captures = new List<(string PodName, ActivityCapture Capture)>(handles.Length);
        var errors = new List<string>();

        var arguments = BuildActivitiesArguments(durationSeconds, maxActivities, sources);

        foreach (var handle in handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var request = new CallToolRequestParams
                {
                    Name = "collect_events",
                    Arguments = arguments,
                };

                var result = await proxy.CallToolAsync(handle, request, cancellationToken).ConfigureAwait(false);
                var capture = TryExtractCapture(result, out var failure);
                if (capture is null)
                {
                    errors.Add($"Pod '{handle.PodName}' (handle {handle.HandleId}): {failure}");
                    continue;
                }

                captures.Add((handle.PodName, capture));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"Pod '{handle.PodName}' (handle {handle.HandleId}): {ex.Message}");
            }
        }

        if (captures.Count == 0)
        {
            return new FanoutResult(null, handles.Length, errors);
        }

        var timeline = DistributedTraceStitcher.Stitch(traceId, captures);
        return new FanoutResult(timeline, handles.Length, errors);
    }

    private static Dictionary<string, JsonElement> BuildActivitiesArguments(
        int durationSeconds,
        int maxActivities,
        IReadOnlyList<string>? sources)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["kind"] = JsonSerializer.SerializeToElement("activities"),
            ["durationSeconds"] = JsonSerializer.SerializeToElement(durationSeconds),
            ["maxActivities"] = JsonSerializer.SerializeToElement(maxActivities),
        };

        if (sources is { Count: > 0 })
        {
            args["sources"] = JsonSerializer.SerializeToElement(sources);
        }

        return args;
    }

    private static ActivityCapture? TryExtractCapture(CallToolResult result, out string failure)
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

        if (envelope.Data?.Activities is not { } capture)
        {
            failure = "pod-local collect_events(kind=activities) returned no activity capture.";
            return null;
        }

        failure = string.Empty;
        return capture;
    }

    private static bool IsOwnedByCaller(InvestigationHandle handle, string? callerSessionId)
    {
        // Mirrors OrchestratorTools.IsOwnedByCaller: un-owned handles (stdio / framework attaches)
        // are reachable by every caller; session-bearing HTTP attaches are isolated per session.
        if (handle.OwnerSessionId is null)
        {
            return true;
        }

        return string.Equals(handle.OwnerSessionId, callerSessionId, StringComparison.Ordinal);
    }
}
