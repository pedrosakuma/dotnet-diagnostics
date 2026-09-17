using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.ProcessDiscovery;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>Independent retention budgets for a single bounded GC/ActivitySource observation.</summary>
public sealed record GcActivitiesCaptureOptions(
    int DurationSeconds = 10,
    int MaxGcEvents = 200,
    int MaxActivities = 200,
    string? TraceId = null,
    int MaxMatchedActivities = 200,
    IReadOnlyList<string>? Sources = null,
    int TopN = 20);

/// <summary>One collector's actual evidence, not a claim of simultaneous stream readiness.</summary>
public sealed record CorrelatedCaptureSide<T>(
    string Status,
    DateTimeOffset RequestedStart,
    DateTimeOffset RequestedEnd,
    DateTimeOffset? ObservedStart,
    DateTimeOffset? ObservedEnd,
    DiagnosticHandle? Handle,
    T? Capture,
    string? UnavailableReason);

/// <summary>Self-contained correlation plus the two independently queryable retained artifacts.</summary>
public sealed record GcActivitiesCapture(
    int ProcessId,
    DateTimeOffset? ProcessStartedAt,
    string Status,
    CorrelatedCaptureSide<GcSummary> Gc,
    CorrelatedCaptureSide<ActivityCapture> Activities,
    DateTimeOffset? IntersectionStart,
    DateTimeOffset? IntersectionEnd,
    double? StartupSkewMs,
    GcOverlayResult? Overlay,
    string? OverlayUnavailableReason,
    IReadOnlyList<string> Notes);

/// <summary>
/// Captures exactly two bounded streams from one resolved local process. Collector implementations
/// own their sessions and must honor cancellation and bounded drain; both tasks are always awaited.
/// </summary>
public static class GcActivitiesCaptureUseCase
{
    public static async Task<DiagnosticResult<GcActivitiesCapture>> CollectAsync(
        IGcCollector gcCollector,
        IActivityCollector activityCollector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        GcActivitiesCaptureOptions options,
        int? processId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gcCollector);
        ArgumentNullException.ThrowIfNull(activityCollector);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(options);
        if (Validate(options) is { } invalid)
            return new DiagnosticResult<GcActivitiesCapture>(invalid, [],
                new DiagnosticError("InvalidArgument", invalid));

        var resolution = await resolver.ResolveAsync(processId, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is not null || resolution.Context is null)
            return new DiagnosticResult<GcActivitiesCapture>("Target resolution failed.", [],
                resolution.Error ?? new DiagnosticError("ProcessNotFound", "No resolved target."));

        var context = resolution.Context;
        var pid = context.ProcessId;
        var lifetime = ProcessLifetime.TryReadStart(pid);
        var requestedStart = DateTimeOffset.UtcNow;
        var duration = TimeSpan.FromSeconds(options.DurationSeconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Startup already has a 30s budget per collector; concurrent session-control calls may queue.
        deadline.CancelAfter(duration + TimeSpan.FromSeconds(65));
        using var monitoring = new CancellationTokenSource();
        var targetExit = CancelOnExitAsync(pid, deadline, monitoring.Token);
        var gcTask = CaptureAsync(() => gcCollector.CollectAsync(pid, duration, options.MaxGcEvents, deadline.Token));
        var activityTask = CaptureAsync(() => activityCollector.CollectAsync(pid, duration, options.Sources,
            options.MaxActivities, options.TraceId, options.MaxMatchedActivities, deadline.Token));
        await Task.WhenAll(gcTask, activityTask).ConfigureAwait(false);
        await monitoring.CancelAsync().ConfigureAwait(false);
        await targetExit.ConfigureAwait(false);
        var gc = await gcTask.ConfigureAwait(false);
        var activities = await activityTask.ConfigureAwait(false);

        var finalLifetime = ProcessLifetime.TryReadStart(pid);
        string? identityError = lifetime is null || finalLifetime is null
            ? "Target lifetime is unavailable or the target exited; attribution is unavailable."
            : lifetime != finalLifetime ? "Target process lifetime changed during collection." : null;
        if (gc.Value is { } gcCapture &&
            (gcCapture.ProcessId != pid || gcCapture.Suspension?.ProcessStartedAt is { } g && g != lifetime))
            identityError = "GC evidence does not match the resolved target lifetime.";
        if (activities.Value is { } activityCapture &&
            (activityCapture.ProcessId != pid || activityCapture.ProcessStartedAt is { } a && a != lifetime))
            identityError = "Activity evidence does not match the resolved target lifetime.";

        var gcSide = BuildGcSide(gc.Value, gc.Error);
        var activitySide = BuildActivitySide(activities.Value, activities.Error);
        DateTimeOffset? intersectionStart = null;
        DateTimeOffset? intersectionEnd = null;
        double? skew = null;
        GcOverlayResult? overlay = null;
        var unavailable = identityError ?? gcSide.UnavailableReason ?? activitySide.UnavailableReason;
        if (gcSide.ObservedStart is { } gs && activitySide.ObservedStart is { } ast &&
            gcSide.ObservedEnd is { } ge && activitySide.ObservedEnd is { } ae)
        {
            skew = (ast - gs).TotalMilliseconds;
            var start = gs > ast ? gs : ast;
            var end = ge < ae ? ge : ae;
            if (end > start)
            {
                intersectionStart = start;
                intersectionEnd = end;
            }
            else unavailable ??= "Activity and GC observation windows do not overlap.";
        }
        if (gcSide.Handle is { } gcHandle && activitySide.Handle is { } activityHandle)
        {
            var error = GcCorrelationHandles.Resolve(handles, new HandleLookup(activityHandle, activities.Value!),
                gcHandle.Id, out var evidence);
            unavailable ??= error;
            if (gc.Value?.Suspension?.ProcessStartedAt is null || activities.Value?.ProcessStartedAt is null)
                unavailable ??= "Capture lifetime provenance is unknown; attribution is unavailable.";
            if (unavailable is null)
                overlay = GcActivityCorrelator.Correlate(activities.Value!, evidence!, options.TopN);
        }
        else unavailable ??= "Both retained artifacts are required for attribution.";

        var status = unavailable is null ? "captured" : gc.Value is null && activities.Value is null ? "failed" : "partial";
        var data = new GcActivitiesCapture(pid, lifetime, status, gcSide, activitySide,
            intersectionStart, intersectionEnd, skew, overlay, unavailable,
            ["Collectors start independently; overlap is not simultaneous readiness or full trace coverage.",
             "Requested windows begin at dispatch; observed windows include startup skew and may be censored.",
             "Source/trace filtering, activity retention loss, GC interval loss and top-N projection are separate.",
             "No retained matching spans does not establish absence of tracing or GC activity."]);
        return new DiagnosticResult<GcActivitiesCapture>(
            $"GC/activity capture: {status}. GC: {gcSide.Status}; activities: {activitySide.Status}. " +
            (unavailable is null ? "Inline overlay uses the observed intersection." : $"Overlay unavailable: {unavailable}"), [],
            status == "failed" ? new DiagnosticError("CollectionFailed", "Both collectors failed; see per-side outcomes.") : null)
        {
            Data = data,
            Handle = activitySide.Handle?.Id ?? gcSide.Handle?.Id,
            HandleExpiresAt = activitySide.Handle?.ExpiresAt ?? gcSide.Handle?.ExpiresAt,
            ResolvedProcess = context,
            Cancelled = cancellationToken.IsCancellationRequested,
        };

        CorrelatedCaptureSide<GcSummary> BuildGcSide(GcSummary? value, string? error)
        {
            if (value is null) return new("failed", requestedStart, requestedStart + duration, null, null, null, null, error);
            var problem = value.Suspension is not { IsAuthoritative: true } ? $"GC measurement unavailable: {value.Suspension?.Status ?? "legacy-unknown"}." : null;
            var handle = Register(value, CollectionHandleKinds.GcEvents, ref error);
            return new(error is null && problem is null ? "captured" : "partial", requestedStart, requestedStart + duration,
                value.Suspension?.ObservationStart ?? value.StartedAt,
                value.Suspension?.ObservationEnd ?? value.StartedAt + value.Duration, handle, value, error ?? problem);
        }

        CorrelatedCaptureSide<ActivityCapture> BuildActivitySide(ActivityCapture? value, string? error)
        {
            if (value is null) return new("failed", requestedStart, requestedStart + duration, null, null, null, null, error);
            var problem = value.Observation is not { Completion: "normal-stop", EventsLost: 0 }
                ? $"Activity stream coverage unavailable: {value.Observation?.Completion ?? "legacy-unknown"}; events lost={value.Observation?.EventsLost.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}." : null;
            var handle = Register(value, CollectionHandleKinds.Activities, ref error);
            return new(error is null && problem is null ? "captured" : "partial", requestedStart, requestedStart + duration,
                value.StartedAt, value.StartedAt + value.Duration, handle, value, error ?? problem);
        }

        static async Task CancelOnExitAsync(int processId, CancellationTokenSource collection, CancellationToken cancellationToken)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await collection.CancelAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (ArgumentException) { await collection.CancelAsync().ConfigureAwait(false); }
            catch (InvalidOperationException) { await collection.CancelAsync().ConfigureAwait(false); }
            catch (System.ComponentModel.Win32Exception)
            {
                // Unavailable monitoring does not fabricate identity; before/after provenance still gates attribution.
            }
        }

        DiagnosticHandle? Register(object value, string kind, ref string? error)
        {
            if (identityError is not null)
            {
                error = identityError;
                return null;
            }
            try { return handles.Register(pid, kind, value, TimeSpan.FromMinutes(10), evictWhenProcessExits: false, origin: HandleOrigin.Live); }
            catch (Exception ex) { error = $"Artifact registration failed: {ex.Message}"; return null; }
        }
    }

    private static async Task<(T? Value, string? Error)> CaptureAsync<T>(Func<Task<T>> collect) where T : class
    {
        try { return (await collect().ConfigureAwait(false), null); }
        catch (OperationCanceledException) { return (null, "Collection cancelled or its bounded deadline elapsed."); }
        catch (Exception ex) { return (null, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    internal static string? Validate(GcActivitiesCaptureOptions options)
    {
        if (options.DurationSeconds is < 1 or > 300) return "durationSeconds must be between 1 and 300.";
        if (options.MaxGcEvents is < 1 or > 10000) return "maxGcEvents must be between 1 and 10000.";
        if (options.MaxActivities is < 1 or > 10000) return "maxActivities must be between 1 and 10000.";
        if (options.MaxMatchedActivities is < 1 or > 10000) return "maxMatchedActivities must be between 1 and 10000.";
        if (options.TopN is < 1 or > 100) return "topN must be between 1 and 100.";
        if (options.TraceId is not null && !ActivityTraceProjector.TryNormalizeTraceId(options.TraceId, out _))
            return "traceId must be a non-zero 32-hex W3C trace-id.";
        return null;
    }
}
