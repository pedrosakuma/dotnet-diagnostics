using System.Diagnostics;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed partial class MonitoredRunnerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task WorkerExitHandoffDoesNotOverrideResultExitAgreement(int exitCode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var execution = fixture.Manifest.Plan.Executions.Single(item => item.CaseId == "F3" && item.Candidate == "A");
        var launcher = new ScriptedWorkerLauncher(ScriptedWorkerBehavior.SuccessfulRecovery,
            awaitIdentityEvent: true, recoveryExitCode: exitCode);
        Func<Task> run = async () =>
        {
            var outcome = await MonitoredCampaignRunner.RunExecutionForComponentAsync(fixture, execution,
                launcher, TimeSpan.FromSeconds(5), CancellationToken.None);
            outcome.Outcome.Should().Be("pass");
            outcome.Worker!.Outcome.Should().Be("pass");
            outcome.MonitoringComplete.Should().BeTrue();
        };
        if (exitCode == 0) await run.Should().NotThrowAsync();
        else (await run.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("WorkerExitCodeMismatch");
        launcher.Identities.Should().HaveCount(2).And.OnlyContain(identity => !LinuxProcessIdentity.Matches(identity));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("release-failed")]
    [InlineData("permission")]
    [InlineData("dead")]
    [InlineData("wrong-start")]
    [InlineData("unregistered")]
    public async Task WorkerExitHandoffFailuresStayHardAndReleaseTheSweepGate(string failure)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        using var child = StartReadyHandoffStub();
        var identity = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "failed-exit.jsonl"), sampledLoss: true,
            observedUnlinked: true, unifiedActive: true);
        if (failure != "unregistered") monitor.AddProcess(identity);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (failure == "dead")
        {
            child.Kill();
            await child.WaitForExitAsync(deadline.Token);
        }
        var requested = failure == "wrong-start"
            ? identity with { LinuxStartTimeTicks = identity.LinuxStartTimeTicks + 1 } : identity;
        var released = false;
        var primary = new IOException("controlled release failure");
        Func<Task> run = () => monitor.ReleaseAndWaitForExitAsync(requested, () =>
        {
            released = true;
            if (failure == "release-failed") throw primary;
            if (failure == "permission") throw new UnauthorizedAccessException("controlled release EACCES");
            deadline.Cancel();
            return Task.CompletedTask;
        }, deadline.Token);
        try
        {
            if (failure == "cancel") await run.Should().ThrowAsync<OperationCanceledException>();
            else if (failure == "release-failed")
                (await run.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(primary);
            else if (failure == "permission") await run.Should().ThrowAsync<UnauthorizedAccessException>();
            else
                (await run.Should().ThrowAsync<DurableStorageExperimentException>()).Which.Code
                    .Should().Be(failure == "unregistered" ? "UnknownProcessIdentity" : "UnexpectedProcessIdentityLoss");
            released.Should().Be(failure is "cancel" or "release-failed" or "permission");
            monitor.IsIncomplete.Should().BeTrue();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (failure != "dead" && failure != "unregistered")
                await monitor.KillOwnedAsync(identity, cleanup.Token);
            var after = await monitor.ObserveBoundaryAsync("failed-handoff-cleanup", false, cleanup.Token);
            if (failure == "dead") after.Errors.Should().Contain("UnexpectedProcessIdentityLoss");
            monitor.IsIncomplete.Should().BeTrue("cleanup cannot turn a failed handoff green");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
        }
    }

    [LinuxOnlyFact]
    public async Task WorkerExitHandoffAuthorityAcknowledgesOnlyInsideGateAndConfirmsBeforeNextControl()
    {
        var fixture = PrepareComponentExecution();
        using var child = StartReadyHandoffStub();
        var identity = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "authority-exit.jsonl"),
            sampledLoss: true, observedUnlinked: true, unifiedActive: true);
        var ownership = Path.Combine(fixture.Manifest.OutputRoot, "ownership.jsonl");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PrevalidationMonitorControl.DispatchAsync(new("register", identity), monitor, ownership, deadline.Token);
        Task<MonitoredSweepResult>? concurrent = null;
        try
        {
            var reply = await PrevalidationMonitorControl.DispatchAsync(new("exit", identity), monitor,
                ownership, deadline.Token, releaseExit: async () =>
                {
                    LinuxPrevalidationProcessOperations.Instance.IsOriginalAlive(identity).Should().BeTrue();
                    PrevalidationMonitorControl.Snapshot(monitor).Incomplete.Should().BeFalse();
                    concurrent = monitor.ObserveBoundaryAsync("worker-exit-quiescent", false, deadline.Token);
                    concurrent.IsCompleted.Should().BeFalse();
                    await child.StandardInput.WriteLineAsync("exit");
                    await child.StandardInput.FlushAsync(deadline.Token);
                });
            child.HasExited.Should().BeTrue();
            reply.Incomplete.Should().BeFalse();
            (await concurrent!.WaitAsync(deadline.Token)).Errors.Should().BeEmpty();
            var status = await PrevalidationMonitorControl.DispatchAsync(new("status"), monitor, ownership, deadline.Token);
            status.Incomplete.Should().BeFalse();
            PrevalidationOwnership.Read(ownership).Should().ContainSingle().Which.Should().Be(identity);
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
        }
    }

    [LinuxOnlyFact]
    public async Task WorkerExitHandoffDrainsSweepAndExcludesNewSweepsUntilConfirmedExit()
    {
        var fixture = PrepareComponentExecution();
        using var child = StartReadyHandoffStub();
        var identity = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "worker-exit.jsonl"),
            sampledLoss: true, observedUnlinked: true, unifiedActive: true);
        monitor.AddProcess(identity);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permitExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? handoff = null;
        monitor.BeforeDescriptorOperationForComponent = (_, operation) =>
        {
            if (operation != 1 || handoff is not null) return;
            handoff = monitor.ReleaseAndWaitForExitAsync(identity, async () =>
            {
                released.SetResult();
                await permitExit.Task.WaitAsync(deadline.Token);
                await child.StandardInput.WriteLineAsync("exit");
                await child.StandardInput.FlushAsync(deadline.Token);
            }, deadline.Token);
            released.Task.IsCompleted.Should().BeFalse("the old mark-and-release path raced the pinned sweep");
        };
        try
        {
            var before = await monitor.ObserveBoundaryAsync("pre-worker-termination", false, deadline.Token);
            before.Errors.Should().BeEmpty();
            await released.Task.WaitAsync(deadline.Token);
            child.HasExited.Should().BeFalse();
            var queued = monitor.ObserveBoundaryAsync("worker-exit-quiescent", false, deadline.Token);
            queued.IsCompleted.Should().BeFalse("the live owner remains registered through the exit handoff");
            permitExit.SetResult();
            await handoff!.WaitAsync(deadline.Token);
            var after = await queued.WaitAsync(deadline.Token);
            child.HasExited.Should().BeTrue();
            after.Errors.Should().BeEmpty();
            after.Summary.SampledLoss!.Counts[5].Should().Be(0);
            monitor.IsIncomplete.Should().BeFalse();
            _output.WriteLine($"owner={identity}; in-flight sweep drained; queued sweep resumed after confirmed exit");
        }
        finally
        {
            permitExit.TrySetResult();
            try
            {
                if (handoff is not null) await handoff.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                await child.WaitForExitAsync();
            }
        }
    }
}
