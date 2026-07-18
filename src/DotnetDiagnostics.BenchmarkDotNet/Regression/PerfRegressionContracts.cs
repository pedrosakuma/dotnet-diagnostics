using System.Text.Json.Serialization;

namespace DotnetDiagnostics.BenchmarkDotNet.Regression;

/// <summary>Outcome of a repeated clean-measurement comparison.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PerfRegressionVerdict>))]
public enum PerfRegressionVerdict
{
    [JsonStringEnumMemberName("regression")]
    Regression,

    [JsonStringEnumMemberName("improvement")]
    Improvement,

    [JsonStringEnumMemberName("inconclusive")]
    Inconclusive,

    [JsonStringEnumMemberName("environment_changed")]
    EnvironmentChanged,
}

/// <summary>The clean BenchmarkDotNet metric being compared.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PerfMetricKind>))]
public enum PerfMetricKind
{
    [JsonStringEnumMemberName("time")]
    Time,

    [JsonStringEnumMemberName("allocation")]
    Allocation,
}

/// <summary>How far a report is safe to roll out as a CI policy.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PerfGateRecommendation>))]
public enum PerfGateRecommendation
{
    [JsonStringEnumMemberName("advisory")]
    Advisory,

    [JsonStringEnumMemberName("soft_gate_candidate")]
    SoftGateCandidate,

    [JsonStringEnumMemberName("hard_gate_candidate")]
    HardGateCandidate,
}

/// <summary>Which direction of a normalized diagnostic signal is preferable.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PerfSignalDirection>))]
public enum PerfSignalDirection
{
    [JsonStringEnumMemberName("lower")]
    Lower,

    [JsonStringEnumMemberName("higher")]
    Higher,

    [JsonStringEnumMemberName("neutral")]
    Neutral,
}

/// <summary>Build identity for one side of a baseline/candidate comparison.</summary>
public sealed record PerfBuildIdentity(
    string Id,
    string? CommitSha = null,
    string? Version = null);

/// <summary>Environment fields that must match before clean measurements are comparable.</summary>
public sealed record PerfEnvironmentProvenance(
    string RuntimeVersion,
    string OperatingSystem,
    string RuntimeIdentifier,
    string Architecture,
    string GcMode,
    string RunnerClass,
    string? RunnerImage = null);

/// <summary>Stable workload identity and parameters shared by every repetition.</summary>
public sealed record PerfWorkloadProvenance(
    string Id,
    string Version,
    IReadOnlyDictionary<string, string> Parameters);

/// <summary>One clean BenchmarkDotNet observation for one logical scenario variant.</summary>
public sealed record PerfBenchmarkObservation(
    string Scenario,
    string Variant,
    bool IsControl,
    double MeanNanoseconds,
    double AllocatedBytesPerOperation,
    double StandardDeviationNanoseconds = 0,
    int MeasurementCount = 0);

/// <summary>
/// One independent clean BenchmarkDotNet launch. A regression decision consumes multiple compatible
/// documents; diagnostic-run timing is intentionally absent.
/// </summary>
public sealed record PerfMeasurementRun(
    string Schema,
    string RunId,
    DateTimeOffset CapturedAt,
    PerfBuildIdentity BaselineBuild,
    PerfBuildIdentity CandidateBuild,
    PerfEnvironmentProvenance Environment,
    PerfWorkloadProvenance Workload,
    IReadOnlyList<PerfBenchmarkObservation> Observations)
{
    public const string SchemaV1 = "dotnet-diagnostics/perf-measurement-run/v1";
    public const string BaselineVariant = "baseline";
    public const string CandidateVariant = "candidate";
}

/// <summary>EventPipe evidence from a separate diagnostic run.</summary>
public sealed record PerfDiagnosticAttribution(
    string Scenario,
    string Kind,
    string Headline,
    string ArtifactPath,
    string ExpectedEvidence,
    bool Matched,
    bool IsError = false,
    IReadOnlyList<PerfDiagnosticSignal>? Signals = null,
    PerfRawArtifactReference? RawArtifact = null);

/// <summary>
/// Compact, normalized signal retained for future comparisons after the raw capture expires.
/// <c>StableId</c> identifies a method, type, site, or resource across builds.
/// </summary>
public sealed record PerfDiagnosticSignal(
    string Name,
    string DisplayName,
    string? StableId,
    double Value,
    string Unit,
    PerfSignalDirection BetterDirection);

/// <summary>Short-lived raw evidence referenced by content hash from a compact diagnostic run.</summary>
public sealed record PerfRawArtifactReference(
    string Path,
    long SizeBytes,
    string ContentSha256,
    int RetentionDays);

/// <summary>Separate EventPipe diagnostic launch used only for attribution.</summary>
public sealed record PerfDiagnosticRun(
    string Schema,
    DateTimeOffset CapturedAt,
    PerfBuildIdentity CandidateBuild,
    PerfEnvironmentProvenance Environment,
    PerfWorkloadProvenance Workload,
    IReadOnlyList<PerfDiagnosticAttribution> Attribution)
{
    public const string SchemaV1 = "dotnet-diagnostics/perf-diagnostic-run/v1";
}

/// <summary>Thresholds and confidence requirements used to analyze independent runs.</summary>
public sealed record PerfRegressionPolicy(
    int MinimumRepetitions = 3,
    int MinimumThresholdAgreement = 2,
    double TimingRegressionThresholdPercent = 10,
    double AllocationRegressionThresholdPercent = 15,
    double MaximumCoefficientOfVariationPercent = 10,
    double MinimumZeroBaselineAllocationIncreaseBytes = 32);

/// <summary>Compatibility decision made before any metric verdict.</summary>
public sealed record PerfCompatibilityResult(
    bool Compatible,
    IReadOnlyList<string> Mismatches);

/// <summary>Repeated-run statistics and verdict for one clean metric.</summary>
public sealed record PerfMetricRegressionResult(
    PerfMetricKind Metric,
    string Unit,
    int Repetitions,
    double BaselineMedian,
    double CandidateMedian,
    double DeltaPercent,
    double BaselineCoefficientOfVariationPercent,
    double CandidateCoefficientOfVariationPercent,
    double ThresholdPercent,
    int RegressionAgreementCount,
    int ImprovementAgreementCount,
    PerfRegressionVerdict Verdict,
    string Rationale);

/// <summary>Measurement and diagnostic-attribution result for one pilot scenario.</summary>
public sealed record PerfScenarioRegressionResult(
    string Scenario,
    bool IsControl,
    PerfMetricRegressionResult Timing,
    PerfMetricRegressionResult Allocation,
    IReadOnlyList<PerfDiagnosticAttribution> Attribution,
    bool AttributionConsistent,
    PerfRegressionVerdict Verdict,
    PerfGateRecommendation Recommendation);

/// <summary>
/// Portable CI regression report. The report stays advisory unless repeated compatible clean
/// measurements and separate diagnostic attribution both support a gate.
/// </summary>
public sealed record PerfRegressionReport(
    string Schema,
    DateTimeOffset CreatedAt,
    PerfRegressionPolicy Policy,
    PerfCompatibilityResult Compatibility,
    IReadOnlyList<PerfScenarioRegressionResult> Scenarios,
    int FalsePositiveCount,
    bool EligibleForGate,
    PerfGateRecommendation Recommendation,
    PerfRegressionVerdict Verdict,
    IReadOnlyList<string> Notes)
{
    public const string SchemaV1 = "dotnet-diagnostics/perf-regression-report/v1";
}
