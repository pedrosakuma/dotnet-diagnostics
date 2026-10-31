using System.Net.Http.Json;
using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Evidence;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

[Collection("LiveProcess")]
public sealed class RetainedGrowthScenarioLiveTests
{
    private const int Repetitions = 3;
    private const int SeedRetainedObjects = 4_000;
    private const int WorkloadObjects = 20_000;
    private const int PayloadBytes = 512;
    private const string TargetType = "GcDumpPressureNode";

    [Fact(Timeout = 240_000)]
    public async Task KnownShapeLiveEvidence_DistinguishesPositiveRetentionFromChurnAndStableControls()
    {
        var retained = await RunScenarioAsync(RetainedGrowthScenarioVariant.RetainedGrowth);
        var churn = await RunScenarioAsync(RetainedGrowthScenarioVariant.AllocationChurn);
        var stable = await RunScenarioAsync(RetainedGrowthScenarioVariant.StableHeap);

        var retainedResult = RetainedGrowthScenarioEvaluator.Evaluate(retained.Evidence);
        var churnResult = RetainedGrowthScenarioEvaluator.Evaluate(churn.Evidence);
        var stableResult = RetainedGrowthScenarioEvaluator.Evaluate(stable.Evidence);

        retainedResult.PositiveRetainedGrowthEvidence.Should()
            .Be(RetainedGrowthPositiveEvidence.Supported);
        churnResult.PositiveRetainedGrowthEvidence.Should()
            .Be(RetainedGrowthPositiveEvidence.NotEstablished);
        stableResult.PositiveRetainedGrowthEvidence.Should()
            .Be(RetainedGrowthPositiveEvidence.NotEstablished);
        retainedResult.RetainedInstancesDelta.Delta!.Absolute.Should().Be(WorkloadObjects);
        churnResult.RetainedInstancesDelta.Delta!.Absolute.Should().Be(0);
        stableResult.RetainedInstancesDelta.Delta!.Absolute.Should().Be(0);
        retainedResult.PersistenceRate.Rate!.Value.Should().Be(1);
        churnResult.PersistenceRate.Rate!.Value.Should().Be(0);
        stableResult.PersistenceRate.Rate!.Value.Should().Be(0);
        retainedResult.RootPathEvidence.Availability.Should()
            .Be(RetainedGrowthRootPathAvailability.UnavailableByMechanism);
        churnResult.RootPathEvidence.Availability.Should()
            .Be(RetainedGrowthRootPathAvailability.UnavailableByMechanism);
        stableResult.RootPathEvidence.Availability.Should()
            .Be(RetainedGrowthRootPathAvailability.UnavailableByMechanism);
        retainedResult.RepeatedRetainedByteSpread.Availability.Should()
            .Be(ScenarioStatisticalAvailability.Available);
        retainedResult.RepeatedRetainedByteInterval.Availability.Should()
            .Be(ScenarioStatisticalAvailability.Available);
        retainedResult.CaptureInducedGen2Collections.Should().BeGreaterThanOrEqualTo(
            Repetitions * RetainedGrowthScenarioBounds.MaximumCapturesPerRun);
        churnResult.CaptureInducedGen2Collections.Should().BeGreaterThanOrEqualTo(
            Repetitions * RetainedGrowthScenarioBounds.MaximumCapturesPerRun);
        stableResult.CaptureInducedGen2Collections.Should().BeGreaterThanOrEqualTo(
            Repetitions * RetainedGrowthScenarioBounds.MaximumCapturesPerRun);

        retained.Truth.Should().OnlyContain(item =>
            item.LastAllocatedObjectCount == WorkloadObjects
            && item.LastAllocatedPayloadBytes == (long)WorkloadObjects * PayloadBytes
            && item.PressureCount == SeedRetainedObjects + WorkloadObjects);
        churn.Truth.Should().OnlyContain(item =>
            item.LastAllocatedObjectCount == WorkloadObjects
            && item.LastAllocatedPayloadBytes == (long)WorkloadObjects * PayloadBytes
            && item.PressureCount == SeedRetainedObjects);
        stable.Truth.Should().OnlyContain(item =>
            item.PressureCount == SeedRetainedObjects);

        var retainedTraffic = retainedResult.AllocationTraffic.Estimate!.Value;
        var churnTraffic = churnResult.AllocationTraffic.Estimate!.Value;
        retainedTraffic.Should().BeGreaterThan(0);
        churnTraffic.Should().BeGreaterThan(0);
        (retainedTraffic / churnTraffic).Should().BeInRange(0.25, 4);

        Console.WriteLine("RETAINED_GROWTH_LIVE_EVIDENCE " + System.Text.Json.JsonSerializer.Serialize(new
        {
            retained = new
            {
                result = retainedResult,
                truth = retained.Truth,
            },
            churn = new
            {
                result = churnResult,
                truth = churn.Truth,
            },
            stable = new
            {
                result = stableResult,
                truth = stable.Truth,
            },
            observerEffects = new
            {
                gcdumpInducesBlockingGen2 = true,
                explicitPostWorkloadStabilization = true,
                eventPipeLossCountAvailable = false,
                allocationSamplerIsApproximate = true,
            },
        }));
    }

    private static async Task<LiveScenario> RunScenarioAsync(RetainedGrowthScenarioVariant variant)
    {
        var runs = new List<RetainedGrowthScenarioRun>(Repetitions);
        var truth = new List<RetainedGrowthStatus>(Repetitions);
        for (var repetition = 1; repetition <= Repetitions; repetition++)
        {
            var trial = await RunTrialAsync(variant, repetition);
            runs.Add(trial.Run);
            truth.Add(trial.Truth);
        }

        var workloadObjects = variant == RetainedGrowthScenarioVariant.StableHeap
            ? 0
            : WorkloadObjects;
        var parameters =
            $"seed={SeedRetainedObjects};growth={workloadObjects};payload={PayloadBytes}";
        var compatibility = new RetainedGrowthCompatibility(
            "CoreCLR",
            "major-minor",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            "EventPipe-gcdump",
            "1",
            "bounded-30s-timeout",
            "one-induced-gc-snapshot",
            "Microsoft-Windows-DotNETRuntime",
            "not-applicable",
            "snapshot-top-types=4096",
            "none",
            "retained-growth-known-shape",
            "1",
            parameters,
            "1",
            "owned-local-process");
        return new(
            new RetainedGrowthScenarioEvidence(
                1,
                $"live-{variant.ToString().ToLowerInvariant()}",
                variant,
                TargetType,
                compatibility,
                compatibility,
                runs),
            truth);
    }

    private static async Task<LiveTrial> RunTrialAsync(
        RetainedGrowthScenarioVariant variant,
        int repetition)
    {
        await using var sample = await LiveSampleProcess.StartPublishedAsync(
            "CoreClrSample",
            new LiveSampleOptions
            {
                HarvestListeningUrl = true,
                WaitForHttpReady = true,
                ReadinessPath = "/weatherforecast",
                Environment = new Dictionary<string, string>
                {
                    ["DOTNET_gcServer"] = "0",
                },
            });
        using var http = new HttpClient
        {
            BaseAddress = new Uri(sample.BaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };

        var reset = await PostStatusAsync(
            http,
            $"/retained-growth/reset?retainedCount={SeedRetainedObjects}&payloadBytes={PayloadBytes}");
        reset.PressureCount.Should().Be(SeedRetainedObjects);
        var baseline = await CaptureAsync(
            sample.ProcessId,
            http,
            $"run-{repetition}-baseline",
            postGcStabilized: true);

        RetainedGrowthAllocationTraffic? traffic = null;
        RetainedGrowthStatus workloadTruth = reset;
        if (variant is RetainedGrowthScenarioVariant.RetainedGrowth
            or RetainedGrowthScenarioVariant.AllocationChurn)
        {
            var mode = variant == RetainedGrowthScenarioVariant.RetainedGrowth
                ? "retained"
                : "churn";
            var sampler = new EventPipeAllocationSampler();
            var sampleTask = sampler.SampleAsync(
                sample.ProcessId,
                TimeSpan.FromSeconds(2),
                topN: 25);
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            workloadTruth = await PostStatusAsync(
                http,
                $"/retained-growth/apply?mode={mode}&objectCount={WorkloadObjects}"
                    + $"&payloadBytes={PayloadBytes}&batches=10&delayMilliseconds=20");
            var allocation = await sampleTask;
            traffic = new(
                allocation.Summary.TotalBytes,
                allocation.Summary.TotalEvents,
                LostOrDropped: 0,
                ScenarioStatisticalRepresentation.Exact);
        }

        var stabilized = await PostStatusAsync(http, "/retained-growth/stabilize");
        var candidate = await CaptureAsync(
            sample.ProcessId,
            http,
            $"run-{repetition}-candidate",
            postGcStabilized: true);
        var persisted = await PostStatusAsync(http, "/retained-growth/stabilize");
        var persistence = await CaptureAsync(
            sample.ProcessId,
            http,
            $"run-{repetition}-persistence",
            postGcStabilized: true);

        stabilized.PressureCount.Should().Be(workloadTruth.PressureCount);
        persisted.PressureCount.Should().Be(workloadTruth.PressureCount);
        FindType(baseline, TargetType).Instances.Should().Be(SeedRetainedObjects);
        var expectedRetained = variant == RetainedGrowthScenarioVariant.RetainedGrowth
            ? SeedRetainedObjects + WorkloadObjects
            : SeedRetainedObjects;
        FindType(candidate, TargetType).Instances.Should().Be(expectedRetained);
        FindType(persistence, TargetType).Instances.Should().Be(expectedRetained);

        return new(
            new RetainedGrowthScenarioRun(
                $"run-{repetition}",
                repetition % 2 == 1
                    ? ScenarioStatisticalPairOrder.BaselineFirst
                    : ScenarioStatisticalPairOrder.CandidateFirst,
                baseline,
                candidate,
                persistence,
                traffic),
            workloadTruth);
    }

    private static async Task<RetainedGrowthHeapObservation> CaptureAsync(
        int processId,
        HttpClient http,
        string captureId,
        bool postGcStabilized)
    {
        var before = await GetStatusAsync(http);
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "test-artifacts",
            nameof(RetainedGrowthScenarioLiveTests),
            captureId + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            var collector = new GcDumpHeapSnapshotCollector(
                artifactRoot: new ScenarioArtifactRootProvider(artifactRoot));
            var snapshot = await collector.CollectAsync(
                processId,
                new GcDumpOptions(
                    TopTypes: 20,
                    SnapshotTopTypes: RetainedGrowthScenarioBounds.MaximumTypesPerCapture,
                    Timeout: TimeSpan.FromSeconds(30),
                    ExportTrace: false),
                CancellationToken.None);
            var after = await GetStatusAsync(http);
            var status = snapshot.GcDumpStatus!;
            var captureCompleted = status.GcStopObserved
                && status.EventStreamCompleted
                && !status.TimedOut
                && !status.ReaderFailed;
            var types = snapshot.TopTypesByBytes
                .GroupBy(type => type.TypeFullName, StringComparer.Ordinal)
                .Select(group => new RetainedGrowthTypeObservation(
                    group.Key,
                    group.Sum(type => type.TotalBytes),
                    group.Sum(type => type.InstanceCount)))
                .OrderBy(type => type.TypeName, StringComparer.Ordinal)
                .ToArray();
            return new(
                captureId,
                snapshot.Heap.TotalBytes,
                types,
                postGcStabilized,
                after.Gen2Collections - before.Gen2Collections,
                captureCompleted,
                LostOrDropped: 0,
                ScenarioStatisticalRepresentation.Exact,
                snapshot.Quality?.Conclusions.RetainedExplicitPositiveEvidence
                    == EvidenceConclusionSupport.Supported,
                GcDumpEvidence.SupportsAbsence(snapshot),
                RetainedGrowthRootPathAvailability.UnavailableByMechanism,
                RootPathCount: null);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    private static RetainedGrowthTypeObservation FindType(
        RetainedGrowthHeapObservation observation,
        string typeName)
        => observation.Types.Single(type =>
            string.Equals(type.TypeName, typeName, StringComparison.Ordinal));

    private static async Task<RetainedGrowthStatus> PostStatusAsync(HttpClient http, string path)
    {
        using var response = await http.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RetainedGrowthStatus>())!;
    }

    private static async Task<RetainedGrowthStatus> GetStatusAsync(HttpClient http)
        => (await http.GetFromJsonAsync<RetainedGrowthStatus>("/gcdump-known-graph/status"))!;

    private sealed record LiveTrial(
        RetainedGrowthScenarioRun Run,
        RetainedGrowthStatus Truth);

    private sealed record LiveScenario(
        RetainedGrowthScenarioEvidence Evidence,
        IReadOnlyList<RetainedGrowthStatus> Truth);

    private sealed record RetainedGrowthStatus(
        int PressureCount,
        int PressurePayloadBytes,
        string? LastWorkload,
        int LastAllocatedObjectCount,
        long LastAllocatedPayloadBytes,
        long LastChecksum,
        DateTimeOffset? LastWorkloadAtUtc,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections);

    private sealed class ScenarioArtifactRootProvider : IArtifactRootProvider
    {
        public ScenarioArtifactRootProvider(string root)
        {
            Root = Path.GetFullPath(root);
            Directory.CreateDirectory(Root);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(
                        Root,
                        UnixFileMode.UserRead
                            | UnixFileMode.UserWrite
                            | UnixFileMode.UserExecute);
                }
                catch (PlatformNotSupportedException)
                {
                }
            }
        }

        public string Root { get; }
    }
}
