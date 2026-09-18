using System.Diagnostics.Tracing;
using System.Text.Json;
using DotnetDiagnostics.Core.Exceptions;
using FluentAssertions;
using Microsoft.Diagnostics.NETCore.Client;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

internal static class CrashGuardLiveContract
{
    internal static async Task AssertAsync(ITestOutputHelper output, bool exitAfterSnapshot)
    {
        output.WriteLine($"sample-launch-request at={DateTimeOffset.UtcNow:O}; exitAfterSnapshot={exitAfterSnapshot}");
        await using var sample = await LiveSampleProcess.StartPublishedAsync("BadCodeSample",
            new LiveSampleOptions
            {
                WaitForHttpReady = true,
                Environment = new Dictionary<string, string> { ["BADCODE_CRASH_GUARD_FIXTURE"] = "1" },
            });
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var configured = NewSignal();
        var ready = NewSignal();
        var terminating = NewSignal();
        var fatalObserved = NewSignal();
        var windowEnded = NewSignal();
        var collector = new EventPipeCrashGuardCollector
        {
            ReadinessProvider = new EventPipeProvider("DotnetDiagnostics.CrashGuardFixture", EventLevel.Informational),
            ConfigureReadiness = source =>
            {
                source.Dynamic.All += e =>
                {
                    if (e.ProviderName != "DotnetDiagnostics.CrashGuardFixture") return;
                    if ((int)e.ID == 1) ready.TrySetResult();
                };
                configured.TrySetResult();
            },
            ExceptionObserved = e =>
            {
                if (e.ExceptionMessage.Contains("controlled crash fixture", StringComparison.Ordinal))
                    fatalObserved.TrySetResult();
            },
            ObservationWindowEnded = windowEnded.Task,
        };
        output.WriteLine($"sample-start pid={sample.ProcessId} at={DateTimeOffset.UtcNow:O}; exitAfterSnapshot={exitAfterSnapshot}");
        var capture = collector.CollectAsync(sample.ProcessId, TimeSpan.FromSeconds(14), 5, deadline.Token);
        try
        {
            await configured.Task.WaitAsync(deadline.Token);
            await RequestAsync("/crash-guard-fixture/ready");
            await ready.Task.WaitAsync(deadline.Token);
            output.WriteLine($"stream-ready at={DateTimeOffset.UtcNow:O}");
            await RequestAsync("/exceptions?count=30");
            await RequestAsync("/crash-guard-fixture/crash");
            output.WriteLine($"request terminating-notification at={DateTimeOffset.UtcNow:O}");
            using (var response = await http.GetAsync("/crash-guard-fixture/terminating", deadline.Token))
            {
                response.EnsureSuccessStatusCode();
                (await response.Content.ReadAsStringAsync(deadline.Token)).Should().Be("true");
                terminating.SetResult();
            }
            await fatalObserved.Task.WaitAsync(deadline.Token);
            output.WriteLine($"terminating-notification-and-runtime-exception at={DateTimeOffset.UtcNow:O}");

            if (exitAfterSnapshot) windowEnded.SetResult();
            else await RequestAsync("/crash-guard-fixture/release", allowDisconnect: true);

            var snapshot = await capture;
            output.WriteLine(JsonSerializer.Serialize(snapshot));
            snapshot.Observation.Should().NotBeNull();
            snapshot.TotalExceptions.Should().BeGreaterThan(5);
            snapshot.Observation!.LastObservedException.Should().NotBeNull();
            snapshot.Observation.LastObservedException!.ExceptionMessage.Should().Contain("controlled crash fixture");

            if (exitAfterSnapshot)
            {
                snapshot.Observation.StreamCompleted.Should().BeTrue();
                snapshot.Observation.DrainCompleted.Should().BeTrue();
                snapshot.Observation.ShutdownError.Should().BeNull();
                snapshot.Observation.EventsLost.Should().Be(0);
                snapshot.Observation.ProcessingError.Should().BeNull();
                sample.Process.HasExited.Should().BeFalse("the terminating callback is held by our release barrier");
                snapshot.ProcessExited.Should().BeFalse();
                snapshot.ExitCode.Should().BeNull();
                snapshot.UnhandledExceptionObserved.Should().Be(snapshot.Observation.ExplicitCrashEventObserved,
                    "later OS exit must not be inferred into an earlier snapshot");
                if (!snapshot.Observation.ExplicitCrashEventObserved) snapshot.FinalException.Should().BeNull();
                var beforeExit = JsonSerializer.Serialize(snapshot);
                await RequestAsync("/crash-guard-fixture/release", allowDisconnect: true);
                await sample.Process.WaitForExitAsync(deadline.Token);
                new DateTimeOffset(sample.Process.ExitTime.ToUniversalTime())
                    .Should().BeAfter(snapshot.StartedAt + snapshot.Duration);
                JsonSerializer.Serialize(snapshot).Should().Be(beforeExit);
            }
            else
            {
                snapshot.ProcessExited.Should().BeTrue();
                // Abrupt exit can truncate the stream, and a non-owning Process cannot always
                // recover an exit code on Unix. Require the fixture owner's actual code below.
                snapshot.ExitCode.Should().NotBe(0);
                snapshot.UnhandledExceptionObserved.Should().BeTrue();
                snapshot.FinalException.Should().NotBeNull();
                snapshot.FinalException!.IsUnhandled.Should().BeTrue();
                snapshot.FinalException.ExceptionType.Should().Contain("InvalidOperationException");
                snapshot.FinalException.ExceptionMessage.Should().Contain("controlled crash fixture");
                snapshot.FinalException.ManagedStack.Should().NotBeEmpty();
                snapshot.Exceptions.Should().HaveCount(5).And.Contain(snapshot.FinalException);
            }
            sample.Process.ExitCode.Should().NotBe(0);
            output.WriteLine($"exit pid={sample.ProcessId} code={sample.Process.ExitCode} at={sample.Process.ExitTime.ToUniversalTime():O}");
        }
        finally
        {
            windowEnded.TrySetResult();
            if (!sample.Process.HasExited)
            {
                try { await RequestAsync("/crash-guard-fixture/release", allowDisconnect: true, cleanup: true); }
                catch (OperationCanceledException) { output.WriteLine("cleanup release request timed out"); }
            }
            await deadline.CancelAsync();
            try { await capture; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            output.WriteLine($"cleanup at={DateTimeOffset.UtcNow:O}; processExited={sample.Process.HasExited}; " +
                $"configured={configured.Task.IsCompletedSuccessfully}; ready={ready.Task.IsCompletedSuccessfully}; " +
                $"terminating={terminating.Task.IsCompletedSuccessfully}; exception={fatalObserved.Task.IsCompletedSuccessfully}");
        }

        async Task RequestAsync(string path, bool allowDisconnect = false, bool cleanup = false)
        {
            output.WriteLine($"request {path} at={DateTimeOffset.UtcNow:O}");
            try
            {
                using var response = await http.GetAsync(path, cleanup ? CancellationToken.None : deadline.Token);
                response.EnsureSuccessStatusCode();
                output.WriteLine($"response {path} status={(int)response.StatusCode} at={DateTimeOffset.UtcNow:O}");
            }
            catch (HttpRequestException) when (allowDisconnect)
            {
                output.WriteLine($"release disconnected at={DateTimeOffset.UtcNow:O}");
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
