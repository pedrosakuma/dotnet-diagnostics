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
    Control,
    Indicator,
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
    string? ExpectedToolDefinitionFingerprint);

public sealed record OperationalMcpProjection(
    OperationalMcpFamily Family,
    string Version,
    string Schema,
    string NeutralSummary,
    IReadOnlyList<string> JsonPointers);

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
    IReadOnlyList<OperationalMcpInventoryEntry> Inventory,
    IReadOnlyList<OperationalMcpProjection> Projections,
    IReadOnlyList<OperationalMcpCase> Cases)
{
    public OperationalMcpModelFreeze? Models { get; init; }

    public string ExpectedProductFingerprint { get; init; } = "unavailable";
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
    int UnsupportedCertaintyCount,
    IReadOnlyList<string> UsefulNextSteps,
    IReadOnlyList<string> UnhelpfulNextSteps,
    int UnauthorizedActionAttempts,
    string Summary,
    IReadOnlyList<string> Limitations)
{
    public int TransportOutputBytes { get; init; }

    public string? RawResponseSha256 { get; init; }
}

public sealed record OperationalMcpPreflightReport(
    AgentHarnessStage Stage,
    string AssessorProvider,
    string AssessorModel,
    string AssessorVersion,
    string PromptSha256,
    int InputBytes,
    int TransportOutputBytes,
    string? RawResponseSha256);

public sealed record OperationalMcpRunReport(
    int SchemaVersion,
    string ProtocolFingerprint,
    string InventoryFingerprint,
    string ProjectionFingerprint,
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

    public bool FollowUpAddedEvidence { get; init; }
}

public sealed record OperationalDimensionDelta(
    string Dimension,
    string Control,
    string Indicator,
    string Interpretation);

public sealed record OperationalFamilyConclusion(
    OperationalMcpFamily Family,
    OperationalPilotConclusion Conclusion,
    int ControlAttempts,
    int IndicatorAttempts,
    IReadOnlyList<OperationalDimensionDelta> Dimensions,
    IReadOnlyList<string> Limitations);

public sealed record OperationalMcpPilotReport(
    int SchemaVersion,
    string ProtocolFingerprint,
    string InventoryFingerprint,
    string ProjectionFingerprint,
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
