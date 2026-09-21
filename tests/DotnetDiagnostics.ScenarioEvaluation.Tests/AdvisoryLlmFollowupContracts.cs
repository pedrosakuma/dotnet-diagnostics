using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public enum AdvisoryFollowupPhaseAMode
{
    RetainedSemanticExtraction,
    FreshEvidenceOnly,
}

public enum AdvisoryFollowupFormatCompliance
{
    Compliant,
    Noncompliant,
    Unavailable,
}

public enum AdvisoryFollowupSourceKind
{
    AssessmentRun,
    ContinuationRun,
}

public sealed record AdvisoryFollowupSource(
    string SourceId,
    AdvisoryFollowupSourceKind Kind,
    string RootPath,
    string SummarySha256);

public sealed record AdvisoryFollowupRetainedBinding(
    string SourceId,
    string SourceCaseRelativePath,
    string SourceCaseResultSha256,
    string SourcePhaseAPromptSha256,
    [property: JsonRequired]
    string? SourcePhaseAResponseSha256,
    [property: JsonRequired]
    string? NormalizedPayloadSha256,
    string ProjectionSha256,
    [property: JsonRequired]
    string? SealSourceId,
    [property: JsonRequired]
    string? SealCaseResultSha256,
    [property: JsonRequired]
    string? SealSha256);

public sealed record AdvisoryFollowupClaimBinding(
    string ClaimId,
    string TextPointer,
    string EvidenceLocationsPointer);

public sealed record AdvisoryFollowupSemanticBindings(
    IReadOnlyList<AdvisoryFollowupClaimBinding> Claims,
    string UncertaintyPointer,
    string AbstainedPointer,
    string NextQuestionPointer);

public sealed record AdvisoryFollowupCasePlan(
    string SlotId,
    AdvisoryFollowupPhaseAMode PhaseAMode,
    AdvisoryCandidateSource PrimaryFirstCandidate,
    AdvisoryFollowupRetainedBinding Source,
    AdvisoryFollowupSemanticBindings SemanticBindings);

public sealed record AdvisoryFollowupOrderControlPlan(
    string ControlId,
    string SlotId);

public sealed record AdvisoryFollowupGlossaryCitation(
    string SourceRevision,
    string Path,
    string LineRange,
    string Subject);

public sealed record AdvisoryFollowupPlan(
    int SchemaVersion,
    string PlanId,
    string PlanFingerprint,
    string ProtocolFingerprint,
    string PreviousSourceSummarySha256,
    string RubricVersion,
    string RubricSha256,
    string MeasurementGlossaryVersion,
    string MeasurementGlossarySha256,
    IReadOnlyList<AdvisoryFollowupGlossaryCitation> MeasurementGlossaryProvenance,
    string PhaseAPromptFingerprint,
    string PhaseBPromptFingerprint,
    AdvisoryLlmModel PhaseAModel,
    AdvisoryLlmModel PhaseBModel,
    AdvisoryLlmLimits Limits,
    int MaximumNewCalls,
    int MaximumNewPhaseACalls,
    int MaximumNewPhaseBCalls,
    IReadOnlyList<AdvisoryFollowupSource> Sources,
    IReadOnlyList<AdvisoryFollowupCasePlan> Cases,
    IReadOnlyList<AdvisoryFollowupOrderControlPlan> OrderControls);

public sealed record AdvisoryFollowupPhaseAItem(
    string Id,
    string Text,
    IReadOnlyList<string> EvidenceLocations);

public sealed record AdvisoryFollowupPhaseAResponse(
    IReadOnlyList<AdvisoryFollowupPhaseAItem> Observations,
    IReadOnlyList<AdvisoryFollowupPhaseAItem> Hypotheses,
    IReadOnlyList<AdvisoryFollowupPhaseAItem> Alternatives,
    string Uncertainty,
    bool Abstained,
    string NextDiagnosticQuestion);

public sealed record AdvisoryFollowupSemanticItem(
    string ControllerId,
    string TextPointer,
    string EvidenceLocationsPointer,
    string Text,
    IReadOnlyList<string> EvidenceLocations);

public sealed record AdvisoryFollowupCitationResolution(
    string ControllerId,
    string Location,
    bool ExistsInProjection);

public sealed record AdvisoryFollowupSemanticView(
    IReadOnlyList<AdvisoryFollowupSemanticItem> Observations,
    IReadOnlyList<AdvisoryFollowupSemanticItem> Claims,
    IReadOnlyList<AdvisoryFollowupSemanticItem> Alternatives,
    string UncertaintyPointer,
    string Uncertainty,
    string AbstainedPointer,
    bool Abstained,
    string NextQuestionPointer,
    string NextQuestion,
    IReadOnlyList<AdvisoryFollowupCitationResolution> CitationResolutions);

public sealed record AdvisoryFollowupFormatResult(
    AdvisoryFollowupFormatCompliance Compliance,
    string Detail,
    AdvisoryCallStatus SourceStatus);

public sealed record AdvisoryFollowupSemanticSeal(
    int SchemaVersion,
    string PlanId,
    string PlanFingerprint,
    string ProtocolFingerprint,
    string SlotId,
    string PacketFingerprint,
    string ProjectionSha256,
    string RawResponseSha256,
    string NormalizedPayloadSha256,
    AdvisoryFollowupPhaseAMode PhaseAMode,
    AdvisoryFollowupFormatResult Format,
    AdvisoryFollowupSemanticView SemanticView,
    AdvisoryCallRecord PhaseA);

public sealed record AdvisoryFollowupComparison(
    string ComparisonId,
    string SlotId,
    bool IsOrderControl,
    string? BaselineComparisonId,
    AdvisoryCandidateSource FirstCandidate,
    string CandidateContentSha256,
    AdvisoryCallRecord PhaseB,
    IReadOnlyList<AdvisoryCandidateMapping> CandidateMapping,
    AdvisoryPhaseBResponse? Response);

public sealed record AdvisoryFollowupMappedClaimComparison(
    AdvisoryCandidateSource Source,
    string SourceClaimId,
    AdvisoryClaimSupport PrimarySupport,
    AdvisoryClaimSupport ControlSupport,
    AdvisoryCertaintyAssessment PrimaryCertainty,
    AdvisoryCertaintyAssessment ControlCertainty);

public sealed record AdvisoryFollowupMappedCandidateComparison(
    AdvisoryCandidateSource Source,
    AdvisoryUsefulness PrimaryUncertainty,
    AdvisoryUsefulness ControlUncertainty,
    AdvisoryUsefulness PrimaryNextStep,
    AdvisoryUsefulness ControlNextStep);

public sealed record AdvisoryFollowupOrderControlResult(
    string ControlId,
    string SlotId,
    string PrimaryComparisonId,
    string ControlComparisonId,
    IReadOnlyList<AdvisoryFollowupMappedClaimComparison> Claims,
    IReadOnlyList<AdvisoryFollowupMappedCandidateComparison> Candidates,
    IReadOnlyList<string> PrimaryDisagreements,
    IReadOnlyList<string> ControlDisagreements);

public sealed record AdvisoryFollowupCaseResult(
    string SlotId,
    AdvisoryFollowupPhaseAMode PhaseAMode,
    bool PhaseAReceivedMeasurementGlossary,
    AdvisoryCallStatus SourcePhaseAStatus,
    string SourcePhaseADetail,
    string? SourcePhaseAResponseSha256,
    AdvisoryCallRecord PhaseA,
    AdvisoryFollowupFormatResult Format,
    string? SemanticSealPath,
    string? SemanticSealSha256,
    AdvisoryFollowupSemanticView? SemanticView,
    AdvisoryFollowupComparison Primary,
    IReadOnlyList<AdvisoryPointerResolution> OriginalPointerResults,
    IReadOnlyList<string> OriginallyInvalidPointers,
    IReadOnlyList<string> PointersExcludedByProjection);

public sealed record AdvisoryFollowupSummary(
    int SchemaVersion,
    string PlanId,
    string PlanFingerprint,
    string ProtocolFingerprint,
    string PreviousSourceSummarySha256,
    string RubricVersion,
    string RubricSha256,
    string MeasurementGlossaryVersion,
    string MeasurementGlossarySha256,
    IReadOnlyList<AdvisoryFollowupGlossaryCitation> MeasurementGlossaryProvenance,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int MaximumNewCalls,
    int ActualNewCalls,
    int ActualNewPhaseACalls,
    int ActualNewPhaseBCalls,
    bool TechnicallyComplete,
    bool AbortedForGlobalPrerequisite,
    string? AbortDetail,
    IReadOnlyList<AdvisoryFollowupCaseResult> Cases,
    IReadOnlyList<AdvisoryFollowupComparison> OrderControls,
    IReadOnlyList<AdvisoryFollowupOrderControlResult> OrderControlResults);
