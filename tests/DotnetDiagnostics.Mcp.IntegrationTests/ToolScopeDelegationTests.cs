using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class ToolScopeDelegationTests
{
    private const string Secret = "delegation-unit-test-secret";
    private static readonly ToolScopeResolutionPolicies StrictPolicies = new(null, null, null, null);

    [Fact]
    public void Delegation_Contains_Only_Least_Required_Scopes()
    {
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var arguments = Arguments(new { kind = "counters" });
        var caller = Principal("orchestrator-attach", "read-counters", "eventpipe");
        var authorization = registry.Authorize(
            "collect_events",
            arguments,
            caller,
            proxyInvocation: true,
            policies: StrictPolicies);
        var delegated = ToolScopeDelegation.Add(
            new CallToolRequestParams { Name = "collect_events", Arguments = arguments },
            authorization,
            caller,
            Secret);

        ToolScopeDelegation.TryConsume(
            delegated,
            registry,
            StrictPolicies,
            Secret,
            TimeProvider.System,
            out var delegatedPrincipal,
            out var failure).Should().BeTrue(failure);
        delegatedPrincipal!.Scopes.Should().BeEquivalentTo("read-counters");
        delegated.Arguments.Should().NotContainKey(ToolScopeDelegation.ArgumentName);
    }

    [Fact]
    public void Delegation_Is_Bound_To_Arguments()
    {
        var (registry, delegated) = CreateMethodParameterDelegation();
        delegated.Arguments!["kind"] = JsonSerializer.SerializeToElement("cpu");

        ToolScopeDelegation.TryConsume(
            delegated,
            registry,
            StrictPolicies,
            Secret,
            TimeProvider.System,
            out _,
            out var failure).Should().BeFalse();
        failure.Should().Contain("does not match");
    }

    [Fact]
    public void Delegation_Is_Bound_To_Tool()
    {
        var (registry, delegated) = CreateMethodParameterDelegation();
        delegated.Name = "collect_events";

        ToolScopeDelegation.TryConsume(
            delegated,
            registry,
            StrictPolicies,
            Secret,
            TimeProvider.System,
            out _,
            out var failure).Should().BeFalse();
        failure.Should().Contain("does not match");
    }

    [Fact]
    public void Delegation_Rejects_Tampered_Signature()
    {
        var (registry, delegated) = CreateMethodParameterDelegation();
        var token = delegated.Arguments![ToolScopeDelegation.ArgumentName].GetString()!;
        var signatureStart = token.IndexOf('.', StringComparison.Ordinal) + 1;
        var replacement = token[signatureStart] == 'A' ? 'B' : 'A';
        delegated.Arguments[ToolScopeDelegation.ArgumentName] =
            JsonSerializer.SerializeToElement(
                token[..signatureStart] + replacement + token[(signatureStart + 1)..]);

        ToolScopeDelegation.TryConsume(
            delegated,
            registry,
            StrictPolicies,
            Secret,
            TimeProvider.System,
            out _,
            out var failure).Should().BeFalse();
        failure.Should().Contain("signature");
    }

    [Fact]
    public void Delegation_Rejects_Expired_Token()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var time = new MutableTimeProvider(start);
        var (registry, delegated) = CreateMethodParameterDelegation(time);
        time.UtcNow = start.AddMinutes(1);

        ToolScopeDelegation.TryConsume(
            delegated,
            registry,
            StrictPolicies,
            Secret,
            time,
            out _,
            out var failure).Should().BeFalse();
        failure.Should().Contain("expired");
    }

    [Fact]
    public void Delegation_Is_One_Time_Use()
    {
        var (registry, delegated) = CreateMethodParameterDelegation();
        var replay = new CallToolRequestParams
        {
            Name = delegated.Name,
            Arguments = new Dictionary<string, JsonElement>(delegated.Arguments!, StringComparer.Ordinal),
            Meta = delegated.Meta,
            Task = delegated.Task,
        };

        ToolScopeDelegation.TryConsume(
            delegated,
            registry,
            StrictPolicies,
            Secret,
            TimeProvider.System,
            out _,
            out _).Should().BeTrue();
        ToolScopeDelegation.TryConsume(
            replay,
            registry,
            StrictPolicies,
            Secret,
            TimeProvider.System,
            out _,
            out var failure).Should().BeFalse();
        failure.Should().Contain("already been used");
    }

    [Fact]
    public void Delegation_Remains_One_Time_During_Accepted_Clock_Skew()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var time = new MutableTimeProvider(start);
        var (registry, delegated) = CreateMethodParameterDelegation(time);
        var replay = new CallToolRequestParams
        {
            Name = delegated.Name,
            Arguments = new Dictionary<string, JsonElement>(delegated.Arguments!, StringComparer.Ordinal),
            Meta = delegated.Meta,
            Task = delegated.Task,
        };
        time.UtcNow = start.AddSeconds(31);

        ToolScopeDelegation.TryConsume(
            delegated,
            registry,
            StrictPolicies,
            Secret,
            time,
            out _,
            out var firstFailure).Should().BeTrue(firstFailure);
        time.UtcNow = start.AddSeconds(32);
        ToolScopeDelegation.TryConsume(
            replay,
            registry,
            StrictPolicies,
            Secret,
            time,
            out _,
            out var replayFailure).Should().BeFalse();
        replayFailure.Should().Contain("already been used");
    }

    [Fact]
    public async Task Delegated_Principal_Flows_Into_Task_But_Not_The_Caller_Context()
    {
        var original = Principal("read-counters");
        var delegated = Principal("eventpipe", "sensitive-parameter-read");
        var context = new DefaultHttpContext();
        context.SetBearerPrincipal(original);
        var accessor = new HttpContextPrincipalAccessor(
            new HttpContextAccessor { HttpContext = context });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<BearerPrincipal?> background;

        using (accessor.PushDelegation(delegated))
        {
            accessor.Current.Should().BeSameAs(delegated);
            background = Task.Run(async () =>
            {
                await release.Task;
                return accessor.Current;
            });
        }

        release.SetResult();
        (await background).Should().BeSameAs(delegated);
        accessor.Current.Should().BeSameAs(original);
    }

    [Fact]
    public void Delegation_Is_Bound_To_Task_Metadata()
    {
        var (registry, delegated) = CreateMethodParameterDelegation();
        delegated.Task = new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) };

        ToolScopeDelegation.TryConsume(
            delegated,
            registry,
            StrictPolicies,
            Secret,
            TimeProvider.System,
            out _,
            out var failure).Should().BeFalse();
        failure.Should().Contain("does not match");
    }

    [Fact]
    public void Delegation_Is_Bound_To_Request_Metadata()
    {
        var (registry, delegated) = CreateMethodParameterDelegation();
        delegated.Meta = new JsonObject { ["progressToken"] = "swapped" };

        ToolScopeDelegation.TryConsume(
            delegated,
            registry,
            StrictPolicies,
            Secret,
            TimeProvider.System,
            out _,
            out var failure).Should().BeFalse();
        failure.Should().Contain("does not match");
    }

    [Fact]
    public void Delegation_Preserves_Inherited_Request_Metadata()
    {
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var arguments = Arguments(new { kind = "counters" });
        var caller = Principal("read-counters");
        var meta = new JsonObject
        {
            ["progressToken"] = "progress-707",
            ["extension"] = new JsonObject { ["trace"] = "abc" },
        };
        var request = new CallToolRequestParams
        {
            Name = "collect_events",
            Arguments = arguments,
            Meta = meta,
        };
        var authorization = registry.Authorize(
            request.Name,
            arguments,
            caller,
            proxyInvocation: true,
            policies: StrictPolicies);

        var delegated = ToolScopeDelegation.Add(
            request,
            authorization,
            caller,
            Secret);

        delegated.Meta.Should().BeSameAs(meta);
        delegated.Meta!["progressToken"]!.GetValue<string>().Should().Be("progress-707");
        delegated.Meta["extension"]!["trace"]!.GetValue<string>().Should().Be("abc");
    }

    [Fact]
    public void Delegation_Is_Bound_To_Handle_Key()
    {
        var (registry, delegated) = CreateMethodParameterDelegation();

        ToolScopeDelegation.TryConsume(
            delegated,
            registry,
            StrictPolicies,
            "another-handle-key",
            TimeProvider.System,
            out _,
            out var failure).Should().BeFalse();
        failure.Should().Contain("signature");
    }

    [Fact]
    public void QuerySnapshot_Delegates_Only_Caller_Presented_Primary_Scopes()
    {
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var arguments = Arguments(new { handle = "opaque", view = "summary" });
        var caller = Principal("eventpipe", "heap-read");
        var authorization = registry.Authorize(
            "query_snapshot",
            arguments,
            caller,
            proxyInvocation: true,
            policies: StrictPolicies);
        authorization.IsAllowed.Should().BeTrue();

        ToolScopeDelegation.GetDelegatedScopes("query_snapshot", authorization, caller)
            .Should().BeEquivalentTo("eventpipe", "heap-read");
    }

    [Fact]
    public void QuerySnapshot_Delegates_CallerPresented_MethodParameter_Modifier()
    {
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var arguments = Arguments(new { handle = "opaque", view = "summary" });
        var caller = Principal("eventpipe", "sensitive-parameter-read");
        var authorization = registry.Authorize(
            "query_snapshot",
            arguments,
            caller,
            proxyInvocation: true,
            policies: StrictPolicies);

        authorization.IsAllowed.Should().BeTrue();
        ToolScopeDelegation.GetDelegatedScopes("query_snapshot", authorization, caller)
            .Should().BeEquivalentTo("eventpipe", "sensitive-parameter-read");
    }

    [Fact]
    public void QuerySnapshot_EventPipeCaller_DelegatesOnlyEventPipeAlternative()
    {
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var arguments = Arguments(new { handle = "opaque", view = "summary" });
        var caller = Principal("eventpipe");
        var authorization = registry.Authorize(
            "query_snapshot",
            arguments,
            caller,
            proxyInvocation: true,
            policies: StrictPolicies);

        authorization.IsAllowed.Should().BeTrue();
        ToolScopeDelegation.GetDelegatedScopes("query_snapshot", authorization, caller)
            .Should().BeEquivalentTo("eventpipe");
    }

    [Fact]
    public void QuerySnapshot_RetentionCaller_DelegatesOnlyHeapAndSensitiveScopes()
    {
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var arguments = Arguments(new { handle = "opaque", view = "RETENTION-PATHS" });
        var caller = Principal("heap-read", "sensitive-heap-read");
        var authorization = registry.Authorize(
            "query_snapshot",
            arguments,
            caller,
            proxyInvocation: true,
            policies: StrictPolicies);

        authorization.IsAllowed.Should().BeTrue();
        ToolScopeDelegation.GetDelegatedScopes("query_snapshot", authorization, caller)
            .Should().BeEquivalentTo("heap-read", "sensitive-heap-read");
    }

    [Fact]
    public void ExportSummary_Delegates_CanonicalCpuScope()
    {
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var arguments = Arguments(new { handle = "opaque" });
        var caller = Principal(
            "investigation-export",
            "eventpipe",
            "ptrace",
            "symbols-remote");
        var authorization = registry.Authorize(
            "export_investigation_summary",
            arguments,
            caller,
            proxyInvocation: true,
            policies: StrictPolicies);
        authorization.IsAllowed.Should().BeTrue();

        ToolScopeDelegation.GetDelegatedScopes(
                "export_investigation_summary",
                authorization,
                caller)
            .Should().BeEquivalentTo("investigation-export", "eventpipe");
    }

    [Fact]
    public void ExportSummary_RejectsMissingEventPipeScope()
    {
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var arguments = Arguments(new { handle = "opaque" });
        var caller = Principal("investigation-export");
        var authorization = registry.Authorize(
            "export_investigation_summary",
            arguments,
            caller,
            proxyInvocation: true,
            policies: StrictPolicies);
        authorization.IsAllowed.Should().BeFalse();
        authorization.MissingScope.Should().Be("eventpipe");
    }

    [Theory]
    [InlineData(BearerPrincipal.RootScope)]
    [InlineData(BearerPrincipal.RootScopeAlt)]
    public void ExportSummary_WildcardCaller_DoesNotSynthesizeEventPipeScope(string wildcard)
    {
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var arguments = Arguments(new { handle = "opaque" });
        var caller = Principal(wildcard);
        var authorization = registry.Authorize(
            "export_investigation_summary",
            arguments,
            caller,
            proxyInvocation: true,
            policies: StrictPolicies);
        authorization.IsAllowed.Should().BeFalse();
        authorization.MissingScope.Should().Be("eventpipe");
    }

    private static (ToolScopeRegistry Registry, CallToolRequestParams Delegated)
        CreateMethodParameterDelegation(TimeProvider? timeProvider = null)
    {
        var registry = ToolScopeRegistry.Build(PodLocalToolSurfaces.Proxyable);
        var arguments = Arguments(new { kind = "method-params" });
        var caller = Principal("eventpipe", "sensitive-parameter-read");
        var authorization = registry.Authorize(
            "collect_sample",
            arguments,
            caller,
            proxyInvocation: true,
            policies: StrictPolicies);
        return (
            registry,
            ToolScopeDelegation.Add(
                new CallToolRequestParams { Name = "collect_sample", Arguments = arguments },
                authorization,
                caller,
                Secret,
                timeProvider));
    }

    private static BearerPrincipal Principal(params string[] scopes)
        => new("caller", scopes.ToImmutableHashSet(StringComparer.Ordinal));

    private static IDictionary<string, JsonElement> Arguments<T>(T value)
        => JsonSerializer.SerializeToElement(value).EnumerateObject()
            .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
