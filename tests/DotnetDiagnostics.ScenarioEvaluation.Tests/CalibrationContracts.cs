using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public enum CalibrationPartition
{
    Development,
    Heldout,
}

public enum CalibrationProvenanceKind
{
    LiveModel,
    Scripted,
    AuthoredEditedReplay,
}

public enum CalibrationReviewability
{
    Reviewable,
    NotAssessable,
}

public enum SemanticSupportRating
{
    Supported,
    PartiallySupported,
    Unsupported,
    NotAssessable,
}

public enum CertaintyRating
{
    Appropriate,
    Overconfident,
    Unclear,
    NotAssessable,
}

public enum QualityRating
{
    Appropriate,
    PartiallyAppropriate,
    Inappropriate,
    Unclear,
    NotAssessable,
}

public enum UncertaintyAbstentionRating
{
    AppropriateUncertainty,
    CorrectAbstention,
    UnnecessaryAbstention,
    Overconfident,
    Unclear,
    NotAssessable,
}

public enum ApprovalComplianceRating
{
    Compliant,
    Violated,
    Unclear,
    NotAssessable,
}

public enum CalibrationReviewerRole
{
    Primary,
    Independent,
    Adjudicator,
}

public enum CalibrationProtocolSlotKind
{
    Live,
    AuthoredEditedReplay,
}

public enum CalibrationDefinitionVisibility
{
    Public,
    PrivateCommitted,
}

public enum CalibrationEvidenceQualityMarker
{
    HealthyControl,
    CompetingExplanation,
    UnknownThreadPoolReason,
    LossOrTruncation,
    ContradictoryOrInsufficientEvidence,
    CaptureWindowLimited,
}

public sealed record CalibrationCaseDescriptor(
    string ProtocolId,
    string RubricFingerprint,
    string CaseId,
    CalibrationPartition Partition,
    CalibrationProvenanceKind ProvenanceKind,
    string CaptureId,
    string? CaptureHash,
    string Notes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProtocolFingerprint = null);

public sealed record CalibrationModelBaseline(
    string Provider,
    string Model,
    string ModelVersion,
    string ModelVersionEvidence,
    string Transport,
    string TransportVersion);

public sealed record CalibrationProductBaseline(
    string Product,
    string Version,
    string Commit);

public sealed record CalibrationProtocolLimits(
    int ExpectedLiveRuns,
    int ExpectedReplayRuns,
    int MaximumProviderTurns);

public sealed record CalibrationReviewPlan(
    int RequiredDistinctReviewersForIndependentSlots,
    IReadOnlyList<string> IndependentSlotIds,
    bool AdjudicateEveryDisagreement);

public sealed record CalibrationHoldoutPlan(
    string PrivateDefinitionSha256,
    bool KeepLabelsUnavailableDuringDevelopment,
    bool RequireFreshCaptureIds,
    bool RequireDistinctRunIdsAndCaptureHashes);

public sealed record CalibrationProtocolSlot(
    string Id,
    CalibrationPartition Partition,
    CalibrationProtocolSlotKind Kind,
    CalibrationProvenanceKind ProvenanceKind,
    CalibrationDefinitionVisibility DefinitionVisibility,
    string? WorkloadFamily,
    string WorkloadDefinition,
    IReadOnlyDictionary<string, string>? PublicWorkloadParameters,
    int Repetition,
    AgentHarnessBudget? Budget,
    IReadOnlyList<CalibrationEvidenceQualityMarker> EvidenceQualityMarkers,
    bool RequiresIndependentReview);

public sealed record CalibrationProtocol(
    int SchemaVersion,
    string ProtocolId,
    string ProtocolFingerprint,
    string RubricId,
    string RubricFingerprint,
    DateTimeOffset FrozenAtUtc,
    CalibrationModelBaseline Model,
    CalibrationProductBaseline Product,
    CalibrationProtocolLimits Limits,
    CalibrationReviewPlan Review,
    CalibrationHoldoutPlan Holdout,
    IReadOnlyList<CalibrationProtocolSlot> Slots);

public sealed record CalibrationStageSnapshot(
    AgentHarnessStage Prerequisites,
    AgentHarnessStage Activation,
    AgentHarnessStage Collection,
    AgentHarnessStage AgentExecution,
    AgentHarnessStage Assessment,
    AgentHarnessStage Cleanup);

public sealed record CalibrationCitation(
    string Location,
    bool Exists,
    JsonElement? Value,
    string? Error);

public sealed record CalibrationClaim(
    string ClaimId,
    string Text,
    AgentEvidencePosture Posture,
    IReadOnlyList<CalibrationCitation> Citations);

public sealed record CalibrationEvidenceResult(
    string ToolCallId,
    string ToolName,
    bool Succeeded,
    string ContentJson,
    string Sha256,
    int ByteCount,
    bool Truncated,
    string? ErrorCode);

public sealed record CalibrationUsage(
    int? InputTokens,
    int? OutputTokens,
    decimal? EstimatedCostUsd,
    bool Available);

public sealed record CalibrationGenerationProvenance(
    CalibrationProvenanceKind Kind,
    string EvidenceKind,
    string Provider,
    string Model,
    string ModelVersion,
    string ProductCommit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Transport = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TransportVersion = null);

public sealed record CalibrationPacket(
    int SchemaVersion,
    string Fingerprint,
    string SourceReportSha256,
    string SourceRunId,
    string CaseFingerprint,
    CalibrationCaseDescriptor Descriptor,
    CalibrationGenerationProvenance Generation,
    DateTimeOffset CapturedAtUtc,
    CalibrationReviewability Reviewability,
    string ReviewabilityDetail,
    CalibrationStageSnapshot Stages,
    IReadOnlyList<CalibrationClaim> Claims,
    IReadOnlyList<CalibrationEvidenceResult> Evidence,
    string Uncertainty,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> CaptureAndTruncationNotes,
    IReadOnlyList<AgentApprovalEvent> ApprovalEvents,
    CalibrationUsage Usage);

public sealed record CalibrationClaimJudgment(
    string ClaimId,
    SemanticSupportRating? SemanticSupport,
    CertaintyRating? UnsupportedCertainty,
    string? Rationale);

public sealed record CalibrationResponseJudgment(
    SemanticSupportRating? SupportedAttribution,
    UncertaintyAbstentionRating? UncertaintyAndAbstention,
    QualityRating? NextStepUsefulness,
    ApprovalComplianceRating? ApprovalCompliance,
    decimal? ObservedCostUsd,
    QualityRating? CostAssessment,
    string? Rationale);

public sealed record CalibrationReview(
    int SchemaVersion,
    string ReviewId,
    string ReviewerId,
    CalibrationReviewerRole Role,
    DateTimeOffset ReviewedAtUtc,
    string PacketFingerprint,
    string ProtocolId,
    string RubricFingerprint,
    string CaseFingerprint,
    bool Finalized,
    IReadOnlyList<string> AdjudicatesReviewIds,
    IReadOnlyList<CalibrationClaimJudgment> Claims,
    CalibrationResponseJudgment Response,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProtocolFingerprint = null);

public sealed record CalibrationDimensionSummary(
    string Dimension,
    int Eligible,
    int Reviewed,
    int Missing,
    IReadOnlyList<string> Labels);

public sealed record CalibrationDisagreement(
    string Subject,
    IReadOnlyDictionary<string, string> Labels);

public sealed record CalibrationSummary(
    int SchemaVersion,
    string PacketFingerprint,
    string CaseId,
    CalibrationReviewability Reviewability,
    int ImportedReviews,
    int DistinctReviewerCount,
    bool HasIndependentReview,
    bool HasExplicitAdjudication,
    IReadOnlyList<CalibrationDimensionSummary> Dimensions,
    IReadOnlyList<CalibrationDisagreement> UnresolvedDisagreements,
    IReadOnlyList<string> MissingReviews,
    IReadOnlyList<string> Notes);
