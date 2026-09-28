using System.Diagnostics;
using System.Security.Cryptography;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.Triage;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.TestSupport;
using DotnetDiagnostics.TestSupport.OperatingEnvelope;
using Xunit;

namespace DotnetDiagnostics.Core.Tests.OperatingEnvelope;

[Collection("LiveProcess")]
public sealed class OperatingEnvelopeAcceptanceTests
{
    private static readonly CaptureAccess Owner = new("operating-envelope-acceptance");
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(5);

    [OperatingEnvelopeFact]
    public async Task RecordsBoundedMatchedLiveOperatingEnvelope()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("DOTNET_DBG_MCP_OPERATING_ENVELOPE_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException(
                "Set DOTNET_DBG_MCP_OPERATING_ENVELOPE_OUTPUT to a new evidence directory when opting in.");
        }

        var schedule = OperatingEnvelopeProtocol.CreateSchedule();
        var inputHashes = GetInputHashes();
        var writer = await OperatingEnvelopeArtifactWriter.CreateAsync(outputDirectory, schedule, inputHashes);
        var runDeadlineTimestamp = Stopwatch.GetTimestamp() + ToStopwatchTicks(schedule.Configuration.RunDeadline);
        using var runDeadline = CreateDeadline(runDeadlineTimestamp);
        var completedPairs = new List<OperatingEnvelopePairResult>();
        var completedTrials = new List<OperatingEnvelopeTrialResult>();
        var orderedPairs = schedule.Trials.GroupBy(trial => trial.PairId).ToArray();
        var stop = OperatingEnvelopeStopOutcome.None;

        foreach (var pair in orderedPairs)
        {
            if (stop == OperatingEnvelopeStopOutcome.None && runDeadline.IsCancellationRequested)
            {
                stop = OperatingEnvelopeStopOutcome.RunDeadlineExceeded;
            }

            var bytesBeforePair = 0L;
            if (stop == OperatingEnvelopeStopOutcome.None)
            {
                try
                {
                    OperatingEnvelopeDeadlineGuard.CheckRun(
                        runDeadlineTimestamp, Stopwatch.GetTimestamp(), runDeadline);
                    bytesBeforePair = GetDirectoryBytes(
                        writer.RunDirectory,
                        () => OperatingEnvelopeDeadlineGuard.CheckRun(
                            runDeadlineTimestamp, Stopwatch.GetTimestamp(), runDeadline),
                        runDeadline.Token);
                }
                catch (OperationCanceledException) when (runDeadline.IsCancellationRequested)
                {
                    stop = OperatingEnvelopeStopOutcome.RunDeadlineExceeded;
                }
            }

            var pairReservation = 2L * new CaptureStoreOptions().MaxPackageBytes + 16 * 1024 * 1024;
            if (stop == OperatingEnvelopeStopOutcome.None &&
                bytesBeforePair + pairReservation > schedule.Configuration.MaximumOutputBytes)
            {
                stop = OperatingEnvelopeStopOutcome.OutputBudgetExceeded;
            }

            var pairTrials = new List<OperatingEnvelopeTrialResult>(2);
            foreach (var trialPlan in pair.OrderBy(item => item.OrderInPair))
            {
                var trial = stop == OperatingEnvelopeStopOutcome.None
                    ? await RunTrialAsync(
                        trialPlan, writer, runDeadline, runDeadlineTimestamp)
                    : CreateNotRunResult(trialPlan, writer.ConfigurationHash, schedule.Configuration, stop);
                pairTrials.Add(trial);
                completedTrials.Add(trial);
                if (trial.StopOutcome != OperatingEnvelopeStopOutcome.None)
                {
                    stop = trial.StopOutcome;
                }
                if (!trial.CleanupSucceeded)
                {
                    var reason = trial.Error ?? "One or more trial-owned tasks did not settle during bounded cleanup.";
                    await writer.WriteQuarantineAsync(trial, reason);
                    throw new InvalidOperationException(
                        $"Trial {trial.Plan.TrialId} is quarantined; pair and final evidence were not published: {reason}");
                }
            }

            var pairResult = OperatingEnvelopePairValidation.Validate(
                pair.Key, pair.First().Population, pairTrials);
            completedPairs.Add(pairResult);
            await writer.WritePairAsync(pairResult, pairTrials, CancellationToken.None);
        }

        await writer.WriteFinalManifestAsync(CancellationToken.None);
        Assert.All(completedPairs, pair =>
            Assert.True(pair.IsValid, $"{pair.PairId} is invalid: {string.Join("; ", pair.InvalidReasons)}"));
        Assert.Equal(schedule.Trials.Count, completedTrials.Count);
        Assert.Equal(OperatingEnvelopeStopOutcome.None, stop);
    }

    private static async Task<OperatingEnvelopeTrialResult> RunTrialAsync(
        OperatingEnvelopeTrialPlan plan,
        OperatingEnvelopeArtifactWriter writer,
        CancellationTokenSource runDeadline,
        long runDeadlineTimestamp)
    {
        var config = writer.Schedule.Configuration;
        var startedUtc = DateTimeOffset.UtcNow;
        var cellStarted = Stopwatch.GetTimestamp();
        var cellDeadlineTimestamp = cellStarted + ToStopwatchTicks(config.CellDeadline);
        using var cellDeadline = CreateDeadline(cellDeadlineTimestamp);
        using var cellToken = CancellationTokenSource.CreateLinkedTokenSource(
            runDeadline.Token, cellDeadline.Token);
        void CheckDeadline() => OperatingEnvelopeDeadlineGuard.CheckTrial(
            runDeadlineTimestamp, cellDeadlineTimestamp, Stopwatch.GetTimestamp(), runDeadline, cellDeadline);
        var cellDirectory = Path.Combine(writer.RunDirectory, plan.StoreDirectoryName);
        var storeRoot = Path.Combine(cellDirectory, "store");
        Directory.CreateDirectory(storeRoot);

        var samples = new List<LiveSampleProcess>(plan.TargetCount);
        var resources = new OperatingEnvelopeResourceMonitor();
        var cleanupErrors = new List<string>();
        var notes = new List<string>();
        var artifacts = new List<OperatingEnvelopeArtifactMeasurement>();
        var targetRequests = new List<OperatingEnvelopeTargetRequests>();
        var warmupProgress = new List<OperatingEnvelopeRequestProgress>();
        var measuredProgress = new Dictionary<int, OperatingEnvelopeRequestProgress>();
        var warmupStarted = 0L;
        var warmupRequests = EmptyRequests(plan.TargetCount * WarmupRequestSlots(config), 0);
        var requests = EmptyRequests(plan.TargetCount * MeasuredRequestSlots(config), config.WindowDuration.TotalMilliseconds);
        OperatingEnvelopeDiagnosticResources diagnosticResources = EmptyDiagnosticResources("Not sampled.");
        var outcome = OperatingEnvelopeTrialOutcome.Failed;
        var stopOutcome = OperatingEnvelopeStopOutcome.CollectionFailed;
        string? error = null;
        var warmupMilliseconds = 0d;
        var cleanupStarted = 0L;
        var resourceSettlementDeadline = 0L;
        var resourcesStopped = false;
        var ownedTasks = new List<Task>();
        var collectionTasks = new List<Task<ArtifactExecution>>();
        SqliteCaptureStore? store = null;

        try
        {
            store = new SqliteCaptureStore(new RootProvider(storeRoot));
            for (var index = 0; index < plan.TargetCount; index++)
            {
                CheckDeadline();
                samples.Add(await LiveSampleProcess.StartPublishedAsync(
                    "CoreClrSample",
                    new LiveSampleOptions
                    {
                        WaitForHttpReady = true,
                        ReadinessPath = "/weatherforecast",
                        DiagnosticTimeout = TimeSpan.FromSeconds(30),
                        HttpTimeout = TimeSpan.FromSeconds(30),
                    },
                    cellToken.Token));
            }

            warmupStarted = Stopwatch.GetTimestamp();
            warmupProgress.AddRange(samples.Select(_ =>
                new OperatingEnvelopeRequestProgress(WarmupRequestSlots(config))));
            var warmupTasks = samples.Select((sample, index) =>
                    SendWarmupRequestsAsync(
                        sample, plan, index, config, warmupProgress[index], cellToken.Token))
                .ToArray();
            ownedTasks.AddRange(warmupTasks);
            var warmupResults = await Task.WhenAll(warmupTasks);
            warmupMilliseconds = ElapsedMilliseconds(warmupStarted);
            notes.AddRange(warmupResults.SelectMany(result => result.Notes));
            warmupRequests = CombineRequests(
                    warmupResults.Select(result => result.Accounting).ToArray(),
                    warmupMilliseconds);

            resources.Start(samples);
            ownedTasks.Add(resources.SamplingTask);
            var targetPlans = samples.Select((sample, index) => (Sample: sample, Index: index)).ToArray();
            foreach (var target in targetPlans)
            {
                if (plan.Population == OperatingEnvelopePopulation.MixedCollectors)
                {
                    var collection = CollectSweepAsync(
                        target.Sample.ProcessId, plan.StorageMode, store, storeRoot, config, cellToken.Token);
                    collectionTasks.Add(collection);
                    ownedTasks.Add(collection);
                }
                else
                {
                    foreach (var job in JobsFor(plan.Population, target.Sample.ProcessId, config))
                    {
                        var collection = CollectArtifactAsync(
                            target.Sample.ProcessId, job, plan.StorageMode, store, storeRoot, config, cellToken.Token);
                        collectionTasks.Add(collection);
                        ownedTasks.Add(collection);
                    }
                }

                var requestProgress = new OperatingEnvelopeRequestProgress(MeasuredRequestSlots(config));
                measuredProgress.Add(target.Sample.ProcessId, requestProgress);
                var request = DriveMeasuredRequestsAsync(
                    target.Sample, target.Index, plan, config,
                    requestProgress, cellToken.Token);
                ownedTasks.Add(request);
            }

            var workload = Task.WhenAll(ownedTasks);
            try
            {
                await workload.WaitAsync(cellToken.Token);
            }
            catch (OperationCanceledException) when (cellToken.IsCancellationRequested)
            {
                throw;
            }

            var executions = collectionTasks.Select(task => task.Result).ToArray();
            resourceSettlementDeadline = Stopwatch.GetTimestamp() + ToStopwatchTicks(CleanupDeadline);
            diagnosticResources = await resources.StopAsync(resourceSettlementDeadline);
            resourcesStopped = true;
            CheckDeadline();
            foreach (var execution in executions)
            {
                CheckDeadline();
                var measurementTask = MeasureStoredArtifactAsync(execution, CheckDeadline, cellToken.Token);
                ownedTasks.Add(measurementTask);
                artifacts.Add(await measurementTask.WaitAsync(cellToken.Token));
            }
            CheckDeadline();

            if (samples.Any(sample => !sample.IsRunning))
            {
                stopOutcome = OperatingEnvelopeStopOutcome.TargetExited;
                outcome = OperatingEnvelopeTrialOutcome.Stopped;
                error = "At least one target exited before the trial completed.";
            }
            else if (artifacts.Any(artifact => !artifact.CollectionSucceeded))
            {
                stopOutcome = artifacts.Any(artifact => artifact.CaptureState == CaptureState.Interrupted.ToString())
                    ? OperatingEnvelopeStopOutcome.PersistenceFailed
                    : OperatingEnvelopeStopOutcome.CollectionFailed;
                outcome = OperatingEnvelopeTrialOutcome.Incomplete;
                error = "At least one requested collector did not complete successfully.";
            }
            else
            {
                stopOutcome = OperatingEnvelopeStopOutcome.None;
                outcome = OperatingEnvelopeTrialOutcome.Completed;
                error = null;
            }
        }
        catch (OperationCanceledException ex) when (cellDeadline.IsCancellationRequested || runDeadline.IsCancellationRequested)
        {
            stopOutcome = runDeadline.IsCancellationRequested
                ? OperatingEnvelopeStopOutcome.RunDeadlineExceeded
                : OperatingEnvelopeStopOutcome.CellDeadlineExceeded;
            outcome = OperatingEnvelopeTrialOutcome.Stopped;
            error = $"{stopOutcome}: {ex.Message}";
            notes.Add("Cancellation used the one absolute run/cell deadline; no stage received a fresh timeout.");
        }
        catch (Exception ex)
        {
            stopOutcome = OperatingEnvelopeStopOutcome.CollectionFailed;
            outcome = OperatingEnvelopeTrialOutcome.Failed;
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            cleanupStarted = Stopwatch.GetTimestamp();
            var cleanupDeadline = cleanupStarted + ToStopwatchTicks(CleanupDeadline);
            cellToken.Cancel();
            resources.RequestStop();

            var targetTerminationTasks = new List<Task>(samples.Count);
            foreach (var sample in samples)
            {
                try
                {
                    targetTerminationTasks.Add(sample.DisposeAsync().AsTask());
                }
                catch (Exception ex)
                {
                    targetTerminationTasks.Add(Task.FromException(ex));
                    cleanupErrors.Add($"Target {sample.ProcessId} termination: {ex.GetType().Name}: {ex.Message}");
                }
            }

            try
            {
                var settlement = await OperatingEnvelopeTaskSettlement.SettleAfterTargetsAsync(
                    targetTerminationTasks, () => ownedTasks, cleanupDeadline);
                if (!settlement.TargetsSettled)
                {
                    cleanupErrors.Add(
                        $"Target termination did not settle before the absolute {CleanupDeadline} cleanup deadline.");
                }
                cleanupErrors.AddRange(settlement.TargetFaults.Select(
                    fault => $"Target termination faulted: {fault}"));
                if (settlement.TargetFaults.Count > 0)
                {
                    cleanupErrors.Add("At least one target's termination could not be confirmed.");
                }
                if (!settlement.OwnedTasksSettled)
                {
                    cleanupErrors.Add(
                        $"Owned work did not settle after target termination before the shared {CleanupDeadline} cleanup deadline.");
                }
                if (settlement.OwnedTaskFaults.Count > 0)
                {
                    notes.AddRange(settlement.OwnedTaskFaults.Select(
                        fault => $"Owned collection/request task faulted: {fault}"));
                    outcome = OperatingEnvelopeTrialOutcome.Failed;
                    stopOutcome = OperatingEnvelopeStopOutcome.CollectionFailed;
                    error ??= "One or more collection/request tasks faulted after target termination.";
                }
            }
            catch (Exception ex)
            {
                cleanupErrors.Add($"Owned task cleanup: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                if (!resourcesStopped)
                {
                    diagnosticResources = await resources.StopAsync(cleanupDeadline);
                }
            }
            catch (Exception ex)
            {
                cleanupErrors.Add($"Resource sampler cleanup: {ex.GetType().Name}: {ex.Message}");
            }

            foreach (var note in resources.Notes)
            {
                notes.Add(note);
            }
            resources.Dispose();

            if (cleanupErrors.Count == 0)
            {
                foreach (var sample in samples)
                {
                    if (!measuredProgress.TryGetValue(sample.ProcessId, out var progress))
                    {
                        targetRequests.Add(new(sample.ProcessId,
                            EmptyRequests(MeasuredRequestSlots(config), config.WindowDuration.TotalMilliseconds)));
                        continue;
                    }

                    var snapshot = progress.Snapshot(config.WindowDuration.TotalMilliseconds);
                    targetRequests.Add(new(sample.ProcessId, snapshot.Accounting));
                    foreach (var note in snapshot.Notes)
                    {
                        if (!notes.Contains(note, StringComparer.Ordinal))
                        {
                            notes.Add(note);
                        }
                    }
                }

                if (targetRequests.Count > 0)
                {
                    requests = CombineRequests(
                        targetRequests.Select(result => result.Accounting).ToArray(),
                        config.WindowDuration.TotalMilliseconds);
                }

                if (warmupProgress.Count > 0)
                {
                    var elapsed = warmupStarted == 0 ? 0 : ElapsedMilliseconds(warmupStarted);
                    warmupMilliseconds = elapsed;
                    var warmupSnapshots = warmupProgress
                        .Select(progress => progress.Snapshot(elapsed))
                        .ToArray();
                    warmupRequests = CombineRequests(
                        warmupSnapshots.Select(snapshot => snapshot.Accounting).ToArray(),
                        elapsed);
                    foreach (var note in warmupSnapshots.SelectMany(snapshot => snapshot.Notes))
                    {
                        if (!notes.Contains(note, StringComparer.Ordinal))
                        {
                            notes.Add(note);
                        }
                    }
                }
            }

            if (cleanupErrors.Count > 0)
            {
                stopOutcome = OperatingEnvelopeStopOutcome.CleanupFailed;
                outcome = OperatingEnvelopeTrialOutcome.Stopped;
                error = string.Join(" | ", cleanupErrors);
            }
            else if (stopOutcome == OperatingEnvelopeStopOutcome.None)
            {
                try
                {
                    CheckDeadline();
                }
                catch (OperationCanceledException) when (
                    cellDeadline.IsCancellationRequested || runDeadline.IsCancellationRequested)
                {
                    stopOutcome = runDeadline.IsCancellationRequested
                        ? OperatingEnvelopeStopOutcome.RunDeadlineExceeded
                        : OperatingEnvelopeStopOutcome.CellDeadlineExceeded;
                    outcome = OperatingEnvelopeTrialOutcome.Stopped;
                    error = $"{stopOutcome}: deadline elapsed before trial cleanup completed.";
                }
            }
        }

        var targetResources = resources.Targets;
        var cleanupMilliseconds = ElapsedMilliseconds(cleanupStarted);
        return new(
            plan,
            writer.ConfigurationHash,
            outcome,
            stopOutcome,
            startedUtc,
            DateTimeOffset.UtcNow,
            warmupMilliseconds,
            warmupRequests,
            requests,
            targetRequests.AsReadOnly(),
            targetResources,
            diagnosticResources,
            artifacts.AsReadOnly(),
            cleanupMilliseconds,
            cleanupErrors.Count == 0,
            cleanupErrors.AsReadOnly(),
            notes.AsReadOnly(),
            error);
    }

    private static IEnumerable<CollectorJob> JobsFor(
        OperatingEnvelopePopulation population,
        int processId,
        OperatingEnvelopeConfiguration configuration)
    {
        var window = configuration.WindowDuration;
        switch (population)
        {
            case OperatingEnvelopePopulation.IdleCounters:
            case OperatingEnvelopePopulation.TwoTargetsSharedDisk:
                yield return CounterJob(processId, window);
                break;
            case OperatingEnvelopePopulation.CpuAndCounters:
                yield return new("cpu-sample", "collect_sample", async token =>
                {
                    var sample = await new EventPipeCpuSampler().SampleAsync(
                        processId, window, topN: 10, cancellationToken: token);
                    return new CollectorResult(
                        sample.Artifact, sample.Summary.Timings.SessionDrainDuration.TotalMilliseconds);
                });
                yield return CounterJob(processId, window);
                break;
            case OperatingEnvelopePopulation.AllocationAndCounters:
                yield return new("allocation-sample", "collect_sample", async token =>
                {
                    var sample = await new EventPipeAllocationSampler().SampleAsync(
                        processId, window, topN: 10, cancellationToken: token);
                    return new CollectorResult(new AllocationSampleArtifact(sample.Summary, sample.Artifact));
                });
                yield return CounterJob(processId, window);
                break;
            case OperatingEnvelopePopulation.ActivityAndCounters:
                yield return new(CollectionHandleKinds.Activities, "collect_events", async token =>
                    new CollectorResult(await new EventPipeActivityCollector().CollectAsync(
                        processId, window, sources: ["CoreClrSample.Activities"],
                        maxActivities: 100, cancellationToken: token)));
                yield return CounterJob(processId, window);
                break;
            case OperatingEnvelopePopulation.QueueAndThread:
                yield return new(CollectionHandleKinds.ThreadPoolSnapshot, "collect_events", async token =>
                    new CollectorResult(await new EventPipeThreadPoolCollector().CollectAsync(
                        processId, window, cancellationToken: token)));
                yield return new(SamplerUseCases.ThreadSnapshotKind, "collect_thread_snapshot", async token =>
                    new CollectorResult(await new ClrMdThreadSnapshotInspector().InspectLiveAsync(
                        processId, new ThreadSnapshotOptions(MaxFramesPerThread: 32), token)));
                break;
            case OperatingEnvelopePopulation.MixedCollectors:
                throw new InvalidOperationException("Mixed collectors are scheduled as one composed capture.");
            default:
                throw new ArgumentOutOfRangeException(nameof(population), population, "Unknown operating-envelope population.");
        }
    }

    private static CollectorJob CounterJob(int processId, TimeSpan window) =>
        new(CollectionHandleKinds.Counters, "collect_events", async token =>
            new CollectorResult(await new EventPipeCounterCollector().CollectAsync(
                processId, window, providers: ["System.Runtime"], intervalSeconds: 1,
                cancellationToken: token)));

    private static async Task<ArtifactExecution> CollectArtifactAsync(
        int processId,
        CollectorJob job,
        OperatingEnvelopeStorageMode mode,
        SqliteCaptureStore store,
        string storeRoot,
        OperatingEnvelopeConfiguration config,
        CancellationToken cancellationToken)
    {
        var collectionMilliseconds = 0d;
        double? collectorDrainMilliseconds = null;
        var notes = new List<string>();
        try
        {
            if (mode == OperatingEnvelopeStorageMode.Ephemeral)
            {
                var collectionStarted = Stopwatch.GetTimestamp();
                var collected = await job.Collect(cancellationToken);
                collectionMilliseconds = ElapsedMilliseconds(collectionStarted);
                collectorDrainMilliseconds = collected.DrainMilliseconds;
                AddEphemeralNotes(notes);
                AddCollectorNotes(notes, collected.Snapshot);
                if (collectorDrainMilliseconds is null)
                {
                    notes.Add("Collector API exposes operation elapsed time only; drain was not separately measured.");
                }
                var ephemeral = new OperatingEnvelopeArtifactMeasurement(
                    job.Kind, true, null, null,
                    OperatingEnvelopeCaptureAccounting.Ephemeral(
                        "No durable observation sink is active; producer offered/admitted/loss populations are unavailable."),
                    null, null, [], collectionMilliseconds, null, null, null, null,
                    notes.AsReadOnly(), null, GetTargetGcCounters(collected.Snapshot),
                    collectorDrainMilliseconds);
                return new(ephemeral, null, mode, store, storeRoot, config);
            }

            var totalStarted = Stopwatch.GetTimestamp();
            var handles = new MemoryDiagnosticHandleStore();
            var useCases = new DurableCaptureUseCases(store, handles, new CaptureStoreOptions());
            object? capturedSnapshot = null;
            var result = await useCases.CaptureAsync<object>(
                $"Operating envelope {job.Kind}",
                job.Kind,
                Owner,
                async token =>
                {
                    var collectionStarted = Stopwatch.GetTimestamp();
                    var collected = await job.Collect(token);
                    capturedSnapshot = collected.Snapshot;
                    collectorDrainMilliseconds = collected.DrainMilliseconds;
                    collectionMilliseconds = ElapsedMilliseconds(collectionStarted);
                    AddCollectorNotes(notes, capturedSnapshot);
                    var handle = handles.RegisterWithMetadata(
                        processId, job.Kind, capturedSnapshot, TimeSpan.FromMinutes(10),
                        evictWhenProcessExits: false, origin: HandleOrigin.Live,
                        producingTool: job.ProducingTool);
                    return DiagnosticResult.OkWithHandle<object>(
                        capturedSnapshot, "Operating-envelope collector result.", handle.Id, handle.ExpiresAt);
                },
                cancellationToken);

            var captureInfo = result.Capture;
            var quality = captureInfo?.Quality;
            var captureAccounting = quality is null
                ? OperatingEnvelopeCaptureAccounting.Ephemeral("Durable capture did not return quality metadata.")
                : OperatingEnvelopeCaptureAccounting.FromDurableQuality(
                    quality.Offered,
                    quality.Accepted,
                    quality.Persisted,
                    quality.RecordRejected,
                    quality.QueueRejected,
                    quality.StorageRejected,
                    quality.Pending,
                    quality.SourceRejected,
                    quality.UnknownTail || quality.Interrupted,
                    "CaptureQuality counters cover persistence admission; EventPipe source-offered demand is not exposed.");

            if (quality is not null)
            {
                AddQualityNotes(notes, quality);
            }

            var collectionSucceeded = !result.IsError && !result.Cancelled && captureInfo?.State == CaptureState.Sealed;
            if (captureInfo?.State != CaptureState.Sealed)
            {
                notes.Add("Read-only query was not run because the capture did not seal.");
            }

            var totalMilliseconds = ElapsedMilliseconds(totalStarted);
            if (result.Error is { } diagnosticError)
            {
                notes.Add($"{diagnosticError.Kind}: {diagnosticError.Message}");
            }
            if (collectorDrainMilliseconds is null)
            {
                notes.Add("Collector API exposes operation elapsed time only; drain was not separately measured.");
            }

            var measurement = new OperatingEnvelopeArtifactMeasurement(
                job.Kind,
                collectionSucceeded,
                captureInfo?.CaptureId,
                captureInfo?.State.ToString(),
                captureAccounting,
                quality?.Persisted,
                null,
                [],
                collectionMilliseconds,
                Math.Max(0, totalMilliseconds - collectionMilliseconds),
                null,
                null,
                null,
                notes.AsReadOnly(),
                result.Error?.Message,
                GetTargetGcCounters(capturedSnapshot),
                collectorDrainMilliseconds,
                null);
            return new(measurement, captureInfo, mode, store, storeRoot, config);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var measurement = new OperatingEnvelopeArtifactMeasurement(
                job.Kind, false, null, null,
                OperatingEnvelopeCaptureAccounting.Ephemeral("Collection was cancelled; final source and persistence tail are unknown."),
                null, null, [], collectionMilliseconds, null, null, null, null,
                notes.AsReadOnly(), "Collection was cancelled by the absolute trial deadline.");
            return new(measurement, null, mode, store, storeRoot, config);
        }
        catch (Exception ex)
        {
            notes.Add($"{ex.GetType().Name}: {ex.Message}");
            var measurement = new OperatingEnvelopeArtifactMeasurement(
                job.Kind, false, null, null,
                OperatingEnvelopeCaptureAccounting.Ephemeral(
                    "Collection failed before complete source and persistence accounting was available."),
                null, null, [], collectionMilliseconds, null, null, null, null,
                notes.AsReadOnly(), $"{ex.GetType().Name}: {ex.Message}");
            return new(measurement, null, mode, store, storeRoot, config);
        }
    }

    private static async Task<ArtifactExecution> CollectSweepAsync(
        int processId,
        OperatingEnvelopeStorageMode mode,
        SqliteCaptureStore store,
        string storeRoot,
        OperatingEnvelopeConfiguration config,
        CancellationToken cancellationToken)
    {
        var totalStarted = Stopwatch.GetTimestamp();
        var collectionStarted = 0L;
        var handles = new MemoryDiagnosticHandleStore();
        var resolver = new FixedProcessContextResolver(processId);
        var collectionMilliseconds = 0d;
        var notes = new List<string>();
        try
        {
            DiagnosticResult<SweepResult> result;
            if (mode == OperatingEnvelopeStorageMode.Durable)
            {
                var useCases = new DurableCaptureUseCases(store, handles, new CaptureStoreOptions());
                result = await useCases.CaptureAsync("Operating envelope mixed sweep", "sweep", Owner,
                    async token =>
                    {
                        collectionStarted = Stopwatch.GetTimestamp();
                        var captured = await RunSweepAsync(processId, config, resolver, handles, token);
                        collectionMilliseconds = ElapsedMilliseconds(collectionStarted);
                        return captured;
                    }, cancellationToken);
            }
            else
            {
                collectionStarted = Stopwatch.GetTimestamp();
                result = await RunSweepAsync(processId, config, resolver, handles, cancellationToken);
                collectionMilliseconds = ElapsedMilliseconds(collectionStarted);
                AddEphemeralNotes(notes);
            }

            if (result.Data?.Failures is { Count: > 0 } failures)
            {
                notes.AddRange(failures);
            }

            var info = result.Capture;
            var quality = info?.Quality;
            var accounting = quality is null
                ? OperatingEnvelopeCaptureAccounting.Ephemeral(
                    "Ephemeral sweep does not expose durable offered/admitted/loss counts.")
                : OperatingEnvelopeCaptureAccounting.FromDurableQuality(
                    quality.Offered, quality.Accepted, quality.Persisted, quality.RecordRejected,
                    quality.QueueRejected, quality.StorageRejected, quality.Pending, quality.SourceRejected,
                    quality.UnknownTail || quality.Interrupted,
                    "CaptureQuality counts the composed store pipeline; individual EventPipe offered counts are unavailable.");
            if (quality is not null)
            {
                AddQualityNotes(notes, quality);
            }

            var success = !result.IsError && !result.Cancelled &&
                result.Data?.Failures.Count == 0 &&
                (mode == OperatingEnvelopeStorageMode.Ephemeral || info?.State == CaptureState.Sealed);
            var totalMilliseconds = ElapsedMilliseconds(totalStarted);
            var measurement = new OperatingEnvelopeArtifactMeasurement(
                "sweep", success, info?.CaptureId, info?.State.ToString(), accounting,
                quality?.Persisted, null, [], collectionMilliseconds,
                mode == OperatingEnvelopeStorageMode.Durable
                    ? Math.Max(0, totalMilliseconds - collectionMilliseconds)
                    : null,
                null, null, null, notes.AsReadOnly(), result.Error?.Message,
                GetTargetGcCounters(result.Data?.Counters));
            return new(measurement, info, mode, store, storeRoot, config);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var measurement = new OperatingEnvelopeArtifactMeasurement(
                "sweep", false, null, null,
                OperatingEnvelopeCaptureAccounting.Ephemeral("Sweep was cancelled; final source and persistence tail are unknown."),
                null, null, [], collectionMilliseconds, null, null, null, null,
                notes.AsReadOnly(), "Sweep was cancelled by the absolute trial deadline.");
            return new(measurement, null, mode, store, storeRoot, config);
        }
        catch (Exception ex)
        {
            notes.Add($"{ex.GetType().Name}: {ex.Message}");
            var measurement = new OperatingEnvelopeArtifactMeasurement(
                "sweep", false, null, null,
                OperatingEnvelopeCaptureAccounting.Ephemeral("Sweep failed before complete accounting was available."),
                null, null, [], collectionMilliseconds, null, null, null, null,
                notes.AsReadOnly(), $"{ex.GetType().Name}: {ex.Message}");
            return new(measurement, null, mode, store, storeRoot, config);
        }
    }

    private static async Task<OperatingEnvelopeArtifactMeasurement> MeasureStoredArtifactAsync(
        ArtifactExecution execution,
        Action checkDeadline,
        CancellationToken cancellationToken)
    {
        checkDeadline();
        var measurement = execution.Measurement;
        var capture = execution.Capture;
        if (execution.StorageMode != OperatingEnvelopeStorageMode.Durable || capture is null)
        {
            return measurement;
        }

        var notes = measurement.LossAndCapNotes.ToList();
        var files = Array.Empty<OperatingEnvelopeArtifactHash>();
        var evidenceHashMilliseconds = 0d;
        var queryOpenMilliseconds = (double?)null;
        var queryMilliseconds = (double?)null;
        var queryCloseMilliseconds = (double?)null;
        long? queriedRecords = null;
        var succeeded = measurement.CollectionSucceeded;
        string? error = measurement.Error;

        try
        {
            var beforeHashStarted = Stopwatch.GetTimestamp();
            var beforeQuery = await HashCaptureFilesAsync(
                execution.StoreRoot, capture.CaptureId, checkDeadline, cancellationToken);
            evidenceHashMilliseconds += ElapsedMilliseconds(beforeHashStarted);
            if (capture.State == CaptureState.Sealed)
            {
                try
                {
                    var query = await QueryCaptureAsync(
                        execution.Store, capture, checkDeadline, cancellationToken);
                    queryOpenMilliseconds = query.OpenMilliseconds;
                    queryMilliseconds = query.QueryMilliseconds;
                    queryCloseMilliseconds = query.CloseMilliseconds;
                    queriedRecords = query.Records;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is CaptureStoreException or IOException or TimeoutException)
                {
                    succeeded = false;
                    error ??= $"{ex.GetType().Name}: {ex.Message}";
                    notes.Add($"Read-only verification query failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                notes.Add("Read-only query was not run because the capture did not seal.");
            }

            var afterHashStarted = Stopwatch.GetTimestamp();
            files = await HashCaptureFilesAsync(
                execution.StoreRoot, capture.CaptureId, checkDeadline, cancellationToken);
            evidenceHashMilliseconds += ElapsedMilliseconds(afterHashStarted);
            if (capture.State == CaptureState.Sealed && !beforeQuery.SequenceEqual(files))
            {
                succeeded = false;
                notes.Add("Read-only query changed the sealed store package hash inventory.");
            }
            if (capture.State == CaptureState.Sealed &&
                measurement.Accounting.Persisted is { } persisted &&
                queriedRecords is { } queried &&
                persisted != queried)
            {
                succeeded = false;
                notes.Add($"CaptureQuality persisted={persisted} differs from read-only queried records={queried}.");
            }
            if (files.Length == 0)
            {
                succeeded = false;
                notes.Add("Capture package has no hashable files.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is CaptureStoreException or IOException)
        {
            succeeded = false;
            error ??= $"{ex.GetType().Name}: {ex.Message}";
            notes.Add($"Capture package hashing failed: {ex.GetType().Name}: {ex.Message}");
        }

        long? storeBytes = null;
        if (files.Length > 0)
        {
            long totalBytes = 0;
            foreach (var file in files)
            {
                checkDeadline();
                totalBytes = checked(totalBytes + new FileInfo(Path.Combine(
                    execution.StoreRoot, file.Path.Replace('/', Path.DirectorySeparatorChar))).Length);
                checkDeadline();
            }

            storeBytes = totalBytes;
        }

        return measurement with
        {
            CollectionSucceeded = succeeded,
            StoreBytes = storeBytes,
            StoreFiles = files,
            QueryOpenMilliseconds = queryOpenMilliseconds,
            QueryMilliseconds = queryMilliseconds,
            QueryCloseMilliseconds = queryCloseMilliseconds,
            QueriedRecords = queriedRecords,
            LossAndCapNotes = notes.AsReadOnly(),
            Error = error,
            EvidenceHashMilliseconds = evidenceHashMilliseconds,
        };
    }

    private static Task<DiagnosticResult<SweepResult>> RunSweepAsync(
        int processId,
        OperatingEnvelopeConfiguration config,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        CancellationToken cancellationToken) =>
        SweepUseCase.RunSweep(
            new EventPipeCounterCollector(),
            new EventPipeGcCollector(),
            new EventPipeExceptionCollector(),
            new EventPipeThreadPoolCollector(),
            new ProcessResourcesCollector(),
            resolver,
            handles,
            processId,
            durationSeconds: checked((int)Math.Ceiling(config.WindowDuration.TotalSeconds)),
            cancellationToken: cancellationToken);

    private static async Task<QueryMeasurement> QueryCaptureAsync(
        SqliteCaptureStore store,
        CaptureInfo capture,
        Action checkDeadline,
        CancellationToken cancellationToken)
    {
        checkDeadline();
        cancellationToken.ThrowIfCancellationRequested();
        var openStarted = Stopwatch.GetTimestamp();
        var reader = await store.OpenAsync(capture.CaptureId, Owner, cancellationToken);
        var openMilliseconds = ElapsedMilliseconds(openStarted);
        checkDeadline();
        var queryStarted = Stopwatch.GetTimestamp();
        long records = 0;
        var closeMilliseconds = 0d;
        var queryMilliseconds = 0d;
        try
        {
            foreach (var artifact in capture.Artifacts)
            {
                checkDeadline();
                cancellationToken.ThrowIfCancellationRequested();
                _ = reader.ReadSnapshot(artifact.ArtifactId);
                checkDeadline();
                long after = 0;
                OperatingEnvelopeQueryPageRunner.ReadAll(
                    () => reader.Query(new CaptureRecordQuery(
                        artifact.ArtifactId, AfterRecordId: after, PageSize: 256)),
                    page => page.NextAfterRecordId is not null,
                    page =>
                    {
                        records += page.Records.Count;
                        if (page.NextAfterRecordId is not { } next)
                        {
                            return;
                        }

                        if (next <= after)
                        {
                            throw new InvalidDataException("Read-only record query did not advance its keyset cursor.");
                        }

                        after = next;
                    },
                    checkDeadline,
                    cancellationToken);
            }

            checkDeadline();
            cancellationToken.ThrowIfCancellationRequested();
            queryMilliseconds = ElapsedMilliseconds(queryStarted);
        }
        finally
        {
            var closeStarted = Stopwatch.GetTimestamp();
            reader.Dispose();
            closeMilliseconds = ElapsedMilliseconds(closeStarted);
        }

        checkDeadline();
        return new(openMilliseconds, queryMilliseconds, records, closeMilliseconds);
    }

    private static async Task<OperatingEnvelopeArtifactHash[]> HashCaptureFilesAsync(
        string storeRoot,
        string captureId,
        Action checkDeadline,
        CancellationToken cancellationToken)
    {
        checkDeadline();
        var package = Path.Combine(storeRoot, "captures", captureId);
        if (!Directory.Exists(package))
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
        {
            checkDeadline();
            cancellationToken.ThrowIfCancellationRequested();
            paths.Add(path);
        }

        checkDeadline();
        paths.Sort(StringComparer.Ordinal);
        checkDeadline();
        var hashes = new List<OperatingEnvelopeArtifactHash>(paths.Count);
        foreach (var path in paths)
        {
            checkDeadline();
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            checkDeadline();
            var digest = await OperatingEnvelopeEvidenceHasher.HashAsync(
                stream, checkDeadline, cancellationToken);
            hashes.Add(new(
                Path.GetRelativePath(storeRoot, path).Replace(Path.DirectorySeparatorChar, '/'), digest));
        }

        return hashes.ToArray();
    }

    private static async Task<TargetLoadResult> SendWarmupRequestsAsync(
        LiveSampleProcess sample,
        OperatingEnvelopeTrialPlan plan,
        int targetIndex,
        OperatingEnvelopeConfiguration config,
        OperatingEnvelopeRequestProgress progress,
        CancellationToken cancellationToken)
    {
        var planned = WarmupRequestSlots(config);
        using var client = CreateClient(sample.BaseUrl);
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < planned; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var due = started + ToStopwatchTicks(TimeSpan.FromSeconds(index));
            try
            {
                await DelayUntilAsync(due, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            progress.MarkOffered(index);
            var requestStarted = Stopwatch.GetTimestamp();
            try
            {
                var paths = plan.MeasuredRequestPaths.Count == 0
                    ? ["/weatherforecast"]
                    : plan.MeasuredRequestPaths;
                var path = paths[(index + targetIndex) % paths.Count];
                using var response = await client.GetAsync(path,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                progress.Complete(
                    index,
                    response.IsSuccessStatusCode
                        ? OperatingEnvelopeRequestOutcome.Admitted
                        : OperatingEnvelopeRequestOutcome.Rejected,
                    ElapsedMilliseconds(requestStarted));
            }
            catch (HttpRequestException ex)
            {
                progress.Complete(index, OperatingEnvelopeRequestOutcome.Unknown,
                    ElapsedMilliseconds(requestStarted));
                progress.AddNote($"Warmup request {index}: {ex.GetType().Name}: {ex.Message}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                progress.Complete(index, OperatingEnvelopeRequestOutcome.Unknown,
                    ElapsedMilliseconds(requestStarted));
                progress.AddNote($"Warmup request {index} outcome is unknown because the trial deadline expired.");
                break;
            }
        }

        var elapsed = ElapsedMilliseconds(started);
        var snapshot = progress.Snapshot(elapsed);
        return new(sample.ProcessId, snapshot.Accounting, snapshot.Notes);
    }

    private static async Task<TargetLoadResult> DriveMeasuredRequestsAsync(
        LiveSampleProcess sample,
        int targetIndex,
        OperatingEnvelopeTrialPlan plan,
        OperatingEnvelopeConfiguration config,
        OperatingEnvelopeRequestProgress progress,
        CancellationToken cellCancellationToken)
    {
        var planned = MeasuredRequestSlots(config);
        if (plan.MeasuredRequestPaths.Count == 0)
        {
            return new(sample.ProcessId, progress.Snapshot(config.WindowDuration.TotalMilliseconds).Accounting, []);
        }

        var start = Stopwatch.GetTimestamp();
        var end = start + ToStopwatchTicks(config.WindowDuration);
        using var windowDeadline = CreateDeadline(end);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cellCancellationToken, windowDeadline.Token);
        using var client = CreateClient(sample.BaseUrl);
        var intervalTicks = ToStopwatchTicks(config.RequestInterval);

        for (var slot = 0; slot < planned; slot++)
        {
            var due = start + slot * intervalTicks;
            if (due >= end)
            {
                break;
            }

            if (Stopwatch.GetTimestamp() - due > intervalTicks / 2)
            {
                continue;
            }

            if (cellCancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await DelayUntilAsync(due, linked.Token);
            }
            catch (OperationCanceledException) when (cellCancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (windowDeadline.IsCancellationRequested)
            {
                break;
            }

            if (Stopwatch.GetTimestamp() >= end)
            {
                break;
            }

            var path = plan.MeasuredRequestPaths[(slot + targetIndex) % plan.MeasuredRequestPaths.Count];
            progress.MarkOffered(slot);
            var requestStarted = Stopwatch.GetTimestamp();
            try
            {
                using var response = await client.GetAsync(path,
                    HttpCompletionOption.ResponseHeadersRead, linked.Token);
                progress.Complete(
                    slot,
                    response.IsSuccessStatusCode
                        ? OperatingEnvelopeRequestOutcome.Admitted
                        : OperatingEnvelopeRequestOutcome.Rejected,
                    ElapsedMilliseconds(requestStarted));
            }
            catch (OperationCanceledException) when (windowDeadline.IsCancellationRequested &&
                                                     !cellCancellationToken.IsCancellationRequested)
            {
                progress.Complete(slot, OperatingEnvelopeRequestOutcome.Unknown,
                    ElapsedMilliseconds(requestStarted));
                break;
            }
            catch (OperationCanceledException) when (cellCancellationToken.IsCancellationRequested)
            {
                progress.Complete(slot, OperatingEnvelopeRequestOutcome.Unknown,
                    ElapsedMilliseconds(requestStarted));
                progress.AddNote($"Request slot {slot} outcome is unknown because the trial deadline expired.");
                break;
            }
            catch (HttpRequestException ex)
            {
                progress.Complete(slot, OperatingEnvelopeRequestOutcome.Unknown,
                    ElapsedMilliseconds(requestStarted));
                var progressSnapshot = progress.Snapshot(config.WindowDuration.TotalMilliseconds);
                if (progressSnapshot.Notes.Count < config.MaximumRequestsPerCell)
                {
                    progress.AddNote($"Request slot {slot}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        var elapsed = Math.Min(config.WindowDuration.TotalMilliseconds, ElapsedMilliseconds(start));
        var snapshot = progress.Snapshot(elapsed);
        return new(sample.ProcessId, snapshot.Accounting, snapshot.Notes);
    }

    private static HttpClient CreateClient(string baseUrl) => new()
    {
        BaseAddress = new Uri(baseUrl),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static async Task DelayUntilAsync(long timestamp, CancellationToken cancellationToken)
    {
        var remaining = timestamp - Stopwatch.GetTimestamp();
        if (remaining > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency), cancellationToken);
        }
    }

    private static void AddEphemeralNotes(List<string> notes) =>
        notes.Add("Ephemeral collection does not expose durable source-offered/admission/cap counters; null remains unknown.");

    private static void AddCollectorNotes(List<string> notes, object? snapshot)
    {
        var collectorNotes = snapshot switch
        {
            CounterSnapshot counters => counters.Notes,
            DiagnosticResult<SweepResult> sweep => GetSweepNotes(sweep.Data),
            SweepResult sweep => GetSweepNotes(sweep),
            ActivityCapture activity => ActivityNotes(activity),
            ThreadPoolEventSnapshot threadPool => ThreadPoolNotes(threadPool),
            ThreadSnapshotArtifact threads => threads.Warnings ?? [],
            CpuSampleTraceArtifact cpu => cpu.Notes,
            AllocationSampleArtifact allocation => allocation.TraceArtifact.Notes,
            GcSummary gc => GcNotes(gc),
            ExceptionSnapshot exceptions => ExceptionNotes(exceptions),
            _ => [],
        };
        foreach (var note in collectorNotes)
        {
            if (!notes.Contains(note, StringComparer.Ordinal))
            {
                notes.Add(note);
            }
        }
    }

    private static IReadOnlyList<string> GetSweepNotes(SweepResult? sweep)
    {
        if (sweep is null)
        {
            return [];
        }

        var notes = new List<string>(sweep.Failures);
        if (sweep.Gc is { } gc)
        {
            notes.AddRange(GcNotes(gc));
        }
        if (sweep.Exceptions is { } exceptions)
        {
            notes.AddRange(ExceptionNotes(exceptions));
        }
        if (sweep.ThreadPool is { } threadPool)
        {
            notes.AddRange(ThreadPoolNotes(threadPool));
        }
        if (sweep.Counters is { } counters)
        {
            notes.AddRange(counters.Notes);
        }

        return notes;
    }

    private static IReadOnlyList<string> GcNotes(GcSummary gc)
    {
        var notes = gc.GetQuality().Limitations.Select(limitation => limitation.Detail).ToList();
        if (gc.DroppedEvents > 0) notes.Add($"gcDroppedEvents={gc.DroppedEvents}");
        if (gc.DroppedHeapStats > 0) notes.Add($"gcDroppedHeapStats={gc.DroppedHeapStats}");
        return notes;
    }

    private static IReadOnlyList<string> ThreadPoolNotes(ThreadPoolEventSnapshot threadPool)
    {
        var notes = threadPool.Notes.ToList();
        notes.AddRange(ThreadPoolEvidence.GetQuality(threadPool).Limitations.Select(limitation => limitation.Detail));
        return notes;
    }

    private static IReadOnlyList<string> ExceptionNotes(ExceptionSnapshot exceptions) =>
        exceptions.TotalExceptions > exceptions.Recent.Count
            ? [$"exceptionRecentCap={exceptions.RecentCap}; omitted={exceptions.TotalExceptions - exceptions.Recent.Count}"]
            : [];

    private static IReadOnlyList<string> ActivityNotes(ActivityCapture activity)
    {
        var notes = new List<string>();
        if (activity.Observation is { } observation)
        {
            notes.Add($"activityEventsLost={observation.EventsLost}; completion={observation.Completion}");
        }

        if (activity.Retention is { } retention && retention.DroppedMatchingActivities is > 0)
        {
            notes.Add($"activityDroppedMatchingActivities={retention.DroppedMatchingActivities}");
        }

        return notes;
    }

    private static void AddQualityNotes(List<string> notes, CaptureQuality quality)
    {
        if (quality.RecordRejected != 0) notes.Add($"recordRejected={quality.RecordRejected}");
        if (quality.QueueRejected != 0) notes.Add($"queueRejected={quality.QueueRejected}");
        if (quality.StorageRejected != 0) notes.Add($"storageRejected={quality.StorageRejected}");
        if (quality.SnapshotRejected != 0) notes.Add($"snapshotRejected={quality.SnapshotRejected}");
        if (quality.Pending != 0) notes.Add($"pending={quality.Pending}");
        if (quality.SourceRejected is null)
            notes.Add("sourceRejected is unavailable; source-side loss is unknown, not zero.");
        else if (quality.SourceRejected != 0)
            notes.Add($"sourceRejected={quality.SourceRejected}");
        if (quality.UnknownTail || quality.Interrupted)
            notes.Add($"unknownTail={quality.UnknownTail}; interrupted={quality.Interrupted}");
        var accounted = quality.Persisted + quality.RecordRejected + quality.QueueRejected +
            quality.StorageRejected + quality.Pending;
        if (!quality.UnknownTail && quality.Offered != accounted)
        {
            notes.Add($"CaptureQuality stage accounting differs by {quality.Offered - accounted}; unknown count is not assumed to be zero.");
        }
    }

    private static IReadOnlyDictionary<string, double?> GetTargetGcCounters(object? snapshot)
    {
        var counters = snapshot switch
        {
            CounterSnapshot counterSnapshot => counterSnapshot.Counters,
            SweepResult { Counters: { } counterSnapshot } => counterSnapshot.Counters,
            _ => [],
        };
        return counters
            .Where(counter =>
                counter.Name.Contains("gc", StringComparison.OrdinalIgnoreCase) ||
                counter.Name.Contains("gen-", StringComparison.OrdinalIgnoreCase) ||
                counter.Name.Contains("loh", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                counter => $"{counter.Provider}/{counter.Name}",
                counter => (double?)counter.Value,
                StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> GetInputHashes()
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var sample = SampleLocator.LocateSampleDll("CoreClrSample")
            ?? throw new InvalidOperationException("CoreClrSample.dll must be built before the opt-in live harness runs.");
        AddHash(sample);
        AddHash(typeof(EventPipeCounterCollector).Assembly.Location);
        AddHash(typeof(OperatingEnvelopeProtocol).Assembly.Location);
        AddHash(typeof(OperatingEnvelopeAcceptanceTests).Assembly.Location);
        return paths;

        void AddHash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            paths[Path.GetFileName(path)] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }

    private static OperatingEnvelopeTrialResult CreateNotRunResult(
        OperatingEnvelopeTrialPlan plan,
        string configurationHash,
        OperatingEnvelopeConfiguration configuration,
        OperatingEnvelopeStopOutcome stopOutcome)
    {
        var planned = plan.TargetCount * MeasuredRequestSlots(configuration);
        var requests = new OperatingEnvelopeRequestAccounting(
            planned, 0, 0, 0, 0, planned,
            configuration.WindowDuration.TotalMilliseconds, []);
        var warmupPlanned = plan.TargetCount * WarmupRequestSlots(configuration);
        var warmup = new OperatingEnvelopeRequestAccounting(
            warmupPlanned, 0, 0, 0, 0, warmupPlanned, 0, []);
        return new(
            plan, configurationHash, OperatingEnvelopeTrialOutcome.Stopped, stopOutcome,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, warmup, requests, [],
            [], EmptyDiagnosticResources("Trial was not started."), [], 0, true, [],
            ["Not run; no target, collector, or store operation was started."], stopOutcome.ToString());
    }

    private static OperatingEnvelopeRequestAccounting EmptyRequests(int planned, double durationMilliseconds) =>
        new(planned, 0, 0, 0, 0, planned, durationMilliseconds, []);

    private static OperatingEnvelopeRequestAccounting CombineRequests(
        IReadOnlyList<OperatingEnvelopeRequestAccounting> requests,
        double? durationMilliseconds = null)
    {
        var latencies = requests.SelectMany(request => request.LatencyMilliseconds).ToArray();
        return new OperatingEnvelopeRequestAccounting(
            requests.Sum(request => request.Planned),
            requests.Sum(request => request.Offered),
            requests.Sum(request => request.Admitted),
            requests.Sum(request => request.Rejected),
            requests.Sum(request => request.Unknown),
            requests.Sum(request => request.NotOffered),
            durationMilliseconds ?? requests.Max(request => request.MeasurementWindowMilliseconds),
            latencies).Validate();
    }

    private static int WarmupRequestSlots(OperatingEnvelopeConfiguration config) =>
        config.WarmupDuration == TimeSpan.Zero
            ? 0
            : checked((int)Math.Ceiling(config.WarmupDuration.TotalSeconds));

    private static int MeasuredRequestSlots(OperatingEnvelopeConfiguration config) =>
        checked((int)Math.Ceiling(config.WindowDuration.TotalMilliseconds / config.RequestInterval.TotalMilliseconds));

    private static OperatingEnvelopeDiagnosticResources EmptyDiagnosticResources(string note) =>
        new(null, null, null, null, null, null, [note]);

    private static long GetDirectoryBytes(
        string directory,
        Action checkDeadline,
        CancellationToken cancellationToken)
    {
        checkDeadline();
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            checkDeadline();
            cancellationToken.ThrowIfCancellationRequested();
            bytes = checked(bytes + new FileInfo(path).Length);
            checkDeadline();
        }

        return bytes;
    }

    private static CancellationTokenSource CreateDeadline(long deadlineTimestamp)
    {
        var remaining = deadlineTimestamp - Stopwatch.GetTimestamp();
        var duration = remaining <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency);
        return new CancellationTokenSource(duration);
    }

    private static long ToStopwatchTicks(TimeSpan duration) =>
        checked((long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency));

    private static double ElapsedMilliseconds(long startedTimestamp) =>
        (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency;

    private sealed record CollectorJob(
        string Kind,
        string ProducingTool,
        Func<CancellationToken, Task<CollectorResult>> Collect);
    private sealed record CollectorResult(object Snapshot, double? DrainMilliseconds = null);
    private sealed record ArtifactExecution(
        OperatingEnvelopeArtifactMeasurement Measurement,
        CaptureInfo? Capture,
        OperatingEnvelopeStorageMode StorageMode,
        SqliteCaptureStore Store,
        string StoreRoot,
        OperatingEnvelopeConfiguration Configuration);
    private sealed record QueryMeasurement(
        double OpenMilliseconds,
        double QueryMilliseconds,
        long Records,
        double CloseMilliseconds);
    private sealed record TargetLoadResult(
        int ProcessId,
        OperatingEnvelopeRequestAccounting Accounting,
        IReadOnlyList<string> Notes);
    private sealed record RootProvider(string Root) : IArtifactRootProvider;

    private sealed class FixedProcessContextResolver(int processId) : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(
            int? requestedProcessId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProcessContextResolution(
                new ProcessContext(requestedProcessId ?? processId, RuntimeFlavor.CoreClr,
                    CanSampleCpu: true, CanCollectGcDump: true, AutoResolved: false), null));
        }
    }

    private sealed class OperatingEnvelopeResourceMonitor : IDisposable
    {
        private readonly List<ProcessSample> _targets = [];
        private readonly List<string> _notes = [];
        private CancellationTokenSource? _stop;
        private Task? _sampling;
        private Process? _diagnosticProcess;
        private TimeSpan _diagnosticCpuStart;
        private long _diagnosticRssPeak;
        private long _diagnosticAllocatedStart;
        private readonly int[] _diagnosticGcStart = new int[3];

        public IReadOnlyList<string> Notes => _notes.AsReadOnly();
        public Task SamplingTask => _sampling
            ?? throw new InvalidOperationException("Resource sampling has not started.");

        public IReadOnlyList<OperatingEnvelopeTargetResources> Targets =>
            _targets.Select(sample => new OperatingEnvelopeTargetResources(
                sample.ProcessId,
                sample.CpuStart is { } cpuStart && sample.CpuEnd is { } cpuEnd
                    ? Math.Max(0, (cpuEnd - cpuStart).TotalMilliseconds)
                    : null,
                sample.PeakWorkingSetBytes,
                sample.Notes.AsReadOnly())).ToArray();

        public void Start(IReadOnlyList<LiveSampleProcess> samples)
        {
            if (_sampling is not null)
            {
                throw new InvalidOperationException("Resource sampling was already started.");
            }

            _targets.AddRange(samples.Select(sample => new ProcessSample(sample.Process)));
            _diagnosticProcess = Process.GetCurrentProcess();
            _diagnosticProcess.Refresh();
            _diagnosticCpuStart = _diagnosticProcess.TotalProcessorTime;
            _diagnosticRssPeak = _diagnosticProcess.WorkingSet64;
            _diagnosticAllocatedStart = GC.GetTotalAllocatedBytes(precise: false);
            for (var generation = 0; generation < _diagnosticGcStart.Length; generation++)
            {
                _diagnosticGcStart[generation] = GC.CollectionCount(generation);
            }

            _stop = new CancellationTokenSource();
            _sampling = SampleUntilStoppedAsync(_stop.Token);
        }

        public async Task<OperatingEnvelopeDiagnosticResources> StopAsync(long settlementDeadlineTimestamp)
        {
            if (_sampling is null || _stop is null || _diagnosticProcess is null)
            {
                return EmptyDiagnosticResources("Resource sampling did not start.");
            }

            RequestStop();
            var settlement = await OperatingEnvelopeTaskSettlement.SettleAsync(
                [_sampling], settlementDeadlineTimestamp);
            if (!settlement.Settled)
            {
                throw new TimeoutException("The diagnostic resource sampler did not settle before its cleanup deadline.");
            }
            if (settlement.Faults.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The diagnostic resource sampler faulted: {string.Join("; ", settlement.Faults)}");
            }

            Sample();
            _diagnosticProcess.Refresh();
            var result = new OperatingEnvelopeDiagnosticResources(
                Math.Max(0, (_diagnosticProcess.TotalProcessorTime - _diagnosticCpuStart).TotalMilliseconds),
                _diagnosticRssPeak,
                Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - _diagnosticAllocatedStart),
                GC.CollectionCount(0) - _diagnosticGcStart[0],
                GC.CollectionCount(1) - _diagnosticGcStart[1],
                GC.CollectionCount(2) - _diagnosticGcStart[2],
                _notes.AsReadOnly());
            _diagnosticProcess.Dispose();
            _diagnosticProcess = null;
            _stop.Dispose();
            _stop = null;
            return result;
        }

        public void RequestStop() => _stop?.Cancel();

        public void Dispose()
        {
            if (_sampling is { IsCompleted: false } sampling)
            {
                var stop = _stop;
                var diagnosticProcess = _diagnosticProcess;
                _stop = null;
                _diagnosticProcess = null;
                _ = sampling.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    diagnosticProcess?.Dispose();
                    stop?.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            _stop?.Dispose();
            _diagnosticProcess?.Dispose();
        }

        private async Task SampleUntilStoppedAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Sample();
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
        }

        private void Sample()
        {
            foreach (var target in _targets)
            {
                try
                {
                    target.Process.Refresh();
                    if (target.Process.HasExited)
                    {
                        AddNoteOnce(target.Notes, "Target exited while resources were being sampled.");
                        continue;
                    }

                    target.CpuEnd = target.Process.TotalProcessorTime;
                    target.PeakWorkingSetBytes = Math.Max(
                        target.PeakWorkingSetBytes ?? 0, target.Process.WorkingSet64);
                    target.CpuStart ??= target.CpuEnd;
                }
                catch (InvalidOperationException ex)
                {
                    AddNoteOnce(target.Notes, $"Target process metrics unavailable: {ex.Message}");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    AddNoteOnce(target.Notes, $"Target process metrics unavailable: {ex.Message}");
                }
            }

            try
            {
                _diagnosticProcess?.Refresh();
                if (_diagnosticProcess is { HasExited: false })
                {
                    _diagnosticRssPeak = Math.Max(_diagnosticRssPeak, _diagnosticProcess.WorkingSet64);
                }
            }
            catch (InvalidOperationException ex)
            {
                AddNoteOnce(_notes, $"Diagnostic-process metrics unavailable: {ex.Message}");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                AddNoteOnce(_notes, $"Diagnostic-process metrics unavailable: {ex.Message}");
            }
        }

        private static void AddNoteOnce(List<string> notes, string note)
        {
            if (!notes.Contains(note, StringComparer.Ordinal))
            {
                notes.Add(note);
            }
        }

        private sealed class ProcessSample(Process process)
        {
            public Process Process { get; } = process;
            public int ProcessId { get; } = process.Id;
            public TimeSpan? CpuStart { get; set; }
            public TimeSpan? CpuEnd { get; set; }
            public long? PeakWorkingSetBytes { get; set; }
            public List<string> Notes { get; } = [];
        }
    }

    private sealed class OperatingEnvelopeFactAttribute : FactAttribute
    {
        public OperatingEnvelopeFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("DOTNET_DBG_MCP_OPERATING_ENVELOPE"),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = "Set DOTNET_DBG_MCP_OPERATING_ENVELOPE=1 to run the multi-minute live operating-envelope matrix.";
            }
        }
    }
}
