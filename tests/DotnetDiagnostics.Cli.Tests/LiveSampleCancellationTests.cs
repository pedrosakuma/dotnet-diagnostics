using System.Diagnostics;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class LiveSampleCancellationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UrlTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task PreCancellationPreventsEvenSampleLookupAndDiagnosticPolling()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        var start = () => LiveSampleProcess.StartPublishedAsync("missing-sample-must-not-be-looked-up", null, caller.Token);
        await start.Should().ThrowAsync<OperationCanceledException>();
        var diagnostic = () => DiagnosticReadiness.WaitForDiagnosticEndpointAsync(-1, UrlTimeout, caller.Token);
        await diagnostic.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingUrlCancellationDoesNotReturnBeforeOwnedExitAndBothDrains(bool stageExpires)
    {
        var urlClock = new HttpReadinessTests.ControlledClock();
        var cleanupClock = new HttpReadinessTests.ControlledClock();
        using var caller = new CancellationTokenSource();
        var url = Signal<string>();
        var exit = Signal();
        var stdout = Signal();
        var stderr = Signal();
        var terminationRequested = Signal();
        var startup = LiveSampleProcess.CompleteStartupAsync(
            () => LiveSampleProcess.WaitForUrlAsync(url.Task, "OwnedSample.dll", UrlTimeout, urlClock, caller.Token),
            () => new ValueTask(LiveSampleProcess.ObserveCleanupAsync(() =>
            {
                url.TrySetCanceled();
                terminationRequested.TrySetResult();
            }, exit.Task, stdout.Task, stderr.Task, LiveSampleProcess.CleanupTimeout, cleanupClock)));
        var urlDeadline = await urlClock.NextTimerAsync(UrlTimeout);
        try
        {
            if (stageExpires) urlDeadline.Fire();
            else await caller.CancelAsync();
            await terminationRequested.Task.WaitAsync(Bound);
            startup.IsCompleted.Should().BeFalse("canceling a URL promise is not owned process/readers completion");
            exit.TrySetResult();
            startup.IsCompleted.Should().BeFalse();
            stdout.TrySetResult();
            startup.IsCompleted.Should().BeFalse("stderr still belongs to startup cleanup");
            stderr.TrySetResult();
            var observe = () => startup.WaitAsync(Bound);
            if (stageExpires)
                (await observe.Should().ThrowAsync<SkipException>()).Which.Message.Should().Contain("did not advertise");
            else await observe.Should().ThrowAsync<OperationCanceledException>();
            (await cleanupClock.NextTimerAsync(LiveSampleProcess.CleanupTimeout)).Disposed.Should().BeTrue();
            url.Task.IsCanceled.Should().BeTrue();
            Task.WhenAll(exit.Task, stdout.Task, stderr.Task).IsCompletedSuccessfully.Should().BeTrue();
        }
        finally
        {
            exit.TrySetResult();
            stdout.TrySetResult();
            stderr.TrySetResult();
            await ObserveFailureAsync(startup);
        }
    }

    [Fact]
    public async Task CallerCancellationWinsIfUrlStageAlsoExpires()
    {
        var clock = new HttpReadinessTests.ControlledClock();
        using var caller = new CancellationTokenSource();
        var url = Signal<string>();
        var wait = LiveSampleProcess.WaitForUrlAsync(url.Task, "sample.dll", UrlTimeout, clock, caller.Token);
        var deadline = await clock.NextTimerAsync(UrlTimeout);
        await caller.CancelAsync();
        deadline.Fire();
        var observe = () => wait.WaitAsync(Bound);
        await observe.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanupDeadlineReportsUnfinishedOwnedExitOrDrain(bool pendingExit)
    {
        var clock = new HttpReadinessTests.ControlledClock();
        var pending = Signal();
        var cleanup = LiveSampleProcess.ObserveCleanupAsync(() => { },
            pendingExit ? pending.Task : Task.CompletedTask, Task.CompletedTask,
            pendingExit ? Task.CompletedTask : pending.Task, LiveSampleProcess.CleanupTimeout, clock);
        var deadline = await clock.NextTimerAsync(LiveSampleProcess.CleanupTimeout);
        try
        {
            deadline.Fire();
            var observe = () => cleanup.WaitAsync(Bound);
            (await observe.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("WaitingForActivation");
            pending.Task.IsCompleted.Should().BeFalse("a timed-out observation must not claim owned completion");
        }
        finally { pending.TrySetResult(); }
    }

    [Fact]
    public async Task StartupPreservesOriginalCancellationAndExplicitCleanupFailure()
    {
        var primary = new OperationCanceledException("owned startup canceled");
        var cleanup = new IOException("owned pipe drain failed");
        var action = () => LiveSampleProcess.CompleteStartupAsync(
            () => Task.FromException(primary), () => ValueTask.FromException(cleanup));
        var failure = (await action.Should().ThrowAsync<AggregateException>()).Which;
        failure.InnerExceptions.Should().Equal(primary, cleanup);
    }

    [Fact]
    public async Task TerminationFailureStillObservesExitAndDrainsThenRemainsAFailure()
    {
        var clock = new HttpReadinessTests.ControlledClock();
        var exit = Signal();
        var stdout = Signal();
        var stderr = Signal();
        var failure = new InvalidOperationException("owned kill failed");
        var cleanup = LiveSampleProcess.ObserveCleanupAsync(() => throw failure,
            exit.Task, stdout.Task, stderr.Task, LiveSampleProcess.CleanupTimeout, clock);
        cleanup.IsCompleted.Should().BeFalse();
        exit.TrySetResult();
        stdout.TrySetResult();
        cleanup.IsCompleted.Should().BeFalse();
        stderr.TrySetResult();
        var observe = () => cleanup.WaitAsync(Bound);
        (await observe.Should().ThrowAsync<AggregateException>()).Which.InnerExceptions.Should().Contain(failure);
    }

    [Fact]
    public async Task ReaderFailureCannotBecomeSuccessfulCleanup()
    {
        var failure = new IOException("owned stdout read failed");
        var action = () => LiveSampleProcess.ObserveCleanupAsync(() => { }, Task.CompletedTask,
            Task.FromException(failure), Task.CompletedTask, LiveSampleProcess.CleanupTimeout, new HttpReadinessTests.ControlledClock());
        (await action.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task NormalCleanupAlsoWaitsForBothLineReaderTasks()
    {
        var stdout = Signal();
        var stderr = Signal();
        var cleanup = LiveSampleProcess.ObserveCleanupAsync(() => { }, Task.CompletedTask,
            stdout.Task, stderr.Task, LiveSampleProcess.CleanupTimeout, new HttpReadinessTests.ControlledClock());
        cleanup.IsCompleted.Should().BeFalse();
        stdout.TrySetResult();
        cleanup.IsCompleted.Should().BeFalse();
        stderr.TrySetResult();
        await cleanup.WaitAsync(Bound);
    }

    [Fact(Timeout = 15_000)]
    public async Task OwnedHelperCancellationObservesActualExitAndLineReaderEof()
    {
        var windows = OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(windows ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (windows) start.ArgumentList.Add("/d");
        start.ArgumentList.Add(windows ? "/c" : "-c");
        start.ArgumentList.Add(windows
            ? "echo owned-out & echo owned-error 1>&2 & set /p hold="
            : "printf 'owned-out\\n'; printf 'owned-error\\n' >&2; read hold");
        using var process = Process.Start(start)!;
        var outReady = Signal();
        var errorReady = Signal();
        var stdout = DrainAsync(process.StandardOutput, outReady);
        var stderr = DrainAsync(process.StandardError, errorReady);
        using var caller = new CancellationTokenSource();
        var pendingUrl = Signal<string>();
        Task? startup = null;
        try
        {
            await Task.WhenAll(outReady.Task, errorReady.Task).WaitAsync(Bound);
            process.HasExited.Should().BeFalse();
            startup = LiveSampleProcess.CompleteStartupAsync(
                () => LiveSampleProcess.WaitForUrlAsync(pendingUrl.Task, "owned-helper", UrlTimeout, TimeProvider.System, caller.Token),
                () => new ValueTask(LiveSampleProcess.ObserveCleanupAsync(
                    () => { pendingUrl.TrySetCanceled(); process.Kill(entireProcessTree: true); }, process.WaitForExitAsync(),
                    stdout, stderr, LiveSampleProcess.CleanupTimeout, TimeProvider.System)));
            await caller.CancelAsync();
            var observe = () => startup.WaitAsync(Bound);
            await observe.Should().ThrowAsync<OperationCanceledException>();
            process.HasExited.Should().BeTrue();
            stdout.IsCompletedSuccessfully.Should().BeTrue();
            stderr.IsCompletedSuccessfully.Should().BeTrue();
        }
        finally
        {
            await caller.CancelAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr).WaitAsync(Bound);
            if (startup is not null) await ObserveFailureAsync(startup);
        }
    }

    private static async Task DrainAsync(StreamReader reader, TaskCompletionSource ready)
    {
        using (reader)
        {
            while (await reader.ReadLineAsync() is not null) ready.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false, 23, 20, 30_000)]
    [InlineData(true, 33, 25, 40_000)]
    public async Task CallerBudgetReservesCleanupAndKeepsBodyDeadlineLinked(bool positive, int workSeconds,
        int bodySeconds, int outerMs)
    {
        var outer = positive ? CliGcActivitiesLiveTests.PositiveOuterTimeoutMs : CliGcActivitiesLiveTests.NegativeOuterTimeoutMs;
        var body = positive ? CliGcActivitiesLiveTests.PositiveBodyTimeout : CliGcActivitiesLiveTests.NegativeBodyTimeout;
        outer.Should().Be(outerMs);
        var method = typeof(CliGcActivitiesLiveTests).GetMethod(positive
            ? nameof(CliGcActivitiesLiveTests.LiveWorkflow_ObservedBothStreamsBeforeTargetedGcSpan_AndQueriesRealArtifacts)
            : nameof(CliGcActivitiesLiveTests.CancellationOrTargetExitStopsBothOwnedStreamsBeforeRequestedLongWindow))!;
        ((TheoryAttribute)Attribute.GetCustomAttribute(method, typeof(TheoryAttribute))!).Timeout.Should().Be(outerMs);
        body.Should().Be(TimeSpan.FromSeconds(bodySeconds));
        CliGcActivitiesLiveTests.WorkTimeout(outer).Should().Be(TimeSpan.FromSeconds(workSeconds));
        (CliGcActivitiesLiveTests.WorkTimeout(outer) + LiveSampleProcess.CleanupTimeout + TimeSpan.FromSeconds(2))
            .Should().Be(TimeSpan.FromMilliseconds(outer));
        using var work = new CancellationTokenSource();
        using var deadline = CliGcActivitiesLiveTests.CreateBodyDeadline(body, work.Token);
        deadline.IsCancellationRequested.Should().BeFalse();
        await work.CancelAsync();
        deadline.IsCancellationRequested.Should().BeTrue();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ObserveFailureAsync(Task task)
    {
        try { await task.WaitAsync(Bound); }
        catch (OperationCanceledException) { }
        catch (SkipException) { }
    }
}
