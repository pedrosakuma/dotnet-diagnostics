using System.Text;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed record AdvisoryComparisonUnusedField(string SourcePointer, string SourceKind);

public sealed record AdvisoryComparisonSubset(
    string PolicyVersion,
    string RawResponseSha256,
    string NormalizedPayloadSha256,
    IReadOnlyList<AdvisoryFollowupSemanticItem> Claims,
    string UncertaintyPointer,
    string Uncertainty,
    string NextQuestionPointer,
    string NextQuestion,
    IReadOnlyList<AdvisoryComparisonUnusedField> UnusedFields,
    IReadOnlyList<AdvisoryFollowupCitationResolution> CitationResolutions,
    IReadOnlyList<AdvisoryCandidate> Candidates,
    IReadOnlyList<AdvisoryCandidateMapping> CandidateMapping,
    string Prompt,
    string PromptSha256);

public static partial class AdvisoryLlmAssessment
{
    public const string ComparisonSubsetPolicyVersion = "advisory-comparison-fields-v1";
    private static readonly string[] ComparisonUnusedProperties =
        ["observations", "alternatives", "abstained"];

    /// <summary>
    /// Selects only fields consumed by Phase B. This does not validate or repair the
    /// source's complete Phase-A schema and never executes a model.
    /// </summary>
    public static AdvisoryComparisonSubset PrepareComparisonSubset(
        AdvisoryFollowupPlan plan,
        AdvisoryProjection projection,
        CalibrationPacket packet,
        string rawResponse,
        AdvisoryCandidateSource firstCandidate)
    {
        if (!Enum.IsDefined(firstCandidate))
        {
            throw new InvalidDataException("Unknown comparison candidate ordering.");
        }
        var normalized = NormalizeJsonResponse(rawResponse, plan.Limits.MaximumResponseBytes);
        RejectDuplicateProperties(Encoding.UTF8.GetBytes(normalized.Payload));
        using var document = JsonDocument.Parse(normalized.Payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The comparison source is not a JSON object.");
        }
        var claims = ExtractSemanticArray(
            root, "hypotheses", "followup-claim", plan.Limits.MaximumHypotheses, plan.Limits);
        var uncertainty = ReadPointerString(root, "/uncertainty", plan.Limits);
        var nextQuestion = ReadPointerString(root, "/nextDiagnosticQuestion", plan.Limits);
        if (packet.Claims.Count > plan.Limits.MaximumClaimsPerCandidate
            || claims.Length > plan.Limits.MaximumClaimsPerCandidate)
        {
            throw new InvalidDataException("Comparison source exceeds the candidate claim bound.");
        }
        var unusedFields = ComparisonUnusedProperties
            .Select(name => new AdvisoryComparisonUnusedField(
                $"/{name}",
                root.TryGetProperty(name, out var value)
                    ? value.ValueKind.ToString()
                    : "Missing"))
            .ToArray();
        var candidates = BuildFollowupCandidates(
            firstCandidate, packet, claims, uncertainty, nextQuestion, projection, out var mapping);
        var prompt = BuildFollowupPhaseBPrompt(plan, projection, candidates);
        return new AdvisoryComparisonSubset(
            ComparisonSubsetPolicyVersion,
            Sha256(Encoding.UTF8.GetBytes(rawResponse)),
            normalized.PayloadSha256,
            claims,
            "/uncertainty",
            uncertainty,
            "/nextDiagnosticQuestion",
            nextQuestion,
            unusedFields,
            ResolveCitations(claims, projection),
            candidates,
            mapping,
            prompt,
            Sha256(Encoding.UTF8.GetBytes(prompt)));
    }
}
