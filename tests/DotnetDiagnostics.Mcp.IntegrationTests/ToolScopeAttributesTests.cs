using System.Collections.Immutable;
using System.Text.Json;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Security;
using FluentAssertions;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// B5.2 — coverage for the attribute surface and the reflective registry build that
/// pre-computes the per-tool scope map. These tests live in-process (no
/// WebApplicationFactory) and run sub-second.
/// </summary>
public sealed class ToolScopeAttributesTests
{
    [Fact]
    public void RequireScope_Accepts_NonEmpty_Scopes()
    {
        var attr = new RequireScopeAttribute("read-counters");
        attr.Scopes.Should().ContainSingle().Which.Should().Be("read-counters");

        var stacked = new RequireScopeAttribute("ptrace", "dump-write");
        stacked.Scopes.Should().Equal("ptrace", "dump-write");
    }

    [Fact]
    public void RequireScope_Rejects_Empty_Arg_List()
    {
        var act = () => new RequireScopeAttribute();
        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("scopes");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void RequireScope_Rejects_Empty_Or_Whitespace_Entries(string? bad)
    {
        var act = () => new RequireScopeAttribute("ok", bad!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RequireAnyScope_Round_Trips()
    {
        var attr = new RequireAnyScopeAttribute("read-counters", "eventpipe");
        attr.Scopes.Should().Equal("read-counters", "eventpipe");
    }

    [Fact]
    public void ToolScopeRegistry_Build_Indexes_Decorated_Tools()
    {
        var registry = ToolScopeRegistry.Build(new[] { typeof(SampleSurface) });

        var req = registry.TryGet("sample_tool");
        req.Should().NotBeNull();
        req!.Value.All.Should().Equal("read-counters");

        var anyReq = registry.TryGet("sample_any_tool");
        anyReq.Should().NotBeNull();
        anyReq!.Value.IsAny.Should().BeTrue();
        anyReq.Value.Any.Should().Equal("read-counters", "eventpipe");

        var stacked = registry.TryGet("sample_stacked");
        stacked!.Value.All.Should().Equal("ptrace", "dump-write");
    }

    [Fact]
    public void ToolScopeRegistry_Throws_When_Tool_Has_No_Scope()
    {
        var act = () => ToolScopeRegistry.Build(new[] { typeof(MissingScopeSurface) });
        act.Should().Throw<InvalidOperationException>().WithMessage("*Missing*sample_unscoped*");
    }

    [Fact]
    public void ToolScopeRegistry_Throws_When_Tool_Has_Both_Attributes()
    {
        var act = () => ToolScopeRegistry.Build(new[] { typeof(ConflictingSurface) });
        act.Should().Throw<InvalidOperationException>().WithMessage("*both [RequireScope] and [RequireAnyScope]*");
    }

    [Fact]
    public void ToolScopeRegistry_Production_Surface_Has_Full_Coverage()
    {
        // Every [McpServerTool] in the shipping tool surface must declare a scope, including
        // the orchestrator surface (the conditional registration in DiagnosticServiceRegistration
        // only flips whether OrchestratorTools is *registered*, not whether its members declare
        // a scope). A missing scope fails Build() — the assertion is "this does not throw".
        var registry = ToolScopeRegistry.Build(new[]
        {
            typeof(DotnetDiagnostics.Mcp.Tools.DiagnosticTools),
            typeof(DotnetDiagnostics.Mcp.Tools.OrchestratorTools),
            typeof(DotnetDiagnostics.Mcp.Tools.ListOrchestratorTool),
            typeof(DotnetDiagnostics.Mcp.Tools.InspectProcessTool),
            typeof(DotnetDiagnostics.Mcp.Tools.CollectEventsTool),
            typeof(DotnetDiagnostics.Mcp.Tools.CollectSampleTool),
            typeof(DotnetDiagnostics.Mcp.Tools.CollectBatchTool),
            typeof(DotnetDiagnostics.Mcp.Tools.QuerySnapshotTool),
            typeof(DotnetDiagnostics.Mcp.Tools.InspectHeapTool),
            typeof(DotnetDiagnostics.Mcp.Tools.GetBytesTool),
            typeof(DotnetDiagnostics.Mcp.Tools.DiscoverAzureTool),
        });

        // Spot-check a representative tool from each scope family to detect accidental
        // regressions in the mapping table.
        registry.TryGet("collect_events")!.Value.Any.Should().Equal("read-counters", "eventpipe");
        registry.TryGet("collect_sample")!.Value.All.Should().Equal("eventpipe");
        registry.TryGet("inspect_process")!.Value.Any.Should().Equal("read-counters", "ptrace");
        registry.TryGet("inspect_heap")!.Value.All.Should().Equal("heap-read");
        registry.TryGet("collect_process_dump")!.Value.All.Should().Equal("dump-write", "ptrace");
        registry.TryGet("query_snapshot")!.Value.Any.Should().Equal("read-counters", "eventpipe", "heap-read", "ptrace", "investigation-export");
        registry.TryGet("export_investigation_summary")!.Value.All.Should().Equal("investigation-export");
        registry.TryGet("attach_to_pod")!.Value.All.Should().Equal("orchestrator-attach");
        registry.TryGet("list_orchestrator")!.Value.Any.Should().Equal("orchestrator-list", "orchestrator-attach");
        registry.TryGet("discover_azure")!.Value.All.Should().Equal("azure-discovery");
    }

    [Fact]
    public void ToolScopeRegistry_Authorize_Requires_Literal_MethodParameter_Modifier()
    {
        var registry = ToolScopeRegistry.Build(
            DotnetDiagnostics.Mcp.Hosting.PodLocalToolSurfaces.Proxyable);
        var arguments = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["kind"] = System.Text.Json.JsonSerializer.SerializeToElement(" method-params "),
        };

        var wildcardOnly = new BearerPrincipal(
            "root",
            ImmutableHashSet.Create(BearerPrincipal.RootScope));
        var denied = registry.Authorize("collect_sample", arguments, wildcardOnly);

        denied.IsAllowed.Should().BeFalse();
        denied.MissingScope.Should().Be("sensitive-parameter-read");
        denied.MissingExplicitScope.Should().BeTrue();

        var allowed = registry.Authorize(
            "collect_sample",
            arguments,
            new BearerPrincipal(
                "capture",
                ImmutableHashSet.Create("eventpipe", "sensitive-parameter-read")));
        allowed.IsAllowed.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(ArgumentAwareScopeCases))]
    public void ToolScopeRegistry_Authorize_Uses_One_ArgumentAware_Resolver(
        string toolName,
        IDictionary<string, JsonElement> arguments,
        string[] heldScopes,
        string missingScope,
        bool explicitModifier)
    {
        var registry = ToolScopeRegistry.Build(
            DotnetDiagnostics.Mcp.Hosting.PodLocalToolSurfaces.Proxyable);
        var denied = registry.Authorize(
            toolName,
            arguments,
            new BearerPrincipal("limited", ImmutableHashSet.Create(heldScopes)));

        denied.IsAllowed.Should().BeFalse();
        denied.MissingScope.Should().Be(missingScope);
        denied.MissingExplicitScope.Should().Be(explicitModifier);

        var allowed = registry.Authorize(
            toolName,
            arguments,
            new BearerPrincipal("allowed", ImmutableHashSet.Create(heldScopes.Append(missingScope).ToArray())));
        allowed.IsAllowed.Should().BeTrue();
    }

    public static TheoryData<string, IDictionary<string, JsonElement>, string[], string, bool>
        ArgumentAwareScopeCases => new()
        {
            {
                "inspect_process",
                Arguments(new { view = "REQUESTS-NOW" }),
                new[] { "read-counters" },
                "ptrace",
                false
            },
            {
                "collect_events",
                Arguments(new { kind = "EXCEPTIONS" }),
                new[] { "read-counters" },
                "eventpipe",
                false
            },
            {
                "collect_events",
                Arguments(new
                {
                    kind = "COUNTERS",
                    triggerWhen = "always-trigger",
                    captureKind = "Dump",
                    confirmDump = true,
                }),
                new[] { "read-counters", "ptrace" },
                "dump-write",
                false
            },
            {
                "collect_events",
                Arguments(new { kind = "EVENT_SOURCE", unsafeProvider = true }),
                new[] { "eventpipe" },
                "eventsource-any",
                true
            },
            {
                "collect_sample",
                Arguments(new { kind = "CPU", resolveMethodInstantiations = true }),
                new[] { "eventpipe" },
                "ptrace",
                false
            },
            {
                "collect_sample",
                Arguments(new
                {
                    kind = "CPU",
                    symbolPath = "srv*/symbols*https://symbols.example.test",
                }),
                new[] { "eventpipe" },
                "symbols-remote",
                true
            },
            {
                "collect_sample",
                Arguments(new { kind = "METHOD-PARAMS" }),
                new[] { "eventpipe" },
                "sensitive-parameter-read",
                true
            },
            {
                "collect_batch",
                Arguments(new
                {
                    requests = new[]
                    {
                        new { tool = "COLLECT_EVENTS", kind = "COUNTERS" },
                        new { tool = "COLLECT_EVENTS", kind = "EXCEPTIONS" },
                    },
                }),
                new[] { "read-counters" },
                "eventpipe",
                false
            },
            {
                "inspect_heap",
                Arguments(new { source = "LIVE" }),
                new[] { "heap-read" },
                "ptrace",
                false
            },
            {
                "inspect_heap",
                Arguments(new { source = "DUMP", includeRetentionPaths = true }),
                new[] { "heap-read" },
                "sensitive-heap-read",
                true
            },
            {
                "query_snapshot",
                Arguments(new { handle = "heap-1", view = "RETENTION-PATHS" }),
                new[] { "heap-read" },
                "sensitive-heap-read",
                true
            },
            {
                "query_snapshot",
                Arguments(new
                {
                    handle = "params-1",
                    view = "events",
                    includeSensitiveValues = true,
                }),
                new[] { "eventpipe" },
                "sensitive-parameter-read",
                true
            },
            {
                "query_snapshot",
                Arguments(new { handle = "threads-1", view = "FRAME-VARS" }),
                new[] { "ptrace" },
                "heap-read",
                false
            },
            {
                "get_bytes",
                Arguments(new { kind = "DUMP", dumpFilePath = "capture.dmp" }),
                new[] { BearerPrincipal.RootScope },
                "module-bytes-read",
                true
            },
            {
                "get_bytes",
                Arguments(new { kind = "DELETE", artifactPath = "capture.dmp" }),
                new[] { "module-bytes-read" },
                "delete-artifact",
                true
            },
            {
                "collect_thread_snapshot",
                Arguments(new { symbolPath = "srv*/symbols*https://symbols.example.test" }),
                new[] { "ptrace" },
                "symbols-remote",
                true
            },
        };

    [Fact]
    public void Allowlisted_Symbol_Host_Is_A_Policy_Alternative_On_Local_And_Proxy_Paths()
    {
        var registry = ToolScopeRegistry.Build(
            DotnetDiagnostics.Mcp.Hosting.PodLocalToolSurfaces.Proxyable);
        var options = new SecurityOptions
        {
            SymbolServerAllowlist = ["symbols.example.test"],
        };
        var policies = Policies(options);
        var arguments = Arguments(new
        {
            kind = "cpu",
            symbolPath = "srv*/symbols*https://symbols.example.test",
        });
        var principal = new BearerPrincipal("caller", ImmutableHashSet.Create("eventpipe"));

        registry.Authorize("collect_sample", arguments, principal, policies: policies)
            .IsAllowed.Should().BeTrue();
        registry.Authorize("collect_sample", arguments, principal, proxyInvocation: true, policies: policies)
            .IsAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("System.Runtime", false)]
    [InlineData("Configured.Custom.Provider", false)]
    [InlineData("Unlisted.Custom.Provider", true)]
    public void EventSource_Policy_Alternatives_Are_Identical_On_Local_And_Proxy_Paths(
        string providerName,
        bool serverGate)
    {
        var registry = ToolScopeRegistry.Build(
            DotnetDiagnostics.Mcp.Hosting.PodLocalToolSurfaces.Proxyable);
        var options = new SecurityOptions
        {
            AllowSensitiveHeapValues = serverGate,
            EventSourceAllowlist = ["Configured.Custom.Provider"],
        };
        var policies = Policies(options);
        var arguments = Arguments(new
        {
            kind = "event_source",
            providerName,
            unsafeProvider = true,
        });
        var principal = new BearerPrincipal("caller", ImmutableHashSet.Create("eventpipe"));

        registry.Authorize("collect_events", arguments, principal, policies: policies)
            .IsAllowed.Should().BeTrue();
        registry.Authorize("collect_events", arguments, principal, proxyInvocation: true, policies: policies)
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void AllowCrossSessionAdmin_Is_A_Policy_Alternative()
    {
        var registry = ToolScopeRegistry.Build(
            [typeof(DotnetDiagnostics.Mcp.Tools.ListOrchestratorTool)]);
        var policies = Policies(
            new SecurityOptions(),
            new OrchestratorOptions { AllowCrossSessionAdmin = true });
        var arguments = Arguments(new { kind = "investigations", includeAllSessions = true });
        var principal = new BearerPrincipal(
            "caller",
            ImmutableHashSet.Create("orchestrator-attach"));

        registry.Authorize("list_orchestrator", arguments, principal, policies: policies)
            .IsAllowed.Should().BeTrue();
    }

    private static IDictionary<string, JsonElement> Arguments<T>(T value)
        => JsonSerializer.SerializeToElement(value).EnumerateObject()
            .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);

    private static ToolScopeResolutionPolicies Policies(
        SecurityOptions options,
        OrchestratorOptions? orchestratorOptions = null)
        => new(
            new SymbolServerAllowlist(options),
            new EventSourceAllowlist(options),
            new SensitiveValueGate(options),
            orchestratorOptions ?? new OrchestratorOptions());


    // --- fixtures -----------------------------------------------------------------

    private static class SampleSurface
    {
        [RequireScope("read-counters")]
        [McpServerTool(Name = "sample_tool")]
        public static int A() => 0;

        [RequireAnyScope("read-counters", "eventpipe")]
        [McpServerTool(Name = "sample_any_tool")]
        public static int B() => 0;

        [RequireScope("ptrace", "dump-write")]
        [McpServerTool(Name = "sample_stacked")]
        public static int C() => 0;
    }

    private static class MissingScopeSurface
    {
        [McpServerTool(Name = "sample_unscoped")]
        public static int A() => 0;
    }

    private static class ConflictingSurface
    {
        [RequireScope("read-counters")]
        [RequireAnyScope("eventpipe")]
        [McpServerTool(Name = "sample_conflicting")]
        public static int A() => 0;
    }
}

/// <summary>
/// Pure-function tests for <see cref="ToolScopeAuthorizationFilter.Authorize"/>: covers
/// wildcard, AND, OR, missing-principal, and partial-match cases without spinning up an
/// MCP server.
/// </summary>
public sealed class ToolScopeAuthorizationTests
{
    private static BearerPrincipal With(params string[] scopes) => new(
        name: "test",
        scopes: ImmutableHashSet.Create(scopes));

    private static ToolScopeRegistry.Requirement All(params string[] scopes) =>
        new(All: ImmutableArray.Create(scopes), Any: ImmutableArray<string>.Empty);

    private static ToolScopeRegistry.Requirement Any(params string[] scopes) =>
        new(All: ImmutableArray<string>.Empty, Any: ImmutableArray.Create(scopes));

    [Fact]
    public void Authorize_Denies_When_No_Principal()
    {
        var decision = ToolScopeAuthorizationFilter.Authorize(All("read-counters"), principal: null);
        decision.IsAllowed.Should().BeFalse();
        decision.MissingScope.Should().Be("read-counters");
    }

    [Fact]
    public void Authorize_Allows_Single_Match()
    {
        var decision = ToolScopeAuthorizationFilter.Authorize(
            All("read-counters"), With("read-counters", "eventpipe"));
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Authorize_Stacked_Scope_Requires_All()
    {
        var req = All("ptrace", "dump-write");
        ToolScopeAuthorizationFilter.Authorize(req, With("ptrace", "dump-write"))
            .IsAllowed.Should().BeTrue();

        var partial = ToolScopeAuthorizationFilter.Authorize(req, With("ptrace"));
        partial.IsAllowed.Should().BeFalse();
        partial.MissingScope.Should().Be("dump-write");
    }

    [Fact]
    public void Authorize_AnyOf_Matches_First_Held_Scope()
    {
        var req = Any("read-counters", "eventpipe");
        ToolScopeAuthorizationFilter.Authorize(req, With("eventpipe"))
            .IsAllowed.Should().BeTrue();

        var none = ToolScopeAuthorizationFilter.Authorize(req, With("orchestrator-list"));
        none.IsAllowed.Should().BeFalse();
        none.MissingScope.Should().Be("read-counters");
    }

    [Fact]
    public void Authorize_Root_Wildcard_Satisfies_Every_Requirement()
    {
        var root = With(BearerPrincipal.RootScope);
        ToolScopeAuthorizationFilter.Authorize(All("ptrace", "dump-write"), root)
            .IsAllowed.Should().BeTrue();
        ToolScopeAuthorizationFilter.Authorize(Any("read-counters", "eventpipe"), root)
            .IsAllowed.Should().BeTrue();

        var star = With(BearerPrincipal.RootScopeAlt);
        ToolScopeAuthorizationFilter.Authorize(All("heap-read"), star)
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void HasExplicitScope_Does_Not_Honour_Wildcard()
    {
        // Modifier-scope guards (docs/authorization.md#modifier-scopes) must NOT fire just because the principal
        // is root — sensitive-heap-read / symbols-remote / eventsource-any / orchestrator-admin
        // are explicit additive opt-ins by design.
        var root = With(BearerPrincipal.RootScope);
        root.HasScope("sensitive-heap-read").Should().BeTrue();        // wildcard honoured
        root.HasExplicitScope("sensitive-heap-read").Should().BeFalse(); // literal membership only

        var explicitGrant = With(BearerPrincipal.RootScope, "sensitive-heap-read");
        explicitGrant.HasExplicitScope("sensitive-heap-read").Should().BeTrue();
    }
}
