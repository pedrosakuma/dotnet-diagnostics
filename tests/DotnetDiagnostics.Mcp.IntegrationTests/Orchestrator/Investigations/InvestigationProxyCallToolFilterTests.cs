using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.IntegrationTests;
using DotnetDiagnostics.Mcp.Observability;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator;

/// <summary>
/// Unit tests for <see cref="InvestigationProxyCallToolFilter.InvokeAsync"/>. The filter
/// is exercised through its core method so tests don't have to construct an McpServer
/// (its abstract surface is non-trivial; the surrounding <see cref="InvestigationProxyEndpointTests"/>
/// covers the wired-up DI path end-to-end).
/// </summary>
public sealed class InvestigationProxyCallToolFilterTests
{
    private static readonly InvestigationHandle ActiveHandle = new(
        HandleId: "inv-1",
        Namespace: "ns",
        PodName: "pod-a",
        TargetContainerName: "api",
        EphemeralContainerName: "diag-1",
        PodLocalBearerToken: "pod-bearer",
        State: InvestigationState.Active,
        AttachedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
        InternalScopeDelegationKey: "test-delegation-key");

    private static readonly InvestigationHandle FailedHandle = ActiveHandle with { HandleId = "inv-failed", State = InvestigationState.Failed };

    [Fact]
    public async Task PassesThrough_WhenSessionIdIsNullOrEmpty()
    {
        var fx = new Fixture();
        var result = await fx.Invoke(Params("collect_events"), sessionId: null);

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PassesThrough_WhenSessionHasNoBinding()
    {
        var fx = new Fixture();
        var result = await fx.Invoke(Params("collect_events"), sessionId: "session-unbound");

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("list_orchestrator")]
    [InlineData("attach_to_pod")]
    [InlineData("detach_from_pod")]
    public async Task PassesThrough_WhenToolIsOrchestratorBypassed(string toolName)
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-1", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(Params(toolName), sessionId: "session-1");

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("4242")]
    [InlineData("\"4242\"")]
    public async Task PassesThrough_WhenArgumentsCarryExplicitProcessId(string processIdJson)
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-pid", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["processId"] = JsonDocument.Parse(processIdJson).RootElement,
        };
        var result = await fx.Invoke(Params("collect_events", args), sessionId: "session-pid");

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PassesThrough_WhenBoundHandleIsNotActive()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-fail", FailedHandle.HandleId);
        fx.Store.Add(FailedHandle);

        var result = await fx.Invoke(Params("collect_events"), sessionId: "session-fail");

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PassesThrough_WhenBoundHandleIsMissingFromStore()
    {
        var fx = new Fixture();
        // Binder knows about a handle the store evicted (race during TTL reaping).
        fx.Binder.Bind("session-orphan", "inv-vanished");

        var result = await fx.Invoke(Params("collect_events"), sessionId: "session-orphan");

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Forwards_WhenSessionBoundToActiveHandleAndArgsImplicit()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-ok", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var upstream = new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "upstream-ok" } },
        };
        fx.ProxyClient.Next = (_, _, _) => Task.FromResult(upstream);

        var p = Params("collect_events", new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement("counters"),
        });
        p.Meta = new JsonObject { ["progressToken"] = "progress-707" };
        p.Task = new McpTaskMetadata { TimeToLive = TimeSpan.FromSeconds(30) };
        var result = await fx.Invoke(p, sessionId: "session-ok");

        result.Should().BeSameAs(upstream);
        fx.ProxyClient.CallCount.Should().Be(1);
        fx.ProxyClient.LastHandle.Should().BeSameAs(ActiveHandle);
        fx.ProxyClient.LastRequest.Should().NotBeSameAs(p);
        fx.ProxyClient.LastRequest!.Meta.Should().BeSameAs(p.Meta);
        fx.ProxyClient.LastRequest.ProgressToken!.Value.Token.Should().Be("progress-707");
        fx.ProxyClient.LastRequest!.Task.Should().BeSameAs(p.Task);
        fx.ProxyClient.LastRequest!.Arguments.Should().ContainKey(ToolScopeDelegation.ArgumentName);
        p.Arguments.Should().NotContainKey(ToolScopeDelegation.ArgumentName);
        fx.LocalInvocations.Should().Be(0);
    }

    [Fact]
    public async Task TaskAugmentedForwarding_IsOwnedByOuterSession_AndRunsPodCallWithoutNestedTask()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-task", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);
        var metadata = new JsonObject
        {
            ["progressToken"] = "progress-task-707",
            ["extension"] = "preserved",
        };
        var request = Params("collect_events", new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement("counters"),
        });
        request.Meta = metadata;
        request.Task = new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) };
        var promoterCalls = 0;

        var result = await fx.Invoke(
            request,
            "session-task",
            taskPromoter: async (forward, ct) =>
            {
                Interlocked.Increment(ref promoterCalls);
                fx.ProxyClient.CallCount.Should().Be(0, "pod execution must start inside the outer task");
                return await forward(ct);
            });

        result.IsError.Should().NotBe(true);
        promoterCalls.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(1);
        fx.ProxyClient.LastRequest!.Task.Should().BeNull(
            "the pod must not create an orphan task in its private MCP session");
        fx.ProxyClient.LastRequest.Meta.Should().BeSameAs(metadata);
        fx.ProxyClient.LastRequest.ProgressToken!.Value.Token.Should().Be("progress-task-707");
    }

    [Fact]
    public async Task TaskAugmentedExportForwarding_DelegatesCallerEvidenceScopes()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "investigation-export",
            "eventpipe"));
        fx.Binder.Bind("session-export-task", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);
        var request = Params("export_investigation_summary", new Dictionary<string, JsonElement>
        {
            ["handle"] = JsonSerializer.SerializeToElement("opaque-cpu-handle"),
        });
        request.Task = new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) };
        var promoterCalls = 0;

        var result = await fx.Invoke(
            request,
            "session-export-task",
            taskPromoter: async (forward, ct) =>
            {
                Interlocked.Increment(ref promoterCalls);
                return await forward(ct);
            });

        result.IsError.Should().NotBe(true);
        promoterCalls.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(1);
        fx.ProxyClient.LastRequest!.Task.Should().BeNull();
        fx.LocalInvocations.Should().Be(0);
        ToolScopeDelegation.TryConsume(
            fx.ProxyClient.LastRequest,
            ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable),
            new ToolScopeResolutionPolicies(null, null, null, null),
            ActiveHandle.InternalScopeDelegationKey,
            TimeProvider.System,
            out var delegatedPrincipal,
            out var failure).Should().BeTrue(failure);
        delegatedPrincipal!.Scopes.Should().BeEquivalentTo(
            "investigation-export",
            "eventpipe");
    }

    [Fact]
    public async Task ProxiedExport_PreservesProducingToolEvidenceFromPod()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "investigation-export",
            "eventpipe"));
        fx.Binder.Bind("session-export-producer", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);
        var upstream = new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = """{"Summary":{"Evidence":[{"SourceTool":"collect_events"}]}}""",
                },
            ],
        };
        fx.ProxyClient.Next = (_, _, _) => Task.FromResult(upstream);

        var result = await fx.Invoke(
            Params("export_investigation_summary", new Dictionary<string, JsonElement>
            {
                ["handle"] = JsonSerializer.SerializeToElement("gated-cpu-handle"),
            }),
            "session-export-producer");

        result.Should().BeSameAs(upstream);
        result.Content.OfType<TextContentBlock>().Single().Text.Should()
            .Contain("\"SourceTool\":\"collect_events\"");
        fx.LocalInvocations.Should().Be(0);
    }

    [Fact]
    public async Task TaskAugmentedExport_MissingInvestigationScope_IsRejectedBeforePromotion()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "eventpipe"));
        fx.Binder.Bind("session-export-task-denied", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);
        var request = Params("export_investigation_summary", new Dictionary<string, JsonElement>
        {
            ["handle"] = JsonSerializer.SerializeToElement("opaque-cpu-handle"),
        });
        request.Task = new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) };
        var promoterCalls = 0;

        var result = await fx.Invoke(
            request,
            "session-export-task-denied",
            taskPromoter: (_, _) =>
            {
                promoterCalls++;
                throw new InvalidOperationException("Unauthorized exports must not be promoted.");
            });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("investigation-export");
        promoterCalls.Should().Be(0);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task TaskAugmentedExport_MissingEvidenceScope_IsDelegatedWithoutWidening()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "investigation-export"));
        fx.Binder.Bind("session-export-task-limited", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);
        var request = Params("export_investigation_summary", new Dictionary<string, JsonElement>
        {
            ["handle"] = JsonSerializer.SerializeToElement("opaque-evidence-handle"),
        });
        request.Task = new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) };
        var promoterCalls = 0;

        var result = await fx.Invoke(
            request,
            "session-export-task-limited",
            taskPromoter: async (forward, ct) =>
            {
                promoterCalls++;
                return await forward(ct);
            });

        result.IsError.Should().NotBe(true);
        promoterCalls.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(1);
        ToolScopeDelegation.TryConsume(
            fx.ProxyClient.LastRequest!,
            ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable),
            new ToolScopeResolutionPolicies(null, null, null, null),
            ActiveHandle.InternalScopeDelegationKey,
            TimeProvider.System,
            out var delegatedPrincipal,
            out var failure).Should().BeTrue(failure);
        delegatedPrincipal!.Scopes.Should().BeEquivalentTo("investigation-export");
    }

    [Theory]
    [InlineData(false, "or")]
    [InlineData(false, "and")]
    [InlineData(false, "or+and")]
    [InlineData(true, "or")]
    [InlineData(true, "and")]
    [InlineData(true, "or+and")]
    public async Task ProxyAndTaskDenials_PreserveStructuredScopeSemantics(
        bool taskAugmented,
        string requirementShape)
    {
        var (toolName, arguments, scopes) = requirementShape switch
        {
            "or" => (
                "query_snapshot",
                new Dictionary<string, JsonElement>
                {
                    ["handle"] = JsonSerializer.SerializeToElement("opaque"),
                },
                new[] { "orchestrator-attach" }),
            "and" => (
                "collect_process_dump",
                new Dictionary<string, JsonElement>(),
                new[] { "orchestrator-attach", "ptrace" }),
            "or+and" => (
                "inspect_process",
                new Dictionary<string, JsonElement>
                {
                    ["view"] = JsonSerializer.SerializeToElement("requests-now"),
                },
                new[] { "orchestrator-attach", "read-counters" }),
            _ => throw new InvalidOperationException($"Unknown requirement shape '{requirementShape}'."),
        };
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(scopes));
        fx.Binder.Bind("session-contract", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);
        var request = Params(toolName, arguments);
        if (taskAugmented)
        {
            request.Task = new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) };
        }
        var promoterCalls = 0;

        var result = await fx.Invoke(
            request,
            "session-contract",
            taskPromoter: (_, _) =>
            {
                promoterCalls++;
                throw new InvalidOperationException("Denied calls must not be promoted.");
            });

        result.IsError.Should().BeTrue();
        fx.ProxyClient.CallCount.Should().Be(0);
        fx.LocalInvocations.Should().Be(0);
        promoterCalls.Should().Be(0);
        var (summary, error) = ParseForbidden(result);

        switch (requirementShape)
        {
            case "or":
                summary.Should().Contain("requires any of [");
                error.GetProperty("semantics").GetString().Should().Be("any");
                error.GetProperty("any_of_scopes").EnumerateArray().Should().NotBeEmpty();
                error.GetProperty("all_of_scopes").EnumerateArray().Should().BeEmpty();
                error.GetProperty("any_of_satisfied").GetBoolean().Should().BeFalse();
                error.GetProperty("missing_all_of_scopes").EnumerateArray().Should().BeEmpty();
                break;
            case "and":
                summary.Should().Contain("requires all of [dump-write, ptrace]");
                error.GetProperty("semantics").GetString().Should().Be("all");
                error.GetProperty("any_of_scopes").EnumerateArray().Should().BeEmpty();
                error.GetProperty("all_of_scopes").EnumerateArray()
                    .Select(static scope => scope.GetString()).Should().Equal("dump-write", "ptrace");
                error.GetProperty("missing_all_of_scopes").EnumerateArray()
                    .Select(static scope => scope.GetString()).Should().Equal("dump-write");
                break;
            case "or+and":
                summary.Should().Contain(
                    "requires any of [read-counters, ptrace] and all of [ptrace]");
                error.GetProperty("semantics").GetString().Should().Be("any+all");
                error.GetProperty("any_of_scopes").EnumerateArray()
                    .Select(static scope => scope.GetString()).Should().Equal("read-counters", "ptrace");
                error.GetProperty("all_of_scopes").EnumerateArray()
                    .Select(static scope => scope.GetString()).Should().Equal("ptrace");
                error.GetProperty("any_of_satisfied").GetBoolean().Should().BeTrue();
                error.GetProperty("missing_all_of_scopes").EnumerateArray()
                    .Select(static scope => scope.GetString()).Should().Equal("ptrace");
                error.GetProperty("message").GetString().Should().Be(
                    "tool requires mandatory scope 'ptrace'");
                break;
        }
    }

    [Fact]
    public async Task TaskAugmentedForwarding_RefusesExecution_WhenHandleClosesBeforeTaskStarts()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-task-close", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);
        var request = Params("collect_events", new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement("counters"),
        });
        request.Task = new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) };

        var result = await fx.Invoke(
            request,
            "session-task-close",
            taskPromoter: async (forward, ct) =>
            {
                fx.Store.Update(ActiveHandle with { State = InvestigationState.Closed });
                return await forward(ct);
            });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("became inactive");
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Forwards_WhenExplicitInvestigationHandleIdIsSupplied()
    {
        var fx = new Fixture();
        fx.Store.Add(ActiveHandle);

        var upstream = new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "upstream-ok" } },
        };
        fx.ProxyClient.Next = (_, _, _) => Task.FromResult(upstream);

        var p = Params("collect_events", new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement("counters"),
            [InvestigationRoutingArguments.InvestigationHandleIdArgument] = JsonSerializer.SerializeToElement(ActiveHandle.HandleId),
        });
        var result = await fx.Invoke(p, sessionId: null);

        result.Should().BeSameAs(upstream);
        fx.ProxyClient.CallCount.Should().Be(1);
        fx.ProxyClient.LastHandle.Should().BeSameAs(ActiveHandle);
        fx.ProxyClient.LastRequest!.Arguments.Should().NotContainKey(InvestigationRoutingArguments.InvestigationHandleIdArgument);
        fx.LocalInvocations.Should().Be(0);
    }

    [Fact]
    public async Task RejectsDump_WhenCallerOnlyHasOrchestratorAttachAndPtrace()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes("orchestrator-attach", "ptrace"));
        fx.Binder.Bind("session-dump", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(Params("collect_process_dump"), "session-dump");

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("dump-write");
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RejectsMissingScope_BeforeExplicitHandleLookup()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes("orchestrator-attach", "ptrace"));
        var result = await fx.Invoke(
            Params("collect_process_dump", new Dictionary<string, JsonElement>
            {
                [InvestigationRoutingArguments.InvestigationHandleIdArgument] =
                    JsonSerializer.SerializeToElement("inv-does-not-exist"),
            }),
            sessionId: null);

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("dump-write");
        result.Content.OfType<TextContentBlock>().Single().Text.Should().NotContain("unknown or no longer active");
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RejectsPtraceTool_WhenCallerOnlyHasOrchestratorAttach()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes("orchestrator-attach"));
        fx.Binder.Bind("session-ptrace", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(Params("collect_thread_snapshot"), "session-ptrace");

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("ptrace");
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RejectsExport_BeforeForwarding_WhenInvestigationScopeIsMissing()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "eventpipe"));
        fx.Binder.Bind("session-export-denied", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(
            Params("export_investigation_summary", new Dictionary<string, JsonElement>
            {
                ["handle"] = JsonSerializer.SerializeToElement("opaque-cpu-handle"),
            }),
            "session-export-denied");

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("investigation-export");
        fx.ProxyClient.CallCount.Should().Be(0);
        fx.LocalInvocations.Should().Be(0);
    }

    [Theory]
    [InlineData("read-counters")]
    [InlineData("eventpipe")]
    [InlineData("ptrace")]
    public async Task ForwardsExport_WithRequestBoundCallerEvidenceScope(string evidenceScope)
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "investigation-export",
            evidenceScope));
        fx.Binder.Bind("session-export-allowed", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(
            Params("export_investigation_summary", new Dictionary<string, JsonElement>
            {
                ["handle"] = JsonSerializer.SerializeToElement("opaque-evidence-handle"),
            }),
            "session-export-allowed");

        result.IsError.Should().BeNull();
        fx.ProxyClient.CallCount.Should().Be(1);
        fx.LocalInvocations.Should().Be(0);
        ToolScopeDelegation.TryConsume(
            fx.ProxyClient.LastRequest!,
            ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable),
            new ToolScopeResolutionPolicies(null, null, null, null),
            ActiveHandle.InternalScopeDelegationKey,
            TimeProvider.System,
            out var delegatedPrincipal,
            out var failure).Should().BeTrue(failure);
        delegatedPrincipal!.Scopes.Should().BeEquivalentTo(
            "investigation-export",
            evidenceScope);
    }

    [Fact]
    public async Task ForwardsExport_WithoutSynthesizingMissingEvidenceScope()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "investigation-export"));
        fx.Binder.Bind("session-export-limited", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(
            Params("export_investigation_summary", new Dictionary<string, JsonElement>
            {
                ["handle"] = JsonSerializer.SerializeToElement("opaque-evidence-handle"),
            }),
            "session-export-limited");

        result.IsError.Should().BeNull();
        fx.ProxyClient.CallCount.Should().Be(1);
        ToolScopeDelegation.TryConsume(
            fx.ProxyClient.LastRequest!,
            ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable),
            new ToolScopeResolutionPolicies(null, null, null, null),
            ActiveHandle.InternalScopeDelegationKey,
            TimeProvider.System,
            out var delegatedPrincipal,
            out var failure).Should().BeTrue(failure);
        delegatedPrincipal!.Scopes.Should().BeEquivalentTo("investigation-export");
    }

    [Fact]
    public async Task ForwardsExport_WithExplicitInvestigationHandleId_WithoutLocalExecution()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "investigation-export",
            "eventpipe"));
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(
            Params("export_investigation_summary", new Dictionary<string, JsonElement>
            {
                ["handle"] = JsonSerializer.SerializeToElement("opaque-cpu-handle"),
                [InvestigationRoutingArguments.InvestigationHandleIdArgument] =
                    JsonSerializer.SerializeToElement(ActiveHandle.HandleId),
            }),
            sessionId: null);

        result.IsError.Should().BeNull();
        fx.ProxyClient.CallCount.Should().Be(1);
        fx.LocalInvocations.Should().Be(0);
        fx.ProxyClient.LastRequest!.Arguments.Should()
            .NotContainKey(InvestigationRoutingArguments.InvestigationHandleIdArgument);
        ToolScopeDelegation.TryConsume(
            fx.ProxyClient.LastRequest,
            ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable),
            new ToolScopeResolutionPolicies(null, null, null, null),
            ActiveHandle.InternalScopeDelegationKey,
            TimeProvider.System,
            out var delegatedPrincipal,
            out var failure).Should().BeTrue(failure);
        delegatedPrincipal!.Scopes.Should().BeEquivalentTo(
            "investigation-export",
            "eventpipe");
    }

    [Fact]
    public async Task RejectsModifierGatedCall_WhenLiteralModifierScopeIsMissing()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes("orchestrator-attach", "eventpipe"));
        fx.Binder.Bind("session-sensitive", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(
            Params("collect_sample", new Dictionary<string, JsonElement>
            {
                ["kind"] = JsonSerializer.SerializeToElement("method-params"),
            }),
            "session-sensitive");

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("sensitive-parameter-read");
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("RETENTION-PATHS")]
    [InlineData("GROWTH")]
    public async Task RejectsMixedCaseSensitiveHeapView_WhenSensitiveHeapScopeIsMissing(string view)
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "heap-read"));
        fx.Binder.Bind("session-retention", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(
            Params("query_snapshot", new Dictionary<string, JsonElement>
            {
                ["handle"] = JsonSerializer.SerializeToElement("heap-handle"),
                ["view"] = JsonSerializer.SerializeToElement(view),
            }),
            "session-retention");

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Contain("sensitive-heap-read");
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("collect_sample", "kind", "method-params", "eventpipe", "sensitive-parameter-read")]
    [InlineData("get_bytes", "kind", "delete", "module-bytes-read", "delete-artifact")]
    [InlineData("query_snapshot", "handle", "opaque", "eventpipe", "sensitive-parameter-read")]
    [InlineData("query_snapshot", "view", "RETENTION-PATHS", "heap-read", "sensitive-heap-read")]
    [InlineData("query_snapshot", "view", "GROWTH", "heap-read", "sensitive-heap-read")]
    public async Task Forwards_ModifierGatedCall_With_RequestBound_Exact_Delegation(
        string toolName,
        string argumentName,
        string argumentValue,
        string primaryScope,
        string modifierScope)
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            primaryScope,
            modifierScope));
        fx.Binder.Bind("session-modifier", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(
            Params(toolName, new Dictionary<string, JsonElement>
            {
                [argumentName] = JsonSerializer.SerializeToElement(argumentValue),
            }),
            "session-modifier");

        result.IsError.Should().BeNull();
        fx.ProxyClient.CallCount.Should().Be(1);
        var delegatedRequest = fx.ProxyClient.LastRequest!;
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        ToolScopeDelegation.TryConsume(
            delegatedRequest,
            registry,
            new ToolScopeResolutionPolicies(null, null, null, null),
            ActiveHandle.InternalScopeDelegationKey,
            TimeProvider.System,
            out var delegatedPrincipal,
            out var failure).Should().BeTrue(failure);
        delegatedPrincipal!.Scopes.Should().BeEquivalentTo(primaryScope, modifierScope);
    }

    [Fact]
    public async Task Forwards_OpaqueEventPipeQuery_WithoutUnrelatedPrimaryScopes()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "eventpipe"));
        fx.Binder.Bind("session-eventpipe-query", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(
            Params("query_snapshot", new Dictionary<string, JsonElement>
            {
                ["handle"] = JsonSerializer.SerializeToElement("opaque"),
                ["view"] = JsonSerializer.SerializeToElement("summary"),
            }),
            "session-eventpipe-query");

        result.IsError.Should().BeNull();
        var delegatedRequest = fx.ProxyClient.LastRequest!;
        ToolScopeDelegation.TryConsume(
            delegatedRequest,
            ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable),
            new ToolScopeResolutionPolicies(null, null, null, null),
            ActiveHandle.InternalScopeDelegationKey,
            TimeProvider.System,
            out var delegatedPrincipal,
            out var failure).Should().BeTrue(failure);
        delegatedPrincipal!.Scopes.Should().BeEquivalentTo("eventpipe");
    }

    [Theory]
    [InlineData(
        "collect_sample",
        "eventpipe",
        "{\"kind\":\"cpu\",\"symbolPath\":\"srv*/symbols*https://symbols.example.test\"}")]
    [InlineData(
        "collect_events",
        "eventpipe",
        "{\"kind\":\"event_source\",\"providerName\":\"Custom.Provider\",\"unsafeProvider\":true}")]
    public async Task Policy_Alternative_Allows_Investigation_Proxy(
        string toolName,
        string primaryScope,
        string argumentsJson)
    {
        var security = new SecurityOptions
        {
            SymbolServerAllowlist = ["symbols.example.test"],
            EventSourceAllowlist = ["Custom.Provider"],
        };
        var fx = new Fixture(TestPrincipalAccessors.WithScopes("orchestrator-attach", primaryScope))
        {
            Policies = new ToolScopeResolutionPolicies(
                new SymbolServerAllowlist(security),
                new EventSourceAllowlist(security),
                new SensitiveValueGate(security),
                new OrchestratorOptions()),
        };
        fx.Binder.Bind("session-policy", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(
            Params(
                toolName,
                JsonDocument.Parse(argumentsJson).RootElement.EnumerateObject()
                    .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal)),
            "session-policy");

        result.IsError.Should().BeNull();
        fx.ProxyClient.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RejectsGatedDumpExploit_WithExplicitInvestigationHandleId()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "read-counters"));
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(
            Params("collect_events", new Dictionary<string, JsonElement>
            {
                ["kind"] = JsonSerializer.SerializeToElement("counters"),
                ["triggerWhen"] = JsonSerializer.SerializeToElement("always-trigger"),
                ["captureKind"] = JsonSerializer.SerializeToElement("Dump"),
                ["confirmDump"] = JsonSerializer.SerializeToElement(true),
                [InvestigationRoutingArguments.InvestigationHandleIdArgument] =
                    JsonSerializer.SerializeToElement(ActiveHandle.HandleId),
            }),
            sessionId: null);

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("dump-write");
        fx.ProxyClient.CallCount.Should().Be(0);
        fx.LocalInvocations.Should().Be(0);
    }

    [Theory]
    [MemberData(
        nameof(ToolScopeAttributesTests.ArgumentAwareScopeCases),
        MemberType = typeof(ToolScopeAttributesTests))]
    public async Task ArgumentAwareDenial_MatchesLocalAuthorization(
        string toolName,
        IDictionary<string, JsonElement> arguments,
        string[] heldScopes,
        string missingScope,
        bool _)
    {
        var scopes = heldScopes.Append("orchestrator-attach").ToArray();
        var principalAccessor = TestPrincipalAccessors.WithScopes(scopes);
        var fx = new Fixture(principalAccessor);
        fx.Binder.Bind("session-parity", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var localDecision = fx.ScopeRegistry.Authorize(
            toolName,
            arguments,
            principalAccessor.Current);
        var proxyDecision = fx.ScopeRegistry.Authorize(
            toolName,
            arguments,
            principalAccessor.Current,
            proxyInvocation: true);
        var result = await fx.Invoke(Params(toolName, arguments), "session-parity");

        localDecision.IsAllowed.Should().BeFalse();
        localDecision.MissingScope.Should().Be(missingScope);
        proxyDecision.IsAllowed.Should().BeFalse();
        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain(proxyDecision.MissingScope);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ForwardsAllowedCall_WhenAttachAndDiagnosticScopeArePresent()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes("orchestrator-attach", "read-counters"));
        fx.Binder.Bind("session-counters", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(
            Params("collect_events", new Dictionary<string, JsonElement>
            {
                ["kind"] = JsonSerializer.SerializeToElement("counters"),
            }),
            "session-counters");

        result.IsError.Should().NotBeTrue();
        fx.ProxyClient.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RejectsSameDisplayName_WhenOwnershipKeysDiffer()
    {
        const string displayName = "shared-display";
        var fx = new Fixture(TestPrincipalAccessors.WithIdentity(
            displayName,
            PrincipalOwnershipKey.ForJwt(
                "oidc",
                "https://issuer-b.example.test",
                "audience",
                "client",
                "subject"),
            "orchestrator-attach",
            "read-counters"));
        fx.Binder.Bind("session-owner-collision", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle with
        {
            OwnerBearerName = displayName,
            OwnerPrincipalKey = PrincipalOwnershipKey.ForJwt(
                "oidc",
                "https://issuer-a.example.test",
                "audience",
                "client",
                "subject"),
        });

        var result = await fx.Invoke(
            Params("collect_events", new Dictionary<string, JsonElement>
            {
                ["kind"] = JsonSerializer.SerializeToElement("counters"),
            }),
            "session-owner-collision");

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("different bearer identity");
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Forwards_WhenExplicitInvestigationHandleIdIsSupplied_ByAdminBearer()
    {
        var fx = new Fixture(TestPrincipalAccessors.WithScopes(
            "orchestrator-attach",
            "orchestrator-admin",
            "read-counters"));
        fx.Store.Add(ActiveHandle with { OwnerBearerName = "somebody-else" });

        var upstream = new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "upstream-ok" } },
        };
        fx.ProxyClient.Next = (_, _, _) => Task.FromResult(upstream);

        var p = Params("collect_events", new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement("counters"),
            [InvestigationRoutingArguments.InvestigationHandleIdArgument] = JsonSerializer.SerializeToElement(ActiveHandle.HandleId),
        });
        var result = await fx.Invoke(p, sessionId: null);

        result.Should().BeSameAs(upstream);
        fx.ProxyClient.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ReturnsError_WhenExplicitInvestigationHandleIdIsMissing()
    {
        var fx = new Fixture();

        var result = await fx.Invoke(Params("collect_events", new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement("counters"),
            [InvestigationRoutingArguments.InvestigationHandleIdArgument] = JsonSerializer.SerializeToElement("inv-missing"),
        }), sessionId: null);

        result.IsError.Should().BeTrue();
        fx.LocalInvocations.Should().Be(0);
        fx.ProxyClient.CallCount.Should().Be(0);
        result.Content.Should().ContainSingle();
        result.Content![0].Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Contain("unknown or no longer active");
    }

    [Fact]
    public async Task DoesNotForward_DistributedTraceFanout_RunsLocallyEvenWhenBound()
    {
        // #437 — collect_events(kind="distributed_trace") is an orchestrator-side fan-out: even when
        // the session is bound to a single Active handle it must execute LOCALLY (so it can enumerate
        // every attached Pod), never be proxied into the one bound Pod.
        var fx = new Fixture();
        fx.Binder.Bind("session-trace", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var p = Params("collect_events", new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement(" DISTRIBUTED_TRACE "),
            ["traceId"] = JsonSerializer.SerializeToElement("0af7651916cd43dd8448eb211c80319c"),
        });
        var result = await fx.Invoke(p, sessionId: "session-trace");

        result.IsError.Should().BeNull();
        fx.LocalInvocations.Should().Be(1);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task DoesNotForward_DistributedTraceFanout_PreservesExplicitHandleIdsForLocalExecution()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-trace", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var p = Params("collect_events", new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement("distributed_trace"),
            [InvestigationRoutingArguments.InvestigationHandleIdsArgument] = JsonSerializer.SerializeToElement(new[] { ActiveHandle.HandleId }),
            ["traceId"] = JsonSerializer.SerializeToElement("0af7651916cd43dd8448eb211c80319c"),
        });
        await fx.Invoke(p, sessionId: "session-trace");

        fx.LastLocalRequest!.Arguments.Should().ContainKey(InvestigationRoutingArguments.InvestigationHandleIdsArgument);
        fx.ProxyClient.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("collect_events", "distributed_trace", true)]
    [InlineData("collect_events", "replica_counters", true)]
    [InlineData("collect_events", " DISTRIBUTED_TRACE ", true)]
    [InlineData("collect_events", "REPLICA_COUNTERS", true)]
    [InlineData("collect_events", "activities", false)]
    [InlineData("collect_events", "counters", false)]
    [InlineData("inspect_process", "distributed_trace", false)]
    public void IsOrchestratorFanout_MatchesOnlyCollectEventsFanoutKinds(string tool, string kind, bool expected)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement(kind),
        };

        InvestigationProxyCallToolFilter.IsOrchestratorFanout(tool, args).Should().Be(expected);
    }

    [Fact]
    public void IsOrchestratorFanout_FalseWhenKindMissing()
    {
        InvestigationProxyCallToolFilter.IsOrchestratorFanout("collect_events", new Dictionary<string, JsonElement>())
            .Should().BeFalse();
    }

    [Fact]
    public async Task ForwardingFailure_SurfacesStructuredError_AndDoesNotFallThrough()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-err", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var thrown = new InvalidOperationException("upstream MCP exploded");
        fx.ProxyClient.Next = (_, _, _) => throw thrown;

        var result = await fx.Invoke(Params("collect_events", new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement("counters"),
        }), sessionId: "session-err");

        result.IsError.Should().Be(true);
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().Contain("collect_events failed: proxy forwarding to investigation inv-1");
        text.Should().Contain(nameof(InvalidOperationException));
        text.Should().Contain("upstream MCP exploded");
        fx.LocalInvocations.Should().Be(0);
    }

    [Fact]
    public async Task ForwardingFailure_RethrowsMcpProtocolException()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-proto", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        fx.ProxyClient.Next = (_, _, _) => throw new McpProtocolException("bad rpc");

        var act = async () => await fx.Invoke(Params("collect_events", new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement("counters"),
        }), sessionId: "session-proto");

        await act.Should().ThrowAsync<McpProtocolException>().WithMessage("bad rpc");
        fx.LocalInvocations.Should().Be(0);
    }

    [Fact]
    public async Task ForwardingFailure_RethrowsOperationCanceled_WhenCallerCancelled()
    {
        var fx = new Fixture();
        fx.Binder.Bind("session-cancel", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        fx.ProxyClient.Next = (_, _, ct) => Task.FromException<CallToolResult>(new OperationCanceledException(ct));

        var act = async () => await fx.Invoke(Params("collect_events", new Dictionary<string, JsonElement>
        {
            ["kind"] = JsonSerializer.SerializeToElement("counters"),
        }), sessionId: "session-cancel", token: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fx.LocalInvocations.Should().Be(0);
    }

    [Fact]
    public async Task RejectsDisallowedTool_BeforeForwarding()
    {
        // H7: even after binding, an unknown / non-DiagnosticTools tool name is
        // rejected with a structured error result and is never forwarded. The
        // allowlist is the second gate after BypassToolNames.
        var fx = new Fixture();
        fx.Binder.Bind("session-bad-tool", ActiveHandle.HandleId);
        fx.Store.Add(ActiveHandle);

        var result = await fx.Invoke(Params("totally_not_a_real_tool"), sessionId: "session-bad-tool");

        result.IsError.Should().BeTrue();
        fx.ProxyClient.CallCount.Should().Be(0, "the allowlist must reject the call before forwarding");
        fx.LocalInvocations.Should().Be(0, "and must not fall back to local execution either");
    }

    [Fact]
    public void Allowlist_ContainsKnownDiagnosticTools()
    {
        // Sanity-check the reflection-built allowlist actually loaded the expected
        // tool surface. If this drops to zero, the [McpServerTool] discovery broke.
        InvestigationProxyToolAllowlist.AllowedToolNames.Should().Contain("collect_events");
        InvestigationProxyToolAllowlist.AllowedToolNames.Should().Contain("collect_sample");
        InvestigationProxyToolAllowlist.AllowedToolNames.Should().Contain("get_bytes");
        InvestigationProxyToolAllowlist.IsAllowed("collect_events").Should().BeTrue();
        InvestigationProxyToolAllowlist.IsAllowed("get_bytes").Should().BeTrue();
        InvestigationProxyToolAllowlist.IsAllowed("totally_not_a_real_tool").Should().BeFalse();
        InvestigationProxyToolAllowlist.IsAllowed(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("4242", true)]
    [InlineData("\"4242\"", true)]
    [InlineData("\" \"", false)]
    [InlineData("\"\"", false)]
    [InlineData("null", false)]
    public void HasExplicitProcessId_RecognisesValueShapes(string json, bool expected)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["processId"] = JsonDocument.Parse(json).RootElement,
        };
        InvestigationProxyCallToolFilter.HasExplicitProcessId(args).Should().Be(expected);
    }

    [Fact]
    public void HasExplicitProcessId_FalseOnMissingKey()
    {
        InvestigationProxyCallToolFilter.HasExplicitProcessId(null).Should().BeFalse();
        InvestigationProxyCallToolFilter.HasExplicitProcessId(new Dictionary<string, JsonElement>()).Should().BeFalse();
    }

    private static CallToolRequestParams Params(string toolName, IDictionary<string, JsonElement>? args = null)
        => new() { Name = toolName, Arguments = args };

    private static (string Summary, JsonElement Error) ParseForbidden(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        var separator = text.IndexOf('\n');
        separator.Should().BeGreaterThan(0);
        using var document = JsonDocument.Parse(text[(separator + 1)..]);
        return (text[..separator], document.RootElement.GetProperty("error").Clone());
    }

    private sealed class Fixture
    {
        public ToolScopeRegistry ScopeRegistry { get; } =
            ToolScopeRegistry.Build(DotnetDiagnostics.Mcp.Hosting.PodLocalToolSurfaces.Proxyable);
        public InMemorySessionBinder Binder { get; } = new();
        public InMemoryInvestigationStore Store { get; } = new();
        public FakeProxyClient ProxyClient { get; } = new();
        public IPrincipalAccessor PrincipalAccessor { get; }
        public OrchestratorOptions Options { get; } = new();
        public ToolScopeResolutionPolicies? Policies { get; set; }
        public OrchestratorObservability Observability { get; }
        public int LocalInvocations;
        public CallToolRequestParams? LastLocalRequest;

        public Fixture(IPrincipalAccessor? principalAccessor = null)
        {
            PrincipalAccessor = principalAccessor ?? TestPrincipalAccessors.Root;
            var services = new ServiceCollection();
            services.AddMetrics();
            var provider = services.BuildServiceProvider();
            Observability = new OrchestratorObservability(
                provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
                Store,
                new AuditLogWriter(TextWriter.Null));
        }

        public ValueTask<CallToolResult> Invoke(
            CallToolRequestParams? request,
            string? sessionId,
            CancellationToken token = default,
            Func<Func<CancellationToken, ValueTask<CallToolResult>>, CancellationToken, ValueTask<CallToolResult>>? taskPromoter = null)
        {
            return InvestigationProxyCallToolFilter.InvokeAsync(
                request,
                sessionId,
                next: (p, _) =>
                {
                    LastLocalRequest = p;
                    Interlocked.Increment(ref LocalInvocations);
                    return ValueTask.FromResult(new CallToolResult());
                },
                ScopeRegistry,
                Binder,
                Store,
                ProxyClient,
                Options,
                PrincipalAccessor,
                Observability,
                loggerAccessor: () => null,
                cancellationToken: token,
                policies: Policies,
                taskPromoter: taskPromoter);
        }
    }

    private sealed class InMemorySessionBinder : IInvestigationSessionBinder
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

        public string? TryGetHandleId(string? sessionId)
            => sessionId is { Length: > 0 } && _map.TryGetValue(sessionId, out var h) ? h : null;

        public void Bind(string sessionId, string handleId) => _map[sessionId] = handleId;

        public string? Unbind(string? sessionId)
        {
            if (sessionId is null || !_map.TryGetValue(sessionId, out var h)) return null;
            _map.Remove(sessionId);
            return h;
        }

        public IReadOnlyCollection<string> UnbindAllForHandle(string handleId)
        {
            var removed = _map.Where(kv => kv.Value == handleId).Select(kv => kv.Key).ToList();
            foreach (var k in removed) _map.Remove(k);
            return removed;
        }

        public IReadOnlyCollection<KeyValuePair<string, string>> Snapshot() => _map.ToArray();
    }

    private sealed class InMemoryInvestigationStore : IInvestigationStore, IInvestigationStoreActivation
    {
        private readonly Dictionary<string, InvestigationHandle> _byId = new(StringComparer.Ordinal);

        public void Add(InvestigationHandle handle) => _byId[handle.HandleId] = handle;

        public bool TryReserveTarget(InvestigationHandle newHandle, bool allowReuse, out InvestigationHandle? existing)
        {
            existing = null;
            _byId[newHandle.HandleId] = newHandle;
            return true;
        }

        public void Update(InvestigationHandle handle) => _byId[handle.HandleId] = handle;
        public bool TryTransitionToActive(string handleId, out InvestigationHandle? active)
        {
            if (!_byId.TryGetValue(handleId, out var current) ||
                current.State != InvestigationState.Attaching)
            {
                active = null;
                return false;
            }

            active = current with { State = InvestigationState.Active };
            _byId[handleId] = active;
            return true;
        }
        public InvestigationHandle? GetById(string handleId) => _byId.TryGetValue(handleId, out var h) ? h : null;
        public InvestigationTerminalTransition TryTransitionToTerminal(
            string handleId,
            InvestigationState targetState,
            string? failureReason,
            out InvestigationState? previousState)
        {
            previousState = null;
            if (!_byId.TryGetValue(handleId, out var current)) return InvestigationTerminalTransition.NotFound;
            previousState = current.State;
            if (current.State is InvestigationState.Closed or InvestigationState.Expired or InvestigationState.Failed)
                return InvestigationTerminalTransition.AlreadyTerminal;
            _byId[handleId] = current with { State = targetState, FailureReason = targetState == InvestigationState.Closed ? current.FailureReason : failureReason ?? current.FailureReason };
            return InvestigationTerminalTransition.Transitioned;
        }
        public InvestigationHandle? FindReusableTarget(string podNamespace, string podName, string containerName) => null;
        public IReadOnlyCollection<InvestigationHandle> Snapshot() => _byId.Values.ToArray();
    }

    private sealed class FakeProxyClient : IInvestigationProxyClient
    {
        public int CallCount;
        public InvestigationHandle? LastHandle;
        public CallToolRequestParams? LastRequest;
        public Func<InvestigationHandle, CallToolRequestParams, CancellationToken, Task<CallToolResult>> Next
            = (_, _, _) => Task.FromResult(new CallToolResult());

        public Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            LastHandle = handle;
            LastRequest = request;
            return Next(handle, request, cancellationToken);
        }

        public int DisposeCallCount;
        public string? LastDisposedHandleId;

        public Task DisposeForHandleAsync(string handleId)
        {
            Interlocked.Increment(ref DisposeCallCount);
            LastDisposedHandleId = handleId;
            return Task.CompletedTask;
        }
    }
}
