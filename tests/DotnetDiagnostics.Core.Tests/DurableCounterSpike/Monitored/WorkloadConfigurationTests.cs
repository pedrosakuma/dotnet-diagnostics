using DotnetDiagnostics.Core.Tests.DurableCounterSpike.Sqlite;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed class WorkloadConfigurationTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        AppContext.BaseDirectory, "dc5-workload-configuration", Guid.NewGuid().ToString("N"));

    public static TheoryData<int, string, string> FrozenExecutions()
    {
        var data = new TheoryData<int, string, string>();
        foreach (var execution in MonitoredExecutionPlanner.Expand())
        {
            data.Add(execution.Ordinal, execution.CaseId, execution.Candidate);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(FrozenExecutions))]
    public async Task EveryFrozenExecutionConstructsWithoutAdapterConfigurationDrift(
        int ordinal, string caseId, string candidate)
    {
        var descriptor = Descriptor(ordinal);
        var expected = new DurableCounterPipelineLimits(BatchMaxAge: TimeSpan.FromMilliseconds(100));
        var pipelineLimits = MonitoredWorkerExecutor.PipelineLimitsForCase(caseId);
        pipelineLimits.Should().Be(caseId == "M1"
            ? expected with { OwnedBufferBytes = 262_144 }
            : expected);
        descriptor.Execution.Candidate.Should().Be(candidate);

        if (candidate is not ("A" or "B"))
        {
            await using var shared = new DurableCounterPipeline(
                pipelineLimits, new DurableCounterGlobalBudget(pipelineLimits), new RecordingCounterSink());
            shared.GetAccounting().Offered.Should().Be(0);
            return;
        }

        var request = MonitoredWorkerExecutor.CreateAdapterRequest(descriptor, NoDurableStorageFaults.Instance);
        var factory = MonitoredAdapterRegistry.Require(candidate);
        await using var adapter = factory.Create(request);
        request.Limits.Should().Be(expected);
        request.Configuration.GetRawText().Should().Be("""{"profile":"P1"}""");
        request.Limits.PageRows.Should().Be(100);
        request.Limits.ResultBytes.Should().Be(1_048_576);
        request.Limits.DistinctKeys.Should().Be(128);
        await using var pipeline = new DurableCounterPipeline(
            pipelineLimits, new DurableCounterGlobalBudget(pipelineLimits), adapter);
        pipeline.GetAccounting().Offered.Should().Be(0);

        if (caseId == "F5")
        {
            var second = descriptor with
            {
                CaptureId = $"{descriptor.CaptureId}-second",
                ArtifactId = $"{descriptor.ArtifactId}-second",
                PackageStagingRoot = Path.Combine(_workspace, "second"),
            };
            var secondRequest = MonitoredWorkerExecutor.CreateAdapterRequest(second, NoDurableStorageFaults.Instance);
            secondRequest.Limits.Should().Be(expected);
            await using var secondAdapter = factory.Create(secondRequest);
        }
    }

    [Theory]
    [InlineData("A", "M1", 64)]
    [InlineData("B", "M1", 64)]
    [InlineData("A", "B1", 65)]
    [InlineData("B", "B1", 65)]
    public async Task PipelineReservationRemainsIndependentOfAdapterDefaults(
        string candidate, string caseId, int expectedAccepted)
    {
        var execution = MonitoredExecutionPlanner.Expand().Single(
            item => item.CaseId == caseId && item.Candidate == candidate);
        var descriptor = Descriptor(execution.Ordinal);
        var limits = MonitoredWorkerExecutor.PipelineLimitsForCase(caseId);
        var request = MonitoredWorkerExecutor.CreateAdapterRequest(descriptor, NoDurableStorageFaults.Instance);
        await using var adapter = MonitoredAdapterRegistry.Require(candidate).Create(request);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipeline = new DurableCounterPipeline(
            limits, new DurableCounterGlobalBudget(limits), adapter, writerStartGate: gate.Task);
        try
        {
            for (var ordinal = 1; ordinal <= 65; ordinal++)
            {
                pipeline.TryWrite(DurableCounterFixture.Generate(ordinal)).Status.Should().Be(
                    ordinal <= expectedAccepted
                        ? DurableCounterOfferStatus.Accepted
                        : DurableCounterOfferStatus.OwnedBudgetFull);
            }
            var accounting = pipeline.GetAccounting();
            accounting.Committed.Should().Be(0);
            accounting.Admitted.Should().Be(expectedAccepted);
            accounting.Rejected.Should().Be(65 - expectedAccepted);
        }
        finally
        {
            gate.TrySetResult();
        }
        (await pipeline.DrainAsync(TimeSpan.FromSeconds(5))).CompletedWithinTimeout.Should().BeTrue();
        pipeline.GetAccounting().Committed.Should().Be(expectedAccepted);
    }

    [Fact]
    public void SqliteGuardStillRejectsPipelineOverridesAndQueryDrift()
    {
        var request = MonitoredWorkerExecutor.CreateAdapterRequest(Descriptor(11), NoDurableStorageFaults.Instance);
        var expected = new DurableCounterPipelineLimits(BatchMaxAge: TimeSpan.FromMilliseconds(100));
        var invalid = new[]
        {
            expected with { OwnedBufferBytes = 262_144 },
            expected with { PageRows = 99 },
            expected with { ResultBytes = 1_048_575 },
            expected with { BatchRecords = 63 },
        };
        foreach (var limits in invalid)
        {
            Action create = () => new DurableSqliteStorageAdapterFactory().Create(request with { Limits = limits });
            create.Should().Throw<DurableStorageExperimentException>()
                .Which.Code.Should().Be("SqliteProtocolLimitMismatch");
        }
        Directory.Exists(request.StagingRoot).Should().BeFalse();
    }

    private MonitoredWorkerDescriptor Descriptor(int ordinal)
        => new(
            Schema: MonitoredProtocolVersions.WorkerDescriptorSchema,
            Mode: MonitoredWorkerMode.Execute,
            RepositoryRoot: _workspace,
            ManifestPath: Path.Combine(_workspace, "unused-manifest.json"),
            ManifestSha256: new string('a', 64),
            Execution: MonitoredExecutionPlanner.Expand().Single(item => item.Ordinal == ordinal),
            ExecutionRoot: _workspace,
            PackageStagingRoot: Path.Combine(_workspace, $"package-{ordinal}"),
            PackageRoot: Path.Combine(_workspace, $"published-{ordinal}"),
            CaptureId: $"capture-{ordinal}",
            ArtifactId: $"artifact-{ordinal}",
            SourcePackageRoot: null,
            SourceCaptureId: null,
            SourceArtifactId: null,
            ConfirmedAcknowledgements: null,
            KnownOfferedSequences: null);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }
}
