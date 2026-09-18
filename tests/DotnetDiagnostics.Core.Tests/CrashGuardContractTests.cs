using DotnetDiagnostics.Core.Exceptions;
using FluentAssertions;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class CrashGuardContractTests
{
    private static readonly CrashGuardExceptionEvent Exception = new(DateTimeOffset.UnixEpoch,
        "System.InvalidOperationException", "real exception", "0x80131509", 1, "ExceptionThrown_V1",
        false, ["at Fixture.Throw()"]);

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, 134, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 134, true)]
    [InlineData(true, null, true)]
    public void FinalEvidenceUsesOnlyExitAvailableAtSnapshot(bool exited, int? exitCode, bool expected)
    {
        var result = CrashGuardFinalEvidence.Resolve(exited, exitCode, false, null, Exception);
        result.UnhandledObserved.Should().Be(expected);
        result.InferredFromExit.Should().Be(expected);
        if (expected) result.FinalException!.IsUnhandled.Should().BeTrue();
        if (!exited) result.FinalException.Should().BeNull();
    }

    [Fact]
    public void LaterNonzeroExitDoesNotRewriteEarlierEvidence()
    {
        var before = CrashGuardFinalEvidence.Resolve(false, null, false, null, Exception);
        var after = CrashGuardFinalEvidence.Resolve(true, 134, false, null, Exception);
        before.UnhandledObserved.Should().BeFalse();
        before.FinalException.Should().BeNull();
        after.UnhandledObserved.Should().BeTrue();
        after.FinalException.Should().NotBeNull();
    }

    [Fact]
    public void NonzeroExitWithoutAnExceptionIsNotUnhandledEvidence()
        => CrashGuardFinalEvidence.Resolve(true, 134, false, null, null).UnhandledObserved.Should().BeFalse();

    [Fact]
    public void ExplicitMarkerRemainsDistinctFromExitInference()
    {
        var explicitException = Exception with { IsUnhandled = true, EventName = "UnhandledException" };
        var result = CrashGuardFinalEvidence.Resolve(false, null, true, explicitException, Exception);
        result.UnhandledObserved.Should().BeTrue();
        result.InferredFromExit.Should().BeFalse();
        result.FinalException.Should().Be(explicitException);
    }

    [Fact]
    public void ExplicitMarkerWithoutPayloadDoesNotInventAFinalExceptionWhileAlive()
    {
        var result = CrashGuardFinalEvidence.Resolve(false, null, true, null, Exception);
        result.UnhandledObserved.Should().BeTrue();
        result.InferredFromExit.Should().BeFalse();
        result.FinalException.Should().BeNull();
    }

    [Fact]
    public void ObservationSurvivesJsonWithoutPromotingFirstChanceEvidence()
    {
        var snapshot = new CrashGuardSnapshot(42, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1),
            false, null, false, 1, [new("System.InvalidOperationException", 1)], [Exception], null, [])
        {
            Observation = new(false, null, "FormatException", false, false, Exception),
        };
        var copy = System.Text.Json.JsonSerializer.Deserialize<CrashGuardSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(snapshot))!;
        copy.UnhandledExceptionObserved.Should().BeFalse();
        copy.FinalException.Should().BeNull();
        copy.Observation!.ProcessingError.Should().Be("FormatException");
        copy.Observation.EventsLost.Should().BeNull();
        copy.Observation.LastObservedException!.IsUnhandled.Should().BeFalse();
    }
}

[Collection("LiveProcess")]
public sealed class CrashGuardTemporalLiveTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public Task TerminatingNotificationDoesNotInventAnExitBeforeTheSnapshot()
        => CrashGuardLiveContract.AssertAsync(output, exitAfterSnapshot: true);

    [Fact(Timeout = 30_000)]
    public async Task ProcessingFailureIsObservableInsteadOfCompleteEmptyEvidence()
    {
        await using var sample = await LiveSampleProcess.StartPublishedAsync("CoreClrSample");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var window = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new EventPipeCrashGuardCollector
        {
            ConfigureReadiness = _ =>
            {
                window.SetResult();
                throw new InvalidOperationException("controlled parser failure");
            },
            ObservationWindowEnded = window.Task,
        };
        var snapshot = await collector.CollectAsync(sample.ProcessId, TimeSpan.FromSeconds(14), 5, deadline.Token);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(snapshot));
        snapshot.ProcessExited.Should().BeFalse();
        snapshot.UnhandledExceptionObserved.Should().BeFalse();
        snapshot.FinalException.Should().BeNull();
        snapshot.Observation!.StreamCompleted.Should().BeFalse();
        snapshot.Observation.EventsLost.Should().BeNull();
        snapshot.Observation.ProcessingError.Should().Be(nameof(InvalidOperationException));
        sample.Process.HasExited.Should().BeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationAfterObservedReadinessReturnsNoSnapshotAndLeavesTargetAlive()
    {
        output.WriteLine($"cancellation-control sample-launch-request at={DateTimeOffset.UtcNow:O}");
        await using var sample = await LiveSampleProcess.StartPublishedAsync("CoreClrSample",
            new LiveSampleOptions
            {
                WaitForHttpReady = true,
                ReadinessPath = "/weatherforecast",
            });
        output.WriteLine($"cancellation-control sample-ready pid={sample.ProcessId} at={DateTimeOffset.UtcNow:O}");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var configured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new EventPipeCrashGuardCollector
        {
            ReadinessProvider = new Microsoft.Diagnostics.NETCore.Client.EventPipeProvider(
                "System.Runtime", System.Diagnostics.Tracing.EventLevel.Informational,
                (long)System.Diagnostics.Tracing.EventKeywords.All,
                new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" }),
            ConfigureReadiness = source =>
            {
                source.Dynamic.All += e =>
                {
                    if (e.ProviderName == "System.Runtime" && e.EventName == "EventCounters")
                        ready.TrySetResult();
                };
                configured.SetResult();
            },
        };
        var capture = collector.CollectAsync(sample.ProcessId, TimeSpan.FromSeconds(14), 5, deadline.Token);
        try
        {
            await configured.Task.WaitAsync(deadline.Token);
            output.WriteLine($"cancellation-control source-configured at={DateTimeOffset.UtcNow:O}");
            await ready.Task.WaitAsync(deadline.Token);
            output.WriteLine($"cancellation-control marker-observed at={DateTimeOffset.UtcNow:O}");
            await deadline.CancelAsync();
            output.WriteLine($"cancellation-control cancellation-requested at={DateTimeOffset.UtcNow:O}");
            var action = async () => await capture;
            await action.Should().ThrowAsync<OperationCanceledException>();
            output.WriteLine($"cancellation-control collection-stopped at={DateTimeOffset.UtcNow:O}");
            sample.Process.HasExited.Should().BeFalse();
            using var response = await http.GetAsync("/weatherforecast", CancellationToken.None);
            response.EnsureSuccessStatusCode();
            output.WriteLine($"Cancellation completed after observed stream readiness; pid={sample.ProcessId} remains responsive.");
        }
        finally
        {
            output.WriteLine($"cancellation-control cleanup-enter at={DateTimeOffset.UtcNow:O}");
            await deadline.CancelAsync();
            try { await capture; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            output.WriteLine($"cancellation-control cleanup-done at={DateTimeOffset.UtcNow:O}");
        }
    }
}
