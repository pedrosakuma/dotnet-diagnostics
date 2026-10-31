using System.Globalization;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.TestSupport;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class CpuHotspotMovementFixtures
{
    internal const string RegenerateFixtureEnvironmentVariable =
        "DOTNET_DIAGNOSTICS_REGENERATE_CPU_HOTSPOT_FIXTURE";
    internal const string LiveReportPathEnvironmentVariable =
        "DOTNET_DIAGNOSTICS_CPU_HOTSPOT_REPORT_PATH";
    internal static string FixturePath => ScenarioManifestLoader.ScenarioPath(
        "Fixtures",
        "CpuHotspotMovement",
        "cpu-hotspot-movement.v1.json");

    private static readonly TimeSpan LiveCaptureDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LiveWorkloadWarmup = TimeSpan.FromMilliseconds(400);
    private const int LiveWorkloadMilliseconds = 4_500;
    private const int LivePairCount = 4;
    private const string AlphaMethodId = "alpha";
    private const string BetaMethodId = "beta";

    internal static CpuHotspotMovementFixture CreateDeterministic()
    {
        var aa = Case(
            "aa-control",
            pair => Synthetic(
                $"aa-baseline-{pair}",
                pair,
                "steady-alpha",
                offeredLoad: 4,
                alpha: 600 + Variation(pair, 0, 10, -10, 5),
                beta: 250,
                other: 100,
                unresolved: 50),
            pair => Synthetic(
                $"aa-candidate-{pair}",
                pair,
                "steady-alpha-repeat",
                offeredLoad: 4,
                alpha: 595 + Variation(pair, 0, 10, 5, -5),
                beta: 255,
                other: 100,
                unresolved: 50));
        var shift = Case(
            "deterministic-hotspot-shift",
            pair => Synthetic(
                $"shift-baseline-{pair}",
                pair,
                "alpha-dominant",
                offeredLoad: 4,
                alpha: 610 + Variation(pair, -10, 0, 10, -5),
                beta: 240,
                other: 100,
                unresolved: 50),
            pair => Synthetic(
                $"shift-candidate-{pair}",
                pair,
                "beta-dominant",
                offeredLoad: 4,
                alpha: 190 + Variation(pair, 0, 10, -5, 5),
                beta: 660,
                other: 100,
                unresolved: 50));
        var changedLoad = Case(
            "changed-offered-load",
            pair => Synthetic(
                $"load-baseline-{pair}",
                pair,
                "alpha-dominant",
                offeredLoad: 4,
                alpha: 600,
                beta: 250,
                other: 100,
                unresolved: 50),
            pair => Synthetic(
                $"load-candidate-{pair}",
                pair,
                "alpha-dominant-higher-load",
                offeredLoad: 8,
                alpha: 600,
                beta: 250,
                other: 100,
                unresolved: 50));
        var unresolved = Case(
            "unresolved-symbol-degradation",
            pair => Synthetic(
                $"symbols-baseline-{pair}",
                pair,
                "alpha-dominant",
                offeredLoad: 4,
                alpha: 600,
                beta: 250,
                other: 100,
                unresolved: 50),
            pair => Synthetic(
                $"symbols-candidate-{pair}",
                pair,
                "alpha-dominant-degraded-symbols",
                offeredLoad: 4,
                alpha: 300,
                beta: 150,
                other: 50,
                unresolved: 550));
        var backend = Case(
            "backend-incompatibility",
            pair => Synthetic(
                $"backend-baseline-{pair}",
                pair,
                "alpha-dominant",
                offeredLoad: 4,
                alpha: 600,
                beta: 250,
                other: 100,
                unresolved: 50),
            pair => Synthetic(
                $"backend-candidate-{pair}",
                pair,
                "alpha-dominant",
                offeredLoad: 4,
                alpha: 600,
                beta: 250,
                other: 100,
                unresolved: 50,
                backend: "linux-perf",
                evidenceKind: "os-on-cpu-samples"));
        var lowSamples = Case(
            "low-sample-count",
            pair => Synthetic(
                $"low-baseline-{pair}",
                pair,
                "alpha-dominant",
                offeredLoad: 4,
                alpha: 18,
                beta: 8,
                other: 2,
                unresolved: 2,
                totalSamples: 30),
            pair => Synthetic(
                $"low-candidate-{pair}",
                pair,
                "beta-dominant",
                offeredLoad: 4,
                alpha: 8,
                beta: 18,
                other: 2,
                unresolved: 2,
                totalSamples: 30));

        return new(
            CpuHotspotMovementEvaluator.CurrentSchemaVersion,
            "cpu-hotspot-movement-deterministic-v1",
            [aa, shift, changedLoad, unresolved, backend, lowSamples]);
    }

    internal static async Task<CpuHotspotMovementFixture> CaptureLiveAsync(
        CancellationToken cancellationToken)
    {
        await using var sample = await LiveSampleProcess.StartPublishedAsync(
            "BadCodeSample",
            new LiveSampleOptions
            {
                HarvestListeningUrl = true,
                WaitForHttpReady = true,
                ReadinessPath = "/",
            },
            cancellationToken).ConfigureAwait(false);
        using var http = new HttpClient
        {
            BaseAddress = new Uri(sample.BaseUrl),
            Timeout = TimeSpan.FromSeconds(15),
        };
        var processRunId = $"pid-{sample.ProcessId}";
        var aaBaseline = new List<CpuHotspotCaptureEvidence>(LivePairCount);
        var aaCandidate = new List<CpuHotspotCaptureEvidence>(LivePairCount);
        var shiftBaseline = new List<CpuHotspotCaptureEvidence>(LivePairCount);
        var shiftCandidate = new List<CpuHotspotCaptureEvidence>(LivePairCount);

        for (var index = 0; index < LivePairCount; index++)
        {
            var pairKey = $"pair-{index + 1}";
            var order = index % 2 == 0
                ? ScenarioStatisticalPairOrder.BaselineFirst
                : ScenarioStatisticalPairOrder.CandidateFirst;
            var aaPair = await CapturePairAsync(
                sample.ProcessId,
                processRunId,
                http,
                pairKey,
                order,
                baseline: new("steady-alpha", 7, 1),
                candidate: new("steady-alpha-repeat", 7, 1),
                cancellationToken).ConfigureAwait(false);
            aaBaseline.Add(aaPair.Baseline with { Id = $"aa-baseline-{index + 1}" });
            aaCandidate.Add(aaPair.Candidate with { Id = $"aa-candidate-{index + 1}" });

            var shiftPair = await CapturePairAsync(
                sample.ProcessId,
                processRunId,
                http,
                pairKey,
                order,
                baseline: new("alpha-dominant", 7, 1),
                candidate: new("beta-dominant", 1, 7),
                cancellationToken).ConfigureAwait(false);
            shiftBaseline.Add(shiftPair.Baseline with { Id = $"shift-baseline-{index + 1}" });
            shiftCandidate.Add(shiftPair.Candidate with { Id = $"shift-candidate-{index + 1}" });
        }

        var aa = new CpuHotspotMovementCase(
            "aa-control",
            AlphaMethodId,
            new("baseline", ScenarioStatisticalArmRole.Baseline, aaBaseline),
            new("candidate", ScenarioStatisticalArmRole.Candidate, aaCandidate));
        var shift = new CpuHotspotMovementCase(
            "deterministic-hotspot-shift",
            AlphaMethodId,
            new("baseline", ScenarioStatisticalArmRole.Baseline, shiftBaseline),
            new("candidate", ScenarioStatisticalArmRole.Candidate, shiftCandidate));
        var changedLoad = DerivedCase(
            "changed-offered-load",
            aaBaseline,
            aaCandidate.Select(capture => capture with
            {
                Id = capture.Id.Replace("aa-", "load-", StringComparison.Ordinal),
                WorkloadVariant = "steady-alpha-higher-load",
                OfferedLoad = 16,
            }).ToArray());
        var unresolved = DerivedCase(
            "unresolved-symbol-degradation",
            aaBaseline,
            aaCandidate.Select(capture => DegradeSymbols(
                capture with
                {
                    Id = capture.Id.Replace("aa-", "symbols-", StringComparison.Ordinal),
                    WorkloadVariant = "steady-alpha-degraded-symbols",
                })).ToArray());
        var backend = DerivedCase(
            "backend-incompatibility",
            aaBaseline,
            aaCandidate.Select(capture => capture with
            {
                Id = capture.Id.Replace("aa-", "backend-", StringComparison.Ordinal),
                Backend = "linux-perf",
                EvidenceKind = "os-on-cpu-samples",
            }).ToArray());
        var lowSamples = DerivedCase(
            "low-sample-count",
            aaBaseline.Select(capture => LowSample(capture, "low-baseline")).ToArray(),
            shiftCandidate.Select(capture => LowSample(capture, "low-candidate")).ToArray());

        return new(
            CpuHotspotMovementEvaluator.CurrentSchemaVersion,
            "cpu-hotspot-movement-live-v1",
            [aa, shift, changedLoad, unresolved, backend, lowSamples]);
    }

    private static CpuHotspotMovementCase Case(
        string id,
        Func<int, CpuHotspotCaptureEvidence> baselineFactory,
        Func<int, CpuHotspotCaptureEvidence> candidateFactory)
    {
        var baseline = Enumerable.Range(1, 4).Select(baselineFactory).ToArray();
        var candidate = Enumerable.Range(1, 4).Select(candidateFactory).ToArray();
        return new(
            id,
            AlphaMethodId,
            new("baseline", ScenarioStatisticalArmRole.Baseline, baseline),
            new("candidate", ScenarioStatisticalArmRole.Candidate, candidate));
    }

    private static CpuHotspotMovementCase DerivedCase(
        string id,
        IReadOnlyList<CpuHotspotCaptureEvidence> baseline,
        IReadOnlyList<CpuHotspotCaptureEvidence> candidate)
        => new(
            id,
            AlphaMethodId,
            new("baseline", ScenarioStatisticalArmRole.Baseline, baseline),
            new("candidate", ScenarioStatisticalArmRole.Candidate, candidate));

    private static CpuHotspotCaptureEvidence Synthetic(
        string id,
        int pair,
        string variant,
        int offeredLoad,
        long alpha,
        long beta,
        long other,
        long unresolved,
        long totalSamples = 1_100,
        string backend = "eventpipe-sample-profiler",
        string evidenceKind = "stack-frequency-with-heuristic-waits")
        => new(
            id,
            $"pair-{pair}",
            PairOrder(pair),
            "fixture-process-run",
            "CoreCLR",
            "major-minor",
            "fixture-os",
            "x64",
            backend,
            evidenceKind,
            3,
            "1ms-nominal",
            "cpu-hotspot-fixture",
            "1",
            variant,
            offeredLoad,
            totalSamples,
            unresolved,
            ScenarioStatisticalRepresentation.Exact,
            [
                new(AlphaMethodId, alpha),
                new(BetaMethodId, beta),
                new("other", other),
            ]);

    private static async Task<CapturePair> CapturePairAsync(
        int processId,
        string processRunId,
        HttpClient http,
        string pairKey,
        ScenarioStatisticalPairOrder order,
        Workload baseline,
        Workload candidate,
        CancellationToken cancellationToken)
    {
        CpuHotspotCaptureEvidence left;
        CpuHotspotCaptureEvidence right;
        if (order == ScenarioStatisticalPairOrder.BaselineFirst)
        {
            left = await CaptureAsync(
                processId, processRunId, http, pairKey, order, baseline, cancellationToken)
                .ConfigureAwait(false);
            right = await CaptureAsync(
                processId, processRunId, http, pairKey, order, candidate, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            right = await CaptureAsync(
                processId, processRunId, http, pairKey, order, candidate, cancellationToken)
                .ConfigureAwait(false);
            left = await CaptureAsync(
                processId, processRunId, http, pairKey, order, baseline, cancellationToken)
                .ConfigureAwait(false);
        }

        return new(left, right);
    }

    private static async Task<CpuHotspotCaptureEvidence> CaptureAsync(
        int processId,
        string processRunId,
        HttpClient http,
        string pairKey,
        ScenarioStatisticalPairOrder order,
        Workload workload,
        CancellationToken cancellationToken)
    {
        var sampler = new EventPipeCpuSampler();
        var request = http.GetAsync(
            $"/cpu-hotspot-fixture?alphaWorkers={workload.AlphaWorkers}"
            + $"&betaWorkers={workload.BetaWorkers}&ms={LiveWorkloadMilliseconds}",
            cancellationToken);
        CpuSampleResult result;
        try
        {
            await Task.Delay(LiveWorkloadWarmup, cancellationToken).ConfigureAwait(false);
            if (request.IsCompleted)
            {
                throw new InvalidOperationException(
                    "CPU hotspot workload completed before the sampling window was armed.");
            }

            result = await sampler.SampleAsync(
                processId,
                LiveCaptureDuration,
                topN: 25,
                sourceResolution: new SourceResolutionOptions(Enabled: false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            using var response = await request.ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        return Project(
            result.Artifact,
            pairKey,
            order,
            processRunId,
            workload);
    }

    private static CpuHotspotCaptureEvidence Project(
        CpuSampleTraceArtifact artifact,
        string pairKey,
        ScenarioStatisticalPairOrder order,
        string processRunId,
        Workload workload)
    {
        var rows = CpuSampleComparableProjection.ProjectTyped(artifact, "cpu-sample");
        var unresolved = rows
            .Where(item => IsUnresolved(item.Key.Symbol.MethodFullName))
            .Sum(item => item.Value.ExclusiveSamples);
        var alpha = rows
            .Where(item => item.Key.Symbol.MethodFullName.Contains(
                "CpuHotspotWorkload.RunAlpha",
                StringComparison.Ordinal))
            .Sum(item => item.Value.InclusiveSamples);
        var beta = rows
            .Where(item => item.Key.Symbol.MethodFullName.Contains(
                "CpuHotspotWorkload.RunBeta",
                StringComparison.Ordinal))
            .Sum(item => item.Value.InclusiveSamples);
        return new(
            $"capture-{Guid.NewGuid():N}",
            pairKey,
            order,
            processRunId,
            "CoreCLR",
            "major-minor",
            OperatingSystemName(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            artifact.Evidence?.Backend.ToString() ?? "unknown",
            artifact.Evidence?.Kind.ToString() ?? "unknown",
            artifact.Duration.TotalSeconds,
            "EventPipe-SampleProfiler-default",
            "cpu-hotspot-fixture",
            "1",
            workload.Variant,
            workload.AlphaWorkers + workload.BetaWorkers,
            artifact.TotalSamples,
            unresolved,
            ScenarioStatisticalRepresentation.Exact,
            [
                new(AlphaMethodId, alpha),
                new(BetaMethodId, beta),
            ]);
    }

    private static CpuHotspotCaptureEvidence DegradeSymbols(CpuHotspotCaptureEvidence capture)
    {
        var scaled = capture.Methods.Select(method => method with
        {
            InclusiveSamples = (long)Math.Floor(method.InclusiveSamples * 0.4),
        }).ToArray();
        var unresolved = Math.Max(
            capture.UnresolvedExclusiveSamples,
            (long)Math.Ceiling(capture.TotalSamples * 0.50));
        var maximumResolved = capture.TotalSamples - unresolved;
        var resolved = scaled.Sum(method => method.InclusiveSamples);
        if (resolved > maximumResolved)
        {
            var factor = maximumResolved / (double)resolved;
            scaled = scaled.Select(method => method with
            {
                InclusiveSamples = (long)Math.Floor(method.InclusiveSamples * factor),
            }).ToArray();
        }
        return capture with
        {
            UnresolvedExclusiveSamples = unresolved,
            Methods = scaled,
        };
    }

    private static CpuHotspotCaptureEvidence LowSample(
        CpuHotspotCaptureEvidence capture,
        string prefix)
        => capture with
        {
            Id = $"{prefix}-{capture.PairKey}",
            TotalSamples = 30,
            UnresolvedExclusiveSamples = 2,
            Methods =
            [
                new(AlphaMethodId, 18),
                new(BetaMethodId, 8),
            ],
        };

    private static ScenarioStatisticalPairOrder PairOrder(int pair)
        => pair % 2 == 1
            ? ScenarioStatisticalPairOrder.BaselineFirst
            : ScenarioStatisticalPairOrder.CandidateFirst;

    private static int Variation(int pair, params int[] values)
        => values[(pair - 1) % values.Length];

    private static bool IsUnresolved(string? name)
        => string.IsNullOrEmpty(name)
            || string.Equals(name, "[unknown]", StringComparison.Ordinal)
            || name.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

    private static string OperatingSystemName()
        => OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : "other";

    private sealed record Workload(
        string Variant,
        int AlphaWorkers,
        int BetaWorkers);

    private sealed record CapturePair(
        CpuHotspotCaptureEvidence Baseline,
        CpuHotspotCaptureEvidence Candidate);
}
