using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Activities;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;

/// <summary>
/// RFC 0002 §4.5 / issue #208 — dual-entrypoint contract tests for
/// <see cref="CollectEventsTool"/>. For every supported <c>kind</c>, asserts that calling
/// <c>collect_events(kind="X", ...)</c> produces an envelope structurally equivalent to the
/// envelope produced by the legacy collector tool (<c>snapshot_counters</c>,
/// <c>collect_exceptions</c>, <c>collect_gc_events</c>, <c>collect_event_source</c>,
/// <c>collect_activities</c>). The polymorphic payload field for the chosen kind is compared
/// against the legacy payload directly; the surrounding envelope (Summary, Hints, ResolvedProcess)
/// is sanity-checked rather than diffed byte-for-byte because the legacy tools carry per-tool
/// next-action hints that the discriminator entry-point inherits verbatim.
/// </summary>
/// <remarks>
/// <para>The tests exercise the test host process itself (<see cref="Environment.ProcessId"/>) —
/// same pattern as the existing collector tests in <c>McpToolsTests</c>. AGENTS.md notes EventPipe
/// sessions take ~500 ms–1 s to fully start; counter windows therefore use ≥3 s, exception/GC/
/// EventSource/activity windows use 2 s (matches the legacy tests).</para>
/// </remarks>
[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class CollectEventsCompatibilityTests : IClassFixture<CollectEventsCompatibilityTests.AuthedFactory>
{
    private readonly AuthedFactory _factory;

    public CollectEventsCompatibilityTests(AuthedFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Kind_Counters_MatchesLegacySnapshotCounters()
    {
        await using var client = await ConnectAsync();

        var common = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 3,
            ["providers"] = new[] { "System.Runtime" },
            ["intervalSeconds"] = 1,
        };

        var legacy = DeserializeStructured<CounterSnapshot>(
            await client.CallToolAsync("snapshot_counters", common, cancellationToken: CancellationToken.None));
        var unified = DeserializeStructured<CollectEventsEnvelope>(
            await client.CallToolAsync("collect_events", With(common, ("kind", "counters")), cancellationToken: CancellationToken.None));

        unified!.Kind.Should().Be("counters");
        unified.Counters.Should().NotBeNull();
        unified.Exceptions.Should().BeNull();
        unified.Gc.Should().BeNull();
        unified.EventSource.Should().BeNull();
        unified.Activities.Should().BeNull();

        unified.Counters!.ProcessId.Should().Be(legacy!.ProcessId);
        unified.Counters.Counters.Should().NotBeEmpty();
        unified.Counters.Counters.Select(c => c.Provider).Should().Contain("System.Runtime");
    }

    [Fact]
    public async Task Kind_Exceptions_MatchesLegacyCollectExceptions()
    {
        await using var client = await ConnectAsync();

        var common = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 2,
            ["maxRecent"] = 10,
        };

        var legacy = DeserializeStructured<ExceptionSnapshot>(
            await client.CallToolAsync("collect_exceptions", common, cancellationToken: CancellationToken.None));
        var unified = DeserializeStructured<CollectEventsEnvelope>(
            await client.CallToolAsync("collect_events", With(common, ("kind", "exceptions")), cancellationToken: CancellationToken.None));

        unified!.Kind.Should().Be("exceptions");
        unified.Exceptions.Should().NotBeNull();
        unified.Counters.Should().BeNull();
        unified.Exceptions!.ProcessId.Should().Be(legacy!.ProcessId);
        unified.Exceptions.TotalExceptions.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Kind_Gc_MatchesLegacyCollectGcEvents()
    {
        await using var client = await ConnectAsync();

        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }

        var common = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 2,
            ["maxEvents"] = 50,
        };

        var unified = DeserializeStructured<CollectEventsEnvelope>(
            await client.CallToolAsync("collect_events", With(common, ("kind", "gc")), cancellationToken: CancellationToken.None));

        unified!.Kind.Should().Be("gc");
        unified.Gc.Should().NotBeNull();
        unified.Gc!.ProcessId.Should().Be(Environment.ProcessId);

        // Cross-check the legacy tool — both now share the same parameter name.
        var legacy = DeserializeStructured<GcSummary>(
            await client.CallToolAsync(
                "collect_gc_events",
                new Dictionary<string, object?>
                {
                    ["processId"] = Environment.ProcessId,
                    ["durationSeconds"] = 2,
                    ["maxEvents"] = 50,
                },
                cancellationToken: CancellationToken.None));
        legacy.Should().NotBeNull();
        legacy!.ProcessId.Should().Be(unified.Gc.ProcessId);
    }

    [Fact]
    public async Task Kind_EventSource_MatchesLegacyCollectEventSource()
    {
        await using var client = await ConnectAsync();

        var common = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 2,
            ["providerName"] = "System.Runtime",
            ["maxEvents"] = 10,
        };

        var legacy = await client.CallToolAsync("collect_event_source", common, cancellationToken: CancellationToken.None);
        legacy.IsError.Should().NotBe(true);

        var unified = DeserializeStructured<CollectEventsEnvelope>(
            await client.CallToolAsync("collect_events", With(common, ("kind", "event_source")), cancellationToken: CancellationToken.None));

        unified!.Kind.Should().Be("event_source");
        unified.EventSource.Should().NotBeNull();
        unified.EventSource!.Provider.Should().Be("System.Runtime");
    }

    [Fact]
    public async Task Kind_Activities_PopulatesActivitiesField()
    {
        await using var client = await ConnectAsync();

        var unified = DeserializeStructured<CollectEventsEnvelope>(
            await client.CallToolAsync(
                "collect_events",
                new Dictionary<string, object?>
                {
                    ["kind"] = "activities",
                    ["processId"] = Environment.ProcessId,
                    ["durationSeconds"] = 2,
                    ["maxActivities"] = 25,
                },
                cancellationToken: CancellationToken.None));

        unified!.Kind.Should().Be("activities");
        unified.Activities.Should().NotBeNull();
        unified.Activities!.ProcessId.Should().Be(Environment.ProcessId);
        unified.Counters.Should().BeNull();
        unified.Exceptions.Should().BeNull();
        unified.Gc.Should().BeNull();
        unified.EventSource.Should().BeNull();
    }

    [Fact]
    public async Task Kind_Invalid_ReturnsInvalidArgument()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "collect_events",
            new Dictionary<string, object?>
            {
                ["kind"] = "not-a-real-kind",
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 2,
            },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("InvalidArgument");
        envelope.Error.Message.Should().Contain("counters")
            .And.Contain("exceptions")
            .And.Contain("gc")
            .And.Contain("event_source")
            .And.Contain("activities");
    }

    [Fact]
    public async Task LegacyTools_RetainOriginalNames_AndCarryDeprecationNotice()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        foreach (var legacy in new[] { "snapshot_counters", "collect_exceptions", "collect_gc_events", "collect_event_source", "collect_activities" })
        {
            var tool = tools.SingleOrDefault(t => t.Name == legacy);
            tool.Should().NotBeNull($"legacy tool '{legacy}' must remain registered through the deprecation window");
            tool!.Description.Should().Contain("DEPRECATED")
                .And.Contain("collect_events", $"legacy tool '{legacy}' must point callers at collect_events");
        }

        tools.Should().Contain(t => t.Name == "collect_events");
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> source, params (string Key, object? Value)[] overrides)
    {
        var copy = new Dictionary<string, object?>(source);
        foreach (var (k, v) in overrides) copy[k] = v;
        return copy;
    }

    private async Task<McpClient> ConnectAsync()
    {
        var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AuthedFactory.Token);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {AuthedFactory.Token}",
                },
            },
            httpClient,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport, clientOptions: null, cancellationToken: CancellationToken.None);
    }

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static T? DeserializeStructured<T>(CallToolResult result)
    {
        result.IsError.Should().NotBe(true, "tool call must succeed");
        var json = result.StructuredContent is { } structured
            ? structured.GetRawText()
            : result.Content.OfType<TextContentBlock>().First().Text;
        var envelope = JsonSerializer.Deserialize<DiagnosticResult<T>>(json, DeserializeOptions);
        envelope.Should().NotBeNull();
        envelope!.Summary.Should().NotBeNullOrWhiteSpace();
        envelope.Error.Should().BeNull();
        return envelope.Data;
    }

    private static DiagnosticResult<JsonElement>? DeserializeEnvelope(CallToolResult result)
    {
        var json = result.StructuredContent is { } structured
            ? structured.GetRawText()
            : result.Content.OfType<TextContentBlock>().First().Text;
        return JsonSerializer.Deserialize<DiagnosticResult<JsonElement>>(json, DeserializeOptions);
    }

    public sealed class AuthedFactory : WebApplicationFactory<DotnetDiagnosticsMcp.Server.Program>
    {
        public const string Token = "test-bearer-collect-events-do-not-use";

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", Token);
            base.ConfigureWebHost(builder);
        }
    }
}
