using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class OperationalMcpProtocolTests
{
    [Fact]
    public void FrozenProtocol_DeclaresOnlyTruthfullyReachableMcpFamilies()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var preserved = OperationalMcpProtocolLoader.LoadPreservedV1();

        protocol.SchemaVersion.Should().Be(2);
        protocol.ProtocolVersion.Should().Be("2.0.0");
        preserved.ProtocolVersion.Should().Be("1.4.0");
        protocol.Models.Should().NotBeNull();
        protocol.Models!.DiagnosingPromptSha256.Should().Be(
            BlindedAgentHarness.SystemPromptSha256);
        protocol.Models.AssessorPromptSha256.Should().Be(
            OperationalMcpAssessor.SystemPromptSha256);
        protocol.Models.TransportProtocolSha256.Should().Be(
            BlindedAgentHarness.TransportProtocolSha256);
        protocol.Budgets.MaximumMcpPayloadBytes.Should().BeLessThan(
            protocol.Budgets.MaximumModelTransportOutputBytes);
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
            .Reason.Should().Contain("truthfully populates");
        protocol.Cases.Should().Contain(value =>
            value.Partition == OperationalMcpPartition.Development);
        protocol.Cases.Should().Contain(value =>
            value.Partition == OperationalMcpPartition.Heldout);
        var manifest = OperationalMcpProtocolLoader.ToScenarioManifest(
            protocol.Cases.Single(value => value.Id == "v2-dev-allocation-primary"));
        manifest.ReportedSymptom.Should().Contain(
            "already executed exactly 3 fresh collect_sample(kind=\"allocation\") captures");
        manifest.ReportedSymptom.Should().Contain(
            "query_snapshot(view=\"allocation-reproducibility\") sequence");
        manifest.ReportedSymptom.Should().Contain("follow-up can add evidence");
        manifest.ReportedSymptom.Should().NotContain("v2-dev-allocation-primary");
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
                "df1db2c83d7d71967fa582153f49e88b0201017f3f4486602f2da49a2604c43d");
        OperationalMcpProtocolLoader.InventoryFingerprint(protocol)
            .Should().Be(
                "5502f0bfd8617e77faf3315fe5e8087976f8667d08c353b01093c4be95463f67");
        OperationalMcpProtocolLoader.ProjectionFingerprint(protocol)
            .Should().Be(
                "9ab6488c85a32d5fa89426411068866c6966832e41cfa899d2c348e8ced47696");
    }

    [Theory]
    [InlineData("tool-result://call-1#")]
    [InlineData("tool-result://call-1#/")]
    public void CitationResolver_AcceptsCanonicalAndLegacyDocumentRoot(string location)
    {
        var result = new AgentToolResult(
            "call-1",
            "query_snapshot",
            true,
            """{"availability":"available"}""",
            "hash",
            28,
            false,
            null);

        var resolution = AgentEvidenceResolver.Resolve(location, [result]);

        resolution.Exists.Should().BeTrue();
        resolution.Value!.Value.GetProperty("availability").GetString()
            .Should().Be("available");
    }

    [Fact]
    public async Task Conversation_ReceivesPredeclaredEvidenceBeforeFirstModelTurn()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var testCase = protocol.Cases.Single(value =>
            value.Id == "v2-dev-allocation-primary");
        var call = new AgentToolCall(
            "predeclared-query-1",
            "query_snapshot",
            """{"handle":"h-3","view":"allocation-reproducibility"}""");
        var result = new AgentToolResult(
            call.Id,
            call.Name,
            true,
            """{"availability":"available"}""",
            "hash",
            28,
            false,
            null);
        var request = new AgentHarnessRequest(
            OperationalMcpProtocolLoader.ToScenarioManifest(testCase),
            ModelConfiguration(),
            protocol.Budgets.ToAgentBudget(),
            "unused.json",
            "test")
        {
            PredeclaredEvidence =
            [
                new AgentPredeclaredEvidence(
                    call,
                    result,
                    Approval(call.Id, "not-required-read-only", executed: true)),
            ],
        };
        var transport = new PredeclaredEvidenceTransport();
        var stage = new AgentHarnessStage(
            AgentHarnessStageStatus.Passed,
            AgentHarnessFailureKind.None,
            "test",
            0);

        var report = await BlindedAgentHarness.RunConversationAsync(
            request,
            transport,
            new NoOpGateway(),
            DateTimeOffset.UtcNow,
            stage,
            CancellationToken.None);

        transport.SawPredeclaredEvidence.Should().BeTrue();
        report.AgentExecution.Status.Should().Be(AgentHarnessStageStatus.Passed);
        report.Assessment.Stage.Status.Should().Be(AgentHarnessStageStatus.Passed);
        report.TotalToolCalls.Should().Be(1);
        report.ModelTransportOutputBytes.Should().Be(4096);
        report.Transcript[0].Turn.Should().Be(0);
        report.Transcript[0].ToolResults.Should().ContainSingle();
    }

    [Fact]
    public async Task Conversation_RejectsFollowUpIdCollisionWithPredeclaredEvidence()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var testCase = protocol.Cases.Single(value =>
            value.Id == "v2-dev-allocation-primary");
        var call = new AgentToolCall(
            "same-id",
            "query_snapshot",
            """{"handle":"h-3","view":"allocation-reproducibility"}""");
        var result = new AgentToolResult(
            call.Id,
            call.Name,
            true,
            """{"availability":"available"}""",
            "hash",
            28,
            false,
            null);
        var request = new AgentHarnessRequest(
            OperationalMcpProtocolLoader.ToScenarioManifest(testCase),
            ModelConfiguration(),
            protocol.Budgets.ToAgentBudget(),
            "unused.json",
            "test")
        {
            PredeclaredEvidence =
            [
                new AgentPredeclaredEvidence(
                    call,
                    result,
                    Approval(call.Id, "not-required-read-only", executed: true)),
            ],
        };
        var stage = new AgentHarnessStage(
            AgentHarnessStageStatus.Passed,
            AgentHarnessFailureKind.None,
            "test",
            0);

        var report = await BlindedAgentHarness.RunConversationAsync(
            request,
            new CollidingToolCallTransport(),
            new NoOpGateway(),
            DateTimeOffset.UtcNow,
            stage,
            CancellationToken.None);

        report.AgentExecution.Status.Should().Be(AgentHarnessStageStatus.Failed);
        report.AgentExecution.FailureKind.Should().Be(AgentHarnessFailureKind.Model);
        report.AgentExecution.Detail.Should().Contain("unique non-empty id");
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
        assessment.Claims.Should().BeEmpty();
        assessment.Uncertainty.Should().Be(OperationalUncertaintyQuality.NotAssessable);
        assessment.UnsupportedCertaintyCount.Should().Be(0);
    }

    [Fact]
    public async Task AssessorPreflight_RequiresStrictSchemaAndRecordsTransportBytes()
    {
        var turn = new AgentModelTurn(
            "wire",
            """
            {"claims":[{"index":0,"support":"supported","rationale":"The synthetic evidence exists."}],"uncertainty":"appropriate","uncertaintyRationale":"The response is limited to preflight.","unsupportedCertaintyCount":0,"usefulNextSteps":[],"unhelpfulNextSteps":[],"summary":"Schema preflight passed."}
            """,
            [],
            new AgentModelUsage(null, null, null),
            null)
        {
            TransportOutputBytes = 4096,
        };

        var preflight = await OperationalMcpAssessor.PreflightAsync(
            ModelConfiguration(),
            new SingleTurnTransport(turn),
            maximumWallTimeSeconds: 5,
            maximumInputBytes: 32_768,
            maximumTransportOutputBytes: 8_192,
            CancellationToken.None);

        preflight.Stage.Status.Should().Be(AgentHarnessStageStatus.Passed);
        preflight.TransportOutputBytes.Should().Be(4096);
        preflight.RawResponseSha256.Should().HaveLength(64);
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
            var report = await OperationalMcpEvaluationRunner.RunAsync(
                OperationalMcpProtocolLoader.LoadDefault(),
                new AgentModelConfiguration(
                    "github-copilot-cli",
                    "gpt-5.3-codex",
                    new Uri("https://model.example.test"),
                    0,
                    1,
                    MaximumResponseBytes: 524_288),
                new NeverTransport(),
                outputRoot,
                cancellation.Token);

            report.Attempts.Should().BeEmpty();
            report.AssessorPreflight.Should().NotBeNull();
            report.AssessorPreflight!.Stage.Status.Should().Be(AgentHarnessStageStatus.Failed);
            File.Exists(Path.Combine(outputRoot, "pilot-report.json")).Should().BeTrue();
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

    private sealed class PredeclaredEvidenceTransport : IAgentModelTransport
    {
        public bool SawPredeclaredEvidence { get; private set; }

        public Task<AgentModelTurn> CompleteAsync(
            AgentModelConfiguration configuration,
            IReadOnlyList<System.Text.Json.Nodes.JsonObject> messages,
            IReadOnlyList<AgentToolDefinition> tools,
            CancellationToken cancellationToken)
        {
            SawPredeclaredEvidence = messages.Any(message =>
                message["role"]?.GetValue<string>() == "tool"
                && message["tool_call_id"]?.GetValue<string>() == "predeclared-query-1");
            return Task.FromResult(new AgentModelTurn(
                "wire",
                """
                {"claims":[{"text":"Evidence is available.","posture":"observed","evidenceLocations":["tool-result://predeclared-query-1#"]}],"uncertainty":"The retained document is limited.","nextSteps":[]}
                """,
                [],
                new AgentModelUsage(null, null, null),
                null)
            {
                TransportOutputBytes = 4096,
            });
        }

    }

    private sealed class CollidingToolCallTransport : IAgentModelTransport
    {
        public Task<AgentModelTurn> CompleteAsync(
            AgentModelConfiguration configuration,
            IReadOnlyList<System.Text.Json.Nodes.JsonObject> messages,
            IReadOnlyList<AgentToolDefinition> tools,
            CancellationToken cancellationToken)
            => Task.FromResult(new AgentModelTurn(
                "wire",
                null,
                [new AgentToolCall("same-id", "query_snapshot", "{}")],
                new AgentModelUsage(null, null, null),
                null));
    }

    private sealed class NoOpGateway : IBlindedDiagnosticToolGateway
    {
        public IReadOnlyList<AgentToolDefinition> Tools => [];

        public int RetainedArtifactBytes => 28;

        public Task<(AgentToolResult Result, AgentApprovalEvent Approval)> ExecuteAsync(
            AgentToolCall call,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("No follow-up call was expected.");
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
    public async Task PredeclaredFailure_IsRetainedWithoutCallingModel()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var testCase = protocol.Cases.Single(value =>
            value.Id == "v2-dev-allocation-primary");
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "dotnet-diagnostics-predeclared-failure",
            Guid.NewGuid().ToString("n"),
            "investigation.json");
        var transport = new CountingTransport();
        var call = new AgentToolCall(
            "predeclared-collect-1",
            "collect_sample",
            """{"target":"target-1","kind":"allocation","durationSeconds":2,"topN":12}""");
        var content = """{"status":"failed","errorCode":"capture_failed"}""";
        var failed = new AgentToolResult(
            call.Id,
            call.Name,
            false,
            content,
            BlindedDiagnosticToolGateway.Sha256(content),
            content.Length,
            false,
            "capture_failed");

        var report = await BlindedAgentHarness.RunAsync(
            new AgentHarnessRequest(
                OperationalMcpProtocolLoader.ToScenarioManifest(testCase),
                FrozenModelConfiguration(),
                protocol.Budgets.ToAgentBudget(),
                outputPath,
                "test"),
            transport,
            static (_, _, _) => Task.FromResult<IBlindedDiagnosticToolGateway>(
                new RetainedFailureGateway()),
            (_, _) => Task.FromResult<IReadOnlyList<AgentPredeclaredEvidence>>(
            [
                new(
                    call,
                    failed,
                    new AgentApprovalEvent(
                        call.Id,
                        call.Name,
                        false,
                        "not-required-read-only",
                        true,
                        false,
                        "The frozen collection failed.")),
            ]),
            CancellationToken.None);

        transport.Calls.Should().Be(0);
        report.Collection.Status.Should().Be(AgentHarnessStageStatus.Failed);
        report.AgentExecution.Status.Should().Be(AgentHarnessStageStatus.NotRun);
        report.Transcript.Should().ContainSingle();
        report.Transcript[0].ToolResults.Should().ContainSingle(value =>
            !value.Succeeded && value.ErrorCode == "capture_failed");
    }

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
                FrozenModelConfiguration(),
                new PreflightThenCancellingTransport(cancellation),
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
        var testCase = protocol.Cases.Single(value => value.Id == "v2-dev-allocation-primary");
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
        var predeclared = await gateway.CollectPredeclaredEvidenceAsync(
            testCase,
            CancellationToken.None);

        predeclared.Should().HaveCount(testCase.RequiredCaptures + 1);
        predeclared.Should().OnlyContain(value => value.Result.Succeeded);
        using var queryDocument = JsonDocument.Parse(predeclared[^1].Result.ContentJson);
        queryDocument.RootElement.GetProperty("data").GetProperty("schema").GetString()
            .Should().Be("dotnet-diagnostics/allocation-reproducibility/v1");
        gateway.Exchanges.Should().HaveCount(testCase.RequiredCaptures + 1);
        gateway.Exchanges.Should().OnlyContain(value =>
            value.Origin == OperationalMcpExchangeOrigin.Predeclared);
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
        gateway.Exchanges.Should().ContainSingle(value =>
            value.Origin == OperationalMcpExchangeOrigin.FollowUp
            && !value.Succeeded
            && value.ErrorCode == "invalid_arguments");
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

        var cpuCase = protocol.Cases.Single(value => value.Id == "v2-dev-cpu-alpha");
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

    private sealed class PreflightThenCancellingTransport(
        CancellationTokenSource cancellation) : IAgentModelTransport
    {
        private int _calls;

        public Task<AgentModelTurn> CompleteAsync(
            AgentModelConfiguration configuration,
            IReadOnlyList<System.Text.Json.Nodes.JsonObject> messages,
            IReadOnlyList<AgentToolDefinition> tools,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return Task.FromResult(new AgentModelTurn(
                    "preflight",
                    """
                    {"claims":[{"index":0,"support":"supported","rationale":"Synthetic evidence exists."}],"uncertainty":"appropriate","uncertaintyRationale":"Preflight only.","unsupportedCertaintyCount":0,"usefulNextSteps":[],"unhelpfulNextSteps":[],"summary":"Passed."}
                    """,
                    [],
                    new AgentModelUsage(null, null, null),
                    null));
            }

            cancellation.Cancel();
            return Task.FromCanceled<AgentModelTurn>(cancellationToken);
        }
    }

    private sealed class CountingTransport : IAgentModelTransport
    {
        public int Calls { get; private set; }

        public Task<AgentModelTurn> CompleteAsync(
            AgentModelConfiguration configuration,
            IReadOnlyList<System.Text.Json.Nodes.JsonObject> messages,
            IReadOnlyList<AgentToolDefinition> tools,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("The model must not be called.");
        }
    }

    private sealed class RetainedFailureGateway : IBlindedDiagnosticToolGateway
    {
        public IReadOnlyList<AgentToolDefinition> Tools => [];

        public int RetainedArtifactBytes => 128;

        public Task<(AgentToolResult Result, AgentApprovalEvent Approval)> ExecuteAsync(
            AgentToolCall call,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("No follow-up call was expected.");
    }

    private static AgentModelConfiguration FrozenModelConfiguration()
        => new(
            "github-copilot-cli",
            "gpt-5.3-codex",
            new Uri("https://model.example.test"),
            0,
            1,
            MaximumResponseBytes: 524_288);
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
