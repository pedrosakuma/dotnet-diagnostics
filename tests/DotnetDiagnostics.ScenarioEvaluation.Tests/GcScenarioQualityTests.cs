using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Signals;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class GcScenarioQualityTests
{
    [Fact]
    public void MissingSampleReadinessIsEnvironmentFailureNotEvidenceEvaluation()
    {
        ScenarioFailureClassifier.Classify(
            DotnetDiagnostics.TestSupport.SkipException.ForReason("Sample did not advertise an HTTP listening URL."),
            ScenarioFailureKind.Evaluation).Should().Be(ScenarioFailureKind.Environment);
    }

    [Fact]
    public void QualityNotesExplainSuppressedGen2SignalWithoutRelaxingItsGate()
    {
        var summary = Summary(new Dictionary<string, long> { ["right-censored-collection"] = 1 });
        ScenarioLiveRunner.DescribeGcQuality(summary).Should().Contain("gc.limitation:right-censored-collection=1");
        GcSignals.Detect(summary, "test").Should().NotContain(signal => signal.Signal == "gc.gen2-share");
        GcSignals.Detect(Summary(new Dictionary<string, long>()), "test")
            .Should().Contain(signal => signal.Signal == "gc.gen2-share");
    }

    [Fact]
    public void QualityNotesBoundLimitationsAndPreserveCompletion()
    {
        var limitations = Enumerable.Range(0, 30).ToDictionary(index => $"limitation-{index:D2}", _ => 1L);
        var notes = ScenarioLiveRunner.DescribeGcQuality(Summary(limitations));
        notes.Should().HaveCount(14);
        notes.Should().Contain("gc.limitations-omitted=20");
        notes.Should().Contain(note => note.Contains("normal-stop", StringComparison.Ordinal));
        notes.Should().Contain(note => note.Contains("requestedSeconds=8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuiescenceStopsNewRequestsOnlyAfterCountersAndAwaitsInFlightWork()
    {
        var counters = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopIssuing = new CancellationTokenSource();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stopIssuing.Token.Register(() => stopped.SetResult());
        var quiescence = ScenarioLiveRunner.QuiesceGcWorkloadAsync(counters.Task, stopIssuing, driver.Task);
        stopIssuing.IsCancellationRequested.Should().BeFalse();
        counters.SetResult();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        quiescence.IsCompleted.Should().BeFalse("in-flight HTTP handlers must finish, not be cancelled");
        driver.SetResult();
        (await quiescence.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeAfter(DateTimeOffset.UnixEpoch);
    }

    private static GcSummary Summary(IReadOnlyDictionary<string, long> limitations)
    {
        var start = DateTimeOffset.UnixEpoch;
        return new GcSummary(1, start, TimeSpan.FromSeconds(8), 50, TimeSpan.Zero, TimeSpan.Zero,
            [new GenerationStats(2, 50)], [],
            Suspension: new GcSuspensionEvidence("no-detected-loss", start, start.AddSeconds(8), start,
                "normal-stop", TimeSpan.Zero, TimeSpan.Zero, 50, 0, [], limitations))
        {
            RequestedDuration = TimeSpan.FromSeconds(8),
        };
    }
}
