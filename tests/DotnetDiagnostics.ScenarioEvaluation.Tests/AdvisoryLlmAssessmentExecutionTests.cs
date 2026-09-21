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
        var output = RequiredEnvironment("DOTNET_DIAGNOSTICS_ADVISORY_LLM_OUTPUT_DIRECTORY");
        if (operation == "prepare")
        {
            AdvisoryLlmAssessment.Prepare(protocol, output);
            return;
        }

        operation.Should().Be("run");
        Environment.GetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable)
            .Should().Be("1", "real-model execution must never be the default test path");
        var executable = RequiredEnvironment("DOTNET_DIAGNOSTICS_AGENT_COPILOT_PATH");
        var home = RequiredEnvironment("DOTNET_DIAGNOSTICS_AGENT_COPILOT_HOME");
        var workRoot = RequiredEnvironment("DOTNET_DIAGNOSTICS_AGENT_COPILOT_WORK_ROOT");
        var cli = new CopilotCliAgentTransport(executable, home, workRoot);
        using var versionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var version = await cli.DetectVersionAsync(versionTimeout.Token);
        version.Should().Be(protocol.PhaseAModel.TransportVersion);
        protocol.PhaseBModel.TransportVersion.Should().Be(version);

        var summary = await AdvisoryLlmAssessment.RunAsync(
            protocol,
            output,
            new CopilotCliAdvisoryTransport(cli),
            CancellationToken.None);

        summary.Cases.Should().HaveCount(8);
        summary.Cases.Sum(value =>
            (value.PhaseA.Status == AdvisoryCallStatus.Succeeded ? 1 : 0)
            + (value.PhaseB.Status == AdvisoryCallStatus.Succeeded ? 1 : 0))
            .Should().Be(16,
                "all predeclared phases must complete; inspect run-summary.json for preserved failures. "
                + "Semantic judgments themselves are not pass/fail criteria");
    }

    private static string RequiredEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"Environment variable {name} is required.");
}
