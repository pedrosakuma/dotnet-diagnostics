using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AdvisoryLlmAssessmentExecutionTests
{
    [EnvironmentRequiredFact(
        "DOTNET_DIAGNOSTICS_ADVISORY_LLM_OPERATION",
        "Advisory LLM preparation and execution are explicit local-only operations.",
        Timeout = 2_220_000)]
    [Trait("Category", "AdvisoryLlmAssessmentExecution")]
    public async Task ExplicitLocalWorkflow_PreparesOrRunsFrozenProtocol()
    {
        var operation = RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_OPERATION");
        var protocolPath = RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_PROTOCOL");
        if (operation == "freeze")
        {
            AdvisoryLlmAssessment.FreezeProtocol(
                protocolPath,
                RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_FROZEN_PROTOCOL"));
            return;
        }

        var protocol = AdvisoryLlmAssessment.LoadProtocol(protocolPath);
        if (operation == "continue-prepare")
        {
            AdvisoryLlmAssessment.FreezeContinuationPlan(
                protocol,
                RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_CONTINUATION_DRAFT"),
                RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_CONTINUATION_PLAN"));
            return;
        }
        if (operation == "followup-freeze")
        {
            var (_, detectedVersion) = await CreateTransportAsync();
            AdvisoryLlmAssessment.FreezeFollowupPlan(
                protocol,
                RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_FOLLOWUP_DRAFT"),
                RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_FOLLOWUP_PLAN"),
                detectedVersion);
            return;
        }

        var output = RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_OUTPUT_DIRECTORY");
        if (operation == "followup-prepare")
        {
            var followupPlan = AdvisoryLlmAssessment.LoadFollowupPlan(
                protocol,
                RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_FOLLOWUP_PLAN"));
            AdvisoryLlmAssessment.PrepareFollowup(protocol, followupPlan, output);
            return;
        }
        if (operation == "prepare")
        {
            AdvisoryLlmAssessment.Prepare(protocol, output);
            return;
        }

        operation.Should().BeOneOf("run", "continue", "followup");
        if (operation == "followup")
        {
            Environment.GetEnvironmentVariable(AdvisoryLlmAssessment.FollowupAuthorizationVariable)
                .Should().Be("1", "semantic follow-up execution must never be the default test path");
            var followupPlan = AdvisoryLlmAssessment.LoadFollowupPlan(
                protocol,
                RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_FOLLOWUP_PLAN"));
            var (followupTransport, followupVersion) = await CreateTransportAsync();
            followupVersion.Should().Be(followupPlan.PhaseAModel.TransportVersion);
            followupPlan.PhaseBModel.TransportVersion.Should().Be(followupVersion);
            var followup = await AdvisoryLlmAssessment.RunFollowupAsync(
                protocol,
                followupPlan,
                output,
                followupTransport,
                CancellationToken.None);
            followup.ActualNewCalls.Should().BeLessThanOrEqualTo(8);
            followup.TechnicallyComplete.Should().BeTrue(
                "technical completion means every declared safe semantic extraction, primary "
                + "comparison, and order control completed; semantic ratings are not pass/fail");
            return;
        }
        if (operation == "continue")
        {
            Environment.GetEnvironmentVariable(AdvisoryLlmAssessment.ContinuationAuthorizationVariable)
                .Should().Be("1", "continuation model execution must never be the default test path");
            var continuationPlan = AdvisoryLlmAssessment.LoadContinuationPlan(
                protocol,
                RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_CONTINUATION_PLAN"));
            var (continuationTransport, continuationVersion) = await CreateTransportAsync();
            continuationVersion.Should().Be(protocol.PhaseBModel.TransportVersion);
            var continuation = await AdvisoryLlmAssessment.ContinueAsync(
                protocol,
                continuationPlan,
                output,
                continuationTransport,
                CancellationToken.None);
            continuation.ActualNewModelCalls.Should().BeLessThanOrEqualTo(6);
            continuation.TechnicallyComplete.Should().BeTrue(
                "the bounded continuation preserves unavailable cases and therefore intentionally "
                + "fails this technical-completeness assertion after writing its partial summary");
            return;
        }

        Environment.GetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable)
            .Should().Be("1", "real-model execution must never be the default test path");
        var (transport, version) = await CreateTransportAsync();
        version.Should().Be(protocol.PhaseAModel.TransportVersion);
        protocol.PhaseBModel.TransportVersion.Should().Be(version);

        var summary = await AdvisoryLlmAssessment.RunAsync(
            protocol,
            output,
            transport,
            CancellationToken.None);

        summary.Cases.Should().HaveCount(8);
        summary.Cases.Sum(value =>
            (value.PhaseA.Status == AdvisoryCallStatus.Succeeded ? 1 : 0)
            + (value.PhaseB.Status == AdvisoryCallStatus.Succeeded ? 1 : 0))
            .Should().Be(16,
                "all predeclared phases must complete; inspect run-summary.json for preserved failures. "
                + "Semantic judgments themselves are not pass/fail criteria");
    }

    private static async Task<(IAdvisoryStructuredTransport Transport, string Version)> CreateTransportAsync()
    {
        var executable = RequiredEnvironment("DOTNET_DIAGNOSTICS_AGENT_COPILOT_PATH");
        var home = RequiredEnvironment("DOTNET_DIAGNOSTICS_AGENT_COPILOT_HOME");
        var workRoot = RequiredEnvironment("DOTNET_DIAGNOSTICS_AGENT_COPILOT_WORK_ROOT");
        var cli = new CopilotCliAgentTransport(executable, home, workRoot);
        using var versionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var version = await cli.DetectVersionAsync(versionTimeout.Token);
        return (new CopilotCliAdvisoryTransport(cli), version);
    }

    private static string RequiredEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"Environment variable {name} is required.");
}
