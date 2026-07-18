namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ScenarioEvaluator
{
    private const double EvidenceWeight = 0.25;
    private const double AttributionWeight = 0.25;
    private const double NextActionWeight = 0.20;
    private const double CausalityWeight = 0.15;
    private const double UnsupportedConclusionWeight = 0.15;

    public static IReadOnlyList<EvidenceInvariantResult> EvaluateEvidence(
        ScenarioManifest manifest,
        ScenarioEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.SchemaVersion != ScenarioJson.CurrentEvidenceSchemaVersion)
        {
            throw new InvalidDataException(
                $"Evidence for '{evidence.ScenarioId}' uses schema version {evidence.SchemaVersion}; expected {ScenarioJson.CurrentEvidenceSchemaVersion}.");
        }

        if (!string.Equals(manifest.Id, evidence.ScenarioId, StringComparison.Ordinal)
            || !string.Equals(manifest.Version, evidence.ScenarioVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Evidence '{evidence.ScenarioId}@{evidence.ScenarioVersion}' does not match manifest '{manifest.Id}@{manifest.Version}'.");
        }

        return manifest.ExpectedEvidence.Select(invariant => EvaluateInvariant(invariant, evidence)).ToArray();
    }

    public static InterpretationScore ScoreInterpretation(
        ScenarioManifest manifest,
        IReadOnlyList<EvidenceInvariantResult> evidence,
        StructuredInterpretation interpretation)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(interpretation);

        var passedEvidence = evidence.Where(result => result.Passed).Select(result => result.Id).ToHashSet(StringComparer.Ordinal);
        var requiredEvidence = manifest.ExpectedEvidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var citedEvidence = interpretation.EvidenceIds.ToHashSet(StringComparer.Ordinal);
        var validCitations = citedEvidence.Count == 0
            ? 0
            : citedEvidence.Count(passedEvidence.Contains) / (double)citedEvidence.Count;
        var allRequiredEvidencePassed = requiredEvidence.All(passedEvidence.Contains);
        var requiredCoverage = requiredEvidence.Count == 0
            ? 1
            : requiredEvidence.Count(id => citedEvidence.Contains(id) && passedEvidence.Contains(id))
                / (double)requiredEvidence.Count;
        var hypothesisScore = interpretation.HypothesisIds.Count > 0
            && interpretation.HypothesisIds.All(manifest.AcceptableHypotheses.Contains)
            ? 1
            : 0;
        var evidenceScore = allRequiredEvidencePassed
            ? (validCitations + requiredCoverage + hypothesisScore) / 3
            : 0;

        var attributionScore = AllowedSelectionScore(
            manifest.AcceptableAttributions,
            interpretation.AttributionIds);
        var nextActionScore = AllowedSelectionScore(
            manifest.AcceptableNextActions,
            interpretation.NextActionIds);
        var causalityScore = string.Equals(
            manifest.RequiredCausalityPosture,
            interpretation.CausalityPosture,
            StringComparison.Ordinal) ? 1 : 0;
        var forbidden = interpretation.ConclusionIds
            .Intersect(manifest.ForbiddenConclusions, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var unsupportedScore = forbidden.Length == 0 ? 1 : 0;

        var dimensions = new[]
        {
            Dimension("evidence-correctness", EvidenceWeight, evidenceScore,
                $"required evidence={(allRequiredEvidencePassed ? "passed" : "failed")}; valid citations={validCitations:P0}; required coverage={requiredCoverage:P0}; hypotheses={(hypothesisScore == 1 ? "accepted" : "unsupported")}"),
            Dimension("attribution", AttributionWeight, attributionScore,
                $"matched {MatchedCount(manifest.AcceptableAttributions, interpretation.AttributionIds)} of {manifest.AcceptableAttributions.Count} acceptable attribution(s)"),
            Dimension("next-action", NextActionWeight, nextActionScore,
                $"matched {MatchedCount(manifest.AcceptableNextActions, interpretation.NextActionIds)} of {manifest.AcceptableNextActions.Count} acceptable next action(s)"),
            Dimension("correlation-versus-causality", CausalityWeight, causalityScore,
                $"expected '{manifest.RequiredCausalityPosture}', observed '{interpretation.CausalityPosture}'"),
            Dimension("unsupported-conclusions", UnsupportedConclusionWeight, unsupportedScore,
                forbidden.Length == 0 ? "no forbidden conclusion asserted" : $"forbidden: {string.Join(", ", forbidden)}"),
        };

        return new InterpretationScore(
            Math.Round(dimensions.Sum(dimension => dimension.Weight * dimension.Score), 6),
            dimensions);
    }

    public static ScenarioEvaluationReport CreateReport(
        ScenarioManifest manifest,
        ScenarioEvidence evidence,
        StructuredInterpretation? interpretation = null)
    {
        var invariantResults = EvaluateEvidence(manifest, evidence);
        InterpretationScore? interpretationScore = null;
        ScenarioStageResult interpretationStage;
        if (interpretation is null)
        {
            interpretationStage = new ScenarioStageResult(
                ScenarioStageStatus.NotRun,
                ScenarioFailureKind.None,
                "No structured interpretation was supplied.",
                0);
        }
        else
        {
            interpretationScore = ScoreInterpretation(manifest, invariantResults, interpretation);
            var passed = invariantResults.All(result => result.Passed)
                && interpretationScore.WeightedScore >= 0.9;
            interpretationStage = new ScenarioStageResult(
                passed ? ScenarioStageStatus.Passed : ScenarioStageStatus.Failed,
                passed ? ScenarioFailureKind.None : ScenarioFailureKind.Evaluation,
                $"Weighted score {interpretationScore.WeightedScore:P1}.",
                0);
        }

        return new ScenarioEvaluationReport(
            SchemaVersion: ScenarioJson.CurrentReportSchemaVersion,
            ScenarioId: manifest.Id,
            Trial: evidence.Trial,
            Activation: evidence.Activation,
            Collection: evidence.Collection,
            Interpretation: interpretationStage,
            Evidence: invariantResults,
            InterpretationScore: interpretationScore);
    }

    private static EvidenceInvariantResult EvaluateInvariant(
        EvidenceInvariant invariant,
        ScenarioEvidence evidence)
        => invariant.Kind switch
        {
            EvidenceInvariantKind.SignalPresent => SignalPresent(invariant, evidence),
            EvidenceInvariantKind.SignalBucketMatch => SignalBucketMatch(invariant, evidence),
            EvidenceInvariantKind.CounterComparison => CounterComparison(invariant, evidence),
            EvidenceInvariantKind.StackFrameMatch => StackFrameMatch(invariant, evidence),
            EvidenceInvariantKind.ThreadOwnerCorrelation => ThreadOwnerCorrelation(invariant, evidence),
            _ => throw new InvalidDataException($"Unsupported invariant kind '{invariant.Kind}'."),
        };

    private static EvidenceInvariantResult SignalPresent(EvidenceInvariant invariant, ScenarioEvidence evidence)
    {
        var found = evidence.Signals.Any(signal => string.Equals(signal.Signal, invariant.Signal, StringComparison.Ordinal));
        return Result(invariant, found, found
            ? $"Signal '{invariant.Signal}' was present."
            : $"Signal '{invariant.Signal}' was absent.");
    }

    private static EvidenceInvariantResult SignalBucketMatch(EvidenceInvariant invariant, ScenarioEvidence evidence)
    {
        var buckets = evidence.Signals
            .Where(signal => string.Equals(signal.Signal, invariant.Signal, StringComparison.Ordinal))
            .SelectMany(signal => signal.Buckets)
            .Where(bucket => ContainsAny(bucket.Key, invariant.ContainsAny!))
            .ToArray();
        var matched = buckets.FirstOrDefault(bucket => Compare(bucket.Magnitude, invariant.Comparison!.Value, invariant.Threshold!.Value));
        return Result(invariant, matched is not null, matched is null
            ? $"No '{invariant.Signal}' bucket matched [{string.Join(", ", invariant.ContainsAny!)}] and {invariant.Comparison} {invariant.Threshold}."
            : $"Bucket '{matched.Key}' had magnitude {matched.Magnitude} {matched.Unit}.");
    }

    private static EvidenceInvariantResult CounterComparison(EvidenceInvariant invariant, ScenarioEvidence evidence)
    {
        var metric = evidence.Metrics.FirstOrDefault(item => string.Equals(item.Name, invariant.Metric, StringComparison.Ordinal));
        var passed = metric is not null && Compare(metric.Value, invariant.Comparison!.Value, invariant.Threshold!.Value);
        return Result(invariant, passed, metric is null
            ? $"Metric '{invariant.Metric}' was absent."
            : $"Metric '{metric.Name}' was {metric.Value} {metric.Unit}; expected {invariant.Comparison} {invariant.Threshold}.");
    }

    private static EvidenceInvariantResult StackFrameMatch(EvidenceInvariant invariant, ScenarioEvidence evidence)
    {
        var count = evidence.Frames
            .Where(frame => ContainsAny(frame.DisplayName, invariant.ContainsAny!))
            .Sum(frame => frame.MatchCount);
        return Result(invariant, count >= invariant.MinimumMatches,
            $"Matched {count} frame(s); expected at least {invariant.MinimumMatches} containing [{string.Join(", ", invariant.ContainsAny!)}].");
    }

    private static EvidenceInvariantResult ThreadOwnerCorrelation(EvidenceInvariant invariant, ScenarioEvidence evidence)
    {
        var relation = evidence.Relations.FirstOrDefault(item =>
            string.Equals(item.Relation, invariant.Relation, StringComparison.Ordinal)
            && string.Equals(item.OwnerWaitReason, invariant.OwnerWaitReason, StringComparison.Ordinal)
            && Compare(item.WaitingThreadCount, invariant.Comparison!.Value, invariant.Threshold!.Value));
        return Result(invariant, relation is not null, relation is null
            ? $"No relation '{invariant.Relation}' had owner wait reason '{invariant.OwnerWaitReason}' and waiter count {invariant.Comparison} {invariant.Threshold}."
            : $"Owner wait reason '{relation.OwnerWaitReason}' overlapped with {relation.WaitingThreadCount} waiter(s).");
    }

    private static bool Compare(double value, NumericComparison comparison, double threshold)
        => comparison switch
        {
            NumericComparison.GreaterThan => value > threshold,
            NumericComparison.GreaterThanOrEqual => value >= threshold,
            NumericComparison.LessThan => value < threshold,
            NumericComparison.LessThanOrEqual => value <= threshold,
            NumericComparison.Equal => Math.Abs(value - threshold) < 0.000001,
            _ => throw new InvalidDataException($"Unsupported comparison '{comparison}'."),
        };

    private static bool ContainsAny(string value, IReadOnlyList<string> terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static EvidenceInvariantResult Result(EvidenceInvariant invariant, bool passed, string detail)
        => new(invariant.Id, passed, detail);

    private static InterpretationDimension Dimension(string name, double weight, double score, string detail)
        => new(name, weight, Math.Round(score, 6), detail);

    private static double AllowedSelectionScore(IReadOnlyList<string> acceptable, IReadOnlyList<string> observed)
        => observed.Count > 0 && observed.All(acceptable.Contains) ? 1 : 0;

    private static int MatchedCount(IReadOnlyList<string> acceptable, IReadOnlyList<string> observed)
        => acceptable.Count(observed.Contains);
}
