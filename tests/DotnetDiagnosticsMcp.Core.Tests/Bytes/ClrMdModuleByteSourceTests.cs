using System.Security.Cryptography;
using DotnetDiagnosticsMcp.Core.Bytes;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests.Bytes;

[Collection("LiveProcess")]
public sealed class ClrMdModuleByteSourceTests : IAsyncLifetime
{
    private LiveSampleProcess? _sample;

    private int ProcessId => _sample?.ProcessId ?? throw new InvalidOperationException("Sample not started.");
    private string SampleDll => _sample?.SampleDll ?? throw new InvalidOperationException("Sample DLL not resolved.");

    public async Task InitializeAsync()
    {
        _sample = await LiveSampleProcess.StartPublishedAsync(
            "CoreClrSample",
            new LiveSampleOptions { DiagnosticTimeout = TimeSpan.FromSeconds(30) });
        await WaitForModuleVisibilityAsync(_sample.ProcessId, _sample.SampleDll, TimeSpan.FromSeconds(30));
    }

    public async Task DisposeAsync()
    {
        if (_sample is not null)
        {
            await _sample.DisposeAsync();
        }
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
}
