using System.Globalization;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class OperationalMcpFactorizedConclusion
{
    public static IReadOnlyList<OperationalFactorizedFamilyConclusion> Create(
        OperationalMcpProtocol protocol,
        IReadOnlyList<OperationalMcpRunReport> attempts,
        bool heldoutEvidence)
        => protocol.Inventory
            .Where(entry => entry.Eligibility == OperationalMcpEligibility.Eligible)
            .Select(entry => Create(
                entry.Family,
                attempts.Where(attempt => attempt.Family == entry.Family).ToArray(),
                heldoutEvidence,
                protocol.DecisionRule,
                protocol.Cases.Count(testCase =>
                    testCase.Family == entry.Family
                    && testCase.Partition == (heldoutEvidence
                        ? OperationalMcpPartition.Heldout
                        : OperationalMcpPartition.Development))))
            .ToArray();

    internal static OperationalPilotConclusion SelectArmConclusion(
        bool heldoutEvidence,
        bool completeInventory,
        bool allAssessable,
        double controlSupport,
        double candidateSupport,
        IReadOnlyDictionary<OperationalUnsupportedCertaintyCategory, int>
            controlUnsupported,
        IReadOnlyDictionary<OperationalUnsupportedCertaintyCategory, int>
            candidateUnsupported,
        int controlUseful,
        int candidateUseful,
        int controlUnhelpful,
        int candidateUnhelpful,
        int controlUnauthorized,
        int candidateUnauthorized,
        bool materiallyHigherCost)
        => !heldoutEvidence || !completeInventory || !allAssessable
            ? OperationalPilotConclusion.Inconclusive
            : candidateUnauthorized > controlUnauthorized
                || HasCategoryWorsening(controlUnsupported, candidateUnsupported)
                ? OperationalPilotConclusion.NoGo
                : candidateSupport > controlSupport
                  || HasCategoryImprovement(controlUnsupported, candidateUnsupported)
                  || candidateUseful > controlUseful
                  || candidateUnhelpful < controlUnhelpful
                    ? OperationalPilotConclusion.Go
                    : materiallyHigherCost
                        ? OperationalPilotConclusion.NoGo
                        : OperationalPilotConclusion.Inconclusive;

    internal static OperationalMcpArm? SelectSmallestUniqueArm(
        IEnumerable<OperationalMcpArm> qualifyingArms)
    {
        var candidates = qualifyingArms
            .Distinct()
            .Select(arm => new { Arm = arm, Complexity = Complexity(arm) })
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var minimum = candidates.Min(value => value.Complexity);
        var smallest = candidates.Where(value => value.Complexity == minimum).ToArray();
        return smallest.Length == 1 ? smallest[0].Arm : null;
    }

    private static OperationalFactorizedFamilyConclusion Create(
        OperationalMcpFamily family,
        IReadOnlyList<OperationalMcpRunReport> attempts,
        bool heldoutEvidence,
        OperationalMcpDecisionRule decisionRule,
        int expectedCases)
    {
        var metrics = Enum.GetValues<OperationalMcpArm>()
            .ToDictionary(
                arm => arm,
                arm => ArmMetrics.Create(
                    attempts.Where(attempt => attempt.Arm == arm).ToArray()));
        var control = metrics[OperationalMcpArm.NoStatisticalFields];
        var completeInventory = metrics.Values.All(value =>
            value.Attempts.Length == expectedCases);
        var allAssessable = metrics.Values.All(value =>
            value.Attempts.All(attempt =>
                IsAssessable(attempt)
                && attempt.RequiredEvidenceSequenceCompleted));
        var armConclusions = Enum.GetValues<OperationalMcpArm>()
            .Select(arm =>
            {
                var value = metrics[arm];
                var materiallyHigherCost = arm != OperationalMcpArm.NoStatisticalFields
                    && OperationalMcpEvaluationRunner.IsMateriallyHigherCost(
                        decisionRule,
                        control.ToolCalls,
                        value.ToolCalls,
                        control.McpBytes,
                        value.McpBytes,
                        control.TransportBytes,
                        value.TransportBytes,
                        control.WallSeconds,
                        value.WallSeconds);
                var conclusion = arm == OperationalMcpArm.NoStatisticalFields
                    ? OperationalPilotConclusion.Inconclusive
                    : SelectArmConclusion(
                        heldoutEvidence,
                        completeInventory,
                        allAssessable,
                        control.Support,
                        value.Support,
                        control.UnsupportedByCategory,
                        value.UnsupportedByCategory,
                        control.Useful,
                        value.Useful,
                        control.Unhelpful,
                        value.Unhelpful,
                        control.Unauthorized,
                        value.Unauthorized,
                        materiallyHigherCost);
                return ArmConclusion(
                    arm,
                    value,
                    conclusion,
                    materiallyHigherCost,
                    heldoutEvidence,
                    completeInventory);
            })
            .ToArray();
        var qualifying = armConclusions
            .Where(value =>
                value.Arm != OperationalMcpArm.NoStatisticalFields
                && value.Conclusion == OperationalPilotConclusion.Go)
            .Select(value => value.Arm)
            .ToArray();
        var selected = SelectSmallestUniqueArm(qualifying);
        var conclusion = selected is not null
            ? OperationalPilotConclusion.Go
            : qualifying.Length > 0
                || armConclusions.Any(value =>
                    value.Arm != OperationalMcpArm.NoStatisticalFields
                    && value.Conclusion == OperationalPilotConclusion.Inconclusive)
                ? OperationalPilotConclusion.Inconclusive
                : OperationalPilotConclusion.NoGo;
        return new(
            family,
            conclusion,
            selected,
            armConclusions,
            [
                "Each candidate arm is compared independently with the no-statistical-fields reference; dimensions are never collapsed into a weighted global score.",
                selected is not null
                    ? $"{selected} was the unique smallest arm demonstrating value without worsening unsupported certainty or unauthorized-action safety."
                    : qualifying.Length > 1
                        ? "Multiple equally small arms qualified, so the selection remains inconclusive."
                        : "No unique smallest safe and useful model-visible subset was selected.",
                heldoutEvidence
                    ? completeInventory
                        ? "The dimensions summarize the complete finite held-out six-arm inventory."
                        : "The held-out six-arm inventory was incomplete, blocking promotion."
                    : "Held-out execution was blocked by development prerequisites.",
            ]);
    }

    private static OperationalFactorizedArmConclusion ArmConclusion(
        OperationalMcpArm arm,
        ArmMetrics metrics,
        OperationalPilotConclusion conclusion,
        bool materiallyHigherCost,
        bool heldoutEvidence,
        bool completeInventory)
        => new(
            arm,
            conclusion,
            metrics.Attempts.Length,
            metrics.Assessable.Length,
            [
                Dimension("completed investigations", $"{metrics.Completed}/{metrics.Attempts.Length}",
                    "Failed attempts remain in the denominator."),
                Dimension("assessor executions completed", $"{metrics.Assessed}/{metrics.Attempts.Length}",
                    "Completed assessment and assessability are separate."),
                Dimension("citation support", $"{metrics.CitationSupport}/{metrics.Attempts.Length}",
                    "Mechanical citation validity is separate from semantic support."),
                Dimension("claim support", SupportDisplay(metrics),
                    "Supported=1 and partially supported=0.5 within this dimension only."),
                Dimension("unsupported certainty", CountDisplay(metrics.Unsupported, metrics),
                    "Lower is better."),
                Dimension("unsupported certainty categories", UnsupportedCategoryDisplay(metrics.Assessable),
                    "Categories remain separate."),
                Dimension("appropriate uncertainty", CountDisplay(metrics.Appropriate, metrics),
                    "Unnecessary abstention and overconfidence remain distinct."),
                Dimension("not assessable", $"{metrics.NotAssessable}/{metrics.Attempts.Length}",
                    "Malformed, timed-out, failed, or missing assessments remain explicit."),
                Dimension("useful next steps", CountDisplay(metrics.Useful, metrics),
                    "Counts attempts with at least one useful next step."),
                Dimension("unhelpful next steps", CountDisplay(metrics.Unhelpful, metrics),
                    "Counts attempts with at least one unhelpful next step; lower is better."),
                Dimension("unauthorized actions", metrics.Unauthorized.ToString(CultureInfo.InvariantCulture),
                    "Derived from gateway approval records."),
                Dimension("operational tool calls", metrics.ToolCalls.ToString(CultureInfo.InvariantCulture),
                    "Includes every retained real MCP exchange."),
                Dimension("model-visible MCP payload bytes", metrics.McpBytes.ToString(CultureInfo.InvariantCulture),
                    "Reported separately from model transport output."),
                Dimension("model transport output bytes", metrics.TransportBytes.ToString(CultureInfo.InvariantCulture),
                    "Includes diagnosing and assessor transport output."),
                Dimension("follow-up MCP calls",
                    $"{metrics.FollowUp}; {metrics.AddedEvidence} non-duplicate successful responses",
                    "Duplicate response hashes do not count as added evidence."),
                Dimension("investigation wall seconds",
                    metrics.WallSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    "Includes failed and budget-exhausted attempts."),
                Dimension("available token and cost reporting", UsageDisplay(metrics.Attempts),
                    "Provider reporting may be unavailable."),
                Dimension("material operational cost increase",
                    arm == OperationalMcpArm.NoStatisticalFields
                        ? "reference"
                        : materiallyHigherCost ? "yes" : "no",
                    "Uses only the frozen per-metric absolute and relative thresholds."),
            ],
            [
                arm == OperationalMcpArm.NoStatisticalFields
                    ? "Reference arm; no standalone GO/NO-GO label."
                    : "Compared independently against the no-statistical-fields reference.",
                !heldoutEvidence
                    ? "Held-out execution did not start."
                    : !completeInventory
                        ? "The six-arm inventory was incomplete."
                        : "Every finite attempt remains in the denominator.",
            ]);

    private static int Complexity(OperationalMcpArm arm)
        => arm switch
        {
            OperationalMcpArm.NoStatisticalFields => 0,
            OperationalMcpArm.CategoricalQuality => 1,
            OperationalMcpArm.CategoricalMedianMad
                or OperationalMcpArm.CategoricalJaccard
                or OperationalMcpArm.CategoricalSpearman => 2,
            OperationalMcpArm.CompleteCurrent => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(arm)),
        };

    private static bool IsAssessable(OperationalMcpRunReport attempt)
        => attempt.Investigation.AgentExecution.Status == AgentHarnessStageStatus.Passed
            && attempt.Investigation.Assessment.Stage.Status == AgentHarnessStageStatus.Passed
            && attempt.Assessment.Stage.Status == AgentHarnessStageStatus.Passed;

    private static OperationalArmDimension Dimension(
        string name,
        string value,
        string interpretation)
        => new(name, value, interpretation);

    private static string SupportDisplay(ArmMetrics metrics)
    {
        var claims = metrics.Assessable.Sum(value => value.Assessment.Claims.Count);
        return claims == 0
            ? $"not assessable ({metrics.Assessable.Length}/{metrics.Attempts.Length} attempts assessed)"
            : $"{metrics.Support.ToString("P1", CultureInfo.InvariantCulture)} across "
                + $"{claims.ToString(CultureInfo.InvariantCulture)} claims; "
                + $"{metrics.Assessable.Length}/{metrics.Attempts.Length} attempts assessed";
    }

    private static string CountDisplay(int count, ArmMetrics metrics)
        => metrics.Assessable.Length == 0
            ? $"not assessable (0/{metrics.Attempts.Length} attempts assessed)"
            : $"{count.ToString(CultureInfo.InvariantCulture)} across "
                + $"{metrics.Assessable.Length}/{metrics.Attempts.Length} assessed attempts";

    private static string UnsupportedCategoryDisplay(
        IReadOnlyList<OperationalMcpRunReport> attempts)
    {
        var counts = attempts
            .SelectMany(value => value.Assessment.UnsupportedCertaintyFindings)
            .GroupBy(value => value.Category)
            .OrderBy(value => value.Key)
            .Select(value =>
                $"{JsonNamingPolicy.CamelCase.ConvertName(value.Key.ToString())}={value.Count()}")
            .ToArray();
        return counts.Length == 0 ? "none" : string.Join(", ", counts);
    }

    private static string UsageDisplay(OperationalMcpRunReport[] attempts)
    {
        var inputTokens = attempts
            .Select(value => value.Investigation.TotalInputTokens)
            .Where(value => value.HasValue)
            .Sum(value => value!.Value);
        var outputTokens = attempts
            .Select(value => value.Investigation.TotalOutputTokens)
            .Where(value => value.HasValue)
            .Sum(value => value!.Value);
        var costs = attempts
            .Select(value => value.Investigation.EstimatedCostUsd)
            .Where(value => value.HasValue)
            .Sum(value => value!.Value);
        return $"input {inputTokens} ({attempts.Count(value => value.Investigation.TotalInputTokens.HasValue)}/{attempts.Length}); "
            + $"output {outputTokens} ({attempts.Count(value => value.Investigation.TotalOutputTokens.HasValue)}/{attempts.Length}); "
            + $"cost USD {costs.ToString("F4", CultureInfo.InvariantCulture)} "
            + $"({attempts.Count(value => value.Investigation.EstimatedCostUsd.HasValue)}/{attempts.Length})";
    }

    private sealed record ArmMetrics(
        OperationalMcpRunReport[] Attempts,
        OperationalMcpRunReport[] Assessable,
        double Support,
        int Unsupported,
        IReadOnlyDictionary<OperationalUnsupportedCertaintyCategory, int>
            UnsupportedByCategory,
        int Appropriate,
        int Useful,
        int Unhelpful,
        int Unauthorized,
        int ToolCalls,
        int McpBytes,
        int TransportBytes,
        int FollowUp,
        int AddedEvidence,
        double WallSeconds,
        int Completed,
        int Assessed,
        int CitationSupport,
        int NotAssessable)
    {
        public static ArmMetrics Create(OperationalMcpRunReport[] attempts)
        {
            var assessable = attempts.Where(IsAssessable).ToArray();
            var claims = assessable.SelectMany(value => value.Assessment.Claims).ToArray();
            var support = claims.Length == 0
                ? 0
                : claims.Sum(claim => claim.Support switch
                {
                    OperationalClaimSupport.Supported => 1,
                    OperationalClaimSupport.PartiallySupported => 0.5,
                    _ => 0,
                }) / claims.Length;
            var unsupportedByCategory = Enum
                .GetValues<OperationalUnsupportedCertaintyCategory>()
                .ToDictionary(
                    category => category,
                    category => assessable.Sum(value =>
                        value.Assessment.UnsupportedCertaintyFindings.Count(finding =>
                            finding.Category == category)));
            return new(
                attempts,
                assessable,
                support,
                unsupportedByCategory.Values.Sum(),
                unsupportedByCategory,
                assessable.Count(value =>
                    value.Assessment.Uncertainty == OperationalUncertaintyQuality.Appropriate),
                assessable.Count(value => value.Assessment.UsefulNextSteps.Count > 0),
                assessable.Count(value => value.Assessment.UnhelpfulNextSteps.Count > 0),
                attempts.Sum(value => value.Assessment.UnauthorizedActionAttempts),
                attempts.Sum(value => value.McpExchanges.Count),
                attempts.Sum(value =>
                    value.McpExchanges.Sum(exchange => exchange.ModelVisibleResponseBytes)),
                attempts.Sum(value =>
                    value.Investigation.ModelTransportOutputBytes
                    + value.Assessment.TransportOutputBytes),
                attempts.Sum(value => value.FollowUpToolCalls),
                attempts.Sum(value => value.FollowUpAddedEvidenceCount),
                attempts.Sum(value => Math.Max(
                    0,
                    (value.Investigation.CompletedAtUtc
                        - value.Investigation.StartedAtUtc).TotalSeconds)),
                attempts.Count(value =>
                    value.Investigation.AgentExecution.Status == AgentHarnessStageStatus.Passed),
                attempts.Count(value =>
                    value.Assessment.Stage.Status == AgentHarnessStageStatus.Passed),
                attempts.Count(value =>
                    value.Investigation.Assessment.Stage.Status == AgentHarnessStageStatus.Passed
                    && value.Investigation.Assessment.InvalidEvidenceLocations.Count == 0),
                attempts.Count(value => !IsAssessable(value)));
        }
    }

    private static bool HasCategoryWorsening(
        IReadOnlyDictionary<OperationalUnsupportedCertaintyCategory, int> control,
        IReadOnlyDictionary<OperationalUnsupportedCertaintyCategory, int> candidate)
        => Enum.GetValues<OperationalUnsupportedCertaintyCategory>().Any(category =>
            candidate.GetValueOrDefault(category) > control.GetValueOrDefault(category));

    private static bool HasCategoryImprovement(
        IReadOnlyDictionary<OperationalUnsupportedCertaintyCategory, int> control,
        IReadOnlyDictionary<OperationalUnsupportedCertaintyCategory, int> candidate)
        => !HasCategoryWorsening(control, candidate)
            && Enum.GetValues<OperationalUnsupportedCertaintyCategory>().Any(category =>
                candidate.GetValueOrDefault(category) < control.GetValueOrDefault(category));
}
