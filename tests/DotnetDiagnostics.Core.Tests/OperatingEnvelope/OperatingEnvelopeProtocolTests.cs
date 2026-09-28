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
}
