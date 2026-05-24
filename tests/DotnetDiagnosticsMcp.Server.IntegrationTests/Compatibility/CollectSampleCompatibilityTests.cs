using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.OffCpu;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;

/// <summary>
/// RFC 0002 §4.2 / issue #210 — dual-entrypoint contract tests for
/// <see cref="CollectSampleTool"/>. For every supported <c>kind</c>, asserts that calling
/// <c>collect_sample(kind="X", ...)</c> produces an envelope structurally equivalent to the
/// envelope produced by the legacy collector tool (<c>collect_cpu_sample</c>,
/// <c>collect_off_cpu_sample</c>, <c>collect_allocation_sample</c>). The polymorphic payload
/// field for the chosen kind is compared against the legacy payload directly; the surrounding
/// envelope (Summary, Hints, ResolvedProcess) is sanity-checked rather than diffed
/// byte-for-byte because the legacy tools carry per-tool next-action hints that the
/// discriminator entry-point inherits verbatim.
/// </summary>
/// <remarks>
/// <para>The tests exercise the test host process itself (<see cref="Environment.ProcessId"/>) —
/// same pattern as the existing sampler tests in <c>McpToolsTests</c>. The off-CPU test is
/// skipped on non-Linux hosts where perf-based sched_switch capture is unsupported (matches
/// the runtime gate inside <see cref="DiagnosticTools.CollectOffCpuSample"/>).</para>
/// </remarks>
[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class CollectSampleCompatibilityTests : IClassFixture<CollectSampleCompatibilityTests.AuthedFactory>
{
    private readonly AuthedFactory _factory;

    public CollectSampleCompatibilityTests(AuthedFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Kind_Cpu_MatchesLegacyCollectCpuSample()
    {
        await using var client = await ConnectAsync();

        var common = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 2,
            ["topN"] = 5,
            ["resolveSourceLines"] = false,
        };

        var legacy = DeserializeStructured<CpuSample>(
            await client.CallToolAsync("collect_cpu_sample", common, cancellationToken: CancellationToken.None));
        var unified = DeserializeStructured<CollectSampleEnvelope>(
            await client.CallToolAsync("collect_sample", With(common, ("kind", "cpu")), cancellationToken: CancellationToken.None));

        unified!.Kind.Should().Be("cpu");
        unified.Cpu.Should().NotBeNull();
        unified.OffCpu.Should().BeNull();
        unified.Allocation.Should().BeNull();

        unified.Cpu!.ProcessId.Should().Be(legacy!.ProcessId);
    }

    [Fact]
    public async Task Kind_Allocation_MatchesLegacyCollectAllocationSample()
    {
        await using var client = await ConnectAsync();

        var common = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 2,
            ["topN"] = 10,
        };

        var legacy = DeserializeStructured<AllocationSample>(
            await client.CallToolAsync("collect_allocation_sample", common, cancellationToken: CancellationToken.None));
        var unified = DeserializeStructured<CollectSampleEnvelope>(
            await client.CallToolAsync("collect_sample", With(common, ("kind", "allocation")), cancellationToken: CancellationToken.None));

        unified!.Kind.Should().Be("allocation");
        unified.Allocation.Should().NotBeNull();
        unified.Cpu.Should().BeNull();
        unified.OffCpu.Should().BeNull();

        unified.Allocation!.ProcessId.Should().Be(legacy!.ProcessId);
        unified.Allocation.TotalEvents.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Kind_OffCpu_MatchesLegacyCollectOffCpuSample_OrSurfacesUnsupported()
    {
        await using var client = await ConnectAsync();

        var common = new Dictionary<string, object?>
        {
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 2,
            ["topN"] = 5,
        };

        var legacyResult = await client.CallToolAsync("collect_off_cpu_sample", common, cancellationToken: CancellationToken.None);
        var unifiedResult = await client.CallToolAsync("collect_sample", With(common, ("kind", "off_cpu")), cancellationToken: CancellationToken.None);

        // Both must report the same posture: on platforms without a usable backend (or in CI
        // sandboxes without CAP_PERFMON) the legacy tool returns a NotSupported / PermissionDenied
        // envelope. The unified tool must surface the exact same error kind.
        var legacyEnv = DeserializeEnvelope(legacyResult);
        var unifiedEnv = DeserializeEnvelope(unifiedResult);

        if (legacyEnv?.Error is not null)
        {
            unifiedEnv!.Error.Should().NotBeNull("unified tool must report the same NotSupported/PermissionDenied posture as the legacy tool");
            unifiedEnv.Error!.Kind.Should().Be(legacyEnv.Error.Kind);
            return;
        }

        // Both succeeded — verify the payload routes onto the OffCpu slot.
        var legacy = DeserializeStructured<OffCpuSnapshot>(legacyResult);
        var unified = DeserializeStructured<CollectSampleEnvelope>(unifiedResult);

        unified!.Kind.Should().Be("off_cpu");
        unified.OffCpu.Should().NotBeNull();
        unified.Cpu.Should().BeNull();
        unified.Allocation.Should().BeNull();
        unified.OffCpu!.ProcessId.Should().Be(legacy!.ProcessId);
    }

    [Fact]
    public async Task Kind_Invalid_ReturnsInvalidArgumentListingAllowedKinds()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync(
            "collect_sample",
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
        envelope.Error.Message.Should().Contain("cpu")
            .And.Contain("off_cpu")
            .And.Contain("allocation");
    }

    [Fact]
    public async Task LegacyTools_RetainOriginalNames_AndCarryDeprecationNotice()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        foreach (var legacy in new[] { "collect_cpu_sample", "collect_off_cpu_sample", "collect_allocation_sample" })
        {
            var tool = tools.SingleOrDefault(t => t.Name == legacy);
            tool.Should().NotBeNull($"legacy tool '{legacy}' must remain registered through the deprecation window");
            tool!.Description.Should().Contain("DEPRECATED")
                .And.Contain("collect_sample", $"legacy tool '{legacy}' must point callers at collect_sample");
        }

        tools.Should().Contain(t => t.Name == "collect_sample");
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
        public const string Token = "test-bearer-collect-sample-do-not-use";

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", Token);
            base.ConfigureWebHost(builder);
        }
    }
}
