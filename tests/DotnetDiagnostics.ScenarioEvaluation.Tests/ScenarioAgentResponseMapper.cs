using System.Text;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

/// <summary>
/// Prototype for #681 (narrow follow-up to #646): maps a free-text-shaped <see cref="AgentScenarioResponse"/>
/// into the controlled-vocabulary <see cref="StructuredInterpretation"/> contract that
/// <see cref="ScenarioEvaluator"/> already knows how to score, plus a separate <see cref="UncertaintyAssessment"/>.
/// This is deliberately a small, deterministic bag-of-words heuristic (token-set Jaccard similarity against
/// the scenario manifest's known ids) rather than an actual NLU/LLM call -- consistent with the repo's
/// "avoid external network dependencies" and "prefer relative invariants over fragile assertions" principles.
/// It stays advisory: nothing here gates a PR or feeds a production MCP tool.
///
/// Known limitation: no stemming/synonym expansion, so phrasing that shares no tokens with the manifest's
/// controlled ids will not match even when a human would recognize it as equivalent. That is intentional --
/// an unmatched field is reported via <see cref="MappedAgentInterpretation.UnmappedFields"/> and scores as
/// unsupported rather than being guessed.
/// </summary>
public static class ScenarioAgentResponseMapper
{
    // Small, deliberately conservative match threshold: high enough that unrelated free text does not
    // accidentally match a single-candidate vocabulary (e.g. one acceptable attribution), low enough that
    // natural phrasing sharing the candidate's core terms still matches despite extra prose around it.
    private const double MatchThreshold = 0.34;

    // The causality posture is a small, fixed taxonomy across scenario manifests, not open text -- an agent
    // is expected to select from (a paraphrase of) one of these, not invent new posture ids. This list is
    // grounded in the postures actually used by the shipped manifests plus the "wrong" sentinel already
    // exercised by ScenarioReplayTests.
    private static readonly string[] KnownCausalityPostures =
    [
        "correlated-owner-and-waiters",
        "blocking-mechanism-observed",
        "attributed-runtime-cost",
        "causal-claim-without-evidence",
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "it", "its", "that", "this", "these", "those",
        "is", "are", "was", "were", "be", "been", "being", "than", "then", "so", "but", "not", "no",
        "do", "does", "did", "has", "have", "had", "will", "would", "can", "could", "may", "might",
        "must", "should", "we", "you", "they", "he", "she", "i", "because", "due", "which", "who",
        "whom", "with", "without", "by", "on", "in", "at", "as", "for", "from", "one", "like",
        "while", "behind", "some", "somewhere",
    };

    private static readonly string[] HedgeTerms =
    [
        "correlat", "appears to", "likely", "cannot fully confirm", "further investigation",
        "not fully proven", "consistent with", "may indicate",
    ];

    private static readonly string[] OverclaimTerms =
    [
        "definitely the cause", "without any doubt", "100% certain", "guaranteed to be",
        "proven root cause", "no other possible explanation",
    ];

    public static MappedAgentInterpretation Map(ScenarioManifest manifest, AgentScenarioResponse response)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(response);

        var unmapped = new Dictionary<string, string>(StringComparer.Ordinal);

        var hypothesisId = MatchVocabulary(
            response.Hypothesis,
            [.. manifest.AcceptableHypotheses, .. manifest.TemptingWrongHypotheses]);
        if (hypothesisId is null)
        {
            unmapped["hypothesis"] = response.Hypothesis;
        }

        var attributionId = MatchVocabulary(response.Attribution, manifest.AcceptableAttributions);
        if (attributionId is null)
        {
            unmapped["attribution"] = response.Attribution;
        }

        var nextActionId = MatchVocabulary(response.NextAction, manifest.AcceptableNextActions);
        if (nextActionId is null)
        {
            unmapped["nextAction"] = response.NextAction;
        }

        var causalityPosture = MatchVocabulary(response.CausalityStatement, KnownCausalityPostures);
        if (causalityPosture is null)
        {
            unmapped["causality"] = response.CausalityStatement;
        }

        var interpretation = new StructuredInterpretation(
            EvidenceIds: response.CitedEvidenceIds,
            HypothesisIds: hypothesisId is null ? [] : [hypothesisId],
            AttributionIds: attributionId is null ? [] : [attributionId],
            NextActionIds: nextActionId is null ? [] : [nextActionId],
            CausalityPosture: causalityPosture ?? "unclassified-causality-posture",
            ConclusionIds: [.. response.Conclusions.Select(Normalize).Where(value => value.Length > 0)]);

        return new MappedAgentInterpretation(interpretation, AssessUncertainty(response.Narrative), unmapped);
    }

    private static UncertaintyAssessment AssessUncertainty(string narrative)
    {
        var lowered = (narrative ?? string.Empty).ToLowerInvariant();
        var hedges = HedgeTerms.Where(term => lowered.Contains(term, StringComparison.Ordinal)).ToArray();
        var overclaims = OverclaimTerms.Where(term => lowered.Contains(term, StringComparison.Ordinal)).ToArray();
        return new UncertaintyAssessment(
            AcknowledgesLimits: hedges.Length > 0,
            OverclaimsCertainty: overclaims.Length > 0,
            HedgeTermsMatched: hedges,
            OverclaimTermsMatched: overclaims);
    }

    private static string? MatchVocabulary(string text, IReadOnlyList<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(text) || candidates.Count == 0)
        {
            return null;
        }

        var responseTokens = Tokenize(text);
        if (responseTokens.Count == 0)
        {
            return null;
        }

        string? best = null;
        var bestScore = 0.0;
        foreach (var candidate in candidates)
        {
            var score = Jaccard(responseTokens, Tokenize(candidate));
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore >= MatchThreshold ? best : null;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
        => Normalize(text)
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !StopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var previousWasHyphen = false;
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousWasHyphen = false;
            }
            else if (!previousWasHyphen)
            {
                builder.Append('-');
                previousWasHyphen = true;
            }
        }

        return builder.ToString().Trim('-');
    }
}
