using System.Globalization;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.GatedCapture;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Threads;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Host-neutral orchestration for bounded threshold-gated capture (issue #419). Arms a single
/// <see cref="IThresholdGatedCaptureCollector"/> watch that polls one runtime metric and, the moment
/// a <see cref="TriggerPredicate"/> trips, fires a heavier capture (dump / cpu-sample / heap /
/// thread-snapshot) and registers its artifact in the shared <see cref="IDiagnosticHandleStore"/> so
/// the standard <c>query_snapshot</c> drilldown reaches it. The whole operation is one synchronous
/// invocation; nothing persists after the call. Shared by the MCP <c>collect_events</c> gating path
/// and the standalone <c>dotnet-diagnostics collect --capture-when</c> CLI flow.
/// </summary>
public static class GatedCaptureUseCases
{
    /// <summary>Hard upper bound on <c>windowSeconds</c> — keeps the watch "bounded" (a few minutes).</summary>
    public const int MaxWindowSeconds = 300;

    /// <summary>Hard upper bound on <c>maxCaptures</c>.</summary>
    public const int MaxCapturesCeiling = 10;

    /// <summary>Default CPU-sample collection window (seconds) used when captureKind=cpu-sample trips.</summary>
    public const int CpuSampleDurationSeconds = 5;

    private const string CpuSampleHandleKind = "cpu-sample";
    private const string ThreadSnapshotHandleKind = "thread-snapshot";
    private static readonly TimeSpan HandleTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Validates the gating parameters, resolves the target, arms the bounded watch, and returns the
    /// outcome. <paramref name="confirmDump"/> mirrors <c>collect_process_dump</c>'s confirmation gate:
    /// captureKind=dump writes a heap dump to disk and is refused until the caller passes it.
    /// </summary>
    public static async Task<DiagnosticResult<GatedCaptureResult>> WatchAndCapture(
        IThresholdGatedCaptureCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        ICpuSampler cpuSampler,
        IThreadSnapshotInspector threadInspector,
        IDumpInspector dumpInspector,
        IProcessDumper dumper,
        string? triggerWhen,
        string? captureKind,
        int windowSeconds,
        int maxCaptures = 1,
        int sampleIntervalSeconds = 2,
        bool confirmDump = false,
        int? processId = null,
        string? dumpOutputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(handles);

        if (!TriggerPredicate.TryParse(triggerWhen, out var predicate, out var predicateError))
        {
            return Invalid(nameof(triggerWhen), predicateError!);
        }

        if (!GatedCaptureKinds.TryParse(captureKind, out var kind))
        {
            return Invalid(nameof(captureKind),
                $"Unknown captureKind '{captureKind}'. Valid: {string.Join(", ", GatedCaptureKinds.Tokens)}.");
        }

        if (windowSeconds < 1 || windowSeconds > MaxWindowSeconds)
        {
            return Invalid(nameof(windowSeconds), $"must be between 1 and {MaxWindowSeconds} (the watch is bounded)");
        }

        if (maxCaptures < 1 || maxCaptures > MaxCapturesCeiling)
        {
            return Invalid(nameof(maxCaptures), $"must be between 1 and {MaxCapturesCeiling}");
        }

        if (sampleIntervalSeconds < 1 || sampleIntervalSeconds > windowSeconds)
        {
            return Invalid(nameof(sampleIntervalSeconds), $"must be between 1 and windowSeconds ({windowSeconds})");
        }

        if (kind.Value == GatedCaptureKind.Dump && !confirmDump)
        {
            var message = "captureKind='dump' writes a heap dump to disk when the threshold trips. " +
                "Pass confirmDump=true (CLI: --confirm) after explicit approval to arm it.";
            return DiagnosticResult.Fail<GatedCaptureResult>(
                message,
                new DiagnosticError("ConfirmationRequired", message, nameof(confirmDump)),
                new NextActionHint("collect_events",
                    "Re-issue with confirmDump=true to arm the dump-on-threshold watch. Required scopes: dump-write + ptrace.",
                    new Dictionary<string, object?> { ["confirmDump"] = true }) { Priority = NextActionHintPriority.High });
        }

        var resolved = await ResolveContextAsync<GatedCaptureResult>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        async Task<GatedCaptureOutcome> Capture(GatedCaptureTrigger trigger, CancellationToken ct)
        {
            switch (kind.Value)
            {
                case GatedCaptureKind.CpuSample:
                {
                    var result = await cpuSampler
                        .SampleAsync(pid, TimeSpan.FromSeconds(CpuSampleDurationSeconds), topN: 25, cancellationToken: ct)
                        .ConfigureAwait(false);
                    var handle = handles.Register(pid, CpuSampleHandleKind, result.Artifact, HandleTtl);
                    return new GatedCaptureOutcome(
                        $"Captured CPU sample ({result.Summary.TotalSamples:N0} samples over {CpuSampleDurationSeconds}s) when {predicate.Metric.Token()}={FormatValue(trigger.ObservedValue)}. Handle `{handle.Id}`.",
                        handle.Id, handle.ExpiresAt);
                }

                case GatedCaptureKind.ThreadSnapshot:
                {
                    var snapshot = await threadInspector.InspectLiveAsync(pid, options: null, ct).ConfigureAwait(false);
                    var handle = handles.Register(snapshot.ProcessId, ThreadSnapshotHandleKind, snapshot, HandleTtl, evictWhenProcessExits: true);
                    return new GatedCaptureOutcome(
                        $"Captured thread snapshot ({snapshot.Threads.Count} threads, {snapshot.Locks.Count} monitor locks) when {predicate.Metric.Token()}={FormatValue(trigger.ObservedValue)}. Handle `{handle.Id}`.",
                        handle.Id, handle.ExpiresAt);
                }

                case GatedCaptureKind.Heap:
                {
                    var snapshot = await dumpInspector
                        .InspectLiveAsync(pid, new DumpInspectionOptions(TopTypes: 20), ct)
                        .ConfigureAwait(false);
                    var handle = handles.Register(pid, HeapInspectionUseCases.HeapSnapshotKind, snapshot, HeapInspectionUseCases.HeapSnapshotHandleTtl);
                    return new GatedCaptureOutcome(
                        $"Captured live heap snapshot ({snapshot.Heap.TotalBytes:N0} bytes) when {predicate.Metric.Token()}={FormatValue(trigger.ObservedValue)}. Handle `{handle.Id}`.",
                        handle.Id, handle.ExpiresAt);
                }

                case GatedCaptureKind.Dump:
                {
                    var dump = await dumper.WriteDumpAsync(pid, ProcessDumpType.Mini, dumpOutputDirectory, ct).ConfigureAwait(false);
                    return new GatedCaptureOutcome(
                        $"Wrote {dump.DumpType} dump ({dump.FileSizeBytes:N0} bytes) to {dump.FilePath} when {predicate.Metric.Token()}={FormatValue(trigger.ObservedValue)}.",
                        ArtifactPath: dump.FilePath);
                }

                default:
                    throw new InvalidOperationException($"Unhandled captureKind '{kind.Value}'.");
            }
        }

        var watch = await collector.WatchAndCaptureAsync(
            pid,
            predicate,
            kind.Value,
            TimeSpan.FromSeconds(windowSeconds),
            maxCaptures,
            TimeSpan.FromSeconds(sampleIntervalSeconds),
            Capture,
            cancellationToken).ConfigureAwait(false);

        return WithContext(BuildResult(watch), resolved.Context);
    }

    private static DiagnosticResult<GatedCaptureResult> BuildResult(GatedCaptureResult watch)
    {
        var summary = BuildSummary(watch);
        var hints = new List<NextActionHint>();

        var firstWithHandle = watch.Captures.FirstOrDefault(c => c.Handle is not null);
        if (firstWithHandle is not null)
        {
            hints.Add(new NextActionHint("query_snapshot",
                $"Drill into the {firstWithHandle.CaptureKind} captured at the threshold breach without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = firstWithHandle.Handle })
            { Priority = NextActionHintPriority.High });
        }

        if (!watch.Tripped)
        {
            hints.Add(new NextActionHint("collect_events",
                "Predicate never tripped within the window. Re-arm with a longer window, a lower threshold, or while the workload runs.",
                new Dictionary<string, object?> { ["processId"] = watch.ProcessId }));
        }

        return DiagnosticResult.Ok(watch, summary, hints.ToArray());
    }

    private static string BuildSummary(GatedCaptureResult watch)
    {
        if (watch.Captures.Count > 0)
        {
            var ok = watch.Captures.Count(c => c.Error is null);
            return $"Threshold `{watch.Predicate}` tripped — fired {ok}/{watch.Captures.Count} {watch.CaptureKind} capture(s) over {watch.Duration.TotalSeconds:F1}s " +
                $"(peak {watch.Counter}={FormatValue(watch.PeakObservedValue)}).";
        }

        if (watch.ProcessExited)
        {
            return $"Target exited before `{watch.Predicate}` tripped ({watch.SamplesObserved} sample(s) observed; last {FormatValue(watch.LastObservedValue)}).";
        }

        return $"Threshold `{watch.Predicate}` never tripped within the {watch.Window.TotalSeconds:F0}s window " +
            $"({watch.SamplesObserved} sample(s); peak {FormatValue(watch.PeakObservedValue)}). No capture taken.";
    }

    private static string FormatValue(double? value)
        => value is null ? "n/a" : value.Value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Token(this GatedCaptureMetric metric) => GatedCaptureMetrics.Token(metric);

    private static DiagnosticResult<GatedCaptureResult> Invalid(string parameterName, string requirement)
        => DiagnosticResult.Fail<GatedCaptureResult>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid gating arguments. See the tool/CLI schema for ranges."));
}
