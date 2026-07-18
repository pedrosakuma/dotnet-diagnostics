namespace DotnetDiagnostics.BenchmarkDotNet.Regression;

/// <summary>Builds a regression report from independent clean runs and separate attribution.</summary>
public static class PerfRegressionAnalyzer
{
    public static PerfRegressionReport Analyze(
        IReadOnlyList<PerfMeasurementRun> runs,
        PerfDiagnosticRun? diagnosticRun = null,
        PerfRegressionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        policy ??= new PerfRegressionPolicy();
        ValidatePolicy(policy);

        var compatibility = CheckCompatibility(runs, diagnosticRun);
        var notes = new List<string>
        {
            "Only clean BenchmarkDotNet observations drive metric verdicts; diagnostic-run timing is excluded.",
        };

        if (!compatibility.Compatible)
        {
            notes.Add("Measurement runs are incompatible; no regression verdict or gate recommendation was produced.");
            return new PerfRegressionReport(
                PerfRegressionReport.SchemaV1,
                DateTimeOffset.UtcNow,
                policy,
                compatibility,
                Array.Empty<PerfScenarioRegressionResult>(),
                FalsePositiveCount: 0,
                EligibleForGate: false,
                PerfGateRecommendation.Advisory,
                PerfRegressionVerdict.EnvironmentChanged,
                notes);
        }

        var attributionRows = diagnosticRun?.Attribution ?? Array.Empty<PerfDiagnosticAttribution>();
        var scenarioNames = runs
            .SelectMany(static run => run.Observations)
            .Select(static observation => observation.Scenario)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static scenario => scenario, StringComparer.Ordinal)
            .ToArray();

        var scenarios = new List<PerfScenarioRegressionResult>(scenarioNames.Length);
        foreach (var scenario in scenarioNames)
        {
            var observations = runs
                .Select(run => ScenarioPair(run, scenario))
                .ToArray();
            var isControl = observations.All(static pair => pair.Baseline?.IsControl == true && pair.Candidate?.IsControl == true);

            var timing = AnalyzeMetric(
                PerfMetricKind.Time,
                "ns/op",
                observations,
                static observation => observation.MeanNanoseconds,
                policy.TimingRegressionThresholdPercent,
                policy);
            var allocation = AnalyzeMetric(
                PerfMetricKind.Allocation,
                "B/op",
                observations,
                static observation => observation.AllocatedBytesPerOperation,
                policy.AllocationRegressionThresholdPercent,
                policy);

            var scenarioAttribution = attributionRows
                .Where(row => string.Equals(row.Scenario, scenario, StringComparison.Ordinal))
                .ToArray();
            var attributionConsistent = isControl || scenarioAttribution.Any(static row => row.Matched && !row.IsError);
            var verdict = Combine(timing.Verdict, allocation.Verdict);
            var scenarioRecommendation = Recommend(verdict, timing, allocation, attributionConsistent, isControl);

            scenarios.Add(new PerfScenarioRegressionResult(
                scenario,
                isControl,
                timing,
                allocation,
                scenarioAttribution,
                attributionConsistent,
                verdict,
                scenarioRecommendation));
        }

        var controls = scenarios.Where(static scenario => scenario.IsControl).ToArray();
        var pilots = scenarios.Where(static scenario => !scenario.IsControl).ToArray();
        var falsePositiveCount = controls.Count(static scenario => scenario.Verdict == PerfRegressionVerdict.Regression);
        var controlsPassed = controls.Length > 0 && controls.All(scenario => ControlPassed(scenario, policy));
        var overallVerdict = OverallVerdict(pilots);
        var recommendation = OverallRecommendation(pilots, falsePositiveCount, controlsPassed);
        var eligibleForGate = overallVerdict == PerfRegressionVerdict.Regression
            && falsePositiveCount == 0
            && controlsPassed
            && pilots.Where(static scenario => scenario.Verdict == PerfRegressionVerdict.Regression)
                .All(static scenario => scenario.AttributionConsistent);

        if (runs.Count < policy.MinimumRepetitions)
        {
            notes.Add($"Only {runs.Count} independent clean runs were supplied; policy requires {policy.MinimumRepetitions}.");
        }
        if (falsePositiveCount > 0)
        {
            notes.Add($"{falsePositiveCount} unchanged control scenario(s) crossed a regression threshold; gating is disabled.");
        }
        if (controls.Length == 0)
        {
            notes.Add("No unchanged control scenario was supplied; gating is disabled.");
        }
        else if (!controlsPassed && falsePositiveCount == 0)
        {
            notes.Add("Unchanged controls were incomplete, noisy, or repeatedly crossed an improvement threshold; gating is disabled.");
        }
        if (pilots.Any(static scenario => scenario.Verdict == PerfRegressionVerdict.Regression && !scenario.AttributionConsistent))
        {
            notes.Add("At least one measured regression lacks consistent separate diagnostic attribution; gating is disabled.");
        }

        return new PerfRegressionReport(
            PerfRegressionReport.SchemaV1,
            DateTimeOffset.UtcNow,
            policy,
            compatibility,
            scenarios,
            falsePositiveCount,
            eligibleForGate,
            recommendation,
            overallVerdict,
            notes);
    }

    private static PerfCompatibilityResult CheckCompatibility(
        IReadOnlyList<PerfMeasurementRun> runs,
        PerfDiagnosticRun? diagnosticRun)
    {
        var mismatches = new List<string>();
        if (runs.Count == 0)
        {
            mismatches.Add("No measurement runs were supplied.");
            return new PerfCompatibilityResult(false, mismatches);
        }

        var expected = runs[0];
        if (runs.Select(static run => run.RunId).Distinct(StringComparer.Ordinal).Count() != runs.Count)
        {
            mismatches.Add("Measurement run IDs must be unique; duplicate captures are not independent repetitions.");
        }
        if (runs.Select(static run => run.CapturedAt).Distinct().Count() != runs.Count)
        {
            mismatches.Add("Measurement capture timestamps must be unique; duplicate captures are not independent repetitions.");
        }
        foreach (var run in runs)
        {
            if (!string.Equals(run.Schema, PerfMeasurementRun.SchemaV1, StringComparison.Ordinal))
            {
                mismatches.Add($"Run '{run.RunId}' uses unsupported schema '{run.Schema}'.");
            }
            if (run.Environment != expected.Environment)
            {
                mismatches.Add($"Run '{run.RunId}' has different runtime/runner environment provenance.");
            }
            if (!WorkloadsEqual(run.Workload, expected.Workload))
            {
                mismatches.Add($"Run '{run.RunId}' has a different workload identity, version, or parameter set.");
            }
            if (run.BaselineBuild != expected.BaselineBuild || run.CandidateBuild != expected.CandidateBuild)
            {
                mismatches.Add($"Run '{run.RunId}' has different baseline/candidate build identity.");
            }
        }

        if (diagnosticRun is not null)
        {
            if (!string.Equals(diagnosticRun.Schema, PerfDiagnosticRun.SchemaV1, StringComparison.Ordinal))
            {
                mismatches.Add($"Diagnostic run uses unsupported schema '{diagnosticRun.Schema}'.");
            }
            if (diagnosticRun.Environment != expected.Environment)
            {
                mismatches.Add("Diagnostic run has different runtime/runner environment provenance.");
            }
            if (!WorkloadsEqual(diagnosticRun.Workload, expected.Workload))
            {
                mismatches.Add("Diagnostic run has a different workload identity, version, or parameter set.");
            }
            if (diagnosticRun.CandidateBuild != expected.CandidateBuild)
            {
                mismatches.Add("Diagnostic run has different candidate build identity.");
            }
        }

        return new PerfCompatibilityResult(
            mismatches.Count == 0,
            mismatches.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool WorkloadsEqual(PerfWorkloadProvenance left, PerfWorkloadProvenance right)
        => string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && string.Equals(left.Version, right.Version, StringComparison.Ordinal)
            && left.Parameters.Count == right.Parameters.Count
            && left.Parameters.All(pair =>
                right.Parameters.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static (PerfBenchmarkObservation? Baseline, PerfBenchmarkObservation? Candidate) ScenarioPair(
        PerfMeasurementRun run,
        string scenario)
    {
        var rows = run.Observations
            .Where(observation => string.Equals(observation.Scenario, scenario, StringComparison.Ordinal))
            .ToArray();
        return (
            rows.SingleOrDefault(observation =>
                string.Equals(observation.Variant, PerfMeasurementRun.BaselineVariant, StringComparison.Ordinal)),
            rows.SingleOrDefault(observation =>
                string.Equals(observation.Variant, PerfMeasurementRun.CandidateVariant, StringComparison.Ordinal)));
    }

    private static PerfMetricRegressionResult AnalyzeMetric(
        PerfMetricKind metric,
        string unit,
        IReadOnlyList<(PerfBenchmarkObservation? Baseline, PerfBenchmarkObservation? Candidate)> observations,
        Func<PerfBenchmarkObservation, double> selector,
        double threshold,
        PerfRegressionPolicy policy)
    {
        var complete = observations
            .Where(static pair => pair.Baseline is not null && pair.Candidate is not null)
            .Select(pair => (Baseline: selector(pair.Baseline!), Candidate: selector(pair.Candidate!)))
            .ToArray();
        var baseline = complete.Select(static pair => pair.Baseline).ToArray();
        var candidate = complete.Select(static pair => pair.Candidate).ToArray();
        var baselineCv = CoefficientOfVariation(baseline);
        var candidateCv = CoefficientOfVariation(candidate);
        var regressionCount = complete.Count(pair =>
            IsRegression(metric, pair.Baseline, pair.Candidate, threshold, policy));
        var improvementCount = complete.Count(pair =>
            pair.Baseline != 0 && PercentDelta(pair.Baseline, pair.Candidate) <= -threshold);

        PerfRegressionVerdict verdict;
        string rationale;
        if (complete.Length < policy.MinimumRepetitions)
        {
            verdict = PerfRegressionVerdict.Inconclusive;
            rationale = $"Need {policy.MinimumRepetitions} complete repetitions; found {complete.Length}.";
        }
        else if (baselineCv > policy.MaximumCoefficientOfVariationPercent
            || candidateCv > policy.MaximumCoefficientOfVariationPercent)
        {
            verdict = PerfRegressionVerdict.Inconclusive;
            rationale = $"Run-level coefficient of variation exceeds {policy.MaximumCoefficientOfVariationPercent:F1}%.";
        }
        else if (regressionCount >= policy.MinimumThresholdAgreement)
        {
            verdict = PerfRegressionVerdict.Regression;
            rationale = metric == PerfMetricKind.Allocation && baseline.All(static value => value == 0)
                ? $"{regressionCount}/{complete.Length} repetitions exceeded the "
                    + $"+{policy.MinimumZeroBaselineAllocationIncreaseBytes:F0} B/op zero-baseline threshold."
                : $"{regressionCount}/{complete.Length} repetitions exceeded the +{threshold:F1}% threshold.";
        }
        else if (improvementCount >= policy.MinimumThresholdAgreement)
        {
            verdict = PerfRegressionVerdict.Improvement;
            rationale = $"{improvementCount}/{complete.Length} repetitions exceeded the -{threshold:F1}% threshold.";
        }
        else
        {
            verdict = PerfRegressionVerdict.Inconclusive;
            rationale = $"Only {regressionCount}/{complete.Length} regression and {improvementCount}/{complete.Length} improvement repetitions crossed the threshold.";
        }

        return new PerfMetricRegressionResult(
            metric,
            unit,
            complete.Length,
            Median(baseline),
            Median(candidate),
            PercentDelta(Median(baseline), Median(candidate)),
            baselineCv,
            candidateCv,
            threshold,
            regressionCount,
            improvementCount,
            verdict,
            rationale);
    }

    private static PerfRegressionVerdict Combine(PerfRegressionVerdict timing, PerfRegressionVerdict allocation)
    {
        if (timing == PerfRegressionVerdict.Regression || allocation == PerfRegressionVerdict.Regression)
        {
            return PerfRegressionVerdict.Regression;
        }
        if (timing == PerfRegressionVerdict.Improvement || allocation == PerfRegressionVerdict.Improvement)
        {
            return PerfRegressionVerdict.Improvement;
        }
        return PerfRegressionVerdict.Inconclusive;
    }

    private static PerfRegressionVerdict OverallVerdict(PerfScenarioRegressionResult[] pilots)
    {
        if (pilots.Any(static scenario => scenario.Verdict == PerfRegressionVerdict.Regression))
        {
            return PerfRegressionVerdict.Regression;
        }
        if (pilots.Any(static scenario => scenario.Verdict == PerfRegressionVerdict.Inconclusive))
        {
            return PerfRegressionVerdict.Inconclusive;
        }
        return pilots.Length > 0
            ? PerfRegressionVerdict.Improvement
            : PerfRegressionVerdict.Inconclusive;
    }

    private static PerfGateRecommendation Recommend(
        PerfRegressionVerdict verdict,
        PerfMetricRegressionResult timing,
        PerfMetricRegressionResult allocation,
        bool attributionConsistent,
        bool isControl)
    {
        if (isControl || verdict != PerfRegressionVerdict.Regression || !attributionConsistent)
        {
            return PerfGateRecommendation.Advisory;
        }
        return allocation.Verdict == PerfRegressionVerdict.Regression
            && timing.Verdict != PerfRegressionVerdict.Regression
                ? PerfGateRecommendation.HardGateCandidate
                : PerfGateRecommendation.SoftGateCandidate;
    }

    private static PerfGateRecommendation OverallRecommendation(
        PerfScenarioRegressionResult[] pilots,
        int falsePositiveCount,
        bool controlsPassed)
    {
        if (falsePositiveCount > 0
            || !controlsPassed
            || pilots.Length == 0
            || pilots.Any(static scenario =>
                scenario.Verdict == PerfRegressionVerdict.Regression
                && scenario.Recommendation == PerfGateRecommendation.Advisory))
        {
            return PerfGateRecommendation.Advisory;
        }
        if (pilots.Any(static scenario => scenario.Recommendation == PerfGateRecommendation.SoftGateCandidate))
        {
            return PerfGateRecommendation.SoftGateCandidate;
        }
        return pilots.Any(static scenario => scenario.Recommendation == PerfGateRecommendation.HardGateCandidate)
            ? PerfGateRecommendation.HardGateCandidate
            : PerfGateRecommendation.Advisory;
    }

    private static bool ControlPassed(
        PerfScenarioRegressionResult scenario,
        PerfRegressionPolicy policy)
        => MetricStable(scenario.Timing, policy) && MetricStable(scenario.Allocation, policy);

    private static bool MetricStable(PerfMetricRegressionResult metric, PerfRegressionPolicy policy)
        => metric.Repetitions >= policy.MinimumRepetitions
            && metric.BaselineCoefficientOfVariationPercent <= policy.MaximumCoefficientOfVariationPercent
            && metric.CandidateCoefficientOfVariationPercent <= policy.MaximumCoefficientOfVariationPercent
            && metric.RegressionAgreementCount < policy.MinimumThresholdAgreement
            && metric.ImprovementAgreementCount < policy.MinimumThresholdAgreement;

    private static bool IsRegression(
        PerfMetricKind metric,
        double baseline,
        double candidate,
        double percentageThreshold,
        PerfRegressionPolicy policy)
    {
        if (baseline != 0)
        {
            return PercentDelta(baseline, candidate) >= percentageThreshold;
        }
        return metric == PerfMetricKind.Allocation
            && candidate - baseline >= policy.MinimumZeroBaselineAllocationIncreaseBytes;
    }

    private static double PercentDelta(double baseline, double candidate)
    {
        if (baseline == 0)
        {
            return candidate == 0 ? 0 : 100;
        }
        return Math.Round(((candidate - baseline) / Math.Abs(baseline)) * 100, 2);
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return Math.Round(
            ordered.Length % 2 == 0
                ? (ordered[middle - 1] + ordered[middle]) / 2
                : ordered[middle],
            4);
    }

    private static double CoefficientOfVariation(double[] values)
    {
        if (values.Length < 2)
        {
            return 0;
        }
        var mean = values.Average();
        if (mean == 0)
        {
            return values.All(static value => value == 0) ? 0 : 100;
        }
        var variance = values.Sum(value => Math.Pow(value - mean, 2)) / (values.Length - 1);
        return Math.Round((Math.Sqrt(variance) / Math.Abs(mean)) * 100, 2);
    }

    private static void ValidatePolicy(PerfRegressionPolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.MinimumRepetitions, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.MinimumThresholdAgreement, 2);
        if (policy.MinimumThresholdAgreement > policy.MinimumRepetitions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "Minimum threshold agreement cannot exceed minimum repetitions.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(policy.TimingRegressionThresholdPercent);
        ArgumentOutOfRangeException.ThrowIfNegative(policy.AllocationRegressionThresholdPercent);
        ArgumentOutOfRangeException.ThrowIfNegative(policy.MaximumCoefficientOfVariationPercent);
        ArgumentOutOfRangeException.ThrowIfNegative(policy.MinimumZeroBaselineAllocationIncreaseBytes);
    }
}
