using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public enum AdvisoryCandidateSource
{
    Original,
    Reanalysis,
}

public enum AdvisoryCallStatus
{
    NotRun,
    Succeeded,
    TimedOut,
    TransportFailed,
    InvalidResponse,
    InputRejected,
    Skipped,
}

public enum AdvisoryClaimSupport
{
    Supported,
    PartiallySupported,
    Unsupported,
    NotAssessable,
}

public enum AdvisoryCertaintyAssessment
{
    Appropriate,
    Overconfident,
    Underconfident,
    NotAssessable,
}

public enum AdvisoryUsefulness
{
    Useful,
    PartiallyUseful,
    NotUseful,
    NotAssessable,
}

public sealed record AdvisoryLlmModel(
    string Provider,
    string Model,
    string ModelVersion,
    string Transport,
    string TransportVersion);

public sealed record AdvisoryLlmLimits(
    int MaximumCases,
    int MaximumCalls,
    int MaximumCallsPerCase,
    int PerCallTimeoutSeconds,
    int MaximumInferenceSeconds,
    int MaximumOuterOverheadSeconds,
    int MaximumPromptBytes,
    int MaximumResponseBytes,
    int MaximumCaseArtifactBytes,
    int MaximumHypotheses,
    int MaximumObservations,
    int MaximumAlternatives,
    int MaximumCitationsPerItem,
    int MaximumClaimsPerCandidate,
    int MaximumStringCharacters);

public sealed record AdvisoryApprovedFact(
    int SourceEvidenceIndex,
    string SourceJsonPointer,
    string SourceTextSha256,
    string Text);

public sealed record AdvisoryAssessmentSlot(
    string SlotId,
    string PacketPath,
    string PacketFileSha256,
    string PacketFingerprint,
    CalibrationProvenanceKind ExpectedProvenance,
    AdvisoryCandidateSource FirstCandidate,
    IReadOnlyList<AdvisoryApprovedFact> ApprovedFacts);

public sealed record AdvisoryLlmProtocol(
    int SchemaVersion,
    string ProtocolId,
    string ProtocolFingerprint,
    DateTimeOffset FrozenAtUtc,
    AdvisoryLlmModel PhaseAModel,
    AdvisoryLlmModel PhaseBModel,
    AdvisoryLlmLimits Limits,
    IReadOnlyList<AdvisoryAssessmentSlot> Slots);

public sealed record AdvisoryProjectedEvidence(
    string EvidenceId,
    string Collector,
    string Status,
    double? CaptureSeconds,
    string EvidenceBase,
    string ToolCallId,
    JsonElement Limitations,
    JsonElement Evidence);

public sealed record AdvisoryProjection(
    int SchemaVersion,
    string ProtocolId,
    string SlotId,
    string PacketFingerprint,
    string PacketFileSha256,
    string ProjectionSha256,
    IReadOnlyList<AdvisoryProjectedEvidence> Evidence,
    IReadOnlyList<AdvisoryPointerResolution> OriginalPointerResults,
    IReadOnlyList<string> OriginallyInvalidPointers,
    IReadOnlyList<string> PointersExcludedByProjection);

public sealed record AdvisoryPointerResolution(
    string OriginalLocation,
    bool ExistsInOriginalResult,
    bool IncludedInProjection,
    string? ProjectedLocation);

public sealed record AdvisoryObservation(
    string ObservationId,
    string Text,
    IReadOnlyList<string> EvidenceLocations);

public sealed record AdvisoryHypothesis(
    string HypothesisId,
    string Text,
    string Confidence,
    IReadOnlyList<string> EvidenceLocations);

public sealed record AdvisoryAlternative(
    string AlternativeId,
    string Text,
    IReadOnlyList<string> EvidenceLocations);

public sealed record AdvisoryPhaseAResponse(
    IReadOnlyList<AdvisoryObservation> Observations,
    IReadOnlyList<AdvisoryHypothesis> Hypotheses,
    IReadOnlyList<AdvisoryAlternative> Alternatives,
    string Uncertainty,
    bool Abstained,
    string NextDiagnosticQuestion);

public sealed record AdvisoryCandidateClaim(
    string ClaimId,
    string Text,
    IReadOnlyList<string> EvidenceLocations);

public sealed record AdvisoryCandidate(
    string CandidateId,
    IReadOnlyList<AdvisoryCandidateClaim> Claims,
    string Uncertainty,
    string NextStep);

public sealed record AdvisoryClaimJudgment(
    string ClaimId,
    AdvisoryClaimSupport Support,
    AdvisoryCertaintyAssessment Certainty,
    string Rationale);

public sealed record AdvisoryCandidateJudgment(
    string CandidateId,
    IReadOnlyList<AdvisoryClaimJudgment> Claims,
    AdvisoryUsefulness AbstentionAndUncertainty,
    AdvisoryUsefulness NextStepUsefulness,
    string Rationale);

public sealed record AdvisoryPhaseBResponse(
    IReadOnlyList<AdvisoryCandidateJudgment> Candidates,
    IReadOnlyList<string> Disagreements,
    string OverallLimitations);

public sealed record AdvisoryCallRecord(
    AdvisoryCallStatus Status,
    string Detail,
    string Model,
    string ModelVersion,
    string PromptSha256,
    string InputSha256,
    string? ResponseSha256,
    string? RawResponse,
    int? InputTokens,
    int? OutputTokens,
    decimal? EstimatedCostUsd,
    double DurationSeconds);

public sealed record AdvisoryCandidateMapping(
    string CandidateId,
    AdvisoryCandidateSource Source,
    IReadOnlyDictionary<string, string> ClaimIds);

public sealed record AdvisoryCaseResult(
    int SchemaVersion,
    string ProtocolId,
    string ProtocolFingerprint,
    string SlotId,
    string PacketFingerprint,
    string PacketFileSha256,
    string ProjectionSha256,
    AdvisoryCallRecord PhaseA,
    string? SealedPhaseAPath,
    string? SealedPhaseASha256,
    AdvisoryCallRecord PhaseB,
    IReadOnlyList<AdvisoryCandidateMapping> CandidateMapping,
    AdvisoryPhaseAResponse? PhaseAResponse,
    AdvisoryPhaseBResponse? PhaseBResponse,
    IReadOnlyList<AdvisoryPointerResolution> OriginalPointerResults,
    IReadOnlyList<string> OriginallyInvalidPointers,
    IReadOnlyList<string> PointersExcludedByProjection);

public sealed record AdvisoryRunSummary(
    int SchemaVersion,
    string ProtocolId,
    string ProtocolFingerprint,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<AdvisoryCaseResult> Cases,
    bool AbortedForGlobalPrerequisite,
    string? AbortDetail);

public sealed record AdvisoryStructuredInvocation(
    string RawResponse,
    int? InputTokens = null,
    int? OutputTokens = null,
    decimal? EstimatedCostUsd = null);

public interface IAdvisoryStructuredTransport
{
    Task<AdvisoryStructuredInvocation> CompleteAsync(
        AdvisoryLlmModel model,
        string prompt,
        int maximumResponseBytes,
        CancellationToken cancellationToken);
}

public sealed class CopilotCliAdvisoryTransport(CopilotCliAgentTransport transport)
    : IAdvisoryStructuredTransport
{
    public async Task<AdvisoryStructuredInvocation> CompleteAsync(
        AdvisoryLlmModel model,
        string prompt,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        var response = await transport.CompleteStructuredJsonAsync(
            new AgentModelConfiguration(
                model.Provider,
                model.Model,
                new Uri("copilot-cli://local-process"),
                0,
                0,
                model.ModelVersion == "unknown" ? null : model.ModelVersion,
                maximumResponseBytes,
                model.TransportVersion),
            prompt,
            cancellationToken).ConfigureAwait(false);
        return new AdvisoryStructuredInvocation(response);
    }
}
