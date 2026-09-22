using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.AppendFirst;

using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Tests.DurableCounterSpike;

/// <summary>
/// Component tests for the candidate B (append-first) storage adapter. These are
/// deterministic, in-process, no-kill tests against independently authored DC4
/// expectations (<see cref="DurableCounterOracle"/>) plus direct frame-format,
/// fault-barrier, recovery, and lifecycle checks. They are not the 35-case DC5
/// campaign and must not be read as comparative A-vs-B performance evidence.
/// </summary>
public sealed class DurableAppendFirstStorageAdapterTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        AppContext.BaseDirectory, "dc5-append-first", Guid.NewGuid().ToString("N"));

    public DurableAppendFirstStorageAdapterTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Fact]
    public async Task Q1_CommittedThroughRealPipeline_MatchesIndependentOracleAfterReopen()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        await using var adapter = factory.Create(CreateRequest(staging, limits));

        var budget = new DurableCounterGlobalBudget(limits);
        await using (var pipeline = new DurableCounterPipeline(limits, budget, adapter, ImmediateCounterBatchAgeWaiter.Instance))
        {
            var observations = DurableCounterFixture.GenerateQ1();
            for (var offset = 0; offset < observations.Count; offset += 64)
            {
                foreach (var observation in observations.Skip(offset).Take(64))
                {
                    pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
                }
                await WaitUntilAsync(() => pipeline.GetAccounting().Committed >= Math.Min(offset + 64, observations.Count));
            }
            (await pipeline.DrainAsync(TimeSpan.FromSeconds(10))).CompletedWithinTimeout.Should().BeTrue();
            pipeline.State.Should().Be(DurableCounterPipelineState.Completed);

            var quality = QuietQuality(pipeline.GetAccounting());

            // Reader is unavailable during acquisition -- before FinalizePreSealAsync runs.
            var beforeSealAccess = () => adapter.Reader;
            beforeSealAccess.Should().Throw<InvalidOperationException>();

            var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
            preSeal.FinalBytes.Should().BeLessThanOrEqualTo(268_435_456);

            // Reader becomes usable only after that finalization succeeds, and carries the
            // exact terminal quality that was passed in -- not a re-derived approximation.
            var liveQuality = await adapter.Reader.QualityAsync(CancellationToken.None);
            liveQuality.VolatileTailUnknown.Should().BeFalse();
            liveQuality.FinalPipelineQuality.Should().Be(quality);
            liveQuality.RetainedRecords.Should().Be(1_024);

            var manifest = BuildManifest(staging, preSeal, quality, volatileTailUnknown: false);
            await using var reader = factory.OpenReadonly(new DurableStorageOpenRequest(staging, manifest, limits));

            var summary = await reader.SummaryAsync(CancellationToken.None);
            var expectedByKey = DurableCounterOracle.Q1()
                .Where(static row => row.Status == DurableCounterOfferStatus.Accepted)
                .GroupBy(static row => row.Name!)
                .ToDictionary(static g => g.Key, static g => g.OrderBy(static r => r.Sequence).ToArray());

            summary.Should().HaveCount(8);
            summary.Sum(static row => row.RetainedCount).Should().Be(1_024);
            foreach (var row in summary)
            {
                var expectedRows = expectedByKey[row.Name];
                row.RetainedCount.Should().Be(expectedRows.Length);
                row.First.Should().Be(expectedRows[0].Value!.Value);
                row.Last.Should().Be(expectedRows[^1].Value!.Value);
                row.Min.Should().Be(expectedRows.Min(static r => r.Value!.Value));
                row.Max.Should().Be(expectedRows.Max(static r => r.Value!.Value));
                row.UnknownCoverageCount.Should().Be(1);
                row.GapCount.Should().Be(0);
            }

            var firstPage = await reader.SeriesAsync("Synthetic.Provider", "counter-0", null, 100, CancellationToken.None);
            firstPage.Rows.Should().HaveCount(100);
            firstPage.Rows.Should().OnlyContain(static r => r.ResetState == CounterResetState.Unknown);

            // Full 17-logical-field parity check on the first decoded record, not just the
            // handful of fields the aggregated oracle carries: everything the fixture
            // deterministically controls must decode back exactly, including the fields
            // the summary/oracle comparison above never touches (display name, unit,
            // interval, display-scale ticks, clock domain/origin, and encoded byte length).
            var firstRow = firstPage.Rows[0];
            firstRow.Sequence.Should().Be(1);
            firstRow.Provider.Should().Be("Synthetic.Provider");
            firstRow.Name.Should().Be("counter-0");
            firstRow.DisplayName.Should().Be("Synthetic counter 0");
            firstRow.Unit.Should().Be("items");
            firstRow.Value.Should().Be(100 - (1 % 50));
            firstRow.Kind.Should().Be(CounterKind.Mean);
            firstRow.IntervalSec.Should().Be(1.0);
            firstRow.IntervalState.Should().Be(CounterMetadataState.Valid);
            firstRow.DisplayScaleTicks.Should().Be(TimeSpan.TicksPerSecond);
            firstRow.DisplayScaleState.Should().Be(CounterMetadataState.Valid);
            firstRow.SourceTimeTicks.Should().Be(0);
            firstRow.ClockDomain.Should().Be("fixture-relative-100ns");
            firstRow.ClockOrigin.Should().Be("dc3-rev3-generated");
            firstRow.CoverageGap.Should().Be(CoverageGapState.Unknown);
            firstRow.ResetState.Should().Be(CounterResetState.Unknown);
            firstRow.EncodedBytes.Should().Be(512);
            for (var i = 0; i < firstPage.Rows.Count; i++)
            {
                AssertGeneratedRecord(firstPage.Rows[i], 1 + 8L * i);
            }
            for (var key = 0; key < 8; key++)
            {
                var page = await reader.SeriesAsync("Synthetic.Provider", $"counter-{key}", null, 1, CancellationToken.None);
                AssertGeneratedRecord(page.Rows.Single(), key + 1);
            }

            var secondPage = await reader.SeriesAsync(
                "Synthetic.Provider", "counter-0", firstPage.NextAfterSequence, 100, CancellationToken.None);
            secondPage.Rows.Should().HaveCount(28);
            secondPage.NextAfterSequence.Should().BeNull();

            var readerQuality = await reader.QualityAsync(CancellationToken.None);
            DurableStorageQualityRules.Validate(readerQuality, manifest);
            readerQuality.RetainedRecords.Should().Be(1_024);
        }
    }

    [Fact]
    public async Task Q2_SumDecreaseIsNeverTreatedAsResetAndDistinctKeyIsBoundedByGapNotReset()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        await using var adapter = factory.Create(CreateRequest(staging, limits));
        var budget = new DurableCounterGlobalBudget(limits);

        await using (var pipeline = new DurableCounterPipeline(limits, budget, adapter, ImmediateCounterBatchAgeWaiter.Instance))
        {
            var repositoryRoot = FindRepositoryRoot();
            var observations = DurableCounterFixture.LoadQ2(repositoryRoot);
            var expected = DurableCounterOracle.Q2();
            for (var i = 0; i < observations.Count; i++)
            {
                var result = pipeline.TryWrite(observations[i]);
                result.Status.Should().Be(expected[i].Status);
            }
            (await pipeline.DrainAsync(TimeSpan.FromSeconds(5))).CompletedWithinTimeout.Should().BeTrue();

            var quality = QuietQuality(pipeline.GetAccounting());
            var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
            var manifest = BuildManifest(staging, preSeal, quality, volatileTailUnknown: false);
            await using var reader = factory.OpenReadonly(new DurableStorageOpenRequest(staging, manifest, limits));

            // Ordinal 9's Sum value is lower than ordinal 3's Sum -- the point is: whatever
            // the value trend, resetState must remain Unknown -- a decrease is never
            // fabricated reset evidence, and only the fixture's own explicit gap marker
            // (sequence 9) is ever surfaced as a gap.
            var sumSeries = await reader.SeriesAsync("Synthetic.Provider", "counter-1", null, 100, CancellationToken.None);
            sumSeries.Rows.Should().OnlyContain(static r => r.ResetState == CounterResetState.Unknown);
            var gapRow = sumSeries.Rows.Single(r => r.Sequence == 9);
            gapRow.CoverageGap.Should().Be(CoverageGapState.Gap);
        }
    }

    [Fact]
    public void FrameCodec_RoundTripsAndDetectsTruncationAndCorruption()
    {
        var owned = new (long Sequence, byte[] Payload)[]
        {
            (1, "record-one"u8.ToArray()),
            (2, "record-two-longer-payload"u8.ToArray()),
        };
        var frame = DurableAppendFirstFrame.Encode(owned);

        var path = Path.Combine(_workspace, "frame.bin");
        File.WriteAllBytes(path, frame);

        using (var stream = File.OpenRead(path))
        {
            var scan = DurableAppendFirstFrame.ReadNext(stream);
            scan.Outcome.Should().Be(DurableAppendFirstFrameOutcome.Valid);
            scan.Records.Should().HaveCount(2);
            scan.Records![0].Sequence.Should().Be(1);
            scan.Records![1].Payload.Should().Equal("record-two-longer-payload"u8.ToArray());

            var cleanEnd = DurableAppendFirstFrame.ReadNext(stream);
            cleanEnd.Outcome.Should().Be(DurableAppendFirstFrameOutcome.CleanEnd);
        }

        // Truncate mid-footer: an otherwise-complete frame missing its commit footer.
        var truncated = frame[..^3];
        File.WriteAllBytes(path, truncated);
        using (var stream = File.OpenRead(path))
        {
            DurableAppendFirstFrame.ReadNext(stream).Outcome.Should().Be(DurableAppendFirstFrameOutcome.Truncated);
        }

        // Flip a checksum byte: complete length, but corrupt content.
        var corrupted = (byte[])frame.Clone();
        corrupted[^(DurableAppendFirstFrame.FooterBytes + 1)] ^= 0xFF;
        File.WriteAllBytes(path, corrupted);
        using (var stream = File.OpenRead(path))
        {
            DurableAppendFirstFrame.ReadNext(stream).Outcome.Should().Be(DurableAppendFirstFrameOutcome.Corrupt);
        }
    }

    [Fact]
    public async Task BeforeCommitFault_LeavesNoTraceAndAfterCommitFault_IsUnknownNeverFailed()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();

        // F2: fault strictly before the commit footer exists on disk.
        {
            var staging = NewStagingRoot();
            var faults = new ThrowOnceFaultController(DurableStorageFaultBarrier.BeforeCommit);
            await using var adapter = factory.Create(CreateRequest(staging, limits, faults));
            var records = OneRecordBatch(sequence: 1);

            var act = async () => await adapter.CommitAsync(records, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();

            var canonicalPath = Path.Combine(staging, "canonical", "records.bin");
            new FileInfo(canonicalPath).Length.Should().Be(0, "a pre-commit fault must leave no partial frame behind");
        }

        // F3: fault strictly after the footer is flushed -- durable, but ack-interrupted.
        {
            var staging = NewStagingRoot();
            var faults = new ThrowOnceFaultController(DurableStorageFaultBarrier.AfterCommitBeforeAcknowledgement);
            await using var adapter = factory.Create(CreateRequest(staging, limits, faults));
            var records = OneRecordBatch(sequence: 1);

            var result = await adapter.CommitAsync(records, CancellationToken.None);
            result.Outcome.Should().Be(DurableCounterCommitOutcome.Unknown, "post-commit ack interruption is never a Failed outcome");

            var canonicalPath = Path.Combine(staging, "canonical", "records.bin");
            new FileInfo(canonicalPath).Length.Should().BeGreaterThan(0, "the batch is durably flushed despite the ack fault");

            using var frameStream = File.OpenRead(canonicalPath);
            DurableAppendFirstFrame.ReadNext(frameStream).Outcome.Should().Be(DurableAppendFirstFrameOutcome.Valid);
        }
    }

    [Fact]
    public async Task RealPipeline_PostCommitAckFault_RecordsUnknownAndRetainedRowsStayInTheConservativeRange()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        var faults = new ThrowOnceFaultController(DurableStorageFaultBarrier.AfterCommitBeforeAcknowledgement);
        await using var adapter = factory.Create(CreateRequest(staging, limits, faults));
        var budget = new DurableCounterGlobalBudget(limits);

        // Gate the background writer until every record is enqueued so that all 10 land
        // in a single batch deterministically -- without this the writer can race ahead
        // and commit a smaller first batch before the remaining TryWrite calls complete.
        var writerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var pipeline = new DurableCounterPipeline(
            limits, budget, adapter, ImmediateCounterBatchAgeWaiter.Instance, writerGate.Task))
        {
            foreach (var observation in DurableCounterFixture.GenerateQ1().Take(10))
            {
                pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
            }
            writerGate.SetResult();
            (await pipeline.DrainAsync(TimeSpan.FromSeconds(5))).CompletedWithinTimeout.Should().BeTrue();
            pipeline.State.Should().Be(DurableCounterPipelineState.UnknownCommitOutcome);

            var accounting = pipeline.GetAccounting();
            accounting.Committed.Should().Be(0, "the pipeline never counts an ack-interrupted batch as a known Committed outcome");
            accounting.UnknownCommitOutcome.Should().Be(10);

            var quality = QuietQuality(accounting);
            var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
            var liveQuality = await adapter.Reader.QualityAsync(CancellationToken.None);

            // The batch is physically durable (its footer was flushed before the ack fault
            // fired), so the index legitimately retains all 10 rows -- but that retained
            // count must still fall inside [Committed, Committed + UnknownCommitOutcome],
            // i.e. [0, 10], the same conservative bound the shared quality rules enforce.
            liveQuality.RetainedRecords.Should().Be(10);
            liveQuality.RetainedRecords.Should().BeInRange(accounting.Committed, accounting.Committed + accounting.UnknownCommitOutcome);
            DurableStorageQualityRules.Validate(liveQuality);
            preSeal.FinalBytes.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task StorageFullFault_RejectsBeforeAnyBytesAreWritten()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        var faults = new ThrowOnceFaultController(DurableStorageFaultBarrier.StorageFullNextBatch, new IOException("synthetic storage-full"));
        await using var adapter = factory.Create(CreateRequest(staging, limits, faults));

        var act = async () => await adapter.CommitAsync(OneRecordBatch(1), CancellationToken.None);
        await act.Should().ThrowAsync<IOException>();

        new FileInfo(Path.Combine(staging, "canonical", "records.bin")).Length.Should().Be(0);
    }

    [Fact]
    public async Task StorageFullFault_NonIOExceptionFromControllerIsSurfacedAsDeclaredIOException()
    {
        // F1's seam is the write stream itself: whatever the fault controller throws is
        // always surfaced as a declared storage-full IOException from that write attempt,
        // not as whatever arbitrary exception type the controller happened to raise.
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        var faults = new ThrowOnceFaultController(
            DurableStorageFaultBarrier.StorageFullNextBatch, new InvalidOperationException("non-IO fault from the controller"));
        await using var adapter = factory.Create(CreateRequest(staging, limits, faults));

        var act = async () => await adapter.CommitAsync(OneRecordBatch(1), CancellationToken.None);
        (await act.Should().ThrowAsync<IOException>()).Which.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task IndexBuildFailure_OnCorruptedSealedLog_CannotPublishASealAndReaderStaysUnavailable()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        var adapter = factory.Create(CreateRequest(staging, limits));
        await adapter.CommitAsync(OneRecordBatch(1), CancellationToken.None);

        var canonicalPath = Path.Combine(staging, "canonical", "records.bin");

        // Simulate on-disk corruption of the already-committed frame (independent of the
        // adapter's own write path) by flipping a checksum byte directly.
        using (var raw = new FileStream(canonicalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            raw.Seek(-(DurableAppendFirstFrame.FooterBytes + 1), SeekOrigin.End);
            var b = raw.ReadByte();
            raw.Seek(-1, SeekOrigin.Current);
            raw.WriteByte((byte)(b ^ 0xFF));
        }

        var quality = QuietQuality(new DurableCounterAccounting(
            1, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, false, new Dictionary<string, long>()));

        var act = async () => await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        (await act.Should().ThrowAsync<DurableStorageExperimentException>()).Which.Code.Should().Be("IndexBuildSourceNotClean");

        // The half-built derived index must not be left behind as if it were a real seal.
        File.Exists(Path.Combine(staging, "query", "index.db")).Should().BeFalse();

        var readerAccess = () => adapter.Reader;
        readerAccess.Should().Throw<InvalidOperationException>("a failed seal must never make the Reader usable");

        // A repeated finalize attempt must not be treated as a fresh chance to seal.
        var secondAttempt = async () => await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        await secondAttempt.Should().ThrowAsync<InvalidOperationException>();

        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task CrossFrameSequenceRegression_OnCommit_IsRejectedBeforeAnyWriteAndDoesNotMutateTheLog()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        await using var adapter = factory.Create(CreateRequest(staging, limits));

        await adapter.CommitAsync(OneRecordBatch(sequence: 5), CancellationToken.None);
        var canonicalPath = Path.Combine(staging, "canonical", "records.bin");
        var lengthAfterFirstCommit = new FileInfo(canonicalPath).Length;
        lengthAfterFirstCommit.Should().BeGreaterThan(0);

        // A duplicate (same sequence) and a regression (lower sequence) must both be
        // rejected before any byte is written -- never silently accepted into the
        // durable log only to fail later at the derived index's primary key.
        var duplicate = async () => await adapter.CommitAsync(OneRecordBatch(sequence: 5), CancellationToken.None);
        (await duplicate.Should().ThrowAsync<DurableStorageExperimentException>()).Which.Code.Should().Be("SequenceRegression");

        var regression = async () => await adapter.CommitAsync(OneRecordBatch(sequence: 3), CancellationToken.None);
        (await regression.Should().ThrowAsync<DurableStorageExperimentException>()).Which.Code.Should().Be("SequenceRegression");

        new FileInfo(canonicalPath).Length.Should().Be(lengthAfterFirstCommit, "a rejected batch must never mutate the durable log");
    }

    [Fact]
    public async Task Recovery_ScansOnlyValidFramesAndNeverMutatesSource()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        var adapter = factory.Create(CreateRequest(staging, limits));
        var budget = new DurableCounterGlobalBudget(limits);

        await using (var pipeline = new DurableCounterPipeline(limits, budget, adapter, ImmediateCounterBatchAgeWaiter.Instance))
        {
            foreach (var observation in DurableCounterFixture.GenerateQ1().Take(200))
            {
                pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
            }
            (await pipeline.DrainAsync(TimeSpan.FromSeconds(5))).CompletedWithinTimeout.Should().BeTrue();
            var quality = QuietQuality(pipeline.GetAccounting());
            await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        }
        await adapter.DisposeAsync();

        var canonicalPath = Path.Combine(staging, "canonical", "records.bin");
        var beforeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(canonicalPath)));
        var beforeLength = new FileInfo(canonicalPath).Length;

        // Append trailing garbage after the last valid frame to simulate an
        // in-flight/incomplete batch left over from an interrupted process. This is a
        // legitimate incomplete tail (Truncated), not corruption (Corrupt): recovery must
        // keep the verified prefix and report the tail as unknown, not throw.
        using (var stream = new FileStream(canonicalPath, FileMode.Append))
        {
            stream.Write(new byte[] { 1, 2, 3, 4, 5 });
        }

        var recoveryStaging = Path.Combine(_workspace, "recovery-staging");
        Directory.CreateDirectory(recoveryStaging);
        var result = await factory.RecoverAsync(
            new DurableStorageRecoveryRequest(
                SourceCaptureId: "capture-1",
                SourceArtifactId: "artifact-1",
                NewCaptureId: "capture-1-recovered",
                NewArtifactId: "artifact-1-recovered",
                SourcePackageRoot: staging,
                RecoveryStagingRoot: recoveryStaging,
                Reason: "component-test-simulated-truncated-tail"),
            CancellationToken.None);

        result.VolatileTailUnknown.Should().BeTrue();
        // Recovered members are emitted as ordinary canonical/query members (not a
        // candidate-private RecoveryOutput-only shape) so a host-built manifest can hand
        // them straight to the public OpenReadonly role lookup -- see the dedicated
        // round-trip test below.
        result.RecoveredMembers.Should().Contain(m => m.Role == DurableStorageMemberRole.CanonicalData);
        result.RecoveredMembers.Should().Contain(m => m.Role == DurableStorageMemberRole.QueryIndex);
        result.CaptureId.Should().Be("capture-1-recovered");
        result.DerivedFromCaptureId.Should().Be("capture-1");

        // Source bytes must be completely untouched by recovery.
        new FileInfo(canonicalPath).Length.Should().Be(beforeLength + 5);
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(canonicalPath)))
            .Should().NotBe(beforeHash); // sanity: our own test-appended garbage is still there, untouched by recovery

        var recoveredIndexPath = Path.Combine(recoveryStaging, "query", "index.db");
        await using var recoveredReader = DurableAppendFirstQueryIndex.OpenReadOnly(recoveredIndexPath, limits);
        var recoveredSummary = await recoveredReader.SummaryAsync(CancellationToken.None);
        recoveredSummary.Sum(static r => r.RetainedCount).Should().Be(200);
    }

    [Fact]
    public async Task Recovery_OnCorruptedSource_ThrowsExplicitlyAndNeverLaundersAVerifiedPrefixClaim()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        var adapter = factory.Create(CreateRequest(staging, limits));
        await adapter.CommitAsync(OneRecordBatch(1), CancellationToken.None);
        var quality = QuietQuality(new DurableCounterAccounting(
            1, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, false, new Dictionary<string, long>()));
        await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        await adapter.DisposeAsync();

        var canonicalPath = Path.Combine(staging, "canonical", "records.bin");
        var bytes = File.ReadAllBytes(canonicalPath);
        // Flip a checksum byte inside the frame: complete length, but corrupt content --
        // distinct from an incomplete trailing write.
        bytes[^(DurableAppendFirstFrame.FooterBytes + 1)] ^= 0xFF;
        File.WriteAllBytes(canonicalPath, bytes);

        var recoveryStaging = Path.Combine(_workspace, "recovery-staging-corrupt");
        Directory.CreateDirectory(recoveryStaging);

        var act = async () => await factory.RecoverAsync(
            new DurableStorageRecoveryRequest(
                "capture-1", "artifact-1", "capture-1-recovered", "artifact-1-recovered",
                staging, recoveryStaging, "component-test-corrupted-source"),
            CancellationToken.None);
        (await act.Should().ThrowAsync<DurableStorageExperimentException>()).Which.Code.Should().Be("RecoveryCorruptedSource");

        // Handles must be released and the staging directory must be left deletable, not
        // half-published with a misleadingly "clean" result.
        Directory.Delete(recoveryStaging, recursive: true);
    }

    [Fact]
    public async Task Recovery_CrossFrameSequenceRegression_IsRejectedTypedBeforeInsertion()
    {
        // Hand-craft a canonical log with two independently-valid frames whose sequences
        // overlap across the frame boundary -- something the adapter's own watermark
        // check prevents during ingestion, so it must be exercised directly here. Payloads
        // must be genuine encoded records (not arbitrary bytes) so that a well-formed
        // frame's own decode succeeds and the regression is reached at the frame boundary.
        var frame1 = DurableAppendFirstFrame.Encode(new (long, byte[])[]
        {
            (1, EncodedRecord(1)), (2, EncodedRecord(2)), (3, EncodedRecord(3)),
        });
        var frame2 = DurableAppendFirstFrame.Encode(new (long, byte[])[]
        {
            (2, EncodedRecord(2)), (4, EncodedRecord(4)),
        });

        var staging = NewStagingRoot();
        var canonicalDirectory = Path.Combine(staging, "canonical");
        Directory.CreateDirectory(canonicalDirectory);
        File.WriteAllBytes(Path.Combine(canonicalDirectory, "records.bin"), [.. frame1, .. frame2]);

        var factory = new DurableAppendFirstStorageAdapterFactory();
        var recoveryStaging = Path.Combine(_workspace, "recovery-staging-regression");
        Directory.CreateDirectory(recoveryStaging);

        var act = async () => await factory.RecoverAsync(
            new DurableStorageRecoveryRequest(
                "capture-1", "artifact-1", "capture-1-recovered", "artifact-1-recovered",
                staging, recoveryStaging, "component-test-crossframe-regression"),
            CancellationToken.None);
        (await act.Should().ThrowAsync<DurableStorageExperimentException>()).Which.Code.Should().Be("RecoveredSequenceRegression");

        Directory.Delete(recoveryStaging, recursive: true);
    }

    [Fact]
    public async Task Recovery_CancelledMidScan_ReleasesAllHandlesAndLeavesStagingDeletable()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        var adapter = factory.Create(CreateRequest(staging, limits));
        await adapter.CommitAsync(Enumerable.Range(1, 16).SelectMany(i => OneRecordBatch(i)).ToArray(), CancellationToken.None);
        await adapter.CommitAsync(Enumerable.Range(17, 16).SelectMany(i => OneRecordBatch(i)).ToArray(), CancellationToken.None);
        await adapter.DisposeAsync();

        var recoveryStaging = Path.Combine(_workspace, "recovery-staging-cancel");
        Directory.CreateDirectory(recoveryStaging);
        using var cts = new CancellationTokenSource();
        var copiedRecords = 0;
        var recoveryFactory = new DurableAppendFirstStorageAdapterFactory(count =>
        {
            copiedRecords = count;
            count.Should().Be(16);
            new FileInfo(Path.Combine(recoveryStaging, "canonical", "records.bin")).Length.Should().BeGreaterThan(0);
            new FileInfo(Path.Combine(recoveryStaging, "query", "index.db")).Length.Should().BeGreaterThan(0);
            cts.Cancel();
        });

        var act = async () => await recoveryFactory.RecoverAsync(
            new DurableStorageRecoveryRequest(
                "capture-1", "artifact-1", "capture-1-recovered", "artifact-1-recovered",
                staging, recoveryStaging, "component-test-cancelled-recovery"),
            cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        copiedRecords.Should().Be(16);
        Directory.EnumerateFiles(recoveryStaging, "*", SearchOption.AllDirectories).Should().BeEmpty();

        // No lingering open handles: the staging directory must be fully deletable.
        var deleteAfterCancel = () => Directory.Delete(recoveryStaging, recursive: true);
        deleteAfterCancel.Should().NotThrow("a cancelled recovery must release every handle it opened");
    }

    [Fact]
    public async Task Recovery_PublicFactoryRoundTrip_OpensWithNewIdsNullTerminalQualityAndUnknownTail()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        var adapter = factory.Create(CreateRequest(staging, limits));
        var budget = new DurableCounterGlobalBudget(limits);
        await using (var pipeline = new DurableCounterPipeline(limits, budget, adapter, ImmediateCounterBatchAgeWaiter.Instance))
        {
            foreach (var observation in DurableCounterFixture.GenerateQ1().Take(64))
            {
                pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
            }
            (await pipeline.DrainAsync(TimeSpan.FromSeconds(5))).CompletedWithinTimeout.Should().BeTrue();
            var quality = QuietQuality(pipeline.GetAccounting());
            await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        }
        await adapter.DisposeAsync();

        var recoveryStaging = Path.Combine(_workspace, "recovery-staging-public-roundtrip");
        Directory.CreateDirectory(recoveryStaging);
        var recovery = await factory.RecoverAsync(
            new DurableStorageRecoveryRequest(
                "capture-1", "artifact-1", "capture-1-recovered", "artifact-1-recovered",
                staging, recoveryStaging, "component-test-public-roundtrip"),
            CancellationToken.None);

        // A host builds an ordinary manifest around the recovered members and reopens it
        // through the exact same public factory entry point a normal sealed package uses --
        // no private bypass, no candidate-specific reopen path.
        var recoveredManifest = new DurableStoragePackageManifest(
            ContractVersion: DurableStorageExperimentVersions.PackageContract,
            RecordSchemaVersion: DurableStorageExperimentVersions.RecordSchema,
            CaptureId: recovery.CaptureId,
            ArtifactId: recovery.ArtifactId,
            CreatedAt: DateTimeOffset.UtcNow,
            Adapter: factory.Identity,
            ProtocolJsonSha256: new string('0', 64),
            FixtureSha256: new string('0', 64),
            PipelineCommit: "component-test",
            Members: recovery.RecoveredMembers,
            DerivedFromCaptureId: recovery.DerivedFromCaptureId,
            RecoveryReason: recovery.RecoveryReason,
            VolatileTailUnknown: recovery.VolatileTailUnknown,
            FinalPipelineQuality: null);

        await using var reopened = factory.OpenReadonly(new DurableStorageOpenRequest(recoveryStaging, recoveredManifest, limits));
        var summary = await reopened.SummaryAsync(CancellationToken.None);
        summary.Sum(static r => r.RetainedCount).Should().Be(64);

        var quality2 = await reopened.QualityAsync(CancellationToken.None);
        quality2.FinalPipelineQuality.Should().BeNull();
        quality2.VolatileTailUnknown.Should().BeTrue();
        quality2.RetainedRecords.Should().Be(64);

        recovery.CaptureId.Should().NotBe("capture-1");
        recovery.ArtifactId.Should().NotBe("artifact-1");
    }

    [Fact]
    public async Task Reopen_IsStrictlyReadOnlyAndCreatesNoAdditionalFiles()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        var adapter = factory.Create(CreateRequest(staging, limits));
        var budget = new DurableCounterGlobalBudget(limits);
        DurableStoragePreSealResult preSeal;
        DurableCounterQualityReport quality;

        await using (var pipeline = new DurableCounterPipeline(limits, budget, adapter, ImmediateCounterBatchAgeWaiter.Instance))
        {
            foreach (var observation in DurableCounterFixture.GenerateQ1().Take(64))
            {
                pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
            }
            (await pipeline.DrainAsync(TimeSpan.FromSeconds(5))).CompletedWithinTimeout.Should().BeTrue();
            quality = QuietQuality(pipeline.GetAccounting());
            preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        }
        await adapter.DisposeAsync();

        var manifest = BuildManifest(staging, preSeal, quality, volatileTailUnknown: false);
        var beforeFiles = Directory.GetFiles(staging, "*", SearchOption.AllDirectories).OrderBy(static f => f).ToArray();
        var beforeHashes = beforeFiles.ToDictionary(
            static f => f, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

        await using (var reader = factory.OpenReadonly(new DurableStorageOpenRequest(staging, manifest, limits)))
        {
            await reader.SummaryAsync(CancellationToken.None);
            await reader.SeriesAsync("Synthetic.Provider", "counter-0", null, 10, CancellationToken.None);
            await reader.QualityAsync(CancellationToken.None);
        }

        var afterFiles = Directory.GetFiles(staging, "*", SearchOption.AllDirectories).OrderBy(static f => f).ToArray();
        afterFiles.Should().Equal(beforeFiles, "an ordinary reopen must never create wal/shm/journal/index/cache files");
        foreach (var file in afterFiles)
        {
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))
                .Should().Be(beforeHashes[file], "an ordinary reopen must never mutate canonical or query files");
        }
    }

    [Fact]
    public void CapacityGuard_RejectsGrowthThatWouldExceedTheFrozenPackageCap()
    {
        var guard = new DurableAppendFirstCapacityGuard();
        guard.ReserveCanonicalGrowth(DurableAppendFirstCapacityGuard.CapBytes - 100);
        var act = () => guard.ReserveCanonicalGrowth(200);
        act.Should().Throw<DurableAppendFirstCapacityExceededException>();

        var guard2 = new DurableAppendFirstCapacityGuard();
        guard2.ReserveCanonicalGrowth(1_000);
        var act2 = () => guard2.ObserveQueryBytes(DurableAppendFirstCapacityGuard.CapBytes);
        act2.Should().Throw<DurableAppendFirstCapacityExceededException>();
    }

    [Fact]
    public void Configuration_RejectsAnyProfileOtherThanP1()
    {
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var limits = new DurableCounterPipelineLimits();
        var staging = NewStagingRoot();
        var badConfiguration = JsonDocument.Parse("""{"profile":"P2"}""").RootElement;
        var act = () => factory.Create(new DurableStorageAdapterCreateRequest(
            "capture", "artifact", staging, badConfiguration, limits, NoDurableStorageFaults.Instance));
        act.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("UnsupportedConfigurationProfile");
    }

    [Fact]
    public async Task LatePreSealFailureDoesNotPublishReaderOrLeaveIndex()
    {
        var limits = new DurableCounterPipelineLimits(RecordEncodedBytes: 8_192);
        var staging = NewStagingRoot();
        await using var adapter = new DurableAppendFirstStorageAdapterFactory().Create(CreateRequest(staging, limits));
        await adapter.CommitAsync(OneRecordBatch(1), CancellationToken.None);
        var quality = CommittedQuality(1);

        Func<Task> finalize = async () => await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        (await finalize.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("ProtocolLimitMismatch");
        Action reader = () => _ = adapter.Reader;
        reader.Should().Throw<InvalidOperationException>();
        File.Exists(Path.Combine(staging, "query", "index.db")).Should().BeFalse();
        await finalize.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StorageFullWriteSeamDoesNotRelabelCancellation()
    {
        using var cts = new CancellationTokenSource();
        var cancellation = new OperationCanceledException(cts.Token);
        var faults = new ThrowOnceFaultController(DurableStorageFaultBarrier.StorageFullNextBatch, cancellation);
        var staging = NewStagingRoot();
        await using var adapter = new DurableAppendFirstStorageAdapterFactory().Create(
            CreateRequest(staging, new DurableCounterPipelineLimits(), faults));

        Func<Task> commit = async () => await adapter.CommitAsync(OneRecordBatch(1), cts.Token);
        (await commit.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(cancellation);
        new FileInfo(Path.Combine(staging, "canonical", "records.bin")).Length.Should().Be(0);
    }

    [Fact]
    public async Task FreshOpenRejectsForeignIdentityAndPackageSchema()
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        await using var adapter = factory.Create(CreateRequest(staging, limits));
        await adapter.CommitAsync(OneRecordBatch(1), CancellationToken.None);
        var quality = CommittedQuality(1);
        var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        var manifest = BuildManifest(staging, preSeal, quality, false);
        DurableStorageAdapterIdentity[] identities =
        [
            factory.Identity with { Id = "A" },
            factory.Identity with { Version = "unsupported" },
            factory.Identity with { ConfigurationSchema = "unsupported" },
        ];
        foreach (var identity in identities)
        {
            Action open = () => factory.OpenReadonly(new DurableStorageOpenRequest(
                staging, manifest with { Adapter = identity }, limits)).DisposeAsync().GetAwaiter().GetResult();
            open.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("AdapterIdentityMismatch");
        }
        foreach (var unsupported in new[]
        {
            manifest with { ContractVersion = "unsupported" },
            manifest with { RecordSchemaVersion = "unsupported" },
        })
        {
            Action open = () => factory.OpenReadonly(new DurableStorageOpenRequest(staging, unsupported, limits))
                .DisposeAsync().GetAwaiter().GetResult();
            open.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("PackageSchemaMismatch");
        }
    }

    [Theory]
    [InlineData("PRAGMA application_id=0;")]
    [InlineData("PRAGMA user_version=999;")]
    [InlineData("PRAGMA journal_mode=WAL;")]
    [InlineData("DROP TABLE records;")]
    [InlineData("ALTER TABLE records RENAME COLUMN name TO badName;")]
    [InlineData("DROP INDEX idx_records_provider_name_sequence;")]
    public async Task FreshOpenRejectsValidSqliteWithWrongIndexSchema(string mutation)
    {
        var limits = new DurableCounterPipelineLimits();
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        await using var adapter = factory.Create(CreateRequest(staging, limits));
        await adapter.CommitAsync(OneRecordBatch(1), CancellationToken.None);
        var quality = CommittedQuality(1);
        var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        await adapter.DisposeAsync();
        var manifest = BuildManifest(staging, preSeal, quality, false);
        var queryPath = Path.Combine(staging, "query", "index.db");
        MutateIndex(queryPath, mutation);
        manifest = manifest with
        {
            Members = manifest.Members.Select(member => member.Role == DurableStorageMemberRole.QueryIndex
                ? member with
                {
                    Length = new FileInfo(queryPath).Length,
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(queryPath))).ToLowerInvariant(),
                }
                : member).ToArray(),
        };

        var filesBefore = Directory.GetFiles(staging, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Action open = () => factory.OpenReadonly(new DurableStorageOpenRequest(staging, manifest, limits))
            .DisposeAsync().GetAwaiter().GetResult();
        open.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("QuerySchemaMismatch");
        Directory.GetFiles(staging, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Should().Equal(filesBefore);
    }

    [Fact]
    public async Task IndexProjectionVerificationDetectsChangedMetadataWithSameCountAndSequence()
    {
        var path = Path.Combine(NewStagingRoot(), "index.db");
        await using var index = DurableAppendFirstQueryIndex.CreateWritable(path, new DurableCounterPipelineLimits());
        var expected = OneRecordBatch(1)[0].Record;
        index.InsertBatch([expected]);
        index.VerifyRecord(expected);
        MutateIndex(path, "UPDATE records SET clockOrigin='substituted';");
        index.CountRows().Should().Be(1);

        Action verify = () => index.VerifyRecord(expected);
        verify.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be("IndexRecordMismatch");
    }

    [Fact]
    public async Task RecoveryRejectsSourceOrOccupiedDestinationWithoutDeletingExistingBytes()
    {
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var source = NewStagingRoot();
        await using (var adapter = factory.Create(CreateRequest(source, new DurableCounterPipelineLimits())))
        {
            await adapter.CommitAsync(OneRecordBatch(1), CancellationToken.None);
        }
        var canonical = Path.Combine(source, "canonical", "records.bin");
        var before = File.ReadAllBytes(canonical);
        var occupied = NewStagingRoot();
        var sentinel = Path.Combine(occupied, "keep.bin");
        File.WriteAllBytes(sentinel, [1, 2, 3]);
        foreach (var destination in new[] { source, Path.Combine(source, "child"), occupied })
        {
            Func<Task> recover = async () => await factory.RecoverAsync(new DurableStorageRecoveryRequest(
                "capture-1", "artifact-1", "new-capture", "new-artifact",
                source, destination, "component-test"), CancellationToken.None);
            (await recover.Should().ThrowAsync<DurableStorageExperimentException>())
                .Which.Code.Should().Be("InvalidRecoveryStaging");
            File.ReadAllBytes(canonical).Should().Equal(before);
            File.ReadAllBytes(sentinel).Should().Equal(new byte[] { 1, 2, 3 });
        }
    }

    [Fact]
    public async Task EveryTypedViewEnforcesTheSuppliedResultByteLimit()
    {
        var factory = new DurableAppendFirstStorageAdapterFactory();
        var staging = NewStagingRoot();
        await using var adapter = factory.Create(CreateRequest(staging, new DurableCounterPipelineLimits()));
        await adapter.CommitAsync(OneRecordBatch(1), CancellationToken.None);
        var quality = CommittedQuality(1);
        var preSeal = await adapter.FinalizePreSealAsync(quality, CancellationToken.None);
        var manifest = BuildManifest(staging, preSeal, quality, false);
        await using var reader = factory.OpenReadonly(new DurableStorageOpenRequest(
            staging, manifest, new DurableCounterPipelineLimits(ResultBytes: 1)));
        Func<Task>[] queries =
        [
            async () => await reader.SummaryAsync(CancellationToken.None),
            async () => await reader.SeriesAsync("Synthetic.Provider", "counter-0", null, 1, CancellationToken.None),
            async () => await reader.QualityAsync(CancellationToken.None),
        ];
        foreach (var query in queries)
        {
            (await query.Should().ThrowAsync<DurableStorageExperimentException>())
                .Which.Code.Should().Be("ResultLimitExceeded");
        }
    }

    [Fact]
    public async Task FlushedFrameWithFailedRollbackRemainsUnknownAndRecoverable()
    {
        var operations = new DurableAppendFirstFileOperations(
            Resize: (_, _) => throw new IOException("Injected rollback failure."),
            Flush: (stream, durable) =>
            {
                stream.Flush(durable);
                throw new IOException("Injected failure after actual flush.");
            });
        var factory = new DurableAppendFirstStorageAdapterFactory(fileOperations: operations);
        var staging = NewStagingRoot();
        var limits = new DurableCounterPipelineLimits();
        await using var adapter = factory.Create(CreateRequest(staging, limits));
        await using var pipeline = new DurableCounterPipeline(
            limits, new DurableCounterGlobalBudget(limits), adapter, ImmediateCounterBatchAgeWaiter.Instance);
        pipeline.TryWrite(DurableCounterFixture.GenerateQ1()[0]).Status.Should().Be(DurableCounterOfferStatus.Accepted);
        (await pipeline.DrainAsync(TimeSpan.FromSeconds(5))).CompletedWithinTimeout.Should().BeTrue();
        var accounting = pipeline.GetAccounting();
        accounting.UnknownCommitOutcome.Should().Be(1);
        accounting.FailedAfterAdmission.Should().Be(0);

        Func<Task> finalize = async () => await adapter.FinalizePreSealAsync(QuietQuality(accounting), CancellationToken.None);
        (await finalize.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("RecoveryRequired");
        Func<Task> append = async () => await adapter.CommitAsync(OneRecordBatch(2), CancellationToken.None);
        await append.Should().ThrowAsync<InvalidOperationException>();
        await adapter.DisposeAsync();

        var recoveredRoot = NewStagingRoot();
        var recovery = await new DurableAppendFirstStorageAdapterFactory().RecoverAsync(
            new DurableStorageRecoveryRequest(
                "capture-1", "artifact-1", "recovered-capture", "recovered-artifact",
                staging, recoveredRoot, "component-flush-rollback-fault"),
            CancellationToken.None);
        var manifest = new DurableStoragePackageManifest(
            DurableStorageExperimentVersions.PackageContract, DurableStorageExperimentVersions.RecordSchema,
            recovery.CaptureId, recovery.ArtifactId, DateTimeOffset.UnixEpoch, factory.Identity,
            new string('0', 64), new string('0', 64), "component-test",
            recovery.RecoveredMembers, recovery.DerivedFromCaptureId, recovery.RecoveryReason,
            recovery.VolatileTailUnknown, FinalPipelineQuality: null);
        await using var reader = factory.OpenReadonly(new DurableStorageOpenRequest(recoveredRoot, manifest, limits));
        (await reader.QualityAsync(CancellationToken.None)).RetainedRecords.Should().Be(1);
        var page = await reader.SeriesAsync("Synthetic.Provider", "counter-0", null, 1, CancellationToken.None);
        page.Rows.Single().Sequence.Should().Be(1);
    }

    private static void MutateIndex(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DurableCounterQualityReport CommittedQuality(long count)
        => QuietQuality(new DurableCounterAccounting(
            count, 0, count, 0, 0, 0, count, 0, 0, 0, 0, 0, 0, 0, false, new Dictionary<string, long>()));

    private static void AssertGeneratedRecord(DurableCounterRecord actual, long sequence)
    {
        var key = (sequence - 1) % 8;
        actual.Should().Be(new DurableCounterRecord(
            sequence, "Synthetic.Provider", $"counter-{key}", $"Synthetic counter {key}", "items",
            100 - sequence % 50, key % 2 == 0 ? CounterKind.Mean : CounterKind.Sum,
            1, CounterMetadataState.Valid, TimeSpan.TicksPerSecond, CounterMetadataState.Valid,
            (sequence - 1) * 10 * TimeSpan.TicksPerMillisecond,
            "fixture-relative-100ns", "dc3-rev3-generated",
            sequence <= 8 ? CoverageGapState.Unknown : CoverageGapState.NoGap, CounterResetState.Unknown, 512));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the test's wait budget.");
            }
            await Task.Delay(5);
        }
    }

    private string NewStagingRoot()
    {
        var path = Path.Combine(_workspace, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static DurableStorageAdapterCreateRequest CreateRequest(
        string staging, DurableCounterPipelineLimits limits, IDurableStorageFaultController? faults = null)
        => new(
            "capture-1",
            "artifact-1",
            staging,
            JsonDocument.Parse("""{"profile":"P1"}""").RootElement,
            limits,
            faults ?? NoDurableStorageFaults.Instance);

    private static DurableCounterSinkRecord[] OneRecordBatch(long sequence)
    {
        var record = new DurableCounterRecord(
            sequence, "Synthetic.Provider", "counter-0", "Synthetic counter 0", "items",
            42.0, CounterKind.Mean, 1.0, CounterMetadataState.Valid, TimeSpan.TicksPerSecond,
            CounterMetadataState.Valid, sequence * 10, "fixture-relative-100ns", "component-test",
            CoverageGapState.Unknown, CounterResetState.Unknown, EncodedBytes: 0);
        var json = JsonSerializer.SerializeToUtf8Bytes(record);
        return [new DurableCounterSinkRecord(record with { EncodedBytes = json.Length }, json)];
    }

    // Produces a genuinely decodable JSON-encoded payload for hand-crafted frame tests
    // that must exercise the frame/recovery scan directly (bypassing the pipeline).
    private static byte[] EncodedRecord(long sequence)
    {
        var record = new DurableCounterRecord(
            sequence, "Synthetic.Provider", "counter-0", "Synthetic counter 0", "items",
            42.0, CounterKind.Mean, 1.0, CounterMetadataState.Valid, TimeSpan.TicksPerSecond,
            CounterMetadataState.Valid, sequence * 10, "fixture-relative-100ns", "component-test",
            CoverageGapState.Unknown, CounterResetState.Unknown, EncodedBytes: 0);
        return JsonSerializer.SerializeToUtf8Bytes(record);
    }

    private static DurableCounterQualityReport QuietQuality(DurableCounterAccounting accounting)
        => new(accounting, accounting.UnknownCommitOutcome > 0, "unknown",
            "Per-interval increments only; a lower value is never reset evidence.");

    private static DurableStoragePackageManifest BuildManifest(
        string staging, DurableStoragePreSealResult preSeal, DurableCounterQualityReport quality, bool volatileTailUnknown)
        => new(
            ContractVersion: DurableStorageExperimentVersions.PackageContract,
            RecordSchemaVersion: DurableStorageExperimentVersions.RecordSchema,
            CaptureId: "capture-1",
            ArtifactId: "artifact-1",
            CreatedAt: DateTimeOffset.UtcNow,
            Adapter: new DurableAppendFirstStorageAdapterFactory().Identity,
            ProtocolJsonSha256: new string('0', 64),
            FixtureSha256: new string('0', 64),
            PipelineCommit: "component-test",
            Members: [.. preSeal.CanonicalMembers, .. preSeal.QueryMembers],
            DerivedFromCaptureId: null,
            RecoveryReason: null,
            VolatileTailUnknown: volatileTailUnknown,
            FinalPipelineQuality: quality);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class ThrowOnceFaultController : IDurableStorageFaultController
    {
        private readonly DurableStorageFaultBarrier _target;
        private readonly Exception _exception;
        private bool _thrown;

        internal ThrowOnceFaultController(DurableStorageFaultBarrier target, Exception? exception = null)
        {
            _target = target;
            _exception = exception ?? new InvalidOperationException("Simulated fault barrier interruption.");
        }

        public ValueTask ReachAsync(DurableStorageFaultContext context, CancellationToken cancellationToken)
        {
            if (!_thrown && context.Barrier == _target)
            {
                _thrown = true;
                throw _exception;
            }
            return ValueTask.CompletedTask;
        }
    }
}
