using System.Globalization;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record OperationalLexicographicValidity(
    bool HeldoutEvidence,
    bool CompleteInventory,
    bool AllAssessable,
    bool ExactCitations,
    bool NoUnauthorizedActions,
    bool ValidAccounting,
    bool NoHarnessDefect);

internal sealed record OperationalLexicographicArmMetrics(
    OperationalMcpArm Arm,
    IReadOnlyDictionary<OperationalUnsupportedCertaintyCategory, int>
        UnsupportedByCategory,
    int SupportedClaims,
    int ClaimCount,
    int UnhelpfulNextSteps,
    int ModelVisibleFieldCount,
    int TransportOutputBytes);

internal sealed record OperationalLexicographicSelection(
    OperationalPilotConclusion Conclusion,
    OperationalMcpArm? SelectedArm,
    IReadOnlySet<OperationalMcpArm> UnsafeArms,
    IReadOnlyList<OperationalSelectorStep> Steps);

internal static class OperationalMcpFactorizedConclusion
{
    public static IReadOnlyList<OperationalFactorizedFamilyConclusion> Create(
        OperationalMcpProtocol protocol,
        IReadOnlyList<OperationalMcpRunReport> attempts,
        bool heldoutEvidence)
        => protocol.Inventory
            .Where(entry => entry.Eligibility == OperationalMcpEligibility.Eligible)
            .Select(entry => Create(
                protocol,
                entry.Family,
                attempts.Where(attempt => attempt.Family == entry.Family).ToArray(),
                heldoutEvidence,
                protocol.Cases.Count(testCase =>
                    testCase.Family == entry.Family
                    && testCase.Partition == (heldoutEvidence
                        ? OperationalMcpPartition.Heldout
                        : OperationalMcpPartition.Development))))
            .ToArray();

    internal static OperationalLexicographicSelection SelectLexicographically(
        OperationalLexicographicValidity validity,
        IReadOnlyList<OperationalLexicographicArmMetrics> metrics)
    {
        var steps = new List<OperationalSelectorStep>();
        var validityFailures = new List<string>();
        AddValidityFailure(
            validityFailures,
            validity.HeldoutEvidence,
            "held-out evidence is unavailable");
        AddValidityFailure(
            validityFailures,
            validity.CompleteInventory,
            "the finite inventory is incomplete");
        AddValidityFailure(
            validityFailures,
            validity.AllAssessable,
            "one or more attempts are not assessable");
        AddValidityFailure(
            validityFailures,
            validity.ExactCitations,
            "one or more exact citations failed");
        AddValidityFailure(
            validityFailures,
            validity.NoUnauthorizedActions,
            "one or more unauthorized actions were attempted");
        AddValidityFailure(
            validityFailures,
            validity.ValidAccounting,
            "retained artifact accounting is invalid");
        AddValidityFailure(
            validityFailures,
            validity.NoHarnessDefect,
            "an unresolved harness defect or identity mismatch remains");
        if (validityFailures.Count > 0)
        {
            steps.Add(new(
                "validity",
                "failed",
                string.Join("; ", validityFailures)));
            return new(
                OperationalPilotConclusion.Inconclusive,
                null,
                new HashSet<OperationalMcpArm>(),
                steps);
        }

        steps.Add(new(
            "validity",
            "passed",
            "The complete held-out inventory is assessable with exact citations, no unauthorized actions, exact accounting, and no unresolved harness defect."));
        var control = metrics.Single(value =>
            value.Arm == OperationalMcpArm.NoStatisticalFields);
        var unsafeArms = metrics
            .Where(value =>
                value.Arm != OperationalMcpArm.NoStatisticalFields
                && HasCategoryWorsening(
                    control.UnsupportedByCategory,
                    value.UnsupportedByCategory))
            .Select(value => value.Arm)
            .ToHashSet();
        var remaining = metrics
            .Where(value => !unsafeArms.Contains(value.Arm))
            .ToArray();
        steps.Add(new(
            "categorySafety",
            "passed",
            unsafeArms.Count == 0
                ? "Neither candidate increases any unsupported-certainty category versus control."
                : $"Excluded unsafe candidates: {string.Join(", ", unsafeArms.Order())}."));

        var minimumUnsupported = remaining.Min(TotalUnsupported);
        remaining = remaining
            .Where(value => TotalUnsupported(value) == minimumUnsupported)
            .ToArray();
        steps.Add(new(
            "unsupportedCertainty",
            remaining.Length == 1 ? "selected" : "tied",
            $"Retained arms with the lowest total finding count ({minimumUnsupported}): {ArmList(remaining)}."));
        if (remaining.Length > 1)
        {
            var best = remaining.Aggregate((current, value) =>
                CompareSupport(value, current) > 0 ? value : current);
            remaining = remaining
                .Where(value => CompareSupport(value, best) == 0)
                .ToArray();
            steps.Add(new(
                "semanticClaimSupport",
                remaining.Length == 1 ? "selected" : "tied",
                $"Retained arms with the highest supported/total claim rate: {ArmList(remaining)}."));
        }

        if (remaining.Length > 1)
        {
            var minimumUnhelpful = remaining.Min(value => value.UnhelpfulNextSteps);
            remaining = remaining
                .Where(value => value.UnhelpfulNextSteps == minimumUnhelpful)
                .ToArray();
            steps.Add(new(
                "unhelpfulNextSteps",
                remaining.Length == 1 ? "selected" : "tied",
                $"Retained arms with the lowest unhelpful-next-step count ({minimumUnhelpful}): {ArmList(remaining)}."));
        }

        if (remaining.Length > 1)
        {
            var minimumFields = remaining.Min(value => value.ModelVisibleFieldCount);
            remaining = remaining
                .Where(value => value.ModelVisibleFieldCount == minimumFields)
                .ToArray();
            steps.Add(new(
                "modelVisibleFieldCount",
                remaining.Length == 1 ? "selected" : "tied",
                $"Retained arms with the lowest field count ({minimumFields}): {ArmList(remaining)}."));
        }

        if (remaining.Length > 1)
        {
            var minimumTransportBytes = remaining.Min(value => value.TransportOutputBytes);
            remaining = remaining
                .Where(value => value.TransportOutputBytes == minimumTransportBytes)
                .ToArray();
            steps.Add(new(
                "transportOutputBytes",
                remaining.Length == 1 ? "selected" : "tied",
                $"Retained arms with the lowest total transport-output bytes ({minimumTransportBytes}): {ArmList(remaining)}."));
        }

        if (remaining.Length != 1)
        {
            steps.Add(new(
                "decision",
                "inconclusive",
                $"The frozen selector ended with an unresolved tie: {ArmList(remaining)}."));
            return new(
                OperationalPilotConclusion.Inconclusive,
                null,
                unsafeArms,
                steps);
        }

        var selected = remaining[0].Arm;
        var conclusion = selected == OperationalMcpArm.NoStatisticalFields
            ? OperationalPilotConclusion.NoGo
            : OperationalPilotConclusion.Go;
        steps.Add(new(
            "decision",
            conclusion == OperationalPilotConclusion.Go ? "go" : "noGo",
            selected == OperationalMcpArm.NoStatisticalFields
                ? "The no-statistical-fields control is the unique lexicographic selection."
                : $"{selected} is the unique lexicographic selection."));
        return new(conclusion, selected, unsafeArms, steps);
    }

    internal static OperationalLexicographicArmMetrics AggregateForSelection(
        OperationalMcpArm arm,
        IReadOnlyList<OperationalClaimSupport> claims,
        IReadOnlyList<IReadOnlyList<string>> unhelpfulNextSteps,
        IReadOnlyDictionary<OperationalUnsupportedCertaintyCategory, int>
            unsupportedByCategory,
        int modelVisibleFieldCount,
        int transportOutputBytes)
        => new(
            arm,
            unsupportedByCategory,
            claims.Count(value => value == OperationalClaimSupport.Supported),
            claims.Count,
            unhelpfulNextSteps.Sum(value => value.Count),
            modelVisibleFieldCount,
            transportOutputBytes);

    private static OperationalFactorizedFamilyConclusion Create(
        OperationalMcpProtocol protocol,
        OperationalMcpFamily family,
        IReadOnlyList<OperationalMcpRunReport> attempts,
        bool heldoutEvidence,
        int expectedCases)
    {
        var metrics = protocol.ActiveArms
            .ToDictionary(
                arm => arm,
                arm => ArmMetrics.Create(
                    attempts.Where(attempt => attempt.Arm == arm).ToArray(),
                    protocol.DecisionRule.ModelVisibleFieldCounts[arm]));
        var completeInventory = metrics.Values.All(value =>
                value.Attempts.Length == expectedCases)
            && attempts.All(value => protocol.ActiveArms.Contains(value.Arm));
        var allAssessable = metrics.Values.All(value =>
            value.Attempts.All(attempt =>
                IsAssessable(attempt)
                && attempt.RequiredEvidenceSequenceCompleted));
        var exactCitations = attempts.All(value =>
            value.Investigation.Assessment.Stage.Status == AgentHarnessStageStatus.Passed
            && value.Investigation.Assessment.InvalidEvidenceLocations.Count == 0);
        var noUnauthorizedActions = attempts.All(value =>
            value.Assessment.UnauthorizedActionAttempts == 0);
        var validAccounting = attempts.All(HasValidAccounting);
        var noHarnessDefect = attempts.All(value =>
            HasNoHarnessDefect(protocol, value));
        var selection = SelectLexicographically(
            new(
                heldoutEvidence,
                completeInventory,
                allAssessable,
                exactCitations,
                noUnauthorizedActions,
                validAccounting,
                noHarnessDefect),
            protocol.ActiveArms
                .Select(arm => metrics[arm].ToSelectorMetrics(arm))
                .ToArray());
        var armConclusions = protocol.ActiveArms
            .Select(arm =>
            {
                var value = metrics[arm];
                var conclusion = selection.Conclusion == OperationalPilotConclusion.Inconclusive
                    ? OperationalPilotConclusion.Inconclusive
                    : arm == selection.SelectedArm
                        ? selection.Conclusion
                        : selection.UnsafeArms.Contains(arm)
                            ? OperationalPilotConclusion.NoGo
                            : OperationalPilotConclusion.Inconclusive;
                return ArmConclusion(
                    arm,
                    value,
                    conclusion,
                    heldoutEvidence,
                    completeInventory);
            })
            .ToArray();
        return new(
            family,
            selection.Conclusion,
            selection.SelectedArm,
            armConclusions,
            [
                "The frozen selector applies validity, category safety, unsupported-certainty total, exact retained claim-support rate, unhelpful next steps, model-visible field count, and transport-output bytes in that order; no weighted score is used.",
                selection.SelectedArm is not null
                    ? $"{selection.SelectedArm} was the unique lexicographic selection."
                    : "No unique arm survived every frozen selector step.",
                heldoutEvidence
                    ? completeInventory
                        ? "The dimensions summarize the complete finite held-out three-arm inventory."
                        : "The held-out three-arm inventory was incomplete, blocking selection."
                    : "Held-out execution was blocked by development prerequisites.",
            ])
        {
            SelectorSteps = selection.Steps,
        };
    }

    private static OperationalFactorizedArmConclusion ArmConclusion(
        OperationalMcpArm arm,
        ArmMetrics metrics,
        OperationalPilotConclusion conclusion,
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
                    "Exact retained supported/total claim rate; partially supported claims remain in the denominator."),
                Dimension("unsupported certainty", CountDisplay(metrics.Unsupported, metrics),
                    "Lower is better."),
                Dimension("unsupported certainty categories", UnsupportedCategoryDisplay(metrics.Assessable),
                    "Categories remain separate."),
                Dimension("appropriate uncertainty", CountDisplay(metrics.Appropriate, metrics),
                    "Unnecessary abstention and overconfidence remain distinct."),
                Dimension("not assessable", $"{metrics.NotAssessable}/{metrics.Attempts.Length}",
                    "Malformed, timed-out, failed, or missing assessments remain explicit."),
                Dimension("useful next steps", CountDisplay(metrics.Useful, metrics),
                    "Total assessor-listed useful next steps; reported but not used by the selector."),
                Dimension("unhelpful next steps", CountDisplay(metrics.Unhelpful, metrics),
                    "Total assessor-listed unhelpful next steps; lower is better."),
                Dimension("unauthorized actions", metrics.Unauthorized.ToString(CultureInfo.InvariantCulture),
                    "Derived from gateway approval records."),
                Dimension("operational tool calls", metrics.ToolCalls.ToString(CultureInfo.InvariantCulture),
                    "Includes every retained real MCP exchange."),
                Dimension("model-visible MCP payload bytes", metrics.McpBytes.ToString(CultureInfo.InvariantCulture),
                    "Reported separately from model transport output."),
                Dimension("model transport output bytes", metrics.TransportBytes.ToString(CultureInfo.InvariantCulture),
                    "Includes diagnosing and assessor transport output."),
                Dimension("model-visible field count", metrics.ModelVisibleFieldCount.ToString(CultureInfo.InvariantCulture),
                    "Frozen count used only after unsupported certainty, claim support, and unhelpful next steps tie."),
                Dimension("follow-up MCP calls",
                    $"{metrics.FollowUp}; {metrics.AddedEvidence} non-duplicate successful responses",
                    "Duplicate response hashes do not count as added evidence."),
                Dimension("investigation wall seconds",
                    metrics.WallSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    "Includes failed and budget-exhausted attempts."),
                Dimension("available token and cost reporting", UsageDisplay(metrics.Attempts),
                    "Provider reporting may be unavailable."),
            ],
            [
                arm == OperationalMcpArm.NoStatisticalFields
                    ? "Reference arm; no standalone GO/NO-GO label."
                    : "Compared independently against the no-statistical-fields reference.",
                !heldoutEvidence
                    ? "Held-out execution did not start."
                    : !completeInventory
                        ? "The three-arm inventory was incomplete."
                        : "Every finite attempt remains in the denominator.",
            ]);

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
        int ModelVisibleFieldCount,
        int FollowUp,
        int AddedEvidence,
        double WallSeconds,
        int Completed,
        int Assessed,
        int CitationSupport,
        int NotAssessable)
    {
        public static ArmMetrics Create(
            OperationalMcpRunReport[] attempts,
            int modelVisibleFieldCount)
        {
            var assessable = attempts.Where(IsAssessable).ToArray();
            var claims = assessable.SelectMany(value => value.Assessment.Claims).ToArray();
            var support = claims.Length == 0
                ? 0
                : (double)claims.Count(claim =>
                    claim.Support == OperationalClaimSupport.Supported) / claims.Length;
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
                assessable.Sum(value => value.Assessment.UsefulNextSteps.Count),
                assessable.Sum(value => value.Assessment.UnhelpfulNextSteps.Count),
                attempts.Sum(value => value.Assessment.UnauthorizedActionAttempts),
                attempts.Sum(value => value.McpExchanges.Count),
                attempts.Sum(value =>
                    value.McpExchanges.Sum(exchange => exchange.ModelVisibleResponseBytes)),
                attempts.Sum(value =>
                    value.Investigation.ModelTransportOutputBytes
                    + value.Assessment.TransportOutputBytes),
                modelVisibleFieldCount,
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

        public OperationalLexicographicArmMetrics ToSelectorMetrics(OperationalMcpArm arm)
        {
            return AggregateForSelection(
                arm,
                Assessable
                    .SelectMany(value => value.Assessment.Claims)
                    .Select(value => value.Support)
                    .ToArray(),
                Assessable
                    .Select(value => value.Assessment.UnhelpfulNextSteps)
                    .ToArray(),
                UnsupportedByCategory,
                ModelVisibleFieldCount,
                TransportBytes);
        }
    }

    private static bool HasCategoryWorsening(
        IReadOnlyDictionary<OperationalUnsupportedCertaintyCategory, int> control,
        IReadOnlyDictionary<OperationalUnsupportedCertaintyCategory, int> candidate)
        => Enum.GetValues<OperationalUnsupportedCertaintyCategory>().Any(category =>
            candidate.GetValueOrDefault(category) > control.GetValueOrDefault(category));

    private static void AddValidityFailure(
        List<string> failures,
        bool condition,
        string failure)
    {
        if (!condition)
        {
            failures.Add(failure);
        }
    }

    private static int TotalUnsupported(OperationalLexicographicArmMetrics value)
        => value.UnsupportedByCategory.Values.Sum();

    private static int CompareSupport(
        OperationalLexicographicArmMetrics left,
        OperationalLexicographicArmMetrics right)
    {
        if (left.ClaimCount == 0 || right.ClaimCount == 0)
        {
            return left.ClaimCount.CompareTo(right.ClaimCount);
        }

        return ((long)left.SupportedClaims * right.ClaimCount)
            .CompareTo((long)right.SupportedClaims * left.ClaimCount);
    }

    private static string ArmList(IEnumerable<OperationalLexicographicArmMetrics> values)
        => string.Join(", ", values.Select(value => value.Arm).Order());

    private static bool HasValidAccounting(OperationalMcpRunReport attempt)
        => attempt.Investigation.RetainedArtifactBytes
                == attempt.McpExchanges.Sum(value =>
                    value.RawResponseBytes + value.ModelVisibleResponseBytes)
            && HasValidAssessmentAccounting(attempt.Assessment)
            && attempt.RetainedArtifactBytes
                == attempt.Investigation.RetainedArtifactBytes
                    + attempt.Assessment.RetainedArtifactBytes;

    internal static bool HasValidAssessmentAccounting(
        OperationalAutomatedAssessment assessment)
        => assessment.RetainedArtifactBytes
            == (assessment.FailureEvidence?.RetainedBytes ?? 0)
                + assessment.IgnoredAnnotationBytes;

    private static bool HasNoHarnessDefect(
        OperationalMcpProtocol protocol,
        OperationalMcpRunReport attempt)
    {
        var testCase = protocol.Cases.Single(value => value.Id == attempt.CaseId);
        var inventory = protocol.Inventory.Single(value =>
            value.Family == attempt.Family);
        var projection = protocol.Projections.Single(value =>
            value.Family == attempt.Family);
        return attempt.SchemaVersion == OperationalMcpProtocolLoader.CurrentRunSchemaVersion
            && attempt.ProtocolFingerprint
                == OperationalMcpProtocolLoader.ProtocolFingerprint(protocol)
            && attempt.InventoryFingerprint
                == OperationalMcpProtocolLoader.InventoryFingerprint(protocol)
            && attempt.ProjectionFingerprint
                == OperationalMcpProtocolLoader.ProjectionFingerprint(protocol)
            && attempt.ArmProjectionFingerprint
                == projection.ExpectedFingerprints[attempt.Arm]
            && attempt.CatalogFingerprint == protocol.ExpectedCatalogFingerprint
            && attempt.ToolDefinitionFingerprint
                == inventory.ExpectedToolDefinitionFingerprints![attempt.Arm]
            && attempt.Investigation.Activation.Status == AgentHarnessStageStatus.Passed
            && attempt.Investigation.Cleanup.Status == AgentHarnessStageStatus.Passed
            && attempt.Investigation.AgentExecution.Status == AgentHarnessStageStatus.Passed
            && attempt.Assessment.Stage.Status == AgentHarnessStageStatus.Passed
            && attempt.RequiredEvidenceSequenceCompleted
            && attempt.PredeclaredToolCalls == testCase.RequiredCaptures + 1
            && attempt.McpExchanges.Count >= attempt.PredeclaredToolCalls;
    }
}
