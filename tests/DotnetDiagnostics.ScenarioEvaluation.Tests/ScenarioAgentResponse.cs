namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

/// <summary>
/// A free-text-shaped stand-in for what an LLM/agent would produce after investigating a
/// scenario, before it is mapped into the controlled-vocabulary <see cref="StructuredInterpretation"/>
/// contract that <see cref="ScenarioEvaluator"/> can score. This models the realistic input shape
/// (natural phrasing, not pre-picked ids) that #681's agent-response mapper prototype is meant to
/// exercise -- it deliberately does not require an actual LLM call or network access.
/// </summary>
/// <param name="CitedEvidenceIds">
/// Evidence invariant ids the response claims to rely on. Passed through unchanged: matching these
/// against real free-text signal descriptions is a separate, larger concern than this prototype.
/// </param>
/// <param name="Hypothesis">Free-text description of the suspected root cause.</param>
/// <param name="Attribution">Free-text description of the responsible method/resource.</param>
/// <param name="NextAction">Free-text description of the recommended follow-up action.</param>
/// <param name="CausalityStatement">
/// Free-text description of how strongly the response believes evidence supports causality
/// (e.g. "this is a correlation between the owner and waiters, not yet proven").
/// </param>
/// <param name="Conclusions">Free-text conclusions the response is willing to assert.</param>
/// <param name="Narrative">
/// The prose explanation an agent would attach to its diagnosis, scanned for hedging versus
/// overclaiming language by <see cref="ScenarioAgentResponseMapper"/>.
/// </param>
public sealed record AgentScenarioResponse(
    IReadOnlyList<string> CitedEvidenceIds,
    string Hypothesis,
    string Attribution,
    string NextAction,
    string CausalityStatement,
    IReadOnlyList<string> Conclusions,
    string Narrative);

/// <summary>
/// Whether a response's narrative acknowledges the limits of its evidence or overclaims certainty,
/// scanned independently of the controlled-vocabulary fields mapped into <see cref="StructuredInterpretation"/>.
/// This is advisory signal only -- it does not feed <see cref="ScenarioEvaluator.ScoreInterpretation"/> and
/// is not a pass/fail gate.
/// </summary>
public sealed record UncertaintyAssessment(
    bool AcknowledgesLimits,
    bool OverclaimsCertainty,
    IReadOnlyList<string> HedgeTermsMatched,
    IReadOnlyList<string> OverclaimTermsMatched);

/// <summary>
/// Result of mapping a free-text <see cref="AgentScenarioResponse"/> into the scoreable
/// <see cref="StructuredInterpretation"/> contract. <see cref="UnmappedFields"/> records any field
/// the mapper could not confidently resolve against the scenario manifest's controlled vocabulary --
/// those fields are left empty (never guessed) in <see cref="Interpretation"/>, so an unmapped field
/// always scores as unsupported rather than silently matching the wrong thing.
/// </summary>
public sealed record MappedAgentInterpretation(
    StructuredInterpretation Interpretation,
    UncertaintyAssessment Uncertainty,
    IReadOnlyDictionary<string, string> UnmappedFields);
