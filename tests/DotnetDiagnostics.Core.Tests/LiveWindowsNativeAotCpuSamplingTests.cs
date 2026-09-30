using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing.Session;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class LiveWindowsNativeAotCpuSamplingTests : IAsyncLifetime
{
    private Process? sampleProcess;
    private string? publishDirectory;
    private Uri? baseAddress;

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();
        if (!sampler.IsAvailable())
        {
            if (Environment.GetEnvironmentVariable("DIAG_REQUIRE_WINDOWS_DIA_ETW") == "1")
            {
                throw new InvalidOperationException(
                    "The required Windows DIA/ETW integration environment lacks elevated kernel profiling.");
            }

            return;
        }

        publishDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "test-artifacts",
            nameof(LiveWindowsNativeAotCpuSamplingTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(publishDirectory);

        var sampleProject = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "samples", "NativeAotSample", "NativeAotSample.csproj"));
        await PublishAsync(sampleProject, publishDirectory, CancellationToken.None);

        var executablePath = Path.Combine(publishDirectory, "NativeAotSample.exe");
        var pdbPath = Path.Combine(publishDirectory, "NativeAotSample.pdb");
        File.Exists(executablePath).Should().BeTrue("the NativeAOT executable must be published");
        File.Exists(pdbPath).Should().BeTrue("the NativeAOT PDB is required to verify DIA symbol resolution");

        var port = ReserveLoopbackPort();
        baseAddress = new Uri($"http://127.0.0.1:{port}/");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = publishDirectory,
        };
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(baseAddress.ToString().TrimEnd('/'));
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        sampleProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the NativeAOT CPU sampling target.");
        _ = DrainAsync(sampleProcess.StandardOutput);
        _ = DrainAsync(sampleProcess.StandardError);

        await WaitForTargetAsync(sampleProcess.Id, CancellationToken.None);
        using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(5) };
        using var response = await SendWorkloadAsync(client, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    public async Task DisposeAsync()
    {
        if (sampleProcess is { HasExited: false })
        {
            sampleProcess.Kill(entireProcessTree: true);
            await sampleProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        sampleProcess?.Dispose();
        if (publishDirectory is not null && Directory.Exists(publishDirectory))
        {
            Directory.Delete(publishDirectory, recursive: true);
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task SampleAsync_ResolvesNativeAotFunctionFromMatchingPdbRange()
    {
        if (sampleProcess is null || baseAddress is null)
        {
            return;
        }

        using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };
        using var cancellation = new CancellationTokenSource();
        var workload = RunWorkloadAsync(client, cancellation.Token);

        try
        {
            var result = await new EtwNativeAotCpuSampler().SampleAsync(
                sampleProcess!.Id,
                TimeSpan.FromSeconds(5),
                topN: 50);

            result.Summary.TotalSamples.Should().BeGreaterThan(0);
            result.Summary.SymbolSource.Should().Be(
                NativeAotSymbolDemangler.SymbolSource.PdbResolved,
                string.Join(
                    Environment.NewLine,
                    result.Summary.Notes.Concat(
                        result.Summary.TopHotspots.Select(hotspot =>
                            $"{hotspot.Frame.Module}!{hotspot.Frame.Method}: {hotspot.InclusiveSamples}"))));
            result.Summary.TopHotspots.Should().Contain(
                hotspot => hotspot.Frame.Method.Contains("BurnCpu", StringComparison.Ordinal),
                "the known no-inline NativeAOT workload must resolve through its matching PDB function range");
        }
        finally
        {
            cancellation.Cancel();
            await workload;
        }
    }

    private static async Task PublishAsync(string projectPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "publish", projectPath, "-c", "Release", "-r", "win-x64",
            "-p:PublishAot=true", "-o", outputDirectory, "--self-contained", "true",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start NativeAOT publish.");
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"NativeAOT sample publish failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }

    private static async Task WaitForTargetAsync(int processId, CancellationToken cancellationToken)
    {
        var discovery = new LocalProcessDiscovery();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (discovery.TryGetProcess(processId) is not null)
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("NativeAOT sample did not expose a diagnostic endpoint in time.");
    }

    private static async Task RunWorkloadAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var response = await SendWorkloadAsync(client, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<HttpResponseMessage> SendWorkloadAsync(HttpClient client, CancellationToken cancellationToken)
        => await client.GetAsync("cpu", cancellationToken);

    private static async Task<string> DrainAsync(StreamReader reader)
        => await reader.ReadToEndAsync();

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
