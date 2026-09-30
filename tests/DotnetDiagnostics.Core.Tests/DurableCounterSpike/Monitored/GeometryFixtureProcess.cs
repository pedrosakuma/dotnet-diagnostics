using System.Diagnostics;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal static class GeometryFixtureProcess
{
    internal static bool IsChild { get; private set; }

    private static async Task<int> Main(string[] args)
    {
        if (args is not ["--geometry-fixture", "ordered" or "failure" or "cancel"])
        {
            Console.Error.WriteLine("Expected --geometry-fixture ordered|failure|cancel.");
            return 2;
        }

        IsChild = true;
        try
        {
            Console.WriteLine($"Geometry worker PID: {Environment.ProcessId}");
            using var fixture = new MonitoredRunnerTests(new ConsoleOutput());
            if (args[1] == "ordered")
                await fixture.GeometryFixtureOrderedConstructionMeasures539Plus32AndStrictCleanup();
            else
                await fixture.GeometryFixtureConstructionFailureClosesOwnedHandlesWithoutBlockingObserver(
                    args[1] == "cancel");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    internal static async Task RunAsync(string scenario, ITestOutputHelper output)
    {
        // The monitor's absolute harness RSS limit applies to a fresh worker, not the
        // long-lived xUnit host's retained heap from unrelated allocation-heavy tests.
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(GeometryFixtureProcess).Assembly.Location);
        start.ArgumentList.Add("--geometry-fixture");
        start.ArgumentList.Add(scenario);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Geometry worker did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var text = await stdout;
            var errors = await stderr;
            output.WriteLine(text);
            output.WriteLine(errors);
            Assert.True(process.ExitCode == 0, $"Geometry worker exited {process.ExitCode}:\n{errors}\n{text}");
            Assert.Contains($"Geometry worker PID: {process.Id}", text);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanupDeadline.Token);
            }
        }
    }

    private sealed class ConsoleOutput : ITestOutputHelper
    {
        public void WriteLine(string message) => Console.WriteLine(message);
        public void WriteLine(string format, params object[] args) => Console.WriteLine(format, args);
    }
}
