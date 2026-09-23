using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed partial class MonitoredRunnerTests
{
    [Theory]
    [InlineData(1, 2, "DescriptorObservationUnavailable", 2)]
    [InlineData(3, 2, "DescriptorObservationUnavailable", 2)]
    [InlineData(2, -2147024894, "DescriptorFlagsFileNotFoundException", 3)]
    [InlineData(4, -2147024893, "DescriptorFlagsDirectoryNotFoundException", 3)]
    [InlineData(1, 13, "DescriptorObservationUnavailable", 0)]
    [InlineData(3, 5, "DescriptorObservationUnavailable", 0)]
    [InlineData(2, 2, "DescriptorFlagsFileNotFoundException", 0)]
    [InlineData(2, -2147024894, "DescriptorFlagsIOException", 0)]
    [InlineData(1, 2, "RuntimeProofMappingsUnavailable", 0)]
    [InlineData(3, 2, "UnexpectedProcessIdentityLoss", 0)]
    [InlineData(3, 2, "DescriptorIdentityChangedDuringObservation", 0)]
    [InlineData(5, 5, "DescriptorIdentityChangedDuringObservation", 0)]
    [InlineData(0, 2, "DescriptorObservationUnavailable", 0)]
    public void SampledLossOnlyPositiveClosedKindsAreAdmitted(int operation, int error, string code, int kind)
    {
        var exception = new DurableStorageExperimentException(code, "controlled")
            { DescriptorFailure = new(operation, error, 9) };
        SampledLossProtocol.Classify(exception).Should().Be(kind);
        exception.KnownUnlinkedDescriptor = true;
        SampledLossProtocol.Classify(exception).Should().Be(0);
        SampledLossProtocol.Classify(new IOException("not positive")).Should().Be(0);
    }

    [Fact]
    public void SampledLossWireIsVersionedBoundedAndStrictGoldenIsUnchanged()
    {
        MonitoredSweepSummaryEncoding.EncodeLine(MonitoredSweepSummaryEncoding.CreateWorstCaseFixture())
            .Should().HaveCount(854);
        var summary = SampledLossProtocol.WorstCaseSummary();
        var bytes = MonitoredSweepSummaryEncoding.EncodeLine(summary);
        _output.WriteLine($"sampledWorstLfBytes={bytes.Length}");
        bytes.Length.Should().Be(947).And.BeLessThanOrEqualTo(1_024);
        bytes[^1].Should().Be((byte)'\n');
        var roundtrip = JsonSerializer.Deserialize<MonitoredSweepSummary>(bytes, PrevalidationProtocol.Json)!;
        roundtrip.Should().BeEquivalentTo(summary);
        roundtrip.Complete.Should().BeFalse();
        var totals = new SampledLossMeasurement(
            [13_554_432, 10_554_432, 1_000_000, 1_000_000, 1_000_000,
                10_000_000, 7_000_000, 1_000_000, 1_000_000, 1_000_000,
                10_000_000, 7_000_000, 1_000_000, 1_000_000, 1_000_000], [2, 4, -2147024894, int.MaxValue]);
        totals.Validate();
        var reply = new PrevalidationMonitorReply(false, new string('a', 64), int.MaxValue, long.MaxValue,
            int.MaxValue, long.MaxValue, summary) { SampledLoss = totals };
        var encoded = PrevalidationMonitorControl.Encode(reply);
        _output.WriteLine($"sampledWorstControlLfBytes={Encoding.UTF8.GetByteCount(encoded) + 1}");
        (Encoding.UTF8.GetByteCount(encoded) + 1).Should().Be(1_360).And.BeLessThanOrEqualTo(2_048);
        var decoded = JsonSerializer.Deserialize<PrevalidationMonitorReply>(encoded, PrevalidationProtocol.Json);
        decoded.Should().BeEquivalentTo(reply);
        MonitoredRunnerGeometry.MaximumRequiredSummaryRecords.Should().Be(1_265);
        (2_048L * 1_024 + PrevalidationMonitorControl.ReservedControlBytes + 64L * 8_193)
            .Should().BeLessThan(8_388_608);
    }

    [Fact]
    public void SampledLossAdmissionSeparatesLossesHardErrorsAndCompleteFlag()
    {
        var loss = SampledLossMeasurement.Empty();
        loss.Counts[0] = 2;
        loss.Counts[1] = 1;
        loss.Counts[2] = 1;
        loss = loss with { FirstLoss = [0, 1, 2, 9] };
        var summary = MonitoredSweepSummaryEncoding.CreateWorstCaseFixture() with
        {
            ErrorCount = 0, UnclassifiedCount = 0, Alarm = null, SampledLoss = loss,
        };
        SampledLossProtocol.Admissible(summary).Should().BeTrue();
        summary.Complete.Should().BeFalse();
        loss.Candidates.Should().Be(2);
        loss.Classified.Should().Be(1);
        loss.Lost.Should().Be(1);
        loss.LossRate.Should().Be(0.5);
        PrevalidationExecutor.RequireSweep(new(summary, [], []));
        var hard = summary with { ErrorCount = 1, FirstErrorCode = "DeclaredRootMissing" };
        SampledLossProtocol.Admissible(hard).Should().BeFalse();
        Action reject = () => PrevalidationExecutor.RequireSweep(new(hard, ["DeclaredRootMissing"], []));
        reject.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("DeclaredRootMissing");
        Action census = () => SampledLossProtocol.ValidateSummary(summary with { Complete = true });
        census.Should().Throw<DurableStorageExperimentException>();
    }

    [Theory]
    [InlineData("overflow")]
    [InlineData("negative")]
    [InlineData("population")]
    [InlineData("context")]
    [InlineData("role")]
    public void SampledLossRejectsInvalidCountersAndContexts(string mutation)
    {
        var loss = new SampledLossMeasurement([1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [0, 1, 2, 9]);
        switch (mutation)
        {
            case "overflow": loss.Counts[0] = SampledLossMeasurement.MaximumCandidatesPerEntry + 1; break;
            case "negative": loss.Counts[1] = -1; break;
            case "population": loss.Counts[1] = 1; break;
            case "context": loss.FirstLoss![2] = 13; break;
            case "role": loss.FirstLoss![0] = 1; break;
        }
        Action validate = () => loss.Validate();
        validate.Should().Throw<DurableStorageExperimentException>();
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(3, true, false)]
    [InlineData(4, true, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(3, false, false)]
    [InlineData(4, false, false)]
    [InlineData(1, true, true)]
    [InlineData(4, true, true)]
    public async Task SampledLossControlledNativeClosePreservesStrictDefault(int phase, bool sampled, bool missingRoot)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(fixture.Manifest.WorkspaceRoot, "controlled.bin");
        var info = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("exec 9>\"$1\"; printf 'ready\\n'; read -r command; exec 9>&-; printf 'closed\\n'; read -r command");
        info.ArgumentList.Add("controlled-descriptor");
        info.ArgumentList.Add(path);
        using var child = Process.Start(info)!;
        child.StandardOutput.ReadLine().Should().Be("ready");
        var identity = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        var attribution = missingRoot ? fixture.Attribution with
        {
            Roots = [.. fixture.Attribution.Roots, new("workspace", Path.Combine(_workspace, "missing-root"), true)],
        } : fixture.Attribution;
        await using var monitor = new MonitoredStorageMonitor(attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "controlled-monitor.jsonl"), sampledLoss: sampled);
        var closed = false;
        monitor.BeforeDescriptorOperationForComponent = (descriptor, operation) =>
        {
            if (closed || operation != phase || Path.GetFileName(descriptor) != "9") return;
            child.StandardInput.WriteLine("close");
            child.StandardInput.Flush();
            child.StandardOutput.ReadLine().Should().Be("closed");
            closed = true;
        };
        monitor.AddProcess(identity);
        try
        {
            var result = await monitor.ObserveBoundaryAsync("controlled-close", false, CancellationToken.None);
            closed.Should().BeTrue();
            result.Summary.Complete.Should().BeFalse();
            if (sampled)
            {
                if (missingRoot)
                {
                    result.Errors.Should().Contain("DeclaredRootMissing");
                    result.Summary.FirstErrorCode.Should().Be("DeclaredRootMissing");
                }
                else result.Errors.Should().BeEmpty();
                SampledLossProtocol.Admissible(result.Summary).Should().Be(!missingRoot);
                result.Summary.SampledLoss!.Lost.Should().Be(1);
                result.Summary.SampledLoss.Counts[phase is 1 or 3 ? 7 : 8].Should().Be(1);
                result.Summary.SampledLoss.Candidates.Should().Be(
                    result.Summary.SampledLoss.Classified + result.Summary.SampledLoss.Lost);
                result.Summary.SampledLoss.FirstLoss.Should().Equal(1, phase,
                    phase is 1 or 3 ? 2 : -2147024894, 9);
                monitor.IsIncomplete.Should().Be(missingRoot);
                monitor.TerminalIssue.IsCompleted.Should().Be(missingRoot);
            }
            else
            {
                result.Summary.SampledLoss.Should().BeNull();
                monitor.IsIncomplete.Should().BeTrue();
                monitor.TerminalIssue.IsCompleted.Should().BeTrue();
            }
        }
        finally
        {
            await monitor.KillOwnedAsync(identity, CancellationToken.None);
        }
        var post = await monitor.ObserveBoundaryAsync("owned-cleanup-quiescent", false, CancellationToken.None);
        post.Summary.Complete.Should().Be(!missingRoot);
    }

    [Fact]
    public async Task SampledLossProcessDeathIsNeverAConcurrentDescriptorLoss()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        using var child = StartWaitingStub();
        var identity = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Target);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "dead-monitor.jsonl"), sampledLoss: true);
        monitor.AddProcess(identity);
        OwnedProcessTerminator.KillExact(child, identity);
        await child.WaitForExitAsync();
        var result = monitor.Sweep();
        result.Errors.Should().Contain("UnexpectedProcessIdentityLoss");
        result.Summary.SampledLoss!.Lost.Should().Be(0);
        monitor.IsIncomplete.Should().BeTrue();
        SampledLossProtocol.Admissible(result.Summary).Should().BeFalse();
    }

    [Fact]
    public void SampledLossProspectiveManifestCannotUpgradeLegacyInspectionOrPolicy()
    {
        if (!OperatingSystem.IsLinux()) return;
        var manifest = PrevalidationContractFixture();
        var binding = SampledBinding(manifest);
        Action unversioned = () => PrevalidationProtocol.ValidateShape(manifest with { SampledLoss = binding }, true);
        unversioned.Should().Throw<DurableStorageExperimentException>();
        var sampled = manifest with { SampledLoss = binding, Schema = SampledLossProtocol.PrevalidationManifestSchema,
            ContextSummaryFieldMapSha256 = SampledLossProtocol.ContextMapSha256 };
        PrevalidationProtocol.ValidateShape(sampled);
        Action oldMap = () => PrevalidationProtocol.ValidateShape(sampled with
            { ContextSummaryFieldMapSha256 = PrevalidationProtocol.ContextSummaryFieldMapSha256 }, true);
        oldMap.Should().Throw<DurableStorageExperimentException>();
        Action unknown = () => SampledLossProtocol.ValidateBinding(binding with { Policy = "ignore-all-errors" });
        unknown.Should().Throw<DurableStorageExperimentException>();
        Action twoKindHash = () => SampledLossProtocol.ValidateBinding(binding with
            { ProtocolSha256 = "a4f51c446f7caf182441dc8b1d088ace405ccf32f3c79780a6d564ac23272483" });
        twoKindHash.Should().Throw<DurableStorageExperimentException>();
        Action twoKindMap = () => SampledLossProtocol.ValidateBinding(binding with
            { ContextMapSha256 = "d3a2b36cfa7ac65da20d2def4ce02a2f6f36caf6a40642af38e42bf5f178529d" });
        twoKindMap.Should().Throw<DurableStorageExperimentException>();
        Action oldExecution = () => PrevalidationProtocol.ValidateShape(manifest with
            { ContextSummaryFieldMapSha256 = PrevalidationProtocol.LegacyContextSummaryFieldMapSha256 });
        oldExecution.Should().Throw<DurableStorageExperimentException>();
        PrevalidationProtocol.ValidateShape(manifest with
            { ContextSummaryFieldMapSha256 = PrevalidationProtocol.LegacyContextSummaryFieldMapSha256 }, true);
        MonitoredFile.HashFile(Path.Combine(FindRepositoryRoot(), SampledLossProtocol.Path))
            .Should().Be(SampledLossProtocol.ProtocolSha256);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("build")]
    [InlineData("unsealed")]
    public void SampledLossReadinessRejectsForeignManifestBuildOrUnsealedCoverage(string mutation)
    {
        if (!OperatingSystem.IsLinux()) return;
        var pv = PrevalidationContractFixture();
        pv = pv with { SampledLoss = SampledBinding(pv),
            Schema = SampledLossProtocol.PrevalidationManifestSchema,
            ContextSummaryFieldMapSha256 = SampledLossProtocol.ContextMapSha256 };
        var campaign = PrepareComponentExecution().Manifest;
        campaign = campaign with
        {
            Schema = SampledLossProtocol.CampaignManifestSchema, SampledLoss = pv.SampledLoss,
            SourceCommits = pv.SourceCommits, RuntimeBinary = pv.RuntimeBinary, ToolBinary = pv.ToolBinary,
            SampleBinary = pv.SampleBinary, NativeBinaries = pv.NativeBinaries, Host = pv.Host,
            FixtureManifestSha256 = pv.FixtureManifest.Sha256, Clock = pv.Clock,
        };
        if (mutation == "build")
            campaign = campaign with { ToolBinary = campaign.ToolBinary with { Sha256 = new string('f', 64) } };
        Directory.CreateDirectory(pv.PrivateRoot);
        var manifestPath = Path.Combine(_workspace, "sampled-pv.json");
        PrevalidationExecutor.WriteImmutable(manifestPath, pv);
        var sealPath = Path.Combine(pv.PrivateRoot, "seal.json");
        PrevalidationExecutor.WriteImmutable(sealPath, new PrevalidationSeal(
            "durable-prevalidation-seal/1", pv.SuiteId, HashFile(manifestPath), new string('a', 64), []));
        var readiness = new SampledLossReadiness("durable-sampled-readiness/1", Identity(manifestPath),
            Identity(sealPath), mutation == "manifest" ? new string('f', 64)
                : SampledLossProtocol.CampaignBindingHash(campaign), "component-only-not-real-review", DateTimeOffset.UtcNow);
        var receiptPath = Path.Combine(_workspace, "sampled-readiness.json");
        PrevalidationExecutor.WriteImmutable(receiptPath, readiness);
        campaign = campaign with { SampledLoss = campaign.SampledLoss! with { Readiness = Identity(receiptPath) } };
        Action admit = () => SampledLossProtocol.ValidateReadiness(campaign);
        admit.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be(mutation switch
        {
            "manifest" => "SampledLossReadinessBindingMismatch",
            "build" => "SampledLossReadinessForeignBuildOrHost",
            _ => "PrevalidationSealMismatch",
        });
    }

    [Fact]
    public void SampledLossDecisionCannotEnterStrictGate()
    {
        if (!OperatingSystem.IsLinux()) return;
        var outcomes = LiveP95Outcomes([100, 100, 100], [1, 1, 1])
            .Select(outcome => outcome with { SampledLoss = SampledLossMeasurement.Empty(), SampledAdmissible = true })
            .ToArray();
        MonitoredDecisionEngine.Decide(outcomes).CompleteEvidence.Should().BeFalse();
        var campaign = PrepareComponentExecution().Manifest;
        Action missingReadiness = () => MonitoredDecisionEngine.Decide(outcomes, campaign);
        missingReadiness.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("SampledLossReadinessMissing");
    }

    [Fact]
    public async Task SampledLossScriptedRecoveryPreservesLifecycleAndPopulation()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var binding = new SampledLossBinding(SampledLossProtocol.Policy, SampledLossProtocol.ProtocolSha256,
            SampledLossProtocol.ContextMapSha256, PrevalidationProtocol.RuntimeEnvironmentHash(),
            [fixture.Manifest.ToolBinary], null);
        fixture = fixture with { Manifest = fixture.Manifest with { SampledLoss = binding } };
        var launcher = new ScriptedWorkerLauncher(ScriptedWorkerBehavior.SuccessfulRecovery);
        var execution = MonitoredExecutionPlanner.Expand().Single(item => item.CaseId == "F3" && item.Candidate == "A");
        var outcome = await MonitoredCampaignRunner.RunExecutionForComponentAsync(fixture, execution, launcher,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        outcome.SampledAdmissible.Should().BeTrue();
        outcome.SampledLoss.Should().NotBeNull();
        outcome.SampledLoss!.Candidates.Should().Be(outcome.SampledLoss.Classified + outcome.SampledLoss.Lost);
        launcher.Descriptors.Should().HaveCount(2);
        outcome.FailureCode.Should().BeNull();
    }

    [Fact]
    public async Task SampledLossAllRecordsFitAndOverflowStopsBeforeInsertion()
    {
        var path = Path.Combine(_workspace, "sampled-2048.jsonl");
        var budget = new BoundedOutputBudget(8_388_608, 2_048);
        await using var writer = new BoundedJsonLineWriter(path, 1_024, 2_048, 2_048 * 1_024, budget);
        var summary = SampledLossProtocol.WorstCaseSummary();
        for (var index = 0; index < 2_048; index++)
            await writer.WriteAsync(summary, CancellationToken.None);
        writer.Records.Should().Be(2_048);
        writer.Bytes.Should().Be(2_048L * 947);
        Func<Task> extra = async () => await writer.WriteAsync(summary, CancellationToken.None);
        (await extra.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("MonitorSummaryCountLimit");
        writer.Records.Should().Be(2_048);
        writer.Bytes.Should().Be(2_048L * 947);
        var maximum = new SampledLossMeasurement(
            [SampledLossMeasurement.MaximumCandidatesPerEntry, SampledLossMeasurement.MaximumCandidatesPerEntry,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], null);
        var one = new SampledLossMeasurement([1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], null);
        Action overflow = () => SampledLossMeasurement.Merge(maximum, one);
        overflow.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("SampledLossCounterInvalid");
        maximum.Candidates.Should().Be(SampledLossMeasurement.MaximumCandidatesPerEntry);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SampledLossPositivelyKnownUnlinkedCloseIsNotAdmitted(int phase)
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_workspace, "known-unlinked");
        using var file = File.OpenWrite(path);
        var fd = checked((int)file.SafeFileHandle.DangerousGetHandle());
        File.Delete(path);
        Action observe = () => LinuxProcessDescriptorObserver.OpenCoherentForComponent(
            Environment.ProcessId, $"/proc/{Environment.ProcessId}/fd/{fd}", 4_096,
            operation => { if (operation == phase) file.Dispose(); }).Dispose();
        var exception = observe.Should().Throw<DurableStorageExperimentException>().Which;
        exception.KnownUnlinkedDescriptor.Should().BeTrue();
        SampledLossProtocol.Classify(exception).Should().Be(0);
    }

    [Fact]
    public void SampledLossRejectsUnversionedOrContradictoryWire()
    {
        var summary = SampledLossProtocol.WorstCaseSummary();
        var unversioned = JsonSerializer.Serialize(summary, JsonOptions);
        Action legacy = () => JsonSerializer.Deserialize<MonitoredSweepSummary>(unversioned, PrevalidationProtocol.Json);
        legacy.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("SampledLossUnversionedWire");
        var wire = System.Text.Json.Nodes.JsonNode.Parse(MonitoredSweepSummaryEncoding.EncodeLine(summary))!;
        wire["f"]![3] = true;
        Action contradictory = () => JsonSerializer.Deserialize<MonitoredSweepSummary>(wire.ToJsonString(),
            PrevalidationProtocol.Json);
        contradictory.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("SampledLossWireAdmissionMismatch");
    }

    [Theory]
    [InlineData("n", false)]
    [InlineData("n", true)]
    [InlineData("t", false)]
    [InlineData("t", true)]
    [InlineData("f", false)]
    [InlineData("f", true)]
    [InlineData("l", false)]
    [InlineData("l", true)]
    public void SampledLossMissingWireGroupsHaveStructuredFailures(string group, bool explicitNull)
    {
        var wire = System.Text.Json.Nodes.JsonNode.Parse(
            MonitoredSweepSummaryEncoding.EncodeLine(SampledLossProtocol.WorstCaseSummary()))!.AsObject();
        if (explicitNull) wire[group] = null;
        else wire.Remove(group);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new SampledSweepConverter());
        Action read = () => JsonSerializer.Deserialize<MonitoredSweepSummary>(wire.ToJsonString(), options);
        read.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("SampledLossWireShape");
    }

    [Theory]
    [InlineData("n", false)]
    [InlineData("n", true)]
    [InlineData("t", false)]
    [InlineData("t", true)]
    [InlineData("f", false)]
    [InlineData("f", true)]
    public void SampledLossWrongWireGroupLengthsHaveStructuredFailures(string group, bool oversized)
    {
        var wire = System.Text.Json.Nodes.JsonNode.Parse(
            MonitoredSweepSummaryEncoding.EncodeLine(SampledLossProtocol.WorstCaseSummary()))!;
        var values = wire[group]!.AsArray();
        if (oversized) values.Add(values[0]!.DeepClone());
        else values.RemoveAt(0);
        Action read = () => JsonSerializer.Deserialize<MonitoredSweepSummary>(wire.ToJsonString(),
            PrevalidationProtocol.Json);
        read.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("SampledLossWireShape");
    }

    [Fact]
    public void SampledLossNullCountersHaveStructuredFailure()
    {
        var loss = new SampledLossMeasurement(null!, null);
        Action validate = () => loss.Validate(SampledLossMeasurement.MaximumCandidatesPerSweep);
        validate.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("SampledLossCounterInvalid");
    }

    [Theory]
    [InlineData(0, 2147483648L)]
    [InlineData(12, -2147483649L)]
    [InlineData(18, 2147483648L)]
    [InlineData(27, 2147483648L)]
    [InlineData(28, -2147483649L)]
    public void SampledLossOutOfRangeWireScalarsHaveStructuredFailures(int index, long value)
    {
        var wire = System.Text.Json.Nodes.JsonNode.Parse(
            MonitoredSweepSummaryEncoding.EncodeLine(SampledLossProtocol.WorstCaseSummary()))!;
        wire["n"]![index] = value;
        Action read = () => JsonSerializer.Deserialize<MonitoredSweepSummary>(wire.ToJsonString(),
            PrevalidationProtocol.Json);
        read.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("SampledLossWireScalarRange");
    }

    [Fact]
    public void SampledLossReportAdmitsKnownLossCoverageOnlyWithNewManifest()
    {
        if (!OperatingSystem.IsLinux()) return;
        var original = PrevalidationContractFixture();
        var manifest = original with { SampledLoss = SampledBinding(original),
            Schema = SampledLossProtocol.PrevalidationManifestSchema,
            ContextSummaryFieldMapSha256 = SampledLossProtocol.ContextMapSha256 };
        var loss = new SampledLossMeasurement([1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [0, 5, 5, 9]);
        var coverage = manifest.Probes.Select(probe => new PrevalidationCoverage(
            PrevalidationProtocol.CoverageSchema, probe.Ordinal, probe.Id, "coverage-observed", null, false,
            probe.FixtureSlots, probe.FixtureSlots * 4, 571, 512, null, null, null, [])
            { SampledLoss = loss, SampledAdmissible = true }).ToArray();
        var hash = HashText("controlled-sampled-report-not-real-readiness");
        var report = new PrevalidationReport(PrevalidationProtocol.ReportSchema, PrevalidationProtocol.Scope,
            manifest.SuiteId, hash, manifest.HistoricalReport.Sha256, "coverage-observed-awaiting-independent-review",
            PrevalidationLayout.DeriveSuiteIdentityBound(), coverage, "controlled DTO shape only; not sealed coverage");
        PrevalidationReportValidation.ValidateReport(manifest, hash, report);
        coverage[0].ObservationPopulation!.LostObservations.Should().Be(1);
        coverage[0].ObservationPopulation!.LossRate.Should().Be(1);
        Action strict = () => PrevalidationReportValidation.ValidateReport(original, hash, report);
        strict.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("SampledLossCoveragePolicyMismatch");
        coverage[0] = coverage[0] with { MonitoringComplete = true };
        Action falseCompleteness = () => PrevalidationReportValidation.ValidateReport(manifest, hash, report);
        falseCompleteness.Should().Throw<DurableStorageExperimentException>();
    }

    private static SampledLossBinding SampledBinding(PrevalidationManifest manifest)
        => new(SampledLossProtocol.Policy, SampledLossProtocol.ProtocolSha256, SampledLossProtocol.ContextMapSha256,
            manifest.RuntimeEnvironmentSha256, manifest.ManagedBinaries, null);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void SampledLossRealReuseAndFlagsRequireSnapshotProof(bool flagsOnly, bool sampled)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var first = File.OpenWrite(Path.Combine(_workspace, "coherence-original"));
        using var replacement = File.OpenWrite(Path.Combine(_workspace, "coherence-replacement"));
        var fd = checked((int)first.SafeFileHandle.DangerousGetHandle());
        var owner = new MonitoredProcessIdentity(Environment.ProcessId,
            LinuxProcessIdentity.ReadStartTime(Environment.ProcessId), MonitoredProcessRole.Harness);
        Action observe = () => LinuxProcessDescriptorObserver.OpenCoherentForComponent(
            owner.ProcessId, $"/proc/{owner.ProcessId}/fd/{fd}", 4_096, phase =>
            {
                if (phase != 3) return;
                if (flagsOnly)
                {
                    var flags = DescriptorFcntl(fd, 3, 0);
                    flags.Should().BeGreaterThanOrEqualTo(0);
                    DescriptorFcntl(fd, 4, flags ^ 0x400).Should().Be(0);
                }
                else DuplicateDescriptor(checked((int)replacement.SafeFileHandle.DangerousGetHandle()), fd)
                    .Should().Be(fd);
            }, sampledOwner: sampled ? owner : null).Dispose();
        var error = observe.Should().Throw<DurableStorageExperimentException>().Which;
        error.Code.Should().Be("DescriptorIdentityChangedDuringObservation");
        SampledLossProtocol.Classify(error, owner).Should().Be(sampled ? 4 : 0);
        if (sampled)
        {
            error.CoherenceProof!.Valid.Should().BeTrue();
            // dup2 also clears the slot's close-on-exec flag.
            error.DescriptorFailure.Should().Be(new DescriptorObservationFailure(5, flagsOnly ? 2 : 7, fd));
            (error.CoherenceProof.First.Flags ^ error.CoherenceProof.Second.Flags)
                .Should().Be(flagsOnly ? 0x400 : 0x80000);
            _output.WriteLine($"controlledCoherence={JsonSerializer.Serialize(error.DescriptorFailure)}");
            SampledLossProtocol.Classify(error, owner with { LinuxStartTimeTicks = owner.LinuxStartTimeTicks + 1 })
                .Should().Be(0);
            SampledLossProtocol.Classify(error, owner with { Role = MonitoredProcessRole.Target }).Should().Be(0);
        }
        else
        {
            error.CoherenceProof.Should().BeNull();
            error.DescriptorFailure.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("missing-metadata")]
    [InlineData("invalid-flags")]
    [InlineData("invalid-identity")]
    [InlineData("zero-links")]
    [InlineData("deleted")]
    [InlineData("no-change")]
    [InlineData("wrong-mask")]
    [InlineData("reused-process")]
    public void SampledLossInvalidCoherenceProofIsHardFailure(string mutation)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var file = File.OpenWrite(Path.Combine(_workspace, "proof-snapshot"));
        var fd = checked((int)file.SafeFileHandle.DangerousGetHandle());
        using var pinned = LinuxProcessDescriptorObserver.OpenCoherent(
            Environment.ProcessId, $"/proc/{Environment.ProcessId}/fd/{fd}", 4_096);
        var owner = new MonitoredProcessIdentity(Environment.ProcessId,
            LinuxProcessIdentity.ReadStartTime(Environment.ProcessId), MonitoredProcessRole.Harness);
        var first = pinned.Snapshot;
        var second = first with { Flags = first.Flags ^ 0x400 };
        switch (mutation)
        {
            case "missing-metadata": second = second with { RegularFileMetadata = null }; break;
            case "invalid-flags": second = second with { Flags = -1 }; break;
            case "invalid-identity": second = second with { Identity = default }; break;
            case "zero-links":
                second = second with { RegularFileMetadata = second.RegularFileMetadata!.Value with { LinkCount = 0 } };
                break;
            case "deleted": second = second with { Target = second.Target + " (deleted)" }; break;
            case "no-change": second = first; break;
            case "reused-process": owner = owner with { LinuxStartTimeTicks = owner.LinuxStartTimeTicks + 1 }; break;
        }
        var proof = new DescriptorCoherenceProof(owner, first, second);
        var exception = new DurableStorageExperimentException("DescriptorIdentityChangedDuringObservation", "controlled")
        {
            CoherenceProof = proof,
            DescriptorFailure = new(5, mutation == "wrong-mask" ? 7 : proof.Changes, fd),
        };
        SampledLossProtocol.Classify(exception, owner).Should().Be(0);
    }

    [Theory]
    [InlineData(false, true, "none")]
    [InlineData(true, true, "none")]
    [InlineData(false, false, "none")]
    [InlineData(true, false, "none")]
    [InlineData(false, true, "deleted-first")]
    [InlineData(false, true, "deleted-second")]
    [InlineData(false, true, "unknown-target")]
    [InlineData(false, true, "missing-root")]
    [InlineData(false, true, "runtime-proof")]
    public async Task SampledLossCoherenceSweepKeepsHardErrorsAndPopulation(bool flagsOnly, bool sampled, string hard)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var first = Path.Combine(fixture.Manifest.WorkspaceRoot, "first");
        var second = hard == "unknown-target" ? Path.Combine(_workspace, "unknown-replacement")
            : Path.Combine(fixture.Manifest.WorkspaceRoot, "second");
        var info = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(flagsOnly
            ? "exec 9>\"$1\"; printf 'ready\\n'; read -r command; exec 9>>\"$1\"; printf 'changed\\n'; read -r command"
            : "exec 9>\"$1\"; exec 8>\"$2\"; printf 'ready\\n'; read -r command; exec 9>&8; printf 'changed\\n'; read -r command");
        info.ArgumentList.Add("controlled-coherence");
        info.ArgumentList.Add(first);
        info.ArgumentList.Add(second);
        using var child = Process.Start(info)!;
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Target);
        var attribution = hard == "missing-root" ? fixture.Attribution with
        {
            Roots = [.. fixture.Attribution.Roots, new("workspace", Path.Combine(_workspace, "missing"), true)],
        } : fixture.Attribution;
        if (hard == "runtime-proof")
            attribution = attribution with
            {
                RuntimeOnlyDescriptorProofs = [attribution.RuntimeOnlyDescriptorProofs.Single() with
                    { DescriptorTarget = second }],
            };
        await using var monitor = new MonitoredStorageMonitor(attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "coherence-monitor.jsonl"), sampledLoss: sampled);
        monitor.AddProcess(owner);
        if (hard == "deleted-first") File.Delete(first);
        if (hard == "deleted-second") File.Delete(second);
        var changed = false;
        monitor.BeforeDescriptorOperationForComponent = (descriptor, operation) =>
        {
            if (changed || operation != 3 || Path.GetFileName(descriptor) != "9") return;
            child.StandardInput.WriteLine("change");
            child.StandardInput.Flush();
            child.StandardOutput.ReadLine().Should().Be("changed");
            changed = true;
        };
        try
        {
            var result = await monitor.ObserveBoundaryAsync("controlled-coherence", false, CancellationToken.None);
            changed.Should().BeTrue();
            var admitted = sampled && hard is "none" or "missing-root";
            result.Summary.Complete.Should().BeFalse();
            SampledLossProtocol.Admissible(result.Summary).Should().Be(sampled && hard == "none");
            if (sampled)
            {
                var loss = result.Summary.SampledLoss!;
                loss.Counts[14].Should().Be(admitted ? 1 : 0);
                loss.Lost.Should().Be(admitted ? 1 : 0);
                if (admitted) loss.FirstLoss.Should().Equal(2, 5, flagsOnly ? 2 : 5, 9);
                if (hard == "none")
                {
                    result.Errors.Should().BeEmpty();
                    loss.Candidates.Should().Be(loss.Classified + loss.Lost);
                    monitor.LossTotals.Should().BeEquivalentTo(loss);
                }
            }
            monitor.IsIncomplete.Should().Be(!sampled || hard != "none");
            if (!sampled || !admitted) result.Errors.Should().Contain("DescriptorIdentityChangedDuringObservation");
            if (hard == "missing-root") result.Errors.Should().Contain("DeclaredRootMissing");
            var line = MonitoredSweepSummaryEncoding.EncodeLine(result.Summary);
            JsonSerializer.Deserialize<MonitoredSweepSummary>(line, PrevalidationProtocol.Json)
                .Should().BeEquivalentTo(result.Summary);
            var persisted = File.ReadAllBytes(Path.Combine(fixture.Manifest.OutputRoot, "coherence-monitor.jsonl"));
            persisted.Should().Equal(line);
        }
        finally
        {
            await monitor.KillOwnedAsync(owner, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(1, 13)]
    [InlineData(1, 5)]
    [InlineData(3, 13)]
    [InlineData(3, 5)]
    public void SampledLossNativeErrorsNeverProduceCoherenceProof(int phase, int errno)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var file = File.OpenWrite(Path.Combine(_workspace, "coherence-native-error"));
        var fd = checked((int)file.SafeFileHandle.DangerousGetHandle());
        var owner = new MonitoredProcessIdentity(Environment.ProcessId,
            LinuxProcessIdentity.ReadStartTime(Environment.ProcessId), MonitoredProcessRole.Harness);
        Action observe = () => LinuxProcessDescriptorObserver.OpenCoherentForComponent(owner.ProcessId,
            $"/proc/{owner.ProcessId}/fd/{fd}", 4_096, null,
            operation => operation == phase ? errno : null, owner).Dispose();
        var exception = observe.Should().Throw<DurableStorageExperimentException>().Which;
        exception.DescriptorFailure.Should().Be(new DescriptorObservationFailure(phase, errno, fd));
        exception.CoherenceProof.Should().BeNull();
        SampledLossProtocol.Classify(exception, owner).Should().Be(0);
    }

    [Fact]
    public void SampledLossTwoKindWireCannotEnterCoherenceReadiness()
    {
        var wire = System.Text.Json.Nodes.JsonNode.Parse(
            MonitoredSweepSummaryEncoding.EncodeLine(SampledLossProtocol.WorstCaseSummary()))!;
        wire["l"] = new System.Text.Json.Nodes.JsonArray(1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Action read = () => JsonSerializer.Deserialize<MonitoredSweepSummary>(wire.ToJsonString(),
            PrevalidationProtocol.Json);
        read.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("SampledLossCounterInvalid");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SampledLossCoherenceCodecAndCountersAreClosedByRole(int role)
    {
        var loss = SampledLossMeasurement.Empty();
        loss.Counts[role * 5] = 1;
        loss.Counts[role * 5 + 4] = 1;
        loss = loss with { FirstLoss = [role, 5, 7, int.MaxValue] };
        var summary = SampledLossProtocol.WorstCaseSummary() with { SampledLoss = loss };
        for (var mask = 1; mask <= 7; mask++)
        {
            loss.FirstLoss![2] = mask;
            var hard = summary with { FirstDescriptorFailure = [role, 5, mask, int.MaxValue] };
            var line = MonitoredSweepSummaryEncoding.EncodeLine(hard);
            JsonSerializer.Deserialize<MonitoredSweepSummary>(line, PrevalidationProtocol.Json)
                .Should().BeEquivalentTo(hard);
            Action strict = () => MonitoredSweepSummaryEncoding.EncodeLine(hard with { SampledLoss = null });
            strict.Should().Throw<DurableStorageExperimentException>();
            var legacy = JsonSerializer.Serialize(hard with { SampledLoss = null }, JsonOptions);
            Action readStrict = () => JsonSerializer.Deserialize<MonitoredSweepSummary>(legacy, PrevalidationProtocol.Json);
            readStrict.Should().Throw<DurableStorageExperimentException>();
        }
        foreach (var mask in new[] { 0, 8, -1, int.MaxValue })
        {
            loss.FirstLoss![2] = mask;
            Action invalid = () => MonitoredSweepSummaryEncoding.EncodeLine(summary);
            invalid.Should().Throw<DurableStorageExperimentException>();
        }
        loss.FirstLoss![2] = 1;
        loss.Counts[role * 5] = SampledLossMeasurement.MaximumCandidatesPerEntry;
        loss.Counts[role * 5 + 4] = SampledLossMeasurement.MaximumCandidatesPerEntry;
        var one = SampledLossMeasurement.Empty();
        one.Counts[role * 5] = one.Counts[role * 5 + 4] = 1;
        one = one with { FirstLoss = [role, 5, 1, 9] };
        Action overflow = () => SampledLossMeasurement.Merge(loss, one);
        overflow.Should().Throw<DurableStorageExperimentException>();
        loss.Counts[role * 5 + 4] = int.MaxValue;
        Action invalidCounter = () => loss.Validate();
        invalidCounter.Should().Throw<DurableStorageExperimentException>();
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int DescriptorFcntl(int descriptor, int command, int argument);
}
