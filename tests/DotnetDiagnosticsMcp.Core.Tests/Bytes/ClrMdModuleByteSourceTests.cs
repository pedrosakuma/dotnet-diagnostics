using System.Diagnostics;
using System.Security.Cryptography;
using DotnetDiagnosticsMcp.Core.Bytes;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using FluentAssertions;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnosticsMcp.Core.Tests.Bytes;

[Collection("LiveProcess")]
public sealed class ClrMdModuleByteSourceTests : IAsyncLifetime
{
    private Process? _sampleProcess;
    private string? _sampleDll;

    private int ProcessId => _sampleProcess?.Id ?? throw new InvalidOperationException("Sample not started.");
    private string SampleDll => _sampleDll ?? throw new InvalidOperationException("Sample DLL not resolved.");

    public async Task InitializeAsync()
    {
        _sampleDll = LocateSampleDll() ?? throw new InvalidOperationException("CoreClrSample.dll not found.");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_sampleDll)!,
        };
        psi.ArgumentList.Add(_sampleDll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        _sampleProcess = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start CoreClrSample.");
        _ = DrainAsync(_sampleProcess.StandardOutput);
        _ = DrainAsync(_sampleProcess.StandardError);
        await WaitForDiagnosticEndpointAsync(_sampleProcess.Id, TimeSpan.FromSeconds(30));
        await WaitForModuleVisibilityAsync(_sampleProcess.Id, _sampleDll, TimeSpan.FromSeconds(30));
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
            catch
            {
                // best effort
            }
        }

        _sampleProcess?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FetchAsync_PeStreamMatchesOnDiskAssembly()
    {
        var source = new ClrMdModuleByteSource();
        var mvid = new MvidReader().TryRead(SampleDll);
        mvid.Should().NotBeNull();

        var first = await source.FetchAsync(ProcessId, mvid!.Value, asset: "pe", offset: 0, maxBytes: 4096);
        first.SourcePath.Should().Be(Path.GetFullPath(SampleDll));
        Convert.FromBase64String(first.Base64Chunk).Take(2).Should().Equal((byte)'M', (byte)'Z');

        var assembled = new List<byte>();
        ByteFetchEnvelope current = first;
        while (true)
        {
            assembled.AddRange(Convert.FromBase64String(current.Base64Chunk));
            if (current.NextOffset is not long next)
            {
                break;
            }

            current = await source.FetchAsync(ProcessId, mvid.Value, asset: "pe", offset: next, maxBytes: 4096);
        }

        var expectedBytes = File.ReadAllBytes(SampleDll);
        assembled.Should().Equal(expectedBytes);
        first.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant());
    }

    [Fact]
    public async Task FetchAsync_SymbolAvailabilityIsSurfacedAndReadable()
    {
        var source = new ClrMdModuleByteSource();
        var mvid = new MvidReader().TryRead(SampleDll);
        mvid.Should().NotBeNull();

        var peEnvelope = await source.FetchAsync(ProcessId, mvid!.Value, asset: "pe", offset: 0, maxBytes: 512);
        (peEnvelope.CompanionPdbPath is not null || peEnvelope.PdbIsEmbedded == true).Should().BeTrue(
            "CoreClrSample should publish debug symbols either as a sibling PDB or as an embedded portable PDB");

        var pdbEnvelope = await source.FetchAsync(ProcessId, mvid.Value, asset: "pdb", offset: 0, maxBytes: 512);
        pdbEnvelope.ChunkSize.Should().BeGreaterThan(0);
        if (peEnvelope.CompanionPdbPath is not null)
        {
            pdbEnvelope.SourcePath.Should().Be(peEnvelope.CompanionPdbPath);
            pdbEnvelope.PdbIsEmbedded.Should().BeFalse();
        }
        else
        {
            pdbEnvelope.SourcePath.Should().Be(Path.GetFullPath(SampleDll));
            pdbEnvelope.PdbIsEmbedded.Should().BeTrue();
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
        catch
        {
            // best effort
        }
    }

    private static async Task WaitForDiagnosticEndpointAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (DiagnosticsClient.GetPublishedProcesses().Contains(processId))
            {
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException($"CoreClrSample pid {processId} did not expose a diagnostic endpoint within {timeout}.");
    }

    private static async Task WaitForModuleVisibilityAsync(int processId, string sampleDll, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var mvid = new MvidReader().TryRead(sampleDll) ?? throw new InvalidOperationException("Sample DLL MVID not readable.");
        var source = new ClrMdModuleByteSource();
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await source.FetchAsync(processId, mvid, asset: "pe", offset: 0, maxBytes: 2);
                return;
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"CoreClrSample mvid {mvid:D} was not visible in pid {processId} within {timeout}.");
    }

    private static string? LocateSampleDll()
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var sampleDir = Path.Combine(probe, "samples", "CoreClrSample");
            if (Directory.Exists(sampleDir))
            {
                foreach (var configuration in new[] { "Release", "Debug" })
                {
                    var dll = Path.Combine(sampleDir, "bin", configuration, "net10.0", "CoreClrSample.dll");
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
