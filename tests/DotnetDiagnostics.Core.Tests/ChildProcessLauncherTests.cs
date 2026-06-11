using DotnetDiagnostics.Core.Launch;
using FluentAssertions;
using Microsoft.Diagnostics.NETCore.Client;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Live round-trip for the <see cref="ChildProcessLauncher"/> primitive behind the CLI's opt-in
/// <c>--launch</c> dev mode (issue #365): spawn the CoreClrSample as a child, wait for its diagnostic
/// endpoint, and confirm the child is terminated on dispose.
/// </summary>
[Collection("LiveProcess")]
public sealed class ChildProcessLauncherTests
{
    [Fact]
    public async Task Launch_SpawnsChild_WaitsForEndpoint_AndKillsOnDispose()
    {
        var sampleDll = LocateSampleDll("CoreClrSample");
        if (sampleDll is null)
        {
            throw SkipException.ForReason("CoreClrSample.dll not found. Build the sample before running this test.");
        }

        int pid;
        await using (var target = ChildProcessLauncher.Launch(
            "dotnet",
            new[] { sampleDll, "--urls", "http://127.0.0.1:0" }))
        {
            pid = target.ProcessId;
            target.HasExited.Should().BeFalse();

            var ready = await ChildProcessLauncher.WaitForDiagnosticEndpointAsync(
                pid, TimeSpan.FromSeconds(30));

            ready.Should().BeTrue("the launched child should advertise a diagnostic endpoint");
            DiagnosticsClient.GetPublishedProcesses().Should().Contain(pid);
        }

        // After dispose the child is killed (best-effort) and no longer publishes an endpoint.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && DiagnosticsClient.GetPublishedProcesses().Contains(pid))
        {
            await Task.Delay(250);
        }

        DiagnosticsClient.GetPublishedProcesses().Should().NotContain(pid);
    }

    [Fact]
    public void Launch_NullOrEmptyFileName_Throws()
    {
        Assert.Throws<ArgumentException>(() => ChildProcessLauncher.Launch(" ", Array.Empty<string>()));
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
