using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class BlindedAgentHarnessTests
{
    [Fact]
    public async Task Conversation_BlindsControllerRouteFromModel_ButRetainsPrivateProvenance()
    {
        var manifest = ScenarioManifestLoader.LoadAll().Single(value => value.Id == "sync-over-async");
        var transport = new ScriptedTransport(
        [
            new AgentModelTurn(
                """{"id":"response-1","choices":[]}""",
                null,
                [new AgentToolCall("call-counters", "collect_events", """{"target":"target-1","kind":"counters","durationSeconds":2}""")],
                new AgentModelUsage(300, 40, null),
                "response-1"),
            new AgentModelTurn(
                """{"id":"response-2","choices":[]}""",
                """
                {"claims":[{"text":"The queue is elevated.","posture":"observed","evidenceLocations":["tool-result://call-counters#/evidence/counters/0/value"]},{"text":"Worker starvation is plausible.","posture":"inferred","evidenceLocations":["tool-result://call-counters#/evidence/counters/0"]}],"uncertainty":"A thread snapshot would strengthen attribution.","nextSteps":["Capture blocked thread stacks."]}
                """,
                [],
                new AgentModelUsage(500, 120, null),
                "response-2"),
        ]);
        var gateway = new FakeGateway();
        var request = Request(manifest, "scripted");

        var report = await BlindedAgentHarness.RunConversationAsync(
            request,
            transport,
            gateway,
            DateTimeOffset.UtcNow,
            new AgentHarnessStage(AgentHarnessStageStatus.Passed, AgentHarnessFailureKind.None, "test", 0),
            CancellationToken.None);

        report.AgentExecution.Status.Should().Be(
            AgentHarnessStageStatus.Passed,
            $"{report.AgentExecution.FailureKind}: {report.AgentExecution.Detail}");
        report.Collection.Status.Should().Be(AgentHarnessStageStatus.Passed);
        report.Assessment.Stage.Status.Should().Be(AgentHarnessStageStatus.Passed);
        report.TotalToolCalls.Should().Be(1);
        report.Diagnosis!.Claims.Should().HaveCount(2);
        report.EvidenceKind.Should().Be("scripted");
        transport.SeenMessages.SelectMany(messages => messages)
            .Select(message => message.ToJsonString())
            .Should().OnlyContain(text =>
                !text.Contains(manifest.Id, StringComparison.Ordinal)
                && !text.Contains(manifest.GroundTruth, StringComparison.Ordinal)
                && !text.Contains("/sync-over-async", StringComparison.Ordinal));
        report.Transcript.Select(turn => turn.RawModelResponse)
            .Should().OnlyContain(text => !text.Contains("/sync-over-async", StringComparison.Ordinal));
        report.Provenance.WorkloadConfiguration["endpoint"].Should().Be("/sync-over-async");
        report.Provenance.RedactionPolicy.Should().Contain("evaluator-private provenance");
    }

    [Fact]
    public async Task Conversation_RejectsUnresolvedCitation_WithoutConvertingItToSuccess()
    {
        var manifest = ScenarioManifestLoader.LoadAll().Single(value => value.Id == "gc-storm");
        var transport = new ScriptedTransport(
        [
            new AgentModelTurn(
                "{}",
                """
                {"claims":[{"text":"GC is the cause.","posture":"inferred","evidenceLocations":["tool-result://missing#/evidence"]}],"uncertainty":"Limited.","nextSteps":["Collect GC events."]}
                """,
                [],
                new AgentModelUsage(null, null, null),
                null),
        ]);

        var report = await BlindedAgentHarness.RunConversationAsync(
            Request(manifest, "scripted"),
            transport,
            new FakeGateway(),
            DateTimeOffset.UtcNow,
            new AgentHarnessStage(AgentHarnessStageStatus.Passed, AgentHarnessFailureKind.None, "test", 0),
            CancellationToken.None);

        report.AgentExecution.Status.Should().Be(
            AgentHarnessStageStatus.Passed,
            $"{report.AgentExecution.FailureKind}: {report.AgentExecution.Detail}");
        report.Assessment.Stage.Status.Should().Be(AgentHarnessStageStatus.Failed);
        report.Assessment.InvalidEvidenceLocations.Should().ContainSingle("tool-result://missing#/evidence");
        report.Collection.Status.Should().Be(AgentHarnessStageStatus.NotRun);
    }

    [Fact]
    public async Task OpenAiCompatibleTransport_UsesStructuredTools_AndPreservesRawResponse()
    {
        const string response =
            """
            {"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"collect_thread_snapshot","arguments":"{\"target\":\"target-1\"}"}}]}}],"usage":{"prompt_tokens":17,"completion_tokens":9}}
            """;
        var handler = new RecordingHandler(response);
        var transport = new OpenAiCompatibleAgentTransport(new HttpClient(handler), "not-a-real-secret");
        var configuration = new AgentModelConfiguration(
            "scripted-openai-compatible",
            "test-model",
            new Uri("https://model.example.test/v1/chat/completions"),
            0,
            100);

        var turn = await transport.CompleteAsync(
            configuration,
            [new JsonObject { ["role"] = "user", ["content"] = "symptom" }],
            BlindedDiagnosticToolGateway.ToolDefinitions,
            CancellationToken.None);

        turn.RawResponse.Should().Be(response);
        turn.ToolCalls.Should().ContainSingle(call =>
            call.Id == "call-1" && call.Name == "collect_thread_snapshot");
        turn.Usage.InputTokens.Should().Be(17);
        handler.AuthorizationScheme.Should().Be("Bearer");
        handler.RequestBody.Should().Contain("\"tools\"");
        handler.RequestBody.Should().NotContain("not-a-real-secret");
    }

    [Fact]
    public async Task OpenAiCompatibleTransport_RejectsOversizedResponseBeforeParsing()
    {
        var transport = new OpenAiCompatibleAgentTransport(
            new HttpClient(new RecordingHandler(new string('x', 200))),
            "not-a-real-secret");
        var configuration = new AgentModelConfiguration(
            "scripted-openai-compatible",
            "test-model",
            new Uri("https://model.example.test/v1/chat/completions"),
            0,
            100,
            MaximumResponseBytes: 100);

        var action = () => transport.CompleteAsync(
            configuration,
            [new JsonObject { ["role"] = "user", ["content"] = "symptom" }],
            BlindedDiagnosticToolGateway.ToolDefinitions,
            CancellationToken.None);

        await action.Should().ThrowAsync<AgentTransportException>()
            .WithMessage("*response-byte budget*");
    }

    [Fact]
    public void CopilotCliTransport_BuildsIsolatedNonAgenticInvocation()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "dotnet-diagnostics-copilot-transport-" + Guid.NewGuid().ToString("n"));
        var home = Path.Combine(root, "home");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(work);
        var executable = Path.Combine(root, OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
        File.WriteAllText(executable, string.Empty);
        try
        {
            var transport = new CopilotCliAgentTransport(
                executable,
                home,
                work,
                enforceOutsideRepository: false);
            var configuration = new AgentModelConfiguration(
                "github-copilot-cli",
                "test-model",
                new Uri("copilot-cli://local-process"),
                0,
                100);
            var invocationDirectory = Path.Combine(work, "invocation");
            var info = transport.CreateStartInfo(
                configuration,
                """{"private":"prompt"}""",
                invocationDirectory,
                Guid.Parse("e1bf1639-7d23-4d66-a7f3-c21a2280201f"));

            info.UseShellExecute.Should().BeFalse();
            info.WorkingDirectory.Should().Be(invocationDirectory);
            info.ArgumentList.Should().ContainInOrder("--effort", "low");
            info.ArgumentList.Should().ContainInOrder(
                "--session-id",
                "e1bf1639-7d23-4d66-a7f3-c21a2280201f");
            info.ArgumentList.Should().ContainInOrder("--max-ai-credits", "30");
            info.ArgumentList.Should().ContainInOrder(
                "--available-tools",
                "blinded-harness-no-cli-tools",
                "--disable-builtin-mcps",
                "--no-custom-instructions",
                "--no-ask-user",
                "--no-remote-export",
                "--no-remote",
                "--no-auto-update",
                "--no-bash-env",
                "--disallow-temp-dir");
            info.ArgumentList.Should().NotContain("--allow-all");
            info.ArgumentList.Should().NotContain("--allow-all-tools");
            info.Environment["COPILOT_HOME"].Should().Be(home);
            info.Environment.Should().NotContainKey("GITHUB_TOKEN");
            info.Environment.Should().NotContainKey("GH_TOKEN");
            info.Environment.Should().NotContainKey("COPILOT_GITHUB_TOKEN");

            var preflight = transport.CreatePreflightStartInfo(invocationDirectory);
            preflight.ArgumentList.Should().Equal("--no-auto-update", "plugins", "list", "--json");
            preflight.UseShellExecute.Should().BeFalse();
            preflight.WorkingDirectory.Should().Be(invocationDirectory);
            preflight.Environment["COPILOT_HOME"].Should().Be(home);

            var version = transport.CreateVersionStartInfo();
            version.ArgumentList.Should().Equal("--no-auto-update", "--version");
            version.UseShellExecute.Should().BeFalse();
            version.Environment["COPILOT_HOME"].Should().Be(home);
            preflight.Environment.Should().NotContainKey("GITHUB_TOKEN");
            preflight.Environment.Should().NotContainKey("GH_TOKEN");
            preflight.Environment.Should().NotContainKey("COPILOT_GITHUB_TOKEN");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CopilotCliTransport_NormalizesCurrentVersionOutput()
    {
        CopilotCliAgentTransport.ParseVersionOutput(
                "GitHub Copilot CLI 1.0.86.\nRun 'copilot update' to check for updates.\n")
            .Should().Be("GitHub Copilot CLI 1.0.86");

        FluentActions.Invoking(() => CopilotCliAgentTransport.ParseVersionOutput("copilot unknown"))
            .Should().Throw<AgentTransportException>();
    }

    [Fact]
    public void CopilotCliTransport_ParsesOnlyStrictDecisionAndDropsCliMetadata()
    {
        const string output =
            """
            {"type":"session.start","data":{"sessionId":"private-cli-session","model":"test-model"}}
            {"type":"session.tools_updated","data":{"model":"test-model"}}
            {"type":"assistant.message","data":{"content":"{\"action\":\"tool\",\"toolCall\":{\"id\":\"call-1\",\"name\":\"collect_events\",\"arguments\":{\"target\":\"target-1\",\"kind\":\"counters\",\"durationSeconds\":2}}}"}}
            {"type":"usage","data":{"premiumRequests":1}}
            """;

        var turn = CopilotCliAgentTransport.ParseOutput(output);

        turn.RawResponse.Should().NotContain("private-cli-session");
        turn.RawResponse.Should().NotContain("premiumRequests");
        turn.ToolCalls.Should().ContainSingle();
        turn.ToolCalls[0].Name.Should().Be("collect_events");
        turn.Usage.Should().Be(new AgentModelUsage(null, null, null));
        turn.ProviderRequestId.Should().BeNull();
    }

    [Fact]
    public void CopilotCliTransport_RepeatsStrictDecisionBoundaryAfterConversationData()
    {
        var prompt = CopilotCliAgentTransport.BuildPrompt(
            [new JsonObject { ["role"] = "user", ["content"] = "neutral symptom" }],
            BlindedDiagnosticToolGateway.ToolDefinitions);

        prompt.Should().EndWith(CopilotCliAgentTransport.ProtocolReminder);
        prompt.Should().Contain("Return exactly one JSON decision object now.");
        prompt.Should().Contain("""{"action":"final","diagnosis":{...}}""");
        prompt.Should().Contain("never return bare");
        prompt.Should().NotContain("sync-over-async");
    }

    [Fact]
    public void CopilotCliTransport_RejectsEnabledAmbientConfiguration()
    {
        const string inventory =
            """
            {"plugins":[{"kind":"mcp","name":"private-server","scope":"user","source":"user","enabled":true}],"errors":[]}
            """;

        var action = () => CopilotCliAgentTransport.ValidatePluginInventory(inventory);

        action.Should().Throw<AgentTransportException>()
            .WithMessage("*enabled non-builtin configuration*");
    }

    [Theory]
    [InlineData("tool.execution_start")]
    [InlineData("tool.execution_complete")]
    [InlineData("tool.execution_progress")]
    [InlineData("assistant.tool_call")]
    public void CopilotCliTransport_RejectsAnyCliToolActivity(string eventType)
    {
        var output = JsonSerializer.Serialize(new { type = eventType, data = new { toolName = "shell" } })
            + "\n" +
            """
            {"type":"assistant.message","data":{"content":"{\"action\":\"final\",\"diagnosis\":{\"claims\":[],\"uncertainty\":\"none\",\"nextSteps\":[]}}"}}
            """;

        var action = () => CopilotCliAgentTransport.ParseOutput(output);

        action.Should().Throw<JsonException>()
            .WithMessage("*tool activity*");
    }

    [Fact]
    public void CopilotCliTransport_OnlyAcceptsAssistantContent()
    {
        const string decision = """{"action":"final","diagnosis":{"claims":[],"uncertainty":"unknown","nextSteps":[]}}""";
        var metadata = JsonSerializer.Serialize(new { type = "session.context", data = new { content = decision } });
        var noDecision = JsonSerializer.Serialize(new
        {
            type = "assistant.message",
            data = new { content = "No decision.", reasoningText = decision },
        });
        var action = () => CopilotCliAgentTransport.ParseOutput(metadata + "\n" + noDecision);
        action.Should().Throw<JsonException>().WithMessage("*required structured decision*");

        var message = JsonSerializer.Serialize(new
        {
            type = "assistant.message",
            data = new { content = decision, reasoningText = decision, encryptedContent = "opaque-metadata" },
        });
        var turn = CopilotCliAgentTransport.ParseOutput(metadata + "\n" + message);
        turn.RawResponse.Should().Be(decision);
        turn.RawResponse.Should().NotContain("opaque-metadata");
    }

    [Fact]
    public void CopilotCliTransport_RejectsBareDiagnosisAndNativeToolRequests()
    {
        const string diagnosis = """{"claims":[],"uncertainty":"unknown","nextSteps":[]}""";
        var bare = JsonSerializer.Serialize(new { type = "assistant.message", data = new { content = diagnosis } });
        var bareAction = () => CopilotCliAgentTransport.ParseOutput(bare);
        bareAction.Should().Throw<JsonException>().WithMessage("*required structured decision*");

        var native = JsonSerializer.Serialize(new
        {
            type = "assistant.message",
            data = new { content = diagnosis, toolRequests = new[] { new { name = "shell" } } },
        });
        var nativeAction = () => CopilotCliAgentTransport.ParseOutput(native);
        nativeAction.Should().Throw<JsonException>().WithMessage("*tool activity*");
    }

    [Fact]
    public void CopilotCliTransport_AcceptsBuiltinOnlyInventory()
    {
        const string inventory =
            """
            {"plugins":[{"kind":"skill","name":"builtin-skill","scope":"builtin","source":"builtin","enabled":true}],"errors":[]}
            """;

        var action = () => CopilotCliAgentTransport.ValidatePluginInventory(inventory);

        action.Should().NotThrow();
    }

    [Fact]
    public void CopilotCliTransport_ClassifiesKnownCliErrorWithoutRetainingRawStderr()
    {
        const string stderr =
            "error: option '--max-ai-credits <credits>' argument '1' is invalid. "
            + "Invalid value for --max-ai-credits: \"1\". Use at least 30 AI credits.";

        var detail = CopilotCliAgentTransport.DescribeFailure("invocation", 1, stderr);

        detail.Should().Be(
            "Copilot CLI invocation exited with code 1: "
            + "the installed CLI requires --max-ai-credits to be at least 30.");
        detail.Should().NotContain("argument '1'");
    }

    [Fact]
    public void CopilotCliTransport_RejectsProfileWithSettings()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "dotnet-diagnostics-copilot-profile-" + Guid.NewGuid().ToString("n"));
        var home = Path.Combine(root, "home");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(home, "settings.json"), "{}");
        var executable = Path.Combine(root, OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
        File.WriteAllText(executable, string.Empty);
        try
        {
            var action = () => new CopilotCliAgentTransport(
                executable,
                home,
                work,
                enforceOutsideRepository: false);

            action.Should().Throw<ArgumentException>()
                .WithMessage("*dedicated Copilot home contains settings*");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConfiguredTransport_MissingExplicitSettings_IsBlocked()
    {
        using var endpoint = new EnvironmentScope("DOTNET_DIAGNOSTICS_AGENT_ENDPOINT", null);
        using var key = new EnvironmentScope("DOTNET_DIAGNOSTICS_AGENT_API_KEY", null);
        using var model = new EnvironmentScope("DOTNET_DIAGNOSTICS_AGENT_MODEL", null);

        var configured = BlindedAgentHarness.TryCreateConfiguredTransport(
            out var transport,
            out var configuration,
            out var detail);

        configured.Should().BeFalse();
        transport.Should().BeNull();
        configuration.Should().BeNull();
        detail.Should().StartWith("Blocked:");
    }

    [Theory]
    [InlineData("collect_process_dump", """{"target":"target-1"}""", "tool_not_allowed")]
    [InlineData("collect_thread_snapshot", """{"target":"other"}""", "environment_denied")]
    [InlineData("collect_thread_snapshot", """{"target":"target-1","confirm":true}""", "invalid_arguments")]
    public async Task ToolGateway_RejectsUnapprovedToolTargetAndArguments(
        string tool,
        string arguments,
        string expectedError)
    {
        var gateway = new BlindedDiagnosticToolGateway(0, new AgentHarnessBudget());

        var (result, approval) = await gateway.ExecuteAsync(
            new AgentToolCall("call-rejected", tool, arguments),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(expectedError);
        approval.Attempted.Should().BeTrue();
        approval.Executed.Should().BeFalse();
        approval.Requested.Should().BeFalse();
    }

    private static AgentHarnessRequest Request(ScenarioManifest manifest, string evidenceKind)
        => new(
            manifest,
            new AgentModelConfiguration(
                "scripted",
                "scripted-model",
                new Uri("https://model.example.test/v1/chat/completions"),
                0,
                500),
            new AgentHarnessBudget(),
            Path.Combine(AppContext.BaseDirectory, "unused-agent-report.json"),
            evidenceKind);

    private sealed class ScriptedTransport(IReadOnlyList<AgentModelTurn> turns) : IAgentModelTransport
    {
        private int _index;

        public List<IReadOnlyList<JsonObject>> SeenMessages { get; } = [];

        public Task<AgentModelTurn> CompleteAsync(
            AgentModelConfiguration configuration,
            IReadOnlyList<JsonObject> messages,
            IReadOnlyList<AgentToolDefinition> tools,
            CancellationToken cancellationToken)
        {
            SeenMessages.Add(messages.Select(message => message.DeepClone().AsObject()).ToArray());
            return Task.FromResult(turns[_index++]);
        }
    }

    private sealed class FakeGateway : IBlindedDiagnosticToolGateway
    {
        public IReadOnlyList<AgentToolDefinition> Tools => BlindedDiagnosticToolGateway.ToolDefinitions;

        public int RetainedArtifactBytes { get; private set; }

        public Task<(AgentToolResult Result, AgentApprovalEvent Approval)> ExecuteAsync(
            AgentToolCall call,
            CancellationToken cancellationToken)
        {
            var content =
                """
                {"status":"succeeded","collector":"collect_events/counters","evidence":{"counters":[{"name":"threadpool-queue-length","value":42}]},"limitations":{"harnessTruncated":false}}
                """;
            RetainedArtifactBytes += Encoding.UTF8.GetByteCount(content);
            return Task.FromResult((
                new AgentToolResult(
                    call.Id,
                    call.Name,
                    true,
                    content,
                    BlindedDiagnosticToolGateway.Sha256(content),
                    Encoding.UTF8.GetByteCount(content),
                    false,
                    null),
                new AgentApprovalEvent(
                    call.Id,
                    call.Name,
                    false,
                    "not-required",
                    true,
                    true,
                    "Read-only allowlisted tool.")));
        }
    }

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public string? AuthorizationScheme { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
            => Environment.SetEnvironmentVariable(_name, _original);
    }
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class BlindedAgentRealSmokeTests
{
    [Fact(Timeout = 60_000)]
    [Trait("Category", "ScenarioAgentHarnessLive")]
    public async Task LiveGateway_CollectsAllowedEvidence_AndTargetCleanupStopsOwnedProcess()
    {
        var manifest = ScenarioManifestLoader.LoadAll().Single(value => value.Id == "sync-over-async");
        var target = await AgentScenarioTarget.StartAsync(manifest, CancellationToken.None);
        using var ownedProcess = Process.GetProcessById(target.ProcessId);
        var originalStartTime = ownedProcess.StartTime;
        var gateway = new BlindedDiagnosticToolGateway(
            target.ProcessId,
            new AgentHarnessBudget(MaximumCaptureSeconds: 3));
        try
        {
            var (result, approval) = await gateway.ExecuteAsync(
                new AgentToolCall(
                    "call-live",
                    "collect_events",
                    """{"target":"target-1","kind":"counters","durationSeconds":2}"""),
                CancellationToken.None);

            result.Succeeded.Should().BeTrue(result.ContentJson);
            result.ContentJson.Should().Contain("threadpool-queue-length");
            approval.Executed.Should().BeTrue();
            var observationWithoutTermination = () => AgentScenarioTarget.WaitForTerminationAsync(
                ownedProcess,
                TimeSpan.FromMilliseconds(100));
            await observationWithoutTermination.Should().ThrowAsync<AgentScenarioCleanupException>()
                .WithMessage("*did not terminate*");
        }
        finally
        {
            await target.DisposeAsync();
        }

        ownedProcess.StartTime.Should().Be(originalStartTime);
        await AgentScenarioTarget.WaitForTerminationAsync(ownedProcess, TimeSpan.FromSeconds(2));
        ownedProcess.HasExited.Should().BeTrue();
    }

    [EnvironmentRequiredFact(
        "DOTNET_DIAGNOSTICS_AGENT_REAL_SMOKE",
        "The single real-model smoke requires explicit opt-in and explicitly configured provider settings.",
        Timeout = 180_000)]
    [Trait("Category", "ScenarioAgentRealSmoke")]
    public async Task RealModelSmoke_RunsOnePredeclaredSyncOverAsyncInvestigation()
    {
        Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_AGENT_REAL_SMOKE")
            .Should().Be("1", "only the explicit value 1 authorizes the one bounded model invocation");
        BlindedAgentHarness.TryCreateConfiguredTransport(
            out var transport,
            out var configuration,
            out var detail).Should().BeTrue(detail);
        var manifest = ScenarioManifestLoader.LoadAll().Single(value => value.Id == "sync-over-async");
        var outputPath = Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_AGENT_REPORT_PATH")
            ?? Path.Combine("artifacts", "scenario-agent", "sync-over-async.real-agent.json");
        var request = new AgentHarnessRequest(
            manifest,
            configuration! with { MaximumOutputTokens = Math.Min(configuration.MaximumOutputTokens, 1200) },
            new AgentHarnessBudget(
                MaximumWallTimeSeconds: 45,
                MaximumToolCalls: 3,
                MaximumModelTurns: 4,
                MaximumCaptureSeconds: 10,
                MaximumInputTokens: 10_000,
                MaximumOutputTokens: 1_200,
                MaximumEstimatedCostUsd: 0.25m,
                MaximumResponseBytes: 131_072,
                MaximumArtifactBytes: 393_216),
            outputPath,
            "real-model");

        var report = await BlindedAgentHarness.RunAsync(request, transport!, CancellationToken.None);

        report.EvidenceKind.Should().Be("real-model");
        report.Activation.Status.Should().Be(AgentHarnessStageStatus.Passed);
        report.AgentExecution.Status.Should().Be(
            AgentHarnessStageStatus.Passed,
            $"{report.AgentExecution.FailureKind}: {report.AgentExecution.Detail}");
        report.Assessment.Stage.Status.Should().Be(AgentHarnessStageStatus.Passed);
        report.TotalToolCalls.Should().BeInRange(1, request.Budget.MaximumToolCalls);
    }
}
