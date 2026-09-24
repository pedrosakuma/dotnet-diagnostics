using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed partial class MonitoredRunnerTests
{
    [Theory]
    [InlineData(true, 0, "arbitrary")]
    [InlineData(true, 32_768, "arbitrary")]
    [InlineData(false, 0, "not-a-temporary-prefix")]
    [InlineData(false, 32_768, "not-a-temporary-prefix")]
    [InlineData(false, 0, "etilqs-example")]
    public async Task ObservedUnlinkedPositiveIdentityIsChargedOnce(bool owned, int length, string name)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(owned ? fixture.Manifest.WorkspaceRoot : _workspace, name);
        File.WriteAllBytes(path, new byte[length]);
        using var child = StartObservedOwner(path, writable: true, duplicate: true);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "observed.jsonl"),
            includeIdentityEvidence: true, observedUnlinked: true);
        monitor.AddProcess(owner);
        try
        {
            File.Delete(path);
            var result = monitor.Sweep();
            result.Errors.Should().BeEmpty();
            result.Summary.Complete.Should().BeTrue();
            result.Summary.NativeTemporaryBytes.Should().Be(owned ? 0 : length);
            result.Summary.OpenUnlinkedBytes.Should().Be(length);
            result.Summary.SampledLoss!.ObservedUnlinked.Should().Be(new ObservedUnlinkedMeasurement(1,
                owned ? 0 : 1, owned ? 0 : length));
            result.Summary.SampledLoss.Lost.Should().Be(0);
            result.Summary.SampledLoss.Candidates.Should().Be(result.Summary.SampledLoss.Classified);
            var identity = result.IdentityEvidence.Should().ContainSingle(x => x.IsUnlinked).Subject;
            identity.Length.Should().Be(length);
            identity.Role.Should().Be(owned ? "workspace" : "active-native-temporary");
            result.Summary.WorkspaceBytes.Should().BeGreaterThanOrEqualTo(length);
            result.Summary.ObservedSweepBytes.Should().Be(result.Summary.PackageBytes + result.Summary.RecoveryBytes
                + result.Summary.HistoryBytes + result.Summary.WorkspaceBytes + result.Summary.EvidenceBytes);
            var bytes = MonitoredSweepSummaryEncoding.EncodeLine(result.Summary);
            JsonSerializer.Deserialize<MonitoredSweepSummary>(bytes, PrevalidationProtocol.Json)
                .Should().BeEquivalentTo(result.Summary);
            _output.WriteLine(Encoding.UTF8.GetString(bytes));
        }
        finally { await monitor.KillOwnedAsync(owner, CancellationToken.None); }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public async Task ObservedUnlinkedUnknownRequiresWritableDiagnostic(int role, bool writable)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(_workspace, "unowned");
        File.WriteAllBytes(path, []);
        using var child = StartObservedOwner(path, writable);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, (MonitoredProcessRole)role);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "rejected.jsonl"), observedUnlinked: true);
        monitor.AddProcess(owner);
        try
        {
            File.Delete(path);
            var result = monitor.Sweep();
            result.Errors.Should().Contain("UnclassifiedUnlinkedDescriptor");
            result.Summary.Complete.Should().BeFalse();
            result.Summary.SampledLoss!.ObservedUnlinked!.Identities.Should().Be(0);
            result.Summary.SampledLoss.Lost.Should().Be(0);
            DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
        }
        finally { await monitor.KillOwnedAsync(owner, CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(true, false)]
    public async Task ObservedUnlinkedGrowthUsesBothLengthsWithoutStableSizeRestriction(bool deleted, bool owned = true)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(owned ? fixture.Manifest.WorkspaceRoot : _workspace, "growing");
        using var file = File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        file.SetLength(32_768);
        using var child = StartObservedOwner(path, true, duplicate: true);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "growth.jsonl"), includeIdentityEvidence: true, observedUnlinked: true);
        monitor.AddProcess(owner);
        if (deleted) File.Delete(path);
        monitor.BeforeDescriptorOperationForComponent = (descriptor, operation) =>
        {
            if (Path.GetFileName(descriptor) == "9" && operation == 4) file.SetLength(65_536);
        };
        try
        {
            var result = monitor.Sweep();
            result.Errors.Should().BeEmpty();
            result.IdentityEvidence.Should().ContainSingle(x =>
                x.Role == (owned ? "workspace" : "active-native-temporary") && x.Length == 65_536);
            result.Summary.SampledLoss!.ObservedUnlinked!.Identities.Should().Be(deleted ? 1 : 0);
            result.Summary.NativeTemporaryBytes.Should().Be(owned ? 0 : 65_536);
            result.Summary.DescriptorOnlyIdentityCount.Should().Be(deleted ? 1 : 0);
        }
        finally { await monitor.KillOwnedAsync(owner, CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedUnlinkedTransitionNeverBecomesLoss(bool afterSecondReadlink)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(fixture.Manifest.WorkspaceRoot, "transition");
        using var child = StartObservedOwner(path, true);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "transition.jsonl"), observedUnlinked: true);
        monitor.AddProcess(owner);
        monitor.BeforeDescriptorOperationForComponent = (descriptor, operation) =>
        {
            if (Path.GetFileName(descriptor) == "9" && operation == (afterSecondReadlink ? 4 : 3)) File.Delete(path);
        };
        try
        {
            var result = monitor.Sweep();
            result.Errors.Should().Contain("DescriptorIdentityChangedDuringObservation");
            result.Summary.SampledLoss!.Lost.Should().Be(0);
            result.Summary.SampledLoss.ObservedUnlinked!.Identities.Should().Be(0);
        }
        finally { await monitor.KillOwnedAsync(owner, CancellationToken.None); }
    }

    [Theory]
    [InlineData(268_435_457L, "ObservedPackageRecoveryThresholdExceeded")]
    [InlineData(3_221_225_473L, "ObservedSuiteWorkspaceThresholdExceeded")]
    public async Task ObservedUnlinkedSparseTemporaryCrossesSharedAndSuiteBudgets(long length, string alarm)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(_workspace, "sparse-native-file");
        using (var file = File.Create(path)) file.SetLength(length);
        using var child = StartObservedOwner(path, true, duplicate: true);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "budget.jsonl"), observedUnlinked: true);
        monitor.AddProcess(owner);
        try
        {
            File.Delete(path);
            var result = monitor.Sweep();
            result.Errors.Should().BeEmpty();
            result.Summary.NativeTemporaryBytes.Should().Be(length);
            result.Summary.Alarm.Should().Be(alarm);
            DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
        }
        finally { await monitor.KillOwnedAsync(owner, CancellationToken.None); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("identity")]
    [InlineData("negative")]
    [InlineData("flags")]
    [InlineData("link-transition")]
    [InlineData("dead")]
    [InlineData("relative")]
    [InlineData("proc")]
    [InlineData("memfd")]
    [InlineData("anon")]
    [InlineData("hardlinks")]
    public void ObservedUnlinkedProofRejectsIncompleteNativeEvidence(string mutation)
    {
        if (!OperatingSystem.IsLinux()) return;
        var owner = new MonitoredProcessIdentity(Environment.ProcessId,
            LinuxProcessIdentity.ReadStartTime(Environment.ProcessId), MonitoredProcessRole.Diagnostic);
        var identity = new ApparentFileIdentity("native");
        var first = new PinnedDescriptorSnapshot("/arbitrary/native (deleted)", 2, identity, new(identity, 0, 0));
        var second = first;
        switch (mutation)
        {
            case "missing": second = second with { RegularFileMetadata = null }; break;
            case "identity": second = second with { RegularFileMetadata = new(new("other"), 0, 0) }; break;
            case "negative": second = second with { RegularFileMetadata = new(identity, -1, 0) }; break;
            case "flags": first = second = first with { Flags = -1 }; break;
            case "link-transition": second = second with { RegularFileMetadata = new(identity, 0, 1) }; break;
            case "dead": owner = owner with { LinuxStartTimeTicks = owner.LinuxStartTimeTicks + 1 }; break;
            case "relative": first = second = first with { Target = "relative" }; break;
            case "proc": first = second = first with { Target = "/proc/1/fd/9 (deleted)" }; break;
            case "memfd": first = second = first with { Target = "/memfd:arbitrary (deleted)" }; break;
            case "anon": first = second = first with { Target = "anon_inode:[eventfd]" }; break;
            case "hardlinks": first = second = first with { RegularFileMetadata = new(identity, 0, 2) }; break;
        }
        Action proof = () => ObservedUnlinkedDescriptorProof.ValidateUnlinked(owner, first, second);
        proof.Should().Throw<DurableStorageExperimentException>();
    }

    [Fact]
    public void ObservedUnlinkedWireBoundsMergeAndZeroEvidenceAreExplicit()
    {
        var summary = ObservedUnlinkedProtocol.WorstCaseSummary();
        var line = MonitoredSweepSummaryEncoding.EncodeLine(summary);
        line.Length.Should().BeLessThanOrEqualTo(1_024);
        line[^1].Should().Be((byte)'\n');
        JsonSerializer.Deserialize<MonitoredSweepSummary>(line, PrevalidationProtocol.Json)
            .Should().BeEquivalentTo(summary);
        _output.WriteLine($"observedWorstLfBytes={line.Length}");
        var totals = summary.SampledLoss!;
        for (var count = 1; count < 2_048; count++)
            totals = SampledLossMeasurement.Merge(totals, summary.SampledLoss!);
        totals.ObservedUnlinked.Should().Be(new ObservedUnlinkedMeasurement(8_388_608, 2_048_000, long.MaxValue));
        var reply = new PrevalidationMonitorReply(false, new string('a', 64), int.MaxValue, long.MaxValue,
            int.MaxValue, long.MaxValue, summary) { SampledLoss = totals };
        var framed = PrevalidationMonitorControl.Encode(reply);
        (Encoding.UTF8.GetByteCount(framed) + 1).Should().BeLessThanOrEqualTo(2_048);
        _output.WriteLine($"observedWorstAuthorityLfBytes={Encoding.UTF8.GetByteCount(framed) + 1}");
        JsonSerializer.Deserialize<PrevalidationMonitorReply>(framed, PrevalidationProtocol.Json)
            .Should().BeEquivalentTo(reply);
        Action overflow = () => SampledLossMeasurement.Merge(totals, summary.SampledLoss!);
        overflow.Should().Throw<DurableStorageExperimentException>();
        var zero = SampledLossMeasurement.Empty(true);
        JsonSerializer.SerializeToElement(zero, PrevalidationProtocol.Json)
            .GetProperty("observedUnlinked").GetProperty("identities").GetInt32().Should().Be(0);
        SampledLossMeasurement.Merge(zero, summary.SampledLoss!).Should().BeEquivalentTo(summary.SampledLoss);
    }

    [Theory]
    [InlineData(268_435_455L, true, true)]
    [InlineData(268_435_456L, true, false)]
    [InlineData(268_435_456L, false, true)]
    public void ObservedUnlinkedWireAdmissionMatchesThePolicyBudget(long packageBytes,
        bool observedPolicy, bool admissible)
    {
        var summary = ObservedUnlinkedProtocol.WorstCaseSummary() with
        {
            PackageBytes = packageBytes, RecoveryBytes = 0, ErrorCount = 0, UnclassifiedCount = 0,
            Alarm = null, FirstErrorCode = null, FirstDescriptorFailure = null, Complete = true,
            SampledLoss = new([0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0], null)
            {
                ObservedUnlinked = observedPolicy ? new(1, 1, 1) : null,
            },
        };
        var line = MonitoredSweepSummaryEncoding.EncodeLine(summary);
        var wire = JsonNode.Parse(line)!;
        wire["f"]![3]!.GetValue<bool>().Should().Be(admissible);
        JsonSerializer.Deserialize<MonitoredSweepSummary>(line, PrevalidationProtocol.Json)
            .Should().BeEquivalentTo(summary);
        wire["f"]![3] = !admissible;
        Action read = () => JsonSerializer.Deserialize<MonitoredSweepSummary>(
            wire.ToJsonString(), PrevalidationProtocol.Json);
        read.Should().Throw<DurableStorageExperimentException>().Which.Code.Should()
            .Be("SampledLossWireAdmissionMismatch");
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("short")]
    [InlineData("negative")]
    [InlineData("overflow")]
    [InlineData("type")]
    [InlineData("legacy")]
    public void ObservedUnlinkedMalformedWireFailsStructurally(string mutation)
    {
        var wire = JsonNode.Parse(MonitoredSweepSummaryEncoding.EncodeLine(ObservedUnlinkedProtocol.WorstCaseSummary()))!;
        switch (mutation)
        {
            case "absent": wire.AsObject().Remove("a"); break;
            case "null": wire["a"] = null; break;
            case "short": wire["a"] = new JsonArray(0, 0); break;
            case "negative": wire["a"]![0] = -1; break;
            case "overflow": wire["a"]![0] = 4_097; break;
            case "type": wire["a"]![0] = "unknown"; break;
            case "legacy": wire["v"] = 1; break;
        }
        Action read = () => JsonSerializer.Deserialize<MonitoredSweepSummary>(wire.ToJsonString(), PrevalidationProtocol.Json);
        read.Should().Throw<DurableStorageExperimentException>();
    }

    [Fact]
    public async Task ObservedUnlinkedRuntimeProofFailureCannotFallThroughToTemporary()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(_workspace, "runtime-proof-target");
        File.WriteAllBytes(path, []);
        using var child = StartObservedOwner(path, true);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        var proof = fixture.Attribution.RuntimeOnlyDescriptorProofs[0] with
        {
            DescriptorTarget = path + " (deleted)", BackingFileSystemMagic = long.MaxValue,
        };
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution with
            { RuntimeOnlyDescriptorProofs = [proof] }, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "runtime-proof.jsonl"), observedUnlinked: true);
        monitor.AddProcess(owner);
        try
        {
            File.Delete(path);
            var result = monitor.Sweep();
            result.Errors.Should().Contain("RuntimeProofFileSystemMismatch");
            result.Summary.NativeTemporaryBytes.Should().Be(0);
            result.Summary.SampledLoss!.ObservedUnlinked!.Identities.Should().Be(0);
            result.Summary.SampledLoss.Lost.Should().Be(0);
            DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
        }
        finally { await monitor.KillOwnedAsync(owner, CancellationToken.None); }
    }

    [Theory]
    [InlineData("package-staging", 32_768L)]
    [InlineData("recovery", 32_768L)]
    [InlineData("history-owned", 32_768L)]
    public async Task ObservedUnlinkedOwnedRolesAndSharedPartitionRemainExact(string directory, long length)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var root = directory == "history-owned" ? fixture.Manifest.HistoryRoot : fixture.Manifest.WorkspaceRoot;
        var owned = Path.Combine(root, directory);
        Directory.CreateDirectory(owned);
        var path = Path.Combine(owned, "owned");
        using (var file = File.Create(path)) file.SetLength(length);
        var temporary = Path.Combine(_workspace, "native-extra");
        File.WriteAllBytes(temporary, []);
        using var child = StartObservedOwner(path, true, duplicate: true);
        using var extra = StartObservedOwner(temporary, true);
        child.StandardOutput.ReadLine().Should().Be("ready");
        extra.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        var extraOwner = MonitoredProcessIdentity.Capture(extra, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "partitions.jsonl"), includeIdentityEvidence: true,
            observedUnlinked: true);
        monitor.AddProcess(owner);
        monitor.AddProcess(extraOwner);
        try
        {
            // Root observation precedes this unlink; both descriptor snapshots follow it.
            monitor.BeforeDescriptorOperationForComponent = (descriptor, phase) =>
            {
                if (phase == 1) { File.Delete(path); File.Delete(temporary); }
            };
            var result = monitor.Sweep();
            result.Errors.Should().BeEmpty();
            var role = directory == "package-staging" ? "package" : directory == "recovery" ? "recovery" : "history";
            result.IdentityEvidence.Should().ContainSingle(x => x.Role == role && x.Length == length && x.SeenInRoot);
            result.IdentityEvidence.Should().ContainSingle(x => x.Role == "active-native-temporary" && x.Length == 0);
            result.Summary.SampledLoss!.ObservedUnlinked.Should().Be(new ObservedUnlinkedMeasurement(2, 1, 0));
            result.Summary.ObservedSweepBytes.Should().Be(length);
            (result.Summary.PackageBytes + result.Summary.RecoveryBytes + result.Summary.HistoryBytes).Should().Be(length);
            result.Summary.DescriptorOnlyIdentityCount.Should().Be(1);
        }
        finally
        {
            await monitor.KillOwnedAsync(owner, CancellationToken.None);
            await monitor.KillOwnedAsync(extraOwner, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ObservedUnlinkedRecoveryMergesExactlyOnceAndRetainsPolicy()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        fixture = fixture with { Manifest = fixture.Manifest with
        {
            Schema = ObservedUnlinkedProtocol.CampaignManifestSchema,
            SampledLoss = ObservedAdmissionBinding() with { ManagedBinaries = [fixture.Manifest.ToolBinary],
                RuntimeEnvironmentSha256 = PrevalidationProtocol.RuntimeEnvironmentHash() },
        }};
        var launcher = new ScriptedWorkerLauncher(ScriptedWorkerBehavior.SuccessfulRecovery);
        var execution = MonitoredExecutionPlanner.Expand().Single(item => item.CaseId == "F3" && item.Candidate == "A");
        var outcome = await MonitoredCampaignRunner.RunExecutionForComponentAsync(fixture, execution, launcher,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        outcome.FailureCode.Should().BeNull();
        outcome.SampledAdmissible.Should().BeTrue();
        outcome.SampledLoss!.ObservedUnlinked.Should().NotBeNull();
        outcome.SampledLoss.Candidates.Should().Be(outcome.SampledLoss.Classified + outcome.SampledLoss.Lost);
        launcher.Descriptors.Should().HaveCount(2);
    }

    [Fact]
    public void ObservedUnlinkedAllGroupWidthsFitEvenIndependentCounterUpperBounds()
    {
        var widest = JsonNode.Parse(MonitoredSweepSummaryEncoding.EncodeLine(ObservedUnlinkedProtocol.WorstCaseSummary()))!;
        for (var index = 0; index < 15; index++) widest["l"]![index] = SampledLossMeasurement.MaximumCandidatesPerSweep;
        // Independent scalar bounds deliberately overestimate feasible joint populations.
        var upperBytes = Encoding.UTF8.GetByteCount(widest.ToJsonString()) + 1;
        upperBytes.Should().Be(997).And.BeLessThanOrEqualTo(1_024);
        _output.WriteLine($"allGroupsIndependentUpperBoundLfBytes={upperBytes}");
        var totals = new SampledLossMeasurement(Enumerable.Repeat(
            SampledLossMeasurement.MaximumCandidatesPerEntry, 15).ToArray(), [2, 4, -2147024894, int.MaxValue])
        {
            ObservedUnlinked = new(8_388_608, 8_388_608, long.MaxValue),
        };
        var reply = new PrevalidationMonitorReply(false, new string('a', 64), int.MaxValue, long.MaxValue,
            int.MaxValue, long.MaxValue, ObservedUnlinkedProtocol.WorstCaseSummary()) { SampledLoss = totals };
        var frame = JsonNode.Parse(PrevalidationMonitorControl.Encode(reply))!;
        frame["summary"] = widest;
        var frameUpper = Encoding.UTF8.GetByteCount(frame.ToJsonString()) + 1;
        frameUpper.Should().BeLessThanOrEqualTo(2_048);
        _output.WriteLine($"allGroupsIndependentAuthorityUpperBoundLfBytes={frameUpper}");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 32_768)]
    [InlineData(true, 32_768)]
    public async Task ObservedUnlinkedLegacyPrevalidationStillRejectsKnownZeroAndNonzeroFiles(bool sampled, int length)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(fixture.Manifest.WorkspaceRoot, "strict-owned");
        File.WriteAllBytes(path, new byte[length]);
        using var child = StartObservedOwner(path, true);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        using var self = Process.GetCurrentProcess();
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "legacy.jsonl"),
            prevalidationScope: new([], MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness)),
            sampledLoss: sampled);
        monitor.AddProcess(owner);
        try
        {
            File.Delete(path);
            var result = monitor.Sweep();
            result.Errors.Should().Contain("UnclassifiedUnlinkedDescriptor");
            (result.Summary.SampledLoss?.ObservedUnlinked).Should().BeNull();
            DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
        }
        finally { await monitor.KillOwnedAsync(owner, CancellationToken.None); }
    }

    [Fact]
    public async Task ObservedUnlinkedOwnerMustRemainAliveAfterBothSnapshots()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_workspace, "dead-owner");
        using var child = StartObservedOwner(path, true);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        try
        {
            File.Delete(path);
            using var pinned = LinuxProcessDescriptorObserver.OpenCoherent(owner.ProcessId,
                $"/proc/{owner.ProcessId}/fd/9", 4_096, owner);
            OwnedProcessTerminator.KillExact(child, owner);
            await child.WaitForExitAsync();
            Action proof = () => ObservedUnlinkedDescriptorProof.ValidateUnlinked(owner, pinned.Snapshot, pinned.SecondSnapshot);
            proof.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("UnexpectedProcessIdentityLoss");
        }
        finally
        {
            if (!child.HasExited)
            {
                OwnedProcessTerminator.KillExact(child, owner);
                await child.WaitForExitAsync();
            }
        }
    }

    private static Process StartObservedOwner(string path, bool writable, bool duplicate = false)
    {
        var info = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add((writable ? "exec 9<>\"$1\"; " : "exec 9<\"$1\"; ")
            + (duplicate ? "exec 8<&9; " : "") + "printf 'ready\\n'; read -r command");
        info.ArgumentList.Add("controlled-observed-owner");
        info.ArgumentList.Add(path);
        return Process.Start(info)!;
    }
}
