using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Tests.DurableCounterSpike.AppendFirst;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed partial class MonitoredRunnerTests
{
    [Theory]
    [InlineData("periodic")]
    [InlineData("boundary")]
    public async Task UnifiedActiveRealLeafUnlinkIsExplicitLoss(string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "mutable");
        Directory.CreateDirectory(root);
        var leaf = Path.Combine(root, "leaf");
        File.WriteAllBytes(leaf, [1, 2, 3]);
        await using var monitor = UnifiedMonitor(root);
        monitor.SetStage("before-pre-seal-finalization", true);
        monitor.BeforeRootPathObservationForComponent = File.Delete;
        var result = monitor.Sweep(kind);
        result.Errors.Should().BeEmpty();
        result.Summary.Complete.Should().BeFalse();
        DescriptorObservationPolicy.Admissible(result.Summary).Should().BeTrue();
        result.Summary.SampledLoss!.RootSampling.Should().Be(new RootSamplingMeasurement(1, 0, 1, 0));
        result.Summary.SampledLoss.FirstLoss.Should().BeNull("root losses are not descriptor contexts");
        result.Summary.FirstErrorCode.Should().BeNull();
        monitor.IsIncomplete.Should().BeFalse();
        var population = SampledLossPopulation.From(result.Summary.SampledLoss)!;
        population.RootSampling!.NamespaceCoverage.Should().Be("partial");
        population.RootSampling.LostBytesAndTypes.Should().Be("unknown-not-zero");
        JsonSerializer.Deserialize<MonitoredSweepSummary>(
            MonitoredSweepSummaryEncoding.EncodeLine(result.Summary), PrevalidationProtocol.Json)
            .Should().BeEquivalentTo(result.Summary);
    }

    [Fact]
    public async Task UnifiedActiveLostBranchHasUnknownDescendantsNotZeroDenominator()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "mutable");
        var branch = Path.Combine(root, "branch");
        Directory.CreateDirectory(branch);
        File.WriteAllBytes(Path.Combine(branch, "never-enumerated"), [1, 2, 3]);
        await using var monitor = UnifiedMonitor(root);
        monitor.SetStage("admission", true);
        monitor.BeforeRootDirectoryObservationForComponent = path =>
        {
            if (path == branch) Directory.Move(branch, Path.Combine(_workspace, "moved"));
        };
        var result = monitor.Sweep();
        result.Errors.Should().BeEmpty();
        var rootLoss = result.Summary.SampledLoss!.RootSampling!;
        rootLoss.Should().Be(new RootSamplingMeasurement(0, 0, 0, 1));
        RootSamplingPopulation.From(rootLoss)!.UnknownBranchDescendants.Should().Be("unknown-not-zero");
        result.Summary.Complete.Should().BeFalse();
        DescriptorObservationPolicy.Admissible(result.Summary).Should().BeTrue("an all-lost pass has no invented threshold");
        Action require = () => PrevalidationExecutor.RequireSweep(result);
        require.Should().NotThrow();
    }

    [Theory]
    [InlineData("quiescent")]
    [InlineData("control")]
    [InlineData("completed")]
    [InlineData("retained")]
    [InlineData("readonly")]
    [InlineData("recovery-source")]
    public async Task UnifiedActiveStrictNamespacesNeverAdmitLeafLoss(string scope)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "root");
        var mutable = Path.Combine(root, "mutable");
        Directory.CreateDirectory(mutable);
        var parent = scope is "control" or "retained" ? root : mutable;
        if (scope == "recovery-source")
        {
            parent = Path.Combine(mutable, "package-staging");
            Directory.CreateDirectory(parent);
        }
        var leaf = Path.Combine(parent, "leaf");
        File.WriteAllBytes(leaf, [1]);
        if (scope == "readonly") File.SetUnixFileMode(leaf, UnixFileMode.UserRead);
        var coordinator = new MonitoredProcessIdentity(Environment.ProcessId,
            LinuxProcessIdentity.ReadStartTime(Environment.ProcessId), MonitoredProcessRole.Harness);
        await using var monitor = new MonitoredStorageMonitor(
            Attribution(root, root, root, root) with { Roots = [new("workspace", root, true)] },
            EncodingContract(), Path.Combine(_workspace, "strict-monitor.jsonl"),
            prevalidationScope: scope == "completed" ? new([mutable], coordinator) : null,
            unifiedActive: true, activeOwnedRoots: [mutable]);
        monitor.SetStage(scope == "recovery-source" ? "recovery" : "controlled", scope != "quiescent");
        monitor.BeforeRootPathObservationForComponent = File.Delete;
        var result = monitor.Sweep();
        result.Errors.Should().ContainSingle();
        result.Summary.SampledLoss!.RootSampling!.HasLoss.Should().BeFalse();
        DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
        result.Summary.Complete.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnifiedActiveStrictBranchLossIsHard(bool quiescent)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "root");
        var branch = Path.Combine(root, "branch");
        Directory.CreateDirectory(branch);
        await using var monitor = new MonitoredStorageMonitor(
            Attribution(root, root, root, root) with { Roots = [new("workspace", root, true)] },
            EncodingContract(), Path.Combine(_workspace, "branch-monitor.jsonl"), unifiedActive: true,
            activeOwnedRoots: quiescent ? [root] : []);
        monitor.SetStage("controlled", !quiescent);
        monitor.BeforeRootDirectoryObservationForComponent = path =>
        {
            if (path == branch) Directory.Move(branch, Path.Combine(_workspace, "moved"));
        };
        var result = monitor.Sweep();
        result.Errors.Should().Contain("RootObservationDirectoryNotFoundException");
        result.Summary.SampledLoss!.RootSampling!.LostDirectoryBranches.Should().Be(0);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("permission")]
    [InlineData("wrong-hresult")]
    [InlineData("metadata")]
    public async Task UnifiedActivePositiveErrorClassificationDoesNotCatchOtherFailures(string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "mutable");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "leaf"), [1]);
        await using var monitor = UnifiedMonitor(root);
        monitor.SetStage("admission", true);
        monitor.BeforeRootPathObservationForComponent = _ => throw kind switch
        {
            "permission" => new UnauthorizedAccessException(),
            "wrong-hresult" => new FileNotFoundException("unknown") { HResult = 5 },
            "metadata" => new DurableStorageExperimentException("RootMetadataInvalid", "component"),
            _ => new IOException("generic I/O"),
        };
        var result = monitor.Sweep();
        result.Errors.Should().ContainSingle();
        result.Summary.SampledLoss!.RootSampling!.LostFilePaths.Should().Be(0);
        DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
    }

    [Fact]
    public async Task UnifiedActiveLossCannotHidePositiveBudgetOverflow()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "mutable");
        var package = Path.Combine(root, "package-staging");
        Directory.CreateDirectory(package);
        var lost = Path.Combine(root, "lost");
        File.WriteAllBytes(lost, [1]);
        using (var sparse = File.Create(Path.Combine(package, "large"))) sparse.SetLength(268_435_457);
        await using var monitor = UnifiedMonitor(root);
        monitor.SetStage("admission", true);
        monitor.BeforeRootPathObservationForComponent = path => { if (path == lost) File.Delete(path); };
        var result = monitor.Sweep();
        result.Summary.SampledLoss!.RootSampling!.LostFilePaths.Should().Be(1);
        result.Summary.PackageBytes.Should().Be(268_435_457);
        result.Summary.Alarm.Should().Be("ObservedPackageRecoveryThresholdExceeded");
        DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
    }

    [Fact]
    public async Task UnifiedActiveAtomicReplacementObservesNewNativeIdentity()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "mutable");
        Directory.CreateDirectory(root);
        var leaf = Path.Combine(root, "leaf");
        File.WriteAllBytes(leaf, [1, 2, 3]);
        using var old = File.OpenHandle(leaf);
        var prior = LinuxStatxHandleMetadataObserver.Instance.Observe(old);
        await using var monitor = UnifiedMonitor(root, identities: true);
        monitor.SetStage("admission", true);
        monitor.BeforeRootPathObservationForComponent = path =>
        {
            var replacement = Path.Combine(_workspace, "replacement");
            File.WriteAllBytes(replacement, [4]);
            File.Move(replacement, path, true);
        };
        var result = monitor.Sweep();
        result.Errors.Should().BeEmpty();
        result.Summary.Complete.Should().BeTrue();
        result.IdentityEvidence.Should().ContainSingle().Which.Identity.Should().NotBe(prior.Identity.Value);
        result.Summary.WorkspaceBytes.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnifiedActiveEnumerationCapsBeforeRetainingMoreCandidates(bool directories)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "mutable");
        Directory.CreateDirectory(root);
        for (var index = 0; index < 4_097; index++)
        {
            var path = Path.Combine(root, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (directories) Directory.CreateDirectory(path);
            else File.WriteAllBytes(path, []);
        }
        var traversal = new ActiveRootTraversal();
        var visits = 0;
        var errors = new List<Exception>();
        traversal.Observe(root, 4_096, _ => true, _ => { visits++; return true; }, errors.Add);
        errors.OfType<DurableStorageExperimentException>().Should().Contain(error =>
            error.Code == (directories ? "RootDirectoryWorkLimitExceeded" : "TrackedPathLimitExceeded"));
        visits.Should().Be(directories ? 0 : 4_096);
        traversal.Measurement.KnownFileCandidates.Should().Be(directories ? 0 : 4_096);
        await Task.CompletedTask;
    }

    [Fact]
    public void UnifiedActiveMaximalWireAndCumulativeAuthorityStayWithinFrozenBudgets()
    {
        var summary = UnifiedActiveProtocol.WorstCaseSummary();
        var encoded = MonitoredSweepSummaryEncoding.EncodeLine(summary);
        encoded.Length.Should().BeLessThanOrEqualTo(1_024);
        JsonSerializer.Deserialize<MonitoredSweepSummary>(encoded, PrevalidationProtocol.Json)
            .Should().BeEquivalentTo(summary);
        var totals = summary.SampledLoss!;
        for (var index = 1; index < 2_048; index++)
            totals = SampledLossMeasurement.Merge(totals, summary.SampledLoss!);
        totals.RootSampling.Should().Be(new RootSamplingMeasurement(8_388_608, 4_194_304, 4_194_304, 8_388_608));
        var reply = new PrevalidationMonitorReply(false, new string('a', 64), int.MaxValue, long.MaxValue,
            int.MaxValue, long.MaxValue, summary)
        { SampledLoss = totals };
        UnifiedActiveProtocol.WorstCaseAuthorityReply().Should().BeEquivalentTo(reply);
        var frame = PrevalidationMonitorControl.Encode(reply);
        (Encoding.UTF8.GetByteCount(frame) + 1).Should().BeLessThanOrEqualTo(2_048);
        JsonSerializer.Deserialize<PrevalidationMonitorReply>(frame, PrevalidationProtocol.Json)
            .Should().BeEquivalentTo(reply);
        Action overflow = () => SampledLossMeasurement.Merge(totals, summary.SampledLoss!);
        overflow.Should().Throw<DurableStorageExperimentException>();
        var campaign = SampledLossMeasurement.Empty(unifiedActive: true);
        for (var index = 0; index < 35; index++)
            campaign = SampledLossMeasurement.Merge(campaign, totals, 35 * SampledLossMeasurement.MaximumCandidatesPerEntry);
        campaign.RootSampling!.KnownFileCandidates.Should().Be(35 * RootSamplingMeasurement.MaximumPerEntry);
        Action campaignOverflow = () => SampledLossMeasurement.Merge(campaign, totals,
            35 * SampledLossMeasurement.MaximumCandidatesPerEntry);
        campaignOverflow.Should().Throw<DurableStorageExperimentException>();
        var node = JsonNode.Parse(encoded)!.AsObject();
        node["l"] = JsonSerializer.SerializeToNode(Enumerable.Repeat(16_384, 15));
        node["q"] = JsonSerializer.SerializeToNode(Enumerable.Repeat(4_096, 4));
        node["f"]![0] = false;
        var conservative = Encoding.UTF8.GetByteCount(node.ToJsonString(new() { WriteIndented = false })) + 1;
        conservative.Should().Be(1_023).And.BeLessThanOrEqualTo(1_024);
        var authority = JsonNode.Parse(frame)!.AsObject();
        authority["summary"] = node;
        authority["sampledLoss"]!["counts"] = JsonSerializer.SerializeToNode(Enumerable.Repeat(33_554_432, 15));
        authority["sampledLoss"]!["rootSampling"] = JsonSerializer.SerializeToNode(
            new RootSamplingMeasurement(8_388_608, 8_388_608, 8_388_608, 8_388_608), PrevalidationProtocol.Json);
        var conservativeAuthority = Encoding.UTF8.GetByteCount(authority.ToJsonString(new() { WriteIndented = false })) + 1;
        conservativeAuthority.Should().Be(1_695).And.BeLessThanOrEqualTo(2_048);
        _output.WriteLine($"unifiedSummaryLfBytes={encoded.Length}; independentUpper={conservative}; "
            + $"authorityLfBytes={Encoding.UTF8.GetByteCount(frame) + 1}; authorityIndependentUpper={conservativeAuthority}");
        _output.WriteLine($"protocol={UnifiedActiveProtocol.ProtocolSha256}; map={UnifiedActiveProtocol.ContextMapSha256}");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("shape")]
    [InlineData("negative")]
    [InlineData("overflow")]
    [InlineData("integer-overflow")]
    [InlineData("denominator")]
    [InlineData("quiescent")]
    [InlineData("complete")]
    [InlineData("admissible")]
    [InlineData("old-version")]
    public void UnifiedActiveMalformedWireFailsStructured(string mutation)
    {
        var node = JsonSerializer.SerializeToNode(UnifiedActiveProtocol.WorstCaseSummary(), PrevalidationProtocol.Json)!.AsObject();
        switch (mutation)
        {
            case "missing": node.Remove("q"); break;
            case "null": node["q"] = null; break;
            case "shape": node["q"] = new JsonArray(1, 2, 3); break;
            case "negative": node["q"]![0] = -1; break;
            case "overflow": node["q"]![3] = 4_097; break;
            case "integer-overflow": node["q"]![0] = long.MaxValue; break;
            case "denominator": node["q"]![1] = 4_096; break;
            case "quiescent": node["f"]![0] = false; break;
            case "complete": node["f"]![1] = true; break;
            case "admissible": node["f"]![3] = true; break;
            case "old-version": node["v"] = 2; break;
        }
        Action decode = () => JsonSerializer.Deserialize<MonitoredSweepSummary>(node.ToJsonString(), PrevalidationProtocol.Json);
        decode.Should().Throw<DurableStorageExperimentException>();
    }

    [Theory]
    [InlineData("suite-final-enumeration")]
    [InlineData("post-kill-quiescent-root-inventory")]
    [InlineData("before-ordinary-reopen")]
    [InlineData("owned-cleanup-quiescent")]
    public async Task UnifiedActiveDeclaredExactBoundaryCannotBeRelabeledActive(string boundary)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "mutable");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "leaf"), [1]);
        await using var monitor = UnifiedMonitor(root);
        monitor.BeforeRootPathObservationForComponent = File.Delete;
        var result = await monitor.ObserveBoundaryAsync(boundary, true, CancellationToken.None);
        result.Errors.Should().ContainSingle();
        DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
        result.Summary.SampledLoss!.RootSampling!.HasLoss.Should().BeFalse();
        var forged = UnifiedActiveProtocol.WorstCaseSummary() with { Boundary = boundary };
        Action encode = () => MonitoredSweepSummaryEncoding.EncodeLine(forged);
        encode.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("RootSamplingQuiescentLoss");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task UnifiedActiveLegacyRootFailuresRemainHard(int revision)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "mutable");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "leaf"), [1]);
        await using var monitor = new MonitoredStorageMonitor(
            Attribution(root, root, root, root) with { Roots = [new("workspace", root, true)] },
            EncodingContract(), Path.Combine(_workspace, "legacy.jsonl"),
            sampledLoss: revision == 5, observedUnlinked: revision == 6);
        monitor.SetStage("admission", true);
        monitor.BeforeRootPathObservationForComponent = File.Delete;
        var result = monitor.Sweep();
        result.Errors.Should().ContainSingle().Which.Should().Be("PathObservationFileNotFoundException");
        DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
    }

    [Fact]
    public async Task UnifiedActiveRootAndDescriptorAliasesChargeOneMaximumPositiveLength()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(fixture.Manifest.WorkspaceRoot, "owned");
        File.WriteAllBytes(path, new byte[32]);
        var temporary = Path.Combine(_workspace, "native-extra");
        File.WriteAllBytes(temporary, []);
        using var child = StartObservedOwner(path, true, duplicate: true);
        using var extra = StartObservedOwner(temporary, true);
        child.StandardOutput.ReadLine().Should().Be("ready");
        extra.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        var extraOwner = MonitoredProcessIdentity.Capture(extra, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "unified.jsonl"), includeIdentityEvidence: true,
            unifiedActive: true, activeOwnedRoots: [fixture.Manifest.WorkspaceRoot]);
        monitor.AddProcess(owner);
        monitor.AddProcess(extraOwner);
        monitor.SetStage("finalization", true);
        try
        {
            monitor.BeforeDescriptorOperationForComponent = (_, phase) =>
            {
                if (phase == 1) { File.Delete(path); File.Delete(temporary); }
            };
            var result = monitor.Sweep();
            result.Errors.Should().BeEmpty();
            result.IdentityEvidence.Should().ContainSingle(x => x.SeenInRoot && x.SeenInDescriptor && x.Length == 32);
            result.IdentityEvidence.Should().ContainSingle(x => x.Role == "active-native-temporary" && x.Length == 0);
            result.Summary.SampledLoss!.ObservedUnlinked.Should().Be(new ObservedUnlinkedMeasurement(2, 1, 0));
            result.Summary.WorkspaceBytes.Should().Be(32);
            result.Summary.SampledLoss.RootSampling!.ObservedFiles.Should().BeGreaterThan(0);
        }
        finally
        {
            await monitor.KillOwnedAsync(owner, CancellationToken.None);
            await monitor.KillOwnedAsync(extraOwner, CancellationToken.None);
        }
    }

    [Fact]
    public async Task UnifiedActiveActualBJournalUnlinkIsSampledWithoutChangingItsTransaction()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "mutable");
        Directory.CreateDirectory(root);
        await using var index = DurableAppendFirstQueryIndex.CreateWritable(Path.Combine(root, "index.db"), new());
        using var inserted = new ManualResetEventSlim();
        using var commit = new ManualResetEventSlim();
        var record = new DurableCounterRecord(
            1, "Synthetic.Provider", "counter-0", "Synthetic counter 0", "items", 42, CounterKind.Mean, 1,
            CounterMetadataState.Valid, TimeSpan.TicksPerSecond, CounterMetadataState.Valid, 10,
            "fixture-relative-100ns", "component-test", CoverageGapState.Unknown, CounterResetState.Unknown, EncodedBytes: 512);
        var transaction = Task.Run(() => index.InsertBatch(new RootObservedBatch(record, () =>
        {
            inserted.Set();
            commit.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        })));
        await using var monitor = UnifiedMonitor(root);
        monitor.SetStage("admission", true);
        try
        {
            inserted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            monitor.BeforeRootPathObservationForComponent = path =>
            {
                if (!path.EndsWith("-journal", StringComparison.Ordinal)) return;
                commit.Set();
                transaction.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            };
            var result = monitor.Sweep();
            result.Errors.Should().BeEmpty();
            result.Summary.SampledLoss!.RootSampling!.LostFilePaths.Should().Be(1);
            DescriptorObservationPolicy.Admissible(result.Summary).Should().BeTrue();
            result.Summary.Complete.Should().BeFalse();
            index.CountRows().Should().Be(1);
        }
        finally
        {
            commit.Set();
            await transaction;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnifiedActiveRootFailureAndDescriptorLossRemainIndependent(bool hardRoot)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(fixture.Manifest.WorkspaceRoot, "mutable");
        var start = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exec 9>\"$1\"; printf 'ready\\n'; read -r command; exec 9>&-; printf 'closed\\n'; read -r command");
        start.ArgumentList.Add("controlled-unified-loss");
        start.ArgumentList.Add(path);
        using var child = Process.Start(start)!;
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "unified.jsonl"), unifiedActive: true,
            activeOwnedRoots: [fixture.Manifest.WorkspaceRoot]);
        monitor.AddProcess(owner);
        monitor.SetStage("admission", true);
        monitor.BeforeRootPathObservationForComponent = candidate =>
        {
            if (candidate != path) return;
            if (hardRoot) throw new IOException("controlled hard root");
            File.Delete(path);
        };
        var closed = false;
        monitor.BeforeDescriptorOperationForComponent = (descriptor, phase) =>
        {
            if (closed || phase != 1 || Path.GetFileName(descriptor) != "9") return;
            child.StandardInput.WriteLine("close");
            child.StandardInput.Flush();
            child.StandardOutput.ReadLine().Should().Be("closed");
            closed = true;
        };
        try
        {
            var result = monitor.Sweep();
            closed.Should().BeTrue();
            result.Summary.SampledLoss!.Lost.Should().Be(1);
            result.Summary.SampledLoss.RootSampling!.LostFilePaths.Should().Be(hardRoot ? 0 : 1);
            result.Summary.SampledLoss.FirstLoss.Should().Equal(1, 1, 2, 9);
            result.Summary.FirstDescriptorFailure.Should().BeNull();
            result.Summary.FirstErrorCode.Should().Be(hardRoot ? "RootObservationIOException" : null);
            DescriptorObservationPolicy.Admissible(result.Summary).Should().Be(!hardRoot);
        }
        finally { await monitor.KillOwnedAsync(owner, CancellationToken.None); }
    }
    [Fact]
    public async Task UnifiedActiveHardRootErrorStillObservesDescriptorsAndOwnerLoss()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var path = Path.Combine(fixture.Manifest.WorkspaceRoot, "owned");
        File.WriteAllBytes(path, [1]);
        using var child = StartObservedOwner(path, true);
        child.StandardOutput.ReadLine().Should().Be("ready");
        var owner = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "unified.jsonl"), unifiedActive: true,
            activeOwnedRoots: [fixture.Manifest.WorkspaceRoot]);
        monitor.AddProcess(owner);
        monitor.SetStage("finalization", true);
        monitor.BeforeRootPathObservationForComponent = candidate =>
        {
            if (candidate == path) throw new IOException("controlled hard root failure");
        };
        try
        {
            var live = monitor.Sweep();
            live.Errors.Should().Contain("RootObservationIOException");
            live.Summary.SampledLoss!.Classified.Should().BeGreaterThan(0);
            live.Summary.FirstErrorCode.Should().Be("RootObservationIOException");
            child.Kill();
            await child.WaitForExitAsync();
            var dead = monitor.Sweep();
            dead.Errors.Should().Contain("UnexpectedProcessIdentityLoss");
            DescriptorObservationPolicy.Admissible(dead.Summary).Should().BeFalse();
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
        }
    }

    [Fact]
    public void UnifiedActiveCountersCannotBeDowngradedOrMergedIntoLegacyMeasurements()
    {
        var zero = SampledLossMeasurement.Empty(unifiedActive: true);
        var lost = zero with { RootSampling = new(1, 0, 1, 2) };
        lost.Validate();
        SampledLossMeasurement.Merge(zero, lost).Should().BeEquivalentTo(lost);
        Action v5Merge = () => SampledLossMeasurement.Merge(SampledLossMeasurement.Empty(), lost);
        v5Merge.Should().Throw<DurableStorageExperimentException>();
        Action mismatch = () => SampledLossMeasurement.Merge(SampledLossMeasurement.Empty(true), lost);
        mismatch.Should().Throw<DurableStorageExperimentException>();
        PrevalidationReportValidation.MeasurementsEqual(lost, zero).Should().BeFalse();
        Action v6 = () => DescriptorObservationPolicy.ValidateMeasurement(ObservedAdmissionBinding(), lost);
        v6.Should().Throw<DurableStorageExperimentException>();
        DescriptorObservationPolicy.ValidateMeasurement(UnifiedBinding(), lost);
        Action missing = () => DescriptorObservationPolicy.ValidateMeasurement(UnifiedBinding(), SampledLossMeasurement.Empty(true));
        missing.Should().Throw<DurableStorageExperimentException>();
    }

    [Theory]
    [InlineData("file-link")]
    [InlineData("branch-link")]
    [InlineData("permission")]
    [InlineData("path")]
    public async Task UnifiedActiveLinksPermissionAndPathSafetyAreHard(string failure)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "mutable");
        Directory.CreateDirectory(root);
        var leaf = Path.Combine(root, "leaf");
        File.WriteAllBytes(leaf, [1]);
        var outside = Path.Combine(_workspace, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "foreign"), [2]);
        var branch = Path.Combine(root, "branch");
        Directory.CreateDirectory(branch);
        var attribution = Attribution(root, root, root, root) with { Roots = [new("workspace", root, true)] };
        if (failure == "path") attribution = attribution with { MaximumObservedPathUtf8Bytes = Encoding.UTF8.GetByteCount(root) };
        await using var monitor = new MonitoredStorageMonitor(attribution, EncodingContract(),
            Path.Combine(_workspace, "safety.jsonl"), unifiedActive: true, activeOwnedRoots: [root]);
        monitor.SetStage("admission", true);
        if (failure == "branch-link")
            monitor.BeforeRootDirectoryObservationForComponent = path =>
            {
                if (path != branch) return;
                Directory.Delete(branch);
                Directory.CreateSymbolicLink(branch, outside);
            };
        else
            monitor.BeforeRootPathObservationForComponent = path =>
            {
                if (failure == "file-link")
                {
                    File.Delete(path);
                    File.CreateSymbolicLink(path, Path.Combine(outside, "foreign"));
                }
                if (failure == "permission") throw new UnauthorizedAccessException("controlled EACCES");
            };
        var result = monitor.Sweep();
        result.Errors.Should().NotBeEmpty();
        result.Summary.SampledLoss!.RootSampling!.HasLoss.Should().BeFalse();
        DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnifiedActiveOnlyDeclaredMutableEvidenceStreamsCanLosePaths(bool declared)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "outputs");
        Directory.CreateDirectory(root);
        var leaf = Path.Combine(root, declared ? "worker-stdout.jsonl" : "worker-descriptor.json");
        File.WriteAllBytes(leaf, [1]);
        await using var monitor = new MonitoredStorageMonitor(
            Attribution(root, root, root, root) with { Roots = [new("outputs", root, true)] },
            EncodingContract(), Path.Combine(_workspace, "streams.jsonl"),
            unifiedActive: true, activeOwnedFiles: UnifiedActiveProtocol.MutableWorkerStreams(root));
        monitor.SetStage("worker-preparation", true);
        monitor.BeforeRootPathObservationForComponent = File.Delete;
        var result = monitor.Sweep();
        result.Summary.SampledLoss!.RootSampling!.LostFilePaths.Should().Be(declared ? 1 : 0);
        DescriptorObservationPolicy.Admissible(result.Summary).Should().Be(declared);
    }

    [Fact]
    public void UnifiedActiveCannotRegisterForeignMutableRoots()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "root");
        Directory.CreateDirectory(root);
        Action create = () => _ = new MonitoredStorageMonitor(
            Attribution(root, root, root, root) with { Roots = [new("workspace", root, true)] },
            EncodingContract(), Path.Combine(_workspace, "foreign.jsonl"),
            unifiedActive: true, activeOwnedRoots: [_workspace]);
        create.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("RootSamplingForeignRoot");
    }

    private MonitoredStorageMonitor UnifiedMonitor(string root, bool identities = false)
        => new(Attribution(root, root, root, root) with { Roots = [new("workspace", root, true)] },
            EncodingContract(), Path.Combine(_workspace, "unified-monitor.jsonl"),
            includeIdentityEvidence: identities, unifiedActive: true, activeOwnedRoots: [root]);

    private static SampledLossBinding UnifiedBinding()
        => ObservedAdmissionBinding() with
        {
            Policy = UnifiedActiveProtocol.Policy,
            ProtocolSha256 = UnifiedActiveProtocol.ProtocolSha256,
            ContextMapSha256 = UnifiedActiveProtocol.ContextMapSha256,
        };
}
