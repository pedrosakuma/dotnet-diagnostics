using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed partial class MonitoredRunnerTests
{
    [LinuxOnlyFact]
    public async Task LiveHandoffRunnerAcceptsConfirmedTargetEventWithoutRemovingOwnerTwice()
    {
        var fixture = PrepareComponentExecution();
        using var child = StartReadyHandoffStub();
        var identity = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Target);
        var execution = fixture.Manifest.Plan.Executions.Single(item => item.CaseId == "L1" && item.Candidate == "E");
        try
        {
            Func<Task> run = () => MonitoredCampaignRunner.RunExecutionForComponentAsync(fixture, execution,
                new HandoffWorkerLauncher(identity), TimeSpan.FromSeconds(5), CancellationToken.None);
            (await run.Should().ThrowAsync<DurableStorageExperimentException>()).Which.Code
                .Should().Be("MissingWorkerResult", "this tiny control-only worker deliberately produces no capture result");
            child.HasExited.Should().BeTrue();
            var summaries = Directory.EnumerateFiles(fixture.Manifest.OutputRoot, "*-monitor.jsonl",
                    SearchOption.AllDirectories).SelectMany(File.ReadLines)
                .Select(line => JsonSerializer.Deserialize<MonitoredSweepSummary>(line, PrevalidationProtocol.Json)!)
                .ToArray();
            summaries.Should().Contain(summary => summary.Boundary == "pre-target-termination");
            summaries.Should().Contain(summary => summary.Boundary == "worker-exit-quiescent");
            summaries.Should().OnlyContain(summary => summary.ErrorCount == 0 && summary.Alarm == null);
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
        }
    }

    [LinuxOnlyFact]
    public async Task LiveHandoffDrainsInFlightSweepBeforeKillingAndRemovingRealOwner()
    {
        var fixture = PrepareComponentExecution();
        using var child = StartReadyHandoffStub();
        var identity = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Target);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "handoff.jsonl"),
            sampledLoss: true, observedUnlinked: true, unifiedActive: true);
        monitor.AddProcess(identity);
        Task? handoff = null;
        monitor.BeforeDescriptorOperationForComponent = (_, operation) =>
        {
            if (operation != 1 || handoff is not null) return;
            handoff = PrevalidationMonitorControl.DispatchAsync(new("kill", identity), monitor,
                Path.Combine(fixture.Manifest.OutputRoot, "ownership.jsonl"), CancellationToken.None);
            handoff.IsCompleted.Should().BeFalse("the active descriptor sweep still owns the gate");
            child.HasExited.Should().BeFalse("the handoff cannot signal during the in-flight sweep");
        };
        try
        {
            var before = await monitor.ObserveBoundaryAsync("pre-target-termination", false, CancellationToken.None);
            before.Errors.Should().BeEmpty();
            before.Summary.SampledLoss!.Counts[10].Should().BeGreaterThan(0);
            handoff.Should().NotBeNull();
            await handoff!.WaitAsync(TimeSpan.FromSeconds(5));
            child.HasExited.Should().BeTrue();
            var after = await monitor.ObserveBoundaryAsync("owned-cleanup-quiescent", false, CancellationToken.None);
            after.Errors.Should().BeEmpty();
            after.Summary.SampledLoss!.Counts[10].Should().Be(0);
            monitor.IsIncomplete.Should().BeFalse();
            _output.WriteLine($"target={identity}; preCandidates={before.Summary.SampledLoss.Counts[10]}; confirmedExit={child.HasExited}; postCandidates=0");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
        }
    }

    [LinuxOnlyFact]
    public async Task LiveHandoffCannotRetroactivelyAuthorizeUnexpectedRealOwnerDeath()
    {
        var fixture = PrepareComponentExecution();
        using var child = StartReadyHandoffStub();
        var identity = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Target);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "unexpected.jsonl"),
            sampledLoss: true, observedUnlinked: true, unifiedActive: true);
        monitor.AddProcess(identity);
        child.Kill();
        await child.WaitForExitAsync();
        var handoff = () => monitor.KillOwnedAsync(identity, CancellationToken.None);
        (await handoff.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("UnexpectedProcessIdentityLoss");
        Action confirm = () => monitor.ConfirmTerminatedAndRemove(identity);
        confirm.Should().Throw<DurableStorageExperimentException>().Which.Code
            .Should().Be("IntentionalTerminationNotConfirmed");
        var after = await monitor.ObserveBoundaryAsync("unexpected-death", false, CancellationToken.None);
        after.Errors.Should().Contain("UnexpectedProcessIdentityLoss");
        DescriptorObservationPolicy.Admissible(after.Summary).Should().BeFalse();
        monitor.IsIncomplete.Should().BeTrue();
    }

    [LinuxOnlyFact]
    public async Task LiveHandoffCannotRemoveAnOwnerBeforeConfirmedExit()
    {
        var fixture = PrepareComponentExecution();
        using var child = StartReadyHandoffStub();
        var identity = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Target);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "early-confirm.jsonl"), sampledLoss: true);
        monitor.AddProcess(identity);
        try
        {
            await monitor.PrepareTerminationAsync(identity, CancellationToken.None);
            Action confirm = () => monitor.ConfirmTerminatedAndRemove(identity);
            confirm.Should().Throw<DurableStorageExperimentException>().Which.Code
                .Should().Be("IntentionalTerminationNotConfirmed");
            child.HasExited.Should().BeFalse();
            await monitor.KillOwnedAsync(identity, CancellationToken.None);
            child.HasExited.Should().BeTrue();
            monitor.IsIncomplete.Should().BeTrue("the invalid early confirmation cannot be upgraded");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveHandoffStartupFailureAndNormalEndShareCleanupDeadline(bool startupFailure)
    {
        using var clock = new HandoffClock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken handoffToken = default;
        Task Cleanup() => LiveSampleProcess.ObserveCleanupAsync(
            () => signaled.TrySetResult(), Task.CompletedTask, stdout.Task, Task.CompletedTask,
            LiveSampleProcess.CleanupTimeout, clock, async token =>
            {
                handoffToken = token;
                await release.Task.WaitAsync(token);
            });
        var primary = new IOException("controlled readiness failure");
        var operation = startupFailure
            ? LiveSampleProcess.CompleteStartupAsync(() => Task.FromException(primary),
                () => new ValueTask(Cleanup()))
            : Cleanup();
        signaled.Task.IsCompleted.Should().BeFalse();
        release.SetResult();
        await signaled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        operation.IsCompleted.Should().BeFalse("the same cleanup still owns stdout");
        clock.Created.Should().Be(1);
        clock.Fire();
        handoffToken.IsCancellationRequested.Should().BeTrue();
        Func<Task> observe = () => operation;
        if (startupFailure)
        {
            var failure = (await observe.Should().ThrowAsync<AggregateException>()).Which;
            failure.InnerExceptions[0].Should().BeSameAs(primary);
            failure.InnerExceptions[1].Should().BeOfType<TimeoutException>();
        }
        else await observe.Should().ThrowAsync<TimeoutException>();
        clock.Created.Should().Be(1, "the handoff must not restart or extend the five-second cleanup budget");
        stdout.SetResult();
    }

    [Fact]
    public async Task LiveHandoffTimeoutStillSignalsButCannotClaimSuccessfulCleanup()
    {
        using var clock = new HandoffClock();
        var signaled = false;
        var cleanup = LiveSampleProcess.ObserveCleanupAsync(() => signaled = true,
            Task.CompletedTask, Task.CompletedTask, Task.CompletedTask,
            LiveSampleProcess.CleanupTimeout, clock,
            token => Task.Delay(Timeout.InfiniteTimeSpan, token));
        signaled.Should().BeFalse();
        clock.Fire();
        Func<Task> observe = () => cleanup;
        await observe.Should().ThrowAsync<AggregateException>();
        signaled.Should().BeTrue("failed cooperation does not abandon the owned child");
        clock.Created.Should().Be(1);
    }

    [LinuxOnlyFact]
    public async Task LiveHandoffStartupFailureUsesRegisteredCleanupBeforeReturningOriginalError()
        => await VerifyRegisteredLiveCleanupAsync(startupFailure: true);

    [LinuxOnlyFact]
    public async Task LiveHandoffNormalDisposalUsesRegisteredCleanupBeforeKill()
        => await VerifyRegisteredLiveCleanupAsync(startupFailure: false);

    private async Task VerifyRegisteredLiveCleanupAsync(bool startupFailure)
    {
        var fixture = PrepareComponentExecution();
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "startup-handoff.jsonl"), sampledLoss: true);
        MonitoredProcessIdentity? identity = null;
        var confirmed = false;
        var primary = new IOException("controlled failure after target registration; no readiness or capture");
        Func<Task> start = async () =>
        {
            await using var sample = await LiveSampleProcess.StartPublishedAsync("CoreClrSample",
                new LiveSampleOptions
                {
                    ProcessStarted = process =>
                    {
                        identity = MonitoredProcessIdentity.Capture(process, MonitoredProcessRole.Target);
                        monitor.AddProcess(identity);
                        if (startupFailure) throw primary;
                        return Task.CompletedTask;
                    },
                    BeforeTermination = async token =>
                    {
                        identity.Should().NotBeNull();
                        LinuxPrevalidationProcessOperations.Instance.IsOriginalAlive(identity!).Should().BeTrue();
                        await monitor.KillOwnedAsync(identity!, token);
                        confirmed = !LinuxPrevalidationProcessOperations.Instance.IsOriginalAlive(identity!);
                    },
                });
        };
        try
        {
            if (startupFailure)
                (await start.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(primary);
            else await start();
            confirmed.Should().BeTrue();
            monitor.IsIncomplete.Should().BeFalse();
        }
        finally
        {
            if (identity is not null) LinuxPrevalidationProcessOperations.Instance.Signal(identity);
        }
    }

    [Fact]
    public void LiveHandoffRetainsControlAndTimingBudgets()
    {
        MonitoredWorkerExecutor.LiveReadinessPath.Should().Be("/weatherforecast");
        LiveSampleProcess.CleanupTimeout.Should().Be(TimeSpan.FromSeconds(5));
        (64 * 3 + 2 + 4).Should().BeLessThan(PrevalidationMonitorControl.MaximumFrames);
        PrevalidationMonitorControl.FullWindowOutputBytes.Should().Be(2_876_736);
    }

    private static Process StartReadyHandoffStub()
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("printf 'ready\\n'; read release");
        var child = Process.Start(start)!;
        child.StandardOutput.ReadLine().Should().Be("ready");
        return child;
    }

    private sealed class HandoffClock : TimeProvider, IDisposable
    {
        private HandoffTimer? _timer;
        internal int Created { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Created++;
            dueTime.Should().Be(LiveSampleProcess.CleanupTimeout);
            period.Should().Be(Timeout.InfiniteTimeSpan);
            return _timer = new HandoffTimer(callback, state);
        }
        internal void Fire() => _timer!.Fire();
        public void Dispose() => _timer?.Dispose();
    }

    private sealed class HandoffTimer(TimerCallback callback, object? state) : ITimer
    {
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HandoffWorkerLauncher(MonitoredProcessIdentity target) : IMonitoredWorkerLauncher
    {
        public Process Start(IMonitoredExecutionManifest manifest, string descriptorPath)
        {
            var start = new ProcessStartInfo("/bin/bash")
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.Environment["TARGET_PID"] = target.ProcessId.ToString(CultureInfo.InvariantCulture);
            start.Environment["TARGET_START"] = target.LinuxStartTimeTicks.ToString(CultureInfo.InvariantCulture);
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("""
                read -r -a stat_fields < "/proc/$$/stat"
                pid=$$
                start="${stat_fields[21]}"
                printf '{"type":"process","processId":%s,"processStartTimeTicks":%s,"processRole":"diagnostic"}\n' "$pid" "$start"
                printf '{"type":"process","processId":%s,"processStartTimeTicks":%s,"processRole":"target"}\n' "$TARGET_PID" "$TARGET_START"
                printf '{"type":"process-terminating","processId":%s,"processStartTimeTicks":%s,"processRole":"target"}\n' "$TARGET_PID" "$TARGET_START"
                IFS= read -r release || exit 21
                [[ "$release" == "release:process-termination:$TARGET_PID:$TARGET_START" ]] || exit 22
                printf '{"type":"process-terminated","processId":%s,"processStartTimeTicks":%s,"processRole":"target"}\n' "$TARGET_PID" "$TARGET_START"
                printf '{"type":"process-terminating","processId":%s,"processStartTimeTicks":%s,"processRole":"diagnostic"}\n' "$pid" "$start"
                IFS= read -r release || exit 23
                [[ "$release" == "release:process-termination:$pid:$start" ]] || exit 24
                printf '{"type":"completed"}\n'
                """);
            var process = Process.Start(start)!;
            process.StandardOutput.Peek().Should().BeGreaterThanOrEqualTo(0);
            return process;
        }
    }
}
