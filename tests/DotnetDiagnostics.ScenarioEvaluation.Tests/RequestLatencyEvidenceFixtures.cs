using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using DotnetDiagnostics.TestSupport;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class RequestLatencyEvidenceFixtures
{
    public const string RegenerateFixtureEnvironmentVariable =
        "DOTNET_DIAGNOSTICS_REGENERATE_REQUEST_LATENCY_FIXTURE";
    public const string LiveReportPathEnvironmentVariable =
        "DOTNET_DIAGNOSTICS_REQUEST_LATENCY_REPORT_PATH";

    public static string FixturePath => ScenarioManifestLoader.ScenarioPath(
        "Fixtures",
        "RequestLatency",
        "request-latency-controlled.v1.json");

    public static RequestLatencyReplayFixture CreateDeterministic()
    {
        var aaBaseline = Arm(
            "baseline",
            ScenarioStatisticalArmRole.Baseline,
            Identity(
                "fixture-run",
                "workload.repetition-arm",
                "baseline",
                "single-endpoint"),
            Windows((window, sample) => 10 + Noise(window, sample)));
        var aaCandidate = Arm(
            "candidate",
            ScenarioStatisticalArmRole.Candidate,
            Identity(
                "fixture-run",
                "workload.repetition-arm",
                "candidate",
                "single-endpoint"),
            Windows((window, sample) => 10 + Noise(window + 2, sample + 1)));
        var shiftBaseline = Arm(
            "baseline",
            ScenarioStatisticalArmRole.Baseline,
            Identity(
                "fixture-run",
                "workload.injected-delay-ms",
                "10",
                "single-endpoint"),
            Windows((window, sample) => 10 + Noise(window, sample)));
        var shiftCandidate = Arm(
            "candidate",
            ScenarioStatisticalArmRole.Candidate,
            Identity(
                "fixture-run",
                "workload.injected-delay-ms",
                "50",
                "single-endpoint"),
            Windows((window, sample) => 50 + Noise(window + 1, sample + 3)));
        var mixedCandidate = Arm(
            "candidate",
            ScenarioStatisticalArmRole.Candidate,
            Identity(
                "fixture-run",
                "workload.repetition-arm",
                "candidate",
                "mixed-delay-endpoint"),
            Windows((window, sample) =>
                (sample % 2 == 0 ? 10 : 50) + Noise(window, sample)));
        var incompleteCandidate = Arm(
            "candidate",
            ScenarioStatisticalArmRole.Candidate,
            Identity(
                "fixture-run",
                "workload.incomplete-work",
                "errors-censored-missing",
                "single-endpoint"),
            Windows(
                (window, sample) => 10 + Noise(window + 1, sample),
                errors: 1,
                censored: 1,
                missing: 1));
        var incompleteBaseline = aaBaseline with
        {
            CompatibilityIdentity = Identity(
                "fixture-run",
                "workload.incomplete-work",
                "complete",
                "single-endpoint"),
        };
        var lowBaseline = shiftBaseline with
        {
            Windows =
            [
                shiftBaseline.Windows[0] with
                {
                    SourceCompletedCount = 2,
                    RetainedLatenciesMilliseconds =
                        shiftBaseline.Windows[0].RetainedLatenciesMilliseconds.Take(2).ToArray(),
                },
            ],
        };
        var lowCandidate = shiftCandidate with
        {
            Windows =
            [
                shiftCandidate.Windows[0] with
                {
                    SourceCompletedCount = 2,
                    RetainedLatenciesMilliseconds =
                        shiftCandidate.Windows[0].RetainedLatenciesMilliseconds.Take(2).ToArray(),
                },
            ],
        };
        var reservoirBaseline = ReservoirArm(shiftBaseline, capacity: 4);
        var reservoirCandidate = ReservoirArm(shiftCandidate, capacity: 4);
        var incompatibleCandidate = shiftCandidate with
        {
            CompatibilityIdentity = ReplaceDimensions(
                shiftCandidate.CompatibilityIdentity,
                ("target.runtime-flavor", "NativeAOT"),
                ("capture.windowing", "overlapping-1s"),
                ("quantity.filters", "endpoint=/different")),
        };

        return new(
            RequestLatencyEvidenceEvaluator.CurrentSchemaVersion,
            "request-latency-controlled-v1",
            [
                new("aa-control", aaBaseline, aaCandidate),
                new("injected-shift", shiftBaseline, shiftCandidate),
                new("changed-request-mix", aaBaseline, mixedCandidate),
                new("low-sample-count", lowBaseline, lowCandidate),
                new("reservoir-approximation", reservoirBaseline, reservoirCandidate),
                new("incompatible-identity", shiftBaseline, incompatibleCandidate),
                new("incomplete-work", incompleteBaseline, incompleteCandidate),
            ]);
    }

    public static async Task<RequestLatencyReplayFixture> CaptureLiveAsync(
        CancellationToken cancellationToken)
    {
        await using var sample = await LiveSampleProcess.StartPublishedAsync(
            "BadCodeSample",
            new LiveSampleOptions
            {
                HarvestListeningUrl = true,
                WaitForHttpReady = true,
                ReadinessPath = "/",
                DiagnosticTimeout = TimeSpan.FromSeconds(30),
                HttpTimeout = TimeSpan.FromSeconds(30),
            },
            cancellationToken).ConfigureAwait(false);
        using var http = new HttpClient
        {
            BaseAddress = new Uri(sample.BaseUrl),
            Timeout = TimeSpan.FromSeconds(5),
        };
        await CaptureCompletedAsync(http, delayMilliseconds: 1, cancellationToken)
            .ConfigureAwait(false);

        var processRunId = sample.ProcessId.ToString(CultureInfo.InvariantCulture);
        var aa = await CaptureCaseAsync(
            http,
            "aa-control",
            processRunId,
            "workload.repetition-arm",
            "baseline",
            "candidate",
            "single-endpoint",
            "single-endpoint",
            _ => 8,
            _ => 8,
            cancellationToken).ConfigureAwait(false);
        var shift = await CaptureCaseAsync(
            http,
            "injected-shift",
            processRunId,
            "workload.injected-delay-ms",
            "8",
            "55",
            "single-endpoint",
            "single-endpoint",
            _ => 8,
            _ => 55,
            cancellationToken).ConfigureAwait(false);
        var mixed = await CaptureCaseAsync(
            http,
            "changed-request-mix",
            processRunId,
            "workload.repetition-arm",
            "baseline",
            "candidate",
            "single-endpoint",
            "mixed-delay-endpoint",
            _ => 8,
            index => index % 2 == 0 ? 8 : 55,
            cancellationToken).ConfigureAwait(false);
        var incomplete = await CaptureIncompleteCaseAsync(
            http,
            processRunId,
            cancellationToken).ConfigureAwait(false);
        var low = new RequestLatencyEvidenceCase(
            "low-sample-count",
            LowSampleArm(shift.Baseline),
            LowSampleArm(shift.Candidate));
        var reservoir = new RequestLatencyEvidenceCase(
            "reservoir-approximation",
            ReservoirArm(shift.Baseline, capacity: 6),
            ReservoirArm(shift.Candidate, capacity: 6));
        var incompatible = new RequestLatencyEvidenceCase(
            "incompatible-identity",
            shift.Baseline,
            shift.Candidate with
            {
                CompatibilityIdentity = ReplaceDimensions(
                    shift.Candidate.CompatibilityIdentity,
                    ("target.runtime-flavor", "NativeAOT"),
                    ("capture.windowing", "overlapping-1s"),
                    ("quantity.filters", "endpoint=/different")),
            });
        return new(
            RequestLatencyEvidenceEvaluator.CurrentSchemaVersion,
            "request-latency-live-v1",
            [aa, shift, mixed, low, reservoir, incompatible, incomplete]);
    }

    private static async Task<RequestLatencyEvidenceCase> CaptureCaseAsync(
        HttpClient http,
        string id,
        string processRunId,
        string comparisonAxis,
        string baselineAxisValue,
        string candidateAxisValue,
        string baselineMix,
        string candidateMix,
        Func<int, int> baselineDelay,
        Func<int, int> candidateDelay,
        CancellationToken cancellationToken)
    {
        var baseline = new List<RequestLatencyWindow>(
            RequestLatencyEvidenceBounds.MinimumCompleteWindowPairs);
        var candidate = new List<RequestLatencyWindow>(
            RequestLatencyEvidenceBounds.MinimumCompleteWindowPairs);
        for (var window = 0;
             window < RequestLatencyEvidenceBounds.MinimumCompleteWindowPairs;
             window++)
        {
            var order = window % 2 == 0
                ? ScenarioStatisticalPairOrder.BaselineFirst
                : ScenarioStatisticalPairOrder.CandidateFirst;
            if (order == ScenarioStatisticalPairOrder.BaselineFirst)
            {
                baseline.Add(await CaptureWindowAsync(
                    http, window, order, baselineDelay, cancellationToken).ConfigureAwait(false));
                candidate.Add(await CaptureWindowAsync(
                    http, window, order, candidateDelay, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                candidate.Add(await CaptureWindowAsync(
                    http, window, order, candidateDelay, cancellationToken).ConfigureAwait(false));
                baseline.Add(await CaptureWindowAsync(
                    http, window, order, baselineDelay, cancellationToken).ConfigureAwait(false));
            }
        }

        return new(
            id,
            Arm(
                "baseline",
                ScenarioStatisticalArmRole.Baseline,
                Identity(processRunId, comparisonAxis, baselineAxisValue, baselineMix),
                baseline),
            Arm(
                "candidate",
                ScenarioStatisticalArmRole.Candidate,
                Identity(processRunId, comparisonAxis, candidateAxisValue, candidateMix),
                candidate));
    }

    private static async Task<RequestLatencyEvidenceCase> CaptureIncompleteCaseAsync(
        HttpClient http,
        string processRunId,
        CancellationToken cancellationToken)
    {
        var baseline = new List<RequestLatencyWindow>(
            RequestLatencyEvidenceBounds.MinimumCompleteWindowPairs);
        var candidate = new List<RequestLatencyWindow>(
            RequestLatencyEvidenceBounds.MinimumCompleteWindowPairs);
        for (var window = 0;
             window < RequestLatencyEvidenceBounds.MinimumCompleteWindowPairs;
             window++)
        {
            var order = window % 2 == 0
                ? ScenarioStatisticalPairOrder.BaselineFirst
                : ScenarioStatisticalPairOrder.CandidateFirst;
            if (order == ScenarioStatisticalPairOrder.BaselineFirst)
            {
                baseline.Add(await CaptureWindowAsync(
                    http, window, order, _ => 8, cancellationToken).ConfigureAwait(false));
                candidate.Add(await CaptureIncompleteWindowAsync(
                    http, window, order, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                candidate.Add(await CaptureIncompleteWindowAsync(
                    http, window, order, cancellationToken).ConfigureAwait(false));
                baseline.Add(await CaptureWindowAsync(
                    http, window, order, _ => 8, cancellationToken).ConfigureAwait(false));
            }
        }

        return new(
            "incomplete-work",
            Arm(
                "baseline",
                ScenarioStatisticalArmRole.Baseline,
                Identity(
                    processRunId,
                    "workload.incomplete-work",
                    "complete",
                    "single-endpoint"),
                baseline),
            Arm(
                "candidate",
                ScenarioStatisticalArmRole.Candidate,
                Identity(
                    processRunId,
                    "workload.incomplete-work",
                    "errors-censored-missing",
                    "single-endpoint"),
                candidate));
    }

    private static async Task<RequestLatencyWindow> CaptureIncompleteWindowAsync(
        HttpClient http,
        int window,
        ScenarioStatisticalPairOrder order,
        CancellationToken cancellationToken)
    {
        var completed = new List<double>(
            RequestLatencyEvidenceBounds.MinimumCompletedRequestsPerWindow);
        for (var sample = 0;
             sample < RequestLatencyEvidenceBounds.MinimumCompletedRequestsPerWindow;
             sample++)
        {
            completed.Add(await CaptureCompletedAsync(
                http, delayMilliseconds: 8, cancellationToken).ConfigureAwait(false));
        }

        using (var response = await http.GetAsync(
                   "/request-latency-fixture?delayMilliseconds=0&statusCode=503",
                   HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                throw new InvalidDataException(
                    $"Expected HTTP 503, received {(int)response.StatusCode}.");
            }
        }

        var missingCompletion = http.GetAsync(
            "/request-latency-fixture?delayMilliseconds=250",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        if (missingCompletion.IsCompleted)
        {
            using var unexpected = await missingCompletion.ConfigureAwait(false);
            throw new InvalidDataException(
                $"Expected one request without a terminal response at the window boundary, received HTTP {(int)unexpected.StatusCode}.");
        }

        using var censor = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        censor.CancelAfter(TimeSpan.FromMilliseconds(25));
        try
        {
            using var response = await http.GetAsync(
                "/request-latency-fixture?delayMilliseconds=250",
                HttpCompletionOption.ResponseHeadersRead,
                censor.Token).ConfigureAwait(false);
            throw new InvalidDataException(
                $"Expected the request to be censored, received HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (
            censor.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        using (var drained = await missingCompletion.ConfigureAwait(false))
        {
            drained.EnsureSuccessStatusCode();
        }

        return new(
            $"window-{window + 1}",
            $"pair-{window + 1}",
            order,
            completed.Count,
            ErrorCount: 1,
            CensoredCount: 1,
            MissingCount: 1,
            LostOrDropped: 0,
            ScenarioStatisticalRepresentation.Exact,
            completed);
    }

    private static async Task<RequestLatencyWindow> CaptureWindowAsync(
        HttpClient http,
        int window,
        ScenarioStatisticalPairOrder pairOrder,
        Func<int, int> delayMilliseconds,
        CancellationToken cancellationToken)
    {
        const int requestsPerWindow = 12;
        var values = new double[requestsPerWindow];
        for (var sample = 0; sample < values.Length; sample++)
        {
            values[sample] = await CaptureCompletedAsync(
                http,
                delayMilliseconds(sample),
                cancellationToken).ConfigureAwait(false);
        }

        return new(
            $"window-{window + 1}",
            $"pair-{window + 1}",
            pairOrder,
            values.Length,
            ErrorCount: 0,
            CensoredCount: 0,
            MissingCount: 0,
            LostOrDropped: 0,
            ScenarioStatisticalRepresentation.Exact,
            values);
    }

    private static async Task<double> CaptureCompletedAsync(
        HttpClient http,
        int delayMilliseconds,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var response = await http.GetAsync(
            $"/request-latency-fixture?delayMilliseconds={delayMilliseconds}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static RequestLatencyArm LowSampleArm(RequestLatencyArm source)
        => source with
        {
            Windows =
            [
                source.Windows[0] with
                {
                    SourceCompletedCount = 2,
                    RetainedLatenciesMilliseconds =
                        source.Windows[0].RetainedLatenciesMilliseconds.Take(2).ToArray(),
                },
            ],
        };

    private static RequestLatencyArm ReservoirArm(RequestLatencyArm source, int capacity)
        => source with
        {
            Windows = source.Windows.Select(window =>
            {
                var reservoir = new DeterministicLatencyReservoir(
                    capacity,
                    $"{source.Id}/{window.Id}");
                foreach (var (value, index) in window.RetainedLatenciesMilliseconds.Select(
                             (value, index) => (value, index)))
                {
                    reservoir.Add($"sample-{index + 1}", value);
                }

                return window with
                {
                    Representation = ScenarioStatisticalRepresentation.ReservoirSampled,
                    RetainedLatenciesMilliseconds = reservoir.Snapshot(),
                };
            }).ToArray(),
        };

    private static RequestLatencyArm Arm(
        string id,
        ScenarioStatisticalArmRole role,
        ScenarioStatisticalCompatibilityIdentity identity,
        IReadOnlyList<RequestLatencyWindow> windows)
        => new(id, role, identity, windows);

    private static RequestLatencyWindow[] Windows(
        Func<int, int, double> value,
        int errors = 0,
        int censored = 0,
        int missing = 0)
        => Enumerable.Range(0, RequestLatencyEvidenceBounds.MinimumCompleteWindowPairs)
            .Select(window =>
            {
                var values = Enumerable.Range(
                        0,
                        RequestLatencyEvidenceBounds.MinimumCompletedRequestsPerWindow)
                    .Select(sample => value(window, sample))
                    .ToArray();
                return new RequestLatencyWindow(
                    $"window-{window + 1}",
                    $"pair-{window + 1}",
                    window % 2 == 0
                        ? ScenarioStatisticalPairOrder.BaselineFirst
                        : ScenarioStatisticalPairOrder.CandidateFirst,
                    values.Length,
                    errors,
                    censored,
                    missing,
                    LostOrDropped: 0,
                    ScenarioStatisticalRepresentation.Exact,
                    values);
            })
            .ToArray();

    private static double Noise(int window, int sample)
        => ((window * 5 + sample * 3) % 9 - 4) * 0.1;

    private static ScenarioStatisticalCompatibilityIdentity Identity(
        string processRunId,
        string comparisonAxis,
        string axisValue,
        string requestMix)
        => new(
        [
            new("target.runtime-flavor", "CoreCLR"),
            new("target.runtime-version-policy", "same-major-minor"),
            new("target.os", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux"),
            new("target.architecture", RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()),
            new("target.process-run-id", processRunId),
            new("backend.id", "scenario-http-stopwatch"),
            new("backend.version", "1"),
            new("quantity.id", "request-latency"),
            new("quantity.unit", "ms"),
            new("quantity.aggregation", "p50-p95-p99-r7"),
            new("quantity.normalization", "per-completed-request"),
            new("quantity.filters", "endpoint=/request-latency-fixture"),
            new("quantity.representation", "exact"),
            new("capture.duration", "bounded-request-count"),
            new("capture.windowing", "non-overlapping-fixed-count"),
            new("capture.providers", "stopwatch"),
            new("capture.sampling-period", "per-completed-request"),
            new("capture.retention", $"max={RequestLatencyEvidenceBounds.MaximumRetainedSamplesPerWindow}/window"),
            new("capture.enrichment", "http-status-and-completion-state"),
            new("workload.scenario", "request-latency-distribution-shift"),
            new("workload.version", "1"),
            new("workload.parameters", "sequential-fixed-count"),
            new("workload.concurrency", "1"),
            new("workload.deployment", "local-loopback"),
            new("workload.request-mix", requestMix),
            new(
                "workload.repetition-arm",
                comparisonAxis == "workload.repetition-arm" ? axisValue : "paired"),
            new(
                "workload.injected-delay-ms",
                comparisonAxis == "workload.injected-delay-ms" ? axisValue : "not-varied"),
            new(
                "workload.incomplete-work",
                comparisonAxis == "workload.incomplete-work" ? axisValue : "complete"),
            new("independence.unit", "bounded_time_window"),
            new("independence.nesting", "request-event-within-non-overlapping-window"),
            new("independence.pairing-key", "window-pair-id"),
            new("comparison.axis", comparisonAxis),
        ]);

    private static ScenarioStatisticalCompatibilityIdentity ReplaceDimensions(
        ScenarioStatisticalCompatibilityIdentity identity,
        params (string Name, string Value)[] replacements)
    {
        var values = replacements.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        return identity with
        {
            Dimensions = identity.Dimensions.Select(item =>
                values.TryGetValue(item.Name, out var value)
                    ? item with { Value = value }
                    : item).ToArray(),
        };
    }
}
