using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class GcSuspensionLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CancellationNeverPublishesACompleteSnapshot()
    {
        await using var sample = await MultiVersionSampleProcess.StartAsync("net10.0", gcPauseWorkload: true);
        var progress = new GcProgressEvidence();
        var collector = new EventPipeGcCollector
        {
            ReadinessProvider = new Microsoft.Diagnostics.NETCore.Client.EventPipeProvider(
                "DotnetDiagnostics.GcReadiness", System.Diagnostics.Tracing.EventLevel.Informational),
            ConfigureReadiness = progress.Attach,
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = collector.CollectAsync(sample.ProcessId, TimeSpan.FromSeconds(30), cancellationToken: cancellation.Token);
        try
        {
            await progress.Ready.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellation.Token);
            await sample.RequestGcAsync("arm");
            await progress.AcknowledgeArmAsync(0, cancellation.Token);
            await cancellation.CancelAsync();
            var action = async () => await task;
            await action.Should().ThrowAsync<OperationCanceledException>();
            sample.IsRunning.Should().BeTrue();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await sample.StopGcWorkloadAsync(cleanup.Token);
            sample.IsRunning.Should().BeFalse();
        }
        finally
        {
            output.WriteLine(progress.Describe());
            await cancellation.CancelAsync();
            try { await task; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task ProcessingFailureRemainsUnavailableAfterShutdown()
    {
        await using var sample = await MultiVersionSampleProcess.StartAsync("net10.0", gcPauseWorkload: true);
        var collector = new EventPipeGcCollector { ConfigureReadiness = _ => throw new InvalidOperationException("test parser failure") };
        var summary = await collector.CollectAsync(sample.ProcessId, TimeSpan.FromSeconds(1));
        summary.Suspension!.Completion.Should().Be("processing-failure");
        summary.Suspension.TotalSuspensionTime.Should().BeNull();
        summary.PauseMeasurementStatus.Should().Be("unreliable");
    }

    [Theory]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    [InlineData("net10.0")]
    public async Task BlockingAndObservedBackground_HaveIndependentSuspensionEvidence(string framework)
    {
        await using var sample = await MultiVersionSampleProcess.StartAsync(framework, gcPauseWorkload: true);
        var progress = new GcProgressEvidence();
        var collector = new EventPipeGcCollector
        {
            ReadinessProvider = new Microsoft.Diagnostics.NETCore.Client.EventPipeProvider(
                "DotnetDiagnostics.GcReadiness", System.Diagnostics.Tracing.EventLevel.Informational),
            ConfigureReadiness = progress.Attach,
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var capture = collector.CollectAsync(sample.ProcessId, TimeSpan.FromSeconds(8), cancellationToken: deadline.Token);
        GcSummary summary;
        try
        {
            await progress.Ready.Task.WaitAsync(TimeSpan.FromSeconds(4), deadline.Token);
            await RequestAsync(0, "blocking");
            // A runtime's first BackgroundGC can execute wholly suspended. Use the fixed existing
            // three-request budget, independent of labels or favorable samples; never retry a result.
            for (var request = 1; request <= 3; request++)
            {
                await RequestAsync(request, "background");
            }
            summary = await capture;
            await sample.StopGcWorkloadAsync(deadline.Token);
        }
        finally
        {
            output.WriteLine(progress.Describe());
            output.WriteLine(sample.LastOutputLine);
            await deadline.CancelAsync();
            try { await capture; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
        summary.Duration.Should().BeGreaterThan(TimeSpan.FromSeconds(7)).And.BeLessThan(TimeSpan.FromSeconds(15));
        output.WriteLine($"{sample.RuntimeDescription}; {summary.MeasurementSummary}");
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary.Suspension));
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary.Events));
        var progressResult = progress.Evaluate(summary);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(progressResult));
        summary.Events.Should().Contain(e => e.Type == "BackgroundGC", "a request alone does not establish background execution");
        summary.Events.Should().Contain(e => e.Type == "NonConcurrentGC");
        summary.Suspension.Should().NotBeNull();
        var evidence = summary.Suspension!;
        evidence.IsAuthoritative.Should().BeTrue();
        evidence.Intervals.Should().NotBeEmpty();
        evidence.Intervals.Should().OnlyContain(p => p.Reason == 1 || p.Reason == 6);
        evidence.Intervals.Should().Contain(p => p.Reason == 1).And.Contain(p => p.Reason == 6);
        evidence.DroppedIntervals.Should().Be(0, "exact union verification requires every measured interval");
        evidence.OutputOmittedIntervals.Should().Be(0);
        evidence.ObservedIntervals.Should().Be(evidence.Intervals.Count);
        evidence.ObservationStart.Should().Be(summary.StartedAt);
        evidence.ObservationEnd.Should().Be(summary.StartedAt + summary.Duration);
        evidence.Intervals.Should().OnlyContain(p =>
            p.StartedAt >= evidence.ObservationStart && p.StoppedAt <= evidence.ObservationEnd &&
            p.StoppedAt >= p.StartedAt);
        var union = UtcIntervalUnion.Measure(evidence.ObservationStart, evidence.ObservationEnd,
            evidence.Intervals.Select(p => (p.StartedAt, p.StoppedAt)));
        union.Clipped.Should().Be(0);
        union.Disjoint.Should().Be(0);
        // GCStart/GCStop do not enclose every GC-related suspension; compare within the observed pause scope.
        evidence.TotalSuspensionTime.Should().Be(TimeSpan.FromTicks(union.CoveredTicks));
        evidence.TotalSuspensionTime.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(summary.Duration);
        evidence.MaxSuspensionTime.Should().Be(evidence.Intervals.Max(p => p.Duration));
        progressResult.Requests.Should().HaveCount(4);
        progressResult.ProvesProgress.Should().BeTrue(
            "an identity-matched BackgroundGC must contain independent, pre-armed, loss-free application progress");

        async Task RequestAsync(int request, string kind)
        {
            await sample.RequestGcAsync("arm");
            await progress.AcknowledgeArmAsync(request, deadline.Token);
            await sample.RequestGcAsync(kind);
            await progress.WaitForCollectionAsync(request, deadline.Token);
            await sample.RequestGcAsync("stop");
            await progress.WaitForCompletionAsync(request, deadline.Token);
        }
    }
}
