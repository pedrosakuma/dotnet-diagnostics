using DotnetDiagnostics.Core.Counters;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class LiveCounterSessionFailureTests
{
    [Fact(Timeout = 60_000)]
    public async Task CounterSession_WhenTargetExits_ReportsTargetExit()
    {
        await using var target = await LiveSampleProcess.StartPublishedAsync(
            "CoreClrSample",
            new LiveSampleOptions
            {
                BindHttpPort = false,
                DiagnosticTimeout = TimeSpan.FromSeconds(30),
            });
        await using var session = new EventPipeCounterCollector().CreateSession(
            target.ProcessId,
            new CounterSessionOptions { Providers = ["System.Runtime"] });
        using var subscription = session.Attach<CounterObservation>((_, _) => ValueTask.CompletedTask);
        await session.StartAsync();

        target.Process.Kill(entireProcessTree: true);
        await target.Process.WaitForExitAsync();

        var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        completion.Status.Should().Be(DiagnosticSessionStatus.TargetExited);
    }

    [Fact]
    public void DetermineStatus_WhenHandlerFails_ReportsFailure()
    {
        CounterSession.DetermineStatus(
                stopRequested: true,
                targetAlive: true,
                processingError: null,
                shutdownError: null,
                dispatchError: new InvalidOperationException("Handler failed."))
            .Should().Be(DiagnosticSessionStatus.Failed);
    }
}
