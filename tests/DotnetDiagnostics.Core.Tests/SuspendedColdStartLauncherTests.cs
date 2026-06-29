using DotnetDiagnostics.Core.Launch;
using DotnetDiagnostics.Core.Startup;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Live cold-start capture (issue #446): launch CoreClrSample SUSPENDED on a reverse-connect diagnostic
/// port, arm the startup session before any managed code runs, resume, and prove that pre-attach DI
/// container build (ServiceProviderBuilt) is captured — an event the post-attach path always misses.
/// </summary>
[Collection("LiveProcess")]
public sealed class SuspendedColdStartLauncherTests
{
    [Fact(Timeout = 60_000)]
    public async Task ColdStart_CapturesPreAttach_DiServiceProviderBuilt()
    {
        var sampleDll = SuspendedColdStartLauncherTests.LocateSampleDll("CoreClrSample");
        if (sampleDll is null)
        {
            throw SkipException.ForReason("CoreClrSample.dll not found. Build the sample before running this test.");
        }

        await using var target = await SuspendedColdStartLauncher.LaunchSuspendedAsync(
            "dotnet",
            new[] { sampleDll, "--urls", "http://127.0.0.1:0" },
            consoleSink: null,
            connectTimeout: TimeSpan.FromSeconds(30));

        target.HasExited.Should().BeFalse("the launched runtime is suspended waiting on the diagnostic port");

        var collector = new EventPipeStartupCollector();
        var snapshot = await collector.CollectColdStartAsync(target, TimeSpan.FromSeconds(8));

        // The single ServiceProvider build happens once at startup; a post-attach collector cannot see
        // it. Cold start arms the session before resume, so it is captured.
        snapshot.TotalDiEvents.Should().BeGreaterThan(0, "cold-start arms EventPipe before DI is built");
        snapshot.DiServiceProviderBuiltCount.Should().BeGreaterThanOrEqualTo(1);
        snapshot.Notes.Should().Contain(n => n.Contains("Cold-start capture", StringComparison.Ordinal));
    }

    private static string? LocateSampleDll(string sampleName)
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var projectDir = Path.Combine(probe, "samples", sampleName);
            if (Directory.Exists(projectDir))
            {
                foreach (var configuration in new[] { "Release", "Debug" })
                {
                    var dll = Path.Combine(projectDir, "bin", configuration, "net10.0", $"{sampleName}.dll");
                    if (File.Exists(dll))
                    {
                        return dll;
                    }
                }

                return null;
            }

            probe = Path.GetFullPath(Path.Combine(probe, ".."));
        }

        return null;
    }
}
