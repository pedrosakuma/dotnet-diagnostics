using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.TestSupport.OperatingEnvelope;
using Xunit;

namespace DotnetDiagnostics.Core.Tests.OperatingEnvelope;

public sealed class OperatingEnvelopeProtocolTests
{
    [Fact]
    public void DefaultScheduleHasThreeAlternatingFreshPairsForEveryPopulation()
    {
        var schedule = OperatingEnvelopeProtocol.CreateSchedule();

        Assert.Equal("real-operating-envelope/1", schedule.ProtocolVersion);
        Assert.Equal(OperatingEnvelopeProtocol.Populations.Count * 6, schedule.Trials.Count);
        Assert.Equal(schedule.Trials.Count, schedule.Trials.Select(trial => trial.TrialId).Distinct().Count());
        Assert.Equal(schedule.Trials.Count, schedule.Trials.Select(trial => trial.StoreDirectoryName).Distinct().Count());

        foreach (var definition in OperatingEnvelopeProtocol.Populations)
        {
            var populationTrials = schedule.Trials
                .Where(trial => trial.Population == definition.Population)
                .GroupBy(trial => trial.PairNumber)
                .OrderBy(pair => pair.Key)
                .ToArray();

            Assert.Equal(OperatingEnvelopeProtocol.PairsPerPopulation, populationTrials.Length);
            Assert.All(populationTrials, pair =>
            {
                var trials = pair.OrderBy(trial => trial.OrderInPair).ToArray();
                Assert.Equal(2, trials.Length);
                Assert.Equal(new[] { 1, 2 }, trials.Select(trial => trial.OrderInPair));
                var expectedModes = pair.Key % 2 == 0
                    ? new[] { OperatingEnvelopeStorageMode.Durable, OperatingEnvelopeStorageMode.Ephemeral }
                    : new[] { OperatingEnvelopeStorageMode.Ephemeral, OperatingEnvelopeStorageMode.Durable };
                Assert.Equal(expectedModes, trials.Select(trial => trial.StorageMode));
                Assert.Equal(definition.TargetCount, trials[0].TargetCount);
                Assert.Equal(definition.TargetCount, trials[1].TargetCount);
                Assert.NotEqual(trials[0].StoreDirectoryName, trials[1].StoreDirectoryName);
            });
        }

        var firstPopulation = schedule.Trials
            .Where(trial => trial.Population == OperatingEnvelopePopulation.IdleCounters)
            .GroupBy(trial => trial.PairNumber)
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.OrderBy(trial => trial.OrderInPair).Select(trial => trial.StorageMode).ToArray())
            .ToArray();
        Assert.Equal(OperatingEnvelopeStorageMode.Ephemeral, firstPopulation[0][0]);
        Assert.Equal(OperatingEnvelopeStorageMode.Durable, firstPopulation[1][0]);
        Assert.Equal(OperatingEnvelopeStorageMode.Ephemeral, firstPopulation[2][0]);

        var sharedDisk = schedule.Trials.Where(trial =>
            trial.Population == OperatingEnvelopePopulation.TwoTargetsSharedDisk);
        Assert.All(sharedDisk, trial => Assert.Equal(2, trial.TargetCount));
    }

    [Fact]
    public void ConfigurationAndRequestAccountingRejectUnboundedOrInconsistentInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (OperatingEnvelopeConfiguration.Default with { WindowDuration = TimeSpan.FromSeconds(31) }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (OperatingEnvelopeConfiguration.Default with { MaximumRequestsPerCell = 20 }).Validate());
        Assert.Throws<ArgumentException>(() =>
            new OperatingEnvelopeRequestAccounting(10, 7, 5, 1, 0, 3, 1_000, [1, 2, 3, 4, 5, 6, 7]).Validate());

        var valid = new OperatingEnvelopeRequestAccounting(
            Planned: 10,
            Offered: 7,
            Admitted: 5,
            Rejected: 1,
            Unknown: 1,
            NotOffered: 3,
            MeasurementWindowMilliseconds: 1_000,
            LatencyMilliseconds: [3, 1, 8, 2, 4]);
        valid.Validate();
        Assert.Equal(5, valid.ThroughputPerSecond);
        Assert.Equal(3, valid.LatencyMedianMilliseconds);
        Assert.Equal(8, valid.LatencyP95Milliseconds);
    }

    [Fact]
    public void RequestProgressDistinguishesInFlightUnknownFromNotOfferedSlots()
    {
        var progress = new OperatingEnvelopeRequestProgress(planned: 5);
        progress.MarkOffered(0);
        progress.MarkOffered(2);
        progress.Complete(2, OperatingEnvelopeRequestOutcome.Admitted, 4);
        progress.MarkOffered(3);
        progress.Complete(3, OperatingEnvelopeRequestOutcome.Rejected, 6);

        var snapshot = progress.Snapshot(1_000);

        Assert.Equal(3, snapshot.Accounting.Offered);
        Assert.Equal(1, snapshot.Accounting.Admitted);
        Assert.Equal(1, snapshot.Accounting.Rejected);
        Assert.Equal(1, snapshot.Accounting.Unknown);
        Assert.Equal(2, snapshot.Accounting.NotOffered);
        Assert.Equal(new[] { 4d, 6d }, snapshot.Accounting.LatencyMilliseconds);
        snapshot.Accounting.Validate();
    }

    [Fact]
    public async Task RequestProgressCanBeSnapshottedWhileWorkerUpdatesSlots()
    {
        const int planned = 16;
        var progress = new OperatingEnvelopeRequestProgress(planned);
        var firstOffer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Run(() =>
        {
            for (var slot = 0; slot < planned; slot++)
            {
                progress.MarkOffered(slot);
                if (slot == 0)
                {
                    firstOffer.SetResult();
                    continueWorker.Task.GetAwaiter().GetResult();
                }

                progress.Complete(slot, OperatingEnvelopeRequestOutcome.Admitted, slot);
            }
        });

        await firstOffer.Task;
        var inFlight = progress.Snapshot(1_000).Accounting;
        Assert.Equal(1, inFlight.Offered);
        Assert.Equal(1, inFlight.Unknown);
        Assert.Equal(planned - 1, inFlight.NotOffered);
        continueWorker.SetResult();
        await worker;

        var completed = progress.Snapshot(1_000).Accounting;
        Assert.Equal(planned, completed.Offered);
        Assert.Equal(planned, completed.Admitted);
        Assert.Equal(0, completed.Unknown);
        Assert.Equal(0, completed.NotOffered);
    }

    [Fact]
    public void DeadlineGuardEnforcesAbsoluteRunAndCellDeadlines()
    {
        using var runDeadline = new CancellationTokenSource();
        using var cellDeadline = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() =>
            OperatingEnvelopeDeadlineGuard.CheckTrial(
                runDeadlineTimestamp: 100,
                cellDeadlineTimestamp: 200,
                nowTimestamp: 100,
                runDeadline,
                cellDeadline));
        Assert.True(runDeadline.IsCancellationRequested);
        Assert.False(cellDeadline.IsCancellationRequested);

        using var secondRunDeadline = new CancellationTokenSource();
        using var secondCellDeadline = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() =>
            OperatingEnvelopeDeadlineGuard.CheckTrial(
                runDeadlineTimestamp: 300,
                cellDeadlineTimestamp: 200,
                nowTimestamp: 200,
                secondRunDeadline,
                secondCellDeadline));
        Assert.False(secondRunDeadline.IsCancellationRequested);
        Assert.True(secondCellDeadline.IsCancellationRequested);
    }

    [Fact]
    public async Task EvidenceHashingChecksCancellationBetweenChunks()
    {
        var contents = new byte[] { 1, 2, 3, 4, 5, 6 };
        await using var completeStream = new MemoryStream(contents);
        var actualHash = await OperatingEnvelopeEvidenceHasher.HashAsync(
            completeStream, static () => { }, CancellationToken.None, chunkSize: 2);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant(),
            actualHash);

        using var cancellation = new CancellationTokenSource();
        await using var stream = new CancellingReadStream([1, 2, 3, 4, 5, 6], cancellation);
        var checks = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OperatingEnvelopeEvidenceHasher.HashAsync(
                stream,
                () =>
                {
                    checks++;
                    cancellation.Token.ThrowIfCancellationRequested();
                },
                cancellation.Token,
                chunkSize: 2));

        Assert.True(checks >= 2);
    }

    [Fact]
    public void QueryPageRunnerChecksDeadlineBeforeAndAfterEverySynchronousPage()
    {
        var checks = 0;
        var reads = 0;
        var visited = new List<int>();

        Assert.Throws<OperationCanceledException>(() =>
            OperatingEnvelopeQueryPageRunner.ReadAll(
                () => ++reads,
                page => page < 3,
                visited.Add,
                () =>
                {
                    checks++;
                    if (checks == 3)
                    {
                        throw new OperationCanceledException("Absolute deadline reached.");
                    }
                },
                CancellationToken.None));

        Assert.Equal(1, reads);
        Assert.Equal(new[] { 1 }, visited);
        Assert.Equal(3, checks);
    }

    [Fact]
    public async Task TaskSettlementUsesAbsoluteBoundAndReportsWhenWorkRemainsUnsettled()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = await OperatingEnvelopeTaskSettlement.SettleAsync(
            [pending.Task], Stopwatch.GetTimestamp() - 1);
        Assert.False(timedOut.Settled);

        pending.SetResult();
        var settled = await OperatingEnvelopeTaskSettlement.SettleAsync(
            [pending.Task], Stopwatch.GetTimestamp() + Stopwatch.Frequency);
        Assert.True(settled.Settled);
        Assert.Empty(settled.Faults);
    }

    [Fact]
    public void PairValidationKeepsMeasuredLossSeparateFromInvalidExecution()
    {
        var schedule = OperatingEnvelopeProtocol.CreateSchedule();
        var planned = OperatingEnvelopeProtocol.GetPair(schedule, "idle-counters-pair-01");
        var trials = planned.Select(plan => Trial(plan, "same-config")).ToArray();

        var valid = OperatingEnvelopePairValidation.Validate(
            planned[0].PairId, planned[0].Population, trials);
        Assert.True(valid.IsValid);

        var missing = OperatingEnvelopePairValidation.Validate(
            planned[0].PairId, planned[0].Population, trials[..1]);
        Assert.False(missing.IsValid);
        Assert.Contains(missing.InvalidReasons, reason => reason.Contains("two outcomes", StringComparison.Ordinal));

        var failed = trials[1] with { Outcome = OperatingEnvelopeTrialOutcome.Incomplete };
        var invalid = OperatingEnvelopePairValidation.Validate(
            planned[0].PairId, planned[0].Population, [trials[0], failed]);
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.InvalidReasons, reason => reason.Contains("did not produce", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArtifactWriterUsesCreateOnlyFilesAndBindsReportsBySha256()
    {
        var root = Path.Combine(Path.GetTempPath(), "operating-envelope-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var schedule = OperatingEnvelopeProtocol.CreateSchedule();
            var writer = await OperatingEnvelopeArtifactWriter.CreateAsync(
                root, schedule, new Dictionary<string, string> { ["fixture.bin"] = new('a', 64) });
            var pairPlans = OperatingEnvelopeProtocol.GetPair(schedule, "idle-counters-pair-01");
            var outcomes = pairPlans.Select(plan => Trial(plan, writer.ConfigurationHash)).ToArray();
            var pair = OperatingEnvelopePairValidation.Validate(
                pairPlans[0].PairId, pairPlans[0].Population, outcomes);

            await writer.WritePairAsync(pair, outcomes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WritePairAsync(pair, outcomes));
            await writer.WriteFinalManifestAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteFinalManifestAsync());

            var planPath = Path.Combine(writer.RunDirectory, "plan.json");
            var runManifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(writer.RunDirectory, "run-manifest.json")));
            var planHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(planPath)))
                .ToLowerInvariant();
            Assert.Equal(planHash, runManifest.RootElement.GetProperty("plan").GetProperty("sha256").GetString());

            var manifestBytes = await File.ReadAllBytesAsync(
                Path.Combine(writer.RunDirectory, "results-manifest.json"));
            var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
            Assert.Equal(manifestHash + "\n",
                await File.ReadAllTextAsync(Path.Combine(writer.RunDirectory, "results-manifest.sha256")));
            var results = JsonDocument.Parse(manifestBytes);
            var artifactEntries = results.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
            Assert.Contains(artifactEntries, entry =>
                entry.GetProperty("path").GetString() == "results.csv");
            foreach (var entry in artifactEntries)
            {
                var artifactPath = Path.Combine(writer.RunDirectory,
                    entry.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
                var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(artifactPath)))
                    .ToLowerInvariant();
                Assert.Equal(entry.GetProperty("sha256").GetString(), actualHash);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task QuarantinedRunCannotPublishPairOrFinalEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "operating-envelope-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var schedule = OperatingEnvelopeProtocol.CreateSchedule();
            var writer = await OperatingEnvelopeArtifactWriter.CreateAsync(root, schedule);
            const string quarantineReason = "An owned task did not settle.";
            var trial = Trial(schedule.Trials[0], writer.ConfigurationHash) with
            {
                Outcome = OperatingEnvelopeTrialOutcome.Stopped,
                StopOutcome = OperatingEnvelopeStopOutcome.CleanupFailed,
                CleanupSucceeded = false,
                Error = quarantineReason,
            };
            await writer.WriteQuarantineAsync(trial, quarantineReason);

            var pairPlans = OperatingEnvelopeProtocol.GetPair(schedule, "idle-counters-pair-01");
            var outcomes = pairPlans.Select(plan => Trial(plan, writer.ConfigurationHash)).ToArray();
            var pair = OperatingEnvelopePairValidation.Validate(
                pairPlans[0].PairId, pairPlans[0].Population, outcomes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WritePairAsync(pair, outcomes));
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteFinalManifestAsync());

            Assert.True(File.Exists(Path.Combine(writer.RunDirectory, "quarantine.json")));
            Assert.True(File.Exists(Path.Combine(writer.RunDirectory, "quarantine.sha256")));
            Assert.False(File.Exists(Path.Combine(writer.RunDirectory, "results-manifest.json")));
            Assert.False(File.Exists(Path.Combine(writer.RunDirectory, "results.csv")));
            using var quarantine = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(writer.RunDirectory, "quarantine.json")));
            Assert.True(quarantine.RootElement.GetProperty("trialInvalid").GetBoolean());
            Assert.Equal("cleanupFailed", quarantine.RootElement.GetProperty("stopOutcome").GetString());
            var quarantineBytes = await File.ReadAllBytesAsync(Path.Combine(writer.RunDirectory, "quarantine.json"));
            var quarantineHash = Convert.ToHexString(SHA256.HashData(quarantineBytes)).ToLowerInvariant();
            Assert.Equal(quarantineHash + "\n",
                await File.ReadAllTextAsync(Path.Combine(writer.RunDirectory, "quarantine.sha256")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static OperatingEnvelopeTrialResult Trial(
        OperatingEnvelopeTrialPlan plan,
        string configurationHash)
    {
        var start = DateTimeOffset.UtcNow;
        var artifact = new OperatingEnvelopeArtifactMeasurement(
            "fixture",
            CollectionSucceeded: true,
            CaptureId: null,
            CaptureState: null,
            Accounting: OperatingEnvelopeCaptureAccounting.Ephemeral("Deterministic protocol fixture."),
            RecordCount: null,
            StoreBytes: null,
            StoreFiles: [],
            CollectorElapsedMilliseconds: 1,
            StoreEncodeAndSealMilliseconds: null,
            QueryOpenMilliseconds: null,
            QueryMilliseconds: null,
            QueriedRecords: null,
            LossAndCapNotes: [],
            Error: null);
        var requestAccounting = new OperatingEnvelopeRequestAccounting(
            0, 0, 0, 0, 0, 0, 1_000, []);
        return new(
            plan,
            configurationHash,
            OperatingEnvelopeTrialOutcome.Completed,
            OperatingEnvelopeStopOutcome.None,
            start,
            start,
            1,
            requestAccounting,
            requestAccounting,
            [],
            [],
            new(null, null, null, null, null, null, []),
            [artifact],
            1,
            true,
            [],
            [],
            null);
    }

    private sealed class CancellingReadStream(byte[] data, CancellationTokenSource cancellation)
        : MemoryStream(data)
    {
        private bool _cancelled;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (!_cancelled && read > 0)
            {
                _cancelled = true;
                cancellation.Cancel();
            }

            return read;
        }
    }
}
