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
    public AgentHarnessBudget ToAgentBudget()
        => new(
            MaximumWallTimeSeconds,
            MaximumToolCalls,
            MaximumModelTurns,
            MaximumCaptureSeconds,
            MaximumInputTokens,
            MaximumOutputTokens,
            MaximumEstimatedCostUsd,
            MaximumResponseBytes,
            MaximumArtifactBytes);
}

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
    IReadOnlyList<string> UsefulNextSteps);

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
    IReadOnlyList<OperationalMcpCase> Cases);

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
    string? ErrorCode);

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
    IReadOnlyList<string> Limitations);

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
    OperationalAutomatedAssessment Assessment);

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
    string ProductCommit,
    string ModelProvider,
    string Model,
    string ModelVersion,
    string Transport,
    string TransportVersion,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<OperationalMcpRunReport> Attempts,
    IReadOnlyList<OperationalFamilyConclusion> FamilyConclusions,
    IReadOnlyList<string> Limitations);

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
