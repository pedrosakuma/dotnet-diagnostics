using System.Diagnostics;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// End-to-end tests that spawn the <c>CoreClrSample</c> webapi and exercise the diagnostic
/// pipeline against it. The sample project is built+run via <c>dotnet run</c> so the test only
/// requires the .NET SDK to be on PATH (CI satisfies this).
/// </summary>
[Collection("LiveProcess")]
public class LiveCoreClrProcessTests : IAsyncLifetime
{
    private Process? _sampleProcess;

    private int Pid => _sampleProcess?.Id ?? throw new InvalidOperationException("Sample not started.");

    public async Task InitializeAsync()
    {
        var sampleProject = LocateSampleProject();
        if (sampleProject is null)
        {
            return;
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = sampleProject,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        _sampleProcess = Process.Start(psi);
        if (_sampleProcess is null)
        {
            return;
        }

        await WaitForDiagnosticEndpointAsync(_sampleProcess.Id, TimeSpan.FromSeconds(30));
    }

    public Task DisposeAsync()
    {
        if (_sampleProcess is { HasExited: false })
        {
            try
            {
                _sampleProcess.Kill(entireProcessTree: true);
                _sampleProcess.WaitForExit(5_000);
            }
            catch (Exception)
            {
                // best-effort
            }
        }

        _sampleProcess?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void Discovery_FindsRunningSample()
    {
        EnsureSampleRunning();

        var discovery = new LocalProcessDiscovery();
        var processes = discovery.ListProcesses();
        processes.Should().Contain(p => p.ProcessId == Pid);

        var info = discovery.TryGetProcess(Pid);
        info.Should().NotBeNull();
        info!.CommandLine.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Capabilities_DetectsCoreClr()
    {
        EnsureSampleRunning();

        var detector = new CapabilityDetector();
        var caps = await detector.DetectAsync(Pid, CancellationToken.None);

        caps.Runtime.Should().Be(RuntimeFlavor.CoreClr);
        caps.CanSampleCpu.Should().BeTrue();
        caps.CanReadEventCounters.Should().BeTrue();
    }

    [Fact]
    public async Task Counters_ReturnsSystemRuntimeMetrics()
    {
        EnsureSampleRunning();

        var collector = new EventPipeCounterCollector();
        var snapshot = await collector.CollectAsync(
            Pid,
            TimeSpan.FromSeconds(3),
            providers: new[] { "System.Runtime" },
            intervalSeconds: 1,
            cancellationToken: CancellationToken.None);

        snapshot.Counters.Should().NotBeEmpty();
        snapshot.Counters.Should().Contain(c => c.Provider == "System.Runtime" && c.Name == "cpu-usage");
    }

    [Fact]
    public async Task CpuSampler_ProducesHotspots()
    {
        EnsureSampleRunning();

        var sampler = new EventPipeCpuSampler();
        var sample = await sampler.SampleAsync(
            Pid,
            TimeSpan.FromSeconds(3),
            topN: 10,
            cancellationToken: CancellationToken.None);

        sample.TotalSamples.Should().BeGreaterThan(0);
        sample.TopHotspots.Should().NotBeEmpty();
    }

    private void EnsureSampleRunning()
    {
        if (_sampleProcess is null || _sampleProcess.HasExited)
        {
            throw SkipException.ForReason("CoreClrSample is not running (could not start the sample process).");
        }
    }

    private static async Task WaitForDiagnosticEndpointAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient
                .GetPublishedProcesses()
                .Contains(pid))
            {
                return;
            }

            await Task.Delay(500);
        }
    }

    private static string? LocateSampleProject()
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(probe, "samples", "CoreClrSample");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            probe = Path.GetFullPath(Path.Combine(probe, ".."));
        }

        return null;
    }
}

/// <summary>Thrown to skip a test (in lieu of pulling a separate Skippable package).</summary>
public sealed class SkipException : Exception
{
    private SkipException(string reason) : base(reason)
    {
    }

    public static SkipException ForReason(string reason) => new(reason);
}

[CollectionDefinition("LiveProcess", DisableParallelization = true)]
public class LiveProcessCollection;
