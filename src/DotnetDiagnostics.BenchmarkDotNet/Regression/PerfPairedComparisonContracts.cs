using System.Text.Json.Serialization;

namespace DotnetDiagnostics.BenchmarkDotNet.Regression;

/// <summary>Execution order for one same-VM baseline/candidate measurement pair.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PerfPairOrder>))]
public enum PerfPairOrder
{
    [JsonStringEnumMemberName("main_then_pr")]
    MainThenPullRequest,

    [JsonStringEnumMemberName("pr_then_main")]
    PullRequestThenMain,
}

/// <summary>How a workload discovered in two refs participates in comparison.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PerfWorkloadSetStatus>))]
public enum PerfWorkloadSetStatus
{
    [JsonStringEnumMemberName("comparable")]
    Comparable,

    [JsonStringEnumMemberName("new_unbaselined")]
    NewUnbaselined,

    [JsonStringEnumMemberName("removed")]
    Removed,

    [JsonStringEnumMemberName("contract_changed")]
    ContractChanged,
}

/// <summary>Operational phase measured by the paired experiment workflow.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PerfExperimentStageKind>))]
public enum PerfExperimentStageKind
{
    [JsonStringEnumMemberName("checkout")]
    Checkout,

    [JsonStringEnumMemberName("restore_build")]
    RestoreBuild,

    [JsonStringEnumMemberName("clean_pair")]
    CleanPair,

    [JsonStringEnumMemberName("diagnostics")]
    Diagnostics,

    [JsonStringEnumMemberName("report")]
    Report,

    [JsonStringEnumMemberName("upload")]
    Upload,
}

/// <summary>Likely operating cadence for the experiment at its observed cost.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PerfExperimentCadence>))]
public enum PerfExperimentCadence
{
    [JsonStringEnumMemberName("every_pr")]
    EveryPullRequest,

    [JsonStringEnumMemberName("selected_pr")]
    SelectedPullRequest,

    [JsonStringEnumMemberName("nightly")]
    Nightly,

    [JsonStringEnumMemberName("manual")]
    Manual,
}

/// <summary>Policy-derived operational suitability, never a regression gate.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PerfOperationalSuitability>))]
public enum PerfOperationalSuitability
{
    [JsonStringEnumMemberName("suitable")]
    Suitable,

    [JsonStringEnumMemberName("conditional")]
    Conditional,

    [JsonStringEnumMemberName("unsuitable")]
    Unsuitable,
}

/// <summary>Rollout conclusion for the paired experiment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PerfExperimentDecision>))]
public enum PerfExperimentDecision
{
    [JsonStringEnumMemberName("go")]
    Go,

    [JsonStringEnumMemberName("partial_go")]
    PartialGo,

    [JsonStringEnumMemberName("no_go")]
    NoGo,
}

/// <summary>One immutable pair of per-ref clean measurement documents.</summary>
public sealed record PerfPairedMeasurement(
    int PairNumber,
    PerfPairOrder Order,
    PerfMeasurementRun Main,
    PerfMeasurementRun PullRequest);

/// <summary>Measured duration and artifact volume for one experiment phase.</summary>
public sealed record PerfExperimentStageMetric(
    PerfExperimentStageKind Kind,
    string Name,
    double DurationSeconds,
    long ArtifactBytes,
    string? Ref = null,
    int? PairNumber = null);

/// <summary>Policy-neutral operating-cost evidence from one workflow cohort.</summary>
public sealed record PerfExperimentFeasibility(
    string EvidenceScope,
    double TotalRunnerMinutes,
    long CompactArtifactBytes,
    long RawArtifactBytes,
    IReadOnlyList<PerfExperimentStageMetric> Stages);

/// <summary>Policy-neutral provenance for one measured pair.</summary>
public sealed record PerfPairedCaptureReference(
    int PairNumber,
    PerfPairOrder Order,
    string MainRunId,
    DateTimeOffset MainCapturedAt,
    string PullRequestRunId,
    DateTimeOffset PullRequestCapturedAt);

/// <summary>
/// Immutable orchestration manifest. Thresholds and verdicts are deliberately absent so reports can
/// be regenerated under a reviewed policy without rewriting measurement history.
/// </summary>
public sealed record PerfPairedExperimentManifest(
    string Schema,
    DateTimeOffset CreatedAt,
    PerfBuildIdentity MainBuild,
    PerfBuildIdentity PullRequestBuild,
    PerfEnvironmentProvenance Environment,
    IReadOnlyList<PerfPairedCaptureReference> Pairs,
    DateTimeOffset? DiagnosticCapturedAt,
    PerfExperimentFeasibility Feasibility)
{
    public const string SchemaV1 = "dotnet-diagnostics/perf-paired-experiment/v1";
}

/// <summary>Cross-ref metric results for one stable workload variant.</summary>
public sealed record PerfVariantComparisonResult(
    string Variant,
    PerfMetricRegressionResult Timing,
    PerfMetricRegressionResult Allocation,
    PerfRegressionVerdict Verdict);

/// <summary>Workload-set classification and optional cross-ref comparison.</summary>
public sealed record PerfWorkloadComparisonResult(
    string WorkloadId,
    PerfWorkloadSetStatus Status,
    string MainVersion,
    string PullRequestVersion,
    bool IsControl,
    IReadOnlyList<string> MainVariants,
    IReadOnlyList<string> PullRequestVariants,
    IReadOnlyList<PerfVariantComparisonResult> Variants,
    PerfRegressionVerdict Verdict,
    string Rationale);

/// <summary>Detection and unchanged-control behavior of the injected #647 fixture within one ref.</summary>
public sealed record PerfFixtureCalibrationSummary(
    string Ref,
    int InjectedRegressionCount,
    int DetectedRegressionCount,
    double DetectionRatePercent,
    int UnchangedControlCount,
    int FalsePositiveCount,
    double FalsePositiveRatePercent);

/// <summary>Cost-based cadence recommendation under the report policy.</summary>
public sealed record PerfCadenceAssessment(
    PerfExperimentCadence Cadence,
    double BudgetMinutes,
    PerfOperationalSuitability Suitability,
    string Rationale);

/// <summary>Versioned report policy applied to immutable paired evidence.</summary>
public sealed record PerfPairedRegressionPolicy(
    string Version = "issue-651-advisory-v1",
    PerfRegressionPolicy? MetricPolicy = null,
    double EveryPullRequestBudgetMinutes = 10,
    double SelectedPullRequestBudgetMinutes = 15,
    double NightlyBudgetMinutes = 30,
    double ManualBudgetMinutes = 60)
{
    public const string PolicyV1 = "issue-651-advisory-v1";

    public PerfRegressionPolicy EffectiveMetricPolicy => MetricPolicy ?? new PerfRegressionPolicy();
}

/// <summary>Versioned, policy-derived advisory report for one paired-ref cohort.</summary>
public sealed record PerfPairedRegressionReport(
    string Schema,
    DateTimeOffset CreatedAt,
    PerfPairedRegressionPolicy Policy,
    PerfCompatibilityResult Compatibility,
    PerfCompatibilityResult AttributionCompatibility,
    IReadOnlyList<PerfWorkloadComparisonResult> Workloads,
    IReadOnlyList<PerfFixtureCalibrationSummary> Calibration,
    IReadOnlyList<PerfDiagnosticAttribution> Attribution,
    PerfExperimentFeasibility Feasibility,
    IReadOnlyList<PerfCadenceAssessment> Cadence,
    int FalsePositiveCount,
    bool EligibleForGate,
    PerfGateRecommendation Recommendation,
    PerfRegressionVerdict Verdict,
    PerfExperimentDecision Decision,
    IReadOnlyList<string> Notes)
{
    public const string SchemaV1 = "dotnet-diagnostics/perf-paired-regression-report/v1";
}
