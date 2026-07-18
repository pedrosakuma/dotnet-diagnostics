using System.Text.Json.Serialization;

namespace DotnetDiagnostics.BenchmarkDotNet.Regression;

/// <summary>Runner population represented by one calibration cohort.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PerfCalibrationRunnerKind>))]
public enum PerfCalibrationRunnerKind
{
    [JsonStringEnumMemberName("github_hosted")]
    GitHubHosted,

    [JsonStringEnumMemberName("dedicated")]
    Dedicated,
}

/// <summary>
/// Self-contained policy-neutral input from one independent paired-ref runner allocation.
/// Diagnostic captures are deliberately absent because they never contribute timing evidence.
/// </summary>
public sealed record PerfCalibrationCohortEvidence(
    string Schema,
    string CohortId,
    string AllocationId,
    string WorkflowRunId,
    int WorkflowRunAttempt,
    string SelectedSdkVersion,
    PerfCalibrationRunnerKind RunnerKind,
    string RunnerLabel,
    PerfPairedExperimentManifest Manifest,
    IReadOnlyList<PerfPairedMeasurement> Pairs)
{
    public const string SchemaV1 = "dotnet-diagnostics/perf-calibration-cohort/v1";
}

/// <summary>Immutable collection of cohort inputs before calibration policy is applied.</summary>
public sealed record PerfCalibrationEvidencePackage(
    string Schema,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PerfCalibrationCohortEvidence> Cohorts)
{
    public const string SchemaV1 = "dotnet-diagnostics/perf-calibration-evidence/v1";
}

/// <summary>Binomial rate with a Wilson 95% confidence interval.</summary>
public sealed record PerfCalibrationRateEstimate(
    string Ref,
    int PositiveCount,
    int ObservationCount,
    double RatePercent,
    double Lower95Percent,
    double Upper95Percent);

/// <summary>Within-cohort and cross-allocation/day variability for one clean metric.</summary>
public sealed record PerfCalibrationVarianceSummary(
    string WorkloadId,
    string Variant,
    string Ref,
    PerfMetricKind Metric,
    string Unit,
    int CohortCount,
    double MinimumWithinCohortCoefficientOfVariationPercent,
    double MedianWithinCohortCoefficientOfVariationPercent,
    double MaximumWithinCohortCoefficientOfVariationPercent,
    double CrossAllocationCoefficientOfVariationPercent,
    double? CrossDayCoefficientOfVariationPercent);

/// <summary>Exact-compatible calibration group. Cohorts from different groups are never pooled.</summary>
public sealed record PerfCalibrationCompatibilityGroupReport(
    string GroupId,
    PerfCalibrationRunnerKind RunnerKind,
    string RunnerLabel,
    string SelectedSdkVersion,
    PerfEnvironmentProvenance Environment,
    PerfBuildIdentity MainBuild,
    PerfBuildIdentity PullRequestBuild,
    IReadOnlyList<string> CohortIds,
    int IndependentAllocationCount,
    int DistinctDayCount,
    IReadOnlyList<PerfWorkloadComparisonResult> Workloads,
    IReadOnlyList<PerfCalibrationRateEstimate> DetectionRates,
    IReadOnlyList<PerfCalibrationRateEstimate> FalsePositiveRates,
    IReadOnlyList<PerfCalibrationVarianceSummary> Variance,
    double TotalRunnerMinutes,
    long CompactArtifactBytes,
    long RawArtifactBytes,
    bool MeetsCalibrationTargets,
    IReadOnlyList<string> TargetFailures);

/// <summary>Versioned policy applied after immutable cohort evidence is collected.</summary>
public sealed record PerfCalibrationPolicy(
    string Version = "issue-651-calibration-advisory-v1",
    PerfPairedRegressionPolicy? PairedPolicy = null,
    int MinimumHostedAllocations = 3,
    int MinimumHostedDays = 3,
    int MinimumDedicatedCohorts = 3,
    int MinimumDedicatedDays = 3,
    double MinimumDetectionRatePercent = 90,
    double MaximumFalsePositiveRatePercent = 5,
    double MaximumCrossAllocationCoefficientOfVariationPercent = 10)
{
    public const string PolicyV1 = "issue-651-calibration-advisory-v1";

    public PerfPairedRegressionPolicy EffectivePairedPolicy
        => PairedPolicy ?? new PerfPairedRegressionPolicy();
}

/// <summary>Policy-derived calibration conclusion across exact-compatible runner cohorts.</summary>
public sealed record PerfCalibrationReport(
    string Schema,
    DateTimeOffset CreatedAt,
    PerfCalibrationPolicy Policy,
    IReadOnlyList<PerfCalibrationCompatibilityGroupReport> Groups,
    int HostedCohortCount,
    int HostedDistinctDayCount,
    int DedicatedCohortCount,
    int DedicatedDistinctDayCount,
    bool HostedEvidenceMeetsTargets,
    bool DedicatedEvidenceMeetsTargets,
    bool EvidenceSupportsTimingGateConsideration,
    bool EligibleForGate,
    PerfGateRecommendation Recommendation,
    PerfExperimentDecision Decision,
    IReadOnlyList<string> Notes)
{
    public const string SchemaV1 = "dotnet-diagnostics/perf-calibration-report/v1";
}
