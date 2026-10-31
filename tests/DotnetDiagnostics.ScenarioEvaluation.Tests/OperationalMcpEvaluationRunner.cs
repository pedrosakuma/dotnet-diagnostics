using System.Globalization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class OperationalMcpEvaluationRunner
{
    public static async Task<OperationalMcpPilotReport> RunAsync(
        OperationalMcpProtocol protocol,
        AgentModelConfiguration configuration,
        IAgentModelTransport transport,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        if (Directory.Exists(outputRoot)
            && Directory.EnumerateFileSystemEntries(outputRoot).Any())
        {
            throw new InvalidOperationException(
                "The operational MCP output root must be empty so frozen attempts cannot be overwritten or retried in place.");
        }

        Directory.CreateDirectory(outputRoot);
        var startedAt = DateTimeOffset.UtcNow;
        var attempts = new List<OperationalMcpRunReport>();
        using var suiteWallTime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        suiteWallTime.CancelAfter(
            TimeSpan.FromSeconds(protocol.Budgets.MaximumSuiteWallTimeSeconds));

        try
        {
            await RunPartitionAsync(
                protocol,
                OperationalMcpPartition.Development,
                configuration,
                transport,
                outputRoot,
                attempts,
                suiteWallTime.Token).ConfigureAwait(false);

            if (DevelopmentPrerequisitesPassed(attempts))
            {
                await RunPartitionAsync(
                    protocol,
                    OperationalMcpPartition.Heldout,
                    configuration,
                    transport,
                    outputRoot,
                    attempts,
                    suiteWallTime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (suiteWallTime.IsCancellationRequested)
        {
            var report = CreateAndWriteReport(
                protocol,
                configuration,
                outputRoot,
                startedAt,
                attempts,
                suiteCancelled: true,
                suiteBudgetExhausted: !cancellationToken.IsCancellationRequested);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return report;
        }

        return CreateAndWriteReport(
            protocol,
            configuration,
            outputRoot,
            startedAt,
            attempts,
            suiteCancelled: false,
            suiteBudgetExhausted: false);
    }

    private static OperationalMcpPilotReport CreateAndWriteReport(
        OperationalMcpProtocol protocol,
        AgentModelConfiguration configuration,
        string outputRoot,
        DateTimeOffset startedAt,
        IReadOnlyList<OperationalMcpRunReport> attempts,
        bool suiteCancelled,
        bool suiteBudgetExhausted)
    {
        var heldout = attempts
            .Where(attempt => attempt.Partition == OperationalMcpPartition.Heldout)
            .ToArray();
        var development = attempts
            .Where(attempt => attempt.Partition == OperationalMcpPartition.Development)
            .ToArray();
        var conclusionAttempts = heldout.Length > 0 ? heldout : development;
        var conclusions = Enum.GetValues<OperationalMcpFamily>()
            .Where(family => protocol.Inventory.Any(entry =>
                entry.Family == family
                && entry.Eligibility == OperationalMcpEligibility.Eligible))
            .Select(family => Conclude(
                family,
                conclusionAttempts.Where(value => value.Family == family).ToArray(),
                heldout.Length > 0,
                protocol.Cases.Count(testCase =>
                    testCase.Partition == (heldout.Length > 0
                        ? OperationalMcpPartition.Heldout
                        : OperationalMcpPartition.Development)
                    && testCase.Family == family)))
            .ToArray();
        var catalogFingerprints = attempts.Select(value => value.CatalogFingerprint)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var report = new OperationalMcpPilotReport(
            OperationalMcpProtocolLoader.CurrentPilotSchemaVersion,
            OperationalMcpProtocolLoader.ProtocolFingerprint(protocol),
            OperationalMcpProtocolLoader.InventoryFingerprint(protocol),
            OperationalMcpProtocolLoader.ProjectionFingerprint(protocol),
            BlindedAgentHarness.CurrentProductCommit,
            configuration.Provider,
            configuration.Model,
            configuration.Version ?? "unavailable",
            configuration.Provider == "github-copilot-cli"
                ? "github-copilot-cli"
                : "openai-compatible-http",
            configuration.TransportVersion ?? "unavailable",
            startedAt,
            DateTimeOffset.UtcNow,
            attempts,
            conclusions,
            [
                "The pilot is bounded operational evidence, not a model leaderboard, correctness proof, PR gate, or automatic product-adoption verdict.",
                "The diagnosing and assessing phases may use the same model family; isolated context does not eliminate shared-model bias.",
                "Fresh processes and captures reduce dependence but do not eliminate platform, scheduler, JIT, EventPipe, symbol, or workload variability.",
                "Sample recognition cannot be ruled out because repository-owned workload symbols may appear in MCP evidence.",
                "#921 remains human calibration; this automated assessment does not impersonate or complete it.",
                "#997 remains tool-disabled retained-packet reanalysis and is not completed by this live MCP protocol.",
                catalogFingerprints.Length == 1
                    ? $"All attempts used one selected-tool catalog fingerprint: {catalogFingerprints[0]}."
                    : $"Catalog drift was observed across attempts: {string.Join(", ", catalogFingerprints)}.",
                heldout.Length == 0
                    ? "Held-out execution was blocked because one or more frozen development prerequisites failed."
                    : "The finite held-out partition ran once per frozen case and arm; failed attempts were not retried.",
                suiteCancelled
                    ? suiteBudgetExhausted
                        ? $"The frozen {protocol.Budgets.MaximumSuiteWallTimeSeconds}-second suite budget stopped additional arms; every completed arm and this partial pilot report were retained."
                        : "External suite cancellation stopped additional arms; every completed arm and this partial pilot report were retained."
                    : "The evaluator completed its permitted partition sequence without external suite cancellation.",
            ]);
        OperationalMcpJson.Write(Path.Combine(outputRoot, "pilot-report.json"), report);
        return report;
    }

    private static async Task RunPartitionAsync(
        OperationalMcpProtocol protocol,
        OperationalMcpPartition partition,
        AgentModelConfiguration configuration,
        IAgentModelTransport transport,
        string outputRoot,
        List<OperationalMcpRunReport> attempts,
        CancellationToken cancellationToken)
    {
        foreach (var testCase in protocol.Cases.Where(value => value.Partition == partition))
        {
            var order = OperationalMcpProtocolLoader.ArmOrder(protocol, testCase.Id);
            for (var armIndex = 0; armIndex < order.Count; armIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts.Add(await RunArmAsync(
                    protocol,
                    testCase,
                    order[armIndex],
                    armIndex + 1,
                    configuration,
                    transport,
                    outputRoot,
                    cancellationToken).ConfigureAwait(false));
            }
        }
    }

    private static async Task<OperationalMcpRunReport> RunArmAsync(
        OperationalMcpProtocol protocol,
        OperationalMcpCase testCase,
        OperationalMcpArm arm,
        int armOrder,
        AgentModelConfiguration configuration,
        IAgentModelTransport transport,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var attemptDirectory = Path.Combine(
            outputRoot,
            testCase.Partition.ToString().ToLowerInvariant(),
            testCase.Id,
            arm.ToString().ToLowerInvariant());
        Directory.CreateDirectory(attemptDirectory);
        var investigationPath = Path.Combine(attemptDirectory, "investigation.json");
        var request = new AgentHarnessRequest(
            OperationalMcpProtocolLoader.ToScenarioManifest(testCase),
            configuration,
            protocol.Budgets.ToAgentBudget(),
            investigationPath,
            $"operational-mcp/{testCase.Family}/{arm}");
        OperationalMcpToolGateway? gateway = null;
        var investigation = await BlindedAgentHarness.RunAsync(
            request,
            transport,
            async (processId, budget, token) =>
            {
                gateway = await OperationalMcpToolGateway.StartAsync(
                    processId,
                    budget,
                    testCase.Family,
                    testCase.RequiredCaptures,
                    arm,
                    protocol,
                    Path.Combine(attemptDirectory, "mcp-artifacts"),
                    token).ConfigureAwait(false);
                return gateway;
            },
            cancellationToken).ConfigureAwait(false);
        var assessment = await OperationalMcpAssessor.AssessAsync(
            testCase,
            investigation,
            configuration,
            transport,
            protocol.Budgets.MaximumAssessmentWallTimeSeconds,
            cancellationToken).ConfigureAwait(false);
        var report = new OperationalMcpRunReport(
            OperationalMcpProtocolLoader.CurrentRunSchemaVersion,
            OperationalMcpProtocolLoader.ProtocolFingerprint(protocol),
            OperationalMcpProtocolLoader.InventoryFingerprint(protocol),
            OperationalMcpProtocolLoader.ProjectionFingerprint(protocol),
            gateway?.CatalogFingerprint ?? "unavailable",
            gateway?.ToolDefinitionFingerprint ?? "unavailable",
            testCase.Id,
            testCase.Partition,
            testCase.Family,
            arm,
            armOrder,
            gateway?.RequiredEvidenceSequenceCompleted == true,
            investigation,
            gateway?.Exchanges ?? [],
            assessment);
        OperationalMcpJson.Write(Path.Combine(attemptDirectory, "run-report.json"), report);
        return report;
    }

    private static bool DevelopmentPrerequisitesPassed(
        IReadOnlyList<OperationalMcpRunReport> attempts)
    {
        var development = attempts
            .Where(value => value.Partition == OperationalMcpPartition.Development)
            .ToArray();
        return development.Length > 0
            && development.All(attempt =>
                AttemptPassesDevelopmentPrerequisites(
                    attempt.Investigation.Activation.Status == AgentHarnessStageStatus.Passed,
                    attempt.Investigation.Cleanup.Status == AgentHarnessStageStatus.Passed,
                    attempt.Investigation.AgentExecution.Status == AgentHarnessStageStatus.Passed,
                    attempt.Investigation.Assessment.Stage.Status == AgentHarnessStageStatus.Passed,
                    attempt.Assessment.Stage.Status == AgentHarnessStageStatus.Passed,
                    attempt.RequiredEvidenceSequenceCompleted,
                    attempt.McpExchanges.Count > 0));
    }

    internal static bool AttemptPassesDevelopmentPrerequisites(
        bool activationPassed,
        bool cleanupPassed,
        bool investigationPassed,
        bool citationsPassed,
        bool automatedAssessmentPassed,
        bool requiredEvidenceSequenceCompleted,
        bool hasMcpExchanges)
        => activationPassed
            && cleanupPassed
            && investigationPassed
            && citationsPassed
            && automatedAssessmentPassed
            && requiredEvidenceSequenceCompleted
            && hasMcpExchanges;

    private static OperationalFamilyConclusion Conclude(
        OperationalMcpFamily family,
        IReadOnlyList<OperationalMcpRunReport> attempts,
        bool heldoutEvidence,
        int expectedCases)
    {
        var control = attempts.Where(value => value.Arm == OperationalMcpArm.Control).ToArray();
        var indicator = attempts.Where(value => value.Arm == OperationalMcpArm.Indicator).ToArray();
        if (control.Length == 0 || indicator.Length == 0)
        {
            return new(
                family,
                OperationalPilotConclusion.Inconclusive,
                control.Length,
                indicator.Length,
                [],
                [
                    heldoutEvidence
                        ? "The held-out partition did not produce both arms for this family."
                        : "Development prerequisites failed, so no held-out evidence was admitted.",
                ]);
        }

        var controlAssessable = control.Where(IsAssessable).ToArray();
        var indicatorAssessable = indicator.Where(IsAssessable).ToArray();
        var controlSupport = SupportRate(controlAssessable);
        var indicatorSupport = SupportRate(indicatorAssessable);
        var controlUnsupported = controlAssessable.Sum(value =>
            value.Assessment.UnsupportedCertaintyCount);
        var indicatorUnsupported = indicatorAssessable.Sum(value =>
            value.Assessment.UnsupportedCertaintyCount);
        var controlAppropriate = controlAssessable.Count(value =>
            value.Assessment.Uncertainty == OperationalUncertaintyQuality.Appropriate);
        var indicatorAppropriate = indicatorAssessable.Count(value =>
            value.Assessment.Uncertainty == OperationalUncertaintyQuality.Appropriate);
        var controlUseful = controlAssessable.Count(value =>
            value.Assessment.UsefulNextSteps.Count > 0);
        var indicatorUseful = indicatorAssessable.Count(value =>
            value.Assessment.UsefulNextSteps.Count > 0);
        var controlUnhelpful = controlAssessable.Count(value =>
            value.Assessment.UnhelpfulNextSteps.Count > 0);
        var indicatorUnhelpful = indicatorAssessable.Count(value =>
            value.Assessment.UnhelpfulNextSteps.Count > 0);
        var controlUnauthorized = control.Sum(value => value.Assessment.UnauthorizedActionAttempts);
        var indicatorUnauthorized = indicator.Sum(value => value.Assessment.UnauthorizedActionAttempts);
        var controlCalls = control.Sum(value => value.McpExchanges.Count);
        var indicatorCalls = indicator.Sum(value => value.McpExchanges.Count);
        var controlBytes = control.Sum(value =>
            value.McpExchanges.Sum(exchange => exchange.ModelVisibleResponseBytes));
        var indicatorBytes = indicator.Sum(value =>
            value.McpExchanges.Sum(exchange => exchange.ModelVisibleResponseBytes));
        var controlSeconds = control.Sum(InvestigationSeconds);
        var indicatorSeconds = indicator.Sum(InvestigationSeconds);
        var controlCompleted = control.Count(value =>
            value.Investigation.AgentExecution.Status == AgentHarnessStageStatus.Passed);
        var indicatorCompleted = indicator.Count(value =>
            value.Investigation.AgentExecution.Status == AgentHarnessStageStatus.Passed);
        var controlAssessed = control.Count(value =>
            value.Assessment.Stage.Status == AgentHarnessStageStatus.Passed);
        var indicatorAssessed = indicator.Count(value =>
            value.Assessment.Stage.Status == AgentHarnessStageStatus.Passed);
        var allAssessable = attempts.All(value =>
            IsAssessable(value)
            && value.RequiredEvidenceSequenceCompleted);
        var completeCaseInventory = control.Length == expectedCases
            && indicator.Length == expectedCases;
        var conclusion = SelectConclusion(
            heldoutEvidence,
            allAssessable,
            completeCaseInventory,
            controlSupport,
            indicatorSupport,
            controlUnsupported,
            indicatorUnsupported,
            controlAppropriate,
            indicatorAppropriate,
            controlUseful,
            indicatorUseful,
            controlUnhelpful,
            indicatorUnhelpful,
            controlUnauthorized,
            indicatorUnauthorized);
        return new(
            family,
            conclusion,
            control.Length,
            indicator.Length,
            [
                Delta(
                    "completed investigations",
                    $"{controlCompleted}/{control.Length}",
                    $"{indicatorCompleted}/{indicator.Length}",
                    "Budget exhaustion and other failed attempts remain in the denominator."),
                Delta(
                    "assessor executions completed",
                    $"{controlAssessed}/{control.Length}",
                    $"{indicatorAssessed}/{indicator.Length}",
                    "A completed assessor can still return not-assessable when the investigation produced no interpretation."),
                Delta(
                    "claim support",
                    SupportDisplay(control, controlAssessable, controlSupport),
                    SupportDisplay(indicator, indicatorAssessable, indicatorSupport),
                    "Supported=1, partially supported=0.5; not a weighted global score."),
                Delta(
                    "unsupported certainty",
                    AssessmentCountDisplay(
                        controlUnsupported,
                        controlAssessable.Length,
                        control.Length),
                    AssessmentCountDisplay(
                        indicatorUnsupported,
                        indicatorAssessable.Length,
                        indicator.Length),
                    "Lower is better; kept separate from support."),
                Delta(
                    "appropriate uncertainty",
                    AssessmentCountDisplay(
                        controlAppropriate,
                        controlAssessable.Length,
                        control.Length),
                    AssessmentCountDisplay(
                        indicatorAppropriate,
                        indicatorAssessable.Length,
                        indicator.Length),
                    "Unnecessary abstention and overconfidence remain distinct assessor outcomes."),
                Delta(
                    "useful next steps",
                    AssessmentCountDisplay(
                        controlUseful,
                        controlAssessable.Length,
                        control.Length),
                    AssessmentCountDisplay(
                        indicatorUseful,
                        indicatorAssessable.Length,
                        indicator.Length),
                    "Attempts with at least one useful next step; verbosity does not increase the count."),
                Delta(
                    "unhelpful next steps",
                    AssessmentCountDisplay(
                        controlUnhelpful,
                        controlAssessable.Length,
                        control.Length),
                    AssessmentCountDisplay(
                        indicatorUnhelpful,
                        indicatorAssessable.Length,
                        indicator.Length),
                    "Attempts with at least one unhelpful next step; lower is better."),
                Delta(
                    "unauthorized actions",
                    controlUnauthorized.ToString(CultureInfo.InvariantCulture),
                    indicatorUnauthorized.ToString(CultureInfo.InvariantCulture),
                    "Mechanically derived from gateway approval records."),
                Delta(
                    "operational tool calls",
                    controlCalls.ToString(CultureInfo.InvariantCulture),
                    indicatorCalls.ToString(CultureInfo.InvariantCulture),
                    "Counts real retained MCP exchanges, including attempts whose final model turn timed out."),
                Delta(
                    "model-visible MCP bytes",
                    controlBytes.ToString(CultureInfo.InvariantCulture),
                    indicatorBytes.ToString(CultureInfo.InvariantCulture),
                    "Response-volume cost is reported separately from diagnostic quality."),
                Delta(
                    "investigation wall seconds",
                    controlSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    indicatorSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    "Includes failed and budget-exhausted investigation attempts."),
            ],
            [
                "The conclusion is a frozen pilot evidence label, not an automatic product-adoption verdict.",
                "Small finite samples cannot establish universal model quality.",
                "Provider token/cost usage may be unavailable; hard turns, calls, wall time, capture seconds, bytes, and attempts remain bounded.",
                heldoutEvidence
                    ? completeCaseInventory
                        ? "The dimensions summarize the finite held-out partition."
                        : "The held-out case-class inventory was incomplete, so no GO/NO-GO label was admitted."
                    : "The dimensions summarize development attempts only; held-out execution was blocked.",
            ]);
    }

    private static double SupportRate(IReadOnlyList<OperationalMcpRunReport> attempts)
    {
        var claims = attempts.SelectMany(value => value.Assessment.Claims).ToArray();
        if (claims.Length == 0)
        {
            return 0;
        }

        return claims.Sum(claim => claim.Support switch
        {
            OperationalClaimSupport.Supported => 1,
            OperationalClaimSupport.PartiallySupported => 0.5,
            _ => 0,
        }) / claims.Length;
    }

    internal static OperationalPilotConclusion SelectConclusion(
        bool heldoutEvidence,
        bool allAssessable,
        bool completeCaseInventory,
        double controlSupport,
        double indicatorSupport,
        int controlUnsupported,
        int indicatorUnsupported,
        int controlAppropriate,
        int indicatorAppropriate,
        int controlUseful,
        int indicatorUseful,
        int controlUnhelpful,
        int indicatorUnhelpful,
        int controlUnauthorized,
        int indicatorUnauthorized)
        => !heldoutEvidence || !allAssessable || !completeCaseInventory
            ? OperationalPilotConclusion.Inconclusive
            : indicatorUnauthorized > controlUnauthorized
                || indicatorUnsupported > controlUnsupported
                || indicatorUnhelpful > controlUnhelpful
                ? OperationalPilotConclusion.NoGo
                : indicatorSupport > controlSupport + 0.10
                  && indicatorUnsupported <= controlUnsupported
                  && indicatorAppropriate >= controlAppropriate
                  && indicatorUseful >= controlUseful
                  && indicatorUnhelpful <= controlUnhelpful
                    ? OperationalPilotConclusion.Go
                    : OperationalPilotConclusion.Inconclusive;

    private static bool IsAssessable(OperationalMcpRunReport attempt)
        => attempt.Investigation.AgentExecution.Status == AgentHarnessStageStatus.Passed
            && attempt.Investigation.Assessment.Stage.Status == AgentHarnessStageStatus.Passed
            && attempt.Assessment.Stage.Status == AgentHarnessStageStatus.Passed;

    private static string SupportDisplay(
        OperationalMcpRunReport[] attempts,
        OperationalMcpRunReport[] assessable,
        double supportRate)
    {
        var claimCount = assessable.Sum(value => value.Assessment.Claims.Count);
        return claimCount == 0
            ? $"not assessable ({assessable.Length}/{attempts.Length} attempts assessed)"
            : $"{supportRate.ToString("P1", CultureInfo.InvariantCulture)} across "
                + $"{claimCount.ToString(CultureInfo.InvariantCulture)} claims; "
                + $"{assessable.Length}/{attempts.Length} attempts assessed";
    }

    private static string AssessmentCountDisplay(
        int value,
        int assessableAttempts,
        int totalAttempts)
        => assessableAttempts == 0
            ? $"not assessable (0/{totalAttempts} attempts assessed)"
            : $"{value.ToString(CultureInfo.InvariantCulture)} across "
                + $"{assessableAttempts}/{totalAttempts} assessed attempts";

    private static OperationalDimensionDelta Delta(
        string dimension,
        string control,
        string indicator,
        string interpretation)
        => new(dimension, control, indicator, interpretation);

    private static double InvestigationSeconds(OperationalMcpRunReport attempt)
        => Math.Max(
            0,
            (attempt.Investigation.CompletedAtUtc - attempt.Investigation.StartedAtUtc)
            .TotalSeconds);
}
