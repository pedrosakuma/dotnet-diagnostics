using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Jobs;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

public class CollectionJobRunnerTests
{
    private static readonly TimeSpan ShortTtl = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Start_TransitionsRunningToCompleted_WhenWorkSucceeds()
    {
        var store = new MemoryDiagnosticHandleStore();
        var runner = new CollectionJobRunner(store);
        var gate = new TaskCompletionSource();

        var handle = runner.Start(processId: 42, kind: "test-job", ShortTtl,
            async ct =>
            {
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                return DiagnosticResult.Ok(new Payload("done"), "ok");
            });

        store.TryGet<CollectionJob>(handle.Id)!.Snapshot().Status.Should().Be(CollectionJobStatus.Running);

        gate.SetResult();

        await WaitForStatusAsync(store, handle.Id, CollectionJobStatus.Completed);
        var snap = store.TryGet<CollectionJob>(handle.Id)!.Snapshot();
        snap.Status.Should().Be(CollectionJobStatus.Completed);
        snap.Result.Should().BeOfType<DiagnosticResult<Payload>>();
        snap.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task Start_CapturesException_AsFailed()
    {
        var store = new MemoryDiagnosticHandleStore();
        var runner = new CollectionJobRunner(store);

        var handle = runner.Start<Payload>(processId: 1, kind: "test-job", ShortTtl,
            _ => throw new InvalidOperationException("boom"));

        await WaitForStatusAsync(store, handle.Id, CollectionJobStatus.Failed);
        var snap = store.TryGet<CollectionJob>(handle.Id)!.Snapshot();
        snap.Error.Should().NotBeNull();
        snap.Error!.Kind.Should().Be(nameof(InvalidOperationException));
        snap.Error.Message.Should().Be("boom");
    }

    [Fact]
    public async Task Start_PropagatesDiagnosticError_AsFailed()
    {
        var store = new MemoryDiagnosticHandleStore();
        var runner = new CollectionJobRunner(store);

        var handle = runner.Start(processId: 1, kind: "test-job", ShortTtl,
            _ => Task.FromResult(DiagnosticResult.Fail<Payload>(
                "nope",
                new DiagnosticError("X", "msg"))));

        await WaitForStatusAsync(store, handle.Id, CollectionJobStatus.Failed);
        var snap = store.TryGet<CollectionJob>(handle.Id)!.Snapshot();
        snap.Error!.Kind.Should().Be("X");
    }

    [Fact]
    public async Task Cancel_TransitionsToCanceled()
    {
        var store = new MemoryDiagnosticHandleStore();
        var runner = new CollectionJobRunner(store);
        var started = new TaskCompletionSource();

        var handle = runner.Start(processId: 1, kind: "test-job", ShortTtl,
            async ct =>
            {
                started.SetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                return DiagnosticResult.Ok(new Payload("unreachable"), "x");
            });

        await started.Task;
        runner.Cancel(handle.Id).Should().BeTrue();

        await WaitForStatusAsync(store, handle.Id, CollectionJobStatus.Canceled);
        store.TryGet<CollectionJob>(handle.Id)!.Snapshot().Status.Should().Be(CollectionJobStatus.Canceled);
    }

    [Fact]
    public void Cancel_OnUnknownHandle_ReturnsFalse()
    {
        var store = new MemoryDiagnosticHandleStore();
        var runner = new CollectionJobRunner(store);

        runner.Cancel("does-not-exist").Should().BeFalse();
    }

    private static async Task WaitForStatusAsync(
        IDiagnosticHandleStore store,
        string handle,
        CollectionJobStatus expected,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var job = store.TryGet<CollectionJob>(handle);
            if (job is not null && job.Snapshot().Status == expected)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Job '{handle}' did not reach status {expected} within the timeout.");
    }

    private sealed record Payload(string Value);
}
