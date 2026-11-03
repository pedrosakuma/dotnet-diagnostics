using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

[JsonConverter(typeof(JsonStringEnumConverter<OperationalMcpPartition>))]
public enum OperationalMcpPartition
{
    Development,
    Heldout,
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationalMcpFamily>))]
public enum OperationalMcpFamily
{
    AllocationReproducibility,
    CpuHotspotCaptureProfiles,
    CpuHotspotMovementComparison,
    ContentionConcentration,
    GcPressureCharacterization,
    RetainedGrowth,
    ThreadPoolBacklog,
    RequestLatencyDistribution,
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationalMcpEligibility>))]
public enum OperationalMcpEligibility
{
    Eligible,
    Excluded,
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationalMcpArm>))]
public enum OperationalMcpArm
{
    NoStatisticalFields,
    CategoricalQuality,
    CategoricalMedianMad,
    CategoricalJaccard,
    CategoricalSpearman,
    CompleteCurrent,
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationalMcpCaseClass>))]
public enum OperationalMcpCaseClass
{
    Positive,
    HealthyControl,
    CompetingExplanation,
    DegradedEvidence,
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationalClaimSupport>))]
public enum OperationalClaimSupport
{
    Supported,
    PartiallySupported,
    Unsupported,
    NotAssessable,
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationalUncertaintyQuality>))]
public enum OperationalUncertaintyQuality
{
    Appropriate,
    UnnecessaryAbstention,
    Overconfident,
    NotAssessable,
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationalPilotConclusion>))]
public enum OperationalPilotConclusion
{
    Go,
    NoGo,
    Inconclusive,
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationalUnsupportedCertaintyCategory>))]
public enum OperationalUnsupportedCertaintyCategory
{
    Dominance,
    Exactness,
    WorkloadEquivalence,
    Causality,
    Severity,
    Regression,
    Health,
    OptimizationPriority,
    MissingAsZero,
}

public sealed record OperationalMcpBudget(
    int MaximumSuiteWallTimeSeconds,
    int MaximumWallTimeSeconds,
    int MaximumAssessmentWallTimeSeconds,
    int MaximumToolCalls,
    int MaximumModelTurns,
    int MaximumCaptureSeconds,
    int MaximumInputTokens,
    int MaximumOutputTokens,
    decimal? MaximumEstimatedCostUsd,
    int MaximumResponseBytes,
    int MaximumArtifactBytes)
{
    public int MaximumPreflightWallTimeSeconds { get; init; } = 60;

    public int MaximumMcpPayloadBytes { get; init; } = MaximumResponseBytes;

    public int MaximumModelTransportOutputBytes { get; init; } = MaximumResponseBytes;

    public int MaximumAssessmentInputBytes { get; init; } = 196_608;

    public int MaximumAssessmentTransportOutputBytes { get; init; } = 131_072;

    public AgentHarnessBudget ToAgentBudget()
        => new(
            MaximumWallTimeSeconds,
            MaximumToolCalls,
            MaximumModelTurns,
            MaximumCaptureSeconds,
            MaximumInputTokens,
            MaximumOutputTokens,
            MaximumEstimatedCostUsd,
            MaximumModelTransportOutputBytes,
            MaximumArtifactBytes);
}

public sealed record OperationalMcpModelFreeze(
    string Provider,
    string DiagnosingModel,
    string AssessorModel,
    string ContextTier,
    string DiagnosingPromptSha256,
    string AssessorPromptSha256,
    string TransportProtocolSha256);

public sealed record OperationalMcpInventoryEntry(
    OperationalMcpFamily Family,
    OperationalMcpEligibility Eligibility,
    string Tool,
    string View,
    string Schema,
    string Reason,
    IReadOnlyDictionary<OperationalMcpArm, string>? ExpectedToolDefinitionFingerprints);

public sealed record OperationalProjectionMutation(
    string JsonPointer,
    string Operation,
    string ExpectedKind,
    JsonElement? CurrentValue,
    IReadOnlyList<OperationalMcpArm> Arms,
    IReadOnlyDictionary<OperationalMcpArm, JsonElement>? Values);

public sealed record OperationalGuardMutation(
    string JsonPointer,
    string ExpectedKind,
    JsonElement CurrentValue,
    JsonElement Value);

public sealed record OperationalMcpGuardPackage(
    string CurrentSummary,
    IReadOnlyList<string> CurrentAssumptions,
    IReadOnlyList<string> CurrentLimitations,
    string Summary,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<OperationalGuardMutation> ToolDefinitionMutations)
{
    public string ExpectedFingerprint { get; init; } = string.Empty;
}

public sealed record OperationalMcpProjection(
    OperationalMcpFamily Family,
    string Schema,
    IReadOnlyDictionary<OperationalMcpArm, string> Versions,
    IReadOnlyDictionary<OperationalMcpArm, string> Sources,
    string CurrentSourceCommit,
    OperationalMcpGuardPackage GuardPackage,
    IReadOnlyList<string> CandidateResponsePointers,
    IReadOnlyList<OperationalProjectionMutation> ResponseMutations)
{
    public IReadOnlyDictionary<OperationalMcpArm, string> ExpectedFingerprints { get; init; } =
        new Dictionary<OperationalMcpArm, string>();

    public string ExpectedMutationFingerprint { get; init; } = string.Empty;
}

public sealed record OperationalMcpDecisionRule(
    double MaterialCostRelativeIncrease,
    int MaterialMcpPayloadByteIncrease,
    int MaterialModelTransportByteIncrease,
    double MaterialWallTimeSecondIncrease,
    int MaterialToolCallIncrease)
{
    public IReadOnlyDictionary<OperationalMcpArm, int> ModelVisibleFieldCounts { get; init; } =
        new Dictionary<OperationalMcpArm, int>();
}

public sealed record OperationalMcpCase(
    string Id,
    OperationalMcpPartition Partition,
    OperationalMcpFamily Family,
    OperationalMcpCaseClass CaseClass,
    string ReportedSymptom,
    string EndpointPath,
    int Workers,
    int WarmupMilliseconds,
    int RequiredCaptures,
    string ExpectedEvidence,
    IReadOnlyList<string> ForbiddenCertainty,
    IReadOnlyList<string> UsefulNextSteps)
{
    public int CaptureDurationSeconds { get; init; } = 2;

    public int CaptureTopN { get; init; } = 10;

    public int QueryTopN { get; init; } = 5;
}

public sealed record OperationalMcpProtocol(
    int SchemaVersion,
    string ProtocolId,
    string ProtocolVersion,
    DateTimeOffset FrozenAtUtc,
    string ArmOrderSeed,
    string FailurePolicy,
    string ExpectedCatalogFingerprint,
    OperationalMcpBudget Budgets,
    OperationalMcpDecisionRule DecisionRule,
    IReadOnlyList<OperationalMcpInventoryEntry> Inventory,
    IReadOnlyList<OperationalMcpProjection> Projections,
    IReadOnlyList<OperationalMcpCase> Cases)
{
    public OperationalMcpModelFreeze? Models { get; init; }

    public string ExpectedProductFingerprint { get; init; } = "unavailable";

    public IReadOnlyDictionary<string, string> AssessorAdditiveProperties { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<OperationalMcpArm> ActiveArms { get; init; } =
        Enum.GetValues<OperationalMcpArm>();
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationalMcpExchangeOrigin>))]
public enum OperationalMcpExchangeOrigin
{
    Predeclared,
    FollowUp,
}

public sealed record OperationalMcpExchange(
    int Sequence,
    string Tool,
    string ArgumentsJson,
    string RawResponseJson,
    string RawResponseSha256,
    int RawResponseBytes,
    string ModelVisibleResponseJson,
    string ModelVisibleResponseSha256,
    int ModelVisibleResponseBytes,
    bool Succeeded,
    bool Projected,
    string? ProjectedSchema,
    string? ErrorCode)
{
    public OperationalMcpExchangeOrigin Origin { get; init; } =
        OperationalMcpExchangeOrigin.FollowUp;
}

public sealed record OperationalClaimAssessment(
    string Claim,
    OperationalClaimSupport Support,
    string Rationale);

public sealed record OperationalUnsupportedCertaintyFinding(
    int ClaimIndex,
    OperationalUnsupportedCertaintyCategory Category,
    string Rationale);

public enum OperationalAssessmentFailureCategory
{
    InvalidJson,
    MissingProperty,
    AdditionalProperty,
    UnexpectedProperty,
    DuplicateProperty,
    AmbiguousProperty,
    InvalidValueKind,
    InvalidValue,
    ContradictoryValue,
    DepthLimit,
    SizeLimit,
    SchemaValidation,
}

public sealed record OperationalAssessmentDiagnostic(
    OperationalAssessmentFailureCategory Category,
    string JsonPath,
    string? PropertyName,
    string? ValueKind);

public sealed record OperationalAssessmentFailureEvidence(
    string ProtocolId,
    string ProtocolFingerprint,
    string AttemptId,
    OperationalAssessmentFailureCategory Category,
    string? JsonPath,
    string? PropertyName,
    string? ValueKind,
    string RawResponseSha256,
    int RawResponseBytes,
    string AssistantContentSha256,
    int AssistantContentBytes,
    string? ReplayJson,
    int RetainedBytes,
    bool Truncated,
    bool Redacted)
{
    public IReadOnlyList<OperationalAssessmentDiagnostic> Diagnostics { get; init; } = [];

    public int DiagnosticCount { get; init; }

    public bool DiagnosticsTruncated { get; init; }

    public int AdditionalPropertyCount { get; init; }

    public int ReplayBytes { get; init; }

    public int DiagnosticBytes { get; init; }
}

public sealed record OperationalAutomatedAssessment(
    string AssessorProvider,
    string AssessorModel,
    string AssessorVersion,
    string PromptSha256,
    AgentHarnessStage Stage,
    AgentModelUsage Usage,
    IReadOnlyList<OperationalClaimAssessment> Claims,
    OperationalUncertaintyQuality Uncertainty,
    string UncertaintyRationale,
    IReadOnlyList<OperationalUnsupportedCertaintyFinding> UnsupportedCertaintyFindings,
    IReadOnlyList<string> UsefulNextSteps,
    IReadOnlyList<string> UnhelpfulNextSteps,
    int UnauthorizedActionAttempts,
    string Summary,
    IReadOnlyList<string> Limitations)
{
    public int TransportOutputBytes { get; init; }

    public string? RawResponseSha256 { get; init; }

    public OperationalAssessmentFailureEvidence? FailureEvidence { get; init; }

    public int RetainedArtifactBytes { get; init; }

    public bool ClaimTextAliasNormalized { get; init; }

    public IReadOnlyList<OperationalAssessmentDiagnostic> IgnoredAnnotations { get; init; } = [];

    public int IgnoredAnnotationCount { get; init; }

    public bool IgnoredAnnotationsTruncated { get; init; }

    public int IgnoredAnnotationBytes { get; init; }

    public int UnsupportedCertaintyCount => UnsupportedCertaintyFindings.Count;
}

public sealed record OperationalMcpPreflightReport(
    AgentHarnessStage Stage,
    string AssessorProvider,
    string AssessorModel,
    string AssessorVersion,
    string PromptSha256,
    int InputBytes,
    int TransportOutputBytes,
    string? RawResponseSha256)
{
    public OperationalAssessmentFailureEvidence? FailureEvidence { get; init; }

    public int RetainedArtifactBytes { get; init; }

    public bool ClaimTextAliasNormalized { get; init; }

    public IReadOnlyList<OperationalAssessmentDiagnostic> IgnoredAnnotations { get; init; } = [];

    public int IgnoredAnnotationCount { get; init; }

    public bool IgnoredAnnotationsTruncated { get; init; }

    public int IgnoredAnnotationBytes { get; init; }
}

public sealed record OperationalMcpRunReport(
    int SchemaVersion,
    string ProtocolFingerprint,
    string InventoryFingerprint,
    string ProjectionFingerprint,
    string ArmProjectionFingerprint,
    string CatalogFingerprint,
    string ToolDefinitionFingerprint,
    string CaseId,
    OperationalMcpPartition Partition,
    OperationalMcpFamily Family,
    OperationalMcpArm Arm,
    int ArmOrder,
    bool RequiredEvidenceSequenceCompleted,
    AgentHarnessReport Investigation,
    IReadOnlyList<OperationalMcpExchange> McpExchanges,
    OperationalAutomatedAssessment Assessment)
{
    public int PredeclaredToolCalls { get; init; }

    public int FollowUpToolCalls { get; init; }

    public int FollowUpAddedEvidenceCount { get; init; }

    public int RetainedArtifactBytes { get; init; }
}

public sealed record OperationalDimensionDelta(
    string Dimension,
    string NoStatisticalFields,
    string CompleteCurrent,
    string Interpretation);

public sealed record OperationalFamilyConclusion(
    OperationalMcpFamily Family,
    OperationalPilotConclusion Conclusion,
    int NoStatisticalFieldsAttempts,
    int CompleteCurrentAttempts,
    IReadOnlyList<OperationalDimensionDelta> Dimensions,
    IReadOnlyList<string> Limitations);

public sealed record OperationalArmDimension(
    string Dimension,
    string Value,
    string Interpretation);

public sealed record OperationalFactorizedArmConclusion(
    OperationalMcpArm Arm,
    OperationalPilotConclusion Conclusion,
    int Attempts,
    int AssessableAttempts,
    IReadOnlyList<OperationalArmDimension> Dimensions,
    IReadOnlyList<string> Limitations);

public sealed record OperationalFactorizedFamilyConclusion(
    OperationalMcpFamily Family,
    OperationalPilotConclusion Conclusion,
    OperationalMcpArm? SelectedArm,
    IReadOnlyList<OperationalFactorizedArmConclusion> Arms,
    IReadOnlyList<string> Limitations)
{
    public IReadOnlyList<OperationalSelectorStep> SelectorSteps { get; init; } = [];
}

public sealed record OperationalSelectorStep(
    string Gate,
    string Outcome,
    string Detail);

public sealed record OperationalMcpPilotReport(
    int SchemaVersion,
    string ProtocolFingerprint,
    string InventoryFingerprint,
    string ProjectionFingerprint,
    IReadOnlyDictionary<OperationalMcpArm, string> ArmProjectionFingerprints,
    string ProductFingerprint,
    string ModelProvider,
    string Model,
    string ModelVersion,
    string Transport,
    string TransportVersion,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<OperationalMcpRunReport> Attempts,
    IReadOnlyList<OperationalFamilyConclusion> FamilyConclusions,
    IReadOnlyList<string> Limitations)
{
    public OperationalMcpPreflightReport? AssessorPreflight { get; init; }

    public string EvaluatorCommit { get; init; } = "unavailable";

    public IReadOnlyList<OperationalFactorizedFamilyConclusion> FactorizedFamilyConclusions
        { get; init; } = [];

    public int RetainedArtifactBytes { get; init; }
}

internal static class OperationalMcpJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static T Read<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"JSON document '{path}' was empty.");

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
