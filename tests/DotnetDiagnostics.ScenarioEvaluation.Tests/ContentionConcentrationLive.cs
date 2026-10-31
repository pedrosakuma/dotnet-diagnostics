using System.Diagnostics;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Signals;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.TestSupport;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class ContentionCharacterizationEnvironmentVariables
{
    public const string Repetitions = "DOTNET_DIAGNOSTICS_CONTENTION_REPETITIONS";
    public const string ReportPath = "DOTNET_DIAGNOSTICS_CONTENTION_REPORT_PATH";
}

public static class ContentionConcentrationLive
{
    private static readonly TimeSpan CaptureDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(1);

    public static async Task<IReadOnlyList<ContentionCaptureEvidence>> CaptureAllAsync(
        int repetitions,
        CancellationToken cancellationToken)
    {
        if (repetitions is < 1 or > ContentionConcentrationBounds.MaximumCapturesPerScenario)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repetitions),
                $"Repetitions must be in [1, {ContentionConcentrationBounds.MaximumCapturesPerScenario}].");
        }

        var captures = new List<ContentionCaptureEvidence>(
            repetitions * Enum.GetValues<ContentionConcentrationScenario>().Length);
        foreach (var scenario in Enum.GetValues<ContentionConcentrationScenario>())
        {
            for (var repetition = 1; repetition <= repetitions; repetition++)
            {
                captures.Add(await CaptureAsync(scenario, repetition, cancellationToken).ConfigureAwait(false));
            }
        }

        return captures;
    }

    public static async Task<ContentionCaptureEvidence> CaptureAsync(
        ContentionConcentrationScenario scenario,
        int repetition,
        CancellationToken cancellationToken)
    {
        var workload = Workload(scenario);
        await using var sample = await LiveSampleProcess.StartPublishedAsync(
            "BadCodeSample",
            new LiveSampleOptions
            {
                HarvestListeningUrl = true,
                WaitForHttpReady = true,
                ReadinessPath = "/",
                DiagnosticTimeout = TimeSpan.FromSeconds(10),
                HttpTimeout = TimeSpan.FromSeconds(10),
                Environment = new Dictionary<string, string>
                {
                    ["DOTNET_gcServer"] = "0",
                    ["DOTNET_TieredCompilation"] = "1",
                    ["DOTNET_TC_QuickJit"] = "1",
                    ["DOTNET_TieredPGO"] = "1",
                },
            },
            cancellationToken).ConfigureAwait(false);
        using var http = new HttpClient
        {
            BaseAddress = new Uri(sample.BaseUrl),
            Timeout = TimeSpan.FromSeconds(15),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var captureStartedAt = DateTimeOffset.UtcNow;
        var accumulator = new BoundedContentionAccumulator(captureStartedAt, WindowDuration);
        var collector = new EventPipeContentionCollector();
        var collectionTask = collector.CollectStreamingAsync(
            sample.ProcessId,
            CaptureDuration,
            accumulator.Add,
            timeout.Token);

        await Task.Delay(ArmDelay, timeout.Token).ConfigureAwait(false);
        var workloadTask = RunWorkloadAsync(http, workload, timeout.Token);
        Task<ThreadSnapshotArtifact?> snapshotTask = scenario == ContentionConcentrationScenario.HealthyControl
            ? Task.FromResult<ThreadSnapshotArtifact?>(null)
            : CaptureSnapshotAsync(
                sample.ProcessId,
                workload.SnapshotDelay,
                timeout.Token);

        await Task.WhenAll(collectionTask, workloadTask, snapshotTask).ConfigureAwait(false);
        var captureId = $"{ScenarioName(scenario)}-{repetition:D2}";
        return accumulator.Complete(
            captureId,
            scenario,
            sample.ProcessId,
            collectionTask.Result,
            snapshotTask.Result,
            forceOwnerUnavailable: scenario == ContentionConcentrationScenario.OwnerUnavailable);
    }

    private static async Task RunWorkloadAsync(
        HttpClient http,
        ContentionWorkload workload,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            $"/contention-shape?shape={workload.Shape}&milliseconds={workload.Duration.TotalMilliseconds:0}&locks={workload.LockCount}&contenders={workload.Contenders}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<ThreadSnapshotArtifact?> CaptureSnapshotAsync(
        int processId,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return await new ClrMdThreadSnapshotInspector().InspectLiveAsync(
            processId,
            new ThreadSnapshotOptions(MaxFramesPerThread: 32),
            cancellationToken).ConfigureAwait(false);
    }

    private static ContentionWorkload Workload(ContentionConcentrationScenario scenario)
        => scenario switch
        {
            ContentionConcentrationScenario.DominantLock
                => new("dominant", TimeSpan.FromMilliseconds(2200), 1, 16, TimeSpan.FromMilliseconds(650)),
            ContentionConcentrationScenario.DiffuseLocks
                => new("diffuse", TimeSpan.FromMilliseconds(2200), 5, 20, TimeSpan.FromMilliseconds(650)),
            ContentionConcentrationScenario.OwnerUnavailable
                => new("dominant", TimeSpan.FromMilliseconds(2200), 1, 16, TimeSpan.FromMilliseconds(650)),
            ContentionConcentrationScenario.ShortBurst
                => new("burst", TimeSpan.FromMilliseconds(250), 1, 3, TimeSpan.FromMilliseconds(100)),
            ContentionConcentrationScenario.TruncatedInventory
                => new("truncated", TimeSpan.FromMilliseconds(2200), 12, 36, TimeSpan.FromMilliseconds(650)),
            ContentionConcentrationScenario.HealthyControl
                => new("healthy", TimeSpan.FromMilliseconds(1200), 1, 2, TimeSpan.Zero),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private static string ScenarioName(ContentionConcentrationScenario scenario)
        => scenario switch
        {
            ContentionConcentrationScenario.DominantLock => "dominant-lock",
            ContentionConcentrationScenario.DiffuseLocks => "diffuse-locks",
            ContentionConcentrationScenario.OwnerUnavailable => "owner-unavailable",
            ContentionConcentrationScenario.ShortBurst => "short-burst",
            ContentionConcentrationScenario.TruncatedInventory => "truncated-inventory",
            ContentionConcentrationScenario.HealthyControl => "healthy-control",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private sealed record ContentionWorkload(
        string Shape,
        TimeSpan Duration,
        int LockCount,
        int Contenders,
        TimeSpan SnapshotDelay);

    private sealed class BoundedContentionAccumulator(
        DateTimeOffset captureStartedAt,
        TimeSpan windowDuration)
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, MutableLockEvidence> _locks =
            new(StringComparer.Ordinal);
        private readonly Dictionary<int, MutableWindowEvidence> _windows = [];
        private int _eventCount;
        private double _totalWaitMilliseconds;
        private bool _aggregationOverflow;
        private bool _ownerMetadataObserved;

        public void Add(ContentionEventSample sample)
        {
            lock (_sync)
            {
                _eventCount++;
                var waitMilliseconds = Math.Max(0, sample.Duration.TotalMilliseconds);
                _totalWaitMilliseconds += waitMilliseconds;
                _ownerMetadataObserved |= sample.OwnerManagedThreadId is > 0;
                var lockKey = $"{sample.LockId:x16}:{sample.AssociatedObjectId:x16}";
                if (!_locks.TryGetValue(lockKey, out var lockEvidence))
                {
                    if (_locks.Count >= ContentionConcentrationBounds.MaximumTrackedLocks)
                    {
                        _aggregationOverflow = true;
                        return;
                    }
                    else
                    {
                        lockEvidence = new MutableLockEvidence();
                        _locks.Add(lockKey, lockEvidence);
                    }
                }

                lockEvidence?.Add(waitMilliseconds);
                var windowIndex = (int)Math.Floor(
                    (sample.StoppedAt - captureStartedAt).TotalMilliseconds / windowDuration.TotalMilliseconds);
                if (windowIndex is < 0 or >= ContentionConcentrationBounds.MaximumWindowsPerCapture)
                {
                    return;
                }

                if (!_windows.TryGetValue(windowIndex, out var window))
                {
                    window = new MutableWindowEvidence();
                    _windows.Add(windowIndex, window);
                }

                window.Add(lockKey, waitMilliseconds);
            }
        }

        public ContentionCaptureEvidence Complete(
            string captureId,
            ContentionConcentrationScenario scenario,
            int processId,
            ContentionSnapshot snapshot,
            ThreadSnapshotArtifact? threadSnapshot,
            bool forceOwnerUnavailable)
        {
            lock (_sync)
            {
                var ranked = _locks
                    .Select(pair => new
                    {
                        pair.Key,
                        pair.Value.EventCount,
                        pair.Value.TotalWaitMilliseconds,
                    })
                    .OrderByDescending(item => item.TotalWaitMilliseconds)
                    .ThenBy(item => item.Key, StringComparer.Ordinal)
                    .ToArray();
                var top1 = Share(ranked.Take(1).Sum(item => item.TotalWaitMilliseconds));
                var top3 = Share(ranked.Take(3).Sum(item => item.TotalWaitMilliseconds));
                var inventoryTruncated = ranked.Length > ContentionConcentrationBounds.MaximumReportedLocks;
                var topLocks = ranked.Take(ContentionConcentrationBounds.MaximumReportedLocks)
                    .Select(item => new ContentionLockWaitEvidence(
                        item.Key,
                        item.EventCount,
                        item.TotalWaitMilliseconds,
                        Share(item.TotalWaitMilliseconds) ?? 0))
                    .ToArray();
                var windows = _windows
                    .OrderBy(pair => pair.Key)
                    .Select(pair => pair.Value.ToEvidence(pair.Key))
                    .ToArray();
                var ownerSignals = threadSnapshot is null
                    ? []
                    : ThreadWaitSignals.Detect(threadSnapshot, captureId);
                var ownerMetadataAvailable = !forceOwnerUnavailable
                    && (_ownerMetadataObserved
                        || threadSnapshot?.Locks.Any(lockState => lockState.OwnerManagedThreadId > 0) == true);
                bool? ownerOverlap = ownerMetadataAvailable
                    ? ownerSignals.Any(signal => signal.Signal == "correlation.thread-overlap")
                    : null;
                var limitations = new List<string>
                {
                    "Concentration is computed from delivered contention wait durations and does not establish why a lock was contended.",
                    "Non-overlapping one-second windows are nested descriptive evidence; only fresh captures are independent units.",
                };
                if (inventoryTruncated)
                {
                    limitations.Add(
                        $"The reported top-lock inventory is bounded to {ContentionConcentrationBounds.MaximumReportedLocks}; omitted locks remain possible and bounded absence is not zero.");
                }
                if (_aggregationOverflow)
                {
                    limitations.Add(
                        $"The streaming aggregation exceeded {ContentionConcentrationBounds.MaximumTrackedLocks} distinct locks; concentration is incomplete.");
                }
                if (forceOwnerUnavailable)
                {
                    limitations.Add(
                        "Owner metadata was intentionally withheld after live collection to validate the unavailable-owner decision path.");
                }
                if (_eventCount == 0)
                {
                    limitations.Add(
                        "No contention event was delivered in this bounded capture; this is unavailable concentration evidence, not a zero-wait claim.");
                }
                limitations.AddRange(snapshot.Notes.Where(note =>
                    note.Contains("dropped", StringComparison.OrdinalIgnoreCase)
                    || note.Contains("No lock contention", StringComparison.Ordinal)));

                return new(
                    captureId,
                    scenario,
                    processId,
                    threadSnapshot?.RuntimeVersion ?? Environment.Version.ToString(),
                    Environment.OSVersion.Platform.ToString(),
                    snapshot.Duration.TotalSeconds,
                    _eventCount,
                    ranked.Length,
                    _totalWaitMilliseconds,
                    snapshot.P50ContentionDuration.TotalMilliseconds,
                    snapshot.P95ContentionDuration.TotalMilliseconds,
                    snapshot.MaxContentionDuration.TotalMilliseconds,
                    top1,
                    top3,
                    topLocks.Length,
                    inventoryTruncated,
                    _aggregationOverflow,
                    ownerMetadataAvailable,
                    ownerOverlap,
                    windows.Length,
                    (int)Math.Ceiling(snapshot.Duration.TotalMilliseconds / windowDuration.TotalMilliseconds),
                    topLocks,
                    windows,
                    limitations.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
            }
        }

        private double? Share(double numerator)
            => _totalWaitMilliseconds > 0 ? numerator / _totalWaitMilliseconds : null;

        private sealed class MutableLockEvidence
        {
            public int EventCount { get; private set; }
            public double TotalWaitMilliseconds { get; private set; }

            public void Add(double waitMilliseconds)
            {
                EventCount++;
                TotalWaitMilliseconds += waitMilliseconds;
            }
        }

        private sealed class MutableWindowEvidence
        {
            private readonly Dictionary<string, MutableLockEvidence> _locks =
                new(StringComparer.Ordinal);

            public void Add(string lockKey, double waitMilliseconds)
            {
                if (!_locks.TryGetValue(lockKey, out var evidence))
                {
                    evidence = new MutableLockEvidence();
                    _locks.Add(lockKey, evidence);
                }

                evidence.Add(waitMilliseconds);
            }

            public ContentionWindowEvidence ToEvidence(int windowIndex)
            {
                var ranked = _locks.Values
                    .OrderByDescending(value => value.TotalWaitMilliseconds)
                    .ToArray();
                var total = ranked.Sum(value => value.TotalWaitMilliseconds);
                return new(
                    windowIndex,
                    ranked.Sum(value => value.EventCount),
                    total,
                    total > 0 ? ranked.Take(1).Sum(value => value.TotalWaitMilliseconds) / total : 0,
                    total > 0 ? ranked.Take(3).Sum(value => value.TotalWaitMilliseconds) / total : 0);
            }
        }
    }
}
