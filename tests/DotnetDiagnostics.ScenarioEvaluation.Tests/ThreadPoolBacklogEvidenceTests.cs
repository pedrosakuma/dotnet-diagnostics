using System.Collections.Concurrent;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class ThreadPoolBacklogEvidenceTests
{
    private static readonly string FixturePath = ScenarioManifestLoader.ScenarioPath(
        "Fixtures", "ThreadPoolBacklog", "threadpool-backlog-scenarios.v1.json");

    [Fact]
    public void ReplayFixture_CoversRequiredCasesAndExpectedActions()
    {
        var fixture = ThreadPoolBacklogEvidenceJson.ReadFixture(FixturePath);

        fixture.Cases.Select(item => item.Id).Should().BeEquivalentTo(
        [
            "sustained-backlog",
            "transient-draining-burst",
            "high-workers-without-backlog",
            "unknown-starvation-reason",
            "loss-and-truncation",
            "healthy-control",
        ]);

        foreach (var scenario in fixture.Cases)
        {
            var result = ThreadPoolBacklogEvidenceEvaluator.Evaluate(scenario);

            result.NextAction.Should().Be(scenario.Expected.NextAction, scenario.Id);
            result.StarvationSupport.Should().Be(scenario.Expected.StarvationSupport, scenario.Id);
            result.QueueMedian.Estimate.Should().Be(scenario.Expected.QueueMedian, scenario.Id);
            if (scenario.Expected.QueueP95Median.HasValue)
            {
                result.QueueP95Median.Should().NotBeNull(scenario.Id);
                result.QueueP95Median!.Estimate.Should().Be(
                    scenario.Expected.QueueP95Median,
                    scenario.Id);
            }
            else
            {
                result.QueueP95Median?.Estimate.Should().BeNull(scenario.Id);
            }

            result.QueueSlope.Slope.Should().Be(scenario.Expected.QueueSlope, scenario.Id);
            result.WorkerMedian.Estimate.Should().Be(scenario.Expected.WorkerMedian, scenario.Id);
            result.WorkerSlope.Slope.Should().Be(scenario.Expected.WorkerSlope, scenario.Id);
            result.BacklogWindows.Should().Be(scenario.Expected.BacklogWindows, scenario.Id);
            result.BacklogWindowProportion.Should().Be(scenario.Expected.BacklogWindowProportion, scenario.Id);
            result.DurationAboveLevelSeconds.Should().Be(
                scenario.Expected.DurationAboveLevelSeconds,
                scenario.Id);
            result.DrainedAtTail.Should().Be(scenario.Expected.DrainedAtTail, scenario.Id);
        }
    }

    [Fact]
    public void QueueGrowthWithoutRuntimeReason_NeverEstablishesStarvation()
    {
        var scenario = ThreadPoolBacklogEvidenceJson.ReadFixture(FixturePath)
            .Cases.Single(item => item.Id == "unknown-starvation-reason");

        var result = ThreadPoolBacklogEvidenceEvaluator.Evaluate(scenario);

        result.NextAction.Should().Be(ThreadPoolBacklogNextAction.CollectWaitsAndStacksNow);
        result.QueueSlope.Slope.Should().BePositive();
        result.Reason.Provenance.Should().Be("missing");
        result.StarvationSupport.Should().Be(ThreadPoolStarvationSupport.NotEstablished);
    }

    [Fact]
    public void CollectorP95_IsOptionalAndDoesNotDriveTheAction()
    {
        var scenario = ThreadPoolBacklogEvidenceJson.ReadFixture(FixturePath)
            .Cases.Single(item => item.Id == "sustained-backlog");
        var withoutP95 = scenario with
        {
            Windows = scenario.Windows.Select(window => window with { QueueP95 = null }).ToArray(),
        };

        var withP95Result = ThreadPoolBacklogEvidenceEvaluator.Evaluate(scenario);
        var withoutP95Result = ThreadPoolBacklogEvidenceEvaluator.Evaluate(withoutP95);

        withP95Result.QueueP95Median.Should().NotBeNull();
        withoutP95Result.QueueP95Median.Should().BeNull();
        withoutP95Result.NextAction.Should().Be(withP95Result.NextAction);
        withoutP95Result.StarvationSupport.Should().Be(withP95Result.StarvationSupport);
    }

    [Fact]
    public void ScenarioDefinedBacklogLevel_IsPartOfTheProtocolNotAUniversalThreshold()
    {
        var scenario = ThreadPoolBacklogEvidenceJson.ReadFixture(FixturePath)
            .Cases.Single(item => item.Id == "sustained-backlog");
        var aboveObservedRange = scenario with
        {
            Protocol = scenario.Protocol with { BacklogLevel = 1000 },
        };

        var result = ThreadPoolBacklogEvidenceEvaluator.Evaluate(aboveObservedRange);

        result.BacklogWindows.Should().Be(0);
        result.NextAction.Should().Be(ThreadPoolBacklogNextAction.ContinueObservation);
        result.StarvationSupport.Should().Be(ThreadPoolStarvationSupport.NotEstablished);
    }

    [Fact]
    public void FixedBounds_RejectUnboundedWindowSets()
    {
        var scenario = ThreadPoolBacklogEvidenceJson.ReadFixture(FixturePath).Cases[0];
        var windows = Enumerable.Range(
                0,
                ThreadPoolBacklogEvidenceJson.MaximumWindowsPerCase + 1)
            .Select(index => scenario.Windows[0] with
            {
                Id = $"window-{index + 1}",
                StartSeconds = index,
            })
            .ToArray();

        var action = () => ThreadPoolBacklogEvidenceEvaluator.Evaluate(
            scenario with { Windows = windows });

        action.Should().Throw<InvalidDataException>().WithMessage("*window count*");
    }

    [Fact]
    public void IntermittentBacklog_DoesNotSatisfyContiguousDuration()
    {
        var source = ThreadPoolBacklogEvidenceJson.ReadFixture(FixturePath)
            .Cases.Single(item => item.Id == "sustained-backlog");
        var queueLengths = new double[] { 12, 0, 12, 0, 12, 12 };
        var scenario = source with
        {
            Id = "intermittent-backlog",
            Reason = new ThreadPoolBacklogReason(null, "missing", null, null),
            Windows = source.Windows
                .Select((window, index) => window with
                {
                    QueueLength = queueLengths[index],
                    QueueP95 = null,
                })
                .ToArray(),
        };

        var result = ThreadPoolBacklogEvidenceEvaluator.Evaluate(scenario);

        result.BacklogWindowProportion.Should().BeApproximately(4d / 6d, 1e-12);
        result.DurationAboveLevelSeconds.Should().Be(2);
        result.NextAction.Should().Be(ThreadPoolBacklogNextAction.ContinueObservation);
    }

    [Fact]
    public void RuntimeReason_MustBelongToTheEvaluatedCapture()
    {
        var scenario = ThreadPoolBacklogEvidenceJson.ReadFixture(FixturePath)
            .Cases.Single(item => item.Id == "sustained-backlog") with
        {
            Reason = new ThreadPoolBacklogReason(
                "Starvation",
                "runtime-observed",
                "capture-other-run",
                5),
        };

        var action = () => ThreadPoolBacklogEvidenceEvaluator.Evaluate(scenario);

        action.Should().Throw<InvalidDataException>().WithMessage("*evaluated capture range*");
    }

    [Fact]
    public void OverlappingWindows_AreRejected()
    {
        var scenario = ThreadPoolBacklogEvidenceJson.ReadFixture(FixturePath)
            .Cases.Single(item => item.Id == "healthy-control");
        var windows = scenario.Windows.ToArray();
        windows[1] = windows[1] with { StartSeconds = 0.5 };

        var action = () => ThreadPoolBacklogEvidenceEvaluator.Evaluate(
            scenario with { Windows = windows });

        action.Should().Throw<InvalidDataException>().WithMessage("*must not overlap*");
    }
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class ThreadPoolBacklogLiveEvidenceTests
{
    [Fact]
    public void IrregularCounterCadence_MarksWindowsAsDegraded()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var observations = new[]
        {
            Observation(1, startedAt, "threadpool-queue-length", 5),
            Observation(2, startedAt, "threadpool-thread-count", 4),
            Observation(3, startedAt.AddSeconds(1), "threadpool-queue-length", 5),
            Observation(4, startedAt.AddSeconds(1), "threadpool-thread-count", 4),
            Observation(5, startedAt.AddSeconds(3), "threadpool-queue-length", 5),
            Observation(6, startedAt.AddSeconds(3), "threadpool-thread-count", 4),
        };
        var completion = new DiagnosticSessionCompletion(
            DiagnosticSessionStatus.Completed,
            startedAt,
            startedAt.AddSeconds(4),
            EventPipeEventsLost: 0,
            DroppedObservations: 0,
            Error: null);

        var result = BuildWindows(observations, completion, retainedWindowCapacity: 8);

        result.CadenceDiscontinuities.Should().Be(1);
        result.Windows.Should().OnlyContain(window =>
            window.Representation == ScenarioStatisticalRepresentation.Truncated);
        result.Windows[0].LostOrDropped.Should().Be(1);
    }

    [WindowsOrLinuxFact(
        "Bounded EventCounter ThreadPool scenarios require a live CoreCLR target.",
        Timeout = 240_000)]
    [Trait("Category", "ScenarioEvaluationLive")]
    public async Task LiveScenarios_DistinguishSustainedTransientHighWorkerLossAndHealthyCases()
    {
        var sustained = await CaptureAsync(
            "live-sustained-backlog",
            observationSeconds: 6,
            activationPath: "/threadpool-starve?blockers=128&seconds=60&minWorkers=4&gate=sustained",
            releasePath: "/threadpool-starve/release?gate=sustained");
        sustained.Analysis.NextAction.Should().Be(
            ThreadPoolBacklogNextAction.CollectWaitsAndStacksNow,
            Describe(sustained));
        sustained.Analysis.StarvationSupport.Should().Be(ThreadPoolStarvationSupport.NotEstablished);

        var transient = await CaptureAsync(
            "live-transient-draining-burst",
            observationSeconds: 7,
            activationPath: "/threadpool-starve?blockers=128&seconds=2&minWorkers=4");
        transient.Analysis.NextAction.Should().Be(
            ThreadPoolBacklogNextAction.TreatBurstAsResolved,
            Describe(transient));
        transient.Analysis.DrainedAtTail.Should().BeTrue();

        var highWorkers = await CaptureAsync(
            "live-high-workers-without-backlog",
            observationSeconds: 5,
            primePath: "/threadpool-starve?blockers=32&seconds=1&minWorkers=32",
            primeDrainDelay: TimeSpan.FromMilliseconds(1500));
        highWorkers.Analysis.BacklogWindows.Should().Be(0);
        highWorkers.Analysis.WorkerMedian.Estimate.Should().BeGreaterThanOrEqualTo(16);
        highWorkers.Analysis.NextAction.Should().Be(ThreadPoolBacklogNextAction.InvestigateOtherSignals);

        var degraded = await CaptureAsync(
            "live-loss-and-truncation",
            observationSeconds: 5,
            retainedWindowCapacity: 2);
        degraded.DroppedWindows.Should().BeGreaterThan(0);
        degraded.Analysis.NextAction.Should().Be(
            ThreadPoolBacklogNextAction.RecaptureDegradedEvidence);

        var healthy = await CaptureAsync(
            "live-healthy-control",
            observationSeconds: 5);
        healthy.Analysis.BacklogWindows.Should().Be(0);
        healthy.Analysis.NextAction.Should().Be(ThreadPoolBacklogNextAction.ContinueObservation);
        healthy.Analysis.QueueP95Median.Should().BeNull(
            "one EventCounter tick per window does not provide a collector-sourced p95");
    }

    private static async Task<LiveAnalysis> CaptureAsync(
        string id,
        int observationSeconds,
        string? activationPath = null,
        string? releasePath = null,
        string? primePath = null,
        TimeSpan? primeDrainDelay = null,
        int retainedWindowCapacity = ThreadPoolBacklogEvidenceJson.MaximumWindowsPerCase,
        int observationCapacity = 256)
    {
        await using var sample = await LiveSampleProcess.StartPublishedAsync(
            "BadCodeSample",
            new LiveSampleOptions
            {
                HarvestListeningUrl = true,
                WaitForHttpReady = true,
                ReadinessPath = "/",
                DiagnosticTimeout = TimeSpan.FromSeconds(20),
                HttpTimeout = TimeSpan.FromSeconds(20),
                Environment = new Dictionary<string, string>
                {
                    ["DOTNET_gcServer"] = "0",
                    ["DOTNET_TieredCompilation"] = "1",
                    ["DOTNET_TC_QuickJit"] = "1",
                    ["DOTNET_TieredPGO"] = "1",
                },
            });
        using var http = new HttpClient
        {
            BaseAddress = new Uri(sample.BaseUrl),
            Timeout = TimeSpan.FromSeconds(20),
        };
        if (primePath is not null)
        {
            using var response = await http.GetAsync(
                primePath,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await Task.Delay(primeDrainDelay ?? TimeSpan.FromSeconds(1));
        }

        var observations = new ConcurrentQueue<CounterObservation>();
        await using var session = new EventPipeCounterCollector().CreateSession(
            sample.ProcessId,
            new CounterSessionOptions
            {
                Providers = ["System.Runtime"],
                IntervalSeconds = 1,
                ObservationCapacity = observationCapacity,
            });
        using var subscription = session.Attach<CounterObservation>((observation, _) =>
        {
            if (observation.Counter.Name is "threadpool-queue-length" or "threadpool-thread-count")
            {
                observations.Enqueue(observation);
            }

            return ValueTask.CompletedTask;
        });

        await session.StartAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var activatedAt = DateTimeOffset.UtcNow;
        if (activationPath is not null)
        {
            using var response = await http.GetAsync(
                activationPath,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
        }

        DiagnosticSessionCompletion completion;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(observationSeconds));
            completion = await session.StopAsync();
        }
        finally
        {
            if (releasePath is not null)
            {
                using var response = await http.PostAsync(releasePath, content: null);
                response.EnsureSuccessStatusCode();
            }
        }

        var builtWindows = BuildWindows(
            observations.Where(observation => observation.Timestamp >= activatedAt).ToArray(),
            completion,
            retainedWindowCapacity);
        var scenario = new ThreadPoolBacklogScenarioCase(
            id,
            new ThreadPoolBacklogProtocol(
                "threadpool-live-bounded-windows",
                "1",
                BacklogLevel: 1,
                HighWorkerLevel: 16,
                RequiredBacklogWindowProportion: 0.5,
                RequiredDurationAboveLevelSeconds: 2,
                MinimumWindows: 4,
                DrainTailWindows: 2),
            new ThreadPoolBacklogReason(null, "missing", null, null),
            builtWindows.Windows,
            new ThreadPoolBacklogExpected(
                ThreadPoolBacklogNextAction.ContinueObservation,
                ThreadPoolStarvationSupport.NotEstablished,
                null, null, null, null, null, null, null, null, null));
        return new(
            ThreadPoolBacklogEvidenceEvaluator.Evaluate(scenario),
            completion,
            scenario.Windows,
            builtWindows.DroppedWindows);
    }

    private static WindowBuildResult BuildWindows(
        IReadOnlyList<CounterObservation> observations,
        DiagnosticSessionCompletion completion,
        int retainedWindowCapacity)
    {
        var allQueues = observations
            .Where(item => item.Counter.Name == "threadpool-queue-length")
            .OrderBy(item => item.Timestamp)
            .ToArray();
        var queues = allQueues.Take(retainedWindowCapacity).ToArray();
        var workers = observations
            .Where(item => item.Counter.Name == "threadpool-thread-count")
            .OrderBy(item => item.Timestamp)
            .ToArray();
        queues.Should().NotBeEmpty("the live scenario must observe bounded queue windows");
        workers.Should().NotBeEmpty("the live scenario must observe worker-count windows");
        var droppedWindows = allQueues.Length - queues.Length;
        var cadenceDiscontinuities = 0;
        for (var index = 1; index < queues.Length; index++)
        {
            var observedSpacing = (queues[index].Timestamp - queues[index - 1].Timestamp).TotalSeconds;
            var expectedSpacing = WindowDurationSeconds(queues[index]);
            var tolerance = Math.Max(0.5, expectedSpacing * 0.5);
            if (observedSpacing <= 0
                || Math.Abs(observedSpacing - expectedSpacing) > tolerance)
            {
                cadenceDiscontinuities++;
            }
        }

        var lostOrDropped = (completion.EventPipeEventsLost ?? 0)
            + completion.DroppedObservations
            + droppedWindows
            + cadenceDiscontinuities;
        var representation = lostOrDropped > 0
            ? ScenarioStatisticalRepresentation.Truncated
            : ScenarioStatisticalRepresentation.Exact;

        var nextStartSeconds = 0d;
        var windows = queues.Select((queue, index) =>
        {
            var worker = workers.MinBy(item =>
                Math.Abs((item.Timestamp - queue.Timestamp).TotalMilliseconds))!;
            var durationSeconds = WindowDurationSeconds(queue);
            var startSeconds = nextStartSeconds;
            nextStartSeconds += durationSeconds;
            return new ThreadPoolBacklogWindow(
                $"window-{index + 1}",
                startSeconds,
                durationSeconds,
                queue.Counter.Value,
                worker.Counter.Value,
                QueueP95: null,
                SourceEventCount: 2,
                LostOrDropped: index == 0 ? lostOrDropped : 0,
                representation);
        }).ToArray();
        return new(windows, droppedWindows, cadenceDiscontinuities);
    }

    private static double WindowDurationSeconds(CounterObservation observation)
        => observation.Counter.IntervalSec is double interval
            && double.IsFinite(interval)
            && interval > 0
                ? interval
                : 1;

    private static CounterObservation Observation(
        long sequence,
        DateTimeOffset timestamp,
        string name,
        double value)
        => new(
            sequence,
            timestamp,
            new CounterValue(
                "System.Runtime",
                name,
                name,
                value,
                CounterKind.Mean)
            {
                IntervalSec = 1,
            });

    private sealed record LiveAnalysis(
        ThreadPoolBacklogAnalysis Analysis,
        DiagnosticSessionCompletion Completion,
        IReadOnlyList<ThreadPoolBacklogWindow> Windows,
        int DroppedWindows);

    private sealed record WindowBuildResult(
        ThreadPoolBacklogWindow[] Windows,
        int DroppedWindows,
        int CadenceDiscontinuities);

    private static string Describe(LiveAnalysis live)
        => $"windows={string.Join(", ", live.Windows.Select(window =>
            $"{window.StartSeconds:F1}s:q={window.QueueLength:R}/w={window.WorkerCount:R}"))}; "
            + $"backlogWindows={live.Analysis.BacklogWindows}; "
            + $"proportion={live.Analysis.BacklogWindowProportion:R}; "
            + $"duration={live.Analysis.DurationAboveLevelSeconds:R}; "
            + $"queueSlope={live.Analysis.QueueSlope.Slope:R}; "
            + $"drainedTail={live.Analysis.DrainedAtTail}";
}
