using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Tests.DurableCounterSpike.Sqlite;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Sqlite;

public sealed class DurableSqliteStorageAdapterTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        AppContext.BaseDirectory,
        "dc5-sqlite-adapter",
        Guid.NewGuid().ToString("N"));

    public DurableSqliteStorageAdapterTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Fact]
    public void FactoryAcceptsOnlyExactP1AndFrozenLimits()
    {
        var factory = new DurableSqliteStorageAdapterFactory();
        factory.Identity.Should().Be(new DurableStorageAdapterIdentity(
            "A",
            "dc5-sqlite-p1/1",
            "durable-sqlite-p1-config/1",
            "successful bounded SQLite batch transaction commit under P1"));

        using var unsupported = JsonDocument.Parse("""{"profile":"P1","synchronous":"NORMAL"}""");
        Action badConfiguration = () => factory.Create(new DurableStorageAdapterCreateRequest(
            "capture",
            "artifact",
            NewRoot("bad-config"),
            unsupported.RootElement.Clone(),
            Limits(),
            NoDurableStorageFaults.Instance));
        badConfiguration.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("UnsupportedSqliteConfiguration");

        using var valid = JsonDocument.Parse("""{"profile":"P1"}""");
        Action badLimits = () => factory.Create(new DurableStorageAdapterCreateRequest(
            "capture",
            "artifact",
            NewRoot("bad-limits"),
            valid.RootElement.Clone(),
            Limits() with { PageRows = 99 },
            NoDurableStorageFaults.Instance));
        badLimits.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("SqliteProtocolLimitMismatch");
    }

    [Fact]
    public async Task Q1MatchesIndependentOracleThroughFreshBoundedQueries()
    {
        var root = NewRoot("q1");
        var factory = new DurableSqliteStorageAdapterFactory();
        await using var adapter = factory.Create(CreateRequest(root));
        Action earlyReader = () => _ = adapter.Reader;
        earlyReader.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("SqliteReaderUnavailable");

        var observations = DurableCounterFixture.GenerateQ1();
        await using var pipeline = CreatePipeline(adapter);
        for (var offset = 0; offset < observations.Count; offset += 64)
        {
            foreach (var observation in observations.Skip(offset).Take(64))
            {
                pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
            }
            await WaitUntilAsync(() => pipeline.GetAccounting().Committed >= Math.Min(offset + 64, observations.Count));
        }
        (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();
        (await pipeline.FinalizeAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        var quality = Quality(pipeline.GetAccounting());
        var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        preSeal.CanonicalMembers.Should().ContainSingle();
        preSeal.QueryMembers.Should().BeEmpty();

        var manifest = Manifest(
            "capture-q1",
            "artifact-q1",
            preSeal.CanonicalMembers,
            quality,
            volatileTailUnknown: false);
        await using var reader = factory.OpenReadonly(new DurableStorageOpenRequest(root, manifest, Limits()));

        var summary = await reader.SummaryAsync(CancellationToken.None);
        summary.Should().HaveCount(8);
        summary.Sum(static row => row.RetainedCount).Should().Be(1_024);
        summary.Should().OnlyContain(static row =>
            row.UnknownCoverageCount == 1 && row.GapCount == 0 && row.Unit == "items");

        var expected = DurableCounterOracle.Q1()
            .Where(static row => row.Name == "counter-0")
            .ToArray();
        var actual = new List<DurableCounterRecord>();
        long? cursor = null;
        do
        {
            var page = await reader.SeriesAsync(
                "Synthetic.Provider",
                "counter-0",
                cursor,
                pageSize: 100,
                CancellationToken.None);
            actual.AddRange(page.Rows);
            cursor = page.NextAfterSequence;
        }
        while (cursor.HasValue);

        actual.Select(ToExpected).Should().Equal(expected);
        actual.Should().OnlyContain(static record =>
            record.DisplayName == "Synthetic counter 0"
            && record.Unit == "items"
            && record.IntervalSec == 1
            && record.DisplayScaleTicks == TimeSpan.TicksPerSecond
            && record.ClockDomain == "fixture-relative-100ns"
            && record.ClockOrigin == "dc3-rev3-generated"
            && record.ResetState == CounterResetState.Unknown
            && record.EncodedBytes == 512);
        (await reader.QualityAsync(CancellationToken.None)).Should().Be(
            new DurableStorageQualityReport(quality, 1_024, VolatileTailUnknown: false));

        Func<Task> invalidPage = async () =>
            await reader.SeriesAsync("Synthetic.Provider", "counter-0", null, 101, CancellationToken.None);
        await invalidPage.Should().ThrowAsync<DurableCounterPipelineException>()
            .Where(static exception => exception.Code == "InvalidPageSize");
        Func<Task> invalidCursor = async () =>
            await reader.SeriesAsync("Synthetic.Provider", "counter-0", -1, 1, CancellationToken.None);
        await invalidCursor.Should().ThrowAsync<DurableCounterPipelineException>()
            .Where(static exception => exception.Code == "InvalidCursor");
    }

    [Fact]
    public async Task Q2PreservesEveryRetainedFieldAndHostQualitySnapshot()
    {
        var root = NewRoot("q2");
        var factory = new DurableSqliteStorageAdapterFactory();
        await using var adapter = factory.Create(CreateRequest(root, "capture-q2", "artifact-q2"));
        var observations = DurableCounterFixture.LoadQ2(FindRepositoryRoot());
        await using var pipeline = CreatePipeline(adapter);

        var offers = observations.Select(pipeline.TryWrite).ToArray();
        (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();
        (await pipeline.FinalizeAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        offers.Count(static offer => offer.Status == DurableCounterOfferStatus.Accepted).Should().Be(13);
        var quality = Quality(pipeline.GetAccounting());
        var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        var manifest = Manifest(
            "capture-q2",
            "artifact-q2",
            preSeal.CanonicalMembers,
            quality,
            volatileTailUnknown: false);
        await using var reader = factory.OpenReadonly(new DurableStorageOpenRequest(root, manifest, Limits()));

        var retained = new List<DurableCounterRecord>();
        foreach (var key in new[] { "counter-0", "counter-1" })
        {
            retained.AddRange((await reader.SeriesAsync(
                "Synthetic.Provider",
                key,
                null,
                100,
                CancellationToken.None)).Rows);
        }
        retained.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));

        retained.Select(ToExpected).Should().Equal(
            DurableCounterOracle.Q2().Where(static row => row.Status == DurableCounterOfferStatus.Accepted));
        foreach (var record in retained)
        {
            var source = observations[checked((int)record.Sequence - 1)];
            record.DisplayName.Should().Be(source.Counter.DisplayName);
            record.Unit.Should().Be(source.Counter.Unit);
            record.ClockDomain.Should().Be(source.Clock.Domain);
            record.ClockOrigin.Should().Be(source.Clock.Origin);
            if (double.IsFinite(source.Counter.IntervalSec ?? double.NaN))
            {
                record.IntervalSec.Should().Be(source.Counter.IntervalSec);
            }
            record.DisplayScaleTicks.Should().Be(source.Counter.DisplayRateTimeScale?.Ticks);
        }
        retained.Single(static row => row.Sequence == 13).EncodedBytes.Should().Be(4_096);
        var storageQuality = await reader.QualityAsync(CancellationToken.None);
        storageQuality.FinalPipelineQuality.Should().Be(quality);
        storageQuality.RetainedRecords.Should().Be(13);

        var mismatched = manifest with
        {
            FinalPipelineQuality = quality with
            {
                Accounting = quality.Accounting with { Committed = 12 },
            },
        };
        Action openMismatch = () => factory.OpenReadonly(new DurableStorageOpenRequest(root, mismatched, Limits()));
        openMismatch.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("InvalidFinalQuality");

        var forgedRejections = new Dictionary<string, long>(
            quality.Accounting.Rejections,
            StringComparer.Ordinal)
        {
            ["HostPolicyRejected"] = 1,
        };
        var forgedQuality = quality with
        {
            Accounting = quality.Accounting with
            {
                Offered = 17,
                Rejected = 4,
                Rejections = forgedRejections,
            },
        };
        var forgedEnvelope = new DurableStorageQualityReport(
            forgedQuality,
            RetainedRecords: 13,
            VolatileTailUnknown: false);
        Action validateForged = () => DurableStorageQualityRules.Validate(forgedEnvelope);
        validateForged.Should().NotThrow(
            "the alternate quality is internally valid but is not the authoritative sealed snapshot");

        Action openWithDifferentQuality = () => DurableSqliteReadonlyStore.Open(
            DurableSqlitePackage.DatabasePath(root),
            manifest,
            Limits(),
            manifest.CaptureId,
            manifest.ArtifactId,
            adapterQuality: forgedEnvelope);
        openWithDifferentQuality.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("QualitySnapshotMismatch");
    }

    [Fact]
    public async Task FreshReadonlyOpenLeavesPackageInventoryAndHashesUnchanged()
    {
        var root = NewRoot("readonly");
        var factory = new DurableSqliteStorageAdapterFactory();
        DurableStoragePackageManifest manifest;
        await using (var adapter = factory.Create(CreateRequest(root, "capture-ro", "artifact-ro")))
        {
            await using var pipeline = CreatePipeline(adapter);
            pipeline.TryWrite(DurableCounterFixture.Generate(1)).Status.Should().Be(DurableCounterOfferStatus.Accepted);
            (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();
            (await pipeline.FinalizeAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
            var quality = Quality(pipeline.GetAccounting());
            var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
            manifest = Manifest("capture-ro", "artifact-ro", preSeal.CanonicalMembers, quality, false);
        }

        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Should().Equal(Path.Combine("canonical", "counters.db"));
        var before = Snapshot(root);

        await using (var reader = factory.OpenReadonly(new DurableStorageOpenRequest(root, manifest, Limits())))
        {
            (await reader.SummaryAsync(CancellationToken.None)).Should().ContainSingle();
            (await reader.SeriesAsync(
                "Synthetic.Provider",
                "counter-0",
                null,
                100,
                CancellationToken.None)).Rows.Should().ContainSingle();
            (await reader.QualityAsync(CancellationToken.None)).RetainedRecords.Should().Be(1);
        }

        Snapshot(root).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task PreCommitFailureRollsBackAndPostCommitFailureIsUnknown()
    {
        var factory = new DurableSqliteStorageAdapterFactory();
        var preCommitFaults = new DurableSqliteFaultController(
            barrier: static (context, _) => context.Barrier == DurableStorageFaultBarrier.BeforeCommit
                ? ValueTask.FromException(new IOException("held pre-commit fault"))
                : ValueTask.CompletedTask);
        await using (var adapter = factory.Create(CreateRequest(
            NewRoot("f2"),
            "capture-f2",
            "artifact-f2",
            preCommitFaults)))
        {
            var result = await adapter.CommitAsync([SinkRecord(1)], CancellationToken.None);
            result.Outcome.Should().Be(DurableCounterCommitOutcome.Failed);
            await adapter.FinalizeAsync(CancellationToken.None);
            var quality = Quality(Accounting(committed: 0, failed: 1, unknown: 0));
            var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
            preSeal.CanonicalMembers.Should().ContainSingle();
            (await adapter.Reader.QualityAsync(CancellationToken.None)).RetainedRecords.Should().Be(0);
        }

        var postCommitFaults = new DurableSqliteFaultController(
            barrier: static (context, _) => context.Barrier == DurableStorageFaultBarrier.AfterCommitBeforeAcknowledgement
                ? ValueTask.FromException(new IOException("held post-commit fault"))
                : ValueTask.CompletedTask);
        await using (var adapter = factory.Create(CreateRequest(
            NewRoot("f3"),
            "capture-f3",
            "artifact-f3",
            postCommitFaults)))
        {
            var result = await adapter.CommitAsync([SinkRecord(1)], CancellationToken.None);
            result.Outcome.Should().Be(DurableCounterCommitOutcome.Unknown);
            await adapter.FinalizeAsync(CancellationToken.None);
            var quality = Quality(Accounting(committed: 0, failed: 0, unknown: 1));
            await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
            (await adapter.Reader.QualityAsync(CancellationToken.None)).Should().Be(
                new DurableStorageQualityReport(quality, 1, VolatileTailUnknown: false));
        }

        var postCommitCancellation = new DurableSqliteFaultController(
            barrier: static (context, _) => context.Barrier == DurableStorageFaultBarrier.AfterCommitBeforeAcknowledgement
                ? ValueTask.FromCanceled(new CancellationToken(canceled: true))
                : ValueTask.CompletedTask);
        await using (var adapter = factory.Create(CreateRequest(
            NewRoot("f3-cancel"),
            "capture-f3-cancel",
            "artifact-f3-cancel",
            postCommitCancellation)))
        {
            var result = await adapter.CommitAsync([SinkRecord(1)], CancellationToken.None);
            result.Outcome.Should().Be(DurableCounterCommitOutcome.Unknown);
            result.Error.Should().StartWith("PostCommitAcknowledgement:");
        }
    }

    [Fact]
    public async Task CommitAttemptRollbackAndCleanupFaultsNeverBecomeKnownFailed()
    {
        var factory = new DurableSqliteStorageAdapterFactory();
        var commitBeforeNative = new DurableSqliteOperationHooks(
            commit: static _ => throw new IOException("commit attempt fault"));
        await using (var adapter = factory.Create(CreateRequest(
            NewRoot("commit-attempt"),
            "capture-commit-attempt",
            "artifact-commit-attempt",
            new DurableSqliteFaultController(operations: commitBeforeNative))))
        {
            var result = await adapter.CommitAsync([SinkRecord(1)], CancellationToken.None);
            result.Outcome.Should().Be(DurableCounterCommitOutcome.Unknown);
            result.Error.Should().StartWith("CommitStage:");
        }

        var rollbackFailure = new DurableSqliteOperationHooks(
            rollback: static _ => throw new IOException("rollback fault"));
        await using (var adapter = factory.Create(CreateRequest(
            NewRoot("rollback-failure"),
            "capture-rollback-failure",
            "artifact-rollback-failure",
            new DurableSqliteFaultController(
                barrier: static (context, _) => context.Barrier == DurableStorageFaultBarrier.BeforeCommit
                    ? ValueTask.FromException(new IOException("pre-commit fault"))
                    : ValueTask.CompletedTask,
                operations: rollbackFailure))))
        {
            var result = await adapter.CommitAsync([SinkRecord(1)], CancellationToken.None);
            result.Outcome.Should().Be(DurableCounterCommitOutcome.Unknown);
            result.Error.Should().StartWith("PreCommitRollbackOrCleanup:");
        }

        var cleanupFailure = new DurableSqliteOperationHooks(
            dispose: static transaction =>
            {
                transaction.Dispose();
                throw new IOException("cleanup fault");
            });
        await using (var adapter = factory.Create(CreateRequest(
            NewRoot("cleanup-failure"),
            "capture-cleanup-failure",
            "artifact-cleanup-failure",
            new DurableSqliteFaultController(operations: cleanupFailure))))
        {
            var result = await adapter.CommitAsync([SinkRecord(1)], CancellationToken.None);
            result.Outcome.Should().Be(DurableCounterCommitOutcome.Unknown);
            result.Error.Should().StartWith("CommitCleanup:");
        }

        var preCommitCleanupFailure = new DurableSqliteOperationHooks(
            dispose: static transaction =>
            {
                transaction.Dispose();
                throw new IOException("pre-commit cleanup fault");
            });
        await using (var adapter = factory.Create(CreateRequest(
            NewRoot("pre-commit-cleanup-failure"),
            "capture-pre-commit-cleanup-failure",
            "artifact-pre-commit-cleanup-failure",
            new DurableSqliteFaultController(
                barrier: static (context, _) => context.Barrier == DurableStorageFaultBarrier.BeforeCommit
                    ? ValueTask.FromException(new IOException("pre-commit fault"))
                    : ValueTask.CompletedTask,
                operations: preCommitCleanupFailure))))
        {
            var result = await adapter.CommitAsync([SinkRecord(1)], CancellationToken.None);
            result.Outcome.Should().Be(DurableCounterCommitOutcome.Unknown);
            result.Error.Should().StartWith("PreCommitRollbackOrCleanup:");
        }
    }

    [Fact]
    public async Task CommitHookCanThrowAfterRealCommitAndRecoveryRetainsUnknownBatch()
    {
        var sourceRoot = NewRoot("commit-after-real-source");
        var recoveryRoot = Path.Combine(_workspace, "commit-after-real-recovery");
        var operations = new DurableSqliteOperationHooks(
            commit: static transaction =>
            {
                transaction.Commit();
                throw new IOException("post-native-commit hook");
            });
        var factory = new DurableSqliteStorageAdapterFactory();
        await using var adapter = factory.Create(CreateRequest(
            sourceRoot,
            "capture-commit-after-real",
            "artifact-commit-after-real",
            new DurableSqliteFaultController(operations: operations)));

        var commit = await adapter.CommitAsync([SinkRecord(1)], CancellationToken.None);
        commit.Outcome.Should().Be(DurableCounterCommitOutcome.Unknown);
        commit.Error.Should().StartWith("CommitStage:");

        var request = new DurableStorageRecoveryRequest(
            "capture-commit-after-real",
            "artifact-commit-after-real",
            "capture-commit-after-real-recovered",
            "artifact-commit-after-real-recovered",
            sourceRoot,
            recoveryRoot,
            "commit-stage-ambiguity");
        var recovered = await factory.RecoverAsync(request, CancellationToken.None);
        var manifest = Manifest(
            request.NewCaptureId,
            request.NewArtifactId,
            recovered.RecoveredMembers,
            finalQuality: null,
            volatileTailUnknown: true,
            derivedFromCaptureId: request.SourceCaptureId,
            recoveryReason: request.Reason);
        await using var reader = factory.OpenReadonly(
            new DurableStorageOpenRequest(recoveryRoot, manifest, Limits()));

        (await reader.QualityAsync(CancellationToken.None)).Should().Be(
            new DurableStorageQualityReport(null, 1, VolatileTailUnknown: true));
        (await reader.SeriesAsync(
            "Synthetic.Provider",
            "counter-0",
            null,
            100,
            CancellationToken.None)).Rows.Should().ContainSingle()
            .Which.Sequence.Should().Be(1);
    }

    [Fact]
    public async Task MaxPageCountFaultActuallyRejectsGrowthAfterInitialCommit()
    {
        var faults = new DurableSqliteFaultController(constrainPagesAfterFirstCommit: true);
        var factory = new DurableSqliteStorageAdapterFactory();
        await using var adapter = factory.Create(CreateRequest(
            NewRoot("f1"),
            "capture-f1",
            "artifact-f1",
            faults));

        var first = Enumerable.Range(1, 64).Select(SinkRecord).ToArray();
        var second = Enumerable.Range(65, 64).Select(SinkRecord).ToArray();
        (await adapter.CommitAsync(first, CancellationToken.None)).Outcome
            .Should().Be(DurableCounterCommitOutcome.Committed);
        var result = await adapter.CommitAsync(second, CancellationToken.None);

        result.Outcome.Should().Be(DurableCounterCommitOutcome.Failed);
        result.Error.Should().Be("SqliteFull");
    }

    [Fact]
    public async Task DirectSinkRejectsMalformedRecordsAndConcurrentWriterEntry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faults = new DurableSqliteFaultController(
            barrier: async (context, _) =>
            {
                if (context.Barrier == DurableStorageFaultBarrier.BeforeBatchWrite)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            });
        var factory = new DurableSqliteStorageAdapterFactory();
        await using var adapter = factory.Create(CreateRequest(
            NewRoot("direct-validation"),
            "capture-direct",
            "artifact-direct",
            faults));

        var first = Task.Run(async () => await adapter.CommitAsync([SinkRecord(1)], CancellationToken.None));
        await entered.Task;
        var concurrent = await Task.Run(
            async () => await adapter.CommitAsync([SinkRecord(2)], CancellationToken.None));
        concurrent.Should().Be(new DurableCounterCommitResult(
            DurableCounterCommitOutcome.Failed,
            "SqliteConcurrentWriter"));
        release.SetResult();
        (await first).Outcome.Should().Be(DurableCounterCommitOutcome.Committed);

        var malformedRecord = SinkRecord(3);
        malformedRecord = malformedRecord with
        {
            Record = malformedRecord.Record with
            {
                IntervalSec = null,
                IntervalState = CounterMetadataState.Valid,
            },
        };
        var malformed = await adapter.CommitAsync([malformedRecord], CancellationToken.None);
        malformed.Should().Be(new DurableCounterCommitResult(
            DurableCounterCommitOutcome.Failed,
            "InvalidSqliteRecord"));
    }

    [Fact]
    public async Task CorruptDatabaseFailsFreshAcquisitionExplicitly()
    {
        var root = NewRoot("corrupt");
        var factory = new DurableSqliteStorageAdapterFactory();
        DurableStoragePackageManifest manifest;
        await using (var adapter = factory.Create(CreateRequest(
            root,
            "capture-corrupt",
            "artifact-corrupt")))
        {
            await using var pipeline = CreatePipeline(adapter);
            pipeline.TryWrite(DurableCounterFixture.Generate(1)).Status.Should().Be(DurableCounterOfferStatus.Accepted);
            (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();
            (await pipeline.FinalizeAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
            var quality = Quality(pipeline.GetAccounting());
            var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
            manifest = Manifest(
                "capture-corrupt",
                "artifact-corrupt",
                preSeal.CanonicalMembers,
                quality,
                volatileTailUnknown: false);
        }

        var database = Path.Combine(root, "canonical", "counters.db");
        using (var stream = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = 100;
            var value = stream.ReadByte();
            value.Should().BeGreaterThanOrEqualTo(0);
            stream.Position = 100;
            stream.WriteByte((byte)(value ^ 0xff));
            stream.Flush(flushToDisk: true);
        }

        Action open = () => factory.OpenReadonly(new DurableStorageOpenRequest(root, manifest, Limits()));
        open.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("SqliteReadonlyOpenFailed");
    }

    [Fact]
    public async Task ExplicitRecoveryCreatesNewPackageAndPreservesEverySourceByte()
    {
        var sourceRoot = NewRoot("recovery-source");
        var recoveryRoot = Path.Combine(_workspace, "recovery-destination");
        var factory = new DurableSqliteStorageAdapterFactory();
        var acknowledgementGap = new DurableSqliteFaultController(
            barrier: static (context, _) =>
                context.Barrier == DurableStorageFaultBarrier.AfterCommitBeforeAcknowledgement
                    && context.BatchOrdinal == 2
                    ? ValueTask.FromException(new IOException("recovery acknowledgement gap"))
                    : ValueTask.CompletedTask);
        await using var adapter = factory.Create(CreateRequest(
            sourceRoot,
            "capture-source",
            "artifact-source",
            acknowledgementGap));
        (await adapter.CommitAsync(
            Enumerable.Range(1, 64).Select(SinkRecord).ToArray(),
            CancellationToken.None)).Outcome.Should().Be(DurableCounterCommitOutcome.Committed);
        (await adapter.CommitAsync(
            Enumerable.Range(65, 64).Select(SinkRecord).ToArray(),
            CancellationToken.None)).Outcome.Should().Be(DurableCounterCommitOutcome.Unknown);
        var sourceWal = Path.Combine(sourceRoot, "canonical", "counters.db-wal");
        File.Exists(sourceWal).Should().BeTrue();
        new FileInfo(sourceWal).Length.Should().BeGreaterThan(0);

        var sourceBefore = Snapshot(sourceRoot);
        var request = new DurableStorageRecoveryRequest(
            "capture-source",
            "artifact-source",
            "capture-recovered",
            "artifact-recovered",
            sourceRoot,
            recoveryRoot,
            "explicit-component-recovery");

        var recovered = await factory.RecoverAsync(request, CancellationToken.None);

        DurableStorageRecoveryRules.Validate(request, recovered);
        recovered.RecoveredMembers.Should().ContainSingle();
        Snapshot(sourceRoot).Should().BeEquivalentTo(sourceBefore);
        var recoveredManifest = Manifest(
            "capture-recovered",
            "artifact-recovered",
            recovered.RecoveredMembers,
            finalQuality: null,
            volatileTailUnknown: true,
            derivedFromCaptureId: "capture-source",
            recoveryReason: request.Reason);
        await using var reader = factory.OpenReadonly(
            new DurableStorageOpenRequest(recoveryRoot, recoveredManifest, Limits()));
        var quality = await reader.QualityAsync(CancellationToken.None);
        quality.FinalPipelineQuality.Should().BeNull();
        quality.VolatileTailUnknown.Should().BeTrue();
        quality.RetainedRecords.Should().Be(128);
        (await reader.SummaryAsync(CancellationToken.None))
            .Sum(static row => row.RetainedCount).Should().Be(128);
        recovered.RecoveredMembers[0].Length.Should().BeGreaterThan(0);
    }

    private static DurableStorageAdapterCreateRequest CreateRequest(
        string root,
        string captureId = "capture-q1",
        string artifactId = "artifact-q1",
        IDurableStorageFaultController? faults = null)
    {
        using var document = JsonDocument.Parse("""{"profile":"P1"}""");
        return new DurableStorageAdapterCreateRequest(
            captureId,
            artifactId,
            root,
            document.RootElement.Clone(),
            Limits(),
            faults ?? NoDurableStorageFaults.Instance);
    }

    private static DurableCounterPipeline CreatePipeline(IDurableCounterSink sink)
        => new(
            Limits(),
            new DurableCounterGlobalBudget(Limits()),
            sink,
            ImmediateCounterBatchAgeWaiter.Instance);

    private static DurableCounterPipelineLimits Limits()
        => new(BatchMaxAge: TimeSpan.FromMilliseconds(100));

    private static DurableCounterQualityReport Quality(DurableCounterAccounting accounting)
        => new(
            accounting,
            accounting.UnknownCommitOutcome > 0,
            "unknown",
            "coverageGap is observed source timing only; unknown is not inferred loss");

    private static DurableCounterAccounting Accounting(long committed, long failed, long unknown)
        => new(
            Offered: 1,
            Rejected: 0,
            Admitted: 1,
            InCopy: 0,
            Queued: 0,
            ActiveBatch: 0,
            Committed: committed,
            FailedAfterAdmission: failed,
            AbandonedKnown: 0,
            UnknownCommitOutcome: unknown,
            OwnedRecords: 0,
            OwnedBytes: 0,
            PeakOwnedRecords: 1,
            PeakOwnedBytes: 512,
            AdmissionCancelled: false,
            Rejections: new Dictionary<string, long>(StringComparer.Ordinal));

    private static DurableCounterSinkRecord SinkRecord(int sequence)
    {
        var key = (sequence - 1) % 8;
        var record = new DurableCounterRecord(
            sequence,
            "Synthetic.Provider",
            $"counter-{key}",
            $"Synthetic counter {key}",
            "items",
            100 - (sequence % 50),
            key % 2 == 0 ? CounterKind.Mean : CounterKind.Sum,
            1,
            CounterMetadataState.Valid,
            TimeSpan.TicksPerSecond,
            CounterMetadataState.Valid,
            checked((sequence - 1L) * TimeSpan.TicksPerMillisecond * 10),
            "fixture-relative-100ns",
            "dc5-sqlite-test",
            sequence <= 8 ? CoverageGapState.Unknown : CoverageGapState.NoGap,
            CounterResetState.Unknown,
            4_096);
        return new DurableCounterSinkRecord(record, new byte[4_096]);
    }

    private static DurableCounterExpected ToExpected(DurableCounterRecord record)
        => new(
            record.Sequence,
            DurableCounterOfferStatus.Accepted,
            null,
            record.Provider,
            record.Name,
            record.Value,
            record.Kind,
            record.SourceTimeTicks,
            record.CoverageGap,
            record.IntervalState,
            record.DisplayScaleState,
            record.ResetState);

    private static DurableStoragePackageManifest Manifest(
        string captureId,
        string artifactId,
        IReadOnlyList<DurableStorageMember> members,
        DurableCounterQualityReport? finalQuality,
        bool volatileTailUnknown,
        string? derivedFromCaptureId = null,
        string? recoveryReason = null)
        => new(
            DurableStorageExperimentVersions.PackageContract,
            DurableStorageExperimentVersions.RecordSchema,
            captureId,
            artifactId,
            DateTimeOffset.UnixEpoch,
            DurableSqliteStorageAdapterFactory.SqliteIdentity,
            new string('a', 64),
            new string('b', 64),
            "69e8274ed97d7892f85a38c940c214077f11d301",
            members,
            derivedFromCaptureId,
            recoveryReason,
            volatileTailUnknown,
            finalQuality);

    private string NewRoot(string name)
    {
        var path = Path.Combine(_workspace, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static Dictionary<string, FileStamp> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path =>
                {
                    using var stream = File.OpenRead(path);
                    return new FileStamp(
                        stream.Length,
                        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
                },
                StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "global.json")))
        {
            current = current.Parent;
        }
        return current?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed record FileStamp(long Length, string Sha256);
}
