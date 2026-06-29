using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Triage;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Parallel initial-triage use case (issue #447 / Wave B1). Fans out the five EventPipe-safe
/// collectors — counters, gc, exceptions, threadpool and resource — concurrently, each over its
/// own session, classifies the counters into a triage verdict and returns one consolidated
/// <see cref="SweepResult"/> envelope plus next-action hints. Cold triage drops from five
/// sequential calls (~25–40s) to a single window. Shared by the MCP <c>collect_events(kind=sweep)</c>
/// path and the standalone CLI (<c>collect --kind sweep</c>); depends on Core only.
/// </summary>
public static class SweepUseCase
{
    /// <summary>Minimum window — EventPipe sessions take ~500ms–1s to start and counters arrive on interval boundaries.</summary>
    public const int MinimumDurationSeconds = 6;

    public static async Task<DiagnosticResult<SweepResult>> RunSweep(
        ICounterCollector counterCollector,
        IGcCollector gcCollector,
        IExceptionCollector exceptionCollector,
        IThreadPoolCollector threadPoolCollector,
        IProcessResourcesCollector resourceCollector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        int? processId = null,
        int durationSeconds = MinimumDurationSeconds,
        int maxRecent = 100,
        int maxEvents = 200,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        // Floor the window so every EventPipe session has time to start and emit at least one
        // counter interval; smaller values would silently return empty snapshots.
        var window = durationSeconds < MinimumDurationSeconds ? MinimumDurationSeconds : durationSeconds;

        // Resolve once so all five collectors target the same pid (and the user-facing ambiguity
        // failure surfaces exactly once instead of five identical envelopes).
        var resolved = await ResolveContextAsync<SweepResult>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var countersTask = SafeAsync(() => EventCollectionUseCases.SnapshotCounters(
            counterCollector, resolver, handles, pid, window,
            providers: null, meters: ["Microsoft.AspNetCore.Hosting"],
            intervalSeconds: 1, maxInstrumentTimeSeries: 100, depth, cancellationToken), "counters", cancellationToken);
        var gcTask = SafeAsync(() => EventCollectionUseCases.CollectGcEvents(
            gcCollector, resolver, handles, pid, window, maxEvents, depth, cancellationToken), "gc", cancellationToken);
        var exceptionsTask = SafeAsync(() => EventCollectionUseCases.CollectExceptions(
            exceptionCollector, resolver, handles, pid, window, maxRecent, depth, cancellationToken), "exceptions", cancellationToken);
        var threadPoolTask = SafeAsync(() => EventCollectionUseCases.CollectThreadPool(
            threadPoolCollector, resolver, handles, pid, window, depth, cancellationToken), "threadpool", cancellationToken);
        var resourceTask = SafeResourceAsync(() => resourceCollector.CollectAsync(pid, window, sampleEverySeconds: 1, cancellationToken), cancellationToken);

        await Task.WhenAll(countersTask, gcTask, exceptionsTask, threadPoolTask, resourceTask).ConfigureAwait(false);

        var counters = countersTask.Result;
        var gc = gcTask.Result;
        var exceptions = exceptionsTask.Result;
        var threadPool = threadPoolTask.Result;
        var resource = resourceTask.Result;

        var failures = new List<string>();
        Note(failures, "counters", counters);
        Note(failures, "gc", gc);
        Note(failures, "exceptions", exceptions);
        Note(failures, "threadpool", threadPool);
        if (resource is null) failures.Add("resource: collection failed");

        var requestDurationP95 = counters.Data is { Meters: var meters }
            ? HeadlineCounters.FindRequestDuration(meters)?.Histogram?.P95
            : null;
        var triage = counters.Data is not null
            ? TriageClassifier.Classify(counters.Data, requestDurationP95)
            : new TriageResult("unknown", TriageSeverity.Healthy, new TriageEvidence(null, null, null, null, null, null, null, null, null));

        var handleMap = new Dictionary<string, string?>
        {
            ["counters"] = counters.Handle,
            ["gc"] = gc.Handle,
            ["exceptions"] = exceptions.Handle,
            ["threadpool"] = threadPool.Handle,
        };

        var sweep = new SweepResult(
            window, triage,
            counters.Data, gc.Data, exceptions.Data, threadPool.Data, resource,
            handleMap, failures);

        var failureText = failures.Count > 0 ? $" {failures.Count} collector(s) failed (see data.failures)." : string.Empty;
        var starvation = threadPool.Data?.HillClimbing.Count(static s => string.Equals(s.Reason, "Starvation", StringComparison.OrdinalIgnoreCase)) ?? 0;
        var fdText = resource?.FdCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a";
        var summary =
            $"Sweep over {window}s: {triage.Verdict} ({triage.Severity}). " +
            $"GC collections={gc.Data?.TotalCollections ?? 0}, exceptions={exceptions.Data?.TotalExceptions ?? 0}, " +
            $"threadpool starvation={starvation}, fd={fdText}.{failureText}";

        var hints = BuildSweepHints(triage, pid);
        return WithContext(DiagnosticResult.Ok(sweep, summary, hints.ToArray()), resolved.Context);
    }

    private static void Note<T>(List<string> failures, string kind, DiagnosticResult<T> r)
    {
        if (r.IsError) failures.Add($"{kind}: {r.Error!.Kind} — {r.Error.Message}");
    }

    // A single collector throwing (e.g. EventPipe session-start TimeoutException) must not fault
    // Task.WhenAll and discard the siblings' results — convert non-cancellation faults into a Fail
    // result so the consolidated envelope + per-collector failure list still come back.
    private static async Task<DiagnosticResult<T>> SafeAsync<T>(Func<Task<DiagnosticResult<T>>> run, string kind, CancellationToken ct)
    {
        try { return await run().ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return DiagnosticResult.Fail<T>($"{kind} failed: {ex.Message}", new DiagnosticError("CollectorFailed", ex.Message)); }
    }

    private static async Task<ProcessResources?> SafeResourceAsync(Func<Task<ProcessResources>> run, CancellationToken ct)
    {
        try { return await run().ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>Maps the triage verdict to drill-down hints, mirroring PerformTriage's mapping but pointing back at the sweep handles.</summary>
    private static List<NextActionHint> BuildSweepHints(TriageResult triage, int pid)
    {
        var hints = new List<NextActionHint>();
        switch (triage.Verdict)
        {
            case TriageClassifier.CpuBound:
                hints.Add(new NextActionHint("collect_sample", $"cpu-usage={triage.Evidence.CpuUsage:F1}% — investigate the hot path.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10, ["topN"] = 25 }));
                break;
            case TriageClassifier.GcPressure:
            case TriageClassifier.MemoryPressure:
                hints.Add(new NextActionHint("inspect_heap", "GC / memory pressure — inspect the live heap for the dominant types.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["source"] = "live" }));
                break;
            case TriageClassifier.ThreadPoolStarvation:
                hints.Add(new NextActionHint("collect_thread_snapshot", $"threadpool-queue-length={triage.Evidence.ThreadPoolQueueLength:F0} — inspect blocking stacks.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
                break;
            case TriageClassifier.LockContention:
                hints.Add(new NextActionHint("collect_events", $"monitor-lock-contention-count={triage.Evidence.MonitorLockContentionCount:F0} — drill into contention.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "contention", ["durationSeconds"] = 10 }));
                break;
            case TriageClassifier.IoBound:
                hints.Add(new NextActionHint("collect_thread_snapshot", "Low CPU + queue buildup — I/O bound likely, inspect blocking stacks.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
                break;
            default:
                hints.Add(new NextActionHint("query_snapshot", "System looks healthy — drill into any of the sweep handles to confirm.", null));
                break;
        }
        return hints;
    }
}
