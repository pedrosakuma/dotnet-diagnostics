using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.DistributedTrace;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Security;
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
/// Bounded + client-owned: one synchronous fan-out per call, optional persistence on each collecting
/// host and no daemon. Per-Pod failures are isolated (one bad
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
        IReadOnlyList<string> PodErrors)
    {
        public IReadOnlyList<RemoteCaptureReference>? RemoteCaptures { get; init; }
    }

    internal static Task<FanoutResult> CorrelateAsync(
        IInvestigationStore store,
        IInvestigationProxyClient proxy,
        BearerPrincipal? callerPrincipal,
        IReadOnlyList<string>? investigationHandleIds,
        string traceId,
        int durationSeconds,
        int maxActivities,
        IReadOnlyList<string>? sources,
        CancellationToken cancellationToken)
        => CorrelateAsync(store, proxy, callerPrincipal, investigationHandleIds, traceId,
            durationSeconds, maxActivities, sources, 200, cancellationToken);

    internal static Task<FanoutResult> CorrelateAsync(
        IInvestigationStore store,
        IInvestigationProxyClient proxy,
        BearerPrincipal? callerPrincipal,
        IReadOnlyList<string>? investigationHandleIds,
        string traceId,
        int durationSeconds,
        int maxActivities,
        IReadOnlyList<string>? sources,
        int maxMatchedActivities,
        CancellationToken cancellationToken)
        => CorrelateAsync(store, proxy, callerPrincipal, investigationHandleIds, traceId,
            durationSeconds, maxActivities, sources, maxMatchedActivities, false, null, cancellationToken);

    internal static Task<FanoutResult> CorrelateAsync(
        IInvestigationStore store, IInvestigationProxyClient proxy, BearerPrincipal? callerPrincipal,
        IReadOnlyList<string>? investigationHandleIds, string traceId, int durationSeconds, int maxActivities,
        IReadOnlyList<string>? sources, int maxMatchedActivities, bool includeHttpDestination,
        SensitiveDataRedactor? redactor, CancellationToken cancellationToken)
        => CorrelateAsync(store, proxy, callerPrincipal, investigationHandleIds, traceId, durationSeconds,
            maxActivities, sources, maxMatchedActivities, includeHttpDestination, redactor, false, cancellationToken);

    internal static async Task<FanoutResult> CorrelateAsync(
        IInvestigationStore store, IInvestigationProxyClient proxy, BearerPrincipal? callerPrincipal,
        IReadOnlyList<string>? investigationHandleIds, string traceId, int durationSeconds, int maxActivities,
        IReadOnlyList<string>? sources, int maxMatchedActivities, bool includeHttpDestination,
        SensitiveDataRedactor? redactor, bool persist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(proxy);
        if (!ActivityTraceProjector.TryNormalizeTraceId(traceId, out var normalizedTraceId))
        {
            throw new ArgumentException("traceId must be a non-zero 32-hex W3C trace-id.", nameof(traceId));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMatchedActivities, 1);

        if (persist && (callerPrincipal is null || !RemoteCaptureReference.ValidSelection(investigationHandleIds)))
            return new(null, 0, ["Durable fan-out requires a current principal and at most 16 bounded investigation IDs. No collection started."]);
        var errors = new List<string>();
        var handles = ResolveHandles(store, callerPrincipal, investigationHandleIds, errors);
        if (persist && handles.Length > RemoteCaptureReference.MaximumTargets)
            return new(null, handles.Length, ["Durable fan-out exceeds 16 targets; select a smaller explicit set. No collection started."]);
        var captures = new List<(string PodName, ActivityCapture Capture)>(handles.Length);
        var remoteCaptures = persist ? new List<RemoteCaptureReference>(handles.Length) : null;

        var arguments = BuildActivitiesArguments(durationSeconds, maxActivities, sources, normalizedTraceId, maxMatchedActivities);
        if (includeHttpDestination) arguments["includeHttpDestination"] = JsonSerializer.SerializeToElement(true);
        if (persist) arguments["persist"] = JsonSerializer.SerializeToElement(true);

        var tasks = handles.Select(handle => CollectAsync(proxy, handle, arguments, persist, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var (handle, capture, failure, wireResult) in results)
        {
            if (persist)
            {
                var reference = RemoteCaptureReference.Read(handle, "activities", wireResult, failure);
                remoteCaptures!.Add(reference);
                if (reference.Error is not null)
                {
                    errors.Add($"Target '{handle.TargetDisplayName}' (handle {handle.HandleId}): {reference.Error.Message}");
                    continue;
                }
            }
            if (capture is null)
            {
                errors.Add($"Target '{handle.TargetDisplayName}' (handle {handle.HandleId}): {failure}");
                continue;
            }

            captures.Add((handle.PodName, capture));
        }

        if (captures.Count == 0)
        {
            return new FanoutResult(null, handles.Length, errors) { RemoteCaptures = remoteCaptures };
        }

        var timeline = DistributedTraceStitcher.Stitch(traceId, captures, redactor);
        return new FanoutResult(timeline, handles.Length, errors) { RemoteCaptures = remoteCaptures };
    }

    private static Dictionary<string, JsonElement> BuildActivitiesArguments(
        int durationSeconds,
        int maxActivities,
        IReadOnlyList<string>? sources,
        string traceId,
        int maxMatchedActivities)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["kind"] = JsonSerializer.SerializeToElement("activities"),
            ["durationSeconds"] = JsonSerializer.SerializeToElement(durationSeconds),
            ["maxActivities"] = JsonSerializer.SerializeToElement(maxActivities),
            ["traceId"] = JsonSerializer.SerializeToElement(traceId),
            ["maxMatchedActivities"] = JsonSerializer.SerializeToElement(maxMatchedActivities),
        };

        if (sources is { Count: > 0 })
        {
            args["sources"] = JsonSerializer.SerializeToElement(sources);
        }

        return args;
    }

    private static async Task<(InvestigationHandle Handle, ActivityCapture? Capture, string Failure, CallToolResult? WireResult)> CollectAsync(
        IInvestigationProxyClient proxy,
        InvestigationHandle handle,
        Dictionary<string, JsonElement> arguments,
        bool persist,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new CallToolRequestParams
            {
                Name = "collect_events",
                Arguments = arguments,
            };

            var result = await proxy.CallToolAsync(handle, request, cancellationToken).ConfigureAwait(false);
            var capture = TryExtractCapture(result, out var failure);
            return (handle, capture, failure, result);
        }
        catch (OperationCanceledException) when (persist)
        {
            return (handle, null, "Collection cancelled; no remote capture result was confirmed. Inspect that host's capture inventory.", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (handle, null, ex.Message, null);
        }
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
                errors.Add($"Handle '{handleId}' is {handle.State} and cannot participate in distributed_trace fan-out.");
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
