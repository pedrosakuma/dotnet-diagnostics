using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliGcActivitiesReadinessTests
{
    [Fact(Timeout = 5000)]
    public async Task ForcedGcWorkloadWaitsForNonGcMarkerInItsOwnStream()
    {
        var readiness = new CliGcActivitiesReadiness();
        var bothStreams = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var work = readiness.ObserveWorkloadAsync(bothStreams.Task, _ =>
        {
            requests++;
            bothStreams.SetResult();
            return Task.CompletedTask;
        }, deadline.Token);

        requests.Should().Be(0, "no forced GC may run before the GC stream observes its marker");
        work.IsCompleted.Should().BeFalse();
        readiness.Observe("Microsoft-Windows-DotNETRuntime", "GCStart");
        readiness.Observe("Other.Provider", "EventCounters");
        readiness.Observe("System.Runtime", "OtherEvent");
        readiness.IsStreamReady.Should().BeFalse("unrelated events must not open the gate, even before continuations run");
        requests.Should().Be(0);
        work.IsCompleted.Should().BeFalse();

        readiness.Observe("System.Runtime", "EventCounters");
        await work;
        requests.Should().Be(1);
    }

    [Fact(Timeout = 5000)]
    public async Task MissingMarkerCancelsWithoutGeneratingForcedGc()
    {
        var readiness = new CliGcActivitiesReadiness();
        var requests = 0;
        using var cancellation = new CancellationTokenSource();
        var work = readiness.ObserveWorkloadAsync(Task.CompletedTask, _ =>
        {
            requests++;
            return Task.CompletedTask;
        }, cancellation.Token);

        work.IsCompleted.Should().BeFalse("even later workload readiness cannot replace the stream marker");
        await cancellation.CancelAsync();
        var action = async () => await work;
        await action.Should().ThrowAsync<OperationCanceledException>();
        requests.Should().Be(0);
    }
}
