using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class OperationalMcpProtocolTests
{
    [Fact]
    public void FrozenProtocol_DeclaresOnlyTruthfullyReachableMcpFamilies()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();

        protocol.Inventory
            .Where(value => value.Eligibility == OperationalMcpEligibility.Eligible)
            .Select(value => value.Family)
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    OperationalMcpFamily.AllocationReproducibility,
                    OperationalMcpFamily.CpuHotspotCaptureProfiles,
                });
        protocol.Inventory.Single(value =>
                value.Family == OperationalMcpFamily.CpuHotspotMovementComparison)
            .Eligibility.Should().Be(OperationalMcpEligibility.Excluded);
        protocol.Inventory.Single(value =>
                value.Family == OperationalMcpFamily.ContentionConcentration)
            .Reason.Should().Contain("omits");
        protocol.Inventory.Single(value =>
                value.Family == OperationalMcpFamily.ThreadPoolBacklog)
            .Reason.Should().Contain("cannot truthfully populate");
        protocol.Cases.Should().Contain(value =>
            value.Partition == OperationalMcpPartition.Development);
        protocol.Cases.Should().Contain(value =>
            value.Partition == OperationalMcpPartition.Heldout);
        var manifest = OperationalMcpProtocolLoader.ToScenarioManifest(
            protocol.Cases.Single(value => value.Id == "dev-allocation-primary"));
        manifest.ReportedSymptom.Should().Contain(
            "request exactly 3 fresh collect_sample(kind=\"allocation\") captures");
        manifest.ReportedSymptom.Should().Contain(
            "view=\"allocation-reproducibility\" before returning a diagnosis");
        manifest.ReportedSymptom.Should().NotContain("dev-allocation-primary");
        manifest.ReportedSymptom.Should().NotContain("control");
        manifest.ReportedSymptom.Should().NotContain("indicator");
        manifest.ReportedSymptom.Should().NotContain("projection");
        foreach (var family in protocol.Inventory
                     .Where(value => value.Eligibility == OperationalMcpEligibility.Eligible)
                     .Select(value => value.Family))
        {
            protocol.Cases.Where(value =>
                    value.Partition == OperationalMcpPartition.Heldout
                    && value.Family == family)
                .Select(value => value.CaseClass)
                .Should().BeEquivalentTo(Enum.GetValues<OperationalMcpCaseClass>());
        }

        OperationalMcpProtocolLoader.ProtocolFingerprint(protocol)
            .Should().Be(
                "1b2ef845bd52287994d822decfca71a073114c72a2c7b5602a70d8e570779879");
        OperationalMcpProtocolLoader.InventoryFingerprint(protocol)
            .Should().Be(
                "c01e7f13107854fdc4f8f9aae5d60d6b0cd4f33a3728b5027c0d88947e57947e");
        OperationalMcpProtocolLoader.ProjectionFingerprint(protocol)
            .Should().Be(
                "ea96a3a44e6326df5e1d58b8a88695ec4dc5307a77d871a0cb2a190674a8010e");
    }

    [Fact]
    public async Task Assessor_MissingRequiredFieldReturnsFailedAssessment()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var transport = new SingleTurnTransport(
            new AgentModelTurn(
                "{}",
                """{"uncertainty":"notAssessable"}""",
                [],
                new AgentModelUsage(null, null, null),
                null));

        var assessment = await OperationalMcpAssessor.AssessAsync(
            protocol.Cases[0],
            CreateInvestigation(),
            ModelConfiguration(),
            transport,
            5,
            CancellationToken.None);

        assessment.Stage.Status.Should().Be(AgentHarnessStageStatus.Failed);
        assessment.Stage.FailureKind.Should().Be(AgentHarnessFailureKind.Assessment);
        assessment.Stage.Detail.Should().Contain("claims").And.Contain("missing");
    }

    [Fact]
    public async Task Assessor_ExternalCancellationReturnsFailedAssessment()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var assessment = await OperationalMcpAssessor.AssessAsync(
            protocol.Cases[0],
            CreateInvestigation(),
            ModelConfiguration(),
            new NeverTransport(),
            5,
            cancellation.Token);

        assessment.Stage.Status.Should().Be(AgentHarnessStageStatus.Failed);
        assessment.Stage.FailureKind.Should().Be(AgentHarnessFailureKind.Assessment);
        assessment.Stage.Detail.Should().Contain("suite was cancelled");
    }

    [Fact]
    public void ArmOrder_IsDeterministicAndContainsBothArms()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var first = OperationalMcpProtocolLoader.ArmOrder(
            protocol,
            "heldout-allocation-primary");
        var second = OperationalMcpProtocolLoader.ArmOrder(
            protocol,
            "heldout-allocation-primary");

        first.Should().Equal(second);
        first.Should().BeEquivalentTo(
            new[] { OperationalMcpArm.Control, OperationalMcpArm.Indicator });
    }

    [Fact]
    public void Projection_RemovesOnlySuccessfulMatchingIndicatorData()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var projection = protocol.Projections.Single(value =>
            value.Family == OperationalMcpFamily.AllocationReproducibility);
        const string raw =
            """
            {"summary":"available","hints":[{"tool":"query_snapshot"}],"error":null,"data":{"schema":"dotnet-diagnostics/allocation-reproducibility/v1","availability":"available"},"handle":"h-1"}
            """;

        var control = OperationalMcpResponseProjector.Apply(
            raw,
            OperationalMcpArm.Control,
            projection,
            out var projected,
            out var schema);
        var indicator = OperationalMcpResponseProjector.Apply(
            raw,
            OperationalMcpArm.Indicator,
            projection,
            out var indicatorProjected,
            out _);

        projected.Should().BeTrue();
        schema.Should().Be(projection.Schema);
        indicatorProjected.Should().BeFalse();
        indicator.Should().Be(raw);
        using var document = JsonDocument.Parse(control);
        document.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("handle").GetString().Should().Be("h-1");
        document.RootElement.GetProperty("summary").GetString()
            .Should().Be(projection.NeutralSummary);
        document.RootElement.TryGetProperty("evaluationProjection", out _).Should().BeFalse();
        control.Should().NotContain("control arm");
        control.Should().NotContain("blinded");
    }

    [Fact]
    public void Projection_PreservesFailuresByteForByte()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var projection = protocol.Projections[0];
        const string failure =
            """{"summary":"failed","hints":[],"error":{"kind":"Unavailable"},"data":{"schema":"dotnet-diagnostics/allocation-reproducibility/v1"}}""";

        OperationalMcpResponseProjector.Apply(
                failure,
                OperationalMcpArm.Control,
                projection,
                out var projected,
                out _)
            .Should().Be(failure);
        projected.Should().BeFalse();
    }

    [Fact]
    public void Projection_NeutralizesBothEligibleSchemasWithoutArmLeakage()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        foreach (var projection in protocol.Projections)
        {
            var raw = JsonSerializer.Serialize(new
            {
                summary = "indicator-derived summary",
                hints = new[] { new { tool = "query_snapshot" } },
                error = (object?)null,
                data = new { schema = projection.Schema, availability = "available" },
                cancelled = false,
            });

            var control = OperationalMcpResponseProjector.Apply(
                raw,
                OperationalMcpArm.Control,
                projection,
                out var projected,
                out _);

            projected.Should().BeTrue();
            control.Should().NotContain("control");
            control.Should().NotContain("indicator");
            control.Should().NotContain("blinded");
            control.Should().NotContain("projection");
            using var document = JsonDocument.Parse(control);
            document.RootElement.GetProperty("summary").GetString()
                .Should().Be(projection.NeutralSummary);
            document.RootElement.GetProperty("data").ValueKind
                .Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public void UnauthorizedActionCount_IgnoresValidationBudgetAndTransportFailures()
    {
        var approvals = new[]
        {
            Approval("invalid", "not-required-read-only", executed: false),
            Approval("budget", "not-required-read-only", executed: false),
            Approval("transport", "not-required-read-only", executed: false),
            Approval("unauthorized", "denied-unauthorized", executed: false),
            Approval("success", "not-required-read-only", executed: true),
        };

        OperationalMcpAssessor.CountUnauthorizedAttempts(approvals).Should().Be(1);
    }

    [Fact]
    public void DevelopmentGate_RequiresEveryFrozenPrerequisite()
    {
        OperationalMcpEvaluationRunner.AttemptPassesDevelopmentPrerequisites(
                activationPassed: true,
                cleanupPassed: true,
                investigationPassed: true,
                citationsPassed: true,
                automatedAssessmentPassed: true,
                requiredEvidenceSequenceCompleted: true,
                hasMcpExchanges: true)
            .Should().BeTrue();

        for (var failed = 0; failed < 7; failed++)
        {
            var prerequisites = Enumerable.Repeat(true, 7).ToArray();
            prerequisites[failed] = false;
            OperationalMcpEvaluationRunner.AttemptPassesDevelopmentPrerequisites(
                    prerequisites[0],
                    prerequisites[1],
                    prerequisites[2],
                    prerequisites[3],
                    prerequisites[4],
                    prerequisites[5],
                    prerequisites[6])
                .Should().BeFalse();
        }
    }

    [Fact]
    public void Conclusion_RequiresCompleteAssessableHeldoutAndKeepsDimensionsSeparate()
    {
        OperationalMcpEvaluationRunner.SelectConclusion(
                heldoutEvidence: false,
                allAssessable: true,
                completeCaseInventory: true,
                controlSupport: 0,
                indicatorSupport: 1,
                controlUnsupported: 0,
                indicatorUnsupported: 0,
                controlAppropriate: 0,
                indicatorAppropriate: 4,
                controlUseful: 0,
                indicatorUseful: 4,
                controlUnhelpful: 0,
                indicatorUnhelpful: 0,
                controlUnauthorized: 0,
                indicatorUnauthorized: 0)
            .Should().Be(OperationalPilotConclusion.Inconclusive);
        OperationalMcpEvaluationRunner.SelectConclusion(
                true, true, true,
                0.4, 0.7,
                1, 1,
                2, 3,
                2, 3,
                1, 1,
                0, 0)
            .Should().Be(OperationalPilotConclusion.Go);
        OperationalMcpEvaluationRunner.SelectConclusion(
                true, true, true,
                0.4, 0.7,
                1, 2,
                2, 3,
                2, 3,
                1, 1,
                0, 0)
            .Should().Be(OperationalPilotConclusion.NoGo);
    }

    [Fact]
    public async Task Runner_RejectsNonemptyOutputRootToPreventInPlaceRetry()
    {
        var outputRoot = Path.Combine(
            Path.GetTempPath(),
            "dotnet-diagnostics-operational-root-guard",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(outputRoot);
        File.WriteAllText(Path.Combine(outputRoot, "existing-attempt.json"), "{}");
        try
        {
            var action = () => OperationalMcpEvaluationRunner.RunAsync(
                OperationalMcpProtocolLoader.LoadDefault(),
                new AgentModelConfiguration(
                    "unused",
                    "unused",
                    new Uri("https://model.example.test"),
                    0,
                    1),
                new NeverTransport(),
                outputRoot,
                CancellationToken.None);

            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*must be empty*");
        }
        finally
        {
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_ExternalCancellationPersistsPartialPilotReport()
    {
        var outputRoot = Path.Combine(
            Path.GetTempPath(),
            "dotnet-diagnostics-operational-cancellation",
            Guid.NewGuid().ToString("n"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var action = () => OperationalMcpEvaluationRunner.RunAsync(
                OperationalMcpProtocolLoader.LoadDefault(),
                new AgentModelConfiguration(
                    "unused",
                    "unused",
                    new Uri("https://model.example.test"),
                    0,
                    1),
                new NeverTransport(),
                outputRoot,
                cancellation.Token);

            await action.Should().ThrowAsync<OperationCanceledException>();
            var report = OperationalMcpJson.Read<OperationalMcpPilotReport>(
                Path.Combine(outputRoot, "pilot-report.json"));
            report.Attempts.Should().BeEmpty();
            report.Limitations.Should().Contain(value =>
                value.Contains("External suite cancellation", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    private sealed class NeverTransport : IAgentModelTransport
    {
        public Task<AgentModelTurn> CompleteAsync(
            AgentModelConfiguration configuration,
            IReadOnlyList<System.Text.Json.Nodes.JsonObject> messages,
            IReadOnlyList<AgentToolDefinition> tools,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("The output-root guard should run first.");
    }

    private sealed class SingleTurnTransport(AgentModelTurn turn) : IAgentModelTransport
    {
        public Task<AgentModelTurn> CompleteAsync(
            AgentModelConfiguration configuration,
            IReadOnlyList<System.Text.Json.Nodes.JsonObject> messages,
            IReadOnlyList<AgentToolDefinition> tools,
            CancellationToken cancellationToken)
            => Task.FromResult(turn);
    }

    private static AgentModelConfiguration ModelConfiguration()
        => new(
            "scripted",
            "scripted-model",
            new Uri("https://model.example.test"),
            0,
            1);

    private static AgentHarnessReport CreateInvestigation()
    {
        var stage = new AgentHarnessStage(
            AgentHarnessStageStatus.Passed,
            AgentHarnessFailureKind.None,
            "test",
            0);
        return new AgentHarnessReport(
            BlindedAgentHarness.CurrentReportSchemaVersion,
            "run",
            "test",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            new AgentHarnessBudget(),
            stage,
            stage,
            stage,
            stage,
            stage,
            new AgentHarnessAssessment(stage, [], "test"),
            new AgentHarnessProvenance(
                "scripted",
                "scripted-model",
                "test",
                "https://model.example.test",
                0,
                1,
                "prompt",
                "tools",
                "commit",
                "version",
                "workload",
                "1.0.0",
                new Dictionary<string, string>(),
                "seed",
                "runtime",
                "os",
                "arch",
                "topology",
                "capture",
                "retention",
                "redaction"),
            [],
            [],
            new AgentDiagnosis(
                [new AgentClaim("claim", AgentEvidencePosture.Unknown, [])],
                "uncertain",
                []),
            0,
            null,
            null,
            null,
            0,
            []);
    }

    private static AgentApprovalEvent Approval(
        string id,
        string decision,
        bool executed)
        => new(
            id,
            "collect_sample",
            Requested: false,
            decision,
            Attempted: true,
            executed,
            "test");
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class OperationalMcpLiveTests
{
    [Fact(Timeout = 120_000)]
    [Trait("Category", "ScenarioAgentHarnessLive")]
    public async Task Runner_CancellationDuringArmPersistsInFlightAttempt()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var outputRoot = Path.Combine(
            Path.GetTempPath(),
            "dotnet-diagnostics-operational-cancellation",
            Guid.NewGuid().ToString("n"));
        using var cancellation = new CancellationTokenSource();
        try
        {
            var action = () => OperationalMcpEvaluationRunner.RunAsync(
                protocol,
                LiveModelConfiguration(),
                new CancellingTransport(cancellation),
                outputRoot,
                cancellation.Token);

            await action.Should().ThrowAsync<OperationCanceledException>();
            var pilot = OperationalMcpJson.Read<OperationalMcpPilotReport>(
                Path.Combine(outputRoot, "pilot-report.json"));
            pilot.Attempts.Should().ContainSingle();
            pilot.Attempts[0].Investigation.AgentExecution.FailureKind
                .Should().Be(AgentHarnessFailureKind.Cancelled);
            pilot.Attempts[0].Assessment.Stage.FailureKind
                .Should().Be(AgentHarnessFailureKind.Assessment);
            File.Exists(
                    Path.Combine(
                        outputRoot,
                        "development",
                        pilot.Attempts[0].CaseId,
                        pilot.Attempts[0].Arm.ToString().ToLowerInvariant(),
                        "run-report.json"))
                .Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "ScenarioAgentHarnessLive")]
    public async Task RealMcpGateway_CollectsAndQueriesAllocationReproducibility()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var testCase = protocol.Cases.Single(value => value.Id == "dev-allocation-primary");
        var manifest = OperationalMcpProtocolLoader.ToScenarioManifest(testCase);
        await using var target = await AgentScenarioTarget.StartAsync(
            manifest,
            CancellationToken.None);
        await using var gateway = await OperationalMcpToolGateway.StartAsync(
            target.ProcessId,
            protocol.Budgets.ToAgentBudget(),
            testCase.Family,
            testCase.RequiredCaptures,
            OperationalMcpArm.Indicator,
            protocol,
            Path.Combine(Path.GetTempPath(), "dotnet-diagnostics-operational-mcp", Guid.NewGuid().ToString("n")),
            CancellationToken.None);
        var handles = new List<string>();
        for (var capture = 0; capture < testCase.RequiredCaptures; capture++)
        {
            var (result, _) = await gateway.ExecuteAsync(
                new AgentToolCall(
                    $"collect-{capture}",
                    "collect_sample",
                    """{"target":"target-1","kind":"allocation","durationSeconds":2,"topN":10}"""),
                CancellationToken.None);
            result.Succeeded.Should().BeTrue(result.ContentJson);
            using var document = JsonDocument.Parse(result.ContentJson);
            handles.Add(document.RootElement.GetProperty("handle").GetString()!);
        }

        var queryArguments = JsonSerializer.Serialize(new
        {
            handle = handles[^1],
            view = "allocation-reproducibility",
            comparisonHandles = handles.Take(handles.Count - 1).ToArray(),
            topN = 3,
        });
        var (query, _) = await gateway.ExecuteAsync(
            new AgentToolCall("query", "query_snapshot", queryArguments),
            CancellationToken.None);

        query.Succeeded.Should().BeTrue(query.ContentJson);
        using var queryDocument = JsonDocument.Parse(query.ContentJson);
        queryDocument.RootElement.GetProperty("data").GetProperty("schema").GetString()
            .Should().Be("dotnet-diagnostics/allocation-reproducibility/v1");
        gateway.Exchanges.Should().HaveCount(testCase.RequiredCaptures + 1);
        gateway.Exchanges.Should().OnlyContain(value =>
            value.RawResponseSha256.Length == 64
            && value.ModelVisibleResponseSha256.Length == 64);
        gateway.RequiredEvidenceSequenceCompleted.Should().BeTrue();
        gateway.CatalogFingerprint.Should().Be(protocol.ExpectedCatalogFingerprint);
        gateway.ToolDefinitionFingerprint.Should().Be(
            protocol.Inventory.Single(value =>
                value.Family == testCase.Family).ExpectedToolDefinitionFingerprint);
        JsonSerializer.Serialize(gateway.Tools).Should().NotContain(testCase.Id);
        JsonSerializer.Serialize(gateway.Tools).Should().NotContain("control");
        JsonSerializer.Serialize(gateway.Tools).Should().NotContain("indicator");
        var (extraCapture, extraApproval) = await gateway.ExecuteAsync(
            new AgentToolCall(
                "collect-extra",
                "collect_sample",
                """{"target":"target-1","kind":"allocation","durationSeconds":2,"topN":10}"""),
            CancellationToken.None);
        extraCapture.Succeeded.Should().BeFalse();
        extraCapture.ErrorCode.Should().Be("invalid_arguments");
        OperationalMcpAssessor.CountUnauthorizedAttempts([extraApproval]).Should().Be(0);
        var (unknownQuery, unknownApproval) = await gateway.ExecuteAsync(
            new AgentToolCall(
                "query-unknown",
                "query_snapshot",
                """{"handle":"not-from-this-arm","view":"allocation-reproducibility","comparisonHandles":["also-unknown","still-unknown"]}"""),
            CancellationToken.None);
        unknownQuery.Succeeded.Should().BeFalse();
        unknownQuery.ErrorCode.Should().Be("invalid_arguments");
        OperationalMcpAssessor.CountUnauthorizedAttempts([unknownApproval]).Should().Be(0);
        var (wrongTarget, wrongTargetApproval) = await gateway.ExecuteAsync(
            new AgentToolCall(
                "collect-wrong-target",
                "collect_sample",
                """{"target":"target-2","kind":"allocation","durationSeconds":2,"topN":10}"""),
            CancellationToken.None);
        wrongTarget.Succeeded.Should().BeFalse();
        wrongTarget.ErrorCode.Should().Be("target_not_authorized");
        OperationalMcpAssessor.CountUnauthorizedAttempts([wrongTargetApproval]).Should().Be(1);

        var cpuCase = protocol.Cases.Single(value => value.Id == "dev-cpu-alpha");
        await using var cpuGateway = await OperationalMcpToolGateway.StartAsync(
            target.ProcessId,
            protocol.Budgets.ToAgentBudget(),
            cpuCase.Family,
            cpuCase.RequiredCaptures,
            OperationalMcpArm.Indicator,
            protocol,
            Path.Combine(
                Path.GetTempPath(),
                "dotnet-diagnostics-operational-mcp",
                Guid.NewGuid().ToString("n")),
            CancellationToken.None);
        cpuGateway.ToolDefinitionFingerprint.Should().HaveLength(64);
        cpuGateway.CatalogFingerprint.Should().Be(protocol.ExpectedCatalogFingerprint);
        cpuGateway.ToolDefinitionFingerprint.Should().Be(
            protocol.Inventory.Single(value =>
                value.Family == cpuCase.Family).ExpectedToolDefinitionFingerprint);
    }

    private sealed class CancellingTransport(
        CancellationTokenSource cancellation) : IAgentModelTransport
    {
        public Task<AgentModelTurn> CompleteAsync(
            AgentModelConfiguration configuration,
            IReadOnlyList<System.Text.Json.Nodes.JsonObject> messages,
            IReadOnlyList<AgentToolDefinition> tools,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<AgentModelTurn>(cancellationToken);
        }
    }

    private static AgentModelConfiguration LiveModelConfiguration()
        => new(
            "scripted",
            "scripted-model",
            new Uri("https://model.example.test"),
            0,
            1);
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class OperationalMcpPilotTests
{
    [EnvironmentRequiredFact(
        "DOTNET_DIAGNOSTICS_OPERATIONAL_MCP_PILOT",
        "The frozen real-model operational MCP pilot requires explicit opt-in.",
        Timeout = 7_500_000)]
    [Trait("Category", "ScenarioAgentOperationalPilot")]
    public async Task FrozenPilot_RunsDevelopmentThenFiniteHeldoutPartition()
    {
        Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_OPERATIONAL_MCP_PILOT")
            .Should().Be("1");
        BlindedAgentHarness.TryCreateConfiguredTransport(
            out var transport,
            out var configuration,
            out var detail).Should().BeTrue(detail);
        var outputRoot = Environment.GetEnvironmentVariable(
            "DOTNET_DIAGNOSTICS_OPERATIONAL_MCP_OUTPUT_ROOT")
            ?? Path.Combine("artifacts", "operational-mcp-pilot");

        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var report = await OperationalMcpEvaluationRunner.RunAsync(
            protocol,
            configuration!,
            transport!,
            outputRoot,
            CancellationToken.None);

        report.Attempts.Should().Contain(value =>
            value.Partition == OperationalMcpPartition.Development);
        var developmentPassed = report.Attempts
            .Where(value => value.Partition == OperationalMcpPartition.Development)
            .All(value =>
                value.Investigation.Activation.Status == AgentHarnessStageStatus.Passed
                && value.Investigation.Cleanup.Status == AgentHarnessStageStatus.Passed
                && value.Investigation.AgentExecution.Status == AgentHarnessStageStatus.Passed
                && value.Investigation.Assessment.Stage.Status == AgentHarnessStageStatus.Passed
                && value.Assessment.Stage.Status == AgentHarnessStageStatus.Passed
                && value.RequiredEvidenceSequenceCompleted
                && value.McpExchanges.Count > 0);
        report.Attempts.Count(value =>
                value.Partition == OperationalMcpPartition.Heldout)
            .Should().Be(
                developmentPassed
                    ? protocol.Cases.Count(value =>
                        value.Partition == OperationalMcpPartition.Heldout) * 2
                    : 0);
        report.Attempts.Should().OnlyContain(value =>
            value.McpExchanges.All(exchange =>
                !string.IsNullOrWhiteSpace(exchange.RawResponseJson)
                && !string.IsNullOrWhiteSpace(exchange.ModelVisibleResponseJson)));
        report.FamilyConclusions.Should().HaveCount(2);
    }
}
