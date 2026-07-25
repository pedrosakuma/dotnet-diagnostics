using System.Collections.Immutable;
using System.Text.Json;
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
            "collect_events",
            delegated.Arguments,
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
            "collect_sample",
            delegated.Arguments,
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

        ToolScopeDelegation.TryConsume(
            "collect_events",
            delegated.Arguments,
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
            "collect_sample",
            delegated.Arguments,
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
            "collect_sample",
            delegated.Arguments,
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
        var replay = new Dictionary<string, JsonElement>(delegated.Arguments!, StringComparer.Ordinal);

        ToolScopeDelegation.TryConsume(
            "collect_sample",
            delegated.Arguments,
            registry,
            StrictPolicies,
            Secret,
            TimeProvider.System,
            out _,
            out _).Should().BeTrue();
        ToolScopeDelegation.TryConsume(
            "collect_sample",
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
    public async Task Delegated_Principal_Does_Not_Outlive_Filter_Execution()
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
        (await background).Should().BeSameAs(original);
        accessor.Current.Should().BeSameAs(original);
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
