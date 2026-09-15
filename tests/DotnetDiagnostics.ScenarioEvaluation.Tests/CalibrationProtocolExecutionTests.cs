using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class CalibrationProtocolExecutionTests
{
    [EnvironmentRequiredFact(
        "DOTNET_DIAGNOSTICS_CALIBRATION_RUN_SLOT",
        "A frozen calibration run requires explicit authorization of one predeclared live slot.",
        Timeout = 180_000)]
    [Trait("Category", "ScenarioCalibrationExecution")]
    public async Task FrozenProtocol_RunsOnePredeclaredLiveSlot()
    {
        var protocol = CalibrationProtocols.Load(
            RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_PROTOCOL"));
        var slotId = RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_RUN_SLOT");
        var slot = protocol.Slots.SingleOrDefault(value => value.Id == slotId)
            ?? throw new InvalidOperationException($"Unknown frozen protocol slot '{slotId}'.");
        slot.Kind.Should().Be(
            CalibrationProtocolSlotKind.Live,
            "authored replay slots do not invoke a model or live target");
        BlindedAgentHarness.CurrentProductCommit.Should().Be(protocol.Product.Commit);
        BlindedAgentHarness.CurrentProductVersion.Should().Be(protocol.Product.Version);

        var manifest = CalibrationProtocols.ResolveWorkload(
            protocol,
            slot.Id,
            Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_CALIBRATION_PRIVATE_DEFINITION"));
        if (!ScenarioLiveRunner.SupportsCurrentPlatform(manifest))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                $"Frozen slot '{slot.Id}' is not supported on this platform and remains notRun.");
        }

        BlindedAgentHarness.TryCreateConfiguredTransport(
            out var transport,
            out var configuration,
            out var detail).Should().BeTrue(detail);
        configuration!.Provider.Should().Be(protocol.Model.Provider);
        configuration.Model.Should().Be(protocol.Model.Model);
        (configuration.Version ?? "unavailable").Should().Be(protocol.Model.ModelVersion);
        configuration.TransportVersion.Should().Be(protocol.Model.TransportVersion);

        var request = new AgentHarnessRequest(
            manifest,
            configuration with { MaximumOutputTokens = slot.Budget!.MaximumOutputTokens },
            slot.Budget,
            RequiredEnvironment("DOTNET_DIAGNOSTICS_AGENT_REPORT_PATH"),
            "real-model");

        var report = await BlindedAgentHarness.RunAsync(
            request,
            transport!,
            CancellationToken.None);

        report.AgentExecution.Status.Should().Be(
            AgentHarnessStageStatus.Passed,
            $"{report.AgentExecution.FailureKind}: {report.AgentExecution.Detail}");
        report.Assessment.Stage.Status.Should().Be(AgentHarnessStageStatus.Passed);
    }

    private static string RequiredEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"Environment variable {name} is required.");
}
