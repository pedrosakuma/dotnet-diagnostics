using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core.Counters;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike;

public sealed class DurableCounterPipelineSpikeTests
{
    private const string ExpectedQ1InputHash = "e4fefbefddac4193103038771b5f3f2d7c3a1c01e0af9db4853b1b51780c6a16";
    private const string ExpectedQ1OracleHash = "9e46cddd49925945f6450921129c48126038d7a07df0107b1a6259b4a52318e3";
    private const string ExpectedQ2InputHash = "1e757f6f1f3328eddf6dbc848eeef4c72a38291c93806ec9204b6e72955c2154";
    private const string ExpectedQ2OracleHash = "e59999f9ab66b9dbc58404bde6c1e359ad01c13dd0be43b28862b94561eb821b";

    [Fact]
    public async Task Q1_FrozenGeneratorMatchesIndependentOracleAndTypedQueries()
    {
        var limits = ProtocolLimits();
        var sink = new RecordingCounterSink();
        var observations = DurableCounterFixture.GenerateQ1();
        var expected = DurableCounterOracle.Q1();
        await using var pipeline = CreatePipeline(limits, sink);

        for (var offset = 0; offset < observations.Count; offset += 64)
        {
            foreach (var observation in observations.Skip(offset).Take(64))
            {
                pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
            }
            await WaitUntilAsync(() => sink.Records.Count >= Math.Min(offset + 64, observations.Count));
        }

        (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();
        var records = sink.Records;
        records.Should().HaveCount(1_024);
        records.Select(ToExpected).Should().Equal(expected);
        AssertRetainedFields(observations, records);
        records.Should().OnlyContain(record => record.EncodedBytes == 512);
        sink.EncodedLengths.Should().OnlyContain(length => length == 512);
        DurableCounterFixture.CanonicalInputHash(observations).Should().Be(ExpectedQ1InputHash);
        DurableCounterFixture.CanonicalOracleHash(expected).Should().Be(ExpectedQ1OracleHash);

        var query = new DurableCounterQuery(records, limits);
        var summary = query.Summary();
        summary.Should().HaveCount(8);
        summary.Sum(static row => row.RetainedCount).Should().Be(1_024);
        summary.Should().OnlyContain(row => row.UnknownCoverageCount == 1 && row.GapCount == 0);

        var firstPage = query.Series("Synthetic.Provider", "counter-0", afterSequence: null, pageSize: 100);
        firstPage.Rows.Should().HaveCount(100);
        firstPage.NextAfterSequence.Should().Be(firstPage.Rows[^1].Sequence);
        var secondPage = query.Series(
            "Synthetic.Provider",
            "counter-0",
            firstPage.NextAfterSequence,
            pageSize: 100);
        secondPage.Rows.Should().HaveCount(28);
        secondPage.Rows[0].Sequence.Should().BeGreaterThan(firstPage.Rows[^1].Sequence);
        secondPage.NextAfterSequence.Should().BeNull();
    }

    [Fact]
    public async Task Q2_ExplicitRowsOverrideGeneratorAndPreserveUnknownGapAndResetSemantics()
    {
        var repositoryRoot = FindRepositoryRoot();
        var observations = DurableCounterFixture.LoadQ2(repositoryRoot);
        var expected = DurableCounterOracle.Q2();
        var sink = new RecordingCounterSink();
        await using var pipeline = CreatePipeline(ProtocolLimits(), sink);

        var offers = observations.Select(pipeline.TryWrite).ToArray();
        (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();

        offers.Select(static result => new { result.Sequence, result.Status, result.Reason })
            .Should().Equal(expected.Select(static row => new { row.Sequence, row.Status, row.Reason }));
        sink.Records.Select(ToExpected).Should().Equal(expected.Where(static row =>
            row.Status == DurableCounterOfferStatus.Accepted));
        AssertRetainedFields(observations, sink.Records);
        sink.Records.Single(static row => row.Sequence == 9).CoverageGap.Should().Be(CoverageGapState.Gap);
        sink.Records.Single(static row => row.Sequence == 10).CoverageGap.Should().Be(CoverageGapState.Unknown);
        sink.Records.Single(static row => row.Sequence == 11).CoverageGap.Should().Be(CoverageGapState.NoGap);
        sink.Records.Single(static row => row.Sequence == 12).CoverageGap.Should().Be(CoverageGapState.Unknown);
        sink.Records.Single(static row => row.Sequence == 13).EncodedBytes.Should().Be(4_096);
        sink.EncodedLengths[12].Should().Be(4_096);
        sink.Records.Single(static row => row.Sequence == 2).Value.Should().Be(10);
        sink.Records.Single(static row => row.Sequence == 3).Value.Should().Be(5);
        sink.Records.Where(static row => row.Kind == CounterKind.Sum)
            .Should().OnlyContain(row => row.ResetState == CounterResetState.Unknown);
        DurableCounterFixture.CanonicalInputHash(observations).Should().Be(ExpectedQ2InputHash);
        DurableCounterFixture.CanonicalOracleHash(expected).Should().Be(ExpectedQ2OracleHash);

        var accounting = pipeline.GetAccounting();
        accounting.Offered.Should().Be(16);
        accounting.Admitted.Should().Be(13);
        accounting.Rejected.Should().Be(3);
        accounting.Committed.Should().Be(13);
        accounting.IsCleanQuiescent.Should().BeTrue();
        accounting.HasKnownTerminalConservation.Should().BeTrue();
        new DurableCounterQuery(sink.Records, ProtocolLimits()).Quality(accounting).Should()
            .Match<DurableCounterQualityReport>(report =>
                !report.UnknownCommitOutcome
                && report.ResetState == "unknown"
                && report.SourceCoverageMeaning.Contains("observed source timing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidationRejectsUtf8OversizeUnknownKindNonfiniteValueAndInvalidClockBeforeOwnership()
    {
        var limits = ProtocolLimits();
        var sink = new RecordingCounterSink();
        await using var pipeline = CreatePipeline(limits, sink);

        var unicodeName = string.Concat(Enumerable.Repeat("é", 129));
        pipeline.TryWrite(Observation(name: unicodeName)).Should().Match<DurableCounterOfferResult>(
            result => result.Status == DurableCounterOfferStatus.Invalid && result.Reason == "StringUtf8Limit");
        pipeline.TryWrite(Observation(kind: (CounterKind)99)).Should().Match<DurableCounterOfferResult>(
            result => result.Status == DurableCounterOfferStatus.Invalid && result.Reason == "UnknownCounterKind");
        pipeline.TryWrite(Observation(value: double.PositiveInfinity)).Should().Match<DurableCounterOfferResult>(
            result => result.Status == DurableCounterOfferStatus.Invalid && result.Reason == "NonfiniteValue");
        pipeline.TryWrite(Observation(clock: new DurableCounterSourceClock("", "fixture"))).Should()
            .Match<DurableCounterOfferResult>(
                result => result.Status == DurableCounterOfferStatus.Invalid
                    && result.Reason == "InvalidClockMetadata");
        pipeline.TryWrite(Observation(requestedEncodedBytes: 4_097)).Should().Match<DurableCounterOfferResult>(
            result => result.Status == DurableCounterOfferStatus.RecordTooLarge
                && result.Reason == "RecordEncodedBytes");
        pipeline.TryWrite(Observation(
            clock: new DurableCounterSourceClock(new string('d', 3_000), new string('o', 3_000)),
            requestedEncodedBytes: null)).Should().Match<DurableCounterOfferResult>(
                result => result.Status == DurableCounterOfferStatus.RecordTooLarge
                    && result.Reason == "RecordEncodedBytes");

        var accounting = pipeline.GetAccounting();
        accounting.OwnedRecords.Should().Be(0);
        accounting.OwnedBytes.Should().Be(0);
        accounting.InCopy.Should().Be(0);
        accounting.Admitted.Should().Be(0);
        accounting.Rejected.Should().Be(6);
    }

    [Fact]
    public async Task DistinctKeyCapRejectsThe129thKeyWithoutOwningIt()
    {
        var limits = ProtocolLimits();
        var sink = new RecordingCounterSink();
        await using var pipeline = CreatePipeline(limits, sink);

        for (var key = 0; key < 128; key++)
        {
            pipeline.TryWrite(Observation(name: $"counter-{key}", sourceTimeTicks: key))
                .Status.Should().Be(DurableCounterOfferStatus.Accepted);
        }
        var rejected = pipeline.TryWrite(Observation(name: "counter-128", sourceTimeTicks: 129));
        rejected.Status.Should().Be(DurableCounterOfferStatus.TooManyKeys);
        rejected.Reason.Should().Be("DistinctKeyLimit");

        (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();
        sink.Records.Should().HaveCount(128);
        new DurableCounterQuery(sink.Records, limits).Summary().Should().HaveCount(128);
    }

    [Fact]
    public async Task NonfiniteIntervalIsPreservedAsExplicitMetadataStateNotAValidNumber()
    {
        var sink = new RecordingCounterSink();
        await using var pipeline = CreatePipeline(ProtocolLimits(), sink);
        var observation = Observation() with
        {
            Counter = Observation().Counter with { IntervalSec = double.NaN },
        };

        pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
        (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();

        var record = sink.Records.Should().ContainSingle().Which;
        record.IntervalSec.Should().BeNull();
        record.IntervalState.Should().Be(CounterMetadataState.Nonfinite);
        record.CoverageGap.Should().Be(CoverageGapState.Unknown);
    }

    [Fact]
    public async Task HeldWriterMakesQueueAndOwnedBudgetPressureExplicitWithoutWaiting()
    {
        var limits = ProtocolLimits();
        var writerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingCounterSink();
        await using var pipeline = CreatePipeline(limits, sink, writerStartGate: writerGate.Task);

        var offers = Enumerable.Range(1, 300)
            .Select(sequence => pipeline.TryWrite(DurableCounterFixture.Generate(sequence)))
            .ToArray();

        offers.Count(static result => result.Status == DurableCounterOfferStatus.Accepted).Should().Be(256);
        offers.Count(static result => result.Status == DurableCounterOfferStatus.QueueFull).Should().Be(44);
        pipeline.GetAccounting().Should().Match<DurableCounterAccounting>(
            accounting => accounting.Queued == 256
                && accounting.ActiveBatch == 0
                && accounting.OwnedRecords == 256
                && accounting.OwnedBytes == 256 * 4_096L);
        writerGate.SetResult();
        (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();
        var finalAccounting = pipeline.GetAccounting();
        finalAccounting.IsCleanQuiescent.Should().BeTrue();
        finalAccounting.HasKnownTerminalConservation.Should().BeTrue();
        finalAccounting.Should().Match<DurableCounterAccounting>(
            accounting => accounting.Offered == 300
                && accounting.Admitted == 256
                && accounting.Rejected == 44
                && accounting.Committed == 256);

        var memoryLimits = limits with { OwnedBufferBytes = 262_144 };
        var memoryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var memoryPipeline = CreatePipeline(
            memoryLimits,
            new RecordingCounterSink(),
            writerStartGate: memoryGate.Task);
        var memoryOffers = Enumerable.Range(1, 65)
            .Select(sequence => memoryPipeline.TryWrite(DurableCounterFixture.Generate(sequence)))
            .ToArray();
        memoryOffers.Take(64).Should().OnlyContain(result => result.Status == DurableCounterOfferStatus.Accepted);
        memoryOffers[^1].Status.Should().Be(DurableCounterOfferStatus.OwnedBudgetFull);
        memoryPipeline.GetAccounting().OwnedBytes.Should().Be(262_144);
        memoryGate.SetResult();
        (await memoryPipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();
    }

    [Fact]
    public async Task CoverageGapUsesPreAdmissionSourceHistoryRatherThanStoredSeries()
    {
        var limits = ProtocolLimits() with { QueueRecords = 1 };
        var writerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingCounterSink();
        await using var pipeline = CreatePipeline(limits, sink, writerStartGate: writerGate.Task);

        pipeline.TryWrite(Observation(sourceTimeTicks: 0)).Status.Should().Be(DurableCounterOfferStatus.Accepted);
        pipeline.TryWrite(Observation(sourceTimeTicks: 4 * TimeSpan.TicksPerSecond))
            .Status.Should().Be(DurableCounterOfferStatus.QueueFull);

        writerGate.SetResult();
        await WaitUntilAsync(() => sink.Records.Count == 1);
        pipeline.TryWrite(Observation(sourceTimeTicks: 5 * TimeSpan.TicksPerSecond))
            .Status.Should().Be(DurableCounterOfferStatus.Accepted);
        (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();

        sink.Records.Select(static record => record.Sequence).Should().Equal(1, 3);
        sink.Records.Single(static record => record.Sequence == 3).CoverageGap
            .Should().Be(CoverageGapState.NoGap,
                "the rejected sequence 2 source observation remains part of pre-admission gap history");
    }

    [Fact]
    public async Task ActiveBatchRemainsChargedWhenNoncooperativeCommitOutlivesDrainDeadline()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DurableCounterCommitResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingCounterSink(_ =>
        {
            entered.TrySetResult();
            return new ValueTask<DurableCounterCommitResult>(release.Task);
        });
        await using var pipeline = CreatePipeline(ProtocolLimits(), sink, writerStartGate: writerGate.Task);
        foreach (var observation in Enumerable.Range(1, 64).Select(DurableCounterFixture.Generate))
        {
            pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
        }
        writerGate.SetResult();
        await entered.Task;

        var drained = await pipeline.DrainAsync(TimeSpan.FromMilliseconds(20));
        var held = pipeline.GetAccounting();
        var completionWhileHeld = pipeline.Completion.IsCompleted;
        release.SetResult(new DurableCounterCommitResult(DurableCounterCommitOutcome.Committed));
        await pipeline.Completion;

        drained.CompletedWithinTimeout.Should().BeFalse();
        drained.State.Should().Be(DurableCounterPipelineState.Draining);
        held.ActiveBatch.Should().Be(64);
        held.OwnedRecords.Should().Be(64);
        held.OwnedBytes.Should().Be(262_144);
        completionWhileHeld.Should().BeFalse();
        pipeline.GetAccounting().Should().Match<DurableCounterAccounting>(
            accounting => accounting.Committed == 64 && accounting.IsCleanQuiescent);
    }

    [Fact]
    public async Task ClosedQueueKnownFailureAndUnknownCommitAreDistinctTerminalOutcomes()
    {
        var limits = ProtocolLimits();
        var closedSink = new RecordingCounterSink();
        await using (var closed = CreatePipeline(limits, closedSink))
        {
            closed.StopAdmission();
            closed.TryWrite(Observation()).Status.Should().Be(DurableCounterOfferStatus.Closed);
            (await closed.DrainAsync(TimeSpan.FromSeconds(2))).Should().Be(
                new DurableCounterDrainResult(true, DurableCounterPipelineState.Completed, null));
        }

        var failedSink = new RecordingCounterSink(_ =>
            ValueTask.FromResult(new DurableCounterCommitResult(
                DurableCounterCommitOutcome.Failed,
                "injected-storage-full")));
        await using (var failed = CreatePipeline(limits, failedSink))
        {
            failed.TryWrite(Observation()).Status.Should().Be(DurableCounterOfferStatus.Accepted);
            await WaitUntilAsync(() => failed.State == DurableCounterPipelineState.Failed);
            failed.TryWrite(Observation()).Status.Should().Be(DurableCounterOfferStatus.WriterFailed);
            var failedDrain = await failed.DrainAsync(TimeSpan.FromSeconds(2));
            failedDrain.Should().Be(new DurableCounterDrainResult(
                true,
                DurableCounterPipelineState.Failed,
                "injected-storage-full"));
            var accounting = failed.GetAccounting();
            accounting.FailedAfterAdmission.Should().Be(1);
            accounting.UnknownCommitOutcome.Should().Be(0);
            accounting.IsCleanQuiescent.Should().BeTrue();
            accounting.HasKnownTerminalConservation.Should().BeTrue();
        }

        var unknownSink = new RecordingCounterSink(_ =>
            ValueTask.FromResult(new DurableCounterCommitResult(
                DurableCounterCommitOutcome.Unknown,
                "injected-ack-gap")));
        await using var unknown = CreatePipeline(limits, unknownSink);
        unknown.TryWrite(Observation()).Status.Should().Be(DurableCounterOfferStatus.Accepted);
        await WaitUntilAsync(() => unknown.State == DurableCounterPipelineState.UnknownCommitOutcome);
        var unknownDrain = await unknown.DrainAsync(TimeSpan.FromSeconds(2));
        unknownDrain.Should().Be(new DurableCounterDrainResult(
            true,
            DurableCounterPipelineState.UnknownCommitOutcome,
            "injected-ack-gap"));
        var unknownAccounting = unknown.GetAccounting();
        unknownAccounting.UnknownCommitOutcome.Should().Be(1);
        unknownAccounting.Committed.Should().Be(0);
        unknownAccounting.FailedAfterAdmission.Should().Be(0);
        unknownAccounting.HasKnownTerminalConservation.Should().BeFalse();
        unknownAccounting.IsCleanQuiescent.Should().BeTrue();
    }

    [Fact]
    public async Task BatchAgeAndFinalizeHaveIndependentControlledDeadlines()
    {
        var ageWaiter = new ControlledBatchAgeWaiter();
        var finalizeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingCounterSink(
            finalizeBehavior: () => new ValueTask(finalizeGate.Task));
        await using var pipeline = CreatePipeline(ProtocolLimits(), sink, ageWaiter);
        pipeline.TryWrite(Observation()).Status.Should().Be(DurableCounterOfferStatus.Accepted);
        await ageWaiter.Entered;
        sink.Records.Should().BeEmpty();

        ageWaiter.Release();
        await WaitUntilAsync(() => sink.Records.Count == 1);
        (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();
        (await pipeline.FinalizeAsync(TimeSpan.FromMilliseconds(20))).Should().BeFalse();
        finalizeGate.SetResult();
        await WaitUntilAsync(() => finalizeGate.Task.IsCompleted);
    }

    [Fact]
    public async Task AgeWinnerDoesNotLoseAlreadyConsumedAvailabilityPermit()
    {
        var ageWaiter = new ControlledBatchAgeWaiter();
        var availabilityRegistered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ageWon = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingCounterSink();
        await using var pipeline = CreatePipeline(
            ProtocolLimits(),
            sink,
            ageWaiter,
            batchAvailabilityWaitRegistered: () => availabilityRegistered.TrySetResult(),
            batchAgeWaitWon: current =>
            {
                current.TryWrite(Observation(sourceTimeTicks: TimeSpan.TicksPerSecond)).Status.Should()
                    .Be(DurableCounterOfferStatus.Accepted);
                ageWon.TrySetResult();
            });

        pipeline.TryWrite(Observation(sourceTimeTicks: 0)).Status.Should()
            .Be(DurableCounterOfferStatus.Accepted);
        await availabilityRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ageWaiter.Release();
        await ageWon.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => sink.Records.Count == 2);

        sink.Records.Select(static record => record.Sequence).Should().Equal(1, 2);
        pipeline.GetAccounting().Should().Match<DurableCounterAccounting>(
            accounting => accounting.Queued == 0
                && accounting.ActiveBatch == 0
                && accounting.Committed == 2);
    }

    [Fact]
    public async Task CaptureCancellationClosesAdmissionThenDrainsToAnExplicitTerminalState()
    {
        var sink = new RecordingCounterSink();
        await using var pipeline = CreatePipeline(ProtocolLimits(), sink);
        foreach (var observation in Enumerable.Range(1, 10).Select(DurableCounterFixture.Generate))
        {
            pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
        }

        var drain = await pipeline.CancelAndDrainAsync(TimeSpan.FromSeconds(2));

        drain.Should().Be(new DurableCounterDrainResult(true, DurableCounterPipelineState.Cancelled, null));
        pipeline.State.Should().Be(DurableCounterPipelineState.Cancelled);
        pipeline.TryWrite(Observation()).Status.Should().Be(DurableCounterOfferStatus.Closed);
        var accounting = pipeline.GetAccounting();
        accounting.AdmissionCancelled.Should().BeTrue();
        accounting.Committed.Should().Be(10);
        accounting.IsCleanQuiescent.Should().BeTrue();
        accounting.HasKnownTerminalConservation.Should().BeTrue();
    }

    [Fact]
    public async Task TypedQueryEnforcesExclusiveCursorPageAndResultBoundsWithoutMutatingSource()
    {
        var source = DurableCounterFixture.GenerateQ1();
        var before = DurableCounterFixture.CanonicalInputHash(source);
        var sink = new RecordingCounterSink();
        var limits = ProtocolLimits();
        await using var pipeline = CreatePipeline(limits, sink);
        foreach (var observation in source.Take(128))
        {
            pipeline.TryWrite(observation).Status.Should().Be(DurableCounterOfferStatus.Accepted);
        }
        (await pipeline.DrainAsync(TimeSpan.FromSeconds(2))).CompletedWithinTimeout.Should().BeTrue();

        var query = new DurableCounterQuery(sink.Records, limits);
        var first = query.Series("Synthetic.Provider", "counter-0", null, 5);
        var second = query.Series("Synthetic.Provider", "counter-0", first.Rows[^1].Sequence, 5);
        second.Rows[0].Sequence.Should().BeGreaterThan(first.Rows[^1].Sequence);
        Action negativeCursor = () => query.Series("Synthetic.Provider", "counter-0", -1, 5);
        negativeCursor.Should().Throw<DurableCounterPipelineException>()
            .Which.Code.Should().Be("InvalidCursor");
        Action oversizedPage = () => query.Series("Synthetic.Provider", "counter-0", null, 101);
        oversizedPage.Should().Throw<DurableCounterPipelineException>()
            .Which.Code.Should().Be("InvalidPageSize");
        Action oversizedResult = () => new DurableCounterQuery(
            sink.Records,
            limits with { ResultBytes = 16 }).Summary();
        oversizedResult.Should().Throw<DurableCounterPipelineException>()
            .Which.Code.Should().Be("ResultLimitExceeded");
        DurableCounterFixture.CanonicalInputHash(source).Should().Be(before);
        ReferenceEquals(source[0].Counter.Provider, sink.Records[0].Provider).Should().BeFalse();
    }

    [Fact]
    public async Task GlobalActiveCaptureCapIsAnInputSeam()
    {
        var limits = ProtocolLimits();
        var budget = new DurableCounterGlobalBudget(limits);
        await using var first = new DurableCounterPipeline(limits, budget, new RecordingCounterSink());

        Action second = () => new DurableCounterPipeline(limits, budget, new RecordingCounterSink());
        second.Should().Throw<DurableCounterPipelineException>()
            .Which.Code.Should().Be("ActiveCaptureLimit");
    }

    [Fact]
    public void FixtureManifestPinsProtocolRevisionEncodingAndHashes()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            repositoryRoot,
            "tests/DotnetDiagnostics.Core.Tests/DurableCounterSpike/durable-counter-fixture-manifest.json")));
        var root = document.RootElement;
        var protocolPath = Path.Combine(
            repositoryRoot,
            root.GetProperty("protocolJson").GetString()
                ?? throw new InvalidOperationException("Fixture manifest protocol path was empty."));
        var protocolBytes = File.ReadAllBytes(protocolPath);
        var actualProtocolHash = Convert.ToHexString(SHA256.HashData(protocolBytes)).ToLowerInvariant();
        using var protocolDocument = JsonDocument.Parse(protocolBytes);
        var protocol = protocolDocument.RootElement;
        var protocolLimits = protocol.GetProperty("limits");

        root.GetProperty("protocolRevision").GetInt32().Should().Be(3);
        protocol.GetProperty("revision").GetInt32().Should().Be(root.GetProperty("protocolRevision").GetInt32());
        actualProtocolHash.Should().Be(root.GetProperty("protocolJsonSha256").GetString());
        root.GetProperty("generatorRevision").GetString().Should().Be(DurableCounterFixture.Revision);
        root.GetProperty("canonicalEncoding").GetString().Should()
            .Be("System.Text.Json web defaults; one UTF-8 JSON value plus LF per logical row; finite doubles use invariant round-trip strings; named nonfinite source values use protocol tokens");
        root.GetProperty("q1InputSha256").GetString().Should().Be(ExpectedQ1InputHash);
        root.GetProperty("q1OracleSha256").GetString().Should().Be(ExpectedQ1OracleHash);
        root.GetProperty("q2InputSha256").GetString().Should().Be(ExpectedQ2InputHash);
        root.GetProperty("q2OracleSha256").GetString().Should().Be(ExpectedQ2OracleHash);

        var defaults = ProtocolLimits();
        defaults.RecordEncodedBytes.Should().Be(protocolLimits.GetProperty("recordEncodedBytes").GetInt32());
        defaults.ProviderUtf8Bytes.Should().Be(protocolLimits.GetProperty("providerUtf8Bytes").GetInt32());
        defaults.NameUtf8Bytes.Should().Be(protocolLimits.GetProperty("nameUtf8Bytes").GetInt32());
        defaults.DisplayNameUtf8Bytes.Should().Be(protocolLimits.GetProperty("displayNameUtf8Bytes").GetInt32());
        defaults.UnitUtf8Bytes.Should().Be(protocolLimits.GetProperty("unitUtf8Bytes").GetInt32());
        defaults.DistinctKeys.Should().Be(protocolLimits.GetProperty("distinctKeys").GetInt32());
        defaults.OwnedBufferBytes.Should().Be(protocolLimits.GetProperty("ownedBufferBytes").GetInt64());
        defaults.OwnedRecords.Should().Be(protocolLimits.GetProperty("ownedRecords").GetInt32());
        defaults.QueueRecords.Should().Be(protocolLimits.GetProperty("queueRecords").GetInt32());
        defaults.InCopyRecords.Should().Be(protocolLimits.GetProperty("inCopyRecords").GetInt32());
        defaults.BatchRecords.Should().Be(protocolLimits.GetProperty("batchRecords").GetInt32());
        defaults.BatchOwnedBytes.Should().Be(protocolLimits.GetProperty("batchOwnedBytes").GetInt64());
        defaults.BatchMaxAge.Should().Be(
            TimeSpan.FromMilliseconds(protocolLimits.GetProperty("batchMaxAgeMs").GetInt32()));
        defaults.PageRows.Should().Be(protocolLimits.GetProperty("pageRows").GetInt32());
        defaults.ResultBytes.Should().Be(protocolLimits.GetProperty("resultBytes").GetInt32());
        defaults.ActiveCaptures.Should().Be(protocolLimits.GetProperty("activeCaptures").GetInt32());
    }

    private static DurableCounterPipeline CreatePipeline(
        DurableCounterPipelineLimits limits,
        IDurableCounterSink sink,
        IDurableCounterBatchAgeWaiter? ageWaiter = null,
        Task? writerStartGate = null,
        Action? batchAvailabilityWaitRegistered = null,
        Action<DurableCounterPipeline>? batchAgeWaitWon = null)
        => new(
            limits,
            new DurableCounterGlobalBudget(limits),
            sink,
            ageWaiter ?? ImmediateCounterBatchAgeWaiter.Instance,
            writerStartGate,
            batchAvailabilityWaitRegistered,
            batchAgeWaitWon);

    private static DurableCounterPipelineLimits ProtocolLimits() => new(
        BatchMaxAge: TimeSpan.FromMilliseconds(100));

    private static DurableCounterObservation Observation(
        string name = "counter",
        CounterKind kind = CounterKind.Mean,
        double value = 1,
        long? sourceTimeTicks = 0,
        DurableCounterSourceClock? clock = null,
        int? requestedEncodedBytes = 512)
        => new(
            new CounterValue("Synthetic.Provider", name, "Synthetic counter", value, kind, "items")
            {
                IntervalSec = 1,
                DisplayRateTimeScale = TimeSpan.FromSeconds(1),
            },
            sourceTimeTicks,
            clock ?? new DurableCounterSourceClock("fixture-relative-100ns", "dc3-rev3-test"),
            requestedEncodedBytes);

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

    private static void AssertRetainedFields(
        IReadOnlyList<DurableCounterObservation> observations,
        IReadOnlyList<DurableCounterRecord> records)
    {
        foreach (var record in records)
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
    }

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class ControlledBatchAgeWaiter : IDurableCounterBatchAgeWaiter
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        public ValueTask WaitAsync(TimeSpan age, CancellationToken cancellationToken)
        {
            age.Should().Be(TimeSpan.FromMilliseconds(100));
            _entered.TrySetResult();
            return new ValueTask(_release.Task);
        }
    }
}
