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
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new EventPipeGcCollector
        {
            ReadinessProvider = new Microsoft.Diagnostics.NETCore.Client.EventPipeProvider(
                "DotnetDiagnostics.GcReadiness", System.Diagnostics.Tracing.EventLevel.Informational),
            ConfigureReadiness = source => source.Dynamic.All += e =>
            {
                if (e.ProviderName == "DotnetDiagnostics.GcReadiness") ready.TrySetResult();
            },
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = collector.CollectAsync(sample.ProcessId, TimeSpan.FromSeconds(30), cancellationToken: cancellation.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await cancellation.CancelAsync();
        var action = async () => await task;
        await action.Should().ThrowAsync<OperationCanceledException>();
        sample.IsRunning.Should().BeTrue();
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
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var background = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new EventPipeGcCollector
        {
            ReadinessProvider = new Microsoft.Diagnostics.NETCore.Client.EventPipeProvider(
                "DotnetDiagnostics.GcReadiness", System.Diagnostics.Tracing.EventLevel.Informational),
            ConfigureReadiness = source => source.Dynamic.All += e =>
            {
                if (e.ProviderName == "DotnetDiagnostics.GcReadiness") ready.TrySetResult();
            },
            CollectionStarted = type =>
            {
                if (type == "BackgroundGC") background.TrySetResult();
            },
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var capture = collector.CollectAsync(sample.ProcessId, TimeSpan.FromSeconds(8), cancellationToken: deadline.Token);
        // Same-session observed event handshake, before inducing any GC.
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(4), deadline.Token);
        await sample.RequestGcAsync("blocking");
        for (var request = 0; request < 3 && !background.Task.IsCompleted; request++)
        {
            await sample.RequestGcAsync("background");
            await Task.WhenAny(background.Task, Task.Delay(500, deadline.Token));
        }
        var summary = await capture;
        summary.Duration.Should().BeGreaterThan(TimeSpan.FromSeconds(7)).And.BeLessThan(TimeSpan.FromSeconds(15));
        output.WriteLine($"{sample.RuntimeDescription}; {summary.MeasurementSummary}");
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(summary.Suspension));
        summary.Events.Should().Contain(e => e.Type == "BackgroundGC", "a request alone does not establish background execution");
        summary.Events.Should().Contain(e => e.Type == "NonConcurrentGC");
        summary.Suspension.Should().NotBeNull();
        var evidence = summary.Suspension!;
        evidence.IsAuthoritative.Should().BeTrue();
        evidence.Intervals.Should().NotBeEmpty();
        evidence.Intervals.Should().OnlyContain(p => p.Reason == 1 || p.Reason == 6);
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
        var heartbeatsInsideBackground = summary.Events.Where(e => e.Type == "BackgroundGC")
            .Count(e => sample.Heartbeats.Any(t => t > e.Timestamp.UtcTicks && t < (e.Timestamp + e.CollectionElapsedDuration).UtcTicks));
        output.WriteLine($"Background collections with application heartbeat: {heartbeatsInsideBackground}");
        heartbeatsInsideBackground.Should().BeGreaterThan(0,
            "observed background collection elapsed must include application progress");
    }
}
