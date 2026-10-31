using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class RequestLatencyEvidenceBounds
{
    public const int MaximumCases = 16;
    public const int MaximumWindowsPerArm = 32;
    public const int MaximumRetainedSamplesPerWindow = 256;
    public const int MinimumCompleteWindowPairs = 8;
    public const int MinimumCompletedRequestsPerWindow = 8;
    public const int BootstrapResamples = 2_048;
    public const double ConfidenceLevel = 0.95;
    public const double MinimumActionableMedianDeltaMilliseconds = 10;
    public const double MinimumAbsoluteRankBiserialEffect = 0.75;
}

public enum RequestLatencyNextAction
{
    [JsonStringEnumMemberName("deeper_investigation_warranted")]
    DeeperInvestigationWarranted,

    [JsonStringEnumMemberName("inconclusive")]
    Inconclusive,
}

public enum RequestLatencyGoNoGo
{
    [JsonStringEnumMemberName("go")]
    Go,

    [JsonStringEnumMemberName("no_go")]
    NoGo,
}

public sealed record RequestLatencyWindow(
    string Id,
    string PairKey,
    ScenarioStatisticalPairOrder PairOrder,
    int SourceCompletedCount,
    int ErrorCount,
    int CensoredCount,
    int MissingCount,
    long LostOrDropped,
    ScenarioStatisticalRepresentation Representation,
    IReadOnlyList<double> RetainedLatenciesMilliseconds);

public sealed record RequestLatencyArm(
    string Id,
    ScenarioStatisticalArmRole Role,
    ScenarioStatisticalCompatibilityIdentity CompatibilityIdentity,
    IReadOnlyList<RequestLatencyWindow> Windows);

public sealed record RequestLatencyEvidenceCase(
    string Id,
    RequestLatencyArm Baseline,
    RequestLatencyArm Candidate);

public sealed record RequestLatencyReplayFixture(
    int SchemaVersion,
    string Id,
    IReadOnlyList<RequestLatencyEvidenceCase> Cases);

public sealed record RequestLatencyOutcomeCounts(
    int SourceCompleted,
    int RetainedCompleted,
    int Errors,
    int Censored,
    int Missing,
    long LostOrDropped);

public sealed record RequestLatencyQuantileEstimate(
    double Probability,
    double? ValueMilliseconds,
    int SourceCompletedCount,
    int RetainedCompletedCount);

public sealed record RequestLatencyEffectSize(
    string Method,
    ScenarioStatisticalAvailability Availability,
    double? Value,
    string? UnavailableReason);

public sealed record RequestLatencyQuantileComparison(
    double Probability,
    ScenarioStatisticalAvailability Availability,
    RequestLatencyQuantileEstimate Baseline,
    RequestLatencyQuantileEstimate Candidate,
    double? AbsoluteDeltaMilliseconds,
    double? RelativeDelta,
    double? PairedWindowMedianDeltaMilliseconds,
    ScenarioStatisticalInterval? PairedWindowBootstrapInterval,
    RequestLatencyEffectSize EffectSize,
    IReadOnlyList<string> Limitations);

public sealed record RequestLatencyIdentityMismatch(
    string Dimension,
    string? BaselineValue,
    string? CandidateValue,
    string Reason);

public sealed record RequestLatencyCaseResult(
    string Id,
    ScenarioStatisticalAvailability Availability,
    RequestLatencyOutcomeCounts BaselineCounts,
    RequestLatencyOutcomeCounts CandidateCounts,
    IReadOnlyList<RequestLatencyIdentityMismatch> IdentityMismatches,
    IReadOnlyList<RequestLatencyQuantileComparison> Quantiles,
    RequestLatencyNextAction NextAction,
    IReadOnlyList<string> Limitations);

public sealed record RequestLatencyEvidenceReport(
    int SchemaVersion,
    string Id,
    RequestLatencyGoNoGo Verdict,
    IReadOnlyList<RequestLatencyCaseResult> Cases,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Limitations);

public static class RequestLatencyEvidenceEvaluator
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFixtureBytes = 512 * 1024;
    private static readonly double[] QuantileProbabilities = [0.50, 0.95, 0.99];
    private static readonly string[] RequiredCaseIds =
    [
        "aa-control",
        "injected-shift",
        "changed-request-mix",
        "low-sample-count",
        "reservoir-approximation",
        "incompatible-identity",
        "incomplete-work",
    ];

    public static RequestLatencyEvidenceReport Evaluate(RequestLatencyReplayFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        Validate(fixture);
        var results = fixture.Cases.Select(EvaluateCase).ToArray();
        var resultsById = results.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var exactCasesPresent = resultsById.Count == RequiredCaseIds.Length
            && RequiredCaseIds.All(resultsById.ContainsKey);
        var control = exactCasesPresent ? resultsById["aa-control"] : null;
        var shift = exactCasesPresent ? resultsById["injected-shift"] : null;
        var controlP95 = control?.Quantiles.Single(item => item.Probability == 0.95);
        var shiftP95 = shift?.Quantiles.Single(item => item.Probability == 0.95);
        var shiftP99 = shift?.Quantiles.Single(item => item.Probability == 0.99);
        var incomplete = exactCasesPresent ? resultsById["incomplete-work"] : null;
        var edgeCasesExplicit = exactCasesPresent
            && resultsById["changed-request-mix"].Availability
                == ScenarioStatisticalAvailability.Incompatible
            && resultsById["low-sample-count"].Availability
                == ScenarioStatisticalAvailability.InsufficientObservations
            && resultsById["reservoir-approximation"].Availability
                == ScenarioStatisticalAvailability.UnsupportedApproximation
            && resultsById["incompatible-identity"].Availability
                == ScenarioStatisticalAvailability.Incompatible
            && incomplete!.Availability
                == ScenarioStatisticalAvailability.InsufficientObservations
            && incomplete.CandidateCounts.Errors > 0
            && incomplete.CandidateCounts.Censored > 0
            && incomplete.CandidateCounts.Missing > 0
            && resultsById.Values
                .Where(item => item.Id is not ("aa-control" or "injected-shift"))
                .All(item => item.NextAction == RequestLatencyNextAction.Inconclusive);
        var go = exactCasesPresent
            && control!.NextAction == RequestLatencyNextAction.Inconclusive
            && shiftP95!.Availability == ScenarioStatisticalAvailability.Available
            && shiftP99!.Availability == ScenarioStatisticalAvailability.Available
            && shift!.NextAction == RequestLatencyNextAction.DeeperInvestigationWarranted
            && shiftP95.PairedWindowBootstrapInterval?.Lower
                > ControlIntervalMagnitude(controlP95!)
            && edgeCasesExplicit;

        return new(
            CurrentSchemaVersion,
            fixture.Id,
            go ? RequestLatencyGoNoGo.Go : RequestLatencyGoNoGo.NoGo,
            results,
            go
                ?
                [
                    "Repeated A/A windows remained below the scenario action boundary while the injected shift crossed it at p95 and p99.",
                    "Uncertainty resampled complete non-overlapping window pairs rather than correlated request events.",
                    "Changed request mix, low counts, approximation, incompatible identity, and incomplete work produced explicit inconclusion instead of a performance verdict.",
                ]
                :
                [
                    "The controlled evidence did not distinguish the injected shift from A/A variability under the predeclared compatibility and sample requirements.",
                ],
            [
                "The GO/NO-GO verdict evaluates this synthetic evidence protocol, not application performance.",
                "A narrow interval does not establish equivalent offered load, endpoint mix, production importance, or causation.",
                "Request-level p50/p95/p99 values are descriptive; inferential resampling uses complete bounded window pairs.",
            ]);
    }

    public static RequestLatencyReplayFixture ReadFixture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is < 1 or > MaximumFixtureBytes)
        {
            throw new InvalidDataException(
                $"Request-latency replay fixture must be between 1 and {MaximumFixtureBytes} bytes.");
        }

        try
        {
            var fixture = JsonSerializer.Deserialize(
                bytes,
                RequestLatencyEvidenceJsonContext.Default.RequestLatencyReplayFixture)
                ?? throw new InvalidDataException($"Request-latency replay fixture '{path}' was empty.");
            Validate(fixture);
            return fixture;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Request-latency replay fixture '{path}' is invalid: {exception.Message}",
                exception);
        }
    }

    public static void WriteFixture(string path, RequestLatencyReplayFixture fixture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fixture);
        Validate(fixture);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            fixture,
            RequestLatencyEvidenceJsonContext.Default.RequestLatencyReplayFixture);
        if (bytes.Length > MaximumFixtureBytes)
        {
            throw new InvalidDataException(
                $"Request-latency replay fixture exceeds {MaximumFixtureBytes} bytes.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public static void WriteReport(string path, RequestLatencyEvidenceReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                report,
                RequestLatencyEvidenceJsonContext.Default.RequestLatencyEvidenceReport));
    }

    private static RequestLatencyCaseResult EvaluateCase(RequestLatencyEvidenceCase evidenceCase)
    {
        var baselineCounts = Counts(evidenceCase.Baseline);
        var candidateCounts = Counts(evidenceCase.Candidate);
        var mismatches = CompareIdentity(evidenceCase.Baseline, evidenceCase.Candidate);
        var baseAvailability = mismatches.Count > 0
            ? ScenarioStatisticalAvailability.Incompatible
            : baselineCounts.LostOrDropped + candidateCounts.LostOrDropped > 0
                ? ScenarioStatisticalAvailability.LossDetected
                : ScenarioStatisticalAvailability.Available;
        var quantiles = QuantileProbabilities.Select(probability => EvaluateQuantile(
            evidenceCase, probability, baseAvailability, baselineCounts, candidateCounts, mismatches)).ToArray();
        var action = quantiles.Any(item =>
                item.Availability == ScenarioStatisticalAvailability.Available
                && item.Probability >= 0.95
                && item.PairedWindowBootstrapInterval is { Lower: > 0 } interval
                && interval.Lower >= RequestLatencyEvidenceBounds.MinimumActionableMedianDeltaMilliseconds
                && item.EffectSize is
                {
                    Availability: ScenarioStatisticalAvailability.Available,
                    Value: not null,
                }
                && Math.Abs(item.EffectSize.Value.Value)
                    >= RequestLatencyEvidenceBounds.MinimumAbsoluteRankBiserialEffect)
            ? RequestLatencyNextAction.DeeperInvestigationWarranted
            : RequestLatencyNextAction.Inconclusive;
        var limitations = new List<string>
        {
            "Quantiles describe completed retained requests and do not treat request events as independent inferential units.",
            "The scenario action is diagnostic routing guidance, not a regression or improvement verdict.",
        };
        if (mismatches.Count > 0)
        {
            limitations.Add("Evidence was not pooled because compatibility identity differed.");
        }
        if (baselineCounts.Errors + baselineCounts.Censored + baselineCounts.Missing
            + candidateCounts.Errors + candidateCounts.Censored + candidateCounts.Missing > 0)
        {
            limitations.Add("Errors, censored requests, and missing work remain separate from completed latency quantiles.");
        }
        if (evidenceCase.Baseline.Windows.Concat(evidenceCase.Candidate.Windows)
            .Any(window => window.Representation != ScenarioStatisticalRepresentation.Exact))
        {
            limitations.Add("Reservoir-sampled latency values remain approximate and do not support interval or effect-size claims.");
        }

        return new(
            evidenceCase.Id,
            quantiles.All(item => item.Availability == ScenarioStatisticalAvailability.Available)
                ? ScenarioStatisticalAvailability.Available
                : quantiles.Select(item => item.Availability).First(item =>
                    item != ScenarioStatisticalAvailability.Available),
            baselineCounts,
            candidateCounts,
            mismatches,
            quantiles,
            action,
            limitations);
    }

    private static double ControlIntervalMagnitude(
        RequestLatencyQuantileComparison comparison)
        => comparison.PairedWindowBootstrapInterval is { } interval
            ? Math.Max(Math.Abs(interval.Lower), Math.Abs(interval.Upper))
            : double.PositiveInfinity;

    private static RequestLatencyQuantileComparison EvaluateQuantile(
        RequestLatencyEvidenceCase evidenceCase,
        double probability,
        ScenarioStatisticalAvailability baseAvailability,
        RequestLatencyOutcomeCounts baselineCounts,
        RequestLatencyOutcomeCounts candidateCounts,
        List<RequestLatencyIdentityMismatch> mismatches)
    {
        var pairing = PairWindows(evidenceCase.Baseline, evidenceCase.Candidate);
        var baselineEstimate = QuantileEstimate(
            pairing.Pairs.Select(item => item.Baseline),
            probability);
        var candidateEstimate = QuantileEstimate(
            pairing.Pairs.Select(item => item.Candidate),
            probability);
        var limitations = new List<string>();
        if (mismatches.Count > 0)
        {
            limitations.Add("Compatibility mismatches prevent pooled comparison.");
        }

        var incomplete = baselineCounts.Errors + baselineCounts.Censored + baselineCounts.Missing
            + candidateCounts.Errors + candidateCounts.Censored + candidateCounts.Missing;
        var approximate = evidenceCase.Baseline.Windows.Concat(evidenceCase.Candidate.Windows)
            .Any(window => window.Representation != ScenarioStatisticalRepresentation.Exact);
        var pairs = CompletePairs(pairing.Pairs, probability);
        var belowWindowMinimum = pairing.Pairs
            .SelectMany(item => new[] { item.Baseline, item.Candidate })
            .Any(window => window.SourceCompletedCount
                < RequestLatencyEvidenceBounds.MinimumCompletedRequestsPerWindow);
        var availability = baseAvailability != ScenarioStatisticalAvailability.Available
            ? baseAvailability
            : pairing.PairOrderMismatch
                ? ScenarioStatisticalAvailability.Incompatible
                : approximate
                ? ScenarioStatisticalAvailability.UnsupportedApproximation
                : incomplete > 0 || pairing.UnpairedWindows > 0 || belowWindowMinimum
                    || pairs.Count < RequestLatencyEvidenceBounds.MinimumCompleteWindowPairs
                    ? ScenarioStatisticalAvailability.InsufficientObservations
                    : ScenarioStatisticalAvailability.Available;

        double? absolute = null;
        double? relative = null;
        if (availability != ScenarioStatisticalAvailability.Incompatible
            && baselineEstimate.ValueMilliseconds is double baseline
            && candidateEstimate.ValueMilliseconds is double candidate)
        {
            absolute = candidate - baseline;
            if (baseline != 0)
            {
                relative = absolute / Math.Abs(baseline);
            }
        }

        double? pairedMedian = null;
        ScenarioStatisticalInterval? interval = null;
        var effect = new RequestLatencyEffectSize(
            "matched-pairs-rank-biserial-v1",
            availability,
            null,
            availability == ScenarioStatisticalAvailability.Available
                ? null
                : UnavailableReason(
                    availability,
                    incomplete,
                    pairing.UnpairedWindows,
                    pairing.PairOrderMismatch,
                    belowWindowMinimum,
                    pairs.Count));
        if (availability == ScenarioStatisticalAvailability.Available)
        {
            var deltas = pairs.Select(pair => pair.Candidate - pair.Baseline).ToArray();
            var medianResult = ScenarioStatisticalEvidenceHarness.Evaluate(
                StatisticalRequest(
                    evidenceCase,
                    probability,
                    deltas,
                    ScenarioStatisticalMethodIds.WindowMedian));
            var bootstrapResult = ScenarioStatisticalEvidenceHarness.Evaluate(
                StatisticalRequest(
                    evidenceCase,
                    probability,
                    deltas,
                    ScenarioStatisticalMethodIds.PercentileBootstrapInterval));
            if (medianResult.Availability != ScenarioStatisticalAvailability.Available
                || medianResult.Estimate is null
                || bootstrapResult.Availability != ScenarioStatisticalAvailability.Available
                || bootstrapResult.Interval is null)
            {
                var unavailable = medianResult.Availability
                        != ScenarioStatisticalAvailability.Available
                    || medianResult.Estimate is null
                    ? medianResult
                    : bootstrapResult;
                availability = unavailable.Availability
                    == ScenarioStatisticalAvailability.Available
                    ? ScenarioStatisticalAvailability.InsufficientObservations
                    : unavailable.Availability;
                var reason = unavailable.UnavailableFields.Count > 0
                    ? unavailable.UnavailableFields[0].Reason
                    : "statistical_harness_result_unavailable";
                effect = new(
                    "matched-pairs-rank-biserial-v1",
                    availability,
                    null,
                    reason);
                limitations.Add(reason);
            }
            else
            {
                pairedMedian = medianResult.Estimate;
                interval = bootstrapResult.Interval;
                effect = new(
                    "matched-pairs-rank-biserial-v1",
                    ScenarioStatisticalAvailability.Available,
                    MatchedPairsRankBiserial(deltas),
                    null);
                limitations.Add("The bootstrap interval resamples complete non-overlapping window-pair deltas.");
                limitations.Add("The rank-biserial effect size applies only to exact complete paired windows with compatible measurement semantics.");
            }
        }
        else
        {
            limitations.Add(UnavailableReason(
                availability,
                incomplete,
                pairing.UnpairedWindows,
                pairing.PairOrderMismatch,
                belowWindowMinimum,
                pairs.Count));
        }

        return new(
            probability,
            availability,
            baselineEstimate,
            candidateEstimate,
            absolute,
            relative,
            pairedMedian,
            interval,
            effect,
            limitations);
    }

    private static ScenarioStatisticalEvidenceRequest StatisticalRequest(
        RequestLatencyEvidenceCase evidenceCase,
        double probability,
        IReadOnlyList<double> deltas,
        string methodId)
    {
        var identity = StatisticalIdentity(
            evidenceCase.Baseline.CompatibilityIdentity,
            probability);
        var attempts = deltas.Select((_, index) => new ScenarioStatisticalAttempt(
            $"rep-{index + 1}",
            "attempt-1",
            ScenarioStatisticalAttemptOutcome.Passed)).ToArray();
        var observations = deltas.Select((value, index) => new ScenarioStatisticalObservation(
            $"paired-window-delta-{index + 1}",
            $"rep-{index + 1}",
            "attempt-1",
            "current-environment",
            "request-latency-capture",
            $"window-pair-{index + 1}",
            $"window-pair-{index + 1}",
            value,
            null,
            SourceEventCount: 2,
            LostOrDropped: 0,
            ScenarioStatisticalRepresentation.Exact)).ToArray();
        var bootstrap = methodId == ScenarioStatisticalMethodIds.PercentileBootstrapInterval;
        return new(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            new ScenarioStatisticalProtocol(
                $"request-latency-p{probability * 100:0}-paired-window-delta",
                "1",
                new ScenarioStatisticalMethod(methodId, 1),
                ScenarioStatisticalIndependenceUnit.BoundedTimeWindow,
                RequestLatencyEvidenceBounds.MinimumCompleteWindowPairs,
                AdmitLoss: false,
                AdmitApproximation: false,
                BootstrapResamples: bootstrap
                    ? RequestLatencyEvidenceBounds.BootstrapResamples
                    : null,
                ConfidenceLevel: bootstrap
                    ? RequestLatencyEvidenceBounds.ConfidenceLevel
                    : null,
                ParentMethodId: bootstrap
                    ? ScenarioStatisticalMethodIds.WindowMedian
                    : null),
            [new ScenarioStatisticalArm(
                "paired-window-deltas",
                ScenarioStatisticalArmRole.Observed,
                identity,
                attempts,
                observations)],
            "paired-window-deltas");
    }

    private static ScenarioStatisticalCompatibilityIdentity StatisticalIdentity(
        ScenarioStatisticalCompatibilityIdentity source,
        double probability)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["target.process-run-id"] = "paired-window-replay",
            ["quantity.id"] = $"request-latency-p{probability * 100:0}-paired-window-delta",
            ["quantity.aggregation"] = "median-of-paired-window-quantile-deltas",
            ["quantity.normalization"] = "per-completed-request",
            ["quantity.filters"] = "completed-only",
            ["quantity.representation"] = "exact",
            ["independence.unit"] = "bounded_time_window",
            ["independence.nesting"] = "request-event-within-non-overlapping-window-pair",
            ["independence.pairing-key"] = "window-pair-id",
            ["comparison.axis"] = "workload.injected-delay-ms",
        };
        var dimensions = source.Dimensions
            .Select(item => replacements.TryGetValue(item.Name, out var value)
                ? item with { Value = value }
                : item)
            .ToDictionary(item => item.Name, item => item, StringComparer.Ordinal);
        foreach (var (name, value) in replacements)
        {
            dimensions[name] = new(name, value);
        }

        return new(dimensions.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray());
    }

    private static RequestLatencyQuantileEstimate QuantileEstimate(
        IEnumerable<RequestLatencyWindow> windows,
        double probability)
    {
        var retainedWindows = windows.ToArray();
        var values = retainedWindows
            .SelectMany(window => window.RetainedLatenciesMilliseconds)
            .Where(double.IsFinite)
            .Order()
            .ToArray();
        return new(
            probability,
            values.Length == 0 ? null : QuantileR7(values, probability),
            retainedWindows.Sum(window => window.SourceCompletedCount),
            values.Length);
    }

    private static List<(double Baseline, double Candidate)> CompletePairs(
        IReadOnlyList<(RequestLatencyWindow Baseline, RequestLatencyWindow Candidate)> windows,
        double probability)
    {
        var pairs = new List<(double Baseline, double Candidate)>();
        foreach (var (left, right) in windows)
        {
            if (left.RetainedLatenciesMilliseconds.Count == 0
                || right.RetainedLatenciesMilliseconds.Count == 0)
            {
                continue;
            }

            pairs.Add((
                QuantileR7(left.RetainedLatenciesMilliseconds.Order().ToArray(), probability),
                QuantileR7(right.RetainedLatenciesMilliseconds.Order().ToArray(), probability)));
        }

        return pairs;
    }

    private static PairingResult PairWindows(
        RequestLatencyArm baseline,
        RequestLatencyArm candidate)
    {
        var baselineWindows = baseline.Windows.ToDictionary(
            item => item.PairKey,
            StringComparer.Ordinal);
        var candidateWindows = candidate.Windows.ToDictionary(
            item => item.PairKey,
            StringComparer.Ordinal);
        var commonKeys = baselineWindows.Keys
            .Intersect(candidateWindows.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var pairs = commonKeys.Select(key => (
            Baseline: baselineWindows[key],
            Candidate: candidateWindows[key])).ToArray();
        return new(
            pairs,
            PairOrderMismatch: pairs.Any(item =>
                item.Baseline.PairOrder != item.Candidate.PairOrder),
            UnpairedWindows: baselineWindows.Count + candidateWindows.Count
                - pairs.Length * 2);
    }

    private static RequestLatencyOutcomeCounts Counts(RequestLatencyArm arm)
        => new(
            arm.Windows.Sum(window => window.SourceCompletedCount),
            arm.Windows.Sum(window => window.RetainedLatenciesMilliseconds.Count),
            arm.Windows.Sum(window => window.ErrorCount),
            arm.Windows.Sum(window => window.CensoredCount),
            arm.Windows.Sum(window => window.MissingCount),
            arm.Windows.Sum(window => window.LostOrDropped));

    private static List<RequestLatencyIdentityMismatch> CompareIdentity(
        RequestLatencyArm baseline,
        RequestLatencyArm candidate)
    {
        var baselineDimensions = baseline.CompatibilityIdentity.Dimensions
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        var candidateDimensions = candidate.CompatibilityIdentity.Dimensions
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        baselineDimensions.TryGetValue("comparison.axis", out var baselineAxis);
        candidateDimensions.TryGetValue("comparison.axis", out var candidateAxis);
        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            "target.process-run-id",
        };
        if (string.Equals(baselineAxis, candidateAxis, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(baselineAxis))
        {
            ignored.Add(baselineAxis);
        }

        var mismatches = new List<RequestLatencyIdentityMismatch>();
        if (!string.Equals(baselineAxis, candidateAxis, StringComparison.Ordinal))
        {
            mismatches.Add(new(
                "comparison.axis",
                baselineAxis,
                candidateAxis,
                "comparison_axis_mismatch"));
        }
        foreach (var name in baselineDimensions.Keys.Concat(candidateDimensions.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .Where(name => !ignored.Contains(name))
                     .Order(StringComparer.Ordinal))
        {
            baselineDimensions.TryGetValue(name, out var baselineValue);
            candidateDimensions.TryGetValue(name, out var candidateValue);
            if (!string.Equals(baselineValue, candidateValue, StringComparison.Ordinal))
            {
                mismatches.Add(new(
                    name,
                    baselineValue,
                    candidateValue,
                    baselineValue is null || candidateValue is null
                        ? "required_dimension_missing"
                        : "dimension_value_mismatch"));
            }
        }

        return mismatches;
    }

    private static double MatchedPairsRankBiserial(IReadOnlyList<double> deltas)
    {
        var nonzero = deltas.Where(value => value != 0).Select(Math.Abs).Order().ToArray();
        if (nonzero.Length == 0)
        {
            return 0;
        }

        var rankByMagnitude = new Dictionary<double, double>();
        var index = 0;
        while (index < nonzero.Length)
        {
            var end = index + 1;
            while (end < nonzero.Length && nonzero[end] == nonzero[index])
            {
                end++;
            }

            rankByMagnitude[nonzero[index]] = ((index + 1) + end) / 2d;
            index = end;
        }

        var positive = deltas.Where(value => value > 0).Sum(value => rankByMagnitude[Math.Abs(value)]);
        var negative = deltas.Where(value => value < 0).Sum(value => rankByMagnitude[Math.Abs(value)]);
        var total = positive + negative;
        return total == 0 ? 0 : (positive - negative) / total;
    }

    private static string UnavailableReason(
        ScenarioStatisticalAvailability availability,
        int incomplete,
        int unpairedWindows,
        bool pairOrderMismatch,
        bool belowWindowMinimum,
        int completePairs)
        => availability switch
        {
            ScenarioStatisticalAvailability.Incompatible when pairOrderMismatch =>
                "pair_order_mismatch",
            ScenarioStatisticalAvailability.Incompatible => "compatibility_identity_mismatch",
            ScenarioStatisticalAvailability.LossDetected => "source_loss_detected",
            ScenarioStatisticalAvailability.UnsupportedApproximation =>
                "reservoir_approximation_not_admitted_for_interval_or_effect_size",
            ScenarioStatisticalAvailability.InsufficientObservations when incomplete > 0 =>
                "incomplete_request_population",
            ScenarioStatisticalAvailability.InsufficientObservations when unpairedWindows > 0 =>
                "incomplete_window_pairs",
            ScenarioStatisticalAvailability.InsufficientObservations when belowWindowMinimum =>
                "minimum_completed_requests_per_window_not_met",
            ScenarioStatisticalAvailability.InsufficientObservations
                when completePairs < RequestLatencyEvidenceBounds.MinimumCompleteWindowPairs =>
                "minimum_complete_window_pairs_not_met",
            _ => "evidence_unavailable",
        };

    private static double QuantileR7(double[] sortedValues, double probability)
    {
        if (sortedValues.Length == 1)
        {
            return sortedValues[0];
        }

        var position = (sortedValues.Length - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var fraction = position - lower;
        return sortedValues[lower] + fraction * (sortedValues[upper] - sortedValues[lower]);
    }

    private static void Validate(RequestLatencyReplayFixture fixture)
    {
        if (fixture.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported request-latency fixture schema version {fixture.SchemaVersion}.");
        }
        if (fixture.Cases.Count is < 1 or > RequestLatencyEvidenceBounds.MaximumCases)
        {
            throw new InvalidDataException("Request-latency fixture case count exceeds its fixed bound.");
        }
        var duplicateIds = fixture.Cases.GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            throw new InvalidDataException(
                $"Request-latency fixture contains duplicate case identities: {string.Join(", ", duplicateIds)}.");
        }

        foreach (var evidenceCase in fixture.Cases)
        {
            ValidateArm(evidenceCase.Baseline);
            ValidateArm(evidenceCase.Candidate);
        }
    }

    private static void ValidateArm(RequestLatencyArm arm)
    {
        if (arm.CompatibilityIdentity.Dimensions.Count
            > ScenarioStatisticalEvidenceBounds.MaximumIdentityDimensions)
        {
            throw new InvalidDataException(
                "Request-latency identity dimension count exceeds its fixed bound.");
        }
        if (arm.CompatibilityIdentity.Dimensions.Any(item =>
                string.IsNullOrWhiteSpace(item.Name)
                || string.IsNullOrWhiteSpace(item.Value))
            || arm.CompatibilityIdentity.Dimensions
                .GroupBy(item => item.Name, StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
        {
            throw new InvalidDataException(
                "Request-latency identity dimensions must be non-empty and unique.");
        }
        var identityNames = arm.CompatibilityIdentity.Dimensions
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var missingDimensions = ScenarioStatisticalEvidenceHarness.RequiredIdentityDimensions
            .Where(name => !identityNames.Contains(name))
            .ToArray();
        if (missingDimensions.Length > 0)
        {
            throw new InvalidDataException(
                $"Request-latency identity is missing required dimensions: {string.Join(", ", missingDimensions)}.");
        }
        if (arm.Windows.Count is < 1 or > RequestLatencyEvidenceBounds.MaximumWindowsPerArm)
        {
            throw new InvalidDataException("Request-latency window count exceeds its fixed bound.");
        }
        if (arm.Windows.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count()
                != arm.Windows.Count
            || arm.Windows.Select(item => item.PairKey).Distinct(StringComparer.Ordinal).Count()
                != arm.Windows.Count)
        {
            throw new InvalidDataException(
                "Request-latency window and pair identities must be unique within an arm.");
        }
        foreach (var window in arm.Windows)
        {
            if (window.RetainedLatenciesMilliseconds.Count
                > RequestLatencyEvidenceBounds.MaximumRetainedSamplesPerWindow)
            {
                throw new InvalidDataException(
                    "Request-latency retained sample count exceeds its fixed bound.");
            }
            if (window.SourceCompletedCount < window.RetainedLatenciesMilliseconds.Count
                || window.ErrorCount < 0
                || window.CensoredCount < 0
                || window.MissingCount < 0
                || window.LostOrDropped < 0
                || window.RetainedLatenciesMilliseconds.Any(value =>
                    !double.IsFinite(value) || value < 0))
            {
                throw new InvalidDataException("Request-latency window contains invalid counts or values.");
            }
            if (window.Representation == ScenarioStatisticalRepresentation.Exact
                && window.SourceCompletedCount
                    != window.RetainedLatenciesMilliseconds.Count)
            {
                throw new InvalidDataException(
                    "Exact request-latency windows must retain every completed request.");
            }
        }
    }

    private sealed record PairingResult(
        IReadOnlyList<(RequestLatencyWindow Baseline, RequestLatencyWindow Candidate)> Pairs,
        bool PairOrderMismatch,
        int UnpairedWindows);
}

public sealed class DeterministicLatencyReservoir
{
    private readonly int _capacity;
    private readonly string _seed;
    private readonly List<Entry> _entries;

    public DeterministicLatencyReservoir(int capacity, string seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        _capacity = capacity;
        _seed = seed;
        _entries = new(capacity);
    }

    public void Add(string id, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var priority = SHA256.HashData(Encoding.UTF8.GetBytes($"{_seed}\n{id}"));
        var entry = new Entry(id, value, priority);
        if (_entries.Count < _capacity)
        {
            _entries.Add(entry);
            return;
        }

        var largest = _entries
            .Select((item, index) => (item, index))
            .MaxBy(item => item.item, EntryComparer.Instance);
        if (EntryComparer.Instance.Compare(entry, largest.item) < 0)
        {
            _entries[largest.index] = entry;
        }
    }

    public IReadOnlyList<double> Snapshot()
        => _entries.Order(EntryComparer.Instance).Select(item => item.Value).ToArray();

    private sealed record Entry(string Id, double Value, byte[] Priority);

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static EntryComparer Instance { get; } = new();

        public int Compare(Entry? left, Entry? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left is null)
            {
                return -1;
            }
            if (right is null)
            {
                return 1;
            }

            var priority = left.Priority.AsSpan().SequenceCompareTo(right.Priority);
            return priority != 0
                ? priority
                : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RequestLatencyReplayFixture))]
[JsonSerializable(typeof(RequestLatencyEvidenceReport))]
internal sealed partial class RequestLatencyEvidenceJsonContext : JsonSerializerContext;
