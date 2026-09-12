using System.Text.Json.Nodes;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public enum AgentHarnessStageStatus
{
    NotRun,
    Passed,
    Failed,
    Blocked,
}

public enum AgentHarnessFailureKind
{
    None,
    Prerequisite,
    Activation,
    Environment,
    Collection,
    Transport,
    Model,
    Assessment,
    Budget,
    Cancelled,
    Cleanup,
}

public enum AgentEvidencePosture
{
    Observed,
    Inferred,
    Unknown,
}

public sealed record AgentHarnessBudget(
    int MaximumWallTimeSeconds = 45,
    int MaximumToolCalls = 4,
    int MaximumModelTurns = 6,
    int MaximumCaptureSeconds = 12,
    int MaximumInputTokens = 12_000,
    int MaximumOutputTokens = 2_000,
    decimal? MaximumEstimatedCostUsd = 1.00m,
    int MaximumResponseBytes = 131_072,
    int MaximumArtifactBytes = 524_288);

public sealed record AgentModelConfiguration(
    string Provider,
    string Model,
    Uri Endpoint,
    double Temperature,
    int MaximumOutputTokens,
    string? Version = null,
    int MaximumResponseBytes = 131_072);

public sealed record AgentHarnessStage(
    AgentHarnessStageStatus Status,
    AgentHarnessFailureKind FailureKind,
    string Detail,
    double DurationSeconds);

public sealed record AgentToolDefinition(
    string Name,
    string Description,
    JsonObject Parameters);

public sealed record AgentToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

public sealed record AgentModelUsage(
    int? InputTokens,
    int? OutputTokens,
    decimal? EstimatedCostUsd);

public sealed record AgentModelTurn(
    string RawResponse,
    string? AssistantContent,
    IReadOnlyList<AgentToolCall> ToolCalls,
    AgentModelUsage Usage,
    string? ProviderRequestId);

public sealed record AgentToolResult(
    string ToolCallId,
    string ToolName,
    bool Succeeded,
    string ContentJson,
    string Sha256,
    int ByteCount,
    bool Truncated,
    string? ErrorCode);

public sealed record AgentTranscriptTurn(
    int Turn,
    string RawModelResponse,
    string? AssistantContent,
    IReadOnlyList<AgentToolCall> ToolCalls,
    IReadOnlyList<AgentToolResult> ToolResults,
    AgentModelUsage Usage,
    string? ProviderRequestId);

public sealed record AgentClaim(
    string Text,
    AgentEvidencePosture Posture,
    IReadOnlyList<string> EvidenceLocations);

public sealed record AgentDiagnosis(
    IReadOnlyList<AgentClaim> Claims,
    string Uncertainty,
    IReadOnlyList<string> NextSteps);

public sealed record AgentApprovalEvent(
    string ToolCallId,
    string ToolName,
    bool Requested,
    string Decision,
    bool Attempted,
    bool Executed,
    string Detail);

public sealed record AgentHarnessProvenance(
    string Provider,
    string Model,
    string ModelVersion,
    string EndpointOrigin,
    double Temperature,
    int MaximumOutputTokens,
    string PromptSha256,
    string ToolPolicySha256,
    string ProductCommit,
    string ProductVersion,
    string WorkloadId,
    string WorkloadVersion,
    IReadOnlyDictionary<string, string> WorkloadConfiguration,
    string WorkloadSeed,
    string TargetRuntime,
    string OperatingSystem,
    string Architecture,
    string Topology,
    string CapturePolicy,
    string RetentionPolicy,
    string RedactionPolicy);

public sealed record AgentHarnessAssessment(
    AgentHarnessStage Stage,
    IReadOnlyList<string> InvalidEvidenceLocations,
    string Method);

public sealed record AgentHarnessReport(
    int SchemaVersion,
    string RunId,
    string EvidenceKind,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    AgentHarnessBudget Budget,
    AgentHarnessStage Prerequisites,
    AgentHarnessStage Activation,
    AgentHarnessStage Collection,
    AgentHarnessStage AgentExecution,
    AgentHarnessStage Cleanup,
    AgentHarnessAssessment Assessment,
    AgentHarnessProvenance Provenance,
    IReadOnlyList<AgentTranscriptTurn> Transcript,
    IReadOnlyList<AgentApprovalEvent> ApprovalEvents,
    AgentDiagnosis? Diagnosis,
    int TotalToolCalls,
    int? TotalInputTokens,
    int? TotalOutputTokens,
    decimal? EstimatedCostUsd,
    int RetainedArtifactBytes,
    IReadOnlyList<string> Limitations);

public sealed record AgentHarnessRequest(
    ScenarioManifest Manifest,
    AgentModelConfiguration Model,
    AgentHarnessBudget Budget,
    string OutputPath,
    string EvidenceKind);
