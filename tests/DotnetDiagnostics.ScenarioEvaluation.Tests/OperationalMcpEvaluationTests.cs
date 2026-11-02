using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class OperationalMcpProtocolTests
{
    [Fact]
    public void FrozenProtocol_DeclaresOnlyTruthfullyReachableMcpFamilies()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        OperationalMcpProtocolLoader.ValidatePreservedV1();
        OperationalMcpProtocolLoader.ValidatePreservedV2();

        protocol.SchemaVersion.Should().Be(3);
        protocol.ProtocolVersion.Should().Be("3.0.0");
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
                });
        protocol.Cases.Should().Contain(value =>
            value.Partition == OperationalMcpPartition.Development);
        protocol.Cases.Should().Contain(value =>
            value.Partition == OperationalMcpPartition.Heldout);
        var manifest = OperationalMcpProtocolLoader.ToScenarioManifest(
            protocol.Cases.Single(value => value.Id == "v3-dev-allocation-positive"));
        manifest.ReportedSymptom.Should().Contain(
            "already executed exactly 3 fresh collect_sample(kind=\"allocation\") captures");
        manifest.ReportedSymptom.Should().Contain(
            "query_snapshot(view=\"allocation-reproducibility\") sequence");
        manifest.ReportedSymptom.Should().Contain("follow-up can add evidence");
        manifest.ReportedSymptom.Should().NotContain("v3-dev-allocation-positive");
        manifest.ReportedSymptom.Should().NotContain("prior");
        manifest.ReportedSymptom.Should().NotContain("hardened");
        manifest.ReportedSymptom.Should().NotContain("projection");
        protocol.Cases.Where(value =>
                value.Partition == OperationalMcpPartition.Development)
            .Select(value => value.CaseClass)
            .Should().BeEquivalentTo(
                new[]
                {
                    OperationalMcpCaseClass.Positive,
                    OperationalMcpCaseClass.HealthyControl,
                });
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
                OperationalMcpProtocolLoader.FrozenProtocolFingerprint);
        OperationalMcpProtocolLoader.InventoryFingerprint(protocol)
            .Should().Be(
                OperationalMcpProtocolLoader.FrozenInventoryFingerprint);
        OperationalMcpProtocolLoader.ProjectionFingerprint(protocol)
            .Should().Be(
                OperationalMcpProtocolLoader.FrozenProjectionFingerprint);
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
            value.Id == "v3-dev-allocation-positive");
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
            value.Id == "v3-dev-allocation-positive");
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
            {"claims":[{"index":0,"support":"supported","rationale":"The synthetic evidence exists."}],"uncertainty":"appropriate","uncertaintyRationale":"The response is limited to preflight.","unsupportedCertaintyFindings":[],"usefulNextSteps":[],"unhelpfulNextSteps":[],"summary":"Schema preflight passed."}
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
            new[]
            {
                OperationalMcpArm.PriorWording,
                OperationalMcpArm.HardenedWording,
            });
    }

    [Fact]
    public void ResponseProjection_RestoresExactPriorWordingAndOnlyWordingDiffers()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var projection = protocol.Projections.Single();
        const string raw =
            """
            {"summary":"Analyzed 3 identity-compatible allocation captures using descriptive bounded independent type and stack top-6 stability; stability does not establish workload dominance or a performance verdict.","hints":[{"tool":"query_snapshot","reason":"unchanged"}],"error":null,"data":{"schema":"dotnet-diagnostics/allocation-reproducibility/v1","method":"bounded-top-k-jaccard-shared-key-spearman-v1","availability":"available","requestedCaptureCount":3,"availableCaptureCount":3,"independenceUnit":"capture","topK":6,"compatibility":{"status":"compatible"},"estimatedBytes":{"median":123.0},"assumptions":["Each input is one separately started and stopped allocation capture.","Identity-compatible captures reported the same runtime semantics, EventPipe allocation backend, requested duration and retained top-N bounds.","Any assertion that workload shape, request mix, intensity or execution conditions were equivalent is caller-supplied and was not verified by capture identity."],"limitations":["GCAllocationTick estimated bytes are runtime sampling weights, not exact allocation totals, retained bytes, object counts or CPU time.","Stable top-K membership or rank describes retained sampled inventories only; it does not prove that a type or call site dominates the workload.","Allocated types and allocation stack sites are independent ranked inventories; no type-to-stack relationship is inferred or serialized.","Capture-level medians and raw median absolute deviations are descriptive only. No confidence interval is computed over weighted allocation ticks.","Capture identity compatibility covers recorded runtime, backend and requested capture settings only; it does not verify equivalent request mix, workload intensity or execution conditions.","Reproducibility is descriptive only: no universal scenario threshold or causal, severity, regression, health, performance or optimization-priority verdict is produced.","Missing, partial, zero-weight, unavailable or empty attribution and bounded inventories do not mean zero allocation activity; they describe observation or attribution limits for that capture."]},"handle":"h-1","cancelled":false}
            """;

        var prior = OperationalMcpResponseProjector.Apply(
            raw,
            OperationalMcpArm.PriorWording,
            projection,
            out var projected,
            out var schema);
        var hardened = OperationalMcpResponseProjector.Apply(
            raw,
            OperationalMcpArm.HardenedWording,
            projection,
            out var hardenedProjected,
            out _);

        projected.Should().BeTrue();
        schema.Should().Be(projection.Schema);
        hardenedProjected.Should().BeFalse();
        hardened.Should().Be(raw);
        using var priorDocument = JsonDocument.Parse(prior);
        using var hardenedDocument = JsonDocument.Parse(hardened);
        priorDocument.RootElement.GetProperty("summary").GetString()
            .Should().Be(
                "Analyzed 3 compatible allocation captures using bounded independent type and stack top-6 stability; no performance verdict was produced.");
        priorDocument.RootElement.GetProperty("data").GetProperty("limitations")
            .GetArrayLength().Should().Be(5);
        priorDocument.RootElement.GetProperty("data").GetProperty("limitations")[0]
            .GetString().Should().Contain("exact allocated bytes");
        priorDocument.RootElement.GetProperty("data").GetProperty("assumptions")[2]
            .GetString().Should().Be("The workload shape and offered work were stable across captures.");
        priorDocument.RootElement.GetProperty("data").GetProperty("estimatedBytes")
            .GetRawText().Should().Be(
                hardenedDocument.RootElement.GetProperty("data")
                    .GetProperty("estimatedBytes").GetRawText());
        priorDocument.RootElement.GetProperty("data").GetProperty("compatibility")
            .GetRawText().Should().Be(
                hardenedDocument.RootElement.GetProperty("data")
                    .GetProperty("compatibility").GetRawText());
        priorDocument.RootElement.GetProperty("hints").GetRawText().Should().Be(
            hardenedDocument.RootElement.GetProperty("hints").GetRawText());
        priorDocument.RootElement.GetProperty("handle").GetString().Should().Be("h-1");
        prior.Should().NotContain("priorWording");
        prior.Should().NotContain("hardenedWording");
        prior.Should().NotContain("projection");
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
                OperationalMcpArm.PriorWording,
                projection,
                out var projected,
                out _)
            .Should().Be(failure);
        projected.Should().BeFalse();
    }

    [Fact]
    public void Projection_PreservesNonAvailableResultsByteForByte()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var projection = protocol.Projections[0];
        const string insufficient =
            """
            {"summary":"Allocation reproducibility requires at least 3 identity-compatible available captures; 1 were available.","hints":[],"error":null,"data":{"schema":"dotnet-diagnostics/allocation-reproducibility/v1","availability":"insufficientCaptureCount","availableCaptureCount":1}}
            """;

        OperationalMcpResponseProjector.Apply(
                insufficient,
                OperationalMcpArm.PriorWording,
                projection,
                out var projected,
                out _)
            .Should().Be(insufficient);
        projected.Should().BeFalse();
    }

    [Fact]
    public void ToolProjection_RestoresExactPriorDescriptionsAndOnlyWordingDiffers()
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var projection = protocol.Projections.Single();
        var hardened = new[]
        {
            new AgentToolDefinition("collect_sample", "collect", new System.Text.Json.Nodes.JsonObject()),
            new AgentToolDefinition(
                "query_snapshot",
                "query",
                System.Text.Json.Nodes.JsonNode.Parse(
                    """
                    {"type":"object","properties":{"view":{"type":"string","description":"Kind-specific view; omit for default. Allocation-reproducibility is descriptive only; read data.limitations. Durable selectors add bounded records/composition children; historical views never reattach."},"topN":{"type":"integer","description":"Ranked entries: defaults 50 heap/thread/collection, 25 off-CPU/diff, 3 allocation-reproducibility (cap 16; descriptive ranks do not prove workload dominance). Inline caps: threads 8, locks 12, retention paths 10; full evidence stays behind the handle."},"comparisonHandles":{"type":"array","description":"Diff: ordered prior handles. Allocation-reproducibility: separately started allocation handles; matching capture identity does not verify request mix, intensity or execution conditions. Current handle is appended; excludes baselineHandle."}}}
                    """)!.AsObject()),
        };

        var prior = OperationalMcpResponseProjector.ApplyToolDefinitions(
            hardened,
            OperationalMcpArm.PriorWording,
            projection);
        var unchanged = OperationalMcpResponseProjector.ApplyToolDefinitions(
            hardened,
            OperationalMcpArm.HardenedWording,
            projection);

        OperationalMcpProtocolLoader.Fingerprint(unchanged)
            .Should().Be(OperationalMcpProtocolLoader.Fingerprint(hardened));
        prior[0].Should().BeEquivalentTo(hardened[0]);
        prior[1].Description.Should().Be(hardened[1].Description);
        prior[1].Parameters["properties"]!["view"]!["description"]!.GetValue<string>()
            .Should().Contain("see tool-reference.md");
        prior[1].Parameters["properties"]!["topN"]!["description"]!.GetValue<string>()
            .Should().Contain("Reproducibility cap 16");
        prior[1].Parameters["properties"]!["comparisonHandles"]!["description"]!.GetValue<string>()
            .Should().Contain("compatible allocation handles");
        JsonSerializer.Serialize(prior).Should().NotContain("priorWording");
        JsonSerializer.Serialize(prior).Should().NotContain("hardenedWording");
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
                priorSupport: 0,
                hardenedSupport: 1,
                priorUnsupported: 0,
                hardenedUnsupported: 0,
                priorAppropriate: 0,
                hardenedAppropriate: 4,
                priorUseful: 0,
                hardenedUseful: 4,
                priorUnhelpful: 0,
                hardenedUnhelpful: 0,
                priorUnauthorized: 0,
                hardenedUnauthorized: 0)
            .Should().Be(OperationalPilotConclusion.Inconclusive);
        OperationalMcpEvaluationRunner.SelectConclusion(
                true, true, true,
                0.4, 0.7,
                2, 1,
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
            value.Id == "v3-dev-allocation-positive");
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

    [Theory(Timeout = 120_000)]
    [InlineData(OperationalMcpArm.PriorWording)]
    [InlineData(OperationalMcpArm.HardenedWording)]
    [Trait("Category", "ScenarioAgentHarnessLive")]
    public async Task RealMcpGateway_CollectsAndQueriesAllocationReproducibility(
        OperationalMcpArm arm)
    {
        var protocol = OperationalMcpProtocolLoader.LoadDefault();
        var testCase = protocol.Cases.Single(value => value.Id == "v3-dev-allocation-positive");
        var manifest = OperationalMcpProtocolLoader.ToScenarioManifest(testCase);
        await using var target = await AgentScenarioTarget.StartAsync(
            manifest,
            CancellationToken.None);
        await using var gateway = await OperationalMcpToolGateway.StartAsync(
            target.ProcessId,
            protocol.Budgets.ToAgentBudget(),
            testCase.Family,
            testCase.RequiredCaptures,
            arm,
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
            arm == OperationalMcpArm.PriorWording
                ? protocol.Inventory.Single(value =>
                    value.Family == testCase.Family)
                    .ExpectedPriorToolDefinitionFingerprint
                : protocol.Inventory.Single(value =>
                    value.Family == testCase.Family)
                    .ExpectedHardenedToolDefinitionFingerprint);
        gateway.WordingProjectionFingerprint.Should().Be(
            arm == OperationalMcpArm.PriorWording
                ? protocol.Projections.Single().ExpectedPriorFingerprint
                : protocol.Projections.Single().ExpectedHardenedFingerprint);
        JsonSerializer.Serialize(gateway.Tools).Should().NotContain(testCase.Id);
        JsonSerializer.Serialize(gateway.Tools).Should().NotContain("priorWording");
        JsonSerializer.Serialize(gateway.Tools).Should().NotContain("hardenedWording");
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
                    {"claims":[{"index":0,"support":"supported","rationale":"Synthetic evidence exists."}],"uncertainty":"appropriate","uncertaintyRationale":"Preflight only.","unsupportedCertaintyFindings":[],"usefulNextSteps":[],"unhelpfulNextSteps":[],"summary":"Passed."}
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
        report.FamilyConclusions.Should().ContainSingle();
    }
}
