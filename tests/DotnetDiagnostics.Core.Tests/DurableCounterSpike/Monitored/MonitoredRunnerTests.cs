using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed partial class MonitoredRunnerTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITestOutputHelper _output;

    private readonly string _workspace = Path.Combine(
        AppContext.BaseDirectory,
        "durable-monitored-runner",
        Guid.NewGuid().ToString("N"));

    public MonitoredRunnerTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public async Task SourceRecoveryStderrSharesExecutionBudgetAndReportsTheRightStream()
    {
        var budget = new BoundedOutputBudget(8);
        using var source = new MemoryStream(new byte[5]);
        using var recovery = new MemoryStream(new byte[3]);
        await MonitoredCampaignRunner.CaptureRawAsync(
            source, Path.Combine(_workspace, "source-stderr"), 8, budget, CancellationToken.None);
        await MonitoredCampaignRunner.CaptureRawAsync(
            recovery, Path.Combine(_workspace, "recovery-stderr"), 8, budget, CancellationToken.None);
        budget.UsedBytes.Should().Be(8);
        using var excess = new MemoryStream(new byte[1]);
        Func<Task> capture = () => MonitoredCampaignRunner.CaptureRawAsync(
            excess, Path.Combine(_workspace, "excess-stderr"), 8, budget, CancellationToken.None);
        (await capture.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("WorkerStderrLimit");
        budget.UsedBytes.Should().Be(8);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var directory in Directory.EnumerateDirectories(
                             _workspace,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(
                        directory,
                        UnixFileMode.UserRead
                            | UnixFileMode.UserWrite
                            | UnixFileMode.UserExecute);
                }
                foreach (var file in Directory.EnumerateFiles(
                             _workspace,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(
                        file,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Fact]
    public void ExecutionPlanExpandsTheExactFrozen35Entries()
    {
        var executions = MonitoredExecutionPlanner.Expand();

        executions.Should().HaveCount(35);
        executions.Select(static item => item.Ordinal).Should().Equal(Enumerable.Range(1, 35));
        executions.Take(2).Select(static item => (item.CaseId, item.Candidate))
            .Should().Equal(("P0", "shared"), ("P1", "E"));
        executions.Skip(2).Take(8).Select(static item => (item.CaseId, item.Candidate))
            .Should().Equal(
                ("Q1", "A"), ("Q1", "B"),
                ("Q2", "B"), ("Q2", "A"),
                ("N1", "A"), ("N1", "B"),
                ("B1", "B"), ("B1", "A"));
        executions.Skip(26).Select(static item => (item.CaseId, item.Candidate))
            .Should().Equal(
                ("L1", "E"), ("L1", "A"), ("L1", "B"),
                ("L2", "B"), ("L2", "E"), ("L2", "A"),
                ("L3", "A"), ("L3", "B"), ("L3", "E"));
        executions.Should().OnlyContain(static item =>
            item.MaximumAttempts == 1 && item.MaximumSeconds == 120);
    }

    [Fact]
    public void SuccessorValidatorRejectsMonitoringOrInheritedProtocolDrift()
    {
        var repositoryRoot = FindRepositoryRoot();
        var published = MonitoredSuccessorProtocolValidator.Validate(
            repositoryRoot,
            MonitoredProtocolVersions.SuccessorProtocolPath);
        published.ProtocolSha256.Should().Be(MonitoredProtocolVersions.SuccessorProtocolSha256);

        var validPath = WriteSuccessorProtocol(repositoryRoot, mutate: null);
        Action valid = () => MonitoredSuccessorProtocolValidator.ValidateShape(
            repositoryRoot,
            Path.GetRelativePath(repositoryRoot, validPath).Replace(Path.DirectorySeparatorChar, '/'));
        valid.Should().NotThrow();

        var monitoringDrift = WriteSuccessorProtocol(repositoryRoot, root =>
        {
            root["monitoring"]!["pollTargetMs"] = 101;
        });
        Action changedMonitor = () => MonitoredSuccessorProtocolValidator.ValidateShape(
            repositoryRoot,
            Path.GetRelativePath(repositoryRoot, monitoringDrift).Replace(Path.DirectorySeparatorChar, '/'));
        changedMonitor.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("MonitoringContractDrift");

        var caseDrift = WriteSuccessorProtocol(repositoryRoot, root =>
        {
            root["cases"]![0]!["records"] = 1_023;
        });
        Action changedCase = () => MonitoredSuccessorProtocolValidator.ValidateShape(
            repositoryRoot,
            Path.GetRelativePath(repositoryRoot, caseDrift).Replace(Path.DirectorySeparatorChar, '/'));
        changedCase.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("InheritedProtocolDrift");
    }

    [LinuxOnlyFact]
    public void ResolvedManifestRequiresArtifactEvidenceAndParentAuthorizationNotReadyBoolean()
    {
        var fixture = WriteResolvedManifest(authorizationApproved: true);

        var validated = MonitoredRunManifestValidator.Validate(
            fixture.RepositoryRoot,
            fixture.ManifestPath,
            requireAuthorization: true);

        validated.Summary.Ready.Should().BeTrue();
        validated.Summary.PlannedExecutions.Should().Be(35);
        validated.Authorization.SingleCampaignOnly.Should().BeTrue();

        var rejected = WriteResolvedManifest(authorizationApproved: false);
        Action validateRejected = () => MonitoredRunManifestValidator.Validate(
            rejected.RepositoryRoot,
            rejected.ManifestPath,
            requireAuthorization: true);
        validateRejected.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("ExecutionAuthorizationInvalid");
    }

    [LinuxOnlyFact]
    public void ResolvedManifestRejectsMutableAuthorizationAndComponentRunnerMismatch()
    {
        var mutable = WriteResolvedManifest(authorizationApproved: true);
        var mutableManifest = JsonSerializer.Deserialize<MonitoredRunManifest>(
            File.ReadAllBytes(mutable.ManifestPath),
            JsonOptions)
            ?? throw new InvalidOperationException("Could not deserialize the test manifest.");
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Monitored artifacts are Linux-only.");
        }
        File.SetUnixFileMode(
            mutableManifest.AuthorizationReceipt,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Action validateMutable = () => MonitoredRunManifestValidator.Validate(
            mutable.RepositoryRoot,
            mutable.ManifestPath,
            requireAuthorization: true);
        validateMutable.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("ExecutionAuthorizationMutable");

        var mismatched = WriteResolvedManifest(authorizationApproved: true);
        var manifestNode = JsonNode.Parse(File.ReadAllText(mismatched.ManifestPath))!.AsObject();
        manifestNode["sourceCommits"]!["runnerCommit"] = CommitIdentity("different-runner");
        File.WriteAllText(
            mismatched.ManifestPath,
            manifestNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Action validateMismatch = () => MonitoredRunManifestValidator.Validate(
            mismatched.RepositoryRoot,
            mismatched.ManifestPath,
            requireAuthorization: true);
        validateMismatch.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("MonitorComponentEvidenceInsufficient");
    }

    [LinuxOnlyFact]
    public void CgroupFactsResolveMembershipDirectoryAndAllMountedAncestors()
    {
        var cgroupMount = Path.Combine(_workspace, "cgroup2");
        var parent = Path.Combine(cgroupMount, "parent");
        var membership = Path.Combine(parent, "init.scope");
        Directory.CreateDirectory(membership);
        File.WriteAllText(Path.Combine(membership, "memory.max"), "max\n");
        File.WriteAllText(Path.Combine(membership, "memory.current"), "1024\n");
        File.WriteAllText(Path.Combine(membership, "cpu.max"), "max 100000\n");
        File.WriteAllText(Path.Combine(membership, "cpuset.cpus.effective"), "0-3\n");
        File.WriteAllText(Path.Combine(parent, "memory.max"), "2048\n");
        File.WriteAllText(Path.Combine(parent, "memory.current"), "512\n");
        File.WriteAllText(Path.Combine(parent, "cpu.max"), "200000 100000\n");
        File.WriteAllText(Path.Combine(parent, "cpuset.cpus.effective"), "0-7\n");
        var procCgroup = Path.Combine(_workspace, "self.cgroup");
        var mountInfo = Path.Combine(_workspace, "self.mountinfo");
        File.WriteAllText(procCgroup, "0::/parent/init.scope\n");
        File.WriteAllText(
            mountInfo,
            $"29 23 0:26 / {cgroupMount} rw,nosuid,nodev,noexec,relatime - cgroup2 cgroup rw\n");

        var facts = MonitoredHostFactsReader.ReadCgroupFacts(procCgroup, mountInfo);

        facts.Version.Should().Be("v2");
        facts.MembershipPath.Should().Be("/parent/init.scope");
        facts.MountRoot.Should().Be("/");
        facts.MountPoint.Should().Be(cgroupMount);
        facts.MembershipAndAncestors.Select(static level => level.HierarchyPath)
            .Should().Equal("/parent/init.scope", "/parent", "/");
        facts.MembershipAndAncestors[0].MemoryMax.Should().Be("max");
        facts.MembershipAndAncestors[1].CpuMax.Should().Be("200000 100000");
        facts.MembershipAndAncestors[2].MemoryMax.Should().BeNull();
        facts.EffectiveAvailableMemoryBytes.Should().Be(1_536);
    }

    [LinuxOnlyFact]
    public async Task MonitorAccountsRootAndOpenUnlinkedFileAndRejectsIdentityReuse()
    {
        var root = Path.Combine(_workspace, "monitor");
        var history = Path.Combine(root, "history");
        var workspace = Path.Combine(root, "workspace");
        var outputs = Path.Combine(root, "outputs");
        Directory.CreateDirectory(history);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outputs);
        await File.WriteAllBytesAsync(Path.Combine(workspace, "root.bin"), new byte[128]);
        var unlinkedPath = Path.Combine(_workspace, "unlinked.bin");
        await using var unlinked = new FileStream(
            unlinkedPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        await unlinked.WriteAsync(new byte[256]);
        await unlinked.FlushAsync();
        File.Delete(unlinkedPath);

        var attribution = Attribution(root, history, workspace, outputs);
        var encoding = EncodingContract();
        await using var monitor = new MonitoredStorageMonitor(
            attribution,
            encoding,
            Path.Combine(outputs, "monitor.jsonl"));
        using var selfProcess = System.Diagnostics.Process.GetCurrentProcess();
        var self = MonitoredProcessIdentity.Capture(selfProcess, MonitoredProcessRole.Harness);
        monitor.AddProcess(self);

        var sweep = await monitor.ObserveBoundaryAsync(
            "component-observation",
            activeStorageStage: true,
            CancellationToken.None);

        sweep.Summary.WorkspaceBytes.Should().BeGreaterThanOrEqualTo(128);
        sweep.Summary.OpenUnlinkedBytes.Should().BeGreaterThanOrEqualTo(256);
        sweep.Summary.IdentityCount.Should().BeLessThanOrEqualTo(4_096);
        (JsonSerializer.SerializeToUtf8Bytes(sweep.Summary).Length + 1)
            .Should().BeLessThanOrEqualTo(1_024);
        Action reused = () => monitor.AddProcess(self with
        {
            LinuxStartTimeTicks = self.LinuxStartTimeTicks + 1,
        });
        reused.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("ProcessIdentityAmbiguous");
    }

    [LinuxOnlyFact]
    public async Task ReadOnlyDescriptorOutsideRootsCannotBeClassifiedByDirectoryName()
    {
        var root = Path.Combine(_workspace, "containment-monitor");
        var history = Path.Combine(root, "history");
        var workspace = Path.Combine(root, "workspace");
        var outputs = Path.Combine(root, "outputs");
        Directory.CreateDirectory(history);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outputs);
        var outsideDirectory = Path.Combine(_workspace, "outside", "package");
        Directory.CreateDirectory(outsideDirectory);
        var outsidePath = Path.Combine(outsideDirectory, "unowned.bin");
        await File.WriteAllBytesAsync(outsidePath, new byte[64]);
        await using var outside = File.OpenRead(outsidePath);

        await using var monitor = new MonitoredStorageMonitor(
            Attribution(
                root,
                history,
                workspace,
                outputs,
                includeTestHostDependencies: false),
            EncodingContract(),
            Path.Combine(outputs, "monitor.jsonl"));
        using var selfProcess = Process.GetCurrentProcess();
        monitor.AddProcess(MonitoredProcessIdentity.Capture(
            selfProcess,
            MonitoredProcessRole.Harness));

        var sweep = await monitor.ObserveBoundaryAsync(
            "out-of-root-readonly",
            activeStorageStage: true,
            CancellationToken.None);

        sweep.Errors.Should().Contain("UnclassifiedReadOnlyDescriptor");
        sweep.Summary.PackageBytes.Should().Be(0);
        sweep.Summary.Complete.Should().BeFalse();
    }

    [LinuxOnlyFact]
    public async Task MissingDeclaredRootMakesObservationIncomplete()
    {
        var root = Path.Combine(_workspace, "missing-root-monitor");
        var history = Path.Combine(root, "history");
        var workspace = Path.Combine(root, "workspace");
        var outputs = Path.Combine(root, "outputs");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outputs);
        await using var monitor = new MonitoredStorageMonitor(
            Attribution(root, history, workspace, outputs),
            EncodingContract(),
            Path.Combine(outputs, "monitor.jsonl"));

        var sweep = await monitor.ObserveBoundaryAsync(
            "missing-declared-root",
            activeStorageStage: false,
            CancellationToken.None);

        sweep.Errors.Should().Contain("DeclaredRootMissing");
        sweep.Summary.Complete.Should().BeFalse();
    }

    [Fact]
    public void DescriptorCoherenceRejectsTargetFlagsAndIdentityMismatch()
    {
        var first = new PinnedDescriptorSnapshot(
            "/tmp/pinned",
            2,
            new ApparentFileIdentity("linux:one"),
            new DurableStorageNativeObservation(
                new ApparentFileIdentity("linux:one"),
                128,
                1));
        Action changedTarget = () => LinuxProcessDescriptorObserver.ValidateCoherent(
            first,
            first with { Target = "/tmp/reused" });
        Action changedFlags = () => LinuxProcessDescriptorObserver.ValidateCoherent(
            first,
            first with { Flags = 0 });
        Action changedIdentity = () => LinuxProcessDescriptorObserver.ValidateCoherent(
            first,
            first with
            {
                Identity = new ApparentFileIdentity("linux:two"),
            });

        changedTarget.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("DescriptorIdentityChangedDuringObservation");
        changedFlags.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("DescriptorIdentityChangedDuringObservation");
        changedIdentity.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("DescriptorIdentityChangedDuringObservation");
    }

    [LinuxOnlyFact]
    public async Task PeriodicGapMetricExcludesLaterBoundaryOnlyDelay()
    {
        var root = Path.Combine(_workspace, "periodic-gap-monitor");
        Directory.CreateDirectory(root);
        var monitorPath = Path.Combine(root, "monitor.jsonl");
        await using var monitor = new MonitoredStorageMonitor(
            new MonitoredAttributionMap(
                MonitoredProtocolVersions.AttributionSchema,
                [new("workspace", root, true)],
                ["package", "package-staging"],
                ["recovery", "recovery-staging"],
                [AppContext.BaseDirectory],
                ["/dev/null", "/dev/urandom"],
                [],
                4_096,
                4_096,
                32),
            EncodingContract(),
            monitorPath);
        monitor.SetStage("periodic-active", activeStorageStage: true);
        _ = monitor.StartAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(260));
        await monitor.StopAsync();
        var periodicGap = monitor.MaximumPeriodicGapMilliseconds;
        await Task.Delay(TimeSpan.FromMilliseconds(1_050));
        await monitor.ObserveBoundaryAsync(
            "post-periodic-boundary",
            activeStorageStage: false,
            CancellationToken.None);

        monitor.PeriodicSummaryRecords.Should().BeGreaterThanOrEqualTo(2);
        periodicGap.Should().BeLessThan(1_000);
        monitor.MaximumPeriodicGapMilliseconds.Should().Be(periodicGap);
        monitor.MaximumObservedGapMilliseconds.Should().BeGreaterThan(1_000);
    }

    [Theory]
    [InlineData("post-monitor-abort-quiescent-root-inventory", true)]
    [InlineData("", false)]
    [InlineData("+", false)]
    [InlineData("<", false)]
    [InlineData("'", false)]
    [InlineData("\u00e9", false)]
    [InlineData("\n", false)]
    public void SummaryTokensCannotExpandThroughJsonEscaping(string value, bool accepted)
    {
        MonitoredSweepSummaryEncoding.IsBoundedToken(value, 64).Should().Be(accepted);
        MonitoredSweepSummaryEncoding.IsBoundedToken(new string('a', 65), 64)
            .Should().BeFalse();
    }

    [Fact]
    public void CompactWorstCaseSummaryHasPinnedMappingAndRealHeadroom()
    {
        var line = MonitoredSweepSummaryEncoding.EncodeLine(
            MonitoredSweepSummaryEncoding.CreateWorstCaseFixture());
        var json = JsonDocument.Parse(line.AsMemory(0, line.Length - 1));

        line.Length.Should().BeLessThanOrEqualTo(960);
        MonitoredSweepSummaryEncoding.FieldMapSha256.Should().HaveLength(64);
        json.RootElement.TryGetProperty("observedSweepBytes", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("maximumObservedSweepBytes", out _).Should().BeTrue();
        json.RootElement.TryGetProperty("diagnosticPeakRssBytes", out _).Should().BeFalse();
    }

    [LinuxOnlyFact]
    public async Task ComponentEvidenceUsesOwnedChildKillHandoffAndEnforcedGeometry()
    {
        var output = Path.Combine(_workspace, "component-evidence.json");
        var attributionPath = Path.Combine(_workspace, "component-attribution.json");
        var root = Path.Combine(_workspace, "component-attribution-root");
        Directory.CreateDirectory(root);
        MonitoredFile.WriteNewJson(
            attributionPath,
            Attribution(
                root,
                Path.Combine(root, "history"),
                Path.Combine(root, "workspace"),
                Path.Combine(root, "outputs")));

        var evidence = await MonitoredComponentEvidenceGenerator.GenerateAsync(
            output,
            CommitIdentity("runner"),
            CommitIdentity("monitor"),
            attributionPath,
            CancellationToken.None);

        evidence.PositiveMonitoringComplete.Should().BeTrue();
        evidence.ManagedProcessObserved.Should().BeTrue();
        evidence.RuntimeMemoryClassificationObserved.Should().BeTrue();
        evidence.RuntimeMemoryExcludedBytes.Should().BeGreaterThan(0);
        evidence.RootFileObserved.Should().BeTrue();
        evidence.WritableDescriptorOutsideRootRejected.Should().BeTrue();
        evidence.OpenUnlinkedDescriptorRejected.Should().BeTrue();
        evidence.ProcessIdentityReuseRejected.Should().BeTrue();
        evidence.KillHandoffReleasedObserverReferences.Should().BeTrue();
        evidence.SourceRecoverySharedBudgetExhaustionObserved.Should().BeTrue();
        evidence.SourceRecoverySharedCancellationObserved.Should().BeTrue();
        evidence.SourceRecoveryObservedSummaryRecords.Should().BeGreaterThanOrEqualTo(4);
        evidence.SourceRecoveryObservedSummaryBytes.Should().BeGreaterThan(0);
        evidence.SourceRecoveryObservedControlRecords.Should().Be(2);
        evidence.SourceRecoveryObservedControlBytes.Should().BeGreaterThan(0);
        evidence.OutputLimitEnforced.Should().BeTrue();
        evidence.IdentityLimitEnforced.Should().BeTrue();
        evidence.DerivedMaximumSimultaneousIdentitiesEnforced.Should()
            .Be(MonitoredRunnerGeometry.MaximumSimultaneousIdentities);
        evidence.DerivedMaximumRootedIdentities.Should()
            .Be(MonitoredRunnerGeometry.MaximumRootedIdentities);
        evidence.MaximumDescriptorOnlyIdentitiesEnforced.Should()
            .Be(MonitoredRunnerGeometry.MaximumOwnedWritableDescriptorOnlyFiles);
        evidence.DescriptorOnlyCampaignFeasibilityEstablished.Should().BeFalse();
        evidence.RepresentativeMaximumRootedIdentitiesObserved.Should().BeGreaterThan(0);
        evidence.RepresentativeMaximumDescriptorOnlyIdentitiesObserved.Should()
            .BeLessThanOrEqualTo(MonitoredRunnerGeometry.MaximumOwnedWritableDescriptorOnlyFiles);
        evidence.DerivedMaximumSummaryRecordsRequired.Should()
            .Be(MonitoredRunnerGeometry.MaximumRequiredSummaryRecords);
        evidence.MaximumSummaryRecordsEncoded.Should().Be(2_048);
        evidence.DerivedMaximumCombinedSummaryControlBytes.Should()
            .BeLessThanOrEqualTo(8_388_608);
        evidence.MaximumSummaryUtf8BytesObserved.Should().BeLessThan(1_024);
        evidence.WorstCaseSummaryUtf8Bytes.Should().BeLessThanOrEqualTo(1_024);
        evidence.WorstCaseSummaryUtf8Bytes.Should()
            .BeGreaterThanOrEqualTo(evidence.MaximumSummaryUtf8BytesObserved);
        evidence.PeriodicSummariesObserved.Should().BeGreaterThanOrEqualTo(2);
        evidence.MaximumPeriodicGapMillisecondsObserved.Should().BeLessThanOrEqualTo(1_000);
        evidence.RepresentativeCandidatePackageInventoriesObserved.Should().BeTrue(
            "both package inventories must close within the four-file geometry; observed final max {0}, transient max {1}, SQLite WAL/SHM {2}",
            evidence.RepresentativeMaximumFinalPackageFilesObserved,
            evidence.RepresentativeMaximumTransientPackageFilesObserved,
            evidence.RepresentativeSqliteWalShmObserved);
        evidence.RepresentativeMaximumFinalPackageFilesObserved.Should().Be(4);
        evidence.RepresentativeSqliteWalShmObserved.Should().BeTrue();
        evidence.RawEvidenceFiles.Should().BeGreaterThan(10);
        MonitoredFile.IsReadOnlyTree(evidence.RawEvidenceRoot, 4_096, 4_096)
            .Should().BeTrue();
        MonitoredFile.HashTreeInventory(evidence.RawEvidenceRoot, 4_096, 4_096).Sha256
            .Should().Be(evidence.EvidenceSha256);
        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public async Task BoundedEvidenceWriterRejectsRecordCountAndByteOverflow()
    {
        var path = Path.Combine(_workspace, "bounded.jsonl");
        await using var writer = new BoundedJsonLineWriter(
            path,
            maximumRecordBytes: 64,
            maximumRecords: 1,
            maximumBytes: 65);
        await writer.WriteAsync(new { value = "one" }, CancellationToken.None);

        Func<Task> second = async () =>
            await writer.WriteAsync(new { value = "two" }, CancellationToken.None);

        (await second.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("MonitorSummaryCountLimit");
    }

    [Fact]
    public async Task BoundedEvidenceWriterCountsNewlineInsideRecordAndStreamLimits()
    {
        var emptyBytes = JsonSerializer.SerializeToUtf8Bytes(new { value = string.Empty }).Length;
        var exactLine = new { value = new string('x', 63 - emptyBytes) };
        var acceptedPath = Path.Combine(_workspace, "exact-line.jsonl");
        await using (var accepted = new BoundedJsonLineWriter(
            acceptedPath,
            maximumRecordBytes: 64,
            maximumRecords: 1,
            maximumBytes: 64))
        {
            await accepted.WriteAsync(exactLine, CancellationToken.None);
            accepted.Bytes.Should().Be(64);
            accepted.MaximumObservedRecordBytes.Should().Be(64);
        }

        var rejectedPath = Path.Combine(_workspace, "oversized-line.jsonl");
        await using var rejected = new BoundedJsonLineWriter(
            rejectedPath,
            maximumRecordBytes: 64,
            maximumRecords: 1,
            maximumBytes: 64);
        var oversized = new { value = new string('x', 64 - emptyBytes) };
        Func<Task> write = async () =>
            await rejected.WriteAsync(oversized, CancellationToken.None);
        (await write.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("MonitorSummaryRecordLimit");
    }

    [Fact]
    public async Task MonitorSummariesAndWorkerStdoutShareOneOutputBudget()
    {
        var path = Path.Combine(_workspace, "combined-budget.jsonl");
        var budget = new BoundedOutputBudget(32);
        budget.ConsumeControl(20);
        await using var writer = new BoundedJsonLineWriter(
            path,
            maximumRecordBytes: 64,
            maximumRecords: 2,
            maximumBytes: 64,
            budget);

        Func<Task> write = async () =>
            await writer.WriteAsync(new { value = "one" }, CancellationToken.None);

        (await write.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("CombinedOutputLimit");
    }

    [Fact]
    public async Task SourceAndRecoveryMonitorsShareTheSummaryRecordLimit()
    {
        var budget = new BoundedOutputBudget(1_024, maximumSummaryRecords: 1);
        await using var source = new BoundedJsonLineWriter(
            Path.Combine(_workspace, "source-monitor.jsonl"),
            maximumRecordBytes: 64,
            maximumRecords: 2,
            maximumBytes: 128,
            budget);
        await using var recovery = new BoundedJsonLineWriter(
            Path.Combine(_workspace, "recovery-monitor.jsonl"),
            maximumRecordBytes: 64,
            maximumRecords: 2,
            maximumBytes: 128,
            budget);
        await source.WriteAsync(new { value = "source" }, CancellationToken.None);

        Func<Task> writeRecovery = async () =>
            await recovery.WriteAsync(new { value = "recovery" }, CancellationToken.None);

        (await writeRecovery.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("MonitorSummaryCountLimit");
    }

    [LinuxOnlyFact]
    public async Task MonitorDisposalReleasesWriterWhenPeriodicObservationFaults()
    {
        var root = Path.Combine(_workspace, "dispose-monitor");
        Directory.CreateDirectory(root);
        var writer = new FaultingSummaryWriter();
        var monitor = new MonitoredStorageMonitor(
            Attribution(
                root,
                Path.Combine(root, "history"),
                Path.Combine(root, "workspace"),
                Path.Combine(root, "outputs")),
            writer);
        _ = monitor.StartAsync();
        await writer.WriteAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Func<Task> dispose = async () => await monitor.DisposeAsync();

        (await dispose.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("component writer failure");
        writer.Disposed.Should().BeTrue();
    }

    [LinuxOnlyFact]
    public async Task ExactProcessTerminatorRefusesStaleStartTime()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec sleep 30");
        using var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start owned child.");
        var identity = MonitoredProcessIdentity.Capture(child, MonitoredProcessRole.Diagnostic);
        try
        {
            Action staleKill = () => OwnedProcessTerminator.KillExact(
                child,
                identity with { LinuxStartTimeTicks = identity.LinuxStartTimeTicks + 1 });

            staleKill.Should().Throw<DurableStorageExperimentException>()
                .Which.Code.Should().Be("OwnedProcessIdentityMismatch");
            child.HasExited.Should().BeFalse();
        }
        finally
        {
            OwnedProcessTerminator.KillExact(child, identity);
            await child.WaitForExitAsync();
        }
    }

    [Fact]
    public void WorkerBoundaryRequiresExactReleaseToken()
    {
        Action wrong = () => MonitoredWorkerControl.ValidateBoundaryRelease(
            "after-drain",
            "release:before-drain");

        wrong.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("BoundaryReleaseMismatch");
        MonitoredWorkerControl.ValidateBoundaryRelease(
            "after-drain",
            "release:after-drain");
    }

    [LinuxOnlyFact]
    public async Task HarnessLifecycleDrivesKillRecoveryHandshakeAndResultHandoff()
    {
        var validated = PrepareComponentExecution();
        var execution = validated.Manifest.Plan.Executions.Single(item =>
            item.CaseId == "F3" && item.Candidate == "A");
        var launcher = new ScriptedWorkerLauncher(ScriptedWorkerBehavior.SuccessfulRecovery);

        var outcome = await MonitoredCampaignRunner.RunExecutionForComponentAsync(
            validated,
            execution,
            launcher,
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        outcome.Outcome.Should().Be(
            "pass",
            "failure code {0}, monitor alarm {1}, monitoring complete {2}, worker outcome {3}",
            outcome.FailureCode,
            outcome.MonitoringAlarm,
            outcome.MonitoringComplete,
            outcome.Worker?.Outcome);
        outcome.MonitoringComplete.Should().BeTrue();
        outcome.Worker.Should().NotBeNull();
        outcome.Worker!.RecoveryInvariantSatisfied.Should().BeTrue();
        outcome.Worker.RecoveredRecords.Should().Be(128);
        launcher.Descriptors.Select(static item => item.Mode)
            .Should().Equal(MonitoredWorkerMode.Execute, MonitoredWorkerMode.Recover);
        launcher.Descriptors[1].ConfirmedAcknowledgements.Should().Equal(
            Enumerable.Range(1, 64).Select(static value => (long)value));
        launcher.Descriptors[1].KnownOfferedSequences.Should().HaveCount(128);
        launcher.Identities.Should().OnlyContain(static identity =>
            !LinuxProcessIdentity.Matches(identity));
        outcome.MonitoringEvidenceFiles.Should().HaveCount(2);
        var sourceMonitor = Path.Combine(
            validated.CampaignRoot,
            outcome.MonitoringEvidenceFiles[0].Replace('/', Path.DirectorySeparatorChar));
        var sourceEvidence = File.ReadAllText(sourceMonitor);
        sourceEvidence.Should().Contain("pre-kill-AfterCommitBeforeAcknowledgement");
        sourceEvidence.Should().Contain("post-kill-quiescent-root-inventory");
    }

    [LinuxOnlyFact]
    public async Task HarnessLifecycleCancelsRecoveryAndCleansBothOwnedWorkers()
    {
        var validated = PrepareComponentExecution();
        var execution = validated.Manifest.Plan.Executions.Single(item =>
            item.CaseId == "F3" && item.Candidate == "A");
        using var cancellation = new CancellationTokenSource();
        var launcher = new ScriptedWorkerLauncher(
            ScriptedWorkerBehavior.HangRecovery,
            cancellation.Cancel);

        Func<Task> run = async () => await MonitoredCampaignRunner.RunExecutionForComponentAsync(
            validated,
            execution,
            launcher,
            TimeSpan.FromSeconds(5),
            cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        launcher.Descriptors.Select(static item => item.Mode)
            .Should().Equal(MonitoredWorkerMode.Execute, MonitoredWorkerMode.Recover);
        launcher.Identities.Should().OnlyContain(static identity =>
            !LinuxProcessIdentity.Matches(identity));
    }

    [LinuxOnlyFact]
    public async Task HarnessLifecycleDoesNotPromoteWrongReleaseToSuccess()
    {
        var validated = PrepareComponentExecution();
        var execution = validated.Manifest.Plan.Executions.Single(item =>
            item.CaseId == "F3" && item.Candidate == "A");
        var launcher = new ScriptedWorkerLauncher(ScriptedWorkerBehavior.WrongRecoveryRelease);

        var outcome = await MonitoredCampaignRunner.RunExecutionForComponentAsync(
            validated,
            execution,
            launcher,
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        outcome.Outcome.Should().NotBe("pass");
        outcome.Worker.Should().BeNull();
        launcher.Identities.Should().OnlyContain(static identity =>
            !LinuxProcessIdentity.Matches(identity));
    }

    [LinuxOnlyFact]
    public async Task HarnessLifecycleRejectsUndeclaredBarrierAndUnexpectedSuccessfulExit()
    {
        var barrierValidated = PrepareComponentExecution();
        var q1 = barrierValidated.Manifest.Plan.Executions.Single(item =>
            item.CaseId == "Q1" && item.Candidate == "A");
        var barrierLauncher = new ScriptedWorkerLauncher(
            ScriptedWorkerBehavior.UndeclaredBarrier);

        Func<Task> barrier = async () =>
            await MonitoredCampaignRunner.RunExecutionForComponentAsync(
                barrierValidated,
                q1,
                barrierLauncher,
                TimeSpan.FromSeconds(3),
                CancellationToken.None);

        (await barrier.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("UnexpectedKillBarrier");
        barrierLauncher.Identities.Should().OnlyContain(static identity =>
            !LinuxProcessIdentity.Matches(identity));

        var exitValidated = PrepareComponentExecution();
        var exitExecution = exitValidated.Manifest.Plan.Executions.Single(item =>
            item.CaseId == "Q1" && item.Candidate == "A");
        var exitLauncher = new ScriptedWorkerLauncher(
            ScriptedWorkerBehavior.UnexpectedSuccessfulExit);

        Func<Task> exit = async () =>
            await MonitoredCampaignRunner.RunExecutionForComponentAsync(
                exitValidated,
                exitExecution,
                exitLauncher,
                TimeSpan.FromSeconds(3),
                CancellationToken.None);

        (await exit.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("MissingWorkerResult");
        exitLauncher.Identities.Should().OnlyContain(static identity =>
            !LinuxProcessIdentity.Matches(identity));
    }

    [Fact]
    public void RecoveryRequiresAcknowledgementsCompleteBatchesAndBarrierSpecificRows()
    {
        var offered = Enumerable.Range(1, 128).Select(static value => (long)value).ToArray();
        var acknowledged = offered[..64];

        MonitoredRecoveryInvariant.Validate("F2", acknowledged, offered, acknowledged)
            .Should().BeTrue();
        MonitoredRecoveryInvariant.Validate("F3", acknowledged, offered, offered)
            .Should().BeTrue();
        MonitoredRecoveryInvariant.Validate("F3", acknowledged, offered, acknowledged)
            .Should().BeFalse();
        MonitoredRecoveryInvariant.Validate("F4", [], offered, offered)
            .Should().BeTrue();
        MonitoredRecoveryInvariant.Validate("F2", acknowledged, offered, offered)
            .Should().BeFalse();
        MonitoredRecoveryInvariant.Validate("F3", acknowledged, offered, offered[..65])
            .Should().BeFalse();
        MonitoredRecoveryInvariant.Validate("F4", [], offered, [])
            .Should().BeFalse();
    }

    [Fact]
    public void FinalizationAndReopenUseIndependentDeadlines()
    {
        MonitoredDeadlineRules.WithinFinalizationAndReopenBudgets(
                TimeSpan.FromSeconds(9.9),
                TimeSpan.FromSeconds(1.9))
            .Should().BeTrue();
        MonitoredDeadlineRules.WithinFinalizationAndReopenBudgets(
                TimeSpan.FromSeconds(10.1),
                TimeSpan.FromSeconds(0.1))
            .Should().BeFalse();
        MonitoredDeadlineRules.WithinFinalizationAndReopenBudgets(
                TimeSpan.FromSeconds(0.1),
                TimeSpan.FromSeconds(2.1))
            .Should().BeFalse();
    }

    [Fact]
    public async Task ReopenMeasurementPlacesBoundarySweepsOutsideTheScoredOperation()
    {
        var stages = new List<string>();

        var elapsed = await MonitoredReopenMeasurement.MeasureAsync(
            () =>
            {
                stages.Add("pre-sweep-complete");
                return Task.CompletedTask;
            },
            () =>
            {
                stages.Add("seal-members-validated");
                stages.Add("reader-opened");
                stages.Add("typed-query-materialized-and-size-checked");
                return Task.CompletedTask;
            },
            () =>
            {
                stages.Add("post-sweep");
                return Task.CompletedTask;
            });

        stages.Should().Equal(
            "pre-sweep-complete",
            "seal-members-validated",
            "reader-opened",
            "typed-query-materialized-and-size-checked",
            "post-sweep");
        elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public async Task CandidatePackagePublicationPerformsImmutableFreshReopen(string candidate)
    {
        var fixture = WriteResolvedManifest(authorizationApproved: true);
        var manifest = JsonSerializer.Deserialize<MonitoredRunManifest>(
            File.ReadAllBytes(fixture.ManifestPath),
            JsonOptions)!;
        var execution = MonitoredExecutionPlanner.Expand().Single(item =>
            item.CaseId == "Q1" && item.Candidate == candidate);
        var staging = Path.Combine(_workspace, $"publish-{candidate}-staging");
        var package = Path.Combine(_workspace, $"publish-{candidate}-package");
        Directory.CreateDirectory(staging);
        var descriptor = new MonitoredWorkerDescriptor(
            MonitoredProtocolVersions.WorkerDescriptorSchema,
            MonitoredWorkerMode.Execute,
            fixture.RepositoryRoot,
            fixture.ManifestPath,
            HashFile(fixture.ManifestPath),
            execution,
            Path.Combine(_workspace, $"publish-{candidate}-execution"),
            staging,
            package,
            $"capture-{candidate}",
            $"artifact-{candidate}",
            null,
            null,
            null,
            null,
            null);
        Directory.CreateDirectory(descriptor.ExecutionRoot);
        var limits = new DurableCounterPipelineLimits(
            BatchMaxAge: TimeSpan.FromMilliseconds(100));
        var factory = MonitoredAdapterRegistry.Require(candidate);
        var adapter = factory.Create(new DurableStorageAdapterCreateRequest(
            descriptor.CaptureId,
            descriptor.ArtifactId,
            staging,
            P1Configuration(),
            limits,
            NoDurableStorageFaults.Instance));
        await using var pipeline = new DurableCounterPipeline(
            limits,
            new DurableCounterGlobalBudget(limits),
            adapter);
        foreach (var observation in DurableCounterFixture.GenerateQ1().Take(64))
        {
            pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
        }
        var drain = await pipeline.DrainAsync(TimeSpan.FromSeconds(5));
        drain.CompletedWithinTimeout.Should().BeTrue();
        var finalization = Stopwatch.StartNew();
        (await pipeline.FinalizeAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        var accounting = pipeline.GetAccounting();
        var quality = new DurableCounterQuery([], limits).Quality(accounting);
        var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        await adapter.DisposeAsync();

        var published = await MonitoredPackagePublisher.PublishAsync(
            descriptor,
            manifest,
            factory,
            preSeal,
            accounting,
            volatileTailUnknown: false,
            derivedFromCaptureId: null,
            recoveryReason: null,
            finalization,
            logicalCommittedBytes: 64 * 512,
            observeBoundary: static (_, _) => Task.CompletedTask);
        try
        {
            var summary = await published.Reader.SummaryAsync(CancellationToken.None);
            summary.Sum(static item => item.RetainedCount).Should().Be(64);
            MonitoredFile.IsReadOnlyTree(package, 4_096, 4_096).Should().BeTrue();
            published.ReopenAndFirstQuery.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await published.Reader.DisposeAsync();
        }
    }

    [Fact]
    public void PersistentReceiptCannotBeReplaced()
    {
        var path = Path.Combine(_workspace, "attempt-start.json");
        MonitoredFile.WriteNewJson(path, new { attempt = 1 });
        var original = File.ReadAllBytes(path);

        Action replace = () => MonitoredFile.WriteNewJson(path, new { attempt = 2 });

        replace.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("ArtifactAlreadyExists");
        File.ReadAllBytes(path).Should().Equal(original);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public async Task ConcurrentCaptureProbeUsesActualCandidateWriterLifecycles(string candidate)
    {
        var limits = new DurableCounterPipelineLimits(
            BatchMaxAge: TimeSpan.FromMilliseconds(100));
        var factory = MonitoredAdapterRegistry.Require(candidate);
        var firstRoot = Path.Combine(_workspace, $"f5-{candidate}-first");
        var secondRoot = Path.Combine(_workspace, $"f5-{candidate}-second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        await using var first = factory.Create(new DurableStorageAdapterCreateRequest(
            $"capture-{candidate}-first",
            $"artifact-{candidate}-first",
            firstRoot,
            P1Configuration(),
            limits,
            NoDurableStorageFaults.Instance));
        var result = await MonitoredConcurrentCaptureGate.ProbeAsync(
            limits,
            first,
            () => factory.Create(new DurableStorageAdapterCreateRequest(
                $"capture-{candidate}-second",
                $"artifact-{candidate}-second",
                secondRoot,
                P1Configuration(),
                limits,
                NoDurableStorageFaults.Instance)));

        result.Rejected.Should().BeTrue();
        result.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ScheduledOfferPacingUsesTheDeclaredIntervalWithoutGrace()
    {
        var now = TimeSpan.Zero;
        var limits = new DurableCounterPipelineLimits(
            BatchMaxAge: TimeSpan.FromMilliseconds(1));
        var evidence = new MonitoredSourceAdmissionEvidence();
        await using var pipeline = new DurableCounterPipeline(
            limits,
            new DurableCounterGlobalBudget(limits),
            new RecordingCounterSink());

        var complete = await MonitoredWorkerExecutor.OfferScheduledAsync(
            pipeline,
            records: 10,
            perSecond: 10,
            offers: [],
            evidence,
            duration: TimeSpan.FromSeconds(1),
            elapsed: () => now,
            delay: value =>
            {
                now += value;
                return Task.CompletedTask;
            });

        complete.ScheduledOffers.Should().Be(10);
        complete.AttemptedOffers.Should().Be(10);
        complete.AchievedOfferRatio.Should().Be(1);
        complete.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));

        now = TimeSpan.Zero;
        var truncatedEvidence = new MonitoredSourceAdmissionEvidence();
        await using var truncatedPipeline = new DurableCounterPipeline(
            limits,
            new DurableCounterGlobalBudget(limits),
            new RecordingCounterSink());
        var truncated = await MonitoredWorkerExecutor.OfferScheduledAsync(
            truncatedPipeline,
            records: 10,
            perSecond: 10,
            offers: [],
            truncatedEvidence,
            duration: TimeSpan.FromSeconds(1),
            elapsed: () => now,
            delay: value =>
            {
                now += value + TimeSpan.FromSeconds(1);
                return Task.CompletedTask;
            });

        truncated.ScheduledOffers.Should().Be(10);
        truncated.AttemptedOffers.Should().Be(1);
        truncated.AchievedOfferRatio.Should().Be(0.1);
    }

    [Fact]
    public void RequestMetricsUseOneScheduledMeasurementPopulation()
    {
        var population = new MonitoredRequestPopulation(
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(42),
            maximumRetainedSamples: 1_000);
        population.RecordScheduled(TimeSpan.FromSeconds(11), skipped: false);
        population.RecordCompleted(TimeSpan.FromSeconds(11), 11, success: true);
        population.RecordScheduled(TimeSpan.FromSeconds(12), skipped: false);
        population.RecordCompleted(TimeSpan.FromSeconds(12), 12, success: true);
        population.RecordScheduled(TimeSpan.FromSeconds(41.999), skipped: true);
        population.RecordScheduled(TimeSpan.FromSeconds(42), skipped: false);
        population.RecordCompleted(TimeSpan.FromSeconds(42), 42, success: false);
        population.RecordSchedulingStopped(TimeSpan.FromSeconds(44));
        population.RecordEpisodeCompleted(TimeSpan.FromSeconds(44.5));

        var result = population.Snapshot();

        result.Scheduled.Should().Be(2);
        result.SkippedAtConcurrencyLimit.Should().Be(1);
        result.Completed.Should().Be(1);
        result.Succeeded.Should().Be(1);
        result.Failed.Should().Be(0);
        result.RetainedSamples.Should().Be(1);
        result.P50Milliseconds.Should().Be(12);
        result.P95Milliseconds.Should().Be(12);
        result.EpisodeScheduled.Should().Be(4);
        result.EpisodeCompleted.Should().Be(3);
        result.EpisodeSucceeded.Should().Be(2);
        result.EpisodeFailed.Should().Be(1);
    }

    [Fact]
    public void LiveSourceTimestampConversionAdmitsTheWholeCollectionWindow()
    {
        var counter = new DotnetDiagnostics.Core.Counters.CounterValue(
            "System.Runtime",
            "cpu-usage",
            "CPU Usage",
            1,
            DotnetDiagnostics.Core.Counters.CounterKind.Mean);

        MonitoredWorkerExecutor.TryCreateLiveObservation(
                counter,
                sourceMilliseconds: 100,
                out var early)
            .Should().BeTrue();
        MonitoredWorkerExecutor.TryCreateLiveObservation(
                counter,
                sourceMilliseconds: 33_900,
                out var late)
            .Should().BeTrue();
        MonitoredWorkerExecutor.TryCreateLiveObservation(
                counter,
                sourceMilliseconds: double.NaN,
                out _)
            .Should().BeFalse();

        early.SourceTimeTicks.Should().Be(TimeSpan.FromMilliseconds(100).Ticks);
        late.SourceTimeTicks.Should().Be(TimeSpan.FromMilliseconds(33_900).Ticks);
    }

    [LinuxOnlyFact]
    public async Task BaselineLiveCaptureUsesShippingCollectorAndDoesNotInventRawTickCounts()
    {
        using var current = Process.GetCurrentProcess();

        var capture = await MonitoredWorkerExecutor.CollectLiveTicksAsync(
            current.Id,
            pipeline: null,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        capture.Coverage.Should().StartWith("shipping-eventpipe-counter-collector");
        capture.SourceTicks.Should().BeNull();
        capture.MalformedPayloads.Should().BeNull();
        capture.AdmissionInvalid.Should().BeNull();
        capture.RejectedNewKeys.Should().BeNull();
        capture.OtherRejected.Should().BeNull();
        capture.SourceKeys.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CompleteEvidenceWithNoEligibleCandidateIsConclusiveOnlyAboutInconclusiveness()
    {
        var requests = RequestMetrics(2);
        var outcomes = MonitoredExecutionPlanner.Expand()
            .Select(execution => new MonitoredCaseOutcome(
                execution.Ordinal,
                execution.CaseId,
                execution.Candidate,
                execution.CaseId == "Q1" && execution.Candidate is "A" or "B"
                    ? "fail"
                    : "pass",
                FailureCode: null,
                FailureMessage: null,
                MonitoringComplete: true,
                MonitoringAlarm: null,
                MonitorSummaryRecords: 1,
                MonitorSummaryBytes: 1,
                MaximumObservedIdentities: 1,
                MaximumObservedSweepBytes: 1,
                MonitoringEvidenceFiles: ["monitor.jsonl"],
                Worker: CompleteWorker(execution, requests)))
            .ToArray();

        var decision = MonitoredDecisionEngine.Decide(outcomes);

        decision.Recommendation.Should().Be("inconclusive");
        decision.CompleteEvidence.Should().BeTrue();
        decision.Scope.Should().Be("monitored-scope-only");
    }

    [Fact]
    public void LiveP95ScreenPassesWhenIndependentMedianAllowanceExceedsOldBlockAllowance()
    {
        var outcomes = LiveP95Outcomes(
            baselines: [100, 120, 140],
            increases: [17, 1, 30]);

        MonitoredDecisionEngine.LiveScreensPass("A", outcomes).Should().BeTrue();
    }

    [Fact]
    public void LiveP95ScreenFailsWhenOldBlockAllowanceWouldHavePassed()
    {
        var outcomes = LiveP95Outcomes(
            baselines: [100, 120, 140],
            increases: [1, 30, 20]);

        MonitoredDecisionEngine.LiveScreensPass("A", outcomes).Should().BeFalse();
    }

    [Theory]
    [InlineData("E", 599, 880)]
    [InlineData("E", 601, 880)]
    [InlineData("A", 599, 880)]
    [InlineData("A", 601, 880)]
    [InlineData("B", 599, 880)]
    [InlineData("B", 601, 880)]
    [InlineData("E", 600, 879)]
    [InlineData("A", 600, 879)]
    [InlineData("B", 600, 879)]
    [InlineData("B", 600, 881)]
    public void LiveScheduleCoverageCannotBeAcceptedByWorkerOrCampaign(
        string candidate, int scheduled, int episodeScheduled)
    {
        var requests = RequestMetrics(2) with
        {
            Scheduled = scheduled,
            Completed = scheduled,
            Succeeded = scheduled,
            RetainedSamples = scheduled,
            EpisodeScheduled = episodeScheduled,
            EpisodeCompleted = episodeScheduled,
            EpisodeSucceeded = episodeScheduled,
        };
        var outcomes = LiveP95Outcomes([2, 2, 2], [0, 0, 0])
            .Select(outcome => outcome.CaseId == "L1" && outcome.Candidate == candidate
                ? outcome with { Worker = outcome.Worker! with { Requests = requests } }
                : outcome).ToArray();

        BoundedLiveRequestLoad.HasCompleteSchedule(requests).Should().BeFalse();
        MonitoredDecisionEngine.LiveScreensPass(candidate == "E" ? "A" : candidate, outcomes)
            .Should().BeFalse();
        var decision = MonitoredDecisionEngine.Decide(outcomes);
        decision.CompleteEvidence.Should().BeFalse();
        decision.Recommendation.Should().Be("inconclusive");
    }

    [Theory]
    [InlineData(43.95, 44)]
    [InlineData(44, 43.95)]
    [InlineData(double.NaN, 44)]
    [InlineData(44, double.NaN)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    [InlineData(44, double.PositiveInfinity)]
    public void CompleteNominalCountsCannotReplaceActualElapsedEvidence(double scheduling, double episode)
    {
        var requests = RequestMetrics(2) with
        {
            SchedulingElapsedSeconds = scheduling,
            EpisodeElapsedSeconds = episode,
        };
        BoundedLiveRequestLoad.HasCompleteSchedule(requests).Should().BeFalse();
    }

    [Fact]
    public void PrevalidationPlanIsSeparateFixedAndUnscored()
    {
        var plan = PrevalidationProtocol.Plan();
        plan.Select(static item => item.Id).Should().Equal(
            "PV-O1-A", "PV-O1-B", "PV-LIVE-E", "PV-LIVE-A", "PV-LIVE-B",
            "PV-F3-A", "PV-F3-B", "PV-GEOMETRY");
        plan.Select(static item => item.Ordinal).Should().Equal(Enumerable.Range(1, 8));
        plan.Select(static item => item.FixtureSlots).Should().Equal(63, 63, 64, 63, 63, 63, 63, 64);
        plan.Should().OnlyContain(static item => item.Execution.MaximumSeconds == 120
            && item.Execution.MaximumAttempts == 1);
        PrevalidationLayout.DeriveSuiteIdentityBound().Should().Be(2_506).And.BeLessThan(4_096);
        MonitoredExecutionPlanner.Expand().Should().HaveCount(35);
    }

    [Fact]
    public void PrevalidationCannotTurnComponentProofIntoCampaignReadiness()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var fixture = PrepareComponentExecution();
        var evidence = fixture.ComponentEvidence with { DescriptorOnlyCampaignFeasibilityEstablished = false };
        Action prevalidation = () => MonitoredRunManifestValidator.ValidateComponentEvidence(
            fixture.Manifest.SourceCommits, fixture.Manifest.AttributionMapSha256, fixture.Encoding,
            evidence, MonitoredAdmissionStage.PrevalidationComponentProof);
        prevalidation.Should().NotThrow();
        Action scored = () => MonitoredRunManifestValidator.ValidateComponentEvidence(
            fixture.Manifest.SourceCommits, fixture.Manifest.AttributionMapSha256, fixture.Encoding,
            evidence, MonitoredAdmissionStage.ScoredCampaign);
        scored.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("MonitorComponentEvidenceInsufficient");
        evidence.DescriptorOnlyCampaignFeasibilityEstablished.Should().BeFalse();
        Action unsafeComponent = () => MonitoredRunManifestValidator.ValidateComponentEvidence(
            fixture.Manifest.SourceCommits, fixture.Manifest.AttributionMapSha256, fixture.Encoding,
            evidence with { SourceRecoverySharedCancellationObserved = false },
            MonitoredAdmissionStage.PrevalidationComponentProof);
        unsafeComponent.Should().Throw<DurableStorageExperimentException>();
    }

    [Theory]
    [InlineData("order")]
    [InlineData("scope")]
    [InlineData("attempts")]
    [InlineData("time")]
    [InlineData("context")]
    [InlineData("suite")]
    [InlineData("addendum")]
    [InlineData("schema")]
    [InlineData("namespace")]
    public void PrevalidationRejectsContractDrift(string field)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var manifest = PrevalidationContractFixture();
        PrevalidationProtocol.ValidateShape(manifest);
        var changed = field switch
        {
            "order" => manifest with { Probes = manifest.Probes.Reverse().ToArray() },
            "scope" => manifest with { Scope = "campaign" },
            "attempts" => manifest with { Bounds = manifest.Bounds with { Attempts = 2 } },
            "time" => manifest with { Bounds = manifest.Bounds with { EntrySeconds = 121 } },
            "context" => manifest with { Bounds = manifest.Bounds with { ContextIdentities = 4_096 } },
            "suite" => manifest with { Bounds = manifest.Bounds with { SuiteBytes = 4_294_967_296 } },
            "addendum" => manifest with { AddendumSha256 = new string('a', 64) },
            "schema" => manifest with { Schema = MonitoredProtocolVersions.ManifestSchema },
            "namespace" => manifest with { SuiteId = "campaign-test" },
            _ => throw new InvalidOperationException(),
        };
        Action action = () => PrevalidationProtocol.ValidateShape(changed);
        action.Should().Throw<DurableStorageExperimentException>();
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("schema")]
    [InlineData("scope")]
    [InlineData("attempts")]
    [InlineData("suite")]
    [InlineData("review")]
    public void PrevalidationAuthorizationCannotBeReusedOrPromoted(string field)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var manifest = PrevalidationContractFixture();
        var hash = HashText("component-test-only-manifest");
        var receipt = new PrevalidationAuthorization("durable-prevalidation-authorization/1",
            PrevalidationProtocol.Scope, manifest.SuiteId, hash, PrevalidationProtocol.AddendumSha256,
            manifest.ImplementationAcceptance.Sha256, manifest.HistoricalReport.Sha256,
            "component-test-only", DateTimeOffset.UtcNow, 8, 1, true);
        PrevalidationProtocol.ValidateAuthorization(manifest, hash, receipt);
        var changed = field switch
        {
            "manifest" => receipt with { ManifestSha256 = new string('b', 64) },
            "schema" => receipt with { Schema = MonitoredProtocolVersions.AuthorizationSchema },
            "scope" => receipt with { Scope = "scored-campaign" },
            "attempts" => receipt with { Attempts = 2 },
            "suite" => receipt with { SuiteId = "pv-new-id-does-not-reset-authorization" },
            "review" => receipt with { AcceptanceSha256 = new string('c', 64) },
            _ => throw new InvalidOperationException(),
        };
        Action action = () => PrevalidationProtocol.ValidateAuthorization(manifest, hash, changed);
        action.Should().Throw<DurableStorageExperimentException>();
    }

    [Theory]
    [InlineData("""{"schema":"a","schema":"b"}""")]
    [InlineData("""{"schema":"a","unexpected":true}""")]
    public void PrevalidationJsonIsClosedAndRejectsDuplicateMembers(string json)
    {
        var path = Path.Combine(_workspace, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        Action action = () => PrevalidationProtocol.Read<PrevalidationAdoption>(path);
        action.Should().Throw<Exception>();
    }

    [Fact]
    public void PrevalidationSummaryKeepsBothScopesWithinNewlineInclusiveLimit()
    {
        var original = MonitoredSweepSummaryEncoding.CreateWorstCaseFixture();
        MonitoredSweepSummaryEncoding.EncodeLine(original).Length.Should().Be(854);
        var expanded = original with
        {
            CurrentContextIdentities = int.MaxValue,
            CurrentContextRootedIdentities = int.MaxValue,
            RetainedHistoryBytes = long.MaxValue,
            FirstErrorCode = new string('e', 64),
        };
        var bytes = MonitoredSweepSummaryEncoding.EncodeLine(expanded);
        bytes.Length.Should().Be(983).And.BeLessThanOrEqualTo(1_024);
        var native = MonitoredSweepSummaryEncoding.EncodeLine(expanded with
        {
            FirstDescriptorFailure = [2, 4, int.MinValue, int.MaxValue],
        });
        native.Length.Should().Be(1_017).And.BeLessThanOrEqualTo(1_024);
        bytes[^1].Should().Be((byte)'\n');
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        json.Should().Contain("\"ci\":2147483647").And.Contain("\"cr\":2147483647");
        json.Should().Contain("\"ec\":\"" + new string('e', 64) + "\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("not a token")]
    [InlineData("é")]
    [InlineData("line\nbreak")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void PrevalidationRejectsUnboundedOrNonTokenCausalEvidence(string code)
    {
        Action encode = () => MonitoredSweepSummaryEncoding.EncodeLine(
            MonitoredSweepSummaryEncoding.CreateWorstCaseFixture() with { FirstErrorCode = code });
        encode.Should().Throw<DurableStorageExperimentException>();
        PrevalidationFailureCodes.Normalize(code).Should().Be("PrevalidationErrorCodeEncodingLimit");
        Action validate = () => PrevalidationFailureCodes.Validate(code, null);
        validate.Should().Throw<DurableStorageExperimentException>();
    }

    [Fact]
    public async Task PrevalidationPersistsFirstSweepCauseAndDoesNotResetItAfterACompleteSweep()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        var missing = Path.Combine(fixture.Manifest.WorkspaceRoot, "missing-root");
        var secondMissing = Path.Combine(fixture.Manifest.WorkspaceRoot, "second-missing-root");
        var attribution = fixture.Attribution with
        {
            Roots = [.. fixture.Attribution.Roots, new MonitoredAttributionRoot("workspace", missing, true),
                new MonitoredAttributionRoot("workspace", secondMissing, true)],
        };
        using var self = Process.GetCurrentProcess();
        var scope = new PrevalidationObservationScope([],
            MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness));
        var path = Path.Combine(fixture.Manifest.OutputRoot, "causal-monitor.jsonl");
        await using (var monitor = new MonitoredStorageMonitor(attribution, fixture.Encoding, path,
            prevalidationScope: scope))
        {
            var failed = await monitor.ObserveBoundaryAsync("fixture-preparation", true, CancellationToken.None);
            failed.Summary.FirstErrorCode.Should().Be("DeclaredRootMissing");
            failed.Summary.ErrorCount.Should().Be(2);
            Directory.CreateDirectory(missing);
            Directory.CreateDirectory(secondMissing);
            var later = await monitor.ObserveBoundaryAsync("fixtures-complete", true, CancellationToken.None);
            later.Summary.Complete.Should().BeTrue();
            later.Summary.FirstErrorCode.Should().BeNull();
            Action gate = () => PrevalidationExecutor.RequireMonitor(monitor);
            gate.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("DeclaredRootMissing");
        }
        var lines = File.ReadAllLines(path);
        lines.Should().HaveCount(2);
        var first = JsonSerializer.Deserialize<MonitoredSweepSummary>(lines[0], PrevalidationProtocol.Json)!;
        first.FirstErrorCode.Should().Be("DeclaredRootMissing");
        PrevalidationExecutor.ValidateSummaryFailure(first);
        first.Complete.Should().BeFalse();
        lines.Should().OnlyContain(line => System.Text.Encoding.UTF8.GetByteCount(line) + 1 <= 1_024);
    }

    [Fact]
    public void PrevalidationPreservesPrimaryAndCountsAllLaterFailuresWithoutGrowingArrays()
    {
        var coverage = PrevalidationExecutor.Failed(PrevalidationProtocol.Plan()[0], "DescriptorObservationUnavailable");
        coverage = PrevalidationExecutor.RecordFailure(coverage, "PrevalidationCleanupErrors", "entry-cleanup");
        coverage = PrevalidationExecutor.RecordFailure(coverage, "IOException", "monitor-dispose");
        coverage.FailureCode.Should().Be("DescriptorObservationUnavailable");
        coverage.SecondaryFailures.Should().Be(new PrevalidationSecondaryFailures("PrevalidationCleanupErrors", "entry-cleanup", 2));
        PrevalidationExecutor.HasCoverage(coverage).Should().BeFalse();
        var saturated = coverage with
        {
            SecondaryFailures = new(new string('s', 64), "entry-cleanup", int.MaxValue),
        };
        var next = PrevalidationExecutor.RecordFailure(saturated, "AnotherFinalizationError", "monitor-dispose");
        next.SecondaryFailures!.Count.Should().Be(int.MaxValue);
        next.SecondaryFailures.FirstCode.Should().HaveLength(64);
        var roundtrip = JsonSerializer.Deserialize<PrevalidationCoverage>(
            JsonSerializer.Serialize(next, PrevalidationProtocol.Json), PrevalidationProtocol.Json);
        roundtrip.Should().BeEquivalentTo(next);
        PrevalidationFailureCodes.Validate(next.FailureCode, next.SecondaryFailures);
        Action orphan = () => PrevalidationFailureCodes.Validate(null, new("CleanupError", "entry-cleanup", 1));
        orphan.Should().Throw<DurableStorageExperimentException>();
        Action zero = () => PrevalidationFailureCodes.Validate("Primary", new("CleanupError", "entry-cleanup", 0));
        zero.Should().Throw<DurableStorageExperimentException>();
    }

    [Theory]
    [InlineData("entry-cleanup")]
    [InlineData("monitor-dispose")]
    [InlineData("evidence-finalization")]
    [InlineData("admission-dispose")]
    [InlineData("suite-finalization")]
    [InlineData("suite-dispose")]
    [InlineData("suite-freeze")]
    public void PrevalidationCausalStagesPreserveIdenticalFullWidthCodes(string secondaryStage)
    {
        var code = new string('c', 64);
        var primary = PrevalidationExecutor.Failed(PrevalidationProtocol.Plan()[0], code, "fixture-preparation");
        var outcome = PrevalidationExecutor.RecordFailure(primary, code, secondaryStage);
        outcome.FailureCode.Should().Be(code);
        outcome.FailureStage.Should().Be("fixture-preparation");
        outcome.SecondaryFailures.Should().Be(new PrevalidationSecondaryFailures(code, secondaryStage, 1));
        var json = JsonSerializer.Serialize(outcome, PrevalidationProtocol.Json);
        var read = JsonSerializer.Deserialize<PrevalidationCoverage>(json, PrevalidationProtocol.Json)!;
        PrevalidationFailureCodes.Validate(read.FailureCode, read.SecondaryFailures);
        PrevalidationFailureCodes.ValidateStage(read.FailureCode, read.FailureStage);
        read.Should().BeEquivalentTo(outcome);
        Action invalid = () => PrevalidationFailureCodes.ValidateStage(code, "unbounded-or-unknown-stage");
        invalid.Should().Throw<DurableStorageExperimentException>();
    }

    [Fact]
    public void PrevalidationPinnedProcStatReapedAfterOpenHasVerifiedEsrchMapping()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var process = StartWaitingStub();
        var identity = MonitoredProcessIdentity.Capture(process, MonitoredProcessRole.Diagnostic);
        IOException? observed = null;
        try
        {
            LinuxPrevalidationProcessOperations.Instance.IsOriginalAlive(identity).Should().BeTrue();
            var alive = LinuxPrevalidationProcessOperations.IsOriginalAliveForComponent(identity, path =>
            {
                var pinned = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
                try
                {
                    process.StandardInput.WriteLine("release");
                    process.StandardInput.Flush();
                    process.WaitForExit(5_000).Should().BeTrue();
                    return pinned;
                }
                catch
                {
                    pinned.Dispose();
                    throw;
                }
            }, stream =>
            {
                try { return LinuxPrevalidationProcessOperations.ReadPinnedStat(stream); }
                catch (IOException exception)
                {
                    observed = exception;
                    _output.WriteLine($"Runtime={Environment.Version}; pinned proc/stat read after owned reap: "
                        + $"type={exception.GetType().Name}; HResult={exception.HResult}; hex={exception.HResult:X8}");
                    throw;
                }
            });
            observed.Should().NotBeNull();
            observed.Should().BeOfType<IOException>();
            observed!.HResult.Should().Be(3);
            alive.Should().BeFalse();
            Action reopen = () => File.ReadAllText(FormattableString.Invariant($"/proc/{identity.ProcessId}/stat"));
            var missing = reopen.Should().Throw<DirectoryNotFoundException>().Which;
            missing.HResult.Should().Be(unchecked((int)0x80070003));
            _output.WriteLine($"Fresh proc/stat open after owned reap: type={missing.GetType().Name}; "
                + $"HResult={missing.HResult}; hex={missing.HResult:X8}");
            LinuxPrevalidationProcessOperations.Instance.IsOriginalAlive(identity).Should().BeFalse();
        }
        finally { LinuxPrevalidationProcessOperations.Instance.Signal(identity); }
    }

    [Theory]
    [InlineData("open", "missing-file", true)]
    [InlineData("open", "missing-directory", true)]
    [InlineData("open", "esrch", true)]
    [InlineData("read", "esrch", true)]
    [InlineData("open", "derived-io-esrch", false)]
    [InlineData("read", "derived-io-esrch", false)]
    [InlineData("read", "missing-file", true)]
    [InlineData("read", "missing-directory", true)]
    [InlineData("open", "permission", false)]
    [InlineData("read", "permission", false)]
    [InlineData("open", "eio", false)]
    [InlineData("read", "eio", false)]
    [InlineData("open", "generic-io", false)]
    [InlineData("read", "generic-io", false)]
    [InlineData("open", "windows-path-hresult", false)]
    [InlineData("read", "windows-path-hresult", false)]
    public void PrevalidationProcStatOnlyClassifiesPreciseAbsence(string phase, string kind, bool absent)
    {
        Exception failure = kind switch
        {
            "missing-file" => new FileNotFoundException("controlled"),
            "missing-directory" => new DirectoryNotFoundException("controlled"),
            "esrch" => new IOException("controlled", 3),
            "derived-io-esrch" => new ControlledDerivedIOException(),
            "permission" => new UnauthorizedAccessException("controlled", new IOException("controlled", 13)),
            "eio" => new IOException("controlled", 5),
            "windows-path-hresult" => new IOException("controlled", unchecked((int)0x80070003)),
            _ => new IOException("controlled"),
        };
        using var pinned = new MemoryStream();
        bool Observe() => LinuxPrevalidationProcessOperations.IsOriginalAliveForComponent(
            new(123, 456, MonitoredProcessRole.Diagnostic),
            _ => phase == "open" ? throw failure : pinned,
            _ => throw failure);
        if (absent) Observe().Should().BeFalse();
        else
        {
            Action observe = () => Observe();
            observe.Should().Throw<Exception>().Which.Should().BeSameAs(failure);
        }
        if (phase == "read") pinned.CanRead.Should().BeFalse();
    }

    [Theory]
    [InlineData("S", 456, true)]
    [InlineData("S", 457, false)]
    [InlineData("Z", 456, false)]
    [InlineData("X", 456, false)]
    public void PrevalidationPinnedProcStatRetainsStartTimeAndZombieChecks(string state, int start, bool alive)
    {
        var text = FormattableString.Invariant($"123 (component) {state} {string.Join(' ', Enumerable.Repeat("0", 18))} {start}");
        LinuxPrevalidationProcessOperations.IsOriginalAliveForComponent(
            new(123, 456, MonitoredProcessRole.Diagnostic),
            _ => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)),
            LinuxPrevalidationProcessOperations.ReadPinnedStat).Should().Be(alive);
    }

    [Fact]
    public void PrevalidationPinnedProcStatRejectsMalformedAndOversizeReads()
    {
        foreach (var text in new[] { "malformed", new string('x', 4_097) })
        {
            Action observe = () => LinuxPrevalidationProcessOperations.IsOriginalAliveForComponent(
                new(123, 456, MonitoredProcessRole.Diagnostic),
                _ => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)),
                LinuxPrevalidationProcessOperations.ReadPinnedStat);
            observe.Should().Throw<DurableStorageExperimentException>();
        }
    }

    [Fact]
    public async Task PrevalidationCleanupRetainsIoEvidenceEvenWhenLaterQuiescent()
    {
        var calls = 0;
        var signals = 0;
        var result = await PrevalidationOwnership.StopAsync([new(123, 456, MonitoredProcessRole.Diagnostic)],
            new ScriptedCleanupOperations(_ => ++calls == 1 ? throw new IOException("controlled", 5) : false,
                _ => signals++, _ => Task.CompletedTask), CancellationToken.None);
        signals.Should().Be(0);
        result.Quiescent.Should().BeTrue();
        result.Errors.Should().Equal("123:signal:IOException:hr-00000005");
        result.AdditionalErrorCount.Should().Be(0);
    }

    [Fact]
    public async Task PrevalidationCleanupPermissionRetainsBothOuterAndInnerHResultsAndNeverSignals()
    {
        var signals = 0;
        var result = await PrevalidationOwnership.StopAsync([new(123, 456, MonitoredProcessRole.Diagnostic)],
            new ScriptedCleanupOperations(_ => throw new UnauthorizedAccessException("controlled",
                    new IOException("controlled", 13)), _ => signals++, _ => Task.CompletedTask),
            new CancellationToken(canceled: true));
        signals.Should().Be(0);
        result.Quiescent.Should().BeFalse();
        result.Unconfirmed.Should().HaveCount(1);
        result.Errors.Should().Equal(
            "123:signal:UnauthorizedAccessException:hr-80070005:iohr-0000000D",
            "123:confirm:UnauthorizedAccessException:hr-80070005:iohr-0000000D");
    }

    [Fact]
    public async Task PrevalidationCleanupIoDiagnosticsHaveInsertionCapAndExplicitOverflow()
    {
        var failures = 0;
        var rounds = 0;
        using var deadline = new CancellationTokenSource();
        var result = await PrevalidationOwnership.StopAsync([new(123, 456, MonitoredProcessRole.Diagnostic)],
            new ScriptedCleanupOperations(_ => throw new IOException("controlled", 100 + ++failures),
                _ => throw new InvalidOperationException("must not signal"), _ =>
                {
                    if (++rounds == 20) deadline.Cancel();
                    return Task.CompletedTask;
                }), deadline.Token);
        result.Quiescent.Should().BeFalse();
        result.Errors.Should().HaveCount(PrevalidationOwnership.MaximumCleanupErrors);
        result.Errors.Should().OnlyContain(code => MonitoredSweepSummaryEncoding.IsBoundedToken(
            code, PrevalidationOwnership.MaximumCleanupErrorBytes));
        result.AdditionalErrorCount.Should().Be(failures - PrevalidationOwnership.MaximumCleanupErrors);
        result.AdditionalErrorCount.Should().BeGreaterThan(0);
        var read = JsonSerializer.Deserialize<PrevalidationCleanupResult>(
            JsonSerializer.Serialize(result, PrevalidationProtocol.Json), PrevalidationProtocol.Json);
        read.Should().BeEquivalentTo(result);
    }

    [Fact]
    public void PrevalidationSummaryCannotHideAnIncompleteObservationOrInventSuccess()
    {
        var valid = MonitoredSweepSummaryEncoding.CreateWorstCaseFixture() with { FirstErrorCode = "RootFailure" };
        PrevalidationExecutor.ValidateSummaryFailure(valid);
        foreach (var invalid in new[]
        {
            valid with { FirstErrorCode = null },
            valid with { Complete = true },
            valid with { ErrorCount = -1 },
            valid with { ErrorCount = 0, UnclassifiedCount = 0 },
        })
        {
            Action validate = () => PrevalidationExecutor.ValidateSummaryFailure(invalid);
            validate.Should().Throw<DurableStorageExperimentException>();
        }
    }

    [Fact]
    public void PrevalidationOldFieldMapIsInspectionOnlyAndCannotAuthorizeNewExecution()
    {
        var current = PrevalidationContractFixture();
        var old = current with { ContextSummaryFieldMapSha256 = PrevalidationProtocol.LegacyContextSummaryFieldMapSha256 };
        PrevalidationProtocol.ValidateShape(old, allowLegacyInspection: true);
        Action admission = () => PrevalidationProtocol.ValidateShape(old);
        admission.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("PrevalidationContextEncodingMismatch");
        var previous = current with
        {
            ContextSummaryFieldMapSha256 = PrevalidationProtocol.PreviousContextSummaryFieldMapSha256,
        };
        PrevalidationProtocol.ValidateShape(previous, allowLegacyInspection: true);
        Action previousAdmission = () => PrevalidationProtocol.ValidateShape(previous);
        previousAdmission.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("PrevalidationContextEncodingMismatch");
        PrevalidationProtocol.ValidateShape(current);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void DescriptorObserverClosedDescriptorRetainsExactOperation(int operation, bool unlinked)
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_workspace, "descriptor-close");
        using var writer = File.OpenWrite(path);
        if (unlinked) File.Delete(path);
        var fd = checked((int)writer.SafeFileHandle.DangerousGetHandle());
        var descriptor = $"/proc/{Environment.ProcessId}/fd/{fd}";
        Directory.GetFiles($"/proc/{Environment.ProcessId}/fd").Should().Contain(descriptor);
        Action observe = () => LinuxProcessDescriptorObserver.OpenCoherentForComponent(
            Environment.ProcessId, descriptor, 4_096,
            phase => { if (phase == operation) writer.Dispose(); }).Dispose();
        var error = observe.Should().Throw<DurableStorageExperimentException>().Which;
        error.DescriptorFailure.Should().NotBeNull();
        error.DescriptorFailure!.Operation.Should().Be(operation);
        error.DescriptorFailure.Descriptor.Should().Be(fd);
        if (operation is 1 or 3)
        {
            error.Code.Should().Be("DescriptorObservationUnavailable");
            error.DescriptorFailure.Error.Should().Be(2);
        }
        else
        {
            error.Code.Should().Be("DescriptorFlagsFileNotFoundException");
            error.DescriptorFailure.Error.Should().Be(unchecked((int)0x80070002));
        }
        PersistDescriptorProof(error);
        Directory.GetFiles($"/proc/{Environment.ProcessId}/fd")
            .Should().NotContain(candidate => new FileInfo(candidate).LinkTarget == path
                || new FileInfo(candidate).LinkTarget == path + " (deleted)");
    }

    [Theory]
    [InlineData(1, 13)]
    [InlineData(3, 13)]
    [InlineData(1, 5)]
    [InlineData(3, 5)]
    public void DescriptorObserverInjectedNativeErrorsAreNotTreatedAsDisappearance(int operation, int errno)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var writer = File.OpenWrite(Path.Combine(_workspace, "descriptor-injected-error"));
        var fd = checked((int)writer.SafeFileHandle.DangerousGetHandle());
        Action observe = () => LinuxProcessDescriptorObserver.OpenCoherentForComponent(
            Environment.ProcessId, $"/proc/{Environment.ProcessId}/fd/{fd}", 4_096,
            null, phase => phase == operation ? errno : null).Dispose();
        var error = observe.Should().Throw<DurableStorageExperimentException>().Which;
        error.Code.Should().Be("DescriptorObservationUnavailable");
        error.DescriptorFailure.Should().Be(new DescriptorObservationFailure(operation, errno, fd));
        PersistDescriptorProof(error);
        Directory.GetFiles($"/proc/{Environment.ProcessId}/fd")
            .Count(candidate => new FileInfo(candidate).LinkTarget == writer.Name).Should().Be(1);
    }

    [Fact]
    public void DescriptorObserverRejectsRealFdReuseBetweenPins()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var first = File.OpenWrite(Path.Combine(_workspace, "descriptor-original"));
        using var replacement = File.OpenWrite(Path.Combine(_workspace, "descriptor-replacement"));
        var fd = checked((int)first.SafeFileHandle.DangerousGetHandle());
        Action observe = () => LinuxProcessDescriptorObserver.OpenCoherentForComponent(
            Environment.ProcessId, $"/proc/{Environment.ProcessId}/fd/{fd}", 4_096, phase =>
            {
                if (phase == 3)
                {
                    DuplicateDescriptor(checked((int)replacement.SafeFileHandle.DangerousGetHandle()), fd)
                        .Should().Be(fd);
                }
            }).Dispose();
        observe.Should().Throw<DurableStorageExperimentException>().Which.Code
            .Should().Be("DescriptorIdentityChangedDuringObservation");
    }

    [Fact]
    public void DescriptorObserverSelfEnumerationFiltersItsDirectoryButClosedExplicitPinFails()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = $"/proc/{Environment.ProcessId}/fd";
        string? enumerationDescriptor = null;
        foreach (var descriptor in Directory.EnumerateFileSystemEntries(root))
        {
            if (new FileInfo(descriptor).LinkTarget == root)
            {
                enumerationDescriptor.Should().BeNull();
                enumerationDescriptor = descriptor;
            }
        }
        enumerationDescriptor.Should().NotBeNull();
        Directory.GetFiles(root).Should().NotContain(enumerationDescriptor!);
        Action observe = () => LinuxProcessDescriptorObserver.OpenCoherent(
            Environment.ProcessId, enumerationDescriptor!, 4_096).Dispose();
        var error = observe.Should().Throw<DurableStorageExperimentException>().Which;
        error.DescriptorFailure!.Operation.Should().Be(1);
        error.DescriptorFailure.Error.Should().Be(2);
        PersistDescriptorProof(error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task DescriptorObserverTinyFixtureActuallyClosesDuringObservation(int operation)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "concurrent-fixtures");
        Directory.CreateDirectory(root);
        using var opened = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var closed = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var writes = 0;
        var closes = 0;
        var writer = Task.Run(() => PrevalidationLayout.CreateHistoryFixturesForComponent(root, 1,
            () =>
            {
                if (Interlocked.Increment(ref writes) != 1) return;
                opened.Set();
                release.Wait(deadline.Token);
            }, deadline.Token, () =>
            {
                if (Interlocked.Increment(ref closes) != 1) return;
                closed.Set();
                finish.Wait(deadline.Token);
            }), deadline.Token);
        try
        {
            opened.Wait(deadline.Token);
            var descriptor = Directory.GetFiles($"/proc/{Environment.ProcessId}/fd").Single(path =>
                new FileInfo(path).LinkTarget?.StartsWith(root + "/", StringComparison.Ordinal) == true);
            Action observe = () => LinuxProcessDescriptorObserver.OpenCoherentForComponent(
                Environment.ProcessId, descriptor, 4_096, phase =>
                {
                    if (phase != operation) return;
                    release.Set();
                    closed.Wait(deadline.Token);
                }).Dispose();
            var error = observe.Should().Throw<DurableStorageExperimentException>().Which;
            error.DescriptorFailure!.Operation.Should().Be(operation);
            error.DescriptorFailure.Error.Should().Be(2);
            PersistDescriptorProof(error);
        }
        finally
        {
            release.Set();
            finish.Set();
            await writer;
        }
        PrevalidationGeometry.CountFixtureFiles(root).Should().Be(4);
    }

    [Fact]
    public async Task DescriptorObserverCoordinatorSweepPersistsNativeFailureAndRemainsIncomplete()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        using var self = Process.GetCurrentProcess();
        var identity = MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness);
        var path = Path.Combine(fixture.Manifest.OutputRoot, "native-failure-monitor.jsonl");
        using var coordinatorWrite = File.OpenWrite(Path.Combine(fixture.Manifest.OutputRoot, "coordinator-write"));
        coordinatorWrite.Write(new byte[512]);
        coordinatorWrite.Flush();
        var fd = checked((int)coordinatorWrite.SafeFileHandle.DangerousGetHandle());
        var descriptor = $"/proc/{self.Id}/fd/{fd}";
        using var release = new ManualResetEventSlim();
        using var closed = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var owner = Task.Run(() =>
        {
            release.Wait(deadline.Token);
            coordinatorWrite.Dispose();
            closed.Set();
        }, deadline.Token);
        try
        {
            await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding, path,
                prevalidationScope: new PrevalidationObservationScope([], identity));
            monitor.BeforeDescriptorOperationForComponent = (candidate, phase) =>
            {
                if (candidate != descriptor || phase != 3) return;
                release.Set();
                closed.Wait(deadline.Token);
            };
            monitor.AddProcess(identity);
            var result = await monitor.ObserveBoundaryAsync("native-proof", true, deadline.Token);
            result.Summary.Complete.Should().BeFalse();
            result.Summary.FirstDescriptorFailure.Should().NotBeNull();
            result.Summary.FirstDescriptorFailure.Should().Equal(0, 3, 2, fd);
            monitor.IsIncomplete.Should().BeTrue();
            Action require = () => PrevalidationExecutor.RequireSweep(result);
            require.Should().Throw<DurableStorageExperimentException>();
        }
        finally
        {
            release.Set();
            await owner;
        }
        var raw = File.ReadAllBytes(path);
        raw.Length.Should().BeLessThanOrEqualTo(1_024);
        var summary = JsonSerializer.Deserialize<MonitoredSweepSummary>(raw, PrevalidationProtocol.Json)!;
        PrevalidationExecutor.ValidateSummaryFailure(summary);
        summary.FirstDescriptorFailure.Should().HaveCount(4);
        _output.WriteLine(System.Text.Encoding.UTF8.GetString(raw));
    }

    [Theory]
    [InlineData(new int[] { })]
    [InlineData(new[] { 0, 1, 2 })]
    [InlineData(new[] { 0, 1, 2, 3, 4 })]
    [InlineData(new[] { 3, 1, 2, 3 })]
    [InlineData(new[] { 0, 5, 2, 3 })]
    [InlineData(new[] { 0, 1, 0, 3 })]
    [InlineData(new[] { 0, 3, 4096, 3 })]
    [InlineData(new[] { 0, 1, 2, -1 })]
    public void DescriptorObserverRejectsInvalidNativeContext(int[] context)
    {
        var summary = MonitoredSweepSummaryEncoding.CreateWorstCaseFixture() with
        {
            FirstErrorCode = "DescriptorObservationUnavailable", FirstDescriptorFailure = context,
        };
        Action encode = () => MonitoredSweepSummaryEncoding.EncodeLine(summary);
        encode.Should().Throw<DurableStorageExperimentException>();
        Action inspect = () => PrevalidationExecutor.ValidateSummaryFailure(summary);
        inspect.Should().Throw<DurableStorageExperimentException>();
    }

    [Fact]
    public void DescriptorObserverContextCannotBeAttachedToCompleteOrErrorlessSummary()
    {
        var valid = MonitoredSweepSummaryEncoding.CreateWorstCaseFixture() with
        {
            FirstErrorCode = "DescriptorObservationUnavailable", FirstDescriptorFailure = [0, 1, 2, 3],
        };
        foreach (var summary in new[]
        {
            valid with { Complete = true }, valid with { ErrorCount = 0 },
            valid with { FirstErrorCode = null },
        })
        {
            Action encode = () => MonitoredSweepSummaryEncoding.EncodeLine(summary);
            encode.Should().Throw<DurableStorageExperimentException>();
            Action inspect = () => PrevalidationExecutor.ValidateSummaryFailure(summary);
            inspect.Should().Throw<DurableStorageExperimentException>();
        }
    }

    private void PersistDescriptorProof(DurableStorageExperimentException exception)
    {
        var summary = MonitoredSweepSummaryEncoding.CreateWorstCaseFixture() with
        {
            FirstErrorCode = exception.Code,
            FirstDescriptorFailure = exception.DescriptorFailure!.Encode(MonitoredProcessRole.Harness),
        };
        var bytes = MonitoredSweepSummaryEncoding.EncodeLine(summary);
        var path = Path.Combine(_workspace, "descriptor-proof.jsonl");
        File.WriteAllBytes(path, bytes);
        var persisted = JsonSerializer.Deserialize<MonitoredSweepSummary>(
            File.ReadAllBytes(path), PrevalidationProtocol.Json)!;
        PrevalidationExecutor.ValidateSummaryFailure(persisted);
        persisted.FirstDescriptorFailure.Should().Equal(summary.FirstDescriptorFailure);
        _output.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int DuplicateDescriptor(int source, int destination);

    [Fact]
    public async Task PrevalidationTinyFixtureWritesAreObservedBeforeTheirHandlesClose()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = PrepareComponentExecution();
        using var self = Process.GetCurrentProcess();
        var scope = new PrevalidationObservationScope([],
            MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness),
            CurrentHistoryRoot: fixture.Manifest.HistoryRoot);
        var path = Path.Combine(fixture.Manifest.OutputRoot, "fixture-monitor.jsonl");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var during = new List<MonitoredSweepSummary>(4);
        await using (var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding, path,
            prevalidationScope: scope))
        {
            // Inventory-only component seam: do not exempt or classify the unrelated VSTest descriptors.
            // Each real write holds its handle open until a separate observer task completes its sweep.
            await Task.Run(() => PrevalidationLayout.CreateHistoryFixturesForComponent(
                fixture.Manifest.HistoryRoot, 1, () =>
                {
                    var descriptor = Directory.GetFiles($"/proc/{self.Id}/fd").Single(path =>
                        new FileInfo(path).LinkTarget?.StartsWith(fixture.Manifest.HistoryRoot + "/",
                            StringComparison.Ordinal) == true);
                    var sweep = Task.Run(async () =>
                    {
                        using var pinned = LinuxProcessDescriptorObserver.OpenCoherent(self.Id, descriptor, 4_096);
                        (pinned.Snapshot.RegularFileMetadata?.Length).Should().Be(512);
                        return await monitor.ObserveBoundaryAsync("fixture-write-open", true, deadline.Token);
                    }, deadline.Token).GetAwaiter().GetResult();
                    PrevalidationExecutor.RequireSweep(sweep);
                    during.Add(sweep.Summary);
                }, deadline.Token), deadline.Token);
            var after = await monitor.ObserveBoundaryAsync("fixtures-complete", true, deadline.Token);
            PrevalidationExecutor.RequireSweep(after);
            PrevalidationExecutor.RequireMonitor(monitor);
            after.Summary.RetainedHistoryBytes.Should().Be(2_048);
        }
        during.Select(item => item.RetainedHistoryBytes).Should().Equal(512L, 1_024L, 1_536L, 2_048L);
        PrevalidationGeometry.CountFixtureFiles(fixture.Manifest.HistoryRoot).Should().Be(4);
        File.ReadAllLines(path).Should().HaveCount(5);
    }

    [Theory]
    [InlineData("Admission")]
    [InlineData("Finalization")]
    [InlineData("Entry")]
    public void PrevalidationInspectionPreservesPrimaryAndSecondaryFailureSurfaces(string stage)
    {
        if (!OperatingSystem.IsLinux()) return;
        var manifest = PrevalidationContractFixture();
        Directory.CreateDirectory(manifest.PrivateRoot);
        var manifestPath = Path.Combine(_workspace, "inspection-manifest.json");
        PrevalidationExecutor.WriteImmutable(manifestPath, manifest);
        var outcomes = manifest.Probes.Select(probe => PrevalidationExecutor.Failed(probe, "SuiteStopped")
            with { Outcome = "not-run", ObservedFixtureFiles = 0 }).ToArray();
        if (stage == "Entry")
        {
            outcomes[0] = PrevalidationExecutor.RecordFailure(
                PrevalidationExecutor.Failed(manifest.Probes[0], "DescriptorObservationUnavailable"),
                "PrevalidationCleanupErrors", "entry-cleanup");
        }
        var report = new PrevalidationReport(PrevalidationProtocol.ReportSchema, PrevalidationProtocol.Scope,
            manifest.SuiteId, MonitoredFile.HashFile(manifestPath), manifest.HistoricalReport.Sha256,
            $"partial-unsealed:{stage}", PrevalidationLayout.DeriveSuiteIdentityBound(), outcomes, "component-only");
        report = PrevalidationExecutor.RecordFailure(report, "DescriptorObservationUnavailable",
            stage == "Entry" ? "entry" : "admission");
        report = PrevalidationExecutor.RecordFailure(report, "IOException", "suite-dispose");
        report = PrevalidationExecutor.RecordFailure(report, "PrevalidationFinalizationDeadline", "suite-finalization");
        report.FailureCode.Should().Be("DescriptorObservationUnavailable");
        report.SecondaryFailures.Should().Be(new PrevalidationSecondaryFailures("IOException", "suite-dispose", 2));
        PrevalidationExecutor.WriteImmutable(Path.Combine(manifest.PrivateRoot, "partial.json"), report);
        var inspection = PrevalidationReportValidation.Validate(manifestPath);
        inspection.FailureCode.Should().Be(report.FailureCode);
        inspection.FailureStage.Should().Be(report.FailureStage);
        inspection.SecondaryFailures.Should().Be(report.SecondaryFailures);
        inspection.CampaignAdmissionGranted.Should().BeFalse();
        if (stage == "Entry")
        {
            inspection.FirstFailedProbe!.FailureCode.Should().Be(report.FailureCode);
            inspection.FirstFailedProbe.SecondaryFailures.Should().Be(new PrevalidationSecondaryFailures(
                "PrevalidationCleanupErrors", "entry-cleanup", 1));
        }
        Action overwritten = () => PrevalidationReportValidation.ValidateReport(manifest,
            MonitoredFile.HashFile(manifestPath), report with { FailureCode = null });
        overwritten.Should().Throw<DurableStorageExperimentException>();
    }

    [Fact]
    public void PrevalidationHistoricalInspectionKeepsMissingCausalityUnknown()
    {
        if (!OperatingSystem.IsLinux()) return;
        var manifest = PrevalidationContractFixture() with
        {
            ContextSummaryFieldMapSha256 = PrevalidationProtocol.LegacyContextSummaryFieldMapSha256,
        };
        Directory.CreateDirectory(manifest.PrivateRoot);
        var manifestPath = Path.Combine(_workspace, "historical-manifest.json");
        PrevalidationExecutor.WriteImmutable(manifestPath, manifest);
        var outcomes = manifest.Probes.Select(probe => PrevalidationExecutor.Failed(probe, "SuiteStopped")
            with { Schema = "durable-prevalidation-coverage/1", Outcome = "not-run", FailureStage = null }).ToArray();
        outcomes[0] = outcomes[0] with
        {
            Outcome = "incomplete", FailureCode = "Cleanup-PrevalidationFinalMonitoringIncomplete",
        };
        var report = new PrevalidationReport("durable-prevalidation-report/1", PrevalidationProtocol.Scope,
            manifest.SuiteId, MonitoredFile.HashFile(manifestPath), manifest.HistoricalReport.Sha256,
            "partial-unsealed:PrevalidationOwnedCleanupOrFreezeIncomplete",
            PrevalidationLayout.DeriveSuiteIdentityBound(), outcomes, "synthetic legacy compatibility fixture");
        PrevalidationExecutor.WriteImmutable(Path.Combine(manifest.PrivateRoot, "partial.json"), report);
        var inspection = PrevalidationReportValidation.Validate(manifestPath);
        inspection.FailureCode.Should().BeNull();
        inspection.SecondaryFailures.Should().BeNull();
        inspection.FirstFailedProbe!.FailureCode.Should().Be(outcomes[0].FailureCode);
        inspection.Status.Should().Be("partial-unsealed-not-readiness-evidence");
        inspection.CampaignAdmissionGranted.Should().BeFalse();
        Action mixedSchema = () => PrevalidationReportValidation.ValidateReport(manifest,
            MonitoredFile.HashFile(manifestPath), report with { Schema = PrevalidationProtocol.ReportSchema });
        mixedSchema.Should().Throw<DurableStorageExperimentException>();
    }

    [Fact]
    public async Task PrevalidationTinyInventoryChargesPriorRootsWithoutSpendingCurrentContextIdentities()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var fixture = PrepareComponentExecution();
        var previous = Path.Combine(fixture.Manifest.HistoryRoot, "previous");
        Directory.CreateDirectory(previous);
        File.WriteAllBytes(Path.Combine(previous, "old.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(fixture.Manifest.WorkspaceRoot, "current.bin"), [4, 5]);
        using var self = Process.GetCurrentProcess();
        var scope = new PrevalidationObservationScope([previous],
            MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness));
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "tiny-monitor.jsonl"), maximumEstablishedIdentities: 2,
            prevalidationScope: scope);
        // No VSTest process is registered in this deterministic inventory-only component test.
        var sweep = monitor.Sweep();
        sweep.Summary.Complete.Should().BeTrue();
        sweep.Summary.IdentityCount.Should().Be(3);
        sweep.Summary.CurrentContextIdentities.Should().Be(2);
        sweep.Summary.CurrentContextRootedIdentities.Should().Be(2);
        sweep.Summary.ObservedSweepBytes.Should().Be(5);
        File.WriteAllBytes(Path.Combine(fixture.Manifest.WorkspaceRoot, "over-limit.bin"), [6]);
        var over = monitor.Sweep();
        over.Summary.Complete.Should().BeFalse();
        over.Errors.Should().Contain("EstablishedIdentityGeometryExceeded");
    }

    [Fact]
    public void PrevalidationTinyFixturesAreImmutableInventoryNotPackagesAndCannotBeRetried()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var root = Path.Combine(_workspace, "tiny-history");
        Directory.CreateDirectory(root);
        PrevalidationLayout.CreateHistoryFixtures(root, 1, CancellationToken.None);
        PrevalidationGeometry.CountFixtureFiles(root).Should().Be(4);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        files.Should().HaveCount(4);
        files.Should().OnlyContain(path => new FileInfo(path).Length == 512);
        files.Should().OnlyContain(path => (File.GetUnixFileMode(path) & UnixFileMode.UserWrite) == 0);
        Action retry = () => PrevalidationLayout.CreateHistoryFixtures(root, 1, CancellationToken.None);
        retry.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("PrevalidationFixtureSlotReuse");
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Should().HaveCount(4);
    }

    [Fact]
    public async Task PrevalidationUnsealedSourceIsChargedButNotRetainedHistory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var fixture = PrepareComponentExecution();
        var source = Path.Combine(fixture.Manifest.WorkspaceRoot, "package-staging");
        var recovery = Path.Combine(fixture.Manifest.HistoryRoot, "recovery");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(recovery);
        File.WriteAllBytes(Path.Combine(source, "source.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(recovery, "recovered.bin"), [4, 5, 6, 7, 8]);
        using var self = Process.GetCurrentProcess();
        var scope = new PrevalidationObservationScope([],
            MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness),
            CurrentHistoryRoot: fixture.Manifest.HistoryRoot);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "tiny-history-monitor.jsonl"),
            maximumEstablishedIdentities: 571, prevalidationScope: scope);
        var sweep = monitor.Sweep();
        sweep.Summary.Complete.Should().BeTrue();
        sweep.Summary.PackageBytes.Should().Be(3);
        sweep.Summary.RecoveryBytes.Should().Be(5);
        sweep.Summary.RetainedHistoryBytes.Should().Be(5);
        sweep.Summary.ObservedSweepBytes.Should().Be(8);
    }

    [Fact]
    public void PrevalidationInternalRoutesRejectUnregisteredProcesses()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        Action unregistered = () => PrevalidationOwnership.RequireRegisteredSelf(
            Path.Combine(_workspace, "no-owned-processes.jsonl"), MonitoredProcessRole.Diagnostic);
        unregistered.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("PrevalidationUnregisteredInvocation");
    }

    [Fact]
    public void PrevalidationMissingBoundariesDoNotBecomeCoverageFromPassingHelpers()
    {
        var probe = PrevalidationProtocol.Plan()[0];
        var worker = CompleteWorker(probe.Execution, RequestMetrics(1)) with
        {
            ScheduledSourceOffers = 50_000, AttemptedSourceOffers = 50_000,
        };
        var outcome = new MonitoredCaseOutcome(probe.Ordinal, probe.Workload, probe.Candidate,
            "pass", null, null, true, null, 0, 0, 1, 1, [], worker);
        var coverage = PrevalidationExecutor.ProjectCoverage(probe, outcome, _workspace);
        PrevalidationExecutor.HasCoverage(coverage).Should().BeFalse();
        coverage.CampaignAdmissionGranted.Should().BeFalse();
        coverage.FailureCode.Should().Be("RequiredCoverageMissing");
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("control")]
    [InlineData("worker-exit")]
    [InlineData("cancel")]
    [InlineData("wrong-exit-boundary")]
    public async Task PrevalidationScriptedHarnessUsesExitHandshakeAndExactOwnedCleanup(string behavior)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var manifest = PrevalidationContractFixture();
        var component = PrepareComponentExecution();
        Directory.CreateDirectory(manifest.PrivateRoot);
        var probe = manifest.Probes[0];
        PrevalidationLayout.CreateDirectories(manifest, probe);
        var attribution = component.Attribution with
        {
            Roots = [new MonitoredAttributionRoot("evidence", manifest.PrivateRoot, true)],
        };
        var validated = new PrevalidationValidated(manifest, component.RepositoryRoot,
            component.ManifestPath, component.ManifestSha256, component.AuthorizationSha256,
            attribution, component.Encoding, component.ComponentEvidence);
        await using var monitor = new MonitoredStorageMonitor(attribution, component.Encoding,
            Path.Combine(PrevalidationLayout.ContextRoot(manifest, probe), "component-monitor.jsonl"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        MonitoredProcessIdentity? started = null;
        Process Launch(ProcessStartInfo unused)
        {
            var start = new ProcessStartInfo(behavior == "worker-exit" ? "/bin/bash" : "/bin/sh")
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(behavior switch
            {
                "control" => "printf '{\"operation\":\"boundary\",\"boundary\":\"component-authority\",\"process\":null,\"active\":false}\\n'; read reply; printf 'prevalidation-harness-exit\\n'; read release; test \"$release\" = prevalidation-release-exit",
                "normal" => "printf 'prevalidation-harness-exit\\n'; read release; test \"$release\" = prevalidation-release-exit",
                "worker-exit" => """
                    coproc WORKER {
                        read -r -a fields < "/proc/$BASHPID/stat"
                        printf '%s %s\n' "$BASHPID" "${fields[21]}"
                        read -r release
                        test "$release" = exit
                    }
                    child=$WORKER_PID
                    input=${WORKER[1]}
                    read -r pid ticks <&"${WORKER[0]}"
                    printf '{"operation":"register","process":{"processId":%s,"linuxStartTimeTicks":%s,"role":1}}\n' "$pid" "$ticks"
                    read -r reply || exit 41
                    printf '{"operation":"exit","process":{"processId":%s,"linuxStartTimeTicks":%s,"role":1}}\n' "$pid" "$ticks"
                    read -r reply || exit 42
                    printf 'exit\n' >&"$input"
                    wait "$child" || exit 43
                    printf '{"operation":"boundary","boundary":"after-controlled-worker-exit","active":false}\n'
                    read -r reply || exit 44
                    [[ "$reply" == *after-controlled-worker-exit* ]] || exit 45
                    printf 'prevalidation-harness-exit\n'
                    read -r release || exit 46
                    test "$release" = prevalidation-release-exit
                    """,
                "cancel" => "read release",
                _ => "printf 'wrong\\n'; read release",
            });
            var process = Process.Start(start)!;
            started = MonitoredProcessIdentity.Capture(process, MonitoredProcessRole.Harness);
            if (behavior == "cancel")
            {
                cancellation.Cancel();
            }
            return process;
        }
        // This explicit scripted seam never launches the benchmark harness or any O1/live/F3 workload.
        // Its monitor tracks the owned shell, not the VSTest host.
        Func<Task> run = () => PrevalidationExecutor.RunHarnessProcessForComponentAsync(
            validated, probe, monitor, Launch, cancellation.Token);
        if (behavior is "normal" or "control" or "worker-exit")
        {
            await run.Should().NotThrowAsync();
        }
        else
        {
            await run.Should().ThrowAsync<Exception>();
        }
        started.Should().NotBeNull();
        // An expired cleanup token still signals, but does not promise synchronous reaping.
        using (var confirmation = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var cleanup = await PrevalidationOwnership.StopAsync([started!],
                LinuxPrevalidationProcessOperations.Instance, confirmation.Token);
            cleanup.Quiescent.Should().BeTrue();
        }
        var owners = PrevalidationOwnership.Read(Path.Combine(PrevalidationLayout.ContextRoot(manifest, probe), "ownership.jsonl"));
        owners.Should().HaveCount(behavior == "worker-exit" ? 2 : 1).And.Contain(started!);
        owners.Should().OnlyContain(owner => !LinuxPrevalidationProcessOperations.Instance.IsOriginalAlive(owner));
        if (behavior is "control" or "worker-exit") monitor.SummaryRecords.Should().Be(1);
    }

    [Fact]
    public void PrevalidationReportRequiresRemainingEntriesNotRunAfterFirstFailure()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var manifest = PrevalidationContractFixture();
        var hash = HashText("component-report");
        var outcomes = manifest.Probes.Select(probe => new PrevalidationCoverage(
            PrevalidationProtocol.CoverageSchema, probe.Ordinal, probe.Id,
            probe.Ordinal == 1 ? "incomplete" : "not-run", "component-stop", false,
            probe.FixtureSlots, null, null, null, null, null, null, []) { FailureStage = "entry" }).ToArray();
        var report = new PrevalidationReport(PrevalidationProtocol.ReportSchema, PrevalidationProtocol.Scope,
            manifest.SuiteId, hash, manifest.HistoricalReport.Sha256, "stopped-incomplete",
            PrevalidationLayout.DeriveSuiteIdentityBound(), outcomes, "component-only")
        {
            FailureCode = "component-stop",
            FailureStage = "entry",
        };
        PrevalidationReportValidation.ValidateReport(manifest, hash, report);
        outcomes[1] = outcomes[1] with
        {
            Outcome = "coverage-observed", FailureCode = null, FailureStage = null, MonitoringComplete = true,
            ObservedFixtureFiles = 252, MaximumContextIdentities = 300, MaximumSuiteBytes = 512,
        };
        Action resumed = () => PrevalidationReportValidation.ValidateReport(manifest, hash, report);
        resumed.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("PrevalidationOutcomeOrderMismatch");
        Action approval = () => PrevalidationReportValidation.ValidateReport(manifest, hash,
            report with { CampaignAdmissionGranted = true });
        approval.Should().Throw<DurableStorageExperimentException>();
    }

    [Fact]
    public void PrevalidationPreservesFrozenO1SourceToleranceWithoutShorteningTheSchedule()
    {
        var probe = PrevalidationProtocol.Plan()[0];
        var worker = CompleteWorker(probe.Execution, RequestMetrics(1)) with
        {
            ScheduledSourceOffers = 50_000, AttemptedSourceOffers = 47_500, AchievedSourceOfferRatio = 0.95,
        };
        PrevalidationExecutor.HasFrozenSourceCoverage(probe, worker).Should().BeTrue();
        PrevalidationExecutor.HasFrozenSourceCoverage(probe,
            worker with { ScheduledSourceOffers = 10, AttemptedSourceOffers = 10, AchievedSourceOfferRatio = 1 })
            .Should().BeFalse();
        PrevalidationExecutor.HasFrozenSourceCoverage(probe,
            worker with { AttemptedSourceOffers = 47_499, AchievedSourceOfferRatio = 0.94998 })
            .Should().BeFalse();
    }

    [Fact]
    public void PrevalidationStockBaselinePreservesUnavailableRawTicksWithoutInventingCoverage()
    {
        var probe = PrevalidationProtocol.Plan()[2];
        var worker = CompleteWorker(probe.Execution, RequestMetrics(1)) with
        {
            SourceTicks = null,
            SourceCoverage = "shipping-eventpipe-counter-collector-first-latest-max;raw-tick-counts-unavailable",
            TargetStartedAt = DateTimeOffset.UtcNow,
            CounterSessionStartedAt = DateTimeOffset.UtcNow,
            CounterCollectionSeconds = 34,
        };
        PrevalidationExecutor.HasFrozenSourceCoverage(probe, worker).Should().BeTrue();
        PrevalidationExecutor.HasFrozenSourceCoverage(probe, worker with { SourceKeys = 0 }).Should().BeFalse();
        PrevalidationExecutor.HasFrozenSourceCoverage(probe, worker with { SourceTicks = 1 }).Should().BeFalse();
        PrevalidationExecutor.HasFrozenSourceCoverage(probe,
            worker with { Requests = worker.Requests! with { Scheduled = 599 } }).Should().BeFalse();
        PrevalidationExecutor.HasFrozenSourceCoverage(probe,
            worker with { Requests = worker.Requests! with { Scheduled = 601 } }).Should().BeFalse();
        PrevalidationExecutor.HasFrozenSourceCoverage(PrevalidationProtocol.Plan()[3], worker).Should().BeFalse();
    }

    [Fact]
    public void PrevalidationSingleDescriptorFixtureHasTheDeclaredNativeIdentityAndLength()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var fixture = PrevalidationGeometry.CreateDescriptorFixture(0);
        var native = LinuxStatxHandleMetadataObserver.Instance.Observe(fixture.SafeFileHandle);
        native.Length.Should().Be(512);
        native.LinkCount.Should().Be(0);
        var target = new FileInfo($"/proc/self/fd/{fixture.SafeFileHandle.DangerousGetHandle().ToInt64()}").LinkTarget;
        target.Should().Be("/memfd:dc5-pv-geometry-00 (deleted)");
        PrevalidationGeometry.IsDeclaredDescriptorTarget(target!).Should().BeTrue();
        PrevalidationGeometry.IsDeclaredDescriptorTarget("/memfd:sqlite-temp (deleted)").Should().BeFalse();
        PrevalidationGeometry.IsDeclaredDescriptorTarget("/memfd:dc5-pv-geometry-32 (deleted)").Should().BeFalse();
    }

    [Fact]
    public void PrevalidationCaptureIdentitiesStayInsideTheSuiteNamespace()
    {
        var suiteId = "pv-" + new string('s', 61);
        var identities = PrevalidationProtocol.Plan().Take(7).SelectMany(probe =>
            new[] { MonitoredWorkerMode.Execute, MonitoredWorkerMode.Recover }.SelectMany(mode =>
                new[] { "capture", "artifact" }.Select(kind =>
                    PrevalidationLayout.OwnedIdentity(suiteId, probe.Execution, mode, kind)))).ToArray();
        identities.Should().HaveCount(28).And.OnlyHaveUniqueItems();
        identities.Should().OnlyContain(value => value.StartsWith(suiteId + "-", StringComparison.Ordinal)
            && value.Length <= 128);
    }

    [Fact]
    public async Task PrevalidationSinglePeriodicLoopFitsFullVirtual120SecondsAndAllBoundaryHeadroom()
    {
        var budget = new BoundedOutputBudget(2_048L * 1_024, 2_048, 64, 64);
        using var deadline = new CancellationTokenSource();
        var elapsed = TimeSpan.Zero;
        var periodic = 0;
        Task Observe(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            periodic++;
            budget.ConsumeSummary(1_024, boundary: false);
            return Task.CompletedTask;
        }
        Task Delay(TimeSpan interval, CancellationToken token)
        {
            interval.Should().Be(TimeSpan.FromMilliseconds(100));
            token.Should().Be(deadline.Token);
            elapsed += interval;
            if (elapsed > TimeSpan.FromSeconds(120)) deadline.Cancel();
            return Task.CompletedTask;
        }
        Func<Task> loop = () => MonitoredStorageMonitor.RunPeriodicLoopAsync(Observe, Delay, deadline.Token);
        await loop.Should().ThrowAsync<OperationCanceledException>();
        periodic.Should().Be(PrevalidationMonitorControl.MaximumPeriodicRecords).And.Be(1_201);
        for (var index = 0; index < 64; index++) budget.ConsumeSummary(1_024, boundary: true);
        budget.SummaryRecords.Should().Be(PrevalidationMonitorControl.FullWindowRecords).And.Be(1_265);
        budget.SummaryBytes.Should().Be(1_295_360).And.BeLessThan(2_097_152);
        PrevalidationMonitorControl.FullWindowOutputBytes.Should().Be(2_876_736).And.BeLessThan(8_388_608);
        // Each of the 64 worker controls needs at most three authority operations;
        // two launches and two exit/status pairs still fit the shared frame budget.
        (64 * 3 + 2 + 4).Should().BeLessThan(PrevalidationMonitorControl.MaximumFrames);
    }

    [Fact]
    public async Task PrevalidationControlFramesAreBoundedAndWorstSummaryReplyFits()
    {
        var summary = MonitoredSweepSummaryEncoding.CreateWorstCaseFixture() with
        {
            CurrentContextIdentities = int.MaxValue, CurrentContextRootedIdentities = int.MaxValue,
            RetainedHistoryBytes = long.MaxValue,
            FirstErrorCode = new string('e', 64),
            FirstDescriptorFailure = [2, 4, int.MinValue, int.MaxValue],
        };
        var reply = new PrevalidationMonitorReply(false, new string('a', 64), int.MaxValue, long.MaxValue,
            int.MaxValue, long.MaxValue, summary);
        var encoded = PrevalidationMonitorControl.Encode(reply);
        encoded.Should().NotContain("\n");
        System.Text.Encoding.UTF8.GetByteCount(encoded).Should().BeLessThan(2_048);
        var roundtrip = await PrevalidationMonitorControl.ReadLineAsync(new StringReader(encoded + "\n"),
            CancellationToken.None);
        roundtrip.Should().Be(encoded);
        Func<Task> oversized = () => PrevalidationMonitorControl.ReadLineAsync(
            new StringReader(new string('x', 2_048) + "\n"), CancellationToken.None);
        await oversized.Should().ThrowAsync<DurableStorageExperimentException>();
        Func<Task> truncated = () => PrevalidationMonitorControl.ReadLineAsync(new StringReader(encoded),
            CancellationToken.None);
        await truncated.Should().ThrowAsync<DurableStorageExperimentException>();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task PrevalidationCleanupSignalsAllBeforeSharedDeadlineConfirmation(
        bool initiallyCancelled, bool firstSignalFails, bool firstIdentityUnknown)
    {
        var identities = Enumerable.Range(1, 3)
            .Select(pid => new MonitoredProcessIdentity(pid, (ulong)pid, MonitoredProcessRole.Diagnostic)).ToArray();
        using var deadline = new CancellationTokenSource();
        if (initiallyCancelled) deadline.Cancel();
        var signals = new List<int>();
        var delays = 0;
        var operations = new ScriptedCleanupOperations(
            identity => identity.ProcessId == 3 && firstIdentityUnknown
                ? throw new UnauthorizedAccessException("controlled identity failure") : true,
            identity =>
            {
                signals.Add(identity.ProcessId);
                if (identity.ProcessId == 3 && firstSignalFails)
                    throw new IOException("controlled signal failure");
            },
            token =>
            {
                signals.Should().Equal(firstIdentityUnknown ? [2, 1] : [3, 2, 1]);
                token.Should().Be(deadline.Token);
                delays++;
                deadline.Cancel();
                return Task.FromCanceled(token);
            });
        var result = await PrevalidationOwnership.StopAsync(identities, operations, deadline.Token);
        signals.Should().Equal(firstIdentityUnknown ? [2, 1] : [3, 2, 1]);
        delays.Should().Be(initiallyCancelled ? 0 : 1);
        result.Quiescent.Should().BeFalse();
        result.Unconfirmed.Should().BeEquivalentTo(identities);
        result.Errors.Any().Should().Be(firstSignalFails || firstIdentityUnknown);
    }

    [LinuxOnlyFact]
    public async Task PrevalidationExpiredCleanupSignalsEveryRealOwnedStubWithoutPerProcessWaits()
    {
        using var first = StartWaitingStub();
        using var second = StartWaitingStub();
        using var third = StartWaitingStub();
        var children = new[] { first, second, third };
        var identities = children.Select(process =>
            MonitoredProcessIdentity.Capture(process, MonitoredProcessRole.Diagnostic)).ToArray();
        try
        {
            var result = await PrevalidationOwnership.StopAsync(identities,
                LinuxPrevalidationProcessOperations.Instance, new CancellationToken(canceled: true));
            result.Errors.Should().BeEmpty();
            // Test teardown reaps the already-signalled stubs; this is not an executor wait extension.
            await Task.WhenAll(children.Select(process => process.WaitForExitAsync()))
                .WaitAsync(TimeSpan.FromSeconds(2));
            children.Should().OnlyContain(process => process.HasExited);
            var confirmed = await PrevalidationOwnership.StopAsync(identities,
                LinuxPrevalidationProcessOperations.Instance, new CancellationToken(canceled: true));
            confirmed.Quiescent.Should().BeTrue();
            confirmed.Unconfirmed.Should().BeEmpty();
        }
        finally
        {
            foreach (var identity in identities) LinuxPrevalidationProcessOperations.Instance.Signal(identity);
        }
    }

    [LinuxOnlyFact]
    public async Task PrevalidationReusedPidIsNeverSignalledAndExitedOriginalIsAlreadyQuiescent()
    {
        using var process = StartWaitingStub();
        var identity = MonitoredProcessIdentity.Capture(process, MonitoredProcessRole.Target);
        try
        {
            var reused = identity with { LinuxStartTimeTicks = identity.LinuxStartTimeTicks + 1 };
            var result = await PrevalidationOwnership.StopAsync([reused],
                LinuxPrevalidationProcessOperations.Instance, new CancellationToken(canceled: true));
            result.Quiescent.Should().BeTrue();
            process.HasExited.Should().BeFalse();
            LinuxPrevalidationProcessOperations.Instance.Signal(identity);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            // Covers disappearance between initial ownership observation and acquiring a process handle.
            LinuxPrevalidationProcessOperations.Instance.Signal(identity);
            LinuxPrevalidationProcessOperations.Instance.IsOriginalAlive(identity).Should().BeFalse();
        }
        finally { LinuxPrevalidationProcessOperations.Instance.Signal(identity); }
    }

    [LinuxOnlyFact]
    public void PrevalidationUnconfirmedCleanupCannotFreezeOrSealMutableEvidence()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "partial-mutable");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "still-writable.bin");
        File.WriteAllText(path, "component");
        var original = File.GetUnixFileMode(path);
        Action freeze = () => PrevalidationExecutor.FreezeQuiescentContext(root, quiescent: false);
        freeze.Should().Throw<DurableStorageExperimentException>().Which.Code
            .Should().Be("PrevalidationMutableContextNotFrozen");
        File.GetUnixFileMode(path).Should().Be(original);
        File.Exists(Path.Combine(root, "seal.json")).Should().BeFalse();
        File.AppendAllText(path, "-still-writable");
    }

    [LinuxOnlyFact]
    public async Task PrevalidationAuthoritativeOwnerTracksAndReleasesSourceBeforeRecovery()
    {
        var fixture = PrepareComponentExecution();
        using var coordinator = StartWaitingStub();
        using var harness = StartWaitingStub();
        using var worker = StartWaitingStub();
        using var target = StartWaitingStub();
        using var recovery = StartWaitingStub();
        var harnessId = MonitoredProcessIdentity.Capture(harness, MonitoredProcessRole.Harness);
        var workerId = MonitoredProcessIdentity.Capture(worker, MonitoredProcessRole.Diagnostic);
        var targetId = MonitoredProcessIdentity.Capture(target, MonitoredProcessRole.Target);
        var recoveryId = MonitoredProcessIdentity.Capture(recovery, MonitoredProcessRole.Diagnostic);
        var coordinatorId = MonitoredProcessIdentity.Capture(coordinator, MonitoredProcessRole.Harness);
        var identities = new[] { harnessId, workerId, targetId, recoveryId };
        var journal = Path.Combine(fixture.Manifest.OutputRoot, "ownership.jsonl");
        var scope = new PrevalidationObservationScope([], coordinatorId, journal);
        await using var monitor = new MonitoredStorageMonitor(fixture.Attribution, fixture.Encoding,
            Path.Combine(fixture.Manifest.OutputRoot, "owner-monitor.jsonl"), prevalidationScope: scope);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            monitor.AddProcess(coordinatorId);
            monitor.AddProcess(harnessId);
            PrevalidationOwnership.Append(journal, harnessId);
            await PrevalidationMonitorControl.DispatchAsync(new("register", workerId), monitor, journal, deadline.Token);
            await PrevalidationMonitorControl.DispatchAsync(new("register", targetId), monitor, journal, deadline.Token);
            var before = await PrevalidationMonitorControl.DispatchAsync(
                new("boundary", Boundary: "component-source", Active: true), monitor, journal, deadline.Token);
            before.Summary!.Complete.Should().BeTrue();
            harness.Refresh();
            coordinator.Refresh();
            before.Summary.HarnessPeakRssBytes.Should().Be(harness.WorkingSet64 + coordinator.WorkingSet64);
            before.Summary.DiagnosticPeakRssBytes.Should().BePositive();
            before.Summary.TargetPeakRssBytes.Should().BePositive();
            await PrevalidationMonitorControl.DispatchAsync(new("kill", workerId), monitor, journal, deadline.Token);
            var post = await PrevalidationMonitorControl.DispatchAsync(
                new("boundary", Boundary: "post-kill-quiescent-root-inventory"), monitor, journal, deadline.Token);
            post.Summary!.Complete.Should().BeTrue();
            post.Summary.DiagnosticCpuTicks.Should().BeGreaterThanOrEqualTo(before.Summary.DiagnosticCpuTicks);
            await PrevalidationMonitorControl.DispatchAsync(new("register", recoveryId), monitor, journal, deadline.Token);
            var recovered = await PrevalidationMonitorControl.DispatchAsync(
                new("boundary", Boundary: "component-recovery", Active: true), monitor, journal, deadline.Token);
            recovered.Summary!.Complete.Should().BeTrue();
            recovered.Records.Should().Be(3);
            PrevalidationOwnership.Read(journal).Should().Equal(identities);
            monitor.IsIncomplete.Should().BeFalse();
        }
        finally
        {
            MonitoredProcessIdentity[] all = [coordinatorId, .. identities];
            monitor.MarkOwnedCleanup(all);
            var result = await PrevalidationOwnership.StopAsync(all,
                LinuxPrevalidationProcessOperations.Instance, deadline.Token);
            result.Quiescent.Should().BeTrue();
            monitor.RemoveConfirmedCleanup(all);
        }
    }

    [Fact]
    public async Task PrevalidationConfirmationFailureRetainsEveryUnconfirmedIdentityAfterAllSignals()
    {
        var identities = Enumerable.Range(1, 3)
            .Select(pid => new MonitoredProcessIdentity(pid, (ulong)pid, MonitoredProcessRole.Diagnostic)).ToArray();
        var signals = new List<int>();
        var operations = new ScriptedCleanupOperations(_ => true, identity => signals.Add(identity.ProcessId),
            _ => throw new IOException("controlled aggregate wait failure"));
        var result = await PrevalidationOwnership.StopAsync(identities, operations, CancellationToken.None);
        signals.Should().Equal(3, 2, 1);
        result.Quiescent.Should().BeFalse();
        result.Unconfirmed.Should().BeEquivalentTo(identities);
        result.Errors.Should().Contain("confirmation-wait:IOException:hr-80131620");
    }

    [Fact]
    public void PrevalidationJournalFailureDoesNotLoseInMemoryOwnership()
    {
        var ledger = new PrevalidationOwnedProcessLedger();
        var identity = new MonitoredProcessIdentity(123, 456, MonitoredProcessRole.Diagnostic);
        Action register = () => ledger.Register(_workspace, identity);
        register.Should().Throw<UnauthorizedAccessException>();
        ledger.Identities.Should().ContainSingle().Which.Should().Be(identity);
    }

    private static Process StartWaitingStub()
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("read release");
        return Process.Start(start)!;
    }

    private sealed class ControlledDerivedIOException() : IOException("controlled", 3);

    private sealed class ScriptedCleanupOperations(
        Func<MonitoredProcessIdentity, bool> alive, Action<MonitoredProcessIdentity> signal,
        Func<CancellationToken, Task> delay) : IPrevalidationProcessOperations
    {
        public bool IsOriginalAlive(MonitoredProcessIdentity identity) => alive(identity);
        public void Signal(MonitoredProcessIdentity identity) => signal(identity);
        public Task DelayAsync(CancellationToken cancellationToken) => delay(cancellationToken);
    }

    private PrevalidationManifest PrevalidationContractFixture()
    {
        var fixture = PrepareComponentExecution();
        var manifest = fixture.Manifest;
        var artifact = new MonitoredBinaryIdentity(fixture.ManifestPath, fixture.ManifestSha256);
        return new(PrevalidationProtocol.ManifestSchema, PrevalidationProtocol.Scope, "pv-component-test",
            Path.Combine(_workspace, "pv-component-test"), PrevalidationProtocol.AddendumCommit,
            PrevalidationProtocol.AddendumSha256, MonitoredProtocolVersions.SuccessorProtocolSha256,
            PrevalidationProtocol.Plan(), PrevalidationProtocol.Bounds(), manifest.SourceCommits,
            manifest.RuntimeBinary, manifest.ToolBinary, manifest.SampleBinary, [manifest.ToolBinary],
            manifest.NativeBinaries, artifact, manifest.Host, manifest.Clock, artifact, artifact,
            artifact, artifact, artifact, artifact, manifest.AuthorizationReceipt, PrevalidationProtocol.RuntimeEnvironmentHash(),
            PrevalidationProtocol.ContextSummaryFieldMapSha256);
    }

    private static IReadOnlyList<MonitoredCaseOutcome> LiveP95Outcomes(
        IReadOnlyList<double> baselines,
        IReadOnlyList<double> increases)
    {
        var liveCases = new[] { "L1", "L2", "L3" };
        var baselineP95 = liveCases
            .Select((caseId, index) => (caseId, value: baselines[index]))
            .ToDictionary(static item => item.caseId, static item => item.value, StringComparer.Ordinal);
        var candidateP95 = liveCases
            .Select((caseId, index) => (caseId, value: baselines[index] + increases[index]))
            .ToDictionary(static item => item.caseId, static item => item.value, StringComparer.Ordinal);
        return MonitoredExecutionPlanner.Expand()
            .Select(execution =>
            {
                var p95 = execution.CaseId.StartsWith('L')
                    ? execution.Candidate == "E"
                        ? baselineP95[execution.CaseId]
                        : candidateP95[execution.CaseId]
                    : 2;
                return new MonitoredCaseOutcome(
                    execution.Ordinal,
                    execution.CaseId,
                    execution.Candidate,
                    "pass",
                    null,
                    null,
                    true,
                    null,
                    1,
                    1,
                    1,
                    1,
                    ["monitor.jsonl"],
                    CompleteWorker(execution, RequestMetrics(p95)));
            })
            .ToArray();
    }

    private static MonitoredWorkerResult CompleteWorker(
        MonitoredExecutionSpec execution,
        MonitoredRequestMetrics requests)
        => new(
            MonitoredProtocolVersions.WorkerResultSchema,
            execution.Ordinal,
            execution.CaseId,
            execution.Candidate,
            "pass",
            FailureCode: null,
            FailureMessage: null,
            Offered: 128,
            Admitted: 128,
            Rejected: 0,
            Committed: 128,
            FailedAfterAdmission: 0,
            UnknownCommitOutcome: 0,
            SourceMalformedPayloads: 0,
            SourceAdmissionInvalid: 0,
            SourceRejectedNewKeys: 0,
            SourceAdmissionRejected: 0,
            LogicalCommittedBytes: 128,
            PackageFinalBytes: 128,
            OfferSeconds: 1,
            DrainSeconds: 1,
            FinalizationSeconds: 1,
            ReopenAndFirstQuerySeconds: 1,
            DiagnosticCpuSeconds: 1,
            StorageFaultTriggered: execution.CaseId.StartsWith('F'),
            ConcurrentCaptureRejected: execution.CaseId == "F5",
            RecoveryInvariantSatisfied: true,
            RecoveredRecords: 128,
            Requests: requests,
            SourceTicks: 1,
            SourceKeys: 1,
            ScheduledSourceOffers: null,
            AttemptedSourceOffers: null,
            AchievedSourceOfferRatio: null,
            SourceCoverage: "test",
            TargetStartedAt: null,
            CounterSessionStartedAt: null,
            CounterCollectionSeconds: null,
            HasDurablePackage: execution.Candidate != "E",
            RecommendationScope: "monitored-scope-only");

    private static MonitoredRequestMetrics RequestMetrics(double p95)
        => new(
            Scheduled: 600,
            SkippedAtConcurrencyLimit: 0,
            Completed: 600,
            Succeeded: 600,
            Failed: 0,
            RetainedSamples: 600,
            P50Milliseconds: p95 / 2,
            P95Milliseconds: p95,
            EpisodeScheduled: 880,
            EpisodeSkippedAtConcurrencyLimit: 0,
            EpisodeCompleted: 880,
            EpisodeSucceeded: 880,
            EpisodeFailed: 0,
            SchedulingElapsedSeconds: 44,
            EpisodeElapsedSeconds: 44);

    private MonitoredValidatedManifest PrepareComponentExecution()
    {
        var fixture = WriteResolvedManifest(authorizationApproved: true);
        var validated = MonitoredRunManifestValidator.Validate(
            fixture.RepositoryRoot,
            fixture.ManifestPath,
            requireAuthorization: true);
        foreach (var directory in new[]
        {
            validated.CampaignRoot,
            validated.Manifest.HistoryRoot,
            validated.Manifest.WorkspaceRoot,
            validated.Manifest.OutputRoot,
        })
        {
            Directory.CreateDirectory(directory);
            MonitoredFile.MakePrivateDirectory(directory);
        }
        return validated;
    }

    private ManifestFixture WriteResolvedManifest(bool authorizationApproved)
    {
        var repositoryRoot = FindRepositoryRoot();
        var fixturePath = Path.Combine(
            repositoryRoot,
            "tests/DotnetDiagnostics.Core.Tests/DurableCounterSpike/durable-counter-fixture-manifest.json");
        using var fixtureDocument = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        var fixture = fixtureDocument.RootElement;

        var evidenceRoot = Path.Combine(_workspace, $"evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidenceRoot);
        MonitoredFile.MakePrivateDirectory(evidenceRoot);
        var campaignId = $"campaign-{Guid.NewGuid():N}";
        var campaignRoot = Path.Combine(evidenceRoot, campaignId);
        var history = Path.Combine(campaignRoot, "history");
        var workspace = Path.Combine(campaignRoot, "workspace");
        var outputs = Path.Combine(campaignRoot, "outputs");
        var attributionPath = Path.Combine(_workspace, $"{Guid.NewGuid():N}-attribution.json");
        var encodingPath = Path.Combine(_workspace, $"{Guid.NewGuid():N}-encoding.json");
        var componentPath = Path.Combine(_workspace, $"{Guid.NewGuid():N}-component.json");
        var authorizationPath = Path.Combine(_workspace, $"{Guid.NewGuid():N}-authorization.json");
        var manifestPath = Path.Combine(_workspace, $"{Guid.NewGuid():N}-manifest.json");
        var attribution = Attribution(
            evidenceRoot,
            history,
            workspace,
            outputs,
            includeTestHostDependencies: false);
        var encoding = EncodingContract();
        MonitoredFile.WriteNewJson(attributionPath, attribution);
        MonitoredFile.WriteNewJson(encodingPath, encoding);

        var commits = new MonitoredSourceCommits(
            CommitIdentity("protocol"),
            CommitIdentity("pipeline"),
            CommitIdentity("adapter"),
            CommitIdentity("runner"),
            CommitIdentity("monitor"));
        var rawEvidenceRoot = Path.Combine(_workspace, $"{Guid.NewGuid():N}-component-raw");
        Directory.CreateDirectory(rawEvidenceRoot);
        File.WriteAllBytes(Path.Combine(rawEvidenceRoot, "proof.bin"), [1]);
        var worstCaseSummary = MonitoredSweepSummaryEncoding.EncodeLine(
            MonitoredSweepSummaryEncoding.CreateWorstCaseFixture());
        const string worstCaseRelativePath = "worst-case-monitor-summary.jsonl";
        File.WriteAllBytes(
            Path.Combine(rawEvidenceRoot, worstCaseRelativePath),
            worstCaseSummary);
        MonitoredPackagePublisher.MakeImmutable(rawEvidenceRoot);
        var rawInventory = MonitoredFile.HashTreeInventory(rawEvidenceRoot, 4_096, 4_096);
        var component = new MonitoredComponentEvidence(
            MonitoredProtocolVersions.ComponentEvidenceSchema,
            DateTimeOffset.UtcNow,
            "Linux",
            commits.RunnerCommit,
            commits.MonitorCommit,
            HashFile(attributionPath),
            MonitoredSweepSummaryEncoding.Schema,
            MonitoredSweepSummaryEncoding.FieldMapSha256,
            DerivedMaximumSimultaneousIdentitiesEnforced:
                MonitoredRunnerGeometry.MaximumSimultaneousIdentities,
            DerivedMaximumRootedIdentities:
                MonitoredRunnerGeometry.MaximumRootedIdentities,
            MaximumDescriptorOnlyIdentitiesEnforced:
                MonitoredRunnerGeometry.MaximumOwnedWritableDescriptorOnlyFiles,
            RepresentativeMaximumRootedIdentitiesObserved: 8,
            RepresentativeMaximumDescriptorOnlyIdentitiesObserved: 2,
            DescriptorOnlyCampaignFeasibilityEstablished: true,
            MaximumSummaryUtf8BytesObserved: 700,
            WorstCaseSummaryUtf8Bytes: worstCaseSummary.Length,
            WorstCaseSummarySha256: MonitoredFile.HashBytes(worstCaseSummary),
            WorstCaseSummaryRelativePath: worstCaseRelativePath,
            MaximumWorkerControlUtf8BytesObserved: 900,
            DerivedMaximumSummaryRecordsRequired:
                MonitoredRunnerGeometry.MaximumRequiredSummaryRecords,
            MaximumSummaryRecordsEncoded: 2_048,
            DerivedMaximumCombinedSummaryControlBytes: checked(
                MonitoredRunnerGeometry.MaximumMonitorSummaryBytes
                + MonitoredRunnerGeometry.MaximumWorkerControlRecordsPerExecution * 900L),
            SourceRecoveryObservedSummaryRecords: 4,
            SourceRecoveryObservedSummaryBytes: 2_800,
            SourceRecoveryObservedControlRecords: 2,
            SourceRecoveryObservedControlBytes: 1_800,
            SourceRecoverySharedBudgetExhaustionObserved: true,
            SourceRecoverySharedCancellationObserved: true,
            PeriodicSummariesObserved: 3,
            MaximumPeriodicGapMillisecondsObserved: 100,
            MaximumObservedSweepBytes: 128,
            PositiveMonitoringComplete: true,
            ManagedProcessObserved: true,
            RuntimeMemoryClassificationObserved: true,
            RuntimeMemoryExcludedBytes: 1,
            RootFileObserved: true,
            WritableDescriptorOutsideRootRejected: true,
            OpenUnlinkedDescriptorRejected: true,
            ProcessIdentityReuseRejected: true,
            KillHandoffReleasedObserverReferences: true,
            RepresentativeCandidatePackageInventoriesObserved: true,
            RepresentativeMaximumFinalPackageFilesObserved: 4,
            RepresentativeMaximumTransientPackageFilesObserved: 6,
            RepresentativeSqliteWalShmObserved: true,
            OutputLimitEnforced: true,
            IdentityLimitEnforced: true,
            RawEvidenceRoot: rawEvidenceRoot,
            RawEvidenceFiles: rawInventory.FileCount,
            EvidenceSha256: rawInventory.Sha256);
        MonitoredFile.WriteNewJson(componentPath, component);
        var protocolRelative = MonitoredProtocolVersions.SuccessorProtocolPath;
        var protocolHash = MonitoredProtocolVersions.SuccessorProtocolSha256;

        var runtime = Environment.ProcessPath
            ?? throw new InvalidOperationException("Test process path was unavailable.");
        var tool = typeof(MonitoredRunnerTests).Assembly.Location;
        var sample = SampleLocator.LocateSampleDll("CoreClrSample")
            ?? throw new InvalidOperationException("CoreClrSample build output is required.");
        var plan = MonitoredExecutionPlanner.CreatePlan(repositoryRoot, protocolRelative);
        var manifest = new MonitoredRunManifest(
            MonitoredProtocolVersions.ManifestSchema,
            campaignId,
            plan,
            Path.GetRelativePath(repositoryRoot, fixturePath).Replace(Path.DirectorySeparatorChar, '/'),
            HashFile(fixturePath),
            fixture.GetProperty("q1InputSha256").GetString()!,
            fixture.GetProperty("q1OracleSha256").GetString()!,
            fixture.GetProperty("q2InputSha256").GetString()!,
            fixture.GetProperty("q2OracleSha256").GetString()!,
            commits,
            Identity(runtime),
            Identity(tool),
            Identity(sample),
            [Identity(runtime), attribution.RuntimeOnlyDescriptorProofs[0].RuntimeNativeBinary],
            MonitoredHostFactsReader.Read(evidenceRoot),
            new MonitoredClockDefinition(
                "System.Diagnostics.Stopwatch",
                System.Diagnostics.Stopwatch.Frequency,
                "TraceEvent.TimeStampRelativeMSec",
                "checked(round(relativeMilliseconds*10000)) to 100ns ticks",
                "MidpointRounding.AwayFromZero"),
            attributionPath,
            HashFile(attributionPath),
            encodingPath,
            HashFile(encodingPath),
            componentPath,
            HashFile(componentPath),
            evidenceRoot,
            history,
            workspace,
            outputs,
            authorizationPath);
        MonitoredFile.WriteNewJson(manifestPath, manifest);
        var manifestHash = HashFile(manifestPath);
        MonitoredFile.WriteNewJson(
            authorizationPath,
            new MonitoredAuthorizationReceipt(
                MonitoredProtocolVersions.AuthorizationSchema,
                campaignId,
                manifestHash,
                protocolHash,
                commits.RunnerCommit,
                commits.MonitorCommit,
                "independent-test-reviewer",
                DateTimeOffset.UtcNow,
                ExecutionConditionallyAuthorized: authorizationApproved,
                RunnerReadinessApproved: authorizationApproved,
                SingleCampaignOnly: true));
        MonitoredFile.MakeReadOnly(authorizationPath);
        return new ManifestFixture(repositoryRoot, manifestPath);
    }

    private string WriteSuccessorProtocol(
        string repositoryRoot,
        Action<JsonObject>? mutate)
    {
        var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot,
            MonitoredProtocolVersions.BaselineProtocolPath)))!.AsObject();
        var proposal = JsonNode.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot,
            MonitoredProtocolVersions.ProposalPath)))!.AsObject();
        var successor = new JsonObject
        {
            ["revision"] = 4,
            ["status"] = "adopted-awaiting-runner-readiness",
            ["tracking"] = new JsonObject
            {
                ["issue"] = 1_008,
                ["parent"] = 999,
                ["dc4"] = 1_009,
                ["dc5"] = 1_003,
                ["proposal"] = 1_022,
            },
        };
        foreach (var name in new[]
        {
            "scope",
            "limits",
            "recordSemantics",
            "fixture",
            "durabilityProfile",
            "cases",
            "liveProfile",
            "execution",
            "contextCost",
        })
        {
            successor[name] = baseline[name]!.DeepClone();
        }
        var decision = baseline["decision"]!.DeepClone().AsObject();
        decision["recommendationScope"] = "monitored-scope-only";
        successor["decision"] = decision;
        successor["monitoring"] = proposal["monitoring"]!.DeepClone();
        successor["storageSemantics"] = proposal["semanticOverrides"]!.DeepClone();
        successor["baseline"] = new JsonObject
        {
            ["revision"] = 3,
            ["path"] = "durable-capture-comparison-protocol.json",
            ["sha256"] = MonitoredProtocolVersions.BaselineProtocolSha256,
        };
        var freeze = baseline["freeze"]!.DeepClone().AsObject();
        var requiredIdentities = freeze["requiredIdentities"]!.AsArray();
        requiredIdentities.Add("runnerCommit");
        requiredIdentities.Add("monitorCommit");
        requiredIdentities.Add("monitorComponentEvidenceSha256");
        requiredIdentities.Add("attributionMapSha256");
        requiredIdentities.Add("evidenceEncodingSha256");
        successor["freeze"] = freeze;
        successor["readiness"] = new JsonObject
        {
            ["maintainerAdopted"] = true,
            ["executionConditionallyAuthorized"] = true,
            ["runnerReadinessRequired"] = true,
            ["independentRunnerReviewRequired"] = true,
            ["anyExecutionPerformed"] = false,
        };
        mutate?.Invoke(successor);
        var path = Path.Combine(_workspace, $"{Guid.NewGuid():N}-successor.json");
        File.WriteAllText(path, successor.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static MonitoredAttributionMap Attribution(
        string evidence,
        string history,
        string workspace,
        string outputs,
        bool includeTestHostDependencies = true)
        => new(
            MonitoredProtocolVersions.AttributionSchema,
            [
                new("history", history, true),
                new("workspace", workspace, true),
                new("outputs", outputs, true),
                new("evidence", evidence, true),
            ],
            ["package", "package-staging"],
            ["recovery", "recovery-staging"],
            includeTestHostDependencies
                ?
                [
                    Path.GetDirectoryName(Environment.ProcessPath!)!,
                    AppContext.BaseDirectory,
                    "/usr",
                    "/etc",
                ]
                : [Path.GetDirectoryName(Environment.ProcessPath!)!],
            ["/dev/null", "/dev/urandom"],
            [LinuxRuntimeMemoryClassifier.DiscoverCurrentProcessProof()],
            4_096,
            4_096,
            MonitoredRunnerGeometry.MaximumOwnedWritableDescriptorOnlyFiles);

    private static MonitoredEvidenceEncoding EncodingContract()
        => new(
            MonitoredProtocolVersions.EvidenceEncodingSchema,
            "json-lines",
            "UTF-8 without BOM",
            MonitoredSweepSummaryEncoding.Schema,
            MonitoredSweepSummaryEncoding.FieldMapSha256,
            1_024,
            2_048,
            8_388_608,
            8_388_608,
            false,
            false);

    private static MonitoredBinaryIdentity Identity(string path)
        => new(Path.GetFullPath(path), HashFile(path));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string CommitIdentity(string value) => HashText(value)[..40];

    private static JsonElement P1Configuration()
    {
        using var document = JsonDocument.Parse("""{"profile":"P1"}""");
        return document.RootElement.Clone();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "global.json")))
        {
            current = current.Parent;
        }
        return current?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private enum ScriptedWorkerBehavior
    {
        SuccessfulRecovery,
        HangRecovery,
        WrongRecoveryRelease,
        UndeclaredBarrier,
        UnexpectedSuccessfulExit,
    }

    private sealed class ScriptedWorkerLauncher(
        ScriptedWorkerBehavior behavior,
        Action? onRecoveryStarted = null,
        bool awaitIdentityEvent = false,
        int recoveryExitCode = 0) : IMonitoredWorkerLauncher
    {
        internal List<MonitoredWorkerDescriptor> Descriptors { get; } = [];
        internal List<MonitoredProcessIdentity> Identities { get; } = [];

        public Process Start(IMonitoredExecutionManifest manifest, string descriptorPath)
        {
            var descriptor = JsonSerializer.Deserialize<MonitoredWorkerDescriptor>(
                File.ReadAllBytes(descriptorPath),
                JsonOptions)
                ?? throw new InvalidOperationException("Could not read scripted worker descriptor.");
            Descriptors.Add(descriptor);
            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = descriptor.ExecutionRoot,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(Script(descriptor));
            if (descriptor.Mode == MonitoredWorkerMode.Recover
                && behavior == ScriptedWorkerBehavior.SuccessfulRecovery)
            {
                var result = ScriptedResult(descriptor);
                startInfo.Environment["RESULT_PATH"] = Path.Combine(
                    descriptor.ExecutionRoot,
                    "worker-result.json");
                startInfo.Environment["RESULT_JSON"] = JsonSerializer.Serialize(
                    result,
                    JsonOptions);
                startInfo.Environment["RECOVERY_EXIT_CODE"] =
                    recoveryExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start scripted worker.");
            if (awaitIdentityEvent)
            {
                // This component's private /proc/stat read must finish before observation.
                // Peek leaves the identity event for the runner; real launchers are unchanged.
                process.StandardOutput.Peek().Should().BeGreaterThanOrEqualTo(0);
            }
            Identities.Add(MonitoredProcessIdentity.Capture(
                process,
                MonitoredProcessRole.Diagnostic));
            if (descriptor.Mode == MonitoredWorkerMode.Recover)
            {
                onRecoveryStarted?.Invoke();
            }
            return process;
        }

        private string Script(MonitoredWorkerDescriptor descriptor)
        {
            const string identity = """
                read -r -a stat_fields < "/proc/$$/stat"
                pid=$$
                start="${stat_fields[21]}"
                printf '{"type":"process","processId":%s,"processStartTimeTicks":%s,"processRole":"diagnostic"}\n' "$pid" "$start"
                """;
            if (behavior == ScriptedWorkerBehavior.UnexpectedSuccessfulExit)
            {
                return identity + '\n' + """
                    read -r -t 0.2 ignored || true
                    exit 0
                    """;
            }
            if (behavior == ScriptedWorkerBehavior.UndeclaredBarrier)
            {
                return identity + '\n' + """
                    printf '{"type":"barrier","stage":"AfterCommitBeforeAcknowledgement","activeStorageStage":true,"barrier":"AfterCommitBeforeAcknowledgement","batchOrdinal":2,"firstSequence":65,"lastSequence":128}\n'
                    IFS= read -r ignored
                    exit 31
                    """;
            }
            if (descriptor.Mode == MonitoredWorkerMode.Execute)
            {
                return identity + '\n' + """
                    printf '{"type":"ack","firstSequence":1,"lastSequence":64}\n'
                    printf '{"type":"barrier","stage":"AfterCommitBeforeAcknowledgement","activeStorageStage":true,"barrier":"AfterCommitBeforeAcknowledgement","batchOrdinal":2,"firstSequence":65,"lastSequence":128}\n'
                    IFS= read -r ignored
                    exit 31
                    """;
            }
            if (behavior == ScriptedWorkerBehavior.HangRecovery)
            {
                return identity + '\n' + """
                    IFS= read -r ignored
                    exit 32
                    """;
            }
            if (behavior == ScriptedWorkerBehavior.WrongRecoveryRelease)
            {
                return identity + '\n' + """
                    printf '{"type":"boundary","stage":"component-recovery-boundary","activeStorageStage":false}\n'
                    IFS= read -r release || exit 21
                    [[ "$release" == "release:wrong-boundary" ]] || exit 22
                    exit 0
                    """;
            }
            return identity + '\n' + """
                printf '{"type":"boundary","stage":"component-recovery-boundary","activeStorageStage":false}\n'
                IFS= read -r release || exit 21
                [[ "$release" == "release:component-recovery-boundary" ]] || exit 22
                printf '%s' "$RESULT_JSON" > "$RESULT_PATH"
                printf '{"type":"process-terminating","processId":%s,"processStartTimeTicks":%s,"processRole":"diagnostic"}\n' "$pid" "$start"
                IFS= read -r release || exit 23
                expected="release:process-termination:$pid:$start"
                [[ "$release" == "$expected" ]] || exit 24
                printf '{"type":"completed"}\n'
                exit "$RECOVERY_EXIT_CODE"
                """;
        }

        private static MonitoredWorkerResult ScriptedResult(
            MonitoredWorkerDescriptor descriptor)
            => new(
                MonitoredProtocolVersions.WorkerResultSchema,
                descriptor.Execution.Ordinal,
                descriptor.Execution.CaseId,
                descriptor.Execution.Candidate,
                "pass",
                FailureCode: null,
                FailureMessage: null,
                Offered: 128,
                Admitted: 128,
                Rejected: 0,
                Committed: 128,
                FailedAfterAdmission: 0,
                UnknownCommitOutcome: 0,
                SourceMalformedPayloads: 0,
                SourceAdmissionInvalid: 0,
                SourceRejectedNewKeys: 0,
                SourceAdmissionRejected: 0,
                LogicalCommittedBytes: 65_536,
                PackageFinalBytes: 1_024,
                OfferSeconds: 0.01,
                DrainSeconds: 0.01,
                FinalizationSeconds: 0.01,
                ReopenAndFirstQuerySeconds: 0.01,
                DiagnosticCpuSeconds: 0.01,
                StorageFaultTriggered: true,
                ConcurrentCaptureRejected: false,
                RecoveryInvariantSatisfied: true,
                RecoveredRecords: 128,
                Requests: null,
                SourceTicks: 128,
                SourceKeys: 8,
                ScheduledSourceOffers: null,
                AttemptedSourceOffers: null,
                AchievedSourceOfferRatio: null,
                SourceCoverage: "component-scripted-worker",
                TargetStartedAt: null,
                CounterSessionStartedAt: null,
                CounterCollectionSeconds: null,
                HasDurablePackage: false,
                RecommendationScope: "monitored-scope-only");
    }

    private sealed record ManifestFixture(string RepositoryRoot, string ManifestPath);

    private sealed class FaultingSummaryWriter : IMonitoredSummaryWriter
    {
        internal TaskCompletionSource WriteAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Disposed { get; private set; }
        public int Records => 0;
        public long Bytes => 0;
        public int MaximumObservedRecordBytes => 0;

        public ValueTask WriteAsync<T>(T value, CancellationToken cancellationToken)
        {
            WriteAttempted.TrySetResult();
            throw new InvalidOperationException("component writer failure");
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
