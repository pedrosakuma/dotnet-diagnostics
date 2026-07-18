using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public enum ScenarioPlatform
{
    Windows,
    Linux,
}

public enum ScenarioStageStatus
{
    NotRun,
    Passed,
    Failed,
}

public enum ScenarioFailureKind
{
    None,
    Workload,
    Collection,
    Environment,
    Evaluation,
}

public enum EvidenceInvariantKind
{
    SignalPresent,
    SignalBucketMatch,
    CounterComparison,
    StackFrameMatch,
    ThreadOwnerCorrelation,
}

public enum NumericComparison
{
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Equal,
}

public sealed record ScenarioManifest(
    int SchemaVersion,
    string Id,
    string Version,
    IReadOnlyList<ScenarioPlatform> SupportedLivePlatforms,
    string ReportedSymptom,
    string GroundTruth,
    ScenarioWorkload Workload,
    ScenarioBudget Budget,
    IReadOnlyList<EvidenceInvariant> ExpectedEvidence,
    IReadOnlyList<string> MisleadingSignals,
    IReadOnlyList<string> TemptingWrongHypotheses,
    IReadOnlyList<string> AcceptableHypotheses,
    IReadOnlyList<string> AcceptableAttributions,
    IReadOnlyList<string> AcceptableNextActions,
    IReadOnlyList<string> ForbiddenConclusions,
    string RequiredCausalityPosture);

public sealed record ScenarioWorkload(
    string Setup,
    string Activation,
    string Cleanup,
    IReadOnlyDictionary<string, string> Parameters,
    int WarmupMilliseconds,
    int ObservationSeconds);

public sealed record ScenarioBudget(
    int MaximumRuntimeSeconds,
    int MaximumEvidenceItems);

public sealed record EvidenceInvariant(
    string Id,
    EvidenceInvariantKind Kind,
    string? Signal = null,
    string? Metric = null,
    IReadOnlyList<string>? ContainsAny = null,
    NumericComparison? Comparison = null,
    double? Threshold = null,
    int MinimumMatches = 1,
    string? Relation = null,
    string? OwnerWaitReason = null);

public sealed record ScenarioEnvironment(
    string Os,
    string Architecture,
    string FrameworkDescription,
    string RuntimeVersion);

public sealed record ObservedMetric(string Name, double Value, string? Unit = null);

public sealed record ObservedSignal(
    string Signal,
    double Salience,
    IReadOnlyList<ObservedSignalBucket> Buckets,
    string? NextAction = null);

public sealed record ObservedSignalBucket(string Key, double Magnitude, string? Unit = null);

public sealed record ObservedFrame(string DisplayName, int MatchCount);

public sealed record ObservedRelation(
    string Relation,
    string OwnerWaitReason,
    int WaitingThreadCount);

public sealed record ScenarioEvidence(
    int SchemaVersion,
    string ScenarioId,
    string ScenarioVersion,
    int Trial,
    ScenarioEnvironment Environment,
    ScenarioStageResult Activation,
    ScenarioStageResult Collection,
    IReadOnlyList<ObservedMetric> Metrics,
    IReadOnlyList<ObservedSignal> Signals,
    IReadOnlyList<ObservedFrame> Frames,
    IReadOnlyList<ObservedRelation> Relations,
    IReadOnlyList<string> Notes);

public sealed record ScenarioStageResult(
    ScenarioStageStatus Status,
    ScenarioFailureKind FailureKind,
    string? Detail,
    double DurationSeconds);

public sealed record EvidenceInvariantResult(
    string Id,
    bool Passed,
    string Detail);

public sealed record StructuredInterpretation(
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> HypothesisIds,
    IReadOnlyList<string> AttributionIds,
    IReadOnlyList<string> NextActionIds,
    string CausalityPosture,
    IReadOnlyList<string> ConclusionIds);

public sealed record InterpretationDimension(
    string Name,
    double Weight,
    double Score,
    string Detail);

public sealed record InterpretationScore(
    double WeightedScore,
    IReadOnlyList<InterpretationDimension> Dimensions);

public sealed record ScenarioEvaluationReport(
    int SchemaVersion,
    string ScenarioId,
    int Trial,
    ScenarioStageResult Activation,
    ScenarioStageResult Collection,
    ScenarioStageResult Interpretation,
    IReadOnlyList<EvidenceInvariantResult> Evidence,
    InterpretationScore? InterpretationScore);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ScenarioManifest))]
[JsonSerializable(typeof(ScenarioEvidence))]
[JsonSerializable(typeof(StructuredInterpretation))]
[JsonSerializable(typeof(ScenarioEvaluationReport))]
internal sealed partial class ScenarioJsonContext : JsonSerializerContext;
