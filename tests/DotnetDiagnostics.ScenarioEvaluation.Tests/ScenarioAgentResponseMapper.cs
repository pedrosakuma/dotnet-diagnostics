using System.Text;
using System.Text.RegularExpressions;

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

    // Negation words are deliberately kept OUT of the stopword list (unlike "a", "the", etc.) --
    // dropping them would let a candidate be matched even when the response explicitly negates it
    // (see NegationMarkers / IsNegated below, which needs them present in the token sequence).
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "it", "its", "that", "this", "these", "those",
        "is", "are", "was", "were", "be", "been", "being", "than", "then", "so", "but",
        "do", "does", "did", "has", "have", "had", "will", "would", "can", "could", "may", "might",
        "must", "should", "we", "you", "they", "he", "she", "i", "because", "due", "which", "who",
        "whom", "with", "without", "by", "on", "in", "at", "as", "for", "from", "one", "like",
        "while", "behind", "some", "somewhere",
    };

    // Words that negate whatever candidate token follows within NegationWindow tokens, bounded to the
    // same clause (see ClauseBoundaryPattern) so a contrastive construction like "not X, but Y" negates
    // only X, not Y. This is still an order-sensitive proximity check, not real negation-scope parsing --
    // it guards against the common "not <candidate phrase>" shape a wrong-diagnosis response would use,
    // without rejecting hedged-but-correct phrasing where "not" refers to something else entirely (e.g.
    // "correlated ... not yet proven" -- "not" here precedes "proven", not the candidate's own tokens).
    private static readonly HashSet<string> NegationMarkers = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "cannot", "doesnt", "isnt", "wasnt", "arent", "non",
    };

    private const int NegationWindow = 3;

    // Clause separators: sentence-ending punctuation plus common contrastive/alternative conjunctions.
    // Splitting on these before tokenizing serves two purposes: (1) it keeps the negation-proximity
    // check (IsNegated) from crossing into an unrelated clause, e.g. "Not a GC pause, but sleeping
    // monitor owner serializes work" must not treat "sleeping..." as negated just because "not" appears
    // a few tokens earlier in a *different* clause; (2) it lets MatchVocabulary score each clause on its
    // own token set, so a short candidate phrase mentioned alongside an unrelated one in the same
    // sentence ("sleeping monitor owner serializes work or gc pause") is not diluted below the match
    // threshold by the other clause's unrelated tokens.
    // The punctuation alternatives require a trailing whitespace/end-of-string boundary (optionally
    // preceded by a closing quote or bracket, e.g. `pause!"` or `pause!)`) so a dotted, namespace-
    // qualified attribution id (e.g. "System.Globalization.CompareInfo.GetHashCodeOfString") is never
    // split mid-identifier -- only real sentence/clause punctuation (followed, after any closing
    // quote/bracket, by a space or end of text) counts as a boundary.
    private static readonly Regex ClauseBoundaryPattern = new(
        @"[.,;:!?][""'\u2019\u201d)\]]*(?=\s|$)|\bbut\b|\bhowever\b|\balthough\b|\bor\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Typographic (curly) quote characters are normalized to their ASCII equivalents before contraction
    // expansion, so free text using "smart quotes" (e.g. "isn't" with a Unicode right single quote) still
    // matches the ContractionExpansions patterns below instead of falling through to raw apostrophe-split
    // fragments that lose the negation.
    private static string NormalizeQuotes(string text)
        => text.Replace('\u2019', '\'').Replace('\u2018', '\'');

    // Expanded before clause splitting so a negated contraction ("isn't", "doesn't", ...) produces a
    // real "not" token instead of "isnt" -> "isn"/"t" fragments that neither match NegationMarkers nor
    // stay attached to the word they negate.
    private static readonly (Regex Pattern, string Replacement)[] ContractionExpansions =
    [
        (new Regex(@"\bisn't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "is not"),
        (new Regex(@"\baren't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "are not"),
        (new Regex(@"\bwasn't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "was not"),
        (new Regex(@"\bweren't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "were not"),
        (new Regex(@"\bdoesn't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "does not"),
        (new Regex(@"\bdon't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "do not"),
        (new Regex(@"\bdidn't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "did not"),
        (new Regex(@"\bcan't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "cannot"),
        (new Regex(@"\bwon't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "will not"),
        (new Regex(@"\bcouldn't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "could not"),
        (new Regex(@"\bshouldn't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "should not"),
        (new Regex(@"\bwouldn't\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "would not"),
    ];

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

    // A negation cue immediately before a matched overclaim phrase means the response is rejecting
    // that overclaim ("This is not definitely the cause"), not making it -- so that occurrence must
    // not count. Bounded to the nearest UncertaintyNegationWindow words before the phrase (and never
    // crossing a `.`/`!`/`?` sentence boundary), so an unrelated negation earlier in the same sentence
    // does not suppress a distinct, later hedge/overclaim phrase in that sentence.
    private static readonly Regex UncertaintyNegationPattern = new(
        @"\b(not|no|never|isn't|isnt|doesn't|doesnt|cannot|can't|cant|wasn't|wasnt)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int UncertaintyNegationWindow = 4;

    private static UncertaintyAssessment AssessUncertainty(string narrative)
    {
        var lowered = NormalizeQuotes((narrative ?? string.Empty).ToLowerInvariant());
        var hedges = HedgeTerms.Where(term => ContainsUnnegatedOccurrence(lowered, term)).ToArray();
        var overclaims = OverclaimTerms.Where(term => ContainsUnnegatedOccurrence(lowered, term)).ToArray();
        return new UncertaintyAssessment(
            AcknowledgesLimits: hedges.Length > 0,
            OverclaimsCertainty: overclaims.Length > 0,
            HedgeTermsMatched: hedges,
            OverclaimTermsMatched: overclaims);
    }

    private static bool ContainsUnnegatedOccurrence(string lowered, string term)
    {
        var searchFrom = 0;
        while (true)
        {
            var index = lowered.IndexOf(term, searchFrom, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var sentenceStart = 0;
            for (var i = 0; i < index; i++)
            {
                if (lowered[i] is '.' or '!' or '?')
                {
                    sentenceStart = i + 1;
                }
            }

            // Only the last few words before the phrase count as "immediately preceding" -- this
            // keeps an unrelated negation earlier in a long sentence from suppressing a distinct,
            // later hedge/overclaim phrase in that same sentence.
            var precedingWords = lowered[sentenceStart..index]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var windowWords = precedingWords.Length <= UncertaintyNegationWindow
                ? precedingWords
                : precedingWords[^UncertaintyNegationWindow..];
            var preceding = string.Join(' ', windowWords);
            if (!UncertaintyNegationPattern.IsMatch(preceding))
            {
                return true;
            }

            searchFrom = index + term.Length;
        }
    }

    private static string? MatchVocabulary(string text, IReadOnlyList<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(text) || candidates.Count == 0)
        {
            return null;
        }

        var responseTokens = TokenizeOrdered(text, out var clauseIndex);
        if (responseTokens.Count == 0)
        {
            return null;
        }

        var clauseTokenSets = GroupByClause(responseTokens, clauseIndex);
        var matches = new List<string>();
        foreach (var candidate in candidates)
        {
            var candidateTokens = Tokenize(candidate);

            // Score against each clause's own tokens (not the whole flattened response) and take the
            // best: scoring against the flattened response would dilute a short candidate phrase
            // mentioned in one clause with unrelated tokens from every other clause, silently hiding
            // an explicitly stated alternative diagnosis in the same sentence.
            var bestClauseScore = clauseTokenSets.Count == 0
                ? 0
                : clauseTokenSets.Max(clauseTokens => Jaccard(clauseTokens, candidateTokens));
            if (bestClauseScore < MatchThreshold)
            {
                continue;
            }

            // A candidate whose own tokens are immediately preceded (within the same clause) by a
            // negation marker in the response ("not <candidate phrase>") is excluded even though the
            // bag-of-words overlap looks strong -- otherwise a rejected diagnosis would silently
            // score as accepted.
            if (!IsNegated(responseTokens, clauseIndex, candidateTokens))
            {
                matches.Add(candidate);
            }
        }

        // More than one candidate clearing the threshold means the response is ambiguous between
        // (at least) two controlled-vocabulary ids -- e.g. it hedges between the right and a wrong
        // hypothesis in the same sentence. Silently picking the "best" one would hide that
        // ambiguity and risk a false-clean score, so this is reported as unmapped instead.
        return matches.Count == 1 ? matches[0] : null;
    }

    private static List<HashSet<string>> GroupByClause(List<string> orderedTokens, List<int> clauseIndex)
    {
        var groups = new Dictionary<int, HashSet<string>>();
        for (var i = 0; i < orderedTokens.Count; i++)
        {
            if (!groups.TryGetValue(clauseIndex[i], out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                groups[clauseIndex[i]] = set;
            }

            set.Add(orderedTokens[i]);
        }

        return [.. groups.Values];
    }

    private static bool IsNegated(List<string> orderedResponseTokens, List<int> clauseIndex, HashSet<string> candidateTokens)
    {
        for (var i = 0; i < orderedResponseTokens.Count; i++)
        {
            if (!candidateTokens.Contains(orderedResponseTokens[i]))
            {
                continue;
            }

            var clause = clauseIndex[i];

            // Check both directions within the same clause: "not <candidate>" (negation marker
            // before the candidate token) and "<candidate> ... is not" (negation marker shortly
            // after), so a trailing rejection like "sleeping monitor owner serializes work is not
            // the cause" is caught too, not just a leading one.
            var windowStart = Math.Max(0, i - NegationWindow);
            for (var j = windowStart; j < i; j++)
            {
                if (clauseIndex[j] == clause && NegationMarkers.Contains(orderedResponseTokens[j]))
                {
                    return true;
                }
            }

            var windowEnd = Math.Min(orderedResponseTokens.Count - 1, i + NegationWindow);
            for (var j = i + 1; j <= windowEnd; j++)
            {
                if (clauseIndex[j] == clause && NegationMarkers.Contains(orderedResponseTokens[j]))
                {
                    return true;
                }
            }
        }

        return false;
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
        => TokenizeOrdered(text, out _).ToHashSet(StringComparer.Ordinal);

    private static List<string> TokenizeOrdered(string text, out List<int> clauseIndex)
    {
        var tokens = new List<string>();
        clauseIndex = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var expanded = NormalizeQuotes(text);
        foreach (var (pattern, replacement) in ContractionExpansions)
        {
            expanded = pattern.Replace(expanded, replacement);
        }

        var clauses = ClauseBoundaryPattern.Split(expanded);
        for (var clause = 0; clause < clauses.Length; clause++)
        {
            foreach (var token in Normalize(clauses[clause]).Split('-', StringSplitOptions.RemoveEmptyEntries))
            {
                if (StopWords.Contains(token))
                {
                    continue;
                }

                tokens.Add(token);
                clauseIndex.Add(clause);
            }
        }

        return tokens;
    }

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
