using System.Diagnostics;
using System.Diagnostics.Tracing;
using DotnetDiagnosticsMcp.Core.Internal;
using FluentAssertions;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class EventPipeSessionStartTests
{
    [Fact]
    public async Task StartEventPipeSessionWithTimeoutAsync_InvalidPid_ReturnsQuickly()
    {
        var client = new DiagnosticsClient(int.MaxValue);
        var providers = new[]
        {
            new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational),
        };

        using var callerCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stopwatch = Stopwatch.StartNew();

        var exceptionTask = Record.ExceptionAsync(() => client.StartEventPipeSessionWithTimeoutAsync(
            providers,
            requestRundown: false,
            circularBufferMB: 64,
            TimeSpan.FromMilliseconds(200),
            callerCts.Token));

        var completed = await Task.WhenAny(exceptionTask, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None))
            .ConfigureAwait(false);

        stopwatch.Stop();

        completed.Should().Be(exceptionTask, "session startup against an invalid PID must not hang indefinitely");
        var exception = await exceptionTask.ConfigureAwait(false);
        exception.Should().NotBeNull();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }
}
