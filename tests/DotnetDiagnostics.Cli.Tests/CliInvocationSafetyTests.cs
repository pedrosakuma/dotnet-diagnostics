using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Safety;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliInvocationSafetyTests
{
    [Fact]
    public void EveryCliCommand_HasCanonicalSafetyMapping()
    {
        var options = new Dictionary<string, CliOptions>(StringComparer.Ordinal)
        {
            ["docker-bootstrap"] = new() { Command = "docker-bootstrap" },
            ["processes"] = new() { Command = "processes" },
            ["capabilities"] = new() { Command = "capabilities" },
            ["doctor"] = new() { Command = "doctor" },
            ["collect"] = new() { Command = "collect", Kind = "counters" },
            ["inspect"] = new() { Command = "inspect", View = "triage" },
            ["inspect-heap"] = new() { Command = "inspect-heap", Sources = ["live"] },
            ["dump"] = new() { Command = "dump" },
            ["query"] = new() { Command = "query", View = "summary" },
            ["get-bytes"] = new() { Command = "get-bytes", Kind = "module" },
            ["compare"] = new() { Command = "compare" },
            ["investigate"] = new() { Command = "investigate" },
            ["export-summary"] = new() { Command = "export-summary" },
            ["session"] = new() { Command = "session" },
            ["completion"] = new() { Command = "completion" },
        };

        options.Keys.Should().BeEquivalentTo(CliCommands.Commands);
        foreach (var pair in options)
        {
            var request = CliInvocationSafety.CreateRequest(pair.Value);
            var safety = CliInvocationSafety.Resolve(pair.Value);

            InvocationSafetyRegistry.TryGet(request.Operation, out _).Should().BeTrue(
                $"CLI command '{pair.Key}' must map to a registered Core operation");
            safety.Reason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void EveryCliCollectKind_MapsToRegisteredCoreClassification()
    {
        foreach (var kind in CliCommands.CollectKinds)
        {
            var options = new CliOptions { Command = "collect", Kind = kind };

            var safety = CliInvocationSafety.Resolve(options);

            safety.Reason.Should().NotBeNullOrWhiteSpace(
                $"CLI collect kind '{kind}' must share a Core safety classification");
        }
    }

    [Fact]
    public void CliSensitiveModifiers_ResolveWithoutChangingPromptingBehavior()
    {
        var gatedDump = CliInvocationSafety.Resolve(new CliOptions
        {
            Command = "collect",
            Kind = "counters",
            CaptureWhen = "cpu>85",
            CaptureKind = "dump",
        });
        var heap = CliInvocationSafety.Resolve(new CliOptions
        {
            Command = "inspect-heap",
            Sources = ["gcdump"],
            ExportTrace = true,
        });
        var launchedDump = CliInvocationSafety.Resolve(new CliOptions
        {
            Command = "dump",
            Launch = true,
        });

        gatedDump.RiskLevel.Should().Be(InvocationRiskLevel.Critical);
        heap.TargetImpact.Should().Contain(TargetImpact.InducedGc);
        heap.SideEffects.Should().Contain(InvocationSideEffect.WritesArtifact);
        launchedDump.RiskLevel.Should().Be(InvocationRiskLevel.Critical);
        launchedDump.TargetImpact.Should().Contain(TargetImpact.ProcessLaunch);
        launchedDump.TargetImpact.Should().Contain(TargetImpact.ProcessTermination);
    }

    [Fact]
    public void SessionShell_IsLowRiskAndNestedCommandsRemainPerInvocation()
    {
        var safety = CliInvocationSafety.Resolve(new CliOptions { Command = "session" });

        safety.RiskLevel.Should().Be(InvocationRiskLevel.Low);
        safety.ApprovalPolicy.Should().Be(InvocationApprovalPolicy.None);
    }

    [Fact]
    public void QueryHandle_UsesSharedStoreKindOrFailsClosed()
    {
        var handles = new MemoryDiagnosticHandleStore();
        var handle = handles.Register(
            123,
            "counters",
            new object(),
            TimeSpan.FromMinutes(1),
            evictWhenProcessExits: false);
        var options = new CliOptions
        {
            Command = "query",
            Handle = handle.Id,
            View = "summary",
        };

        CliInvocationSafety.Resolve(options, handles)
            .RiskLevel.Should().Be(InvocationRiskLevel.Low);
        CliInvocationSafety.Resolve(options)
            .RiskLevel.Should().Be(InvocationRiskLevel.Critical);
    }
}
