using System.Reflection;
using System.Text.Json;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Safety;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Safety;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class InvocationSafetyParityTests
{
    [Fact]
    public void RegisteredToolSurface_HasExactlyOneCoreSafetyRegistrationPerTool()
    {
        var toolNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in PodLocalToolSurfaces.GetSurfaceTypes(
                     enableOrchestratorTools: true,
                     enableAzureDiscoveryTools: true))
        {
            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>()?.Name is { Length: > 0 } name)
                {
                    toolNames.Add(name);
                }
            }
        }

        toolNames.Should().BeEquivalentTo(DiagnosticOperationCatalog.McpOperations);
        foreach (var toolName in toolNames)
        {
            InvocationSafetyRegistry.TryGet(toolName, out var registration).Should().BeTrue();
            registration.Should().NotBeNull();
        }
    }

    [Fact]
    public void DispatcherAllowlists_ReuseCoreCatalog()
    {
        CollectEventsTool.AllowedKinds.Should()
            .BeSameAs(DiagnosticOperationCatalog.CollectEventsKinds.All);
        CollectSampleTool.AllowedKinds.Should()
            .BeSameAs(DiagnosticOperationCatalog.CollectSampleKinds.All);
        InspectProcessTool.AllowedViews.Should()
            .BeSameAs(DiagnosticOperationCatalog.InspectProcessViews.All);
        InspectHeapTool.AllowedSources.Should()
            .BeSameAs(DiagnosticOperationCatalog.HeapSources.All);
        GetBytesTool.AllowedKinds.Should()
            .BeSameAs(DiagnosticOperationCatalog.ByteKinds.All);
        CollectBatchTool.AllowedTools.Should()
            .BeSameAs(DiagnosticOperationCatalog.CollectBatchTools.All);
        ListOrchestratorTool.AllowedKinds.Should()
            .BeSameAs(DiagnosticOperationCatalog.ListOrchestratorKinds.All);
        DiscoverAzureTool.AllowedKinds.Should()
            .BeSameAs(DiagnosticOperationCatalog.DiscoverAzureKinds.All);
        QuerySnapshotTool.RegisteredKinds.Should()
            .BeEquivalentTo(DiagnosticOperationCatalog.QuerySnapshotHandleKinds.All);
    }

    [Fact]
    public void McpNormalizer_ResolvesConditionalJsonArguments()
    {
        var arguments = DeserializeArguments(
            """
            {
              "kind": "cpu",
              "resolveMethodInstantiations": true,
              "exportTrace": true,
              "symbolPath": "srv*https://symbols.example.test"
            }
            """);

        var safety = McpInvocationSafety.Resolve(
            DiagnosticOperationCatalog.CollectSample,
            arguments);

        safety.RiskLevel.Should().Be(InvocationRiskLevel.High);
        safety.TargetImpact.Should().Contain(TargetImpact.PtraceAttach);
        safety.SideEffects.Should().Contain(InvocationSideEffect.WritesArtifact);
        safety.SideEffects.Should().Contain(InvocationSideEffect.ContactsRemoteSymbolServer);
    }

    [Fact]
    public void McpNormalizer_BatchCannotHideHighestRiskChild()
    {
        var arguments = DeserializeArguments(
            """
            {
              "requests": [
                { "tool": "collect_events", "kind": "counters" },
                { "tool": "collect_sample", "kind": "off_cpu" }
              ]
            }
            """);

        var safety = McpInvocationSafety.Resolve(
            DiagnosticOperationCatalog.CollectBatch,
            arguments);

        safety.RiskLevel.Should().Be(InvocationRiskLevel.High);
        safety.ApprovalPolicy.Should().Be(InvocationApprovalPolicy.Acknowledge);
        safety.TargetImpact.Should().Contain(TargetImpact.KernelTracing);
    }

    [Fact]
    public void QuerySnapshotHandleKinds_ResolveTheArtifactSpecificExposure()
    {
        var handles = new MemoryDiagnosticHandleStore();
        var countersHandle = handles.Register(
            123,
            "counters",
            new object(),
            TimeSpan.FromMinutes(1),
            evictWhenProcessExits: false);
        var parametersHandle = handles.Register(
            123,
            "method-params-capture",
            new object(),
            TimeSpan.FromMinutes(1),
            evictWhenProcessExits: false);
        var counters = McpInvocationSafety.Resolve(
            DiagnosticOperationCatalog.QuerySnapshot,
            DeserializeArguments(
                $$"""{ "handle": "{{countersHandle.Id}}", "view": "summary" }"""),
            handles);
        var parameters = McpInvocationSafety.Resolve(
            DiagnosticOperationCatalog.QuerySnapshot,
            DeserializeArguments(
                $$"""{ "handle": "{{parametersHandle.Id}}", "view": "events" }"""),
            handles);

        counters.RiskLevel.Should().Be(InvocationRiskLevel.Low);
        counters.DataExposure.Should().Equal(DataExposure.AggregatedMetrics);
        parameters.RiskLevel.Should().Be(InvocationRiskLevel.Critical);
        parameters.ApprovalPolicy.Should().Be(InvocationApprovalPolicy.HumanApproval);
        parameters.DataExposure.Should().Contain(DataExposure.ParameterValues);
        parameters.DataExposure.Should().Contain(DataExposure.PossiblePii);
        parameters.DataExposure.Should().Contain(DataExposure.PossibleConfidentialData);
    }

    [Fact]
    public void McpNormalizer_ResolvesOpaqueQueryHandleThroughSharedStore()
    {
        var handles = new MemoryDiagnosticHandleStore();
        var handle = handles.Register(
            123,
            DiagnosticOperationCatalog.QuerySnapshotHandleKinds.CpuSample,
            new object(),
            TimeSpan.FromMinutes(1),
            evictWhenProcessExits: false);
        var arguments = DeserializeArguments(
            $$""" 
            {
              "handle": "{{handle.Id}}",
              "view": "summary"
            }
            """);

        var safety = McpInvocationSafety.Resolve(
            DiagnosticOperationCatalog.QuerySnapshot,
            arguments,
            handles);

        safety.RiskLevel.Should().Be(InvocationRiskLevel.Moderate);
        safety.DataExposure.Should().Contain(DataExposure.StackNames);
        safety.DataExposure.Should().NotContain(DataExposure.ParameterValues);
    }

    [Fact]
    public void McpNormalizer_IgnoresSpoofedHandleKindAndUsesStoreKind()
    {
        var handles = new MemoryDiagnosticHandleStore();
        var handle = handles.Register(
            123,
            "method-params-capture",
            new object(),
            TimeSpan.FromMinutes(1),
            evictWhenProcessExits: false);
        var arguments = DeserializeArguments(
            $$"""
            {
              "handle": "{{handle.Id}}",
              "view": "events",
              "handleKind": "counters"
            }
            """);

        var safety = McpInvocationSafety.Resolve(
            DiagnosticOperationCatalog.QuerySnapshot,
            arguments,
            handles);

        safety.RiskLevel.Should().Be(InvocationRiskLevel.Critical);
        safety.DataExposure.Should().Contain(DataExposure.ParameterValues);
    }

    [Fact]
    public void McpNormalizer_LookupFailureIgnoresSpoofAndFailsClosed()
    {
        var arguments = DeserializeArguments(
            """
            {
              "handle": "missing-handle",
              "view": "summary",
              "handleKind": "counters"
            }
            """);

        var safety = McpInvocationSafety.Resolve(
            DiagnosticOperationCatalog.QuerySnapshot,
            arguments,
            new MemoryDiagnosticHandleStore());

        safety.Should().Be(InvocationSafetyRegistry.Get(
            DiagnosticOperationCatalog.QuerySnapshot).MaximumSafety);
        safety.RiskLevel.Should().Be(InvocationRiskLevel.Critical);
    }

    private static Dictionary<string, JsonElement> DeserializeArguments(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
            ?? throw new InvalidOperationException("Failed to deserialize test arguments.");
}
