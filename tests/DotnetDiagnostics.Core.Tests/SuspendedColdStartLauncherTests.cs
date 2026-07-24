using System.Diagnostics;
using System.Text;
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
    [Fact]
    public void CreatePortPath_ShortUnixTempPath_UsesPreferredDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string preferredDirectory = "/coldstart";

        var portPath = SuspendedColdStartLauncher.CreatePortPath(preferredDirectory, "/fallback");

        portPath.Should().StartWith(preferredDirectory + Path.DirectorySeparatorChar);
    }

    [Fact]
    public void CreatePortPath_LongUnixTempPath_UsesShortFallback()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var longTempPath = Path.Combine(
            Path.GetPathRoot(AppContext.BaseDirectory)!,
            "deliberately-long-temp-root",
            new string('x', SuspendedColdStartLauncher.MaxUnixSocketPathBytes));
        const string shortFallback = "/coldstart";

        var portPath = SuspendedColdStartLauncher.CreatePortPath(longTempPath, shortFallback);

        portPath.Should().StartWith(shortFallback + Path.DirectorySeparatorChar);
        Encoding.UTF8.GetByteCount(portPath).Should().BeLessThanOrEqualTo(
            SuspendedColdStartLauncher.MaxUnixSocketPathBytes);
    }

    [Fact]
    public void CreatePortPath_WhenNoUnixPathFits_ThrowsActionableError()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var longDirectory = Path.Combine(
            Path.GetPathRoot(AppContext.BaseDirectory)!,
            new string('é', SuspendedColdStartLauncher.MaxUnixSocketPathBytes));

        var action = () => SuspendedColdStartLauncher.CreatePortPath(longDirectory, longDirectory);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unix-domain socket paths must be at most*Set TMPDIR to a shorter writable directory*");
    }

    [Fact]
    public async Task LaunchedTarget_Dispose_RemovesObservedUnixDiagnosticArtifacts()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "long-launch-temp",
            Guid.NewGuid().ToString("N"));
        var longTempPath = Path.Combine(testRoot, new string('x', 48), new string('y', 48));
        Directory.CreateDirectory(longTempPath);

        try
        {
            await using var target = ChildProcessLauncher.Launch(
                "/bin/sh",
                ["-c", "sleep 30"],
                environment: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["TMPDIR"] = longTempPath,
                });
            target.DiagnosticArtifactIdentity.Should().NotBeNullOrWhiteSpace();
            var identity = target.DiagnosticArtifactIdentity!;

            var artifacts = new[]
            {
                Path.Combine(longTempPath, $"dotnet-diagnostic-{target.ProcessId}-{identity}-socket"),
                Path.Combine(longTempPath, $"clr-debug-pipe-{target.ProcessId}-{identity}-in"),
                Path.Combine(longTempPath, $"clr-debug-pipe-{target.ProcessId}-{identity}-out"),
            };
            foreach (var artifact in artifacts)
            {
                await File.WriteAllTextAsync(artifact, "test");
            }

            await target.DisposeAsync();

            artifacts.Should().OnlyContain(path => !File.Exists(path));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchedTarget_Dispose_RemovesArtifactsCreatedAfterKillBeforeReaping(bool useAsyncDispose)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "termination-race-temp",
            Guid.NewGuid().ToString("N"));
        var longTempPath = Path.Combine(testRoot, new string('x', 48), new string('y', 48));
        Directory.CreateDirectory(longTempPath);

        LaunchedTarget? target = null;
        try
        {
            var startInfo = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("sleep 30");
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start disposal race test process.");
            string? lateArtifact = null;
            string? reusedPidArtifact = null;

            target = new LaunchedTarget(
                process,
                [longTempPath],
                postKillObserver: (pid, identity) =>
                {
                    lateArtifact = Path.Combine(longTempPath, $"dotnet-diagnostic-{pid}-{identity}-socket");
                    reusedPidArtifact = Path.Combine(longTempPath, $"dotnet-diagnostic-{pid}-{identity}1-socket");
                    File.WriteAllText(lateArtifact, "late");
                    File.WriteAllText(reusedPidArtifact, "reused-pid");
                });

            if (useAsyncDispose)
            {
                await target.DisposeAsync();
            }
            else
            {
                target.Dispose();
            }

            lateArtifact.Should().NotBeNull();
            reusedPidArtifact.Should().NotBeNull();
            File.Exists(lateArtifact!).Should().BeFalse(
                "the pre-reap scan must catch artifacts created after kill was signaled");
            File.Exists(reusedPidArtifact!).Should().BeTrue(
                "cleanup must not delete a same-pid artifact with a different launch identity");
        }
        finally
        {
            if (target is not null)
            {
                await target.DisposeAsync();
            }

            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ColdStart_CapturesPreAttach_DiServiceProviderBuilt()
    {
        var sampleDll = SuspendedColdStartLauncherTests.LocateSampleDll("CoreClrSample");
        if (sampleDll is null)
        {
            throw SkipException.ForReason("CoreClrSample.dll not found. Build the sample before running this test.");
        }

        string portPath;
        await using (var target = await SuspendedColdStartLauncher.LaunchSuspendedAsync(
            "dotnet",
            new[] { sampleDll, "--urls", "http://127.0.0.1:0" },
            consoleSink: null,
            connectTimeout: TimeSpan.FromSeconds(30)))
        {
            portPath = target.DiagnosticPortPath;
            File.Exists(portPath).Should().BeTrue("the launcher owns a live reverse-connect socket");
            target.HasExited.Should().BeFalse("the launched runtime is suspended waiting on the diagnostic port");

            var collector = new EventPipeStartupCollector();
            var snapshot = await collector.CollectColdStartAsync(target, TimeSpan.FromSeconds(8));

            // The single ServiceProvider build happens once at startup; a post-attach collector cannot see
            // it. Cold start arms the session before resume, so it is captured.
            snapshot.TotalDiEvents.Should().BeGreaterThan(0, "cold-start arms EventPipe before DI is built");
            snapshot.DiServiceProviderBuiltCount.Should().BeGreaterThanOrEqualTo(1);
            snapshot.Notes.Should().Contain(n => n.Contains("Cold-start capture", StringComparison.Ordinal));
        }

        File.Exists(portPath).Should().BeFalse("disposing the target removes the launcher-owned socket");
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
