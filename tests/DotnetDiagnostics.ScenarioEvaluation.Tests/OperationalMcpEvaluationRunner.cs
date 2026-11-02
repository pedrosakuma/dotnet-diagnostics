using System.Globalization;
using System.Text.Json;

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
        var assessorConfiguration = AssessorConfiguration(protocol, configuration);
        OperationalMcpPreflightReport? assessorPreflight = null;
        using var suiteWallTime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        suiteWallTime.CancelAfter(
            TimeSpan.FromSeconds(protocol.Budgets.MaximumSuiteWallTimeSeconds));

        try
        {
            assessorPreflight = await OperationalMcpAssessor.PreflightAsync(
                assessorConfiguration,
                transport,
                protocol.Budgets.MaximumPreflightWallTimeSeconds,
                protocol.Budgets.MaximumAssessmentInputBytes,
                protocol.Budgets.MaximumAssessmentTransportOutputBytes,
                suiteWallTime.Token).ConfigureAwait(false);
            if (assessorPreflight.Stage.Status != AgentHarnessStageStatus.Passed)
            {
                return CreateAndWriteReport(
                    protocol,
                    configuration,
                    outputRoot,
                    startedAt,
                    attempts,
                    assessorPreflight,
                    suiteCancelled: false,
                    suiteBudgetExhausted: false);
            }

            await RunPartitionAsync(
                protocol,
                OperationalMcpPartition.Development,
                configuration,
                assessorConfiguration,
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
                    assessorConfiguration,
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
                assessorPreflight,
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
            assessorPreflight,
            suiteCancelled: false,
            suiteBudgetExhausted: false);
    }

    private static OperationalMcpPilotReport CreateAndWriteReport(
        OperationalMcpProtocol protocol,
        AgentModelConfiguration configuration,
        string outputRoot,
        DateTimeOffset startedAt,
        IReadOnlyList<OperationalMcpRunReport> attempts,
        OperationalMcpPreflightReport? assessorPreflight,
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
            WordingFingerprint(protocol, OperationalMcpArm.PriorWording),
            WordingFingerprint(protocol, OperationalMcpArm.HardenedWording),
            protocol.ExpectedProductFingerprint,
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
                "The diagnosing and assessing phases use different frozen models and isolated contexts; that does not eliminate shared-provider or training-data bias.",
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
                assessorPreflight is null
                    ? "The assessor preflight result was unavailable because execution was cancelled before it completed."
                    : assessorPreflight.Stage.Status == AgentHarnessStageStatus.Passed
                        ? $"Assessor preflight passed for {assessorPreflight.AssessorModel} before the first target process started."
                        : "Empirical execution did not start because the frozen assessor transport/schema preflight failed.",
            ])
        {
            AssessorPreflight = assessorPreflight,
            EvaluatorCommit = BlindedAgentHarness.CurrentProductCommit,
        };
        OperationalMcpJson.Write(Path.Combine(outputRoot, "pilot-report.json"), report);
        return report;
    }

    private static async Task RunPartitionAsync(
        OperationalMcpProtocol protocol,
        OperationalMcpPartition partition,
        AgentModelConfiguration configuration,
        AgentModelConfiguration assessorConfiguration,
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
                    assessorConfiguration,
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
        AgentModelConfiguration assessorConfiguration,
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
            async (createdGateway, token) =>
            {
                if (createdGateway is not OperationalMcpToolGateway operationalGateway)
                {
                    throw new InvalidOperationException(
                        "The operational evaluator requires its real MCP gateway.");
                }

                return await operationalGateway.CollectPredeclaredEvidenceAsync(
                    testCase,
                    token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        var assessment = await OperationalMcpAssessor.AssessAsync(
            testCase,
            investigation,
            assessorConfiguration,
            transport,
            protocol.Budgets.MaximumAssessmentWallTimeSeconds,
            protocol.Budgets.MaximumAssessmentInputBytes,
            protocol.Budgets.MaximumAssessmentTransportOutputBytes,
            cancellationToken).ConfigureAwait(false);
        var exchanges = gateway?.Exchanges ?? [];
        var predeclaredHashes = exchanges
            .Where(value => value.Origin == OperationalMcpExchangeOrigin.Predeclared)
            .Select(value => value.ModelVisibleResponseSha256)
            .ToHashSet(StringComparer.Ordinal);
        var followUp = exchanges
            .Where(value => value.Origin == OperationalMcpExchangeOrigin.FollowUp)
            .ToArray();
        var report = new OperationalMcpRunReport(
            OperationalMcpProtocolLoader.CurrentRunSchemaVersion,
            OperationalMcpProtocolLoader.ProtocolFingerprint(protocol),
            OperationalMcpProtocolLoader.InventoryFingerprint(protocol),
            OperationalMcpProtocolLoader.ProjectionFingerprint(protocol),
            WordingFingerprint(protocol, OperationalMcpArm.PriorWording),
            WordingFingerprint(protocol, OperationalMcpArm.HardenedWording),
            gateway?.CatalogFingerprint ?? "unavailable",
            gateway?.ToolDefinitionFingerprint ?? "unavailable",
            testCase.Id,
            testCase.Partition,
            testCase.Family,
            arm,
            armOrder,
            gateway?.RequiredEvidenceSequenceCompleted == true,
            investigation,
            exchanges,
            assessment)
        {
            PredeclaredToolCalls = exchanges.Count(value =>
                value.Origin == OperationalMcpExchangeOrigin.Predeclared),
            FollowUpToolCalls = followUp.Length,
            FollowUpAddedEvidence = followUp.Any(value =>
                value.Succeeded
                && !predeclaredHashes.Contains(value.ModelVisibleResponseSha256)),
        };
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
        var prior = attempts.Where(value =>
            value.Arm == OperationalMcpArm.PriorWording).ToArray();
        var hardened = attempts.Where(value =>
            value.Arm == OperationalMcpArm.HardenedWording).ToArray();
        if (prior.Length == 0 || hardened.Length == 0)
        {
            return new(
                family,
                OperationalPilotConclusion.Inconclusive,
                prior.Length,
                hardened.Length,
                [],
                [
                    heldoutEvidence
                        ? "The held-out partition did not produce both arms for this family."
                        : "Development prerequisites failed, so no held-out evidence was admitted.",
                ]);
        }

        var priorAssessable = prior.Where(IsAssessable).ToArray();
        var hardenedAssessable = hardened.Where(IsAssessable).ToArray();
        var priorSupport = SupportRate(priorAssessable);
        var hardenedSupport = SupportRate(hardenedAssessable);
        var priorUnsupported = priorAssessable.Sum(value =>
            value.Assessment.UnsupportedCertaintyCount);
        var hardenedUnsupported = hardenedAssessable.Sum(value =>
            value.Assessment.UnsupportedCertaintyCount);
        var priorAppropriate = priorAssessable.Count(value =>
            value.Assessment.Uncertainty == OperationalUncertaintyQuality.Appropriate);
        var hardenedAppropriate = hardenedAssessable.Count(value =>
            value.Assessment.Uncertainty == OperationalUncertaintyQuality.Appropriate);
        var priorUseful = priorAssessable.Count(value =>
            value.Assessment.UsefulNextSteps.Count > 0);
        var hardenedUseful = hardenedAssessable.Count(value =>
            value.Assessment.UsefulNextSteps.Count > 0);
        var priorUnhelpful = priorAssessable.Count(value =>
            value.Assessment.UnhelpfulNextSteps.Count > 0);
        var hardenedUnhelpful = hardenedAssessable.Count(value =>
            value.Assessment.UnhelpfulNextSteps.Count > 0);
        var priorUnauthorized = prior.Sum(value => value.Assessment.UnauthorizedActionAttempts);
        var hardenedUnauthorized = hardened.Sum(value => value.Assessment.UnauthorizedActionAttempts);
        var priorCalls = prior.Sum(value => value.McpExchanges.Count);
        var hardenedCalls = hardened.Sum(value => value.McpExchanges.Count);
        var priorBytes = prior.Sum(value =>
            value.McpExchanges.Sum(exchange => exchange.ModelVisibleResponseBytes));
        var hardenedBytes = hardened.Sum(value =>
            value.McpExchanges.Sum(exchange => exchange.ModelVisibleResponseBytes));
        var priorTransportBytes = prior.Sum(value =>
            value.Investigation.ModelTransportOutputBytes
            + value.Assessment.TransportOutputBytes);
        var hardenedTransportBytes = hardened.Sum(value =>
            value.Investigation.ModelTransportOutputBytes
            + value.Assessment.TransportOutputBytes);
        var priorFollowUp = prior.Sum(value => value.FollowUpToolCalls);
        var hardenedFollowUp = hardened.Sum(value => value.FollowUpToolCalls);
        var priorAddedEvidence = prior.Count(value => value.FollowUpAddedEvidence);
        var hardenedAddedEvidence = hardened.Count(value => value.FollowUpAddedEvidence);
        var priorSeconds = prior.Sum(InvestigationSeconds);
        var hardenedSeconds = hardened.Sum(InvestigationSeconds);
        var priorCompleted = prior.Count(value =>
            value.Investigation.AgentExecution.Status == AgentHarnessStageStatus.Passed);
        var hardenedCompleted = hardened.Count(value =>
            value.Investigation.AgentExecution.Status == AgentHarnessStageStatus.Passed);
        var priorAssessed = prior.Count(value =>
            value.Assessment.Stage.Status == AgentHarnessStageStatus.Passed);
        var hardenedAssessed = hardened.Count(value =>
            value.Assessment.Stage.Status == AgentHarnessStageStatus.Passed);
        var priorCitationSupport = prior.Count(CitationsPassed);
        var hardenedCitationSupport = hardened.Count(CitationsPassed);
        var priorNotAssessable = prior.Count(value => !IsAssessable(value));
        var hardenedNotAssessable = hardened.Count(value => !IsAssessable(value));
        var allAssessable = attempts.All(value =>
            IsAssessable(value)
            && value.RequiredEvidenceSequenceCompleted);
        var completeCaseInventory = prior.Length == expectedCases
            && hardened.Length == expectedCases;
        var conclusion = SelectConclusion(
            heldoutEvidence,
            allAssessable,
            completeCaseInventory,
            priorSupport,
            hardenedSupport,
            priorUnsupported,
            hardenedUnsupported,
            priorAppropriate,
            hardenedAppropriate,
            priorUseful,
            hardenedUseful,
            priorUnhelpful,
            hardenedUnhelpful,
            priorUnauthorized,
            hardenedUnauthorized);
        return new(
            family,
            conclusion,
            prior.Length,
            hardened.Length,
            [
                Delta(
                    "completed investigations",
                    $"{priorCompleted}/{prior.Length}",
                    $"{hardenedCompleted}/{hardened.Length}",
                    "Budget exhaustion and other failed attempts remain in the denominator."),
                Delta(
                    "assessor executions completed",
                    $"{priorAssessed}/{prior.Length}",
                    $"{hardenedAssessed}/{hardened.Length}",
                    "A completed assessor can still return not-assessable when the investigation produced no interpretation."),
                Delta(
                    "citation support",
                    $"{priorCitationSupport}/{prior.Length}",
                    $"{hardenedCitationSupport}/{hardened.Length}",
                    "Mechanically valid claim citations are reported separately from semantic claim support."),
                Delta(
                    "claim support",
                    SupportDisplay(prior, priorAssessable, priorSupport),
                    SupportDisplay(hardened, hardenedAssessable, hardenedSupport),
                    "Supported=1, partially supported=0.5; not a weighted global score."),
                Delta(
                    "unsupported certainty",
                    AssessmentCountDisplay(
                        priorUnsupported,
                        priorAssessable.Length,
                        prior.Length),
                    AssessmentCountDisplay(
                        hardenedUnsupported,
                        hardenedAssessable.Length,
                        hardened.Length),
                    "Lower is better; kept separate from support."),
                Delta(
                    "unsupported certainty categories",
                    UnsupportedCategoryDisplay(priorAssessable),
                    UnsupportedCategoryDisplay(hardenedAssessable),
                    "Dominance, exactness, workload equivalence, causality, severity, regression, health, optimization priority, and missing-as-zero are retained separately."),
                Delta(
                    "appropriate uncertainty",
                    AssessmentCountDisplay(
                        priorAppropriate,
                        priorAssessable.Length,
                        prior.Length),
                    AssessmentCountDisplay(
                        hardenedAppropriate,
                        hardenedAssessable.Length,
                        hardened.Length),
                    "Unnecessary abstention and overconfidence remain distinct assessor outcomes."),
                Delta(
                    "not assessable",
                    $"{priorNotAssessable}/{prior.Length}",
                    $"{hardenedNotAssessable}/{hardened.Length}",
                    "Assessment absence and malformed or failed outputs remain explicit denominators, never favorable results."),
                Delta(
                    "useful next steps",
                    AssessmentCountDisplay(
                        priorUseful,
                        priorAssessable.Length,
                        prior.Length),
                    AssessmentCountDisplay(
                        hardenedUseful,
                        hardenedAssessable.Length,
                        hardened.Length),
                    "Attempts with at least one useful next step; verbosity does not increase the count."),
                Delta(
                    "unhelpful next steps",
                    AssessmentCountDisplay(
                        priorUnhelpful,
                        priorAssessable.Length,
                        prior.Length),
                    AssessmentCountDisplay(
                        hardenedUnhelpful,
                        hardenedAssessable.Length,
                        hardened.Length),
                    "Attempts with at least one unhelpful next step; lower is better."),
                Delta(
                    "unauthorized actions",
                    priorUnauthorized.ToString(CultureInfo.InvariantCulture),
                    hardenedUnauthorized.ToString(CultureInfo.InvariantCulture),
                    "Mechanically derived from gateway approval records."),
                Delta(
                    "operational tool calls",
                    priorCalls.ToString(CultureInfo.InvariantCulture),
                    hardenedCalls.ToString(CultureInfo.InvariantCulture),
                    "Counts real retained MCP exchanges, including attempts whose final model turn timed out."),
                Delta(
                    "model-visible MCP payload bytes",
                    priorBytes.ToString(CultureInfo.InvariantCulture),
                    hardenedBytes.ToString(CultureInfo.InvariantCulture),
                    "MCP payload volume is bounded and reported separately from model transport output."),
                Delta(
                    "model transport output bytes",
                    priorTransportBytes.ToString(CultureInfo.InvariantCulture),
                    hardenedTransportBytes.ToString(CultureInfo.InvariantCulture),
                    "Diagnosing and assessor transport output is bounded independently from MCP payloads."),
                Delta(
                    "follow-up MCP calls",
                    $"{priorFollowUp}; added evidence in {priorAddedEvidence}/{prior.Length} attempts",
                    $"{hardenedFollowUp}; added evidence in {hardenedAddedEvidence}/{hardened.Length} attempts",
                    "Follow-up calls are separated from the frozen evaluator-owned evidence sequence."),
                Delta(
                    "investigation wall seconds",
                    priorSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    hardenedSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    "Includes failed and budget-exhausted investigation attempts."),
                Delta(
                    "available token and cost reporting",
                    UsageDisplay(prior),
                    UsageDisplay(hardened),
                    "Provider-reported usage remains separate from hard calls, turns, bytes, capture time, and wall-time limits."),
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
        double priorSupport,
        double hardenedSupport,
        int priorUnsupported,
        int hardenedUnsupported,
        int priorAppropriate,
        int hardenedAppropriate,
        int priorUseful,
        int hardenedUseful,
        int priorUnhelpful,
        int hardenedUnhelpful,
        int priorUnauthorized,
        int hardenedUnauthorized)
        => !heldoutEvidence || !allAssessable || !completeCaseInventory
            ? OperationalPilotConclusion.Inconclusive
            : hardenedUnauthorized > priorUnauthorized
                || hardenedUnsupported > priorUnsupported
                || hardenedUnhelpful > priorUnhelpful
                ? OperationalPilotConclusion.NoGo
                : hardenedUnsupported < priorUnsupported
                  && hardenedSupport >= priorSupport
                  && hardenedAppropriate >= priorAppropriate
                  && hardenedUseful >= priorUseful
                  && hardenedUnhelpful <= priorUnhelpful
                    ? OperationalPilotConclusion.Go
                    : OperationalPilotConclusion.Inconclusive;

    private static bool IsAssessable(OperationalMcpRunReport attempt)
        => attempt.Investigation.AgentExecution.Status == AgentHarnessStageStatus.Passed
            && attempt.Investigation.Assessment.Stage.Status == AgentHarnessStageStatus.Passed
            && attempt.Assessment.Stage.Status == AgentHarnessStageStatus.Passed;

    private static bool CitationsPassed(OperationalMcpRunReport attempt)
        => attempt.Investigation.Assessment.Stage.Status == AgentHarnessStageStatus.Passed
            && attempt.Investigation.Assessment.InvalidEvidenceLocations.Count == 0;

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
        string priorWording,
        string hardenedWording,
        string interpretation)
        => new(dimension, priorWording, hardenedWording, interpretation);

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
        var inputAvailable = attempts.Count(value =>
            value.Investigation.TotalInputTokens.HasValue);
        var outputAvailable = attempts.Count(value =>
            value.Investigation.TotalOutputTokens.HasValue);
        var costAvailable = attempts.Count(value =>
            value.Investigation.EstimatedCostUsd.HasValue);
        return $"input {inputTokens} ({inputAvailable}/{attempts.Length}); "
            + $"output {outputTokens} ({outputAvailable}/{attempts.Length}); "
            + $"cost USD {costs.ToString("F4", CultureInfo.InvariantCulture)} "
            + $"({costAvailable}/{attempts.Length})";
    }

    private static string UnsupportedCategoryDisplay(
        OperationalMcpRunReport[] attempts)
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

    private static string WordingFingerprint(
        OperationalMcpProtocol protocol,
        OperationalMcpArm arm)
        => OperationalMcpResponseProjector.WordingFingerprint(
            protocol.Projections.Single(),
            arm);

    private static double InvestigationSeconds(OperationalMcpRunReport attempt)
        => Math.Max(
            0,
            (attempt.Investigation.CompletedAtUtc - attempt.Investigation.StartedAtUtc)
            .TotalSeconds);

    private static AgentModelConfiguration AssessorConfiguration(
        OperationalMcpProtocol protocol,
        AgentModelConfiguration configuration)
    {
        var models = protocol.Models
            ?? throw new InvalidDataException(
                "The frozen operational MCP protocol must declare diagnosing and assessor models.");
        if (!string.Equals(configuration.Provider, models.Provider, StringComparison.Ordinal)
            || !string.Equals(configuration.Model, models.DiagnosingModel, StringComparison.Ordinal)
            || !string.Equals(configuration.ContextTier, models.ContextTier, StringComparison.Ordinal)
            || configuration.MaximumResponseBytes
                < protocol.Budgets.MaximumModelTransportOutputBytes
            || configuration.MaximumResponseBytes
                < protocol.Budgets.MaximumAssessmentTransportOutputBytes)
        {
            throw new InvalidOperationException(
                $"The configured diagnosing model '{configuration.Provider}/{configuration.Model}' "
                + $"with context '{configuration.ContextTier}' does not match the frozen "
                + $"'{models.Provider}/{models.DiagnosingModel}' with context '{models.ContextTier}', "
                + "or its transport response cap is below the frozen protocol budget.");
        }

        return configuration with
        {
            Model = models.AssessorModel,
            Version = null,
        };
    }
}
