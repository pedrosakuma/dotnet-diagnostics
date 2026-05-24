using DotnetDiagnosticsMcp.Server.Tools.Deprecation;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// RFC 0002 / #204 — covers the shared deprecation scaffolding:
/// (a) <c>tools/list</c> description carries the deprecation notice for a tool stamped
///     with <see cref="DeprecatedToolAttribute"/>;
/// (b) the once-per-process audit-log warning fires exactly once across N CallTool
///     dispatches;
/// (c) <see cref="ToolDeprecationRegistry.ResetForTesting"/> rearms the once-flag so the
///     warning can fire again — the contract test helpers depend on this.
/// </summary>
public sealed class ToolDeprecationRegistryTests
{
    private const string LegacyTool = "fake_legacy_tool";
    private const string SuccessorTool = "fake_successor_tool";

    [Fact]
    public void Build_ScansAttribute_BuildsExpectedEntry()
    {
        var registry = ToolDeprecationRegistry.Build(new[] { typeof(FakeToolSurface) });

        var entry = registry.TryGet(LegacyTool);
        entry.Should().NotBeNull();
        entry!.SuccessorTool.Should().Be(SuccessorTool);
        entry.RemovalVersion.Should().Be("0.9.0");
        entry.Notice.Should().Contain("DEPRECATED").And.Contain(LegacyTool).And.Contain(SuccessorTool);
    }

    [Fact]
    public void ApplyDeprecationNotices_AppendsNoticeToDescription()
    {
        var registry = ToolDeprecationRegistry.Build(new[] { typeof(FakeToolSurface) });
        var seed = new ListToolsResult
        {
            Tools = new List<Tool>
            {
                new() { Name = LegacyTool, Description = "Original description." },
                new() { Name = "untouched", Description = "Stays as is." },
            },
        };

        ToolDeprecationFilters.ApplyDeprecationNotices(registry, seed);

        seed.Tools.Single(t => t.Name == LegacyTool).Description.Should()
            .Contain("Original description.")
            .And.Contain("DEPRECATED")
            .And.Contain(SuccessorTool);
        seed.Tools.Single(t => t.Name == "untouched").Description.Should().Be("Stays as is.");
    }

    [Fact]
    public void NotifyInvocation_EmitsWarningExactlyOnceAcrossManyInvocations()
    {
        var provider = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        var logger = factory.CreateLogger<ToolDeprecationRegistry>();

        var registry = ToolDeprecationRegistry.Build(new[] { typeof(FakeToolSurface) }, logger);

        for (int i = 0; i < 5; i++)
        {
            registry.NotifyInvocation(LegacyTool);
        }

        provider.Records
            .Count(r => r.Level == LogLevel.Warning && r.Message.Contains(LegacyTool, StringComparison.Ordinal))
            .Should().Be(1);
    }

    [Fact]
    public void ResetForTesting_RearmsWarning()
    {
        var provider = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        var logger = factory.CreateLogger<ToolDeprecationRegistry>();

        var registry = ToolDeprecationRegistry.Build(new[] { typeof(FakeToolSurface) }, logger);
        registry.NotifyInvocation(LegacyTool);
        registry.NotifyInvocation(LegacyTool);

        registry.ResetForTesting();

        registry.NotifyInvocation(LegacyTool);

        provider.Records
            .Count(r => r.Level == LogLevel.Warning && r.Message.Contains(LegacyTool, StringComparison.Ordinal))
            .Should().Be(2, "the once-flag should rearm so test fixtures can reuse the registry");
    }

    [Fact]
    public void NotifyInvocation_UnknownTool_IsSilent()
    {
        var provider = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        var logger = factory.CreateLogger<ToolDeprecationRegistry>();
        var registry = ToolDeprecationRegistry.Build(new[] { typeof(FakeToolSurface) }, logger);

        var entry = registry.NotifyInvocation("not_in_registry");

        entry.Should().BeNull();
        provider.Records.Should().NotContain(r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public void Filters_Construct_WhenRegistryHasEntries()
    {
        // Smoke test for the MCP-facing wrappers — exercises Create* so a regression in the
        // delegate signature surfaces in CI even though we cannot easily build a
        // RequestContext<T> from a unit test.
        var registry = ToolDeprecationRegistry.Build(new[] { typeof(FakeToolSurface) });
        ToolDeprecationFilters.CreateListToolsFilter(registry).Should().NotBeNull();
        ToolDeprecationFilters.CreateCallToolFilter(registry).Should().NotBeNull();
    }

    // Synthetic tool surface — kept private to this file so the production surface stays at
    // 34 tools and nothing in this PR ships a real new [McpServerTool].
    private static class FakeToolSurface
    {
        [McpServerTool(Name = LegacyTool)]
        [DeprecatedTool(SuccessorTool, "0.9.0")]
        public static string LegacyMethod() => "ignored";
    }
}
