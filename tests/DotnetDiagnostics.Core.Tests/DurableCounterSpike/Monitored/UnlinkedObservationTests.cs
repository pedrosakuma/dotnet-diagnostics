using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed partial class MonitoredRunnerTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 32_768)]
    [InlineData(false, 32_768)]
    public async Task UnlinkedObservationOwnedAndUnknownFilesRemainHardFailures(bool owned, int length)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(owned ? fixture.Manifest.WorkspaceRoot : _workspace, "unlink-lifecycle");
        File.WriteAllBytes(path, new byte[length]);
        using var child = StartUnlinkedObservationOwner(path);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "unlinked-monitor.jsonl"),
            includeIdentityEvidence: true, sampledLoss: true);
        monitor.AddProcess(owner);
        try
        {
            File.Delete(path);
            using (var pinned = LinuxProcessDescriptorObserver.OpenCoherent(
                owner.ProcessId, $"/proc/{owner.ProcessId}/fd/9", 4_096, owner))
            {
                pinned.Snapshot.Target.Should().Be(path + " (deleted)");
                pinned.Snapshot.RegularFileMetadata!.Value.LinkCount.Should().Be(0);
                pinned.Snapshot.RegularFileMetadata.Value.Length.Should().Be(length);
                _output.WriteLine($"controlledSnapshot={JsonSerializer.Serialize(pinned.Snapshot)}");
            }
            var result = await monitor.ObserveBoundaryAsync("controlled-unlink", false, CancellationToken.None);
            result.Errors.Should().Equal("UnclassifiedUnlinkedDescriptor");
            result.Summary.OpenUnlinkedBytes.Should().Be(length);
            result.Summary.UnclassifiedCount.Should().Be(owned ? 0 : 1);
            result.Summary.DescriptorOnlyIdentityCount.Should().Be(1);
            result.Summary.FirstDescriptorFailure.Should().BeNull();
            result.Summary.Complete.Should().BeFalse();
            result.Summary.SampledLoss!.Lost.Should().Be(0);
            result.Summary.SampledLoss.Candidates.Should().Be(result.Summary.SampledLoss.Classified + 1);
            SampledLossProtocol.Admissible(result.Summary).Should().BeFalse();
            _output.WriteLine($"controlledSweep={JsonSerializer.Serialize(result, PrevalidationProtocol.Json)}");
        }
        finally
        {
            await monitor.KillOwnedAsync(owner, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnlinkedObservationSecondSnapshotCannotDiscardKnownUnlink(bool sampled)
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_workspace, "second-snapshot-unlink");
        using var file = File.OpenWrite(path);
        var fd = checked((int)file.SafeFileHandle.DangerousGetHandle());
        var owner = new MonitoredProcessIdentity(Environment.ProcessId,
            LinuxProcessIdentity.ReadStartTime(Environment.ProcessId), MonitoredProcessRole.Harness);
        Action observe = () => LinuxProcessDescriptorObserver.OpenCoherentForComponent(
            owner.ProcessId, $"/proc/{owner.ProcessId}/fd/{fd}", 4_096,
            phase => { if (phase == 4) File.Delete(path); },
            sampledOwner: sampled ? owner : null).Dispose();
        var error = observe.Should().Throw<DurableStorageExperimentException>().Which;
        error.Code.Should().Be("DescriptorIdentityChangedDuringObservation");
        error.KnownUnlinkedDescriptor.Should().BeTrue();
        error.DescriptorFailure.Should().BeNull();
        SampledLossProtocol.Classify(error, owner).Should().Be(0);
        if (sampled)
        {
            var proof = error.CoherenceProof!;
            proof.Changes.Should().Be(0);
            proof.First.Target.Should().Be(path);
            proof.Second.Target.Should().Be(path);
            proof.First.RegularFileMetadata!.Value.LinkCount.Should().Be(1);
            proof.Second.RegularFileMetadata!.Value.LinkCount.Should().Be(0);
            proof.Valid.Should().BeFalse();
            _output.WriteLine($"controlledProof={JsonSerializer.Serialize(proof)}");
        }
    }

    [Fact]
    public async Task UnlinkedObservationMetadataOnlyChangeStaysHardInFrozenWire()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(fixture.Manifest.WorkspaceRoot, "late-unlink");
        using var child = StartUnlinkedObservationOwner(path);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "late-unlink-monitor.jsonl"), sampledLoss: true);
        monitor.AddProcess(owner);
        var unlinked = false;
        monitor.BeforeDescriptorOperationForComponent = (descriptor, operation) =>
        {
            if (operation != 4 || Path.GetFileName(descriptor) != "9") return;
            File.Delete(path);
            unlinked = true;
        };
        try
        {
            var result = await monitor.ObserveBoundaryAsync("controlled-late-unlink", false, CancellationToken.None);
            unlinked.Should().BeTrue();
            result.Errors.Should().Equal("DescriptorIdentityChangedDuringObservation");
            result.Summary.SampledLoss!.Lost.Should().Be(0);
            result.Summary.SampledLoss.Candidates.Should().Be(result.Summary.SampledLoss.Classified + 1);
            result.Summary.FirstDescriptorFailure.Should().BeNull();
            SampledLossProtocol.Admissible(result.Summary).Should().BeFalse();
            var line = MonitoredSweepSummaryEncoding.EncodeLine(result.Summary);
            JsonSerializer.Deserialize<MonitoredSweepSummary>(line, PrevalidationProtocol.Json)
                .Should().BeEquivalentTo(result.Summary);
            _output.WriteLine($"controlledLateUnlink={JsonSerializer.Serialize(result, PrevalidationProtocol.Json)}");
        }
        finally
        {
            await monitor.KillOwnedAsync(owner, CancellationToken.None);
        }
    }

    [Fact]
    public async Task UnlinkedObservationPinRetainsInodeNotOriginalDescriptorSlot()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_workspace, "pin-lifetime");
        using var child = StartUnlinkedObservationOwner(path);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        try
        {
            using var pinned = LinuxProcessDescriptorObserver.OpenCoherent(
                owner.ProcessId, $"/proc/{owner.ProcessId}/fd/9", 4_096, owner);
            pinned.Snapshot.RegularFileMetadata!.Value.LinkCount.Should().Be(1);
            File.Exists(path).Should().BeTrue();
            File.Delete(path);
            child.StandardInput.WriteLine("close");
            child.StandardInput.Flush();
            child.StandardOutput.ReadLine().Should().Be("closed");
            var retained = LinuxStatxHandleMetadataObserver.Instance.Observe(pinned.Handle);
            retained.Identity.Should().Be(pinned.Snapshot.Identity);
            retained.LinkCount.Should().Be(0);
            Action observe = () => LinuxProcessDescriptorObserver.OpenCoherent(
                owner.ProcessId, $"/proc/{owner.ProcessId}/fd/9", 4_096, owner).Dispose();
            var error = observe.Should().Throw<DurableStorageExperimentException>().Which;
            error.DescriptorFailure.Should().Be(new DescriptorObservationFailure(1, 2, 9));
            _output.WriteLine($"controlledRetainedInode={JsonSerializer.Serialize(retained)}");
        }
        finally
        {
            OwnedProcessTerminator.KillExact(child, owner);
            await child.WaitForExitAsync();
        }
    }

    private static Process StartUnlinkedObservationOwner(string path)
    {
        var info = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("exec 9<>\"$1\"; printf 'ready\\n'; read -r command; exec 9>&-; printf 'closed\\n'; read -r command");
        info.ArgumentList.Add("controlled-unlinked-owner");
        info.ArgumentList.Add(path);
        return Process.Start(info)!;
    }
}
