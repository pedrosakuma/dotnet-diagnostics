namespace DotnetDiagnostics.BenchmarkDotNet.Regression;

/// <summary>Classifies workload-set changes and compares compatible same-VM measurement pairs.</summary>
public static class PerfPairedComparisonAnalyzer
{
    public static (PerfPairedExperimentManifest Manifest, PerfPairedRegressionReport Report) Analyze(
        IReadOnlyList<PerfPairedMeasurement> pairs,
        PerfExperimentFeasibility feasibility,
        PerfDiagnosticRun? diagnosticRun = null,
        PerfPairedRegressionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(feasibility);
        policy ??= new PerfPairedRegressionPolicy();
        ValidatePolicy(policy);

        var compatibility = CheckCompatibility(pairs, diagnosticRun);
        var orderedPairs = pairs.OrderBy(static pair => pair.PairNumber).ToArray();
        var mainBuild = orderedPairs.Length == 0
            ? new PerfBuildIdentity("unknown")
            : RefBuild(orderedPairs[0].Main);
        var pullRequestBuild = orderedPairs.Length == 0
            ? new PerfBuildIdentity("unknown")
            : RefBuild(orderedPairs[0].PullRequest);
        var environment = orderedPairs.Length == 0
            ? UnknownEnvironment()
            : orderedPairs[0].Main.Environment;

        var manifest = new PerfPairedExperimentManifest(
            PerfPairedExperimentManifest.SchemaV1,
            DateTimeOffset.UtcNow,
            mainBuild,
            pullRequestBuild,
            environment,
            orderedPairs.Select(static pair => new PerfPairedCaptureReference(
                pair.PairNumber,
                pair.Order,
                pair.Main.RunId,
                pair.Main.CapturedAt,
                pair.PullRequest.RunId,
                pair.PullRequest.CapturedAt)).ToArray(),
            diagnosticRun?.CapturedAt,
            feasibility);

        var workloads = ClassifyAndCompare(orderedPairs, compatibility.Compatible, policy.EffectiveMetricPolicy);
        var calibration = compatibility.Compatible
            ? new[]
            {
                Calibrate("main", orderedPairs.Select(static pair => pair.Main).ToArray()),
                Calibrate(
                    "pull_request",
                    orderedPairs.Select(static pair => pair.PullRequest).ToArray(),
                    diagnosticRun),
            }
            : Array.Empty<PerfFixtureCalibrationSummary>();
        var falsePositiveCount = workloads.Count(static workload =>
            workload.Status == PerfWorkloadSetStatus.Comparable
            && workload.IsControl
            && workload.Verdict == PerfRegressionVerdict.Regression);
        var verdict = compatibility.Compatible
            ? OverallVerdict(workloads)
            : PerfRegressionVerdict.EnvironmentChanged;
        var cadence = AssessCadence(feasibility.TotalRunnerMinutes, policy);
        var decision = compatibility.Compatible && cadence.Any(static row =>
            row.Suitability != PerfOperationalSuitability.Unsuitable)
                ? PerfExperimentDecision.PartialGo
                : PerfExperimentDecision.NoGo;
        var notes = BuildNotes(compatibility, orderedPairs, workloads);

        var report = new PerfPairedRegressionReport(
            PerfPairedRegressionReport.SchemaV1,
            DateTimeOffset.UtcNow,
            policy,
            compatibility,
            workloads,
            calibration,
            diagnosticRun?.Attribution ?? Array.Empty<PerfDiagnosticAttribution>(),
            feasibility,
            cadence,
            falsePositiveCount,
            EligibleForGate: false,
            PerfGateRecommendation.Advisory,
            verdict,
            decision,
            notes);
        return (manifest, report);
    }

    private static PerfCompatibilityResult CheckCompatibility(
        IReadOnlyList<PerfPairedMeasurement> pairs,
        PerfDiagnosticRun? diagnosticRun)
    {
        var mismatches = new List<string>();
        if (pairs.Count < 3)
        {
            mismatches.Add($"At least three clean pairs are required; found {pairs.Count}.");
        }
        if (pairs.Count == 0)
        {
            return new PerfCompatibilityResult(false, mismatches);
        }

        var ordered = pairs.OrderBy(static pair => pair.PairNumber).ToArray();
        if (ordered.Select(static pair => pair.PairNumber).Distinct().Count() != ordered.Length)
        {
            mismatches.Add("Pair numbers must be unique.");
        }
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Order == ordered[index].Order)
            {
                mismatches.Add("Clean pair order must alternate between main-then-PR and PR-then-main.");
                break;
            }
        }

        var captures = ordered.SelectMany(static pair => new[] { pair.Main, pair.PullRequest }).ToArray();
        if (captures.Select(static run => run.RunId).Distinct(StringComparer.Ordinal).Count() != captures.Length)
        {
            mismatches.Add("Every per-ref clean capture must have a unique run ID.");
        }
        if (captures.Select(static run => run.CapturedAt).Distinct().Count() != captures.Length)
        {
            mismatches.Add("Every per-ref clean capture must have a unique timestamp.");
        }

        var expectedEnvironment = ordered[0].Main.Environment;
        var expectedMainBuild = RefBuild(ordered[0].Main);
        var expectedPullRequestBuild = RefBuild(ordered[0].PullRequest);
        var expectedMainContracts = Contracts(ordered[0].Main);
        var expectedPullRequestContracts = Contracts(ordered[0].PullRequest);
        foreach (var pair in ordered)
        {
            ValidateRun(pair.Main, "main", expectedEnvironment, expectedMainBuild, expectedMainContracts, mismatches);
            ValidateRun(
                pair.PullRequest,
                "pull request",
                expectedEnvironment,
                expectedPullRequestBuild,
                expectedPullRequestContracts,
                mismatches);
            if (pair.Main.Environment != pair.PullRequest.Environment)
            {
                mismatches.Add($"Pair {pair.PairNumber} did not run both refs in the same runtime/runner environment.");
            }
        }

        if (diagnosticRun is not null)
        {
            if (!string.Equals(diagnosticRun.Schema, PerfDiagnosticRun.SchemaV1, StringComparison.Ordinal))
            {
                mismatches.Add($"Diagnostic run uses unsupported schema '{diagnosticRun.Schema}'.");
            }
            if (diagnosticRun.Environment != expectedEnvironment)
            {
                mismatches.Add("Diagnostic run has different runtime/runner environment provenance.");
            }
            if (diagnosticRun.CandidateBuild != expectedPullRequestBuild)
            {
                mismatches.Add("Diagnostic run does not identify the measured pull-request build.");
            }
            if (!WorkloadsEqual(diagnosticRun.Workload, ordered[0].PullRequest.Workload))
            {
                mismatches.Add("Diagnostic run has a different pull-request workload identity, version, or parameters.");
            }
        }

        return new PerfCompatibilityResult(
            mismatches.Count == 0,
            mismatches.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void ValidateRun(
        PerfMeasurementRun run,
        string refName,
        PerfEnvironmentProvenance expectedEnvironment,
        PerfBuildIdentity expectedBuild,
        IReadOnlyDictionary<string, WorkloadContract> expectedContracts,
        List<string> mismatches)
    {
        if (!string.Equals(run.Schema, PerfMeasurementRun.SchemaV1, StringComparison.Ordinal))
        {
            mismatches.Add($"{refName} run '{run.RunId}' uses unsupported schema '{run.Schema}'.");
        }
        if (run.BaselineBuild != run.CandidateBuild)
        {
            mismatches.Add($"{refName} run '{run.RunId}' must identify one ref build on both fixture variants.");
        }
        if (run.Environment != expectedEnvironment)
        {
            mismatches.Add($"{refName} run '{run.RunId}' has different runtime/runner environment provenance.");
        }
        if (RefBuild(run) != expectedBuild)
        {
            mismatches.Add($"{refName} run '{run.RunId}' has a different build identity.");
        }
        if (!ContractsEqual(Contracts(run), expectedContracts))
        {
            mismatches.Add($"{refName} workload contracts changed between clean captures.");
        }
    }

    private static IReadOnlyList<PerfWorkloadComparisonResult> ClassifyAndCompare(
        PerfPairedMeasurement[] pairs,
        bool environmentCompatible,
        PerfRegressionPolicy policy)
    {
        if (pairs.Length == 0)
        {
            return Array.Empty<PerfWorkloadComparisonResult>();
        }

        var mainContracts = Contracts(pairs[0].Main);
        var pullRequestContracts = Contracts(pairs[0].PullRequest);
        var workloadIds = mainContracts.Keys
            .Concat(pullRequestContracts.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal);
        var results = new List<PerfWorkloadComparisonResult>();
        foreach (var workloadId in workloadIds)
        {
            mainContracts.TryGetValue(workloadId, out var main);
            pullRequestContracts.TryGetValue(workloadId, out var pullRequest);
            if (main is null)
            {
                results.Add(NonComparable(
                    workloadId,
                    PerfWorkloadSetStatus.NewUnbaselined,
                    string.Empty,
                    pullRequest!.Version,
                    pullRequest,
                    "Workload exists only in the pull request and is new/unbaselined; it cannot gate."));
                continue;
            }
            if (pullRequest is null)
            {
                results.Add(NonComparable(
                    workloadId,
                    PerfWorkloadSetStatus.Removed,
                    main.Version,
                    string.Empty,
                    main,
                    "Workload exists only on main and is reported as removed without a regression verdict."));
                continue;
            }
            if (!ContractEqual(main, pullRequest))
            {
                results.Add(new PerfWorkloadComparisonResult(
                    workloadId,
                    PerfWorkloadSetStatus.ContractChanged,
                    main.Version,
                    pullRequest.Version,
                    main.IsControl && pullRequest.IsControl,
                    main.Variants,
                    pullRequest.Variants,
                    Array.Empty<PerfVariantComparisonResult>(),
                    PerfRegressionVerdict.Inconclusive,
                    "Version, parameters, control designation, or variant contract changed; a reviewed baseline is required."));
                continue;
            }
            if (!environmentCompatible)
            {
                results.Add(new PerfWorkloadComparisonResult(
                    workloadId,
                    PerfWorkloadSetStatus.Comparable,
                    main.Version,
                    pullRequest.Version,
                    main.IsControl,
                    main.Variants,
                    pullRequest.Variants,
                    Array.Empty<PerfVariantComparisonResult>(),
                    PerfRegressionVerdict.EnvironmentChanged,
                    "Workload contracts match, but environment incompatibility prevents metric comparison."));
                continue;
            }

            var variants = main.Variants.Select(variant =>
            {
                var observations = pairs.Select(pair => (
                    Main: Observation(pair.Main, workloadId, variant),
                    PullRequest: Observation(pair.PullRequest, workloadId, variant))).ToArray();
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
                return new PerfVariantComparisonResult(
                    variant,
                    timing,
                    allocation,
                    Combine(timing.Verdict, allocation.Verdict));
            }).ToArray();
            results.Add(new PerfWorkloadComparisonResult(
                workloadId,
                PerfWorkloadSetStatus.Comparable,
                main.Version,
                pullRequest.Version,
                main.IsControl,
                main.Variants,
                pullRequest.Variants,
                variants,
                OverallVariantVerdict(variants),
                "Identity, version, parameters, control designation, and variant contract match."));
        }
        return results;
    }

    private static PerfWorkloadComparisonResult NonComparable(
        string workloadId,
        PerfWorkloadSetStatus status,
        string mainVersion,
        string pullRequestVersion,
        WorkloadContract contract,
        string rationale)
        => new(
            workloadId,
            status,
            mainVersion,
            pullRequestVersion,
            contract.IsControl,
            status == PerfWorkloadSetStatus.Removed ? contract.Variants : Array.Empty<string>(),
            status == PerfWorkloadSetStatus.NewUnbaselined ? contract.Variants : Array.Empty<string>(),
            Array.Empty<PerfVariantComparisonResult>(),
            PerfRegressionVerdict.Inconclusive,
            rationale);

    private static PerfFixtureCalibrationSummary Calibrate(
        string refName,
        PerfMeasurementRun[] runs,
        PerfDiagnosticRun? diagnosticRun = null)
    {
        var report = PerfRegressionAnalyzer.Analyze(runs, diagnosticRun);
        var pilots = report.Scenarios.Where(static scenario => !scenario.IsControl).ToArray();
        var controls = report.Scenarios.Where(static scenario => scenario.IsControl).ToArray();
        var detected = pilots.Count(static scenario => scenario.Verdict == PerfRegressionVerdict.Regression);
        var falsePositives = controls.Count(static scenario => scenario.Verdict == PerfRegressionVerdict.Regression);
        return new PerfFixtureCalibrationSummary(
            refName,
            pilots.Length,
            detected,
            Rate(detected, pilots.Length),
            controls.Length,
            falsePositives,
            Rate(falsePositives, controls.Length));
    }

    private static IReadOnlyList<PerfCadenceAssessment> AssessCadence(
        double totalRunnerMinutes,
        PerfPairedRegressionPolicy policy)
        =>
        [
            Assessment(
                PerfExperimentCadence.EveryPullRequest,
                policy.EveryPullRequestBudgetMinutes,
                totalRunnerMinutes,
                withinBudget: PerfOperationalSuitability.Conditional),
            Assessment(
                PerfExperimentCadence.SelectedPullRequest,
                policy.SelectedPullRequestBudgetMinutes,
                totalRunnerMinutes,
                withinBudget: PerfOperationalSuitability.Conditional),
            Assessment(
                PerfExperimentCadence.Nightly,
                policy.NightlyBudgetMinutes,
                totalRunnerMinutes,
                withinBudget: PerfOperationalSuitability.Suitable),
            Assessment(
                PerfExperimentCadence.Manual,
                policy.ManualBudgetMinutes,
                totalRunnerMinutes,
                withinBudget: PerfOperationalSuitability.Suitable),
        ];

    private static PerfCadenceAssessment Assessment(
        PerfExperimentCadence cadence,
        double budgetMinutes,
        double actualMinutes,
        PerfOperationalSuitability withinBudget)
    {
        var suitable = actualMinutes <= budgetMinutes;
        return new PerfCadenceAssessment(
            cadence,
            budgetMinutes,
            suitable ? withinBudget : PerfOperationalSuitability.Unsuitable,
            suitable
                ? $"Observed {actualMinutes:F2} runner minutes is within the {budgetMinutes:F2}-minute policy budget."
                : $"Observed {actualMinutes:F2} runner minutes exceeds the {budgetMinutes:F2}-minute policy budget.");
    }

    private static List<string> BuildNotes(
        PerfCompatibilityResult compatibility,
        PerfPairedMeasurement[] pairs,
        IReadOnlyList<PerfWorkloadComparisonResult> workloads)
    {
        var notes = new List<string>
        {
            "Only clean per-ref BenchmarkDotNet observations drive cross-ref metric verdicts; diagnostic elapsed time is excluded.",
            "This is one within-VM hosted cohort. Multi-runner/day evidence is still required before interpreting its variance as stable.",
            "No dedicated-runner evidence is present; timing hard gates remain blocked.",
            "The workflow and report are advisory-only and cannot become gate-eligible from one cohort.",
        };
        if (!compatibility.Compatible)
        {
            notes.Add("Environment or capture incompatibility prevented pooled comparison.");
        }
        if (pairs.Length < 3)
        {
            notes.Add("Fewer than three paired measurements were supplied.");
        }
        if (workloads.Any(static workload => workload.Status == PerfWorkloadSetStatus.NewUnbaselined))
        {
            notes.Add("New pull-request workloads are recorded as unbaselined and excluded from regression verdicts.");
        }
        if (workloads.Any(static workload => workload.Status == PerfWorkloadSetStatus.ContractChanged))
        {
            notes.Add("Changed workload contracts require explicit baseline review before comparison.");
        }
        return notes;
    }

    private static Dictionary<string, WorkloadContract> Contracts(PerfMeasurementRun run)
        => run.Observations
            .GroupBy(static observation => observation.Scenario, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => new WorkloadContract(
                    run.Workload.Id,
                    run.Workload.Version,
                    run.Workload.Parameters,
                    group.Select(static observation => observation.Variant)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(static variant => variant, StringComparer.Ordinal)
                        .ToArray(),
                    group.All(static observation => observation.IsControl)),
                StringComparer.Ordinal);

    private static bool ContractsEqual(
        IReadOnlyDictionary<string, WorkloadContract> left,
        IReadOnlyDictionary<string, WorkloadContract> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var contract) && ContractEqual(pair.Value, contract));

    private static bool ContractEqual(WorkloadContract left, WorkloadContract right)
        => string.Equals(left.SuiteId, right.SuiteId, StringComparison.Ordinal)
            && string.Equals(left.Version, right.Version, StringComparison.Ordinal)
            && left.IsControl == right.IsControl
            && left.Variants.SequenceEqual(right.Variants, StringComparer.Ordinal)
            && ParametersEqual(left.Parameters, right.Parameters);

    private static bool WorkloadsEqual(PerfWorkloadProvenance left, PerfWorkloadProvenance right)
        => string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && string.Equals(left.Version, right.Version, StringComparison.Ordinal)
            && ParametersEqual(left.Parameters, right.Parameters);

    private static bool ParametersEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count
            && left.All(pair =>
                right.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static PerfBenchmarkObservation Observation(
        PerfMeasurementRun run,
        string scenario,
        string variant)
        => run.Observations.Single(observation =>
            string.Equals(observation.Scenario, scenario, StringComparison.Ordinal)
            && string.Equals(observation.Variant, variant, StringComparison.Ordinal));

    private static PerfMetricRegressionResult AnalyzeMetric(
        PerfMetricKind metric,
        string unit,
        (PerfBenchmarkObservation Main, PerfBenchmarkObservation PullRequest)[] observations,
        Func<PerfBenchmarkObservation, double> selector,
        double threshold,
        PerfRegressionPolicy policy)
    {
        var main = observations.Select(pair => selector(pair.Main)).ToArray();
        var pullRequest = observations.Select(pair => selector(pair.PullRequest)).ToArray();
        var mainCv = CoefficientOfVariation(main);
        var pullRequestCv = CoefficientOfVariation(pullRequest);
        var regressionCount = observations.Count(pair =>
            IsRegression(metric, selector(pair.Main), selector(pair.PullRequest), threshold, policy));
        var improvementCount = observations.Count(pair =>
            selector(pair.Main) != 0
            && PercentDelta(selector(pair.Main), selector(pair.PullRequest)) <= -threshold);

        PerfRegressionVerdict verdict;
        string rationale;
        if (observations.Length < policy.MinimumRepetitions)
        {
            verdict = PerfRegressionVerdict.Inconclusive;
            rationale = $"Need {policy.MinimumRepetitions} complete pairs; found {observations.Length}.";
        }
        else if (mainCv > policy.MaximumCoefficientOfVariationPercent
            || pullRequestCv > policy.MaximumCoefficientOfVariationPercent)
        {
            verdict = PerfRegressionVerdict.Inconclusive;
            rationale = $"Pair-level coefficient of variation exceeds {policy.MaximumCoefficientOfVariationPercent:F1}%.";
        }
        else if (regressionCount >= policy.MinimumThresholdAgreement)
        {
            verdict = PerfRegressionVerdict.Regression;
            rationale = metric == PerfMetricKind.Allocation && main.All(static value => value == 0)
                ? $"{regressionCount}/{observations.Length} pairs exceeded the "
                    + $"+{policy.MinimumZeroBaselineAllocationIncreaseBytes:F0} B/op zero-baseline threshold."
                : $"{regressionCount}/{observations.Length} pairs exceeded the +{threshold:F1}% threshold.";
        }
        else if (improvementCount >= policy.MinimumThresholdAgreement)
        {
            verdict = PerfRegressionVerdict.Improvement;
            rationale = $"{improvementCount}/{observations.Length} pairs exceeded the -{threshold:F1}% threshold.";
        }
        else
        {
            verdict = PerfRegressionVerdict.Inconclusive;
            rationale = $"Only {regressionCount}/{observations.Length} regression and "
                + $"{improvementCount}/{observations.Length} improvement pairs crossed the threshold.";
        }

        return new PerfMetricRegressionResult(
            metric,
            unit,
            observations.Length,
            Median(main),
            Median(pullRequest),
            PercentDelta(Median(main), Median(pullRequest)),
            mainCv,
            pullRequestCv,
            threshold,
            regressionCount,
            improvementCount,
            verdict,
            rationale);
    }

    private static PerfRegressionVerdict OverallVerdict(IReadOnlyList<PerfWorkloadComparisonResult> workloads)
    {
        var comparable = workloads.Where(static workload =>
            workload.Status == PerfWorkloadSetStatus.Comparable && !workload.IsControl).ToArray();
        if (comparable.Any(static workload => workload.Verdict == PerfRegressionVerdict.Regression))
        {
            return PerfRegressionVerdict.Regression;
        }
        if (comparable.Length > 0
            && comparable.All(static workload => workload.Verdict == PerfRegressionVerdict.Improvement))
        {
            return PerfRegressionVerdict.Improvement;
        }
        return PerfRegressionVerdict.Inconclusive;
    }

    private static PerfRegressionVerdict OverallVariantVerdict(PerfVariantComparisonResult[] variants)
    {
        if (variants.Any(static variant => variant.Verdict == PerfRegressionVerdict.Regression))
        {
            return PerfRegressionVerdict.Regression;
        }
        if (variants.Length > 0
            && variants.All(static variant => variant.Verdict == PerfRegressionVerdict.Improvement))
        {
            return PerfRegressionVerdict.Improvement;
        }
        return PerfRegressionVerdict.Inconclusive;
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

    private static bool IsRegression(
        PerfMetricKind metric,
        double main,
        double pullRequest,
        double percentageThreshold,
        PerfRegressionPolicy policy)
        => main != 0
            ? PercentDelta(main, pullRequest) >= percentageThreshold
            : metric == PerfMetricKind.Allocation
                && pullRequest - main >= policy.MinimumZeroBaselineAllocationIncreaseBytes;

    private static double PercentDelta(double baseline, double candidate)
        => baseline == 0
            ? candidate == 0 ? 0 : 100
            : Math.Round(((candidate - baseline) / Math.Abs(baseline)) * 100, 2);

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

    private static double Rate(int numerator, int denominator)
        => denominator == 0 ? 0 : Math.Round(numerator * 100.0 / denominator, 2);

    private static PerfBuildIdentity RefBuild(PerfMeasurementRun run) => run.CandidateBuild;

    private static PerfEnvironmentProvenance UnknownEnvironment()
        => new("unknown", "unknown", "unknown", "unknown", "unknown", "unknown");

    private static void ValidatePolicy(PerfPairedRegressionPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Version);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(policy.EveryPullRequestBudgetMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(policy.SelectedPullRequestBudgetMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(policy.NightlyBudgetMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(policy.ManualBudgetMinutes);
    }

    private sealed record WorkloadContract(
        string SuiteId,
        string Version,
        IReadOnlyDictionary<string, string> Parameters,
        IReadOnlyList<string> Variants,
        bool IsControl);
}
