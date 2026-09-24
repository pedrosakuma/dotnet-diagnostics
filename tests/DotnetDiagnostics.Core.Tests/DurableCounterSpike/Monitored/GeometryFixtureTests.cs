using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed partial class MonitoredRunnerTests
{
    [Fact]
    public async Task GeometryFixtureLinkedCreationAfterTraversalIsGenuineThirtyThirdIdentity()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "geometry");
        Directory.CreateDirectory(root);
        var anchor = Path.Combine(root, "anchor");
        File.WriteAllBytes(anchor, [1]);
        using var self = Process.GetCurrentProcess();
        var owner = MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness);
        await using var monitor = GeometryMonitor(root, owner);
        var streams = new List<FileStream>();
        FileStream? late = null;
        try
        {
            for (var index = 0; index < 32; index++)
                streams.Add(PrevalidationGeometry.CreateDescriptorFixture(index));
            monitor.BeforeRootPathObservationForComponent = path =>
            {
                if (path != anchor || late is not null) return;
                late = new FileStream(Path.Combine(root, "late-linked.bin"), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.ReadWrite);
                late.SetLength(512);
            };
            var failed = await monitor.ObserveBoundaryAsync("geometry-construction-overlap", true,
                CancellationToken.None);
            failed.Summary.DescriptorOnlyIdentityCount.Should().Be(33);
            failed.Summary.SampledLoss!.ObservedUnlinked!.Identities.Should().Be(32);
            failed.Errors.Should().ContainSingle().Which.Should().Be("DescriptorOnlyIdentityLimitExceeded");
            var native = LinuxStatxHandleMetadataObserver.Instance.Observe(late!.SafeFileHandle);
            native.LinkCount.Should().Be(1);
            failed.IdentityEvidence.Should().ContainSingle(item => item.Identity == native.Identity.Value
                && item.SeenInDescriptor && !item.SeenInRoot && !item.IsUnlinked && item.Charged);
            DescriptorObservationPolicy.Admissible(failed.Summary).Should().BeFalse();
            _output.WriteLine(Encoding.UTF8.GetString(MonitoredSweepSummaryEncoding.EncodeLine(failed.Summary)));
            _output.WriteLine($"Controlled extra identity: {native.Identity.Value}; linked=1; path={late.Name}. Not the historical FD.");
            monitor.BeforeRootPathObservationForComponent = null;
            late.Dispose();
            var next = await monitor.ObserveBoundaryAsync("geometry-after-close", true, CancellationToken.None);
            next.Summary.DescriptorOnlyIdentityCount.Should().Be(32);
            monitor.IsIncomplete.Should().BeTrue("closing the extra FD must not clear the sticky cap failure");
        }
        finally
        {
            late?.Dispose();
            foreach (var stream in streams) stream.Dispose();
        }
    }

    [Fact]
    public async Task GeometryFixtureOrderedConstructionMeasures539Plus32AndStrictCleanup()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "geometry");
        var history = Path.Combine(root, "history");
        Directory.CreateDirectory(history);
        PrevalidationLayout.CreateHistoryFixtures(history, 64, CancellationToken.None);
        using var self = Process.GetCurrentProcess();
        var owner = MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness);
        await using var monitor = GeometryMonitor(root, owner, history);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        _ = monitor.StartAsync();
        var coverage = await PrevalidationGeometry.RunForComponentAsync(root, history, monitor, deadline.Token,
            index =>
            {
                // The first memfd is not created until every rooted pathname already exists.
                if (index == 0)
                    Directory.GetFiles(root, "*", SearchOption.AllDirectories).Should().HaveCount(539);
            });
        PrevalidationExecutor.HasCoverage(coverage).Should().BeTrue();
        coverage.MaximumContextIdentities.Should().Be(571);
        coverage.ObservedFixtureFiles.Should().Be(256);
        Directory.GetDirectories(history).Should().HaveCount(64);
        var proofPath = Path.Combine(root, "geometry-descriptor-proof.json");
        PrevalidationProtocol.EnsureImmutable(proofPath);
        var proof = PrevalidationProtocol.Read<PrevalidationDescriptorProof>(proofPath);
        proof.Fixtures.Should().HaveCount(32);
        var cleanup = await monitor.ObserveBoundaryAsync("owned-cleanup-quiescent", false, deadline.Token);
        PrevalidationExecutor.RequireSweep(cleanup);
        PrevalidationExecutor.RequireMonitor(monitor);
        cleanup.Summary.CurrentContextRootedIdentities.Should().Be(539);
        cleanup.Summary.DescriptorOnlyIdentityCount.Should().Be(0);
        cleanup.Summary.RetainedHistoryBytes.Should().Be(64 * 4 * 512);
        monitor.MaximumDescriptorOnlyIdentityCount.Should().Be(32);
        monitor.MaximumObservedGapMilliseconds.Should().BeLessThanOrEqualTo(1_000);
        _output.WriteLine(JsonSerializer.Serialize(coverage, PrevalidationProtocol.Json));
        _output.WriteLine(Encoding.UTF8.GetString(MonitoredSweepSummaryEncoding.EncodeLine(cleanup.Summary)));
        await monitor.StopAsync();
        monitor.PeriodicSummaryRecords.Should().BeGreaterThan(0);
        monitor.SummaryRecords.Should().BeLessThanOrEqualTo(2_048);
        monitor.MaximumSummaryRecordBytes.Should().BeLessThanOrEqualTo(1_024);
        monitor.SummaryBytes.Should().BeLessThanOrEqualTo(2_048 * 1_024);
        deadline.IsCancellationRequested.Should().BeFalse();
        _output.WriteLine("Native geometry summaries (one uninterrupted periodic monitor):");
        _output.WriteLine(File.ReadAllText(Path.Combine(root, "coordinator-monitor.jsonl")));
        var readOnly = Directory.GetFiles(history, "*", SearchOption.AllDirectories)[0];
        monitor.BeforeRootPathObservationForComponent = path =>
        {
            if (path == readOnly) throw new FileNotFoundException("controlled read-only root failure");
        };
        var strict = await monitor.ObserveBoundaryAsync("owned-cleanup-quiescent", false, deadline.Token);
        strict.Errors.Should().Contain("RootObservationFileNotFoundException");
        strict.Summary.SampledLoss!.RootSampling!.LostFilePaths.Should().Be(0);
        DescriptorObservationPolicy.Admissible(strict.Summary).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeometryFixtureCompletedContextDescriptorsStillRequireObservedReadOnlyRoots(bool writable)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "geometry");
        var previous = Path.Combine(root, "completed");
        Directory.CreateDirectory(previous);
        var path = Path.Combine(previous, "old");
        File.WriteAllBytes(path, [1]);
        using var held = File.Open(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var self = Process.GetCurrentProcess();
        var owner = MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness);
        await using var monitor = new MonitoredStorageMonitor(
            Attribution(root, root, root, root) with { Roots = [new("workspace", root, true)] },
            EncodingContract(), Path.Combine(root, "monitor.jsonl"),
            prevalidationScope: new([previous], owner), unifiedActive: true);
        monitor.AddProcess(owner);
        if (!writable) monitor.BeforeRootPathObservationForComponent = leaf =>
        {
            if (leaf == path) File.Delete(leaf);
        };
        var result = await monitor.ObserveBoundaryAsync("owned-cleanup-quiescent", false, CancellationToken.None);
        result.Errors.Should().Contain(writable ? "WritableCompletedContextDescriptor"
            : "UnclassifiedCompletedContextDescriptor");
        result.Summary.SampledLoss!.RootSampling!.LostFilePaths.Should().Be(0);
        DescriptorObservationPolicy.Admissible(result.Summary).Should().BeFalse();
        monitor.IsIncomplete.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeometryFixtureConstructionFailureClosesOwnedHandlesWithoutBlockingObserver(bool cancel)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(_workspace, "geometry");
        var history = Path.Combine(root, "history");
        Directory.CreateDirectory(history);
        PrevalidationLayout.CreateHistoryFixtures(history, 64, CancellationToken.None);
        using var self = Process.GetCurrentProcess();
        var owner = MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness);
        await using var monitor = GeometryMonitor(root, owner, history);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        _ = monitor.StartAsync();
        Func<Task> construct = () => PrevalidationGeometry.RunForComponentAsync(root, history, monitor,
            deadline.Token, index =>
            {
                if (index != 7) return;
                if (cancel) deadline.Cancel();
                else throw new IOException("controlled construction failure");
            });
        if (cancel) await construct.Should().ThrowAsync<OperationCanceledException>();
        else await construct.Should().ThrowAsync<IOException>();
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cleanup = await monitor.ObserveBoundaryAsync("owned-cleanup-quiescent", false, cleanupDeadline.Token);
        PrevalidationExecutor.RequireSweep(cleanup);
        PrevalidationExecutor.RequireMonitor(monitor);
        cleanup.Summary.DescriptorOnlyIdentityCount.Should().Be(0);
        Directory.GetFiles($"/proc/{self.Id}/fd").Select(path => new FileInfo(path).LinkTarget)
            .Should().NotContain(target => target != null
                && (target.StartsWith("/memfd:dc5-pv-geometry-", StringComparison.Ordinal)
                    || target == Path.Combine(root, "geometry-descriptor-proof.json")
                    || target == Path.Combine(root, "harness-result.json")));
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("file")]
    [InlineData("size")]
    [InlineData("writable")]
    public void GeometryFixtureHistoryKeepsSlotFileAndImmutabilityLimits(string overflow)
    {
        if (!OperatingSystem.IsLinux()) return;
        var history = Path.Combine(_workspace, "history");
        Directory.CreateDirectory(history);
        PrevalidationLayout.CreateHistoryFixtures(history, 64, CancellationToken.None);
        PrevalidationGeometry.CountFixtureFiles(history).Should().Be(256);
        var slot = Path.Combine(history, "inventory-00");
        File.SetUnixFileMode(slot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = Path.Combine(slot, "fixture-0.bin");
        switch (overflow)
        {
            case "slot": Directory.CreateDirectory(Path.Combine(history, "inventory-64")); break;
            case "file": File.WriteAllBytes(Path.Combine(slot, "fixture-4.bin"), new byte[512]); break;
            case "size":
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.WriteAllBytes(file, [1]);
                MonitoredFile.MakeReadOnly(file);
                break;
            case "writable": File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); break;
        }
        Action count = () => PrevalidationGeometry.CountFixtureFiles(history);
        count.Should().Throw<DurableStorageExperimentException>();
    }

    private static MonitoredStorageMonitor GeometryMonitor(string root, MonitoredProcessIdentity owner,
        string? history = null)
    {
        var monitor = new MonitoredStorageMonitor(
            Attribution(root, root, root, root) with { Roots = [new("workspace", root, true)] },
            EncodingContract(), Path.Combine(root, "coordinator-monitor.jsonl"),
            includeIdentityEvidence: true,
            prevalidationScope: new([], owner, CurrentHistoryRoot: history, GeometryFixtures: true),
            unifiedActive: true);
        PrevalidationOwnership.Append(Path.Combine(root, "ownership.jsonl"), owner);
        monitor.RegisterGeometryFixtureOwner(owner);
        monitor.AddProcess(owner);
        return monitor;
    }
}
