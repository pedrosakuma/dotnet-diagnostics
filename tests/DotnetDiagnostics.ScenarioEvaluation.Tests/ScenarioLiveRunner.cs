using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Signals;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.TestSupport;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class ScenarioLiveRunner
{
    private const int MaxNotes = 20;

    public static async Task<ScenarioEvidence> CaptureAsync(
        ScenarioManifest manifest,
        int trial,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!SupportsCurrentPlatform(manifest))
        {
            throw new PlatformNotSupportedException(
                $"Scenario '{manifest.Id}' does not support live capture on {CurrentPlatformName()}.");
        }

        using var runtimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runtimeCts.CancelAfter(TimeSpan.FromSeconds(manifest.Budget.MaximumRuntimeSeconds));
        try
        {
            return manifest.Id switch
            {
                "culture-lookup" => await CaptureCultureLookupAsync(manifest, trial, runtimeCts.Token).ConfigureAwait(false),
                "sync-over-async" => await CaptureSyncOverAsyncAsync(manifest, trial, runtimeCts.Token).ConfigureAwait(false),
                "lock-storm" => await CaptureLockStormAsync(manifest, trial, runtimeCts.Token).ConfigureAwait(false),
                _ => throw new InvalidDataException($"No live driver is registered for scenario '{manifest.Id}'."),
            };
        }
        catch (ScenarioRunException)
        {
            throw;
        }
        catch (PlatformNotSupportedException exception)
        {
            throw new ScenarioRunException(exception.Message, ScenarioFailureKind.Environment, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ScenarioRunException(exception.Message, ScenarioFailureKind.Environment, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ScenarioRunException(exception.Message, ScenarioFailureKind.Workload, exception);
        }
        catch (OperationCanceledException exception) when (
            runtimeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ScenarioRunException(
                $"Scenario '{manifest.Id}' exceeded its {manifest.Budget.MaximumRuntimeSeconds}-second runtime budget.",
                ScenarioFailureKind.Environment,
                exception);
        }
    }

    public static bool SupportsCurrentPlatform(ScenarioManifest manifest)
    {
        var platform = OperatingSystem.IsWindows()
            ? ScenarioPlatform.Windows
            : OperatingSystem.IsLinux()
                ? ScenarioPlatform.Linux
                : (ScenarioPlatform?)null;
        return platform is not null && manifest.SupportedLivePlatforms.Contains(platform.Value);
    }

    private static async Task<ScenarioEvidence> CaptureCultureLookupAsync(
        ScenarioManifest manifest,
        int trial,
        CancellationToken cancellationToken)
    {
        await using var sample = await StartSampleAsync(manifest).ConfigureAwait(false);
        using var http = CreateHttpClient(sample.BaseUrl);
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var load = new LoadCounters();
        var activationWatch = Stopwatch.StartNew();
        var driver = DriveRepeatedRequestsAsync(
            http,
            $"{RequiredParameter(manifest, "endpoint")}?iterations={RequiredPositiveIntParameter(manifest, "iterations")}",
            Math.Clamp(
                Environment.ProcessorCount,
                RequiredPositiveIntParameter(manifest, "minimumWorkers"),
                RequiredPositiveIntParameter(manifest, "maximumWorkers")),
            TimeSpan.FromMilliseconds(manifest.Workload.WarmupMilliseconds),
            load,
            loadCts.Token);

        CpuSampleResult result;
        try
        {
            result = await new EventPipeCpuSampler().SampleAsync(
                sample.ProcessId,
                TimeSpan.FromSeconds(manifest.Workload.ObservationSeconds),
                topN: 25,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is Microsoft.Diagnostics.NETCore.Client.DiagnosticsClientException
            or InvalidOperationException)
        {
            throw new ScenarioRunException(
                $"CPU collection failed for '{manifest.Id}'.",
                ScenarioFailureClassifier.Classify(exception, ScenarioFailureKind.Collection),
                exception);
        }
        finally
        {
            await loadCts.CancelAsync().ConfigureAwait(false);
            await DrainDriverAsync(driver).ConfigureAwait(false);
        }

        activationWatch.Stop();
        EnsureActivated(manifest.Id, load);
        var signals = NormalizeSignals(
            manifest,
            CpuSampleSignals.Detect(result.Artifact, "replay"),
            manifest.Budget.MaximumEvidenceItems);

        return Evidence(
            manifest,
            trial,
            activationWatch.Elapsed,
            result.Summary.Duration,
            load,
            metrics:
            [
                new ObservedMetric("total-samples", result.Summary.TotalSamples, "samples"),
            ],
            signals,
            frames: [],
            relations: [],
            notes: []);
    }

    private static async Task<ScenarioEvidence> CaptureSyncOverAsyncAsync(
        ScenarioManifest manifest,
        int trial,
        CancellationToken cancellationToken)
    {
        await using var sample = await StartSampleAsync(manifest).ConfigureAwait(false);
        using var http = CreateHttpClient(sample.BaseUrl);
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var snapshotCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var load = new LoadCounters();
        var activationWatch = Stopwatch.StartNew();
        var driver = DriveRepeatedRequestsAsync(
            http,
            $"{RequiredParameter(manifest, "endpoint")}?n={RequiredPositiveIntParameter(manifest, "n")}&delaySeconds={RequiredPositiveIntParameter(manifest, "delaySeconds")}",
            RequiredPositiveIntParameter(manifest, "concurrentRequests"),
            TimeSpan.FromMilliseconds(manifest.Workload.WarmupMilliseconds),
            load,
            loadCts.Token);

        var snapshotTask = CaptureThreadSnapshotAfterDelayAsync(
            sample.ProcessId,
            TimeSpan.FromMilliseconds(manifest.Workload.WarmupMilliseconds + 1800),
            snapshotCts.Token);
        var snapshotObserved = false;

        CounterSnapshot counters;
        ThreadSnapshotArtifact snapshot;
        try
        {
            counters = await new EventPipeCounterCollector().CollectAsync(
                sample.ProcessId,
                TimeSpan.FromSeconds(manifest.Workload.ObservationSeconds),
                intervalSeconds: 1,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            snapshotObserved = true;
            snapshot = await snapshotTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is Microsoft.Diagnostics.NETCore.Client.DiagnosticsClientException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            throw new ScenarioRunException(
                $"Counter/thread collection failed for '{manifest.Id}'.",
                ScenarioFailureClassifier.Classify(exception, ScenarioFailureKind.Collection),
                exception);
        }
        finally
        {
            await snapshotCts.CancelAsync().ConfigureAwait(false);
            await loadCts.CancelAsync().ConfigureAwait(false);
            await DrainDriverAsync(driver).ConfigureAwait(false);
            if (!snapshotObserved)
            {
                await DrainSnapshotAsync(snapshotTask).ConfigureAwait(false);
            }
        }

        activationWatch.Stop();
        EnsureActivated(manifest.Id, load);
        var metrics = SelectCounters(counters, "cpu-usage", "threadpool-queue-length", "threadpool-thread-count");
        var signals = NormalizeSignals(
            manifest,
            ThreadWaitSignals.Detect(snapshot, "replay"),
            manifest.Budget.MaximumEvidenceItems);
        var frames = NormalizeThreadFrames(snapshot, manifest);
        var notes = counters.Notes
            .Concat(snapshot.Warnings ?? [])
            .OrderBy(note => note, StringComparer.Ordinal)
            .Take(MaxNotes)
            .ToArray();

        return Evidence(
            manifest,
            trial,
            activationWatch.Elapsed,
            counters.Duration + snapshot.WalkDuration,
            load,
            metrics,
            signals,
            frames,
            NormalizeRelations(snapshot, manifest.Budget.MaximumEvidenceItems),
            notes);
    }

    private static async Task<ScenarioEvidence> CaptureLockStormAsync(
        ScenarioManifest manifest,
        int trial,
        CancellationToken cancellationToken)
    {
        await using var sample = await StartSampleAsync(manifest).ConfigureAwait(false);
        using var http = CreateHttpClient(sample.BaseUrl);
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var load = new LoadCounters();
        var activationWatch = Stopwatch.StartNew();
        var driver = DriveRepeatedRequestsAsync(
            http,
            $"{RequiredParameter(manifest, "endpoint")}?seconds={RequiredPositiveIntParameter(manifest, "seconds")}&blockers={RequiredPositiveIntParameter(manifest, "blockers")}",
            workers: 1,
            delay: TimeSpan.Zero,
            load,
            loadCts.Token);

        ThreadSnapshotArtifact snapshot;
        try
        {
            snapshot = await CaptureThreadSnapshotAfterDelayAsync(
                sample.ProcessId,
                TimeSpan.FromMilliseconds(manifest.Workload.WarmupMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or UnauthorizedAccessException)
        {
            throw new ScenarioRunException(
                $"Thread collection failed for '{manifest.Id}'.",
                ScenarioFailureClassifier.Classify(exception, ScenarioFailureKind.Collection),
                exception);
        }
        finally
        {
            await loadCts.CancelAsync().ConfigureAwait(false);
            await DrainDriverAsync(driver).ConfigureAwait(false);
        }

        activationWatch.Stop();
        EnsureActivated(manifest.Id, load);
        return Evidence(
            manifest,
            trial,
            activationWatch.Elapsed,
            snapshot.WalkDuration,
            load,
            metrics:
            [
                new ObservedMetric("managed-thread-count", snapshot.Threads.Count, "threads"),
                new ObservedMetric("monitor-lock-count", snapshot.Locks.Count, "locks"),
            ],
            NormalizeSignals(manifest, ThreadWaitSignals.Detect(snapshot, "replay"), manifest.Budget.MaximumEvidenceItems),
            frames: [],
            NormalizeRelations(snapshot, manifest.Budget.MaximumEvidenceItems),
            (snapshot.Warnings ?? []).OrderBy(note => note, StringComparer.Ordinal).Take(MaxNotes).ToArray());
    }

    private static async Task<LiveSampleProcess> StartSampleAsync(ScenarioManifest manifest)
    {
        var startupStageTimeout = TimeSpan.FromSeconds(
            Math.Max(1, manifest.Budget.MaximumRuntimeSeconds / 4.0));
        return await LiveSampleProcess.StartPublishedAsync(
            "BadCodeSample",
            new LiveSampleOptions
            {
                HarvestListeningUrl = true,
                WaitForHttpReady = true,
                ReadinessPath = "/",
                DiagnosticTimeout = startupStageTimeout,
                HttpTimeout = startupStageTimeout,
                Environment = new Dictionary<string, string>
                {
                    ["DOTNET_gcServer"] = "0",
                    ["DOTNET_TieredCompilation"] = "1",
                    ["DOTNET_TC_QuickJit"] = "1",
                    ["DOTNET_TieredPGO"] = "1",
                },
            }).ConfigureAwait(false);
    }

    private static HttpClient CreateHttpClient(string baseUrl)
        => new()
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };

    private static async Task DriveRepeatedRequestsAsync(
        HttpClient http,
        string path,
        int workers,
        TimeSpan delay,
        LoadCounters counters,
        CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref counters.Attempts);
                try
                {
                    using var response = await http.GetAsync(
                        path,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    Interlocked.Increment(ref counters.Successes);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (HttpRequestException)
                {
                    Interlocked.Increment(ref counters.Failures);
                }
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task DrainDriverAsync(Task driver)
    {
        try
        {
            await driver.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task DrainSnapshotAsync(Task<ThreadSnapshotArtifact> snapshot)
    {
        try
        {
            await snapshot.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<ThreadSnapshotArtifact> CaptureThreadSnapshotAfterDelayAsync(
        int processId,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return await new ClrMdThreadSnapshotInspector().InspectLiveAsync(
            processId,
            new ThreadSnapshotOptions(MaxFramesPerThread: 64),
            cancellationToken).ConfigureAwait(false);
    }

    private static ScenarioEvidence Evidence(
        ScenarioManifest manifest,
        int trial,
        TimeSpan activationDuration,
        TimeSpan collectionDuration,
        LoadCounters load,
        IReadOnlyList<ObservedMetric> metrics,
        IReadOnlyList<ObservedSignal> signals,
        IReadOnlyList<ObservedFrame> frames,
        IReadOnlyList<ObservedRelation> relations,
        IReadOnlyList<string> notes)
        => new(
            SchemaVersion: ScenarioJson.CurrentEvidenceSchemaVersion,
            ScenarioId: manifest.Id,
            ScenarioVersion: manifest.Version,
            Trial: trial,
            Environment: CurrentEnvironment(),
            Activation: new ScenarioStageResult(
                ScenarioStageStatus.Passed,
                ScenarioFailureKind.None,
                $"attempts={load.Attempts}; successes={load.Successes}; requestFailures={load.Failures}",
                RoundSeconds(activationDuration)),
            Collection: new ScenarioStageResult(
                ScenarioStageStatus.Passed,
                ScenarioFailureKind.None,
                null,
                RoundSeconds(collectionDuration)),
            Metrics: metrics.OrderBy(metric => metric.Name, StringComparer.Ordinal).ToArray(),
            Signals: signals,
            Frames: frames,
            Relations: relations,
            Notes: notes);

    private static ObservedMetric[] SelectCounters(CounterSnapshot snapshot, params string[] names)
        => names.Select(name =>
            {
                var counter = snapshot.Counters.FirstOrDefault(value =>
                    string.Equals(value.Provider, "System.Runtime", StringComparison.Ordinal)
                    && string.Equals(value.Name, name, StringComparison.Ordinal));
                return counter is null ? null : new ObservedMetric(name, counter.Value, counter.Unit);
            })
            .Where(metric => metric is not null)
            .Cast<ObservedMetric>()
            .OrderBy(metric => metric.Name, StringComparer.Ordinal)
            .ToArray();

    private static ObservedSignal[] NormalizeSignals(
        ScenarioManifest manifest,
        IReadOnlyList<SignalGroup> signals,
        int maximumEvidenceItems)
    {
        var selectedSignals = manifest.ExpectedEvidence
            .Select(invariant => invariant.Signal)
            .Where(signal => signal is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        return signals
            .Where(signal => selectedSignals.Contains(signal.Signal))
            .OrderBy(signal => signal.Signal, StringComparer.Ordinal)
            .Take(maximumEvidenceItems)
            .Select(signal => new ObservedSignal(
                signal.Signal,
                Math.Round(signal.Salience, 6),
                signal.Buckets
                    .OrderByDescending(bucket => bucket.Magnitude)
                    .ThenBy(bucket => bucket.Key, StringComparer.Ordinal)
                    .Take(Math.Min(5, maximumEvidenceItems))
                    .Select(bucket => new ObservedSignalBucket(
                        NormalizeBucketKey(bucket.Key),
                        Math.Round(bucket.Magnitude, 6),
                        bucket.Unit))
                    .ToArray(),
                signal.NextAction?.NextTool))
            .ToArray();
    }

    private static ObservedFrame[] NormalizeThreadFrames(
        ThreadSnapshotArtifact snapshot,
        ScenarioManifest manifest)
    {
        var terms = manifest.ExpectedEvidence
            .Where(invariant => invariant.Kind == EvidenceInvariantKind.StackFrameMatch)
            .SelectMany(invariant => invariant.ContainsAny ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return snapshot.Threads
            .SelectMany(thread => thread.Frames)
            .Where(frame => terms.Any(term => frame.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(frame => frame.DisplayName, StringComparer.Ordinal)
            .Select(group => new ObservedFrame(group.Key, group.Count()))
            .OrderByDescending(frame => frame.MatchCount)
            .ThenBy(frame => frame.DisplayName, StringComparer.Ordinal)
            .Take(manifest.Budget.MaximumEvidenceItems)
            .ToArray();
    }

    private static ObservedRelation[] NormalizeRelations(
        ThreadSnapshotArtifact snapshot,
        int maximumEvidenceItems)
    {
        var threads = snapshot.Threads.ToDictionary(thread => thread.ManagedThreadId);
        return snapshot.Locks
            .Where(lockState => lockState.WaitingThreadCount > 0)
            .Select(lockState =>
            {
                threads.TryGetValue(lockState.OwnerManagedThreadId, out var owner);
                return owner?.InferredWaitReason is null
                    ? null
                    : new ObservedRelation(
                        "thread-owner-overlap",
                        owner.InferredWaitReason,
                        lockState.WaitingThreadCount);
            })
            .Where(relation => relation is not null)
            .Cast<ObservedRelation>()
            .OrderByDescending(relation => relation.WaitingThreadCount)
            .ThenBy(relation => relation.OwnerWaitReason, StringComparer.Ordinal)
            .Take(Math.Min(10, maximumEvidenceItems))
            .ToArray();
    }

    private static string NormalizeBucketKey(string key)
    {
        if (!key.StartsWith("thread ", StringComparison.Ordinal))
        {
            return key;
        }

        var owns = key.IndexOf(" owns ", StringComparison.Ordinal);
        if (owns < 0)
        {
            return key;
        }

        var value = $"thread{key[owns..]}";
        var address = value.IndexOf(" @ 0x", StringComparison.Ordinal);
        return address < 0 ? value : value[..address];
    }

    private static string RequiredParameter(ScenarioManifest manifest, string name)
    {
        if (!manifest.Workload.Parameters.TryGetValue(name, out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Scenario '{manifest.Id}' requires workload parameter '{name}'.");
        }

        return value;
    }

    private static int RequiredPositiveIntParameter(ScenarioManifest manifest, string name)
    {
        var value = RequiredParameter(manifest, name);
        if (!int.TryParse(value, out var parsed) || parsed < 1)
        {
            throw new InvalidDataException(
                $"Scenario '{manifest.Id}' workload parameter '{name}' must be a positive integer.");
        }

        return parsed;
    }

    private static void EnsureActivated(string scenarioId, LoadCounters load)
    {
        if (load.Attempts == 0 || (load.Failures > 0 && load.Successes == 0))
        {
            throw new ScenarioRunException(
                $"Scenario '{scenarioId}' did not activate successfully (attempts={load.Attempts}, successes={load.Successes}, failures={load.Failures}).",
                ScenarioFailureKind.Workload);
        }
    }

    private static ScenarioEnvironment CurrentEnvironment()
        => new(
            CurrentPlatformName(),
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            RuntimeInformation.FrameworkDescription,
            Environment.Version.ToString());

    private static string CurrentPlatformName()
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported";

    private static double RoundSeconds(TimeSpan duration) => Math.Round(duration.TotalSeconds, 6);

    private sealed class LoadCounters
    {
        public int Attempts;
        public int Successes;
        public int Failures;
    }
}
