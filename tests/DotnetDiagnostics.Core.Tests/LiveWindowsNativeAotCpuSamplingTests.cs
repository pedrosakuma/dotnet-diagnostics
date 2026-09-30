using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class LiveWindowsNativeAotCpuSamplingTests(ITestOutputHelper output) : IAsyncLifetime
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

        try
        {
            await StartTargetAsync(publishDirectory);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task StartTargetAsync(string directory)
    {
        var sampleProject = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "samples", "NativeAotSample", "NativeAotSample.csproj"));
        using var publishDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await PublishAsync(sampleProject, directory, publishDeadline.Token);

        var executablePath = Path.Combine(directory, "NativeAotSample.exe");
        var pdbPath = Path.Combine(directory, "NativeAotSample.pdb");
        File.Exists(executablePath).Should().BeTrue("the NativeAOT executable must be published");
        File.Exists(pdbPath).Should().BeTrue("the NativeAOT PDB is required to verify DIA symbol resolution");
        VerifyPdbIdentityControls(executablePath, pdbPath);

        var port = ReserveLoopbackPort();
        baseAddress = new Uri($"http://127.0.0.1:{port}/");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory,
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
        sampleProcess = null;
        if (publishDirectory is not null && Directory.Exists(publishDirectory))
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(publishDirectory, recursive: true);
                    break;
                }
                catch (Exception ex) when (
                    (ex is IOException or UnauthorizedAccessException) && attempt < 20)
                {
                    await Task.Delay(250);
                }
            }
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

            output.WriteLine($"Samples={result.Summary.TotalSamples}; symbolSource={result.Summary.SymbolSource}");
            foreach (var note in result.Summary.Notes)
            {
                output.WriteLine(note);
            }
            foreach (var hotspot in result.Summary.TopHotspots.Where(hotspot =>
                hotspot.Frame.Method.Contains("BurnCpu", StringComparison.Ordinal)))
            {
                output.WriteLine($"{hotspot.Frame.Module}!{hotspot.Frame.Method}: inclusive={hotspot.InclusiveSamples}, exclusive={hotspot.ExclusiveSamples}");
            }

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

    private void VerifyPdbIdentityControls(string executablePath, string pdbPath)
    {
        using var stream = File.OpenRead(executablePath);
        using var image = new PEReader(stream);
        var entry = image.ReadDebugDirectory().Single(entry => entry.Type == DebugDirectoryEntryType.CodeView);
        var identity = image.ReadCodeViewDebugDirectoryData(entry);

        EtwPdbSymbolResolver.TryOpenPdb(pdbPath, identity.Guid, identity.Age, out var matching, out var status)
            .Should().BeTrue($"the shipped DIA library must open the exact fixture PDB (status={status})");
        using (matching)
        {
            matching.Should().NotBeNull();
        }

        foreach (var (signature, age) in new[]
        {
            (Guid.NewGuid(), identity.Age),
            (identity.Guid, identity.Age + 1),
        })
        {
            var opened = EtwPdbSymbolResolver.TryOpenPdb(pdbPath, signature, age, out var rejected, out status);
            using (rejected)
            {
                opened.Should().BeFalse("mismatched GUID or age must never be accepted");
                status.Should().Be(NativeSymbolResolverOpenStatus.PdbRejected);
                rejected.Should().BeNull();
            }
        }

        output.WriteLine($"Offline DIA controls: matching GUID={identity.Guid}, age={identity.Age} accepted; wrong GUID and wrong age rejected.");
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
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            throw;
        }
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
