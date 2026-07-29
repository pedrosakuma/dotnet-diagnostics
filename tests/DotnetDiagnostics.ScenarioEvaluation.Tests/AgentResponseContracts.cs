namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed record AgentResponseMappingRequest(
    string ScenarioId,
    string FreeTextResponse,
    string? EvidenceFixturePath = null);

public sealed record AgentResponseInterpretation(
    string ScenarioId,
    string EvidenceFixturePath,
    StructuredInterpretation Interpretation,
    IReadOnlyList<AgentEvidenceCitation> EvidenceCitations,
    AgentResponseUncertainty Uncertainty,
    IReadOnlyList<string> Notes);

public sealed record AgentEvidenceCitation(
    string EvidencePath,
    string Summary,
    IReadOnlyList<string> MatchedTerms,
    IReadOnlyList<string> SupportedEvidenceIds);

public enum AgentResponseUncertaintyDisposition
{
    NoneDetected,
    Hedged,
    Assertive,
    Mixed,
}

public sealed record AgentResponseUncertainty(
    AgentResponseUncertaintyDisposition Disposition,
    IReadOnlyList<string> HedgeMarkers,
    IReadOnlyList<string> AssertiveMarkers);
