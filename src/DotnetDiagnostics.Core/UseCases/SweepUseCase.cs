using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Signals;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Triage;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Parallel initial-triage use case (issue #447 / Wave B1). Fans out the five EventPipe-safe
/// collectors — counters, gc, exceptions, threadpool and resource — concurrently, each over its
/// own session, projects the counters into observed signals and hypotheses, and returns one consolidated
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
            : new TriageResult(
                "unknown",
                TriageSeverity.Healthy,
                new TriageEvidence(null, null, null, null, null, null, null, null, null))
            {
                ModelVersion = 2,
                Assessment = "unknown",
                ObservedSignals = [],
                Hypotheses = [],
            };

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

        var failureText = FormatFailureText(failures.Count);
        var starvation = threadPool.Data?.HillClimbing.Count(static s => string.Equals(s.Reason, "Starvation", StringComparison.OrdinalIgnoreCase)) ?? 0;
        var fdText = resource?.FdCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a";
        var hypothesisText = triage.Hypotheses?.Count > 0
            ? string.Join(", ", triage.Hypotheses.Select(static h => $"{h.Name} ({h.Confidence})"))
            : "none";
        var summary =
            $"Sweep over {window}s: assessment={triage.Assessment} ({triage.Severity}), hypotheses={hypothesisText}. " +
            $"GC collections={gc.Data?.TotalCollections ?? 0}, exceptions={exceptions.Data?.TotalExceptions ?? 0}, " +
            $"threadpool starvation={starvation}, fd={fdText}.{failureText}";

        var hints = BuildSweepHints(triage, pid, handleMap);

        // Cross-signal correlation (#528): each collector already computed its own diagnosis-agnostic
        // signal groupings independently; surface it when ≥2 of them stood out in this SAME window
        // (a co-occurrence single-signal views can't see), never inferring a cause between them.
        var correlation = CoOccurrenceSignals.Detect(new CoOccurrenceContext(
        [
            new CorrelationSource("counters", counters.Handle, counters.Signals ?? []),
            new CorrelationSource("gc", gc.Handle, gc.Signals ?? []),
            new CorrelationSource("exceptions", exceptions.Handle, exceptions.Signals ?? []),
        ]));

        var result = DiagnosticResult.Ok(sweep, summary, hints.ToArray());
        if (correlation.Count > 0)
        {
            result = result with { Signals = correlation };
        }

        return WithContext(result, resolved.Context);
    }

    internal static string FormatFailureText(int failureCount)
        => failureCount > 0
            ? $" {failureCount} collector(s) failed."
            : string.Empty;

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

    /// <summary>Maps evidence-backed hypotheses to neutral drill-down hints that reuse sweep handles where possible.</summary>
    private static List<NextActionHint> BuildSweepHints(
        TriageResult triage,
        int pid,
        IReadOnlyDictionary<string, string?> handles)
    {
        var hints = new List<NextActionHint>();
        foreach (var hypothesis in triage.Hypotheses ?? [])
        {
            switch (hypothesis.Name)
            {
                case TriageClassifier.CpuComputeDemandHypothesis:
                    hints.Add(new NextActionHint("collect_sample", hypothesis.NextStep,
                        new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "cpu", ["durationSeconds"] = 10, ["topN"] = 25 }));
                    break;
                case TriageClassifier.GcOverheadHypothesis:
                    AddHandleHint(hints, handles, "gc", "events", hypothesis.NextStep);
                    break;
                case TriageClassifier.ManagedMemoryActivityHypothesis:
                    hints.Add(new NextActionHint("collect_sample", hypothesis.NextStep,
                        new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "allocation", ["durationSeconds"] = 10 }));
                    break;
                case TriageClassifier.ThreadPoolBacklogHypothesis:
                    AddHandleHint(hints, handles, "threadpool", "timeline", hypothesis.NextStep);
                    break;
                case TriageClassifier.SynchronizationContentionHypothesis:
                    hints.Add(new NextActionHint("collect_events", hypothesis.NextStep,
                        new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "contention", ["durationSeconds"] = 10 }));
                    break;
                case TriageClassifier.WaitingOrBackpressureHypothesis:
                    hints.Add(new NextActionHint("collect_thread_snapshot", hypothesis.NextStep,
                        new Dictionary<string, object?> { ["processId"] = pid }));
                    break;
            }
        }

        if (hints.Count == 0 && triage.GetHighestPriorityObservedSignal() is { } prioritySignal)
        {
            var (kind, view) = prioritySignal.Name switch
            {
                "threadpool.queue" => ("threadpool", "timeline"),
                "exceptions.rate" => ("exceptions", "byType"),
                _ => ("counters", "summary"),
            };
            AddHandleHint(
                hints,
                handles,
                kind,
                view,
                $"Observed {prioritySignal.Name}, but the current window is inconclusive. Drill into the matching sweep handle before assigning a cause.");
        }

        if (hints.Count == 0)
        {
            AddHandleHint(
                hints,
                handles,
                "counters",
                "summary",
                "No salient triage signal crossed a threshold; drill into the counter handle if the symptom persists.");
        }

        if (hints.Count == 0)
        {
            hints.Add(new NextActionHint(
                "collect_events",
                "No matching sweep handle is available. Re-collect the relevant signal before assigning a cause.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "counters", ["durationSeconds"] = 10 }));
        }

        return hints;
    }

    private static void AddHandleHint(
        List<NextActionHint> hints,
        IReadOnlyDictionary<string, string?> handles,
        string kind,
        string view,
        string reason)
    {
        if (handles.TryGetValue(kind, out var handle) && handle is not null)
        {
            hints.Add(new NextActionHint(
                "query_snapshot",
                reason,
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = view }));
        }
    }
}
