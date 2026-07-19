using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DotnetDiagnostics.BenchmarkDotNet.Regression;

/// <summary>Aggregates exact-compatible paired-ref cohorts without pooling environments or runner classes.</summary>
public static class PerfCalibrationAnalyzer
{
    public static (PerfCalibrationEvidencePackage Evidence, PerfCalibrationReport Report) Analyze(
        IReadOnlyList<PerfCalibrationCohortEvidence> cohorts,
        PerfCalibrationPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(cohorts);
        policy ??= new PerfCalibrationPolicy();
        ValidatePolicy(policy);
        ValidateCohorts(cohorts, policy.EffectivePairedPolicy);

        var ordered = cohorts
            .OrderBy(static cohort => cohort.Manifest.CreatedAt)
            .ThenBy(static cohort => cohort.CohortId, StringComparer.Ordinal)
            .ToArray();
        var evidence = new PerfCalibrationEvidencePackage(
            PerfCalibrationEvidencePackage.SchemaV1,
            DateTimeOffset.UtcNow,
            ordered);
        var groups = ordered
            .GroupBy(CompatibilityKey)
            .Select(group => AnalyzeGroup(group.ToArray(), policy))
            .OrderBy(static group => group.RunnerKind)
            .ThenByDescending(static group => group.CohortIds.Count)
            .ThenBy(static group => group.GroupId, StringComparer.Ordinal)
            .ToArray();

        var hostedCohorts = ordered.Where(static cohort =>
            cohort.RunnerKind == PerfCalibrationRunnerKind.GitHubHosted).ToArray();
        var dedicatedCohorts = ordered.Where(static cohort =>
            cohort.RunnerKind == PerfCalibrationRunnerKind.Dedicated).ToArray();
        var hostedMeetsTargets = groups.Any(static group =>
            group.RunnerKind == PerfCalibrationRunnerKind.GitHubHosted && group.MeetsCalibrationTargets);
        var dedicatedMeetsTargets = groups.Any(static group =>
            group.RunnerKind == PerfCalibrationRunnerKind.Dedicated && group.MeetsCalibrationTargets);
        var supportsTimingGateConsideration = hostedMeetsTargets && dedicatedMeetsTargets;
        var decision = hostedCohorts.Length > 1 && groups.Any(group =>
            group.RunnerKind == PerfCalibrationRunnerKind.GitHubHosted
            && group.IndependentAllocationCount > 1
            && RatesMeetTargets(group, policy))
                ? PerfExperimentDecision.PartialGo
                : PerfExperimentDecision.NoGo;
        var notes = BuildNotes(groups, hostedCohorts, dedicatedCohorts, supportsTimingGateConsideration);
        var report = new PerfCalibrationReport(
            PerfCalibrationReport.SchemaV1,
            DateTimeOffset.UtcNow,
            policy,
            groups,
            hostedCohorts.Length,
            DistinctDays(hostedCohorts),
            dedicatedCohorts.Length,
            DistinctDays(dedicatedCohorts),
            hostedMeetsTargets,
            dedicatedMeetsTargets,
            supportsTimingGateConsideration,
            EligibleForGate: false,
            PerfGateRecommendation.Advisory,
            decision,
            notes);
        return (evidence, report);
    }

    private static PerfCalibrationCompatibilityGroupReport AnalyzeGroup(
        PerfCalibrationCohortEvidence[] cohorts,
        PerfCalibrationPolicy policy)
    {
        var first = cohorts[0];
        var pairedReports = cohorts.Select(cohort =>
            PerfPairedComparisonAnalyzer.Analyze(
                cohort.Pairs,
                cohort.Manifest.Feasibility,
                policy: policy.EffectivePairedPolicy).Report).ToArray();
        var detectionRates = AggregateRates(pairedReports, detection: true);
        var falsePositiveRates = AggregateRates(pairedReports, detection: false);
        var variance = BuildVariance(cohorts, pairedReports);
        var failures = TargetFailures(cohorts, detectionRates, falsePositiveRates, variance, policy);
        var compatibilityKey = CompatibilityKey(first);
        return new PerfCalibrationCompatibilityGroupReport(
            GroupId(compatibilityKey),
            first.RunnerKind,
            first.RunnerLabel,
            first.SelectedSdkVersion,
            first.Manifest.Environment,
            first.Manifest.MainBuild,
            first.Manifest.PullRequestBuild,
            cohorts.Select(static cohort => cohort.CohortId).Order(StringComparer.Ordinal).ToArray(),
            cohorts.Select(static cohort => cohort.AllocationId).Distinct(StringComparer.Ordinal).Count(),
            DistinctDays(cohorts),
            pairedReports[0].Workloads,
            detectionRates,
            falsePositiveRates,
            variance,
            Math.Round(cohorts.Sum(static cohort => cohort.Manifest.Feasibility.TotalRunnerMinutes), 4),
            cohorts.Sum(static cohort => cohort.Manifest.Feasibility.CompactArtifactBytes),
            cohorts.Sum(static cohort => cohort.Manifest.Feasibility.RawArtifactBytes),
            failures.Count == 0,
            failures);
    }

    private static PerfCalibrationRateEstimate[] AggregateRates(
        PerfPairedRegressionReport[] reports,
        bool detection)
        => reports
            .SelectMany(static report => report.Calibration)
            .GroupBy(static calibration => calibration.Ref, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var positive = detection
                    ? group.Sum(static calibration => calibration.DetectedRegressionCount)
                    : group.Sum(static calibration => calibration.FalsePositiveCount);
                var total = detection
                    ? group.Sum(static calibration => calibration.InjectedRegressionCount)
                    : group.Sum(static calibration => calibration.UnchangedControlCount);
                var (lower, upper) = Wilson95(positive, total);
                return new PerfCalibrationRateEstimate(
                    group.Key,
                    positive,
                    total,
                    Rate(positive, total),
                    lower,
                    upper);
            })
            .ToArray();

    private static List<PerfCalibrationVarianceSummary> BuildVariance(
        IReadOnlyList<PerfCalibrationCohortEvidence> cohorts,
        PerfPairedRegressionReport[] reports)
    {
        var comparable = reports[0].Workloads
            .Where(static workload => workload.Status == PerfWorkloadSetStatus.Comparable)
            .SelectMany(workload => workload.Variants.Select(variant => (workload.WorkloadId, variant.Variant)))
            .ToArray();
        var rows = new List<PerfCalibrationVarianceSummary>();
        foreach (var (workloadId, variant) in comparable)
        {
            foreach (var refName in new[] { "main", "pull_request" })
            {
                AddVariance(rows, cohorts, workloadId, variant, refName, PerfMetricKind.Time);
                AddVariance(rows, cohorts, workloadId, variant, refName, PerfMetricKind.Allocation);
            }
        }
        return rows;
    }

    private static void AddVariance(
        List<PerfCalibrationVarianceSummary> rows,
        IReadOnlyList<PerfCalibrationCohortEvidence> cohorts,
        string workloadId,
        string variant,
        string refName,
        PerfMetricKind metric)
    {
        var within = new List<double>();
        var medians = new List<double>();
        var days = new Dictionary<DateOnly, List<double>>();
        foreach (var cohort in cohorts)
        {
            var values = cohort.Pairs
                .Select(pair => string.Equals(refName, "main", StringComparison.Ordinal)
                    ? pair.Main
                    : pair.PullRequest)
                .Select(run => run.Observations.Single(observation =>
                    string.Equals(observation.Scenario, workloadId, StringComparison.Ordinal)
                    && string.Equals(observation.Variant, variant, StringComparison.Ordinal)))
                .Select(observation => metric == PerfMetricKind.Time
                    ? observation.MeanNanoseconds
                    : observation.AllocatedBytesPerOperation)
                .ToArray();
            within.Add(CoefficientOfVariation(values));
            var median = Median(values);
            medians.Add(median);
            var day = CaptureDay(cohort);
            if (!days.TryGetValue(day, out var dayValues))
            {
                dayValues = [];
                days.Add(day, dayValues);
            }
            dayValues.Add(median);
        }

        rows.Add(new PerfCalibrationVarianceSummary(
            workloadId,
            variant,
            refName,
            metric,
            metric == PerfMetricKind.Time ? "ns/op" : "B/op",
            cohorts.Count,
            within.Min(),
            Median(within),
            within.Max(),
            CoefficientOfVariation(medians),
            days.Count > 1
                ? CoefficientOfVariation(days.Values.Select(static values => Median(values)).ToArray())
                : null));
    }

    private static List<string> TargetFailures(
        PerfCalibrationCohortEvidence[] cohorts,
        IReadOnlyList<PerfCalibrationRateEstimate> detectionRates,
        IReadOnlyList<PerfCalibrationRateEstimate> falsePositiveRates,
        IReadOnlyList<PerfCalibrationVarianceSummary> variance,
        PerfCalibrationPolicy policy)
    {
        var failures = new List<string>();
        var runnerKind = cohorts[0].RunnerKind;
        var allocationCount = cohorts.Select(static cohort => cohort.AllocationId)
            .Distinct(StringComparer.Ordinal).Count();
        var minimumCohorts = runnerKind == PerfCalibrationRunnerKind.GitHubHosted
            ? policy.MinimumHostedAllocations
            : policy.MinimumDedicatedCohorts;
        var minimumDays = runnerKind == PerfCalibrationRunnerKind.GitHubHosted
            ? policy.MinimumHostedDays
            : policy.MinimumDedicatedDays;
        if (allocationCount < minimumCohorts)
        {
            failures.Add($"Need {minimumCohorts} independent {runnerKind} cohorts; found {allocationCount}.");
        }
        var distinctDays = DistinctDays(cohorts);
        if (distinctDays < minimumDays)
        {
            failures.Add($"Need evidence from {minimumDays} UTC days; found {distinctDays}.");
        }
        foreach (var rate in detectionRates.Where(rate =>
                     rate.RatePercent < policy.MinimumDetectionRatePercent))
        {
            failures.Add(
                $"{rate.Ref} detection rate {rate.RatePercent:F2}% is below "
                + $"{policy.MinimumDetectionRatePercent:F2}%.");
        }
        foreach (var rate in falsePositiveRates.Where(rate =>
                     rate.RatePercent > policy.MaximumFalsePositiveRatePercent))
        {
            failures.Add(
                $"{rate.Ref} false-positive rate {rate.RatePercent:F2}% exceeds "
                + $"{policy.MaximumFalsePositiveRatePercent:F2}%.");
        }
        foreach (var row in variance.Where(row =>
                     row.Metric == PerfMetricKind.Time
                     && row.CrossAllocationCoefficientOfVariationPercent
                         > policy.MaximumCrossAllocationCoefficientOfVariationPercent))
        {
            failures.Add(
                $"{row.Ref}/{row.WorkloadId}/{row.Variant} cross-allocation timing CV "
                + $"{row.CrossAllocationCoefficientOfVariationPercent:F2}% exceeds "
                + $"{policy.MaximumCrossAllocationCoefficientOfVariationPercent:F2}%.");
        }
        return failures;
    }

    private static bool RatesMeetTargets(
        PerfCalibrationCompatibilityGroupReport group,
        PerfCalibrationPolicy policy)
        => group.DetectionRates.Count > 0
            && group.DetectionRates.All(rate => rate.RatePercent >= policy.MinimumDetectionRatePercent)
            && group.FalsePositiveRates.Count > 0
            && group.FalsePositiveRates.All(rate => rate.RatePercent <= policy.MaximumFalsePositiveRatePercent);

    private static List<string> BuildNotes(
        PerfCalibrationCompatibilityGroupReport[] groups,
        PerfCalibrationCohortEvidence[] hosted,
        PerfCalibrationCohortEvidence[] dedicated,
        bool supportsTimingGateConsideration)
    {
        var notes = new List<string>
        {
            "Only clean BenchmarkDotNet measurements contribute detection, false-positive, or variance statistics.",
            "EventPipe attribution remains a later, physically separate launch; diagnostic elapsed time is not present in cohort timing evidence.",
            "Cohorts with different runner kind, SDK, runtime/image provenance, ref builds, or workload contracts form separate groups and are never pooled.",
            "The report is advisory-only. Even sufficient calibration evidence does not enable a timing gate.",
        };
        if (hosted.Length == 0)
        {
            notes.Add("No GitHub-hosted cohort evidence is present.");
        }
        if (dedicated.Length == 0)
        {
            notes.Add(
                "No dedicated/self-hosted runner cohort is present. Dedicated timing evidence remains an explicit blocker.");
        }
        if (groups.Length > 1)
        {
            notes.Add($"{groups.Length} exact-compatibility groups were reported separately.");
        }
        if (!supportsTimingGateConsideration)
        {
            notes.Add("Timing soft and hard gates remain a NO-GO under this evidence set.");
        }
        return notes;
    }

    private static void ValidateCohorts(
        IReadOnlyList<PerfCalibrationCohortEvidence> cohorts,
        PerfPairedRegressionPolicy pairedPolicy)
    {
        if (cohorts.Count == 0)
        {
            throw new ArgumentException("At least one calibration cohort is required.", nameof(cohorts));
        }
        Duplicate(cohorts.Select(static cohort => cohort.CohortId), "cohort ID");
        Duplicate(cohorts.Select(static cohort => cohort.AllocationId), "runner allocation ID");
        foreach (var cohort in cohorts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cohort.CohortId);
            ArgumentException.ThrowIfNullOrWhiteSpace(cohort.AllocationId);
            ArgumentException.ThrowIfNullOrWhiteSpace(cohort.WorkflowRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(cohort.SelectedSdkVersion);
            ArgumentException.ThrowIfNullOrWhiteSpace(cohort.RunnerLabel);
            if (!string.Equals(cohort.Schema, PerfCalibrationCohortEvidence.SchemaV1, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Cohort '{cohort.CohortId}' uses unsupported schema '{cohort.Schema}'.",
                    nameof(cohorts));
            }
            if (cohort.WorkflowRunAttempt <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cohorts),
                    $"Cohort '{cohort.CohortId}' must have a positive workflow run attempt.");
            }
            var (derivedManifest, report) = PerfPairedComparisonAnalyzer.Analyze(
                cohort.Pairs,
                cohort.Manifest.Feasibility,
                policy: pairedPolicy);
            if (!report.Compatibility.Compatible)
            {
                throw new ArgumentException(
                    $"Cohort '{cohort.CohortId}' is internally incompatible: "
                    + string.Join("; ", report.Compatibility.Mismatches),
                    nameof(cohorts));
            }
            ValidateManifest(cohort, derivedManifest);
        }
        var captureIds = cohorts.SelectMany(static cohort =>
            cohort.Pairs.SelectMany(static pair => new[] { pair.Main.RunId, pair.PullRequest.RunId }));
        Duplicate(captureIds, "clean capture ID");
    }

    private static void ValidateManifest(
        PerfCalibrationCohortEvidence cohort,
        PerfPairedExperimentManifest derived)
    {
        var manifest = cohort.Manifest;
        if (!string.Equals(manifest.Schema, PerfPairedExperimentManifest.SchemaV1, StringComparison.Ordinal)
            || manifest.MainBuild != derived.MainBuild
            || manifest.PullRequestBuild != derived.PullRequestBuild
            || manifest.Environment != derived.Environment
            || !manifest.Pairs.SequenceEqual(derived.Pairs))
        {
            throw new ArgumentException(
                $"Cohort '{cohort.CohortId}' manifest does not describe its embedded clean pairs.",
                nameof(cohort));
        }
    }

    private static void Duplicate(IEnumerable<string> values, string label)
    {
        var duplicate = values
            .GroupBy(static value => value, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate {label} '{duplicate.Key}' is not independent evidence.");
        }
    }

    private static string CompatibilityKey(PerfCalibrationCohortEvidence cohort)
    {
        var builder = new StringBuilder();
        builder.Append(cohort.RunnerKind).Append('\n')
            .Append(cohort.RunnerLabel).Append('\n')
            .Append(cohort.SelectedSdkVersion).Append('\n')
            .Append(cohort.Manifest.Environment).Append('\n')
            .Append(cohort.Manifest.MainBuild).Append('\n')
            .Append(cohort.Manifest.PullRequestBuild).Append('\n')
            .Append(WorkloadContract(cohort.Pairs[0].Main)).Append('\n')
            .Append(WorkloadContract(cohort.Pairs[0].PullRequest));
        return builder.ToString();
    }

    private static string WorkloadContract(PerfMeasurementRun run)
    {
        var builder = new StringBuilder();
        builder.Append(run.Workload.Id).Append('|').Append(run.Workload.Version);
        foreach (var parameter in run.Workload.Parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append('|').Append(parameter.Key).Append('=').Append(parameter.Value);
        }
        foreach (var observation in run.Observations
                     .OrderBy(static observation => observation.Scenario, StringComparer.Ordinal)
                     .ThenBy(static observation => observation.Variant, StringComparer.Ordinal))
        {
            builder.Append('|').Append(observation.Scenario)
                .Append(':').Append(observation.Variant)
                .Append(':').Append(observation.IsControl);
        }
        return builder.ToString();
    }

    private static string GroupId(string compatibilityKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(compatibilityKey)))[..12]
            .ToLowerInvariant();

    private static DateOnly CaptureDay(PerfCalibrationCohortEvidence cohort)
        => DateOnly.FromDateTime(cohort.Pairs.Min(static pair => pair.Main.CapturedAt).UtcDateTime);

    private static int DistinctDays(IReadOnlyList<PerfCalibrationCohortEvidence> cohorts)
        => cohorts.Select(CaptureDay).Distinct().Count();

    private static double Rate(int numerator, int denominator)
        => denominator == 0 ? 0 : Math.Round(numerator * 100.0 / denominator, 2);

    private static (double Lower, double Upper) Wilson95(int positive, int total)
    {
        if (total == 0)
        {
            return (0, 100);
        }
        const double z = 1.959963984540054;
        var proportion = positive / (double)total;
        var denominator = 1 + (z * z / total);
        var center = (proportion + (z * z / (2 * total))) / denominator;
        var margin = z * Math.Sqrt(
            ((proportion * (1 - proportion)) / total) + (z * z / (4 * total * total))) / denominator;
        return (
            Math.Round(Math.Max(0, center - margin) * 100, 2),
            Math.Round(Math.Min(1, center + margin) * 100, 2));
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
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

    private static double CoefficientOfVariation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }
        var mean = values.Average();
        if (mean == 0)
        {
            return values.All(static value => value == 0) ? 0 : 100;
        }
        var variance = values.Sum(value => Math.Pow(value - mean, 2)) / (values.Count - 1);
        return Math.Round((Math.Sqrt(variance) / Math.Abs(mean)) * 100, 2);
    }

    private static void ValidatePolicy(PerfCalibrationPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Version);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(policy.MinimumHostedAllocations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(policy.MinimumHostedDays);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(policy.MinimumDedicatedCohorts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(policy.MinimumDedicatedDays);
        ValidatePercent(policy.MinimumDetectionRatePercent, nameof(policy.MinimumDetectionRatePercent));
        ValidatePercent(policy.MaximumFalsePositiveRatePercent, nameof(policy.MaximumFalsePositiveRatePercent));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            policy.MaximumCrossAllocationCoefficientOfVariationPercent);
    }

    private static void ValidatePercent(double value, string name)
    {
        if (value is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(name, value, "Percentage must be between 0 and 100.");
        }
    }
}
