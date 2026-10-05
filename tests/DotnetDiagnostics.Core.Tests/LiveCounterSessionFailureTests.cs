using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.TestSupport;
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
        await using var session = await new EventPipeCounterCollector().StartAsync(
            target.ProcessId,
            new CounterSessionOptions { Providers = ["System.Runtime"] });

        target.Process.Kill(entireProcessTree: true);
        await target.Process.WaitForExitAsync();

        var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        completion.Status.Should().Be(CounterSessionStatus.TargetExited);
    }
}
