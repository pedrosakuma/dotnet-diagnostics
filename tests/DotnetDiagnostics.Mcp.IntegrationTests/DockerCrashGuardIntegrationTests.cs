using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Opt-in end-to-end acceptance test for the anchored local Docker PID namespace.
/// Run through <c>scripts/test-docker-crash-guard.sh</c>.
/// </summary>
[Trait("Category", "DockerIntegration")]
public sealed class DockerCrashGuardIntegrationTests
{
    private const string EnableEnvVar = "DOTNET_DBG_MCP_DOCKER_CRASH_GUARD_TEST";
    private const string McpUrl = "http://127.0.0.1:18887/mcp";
    private const string HealthUrl = "http://127.0.0.1:18887/health";
    private const string TargetUrl = "http://127.0.0.1:18180";
    private const string BearerToken = "dev-token";

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly ITestOutputHelper _output;

    public DockerCrashGuardIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 120_000)]
    public async Task AnchoredNamespace_TargetCrash_ReturnsStructuredResultAndKeepsSidecarAlive()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableEnvVar), "1", StringComparison.Ordinal))
        {
            _output.WriteLine($"{EnableEnvVar} is unset; skipping Docker crash-guard acceptance test.");
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = cancellation.Token;
        await using var client = await ConnectAsync(ct).ConfigureAwait(false);

        var listResult = await client.CallToolAsync(
            "inspect_process",
            new Dictionary<string, object?>
            {
                ["view"] = "list",
                ["commandLineContains"] = "BadCodeSample",
            },
            cancellationToken: ct).ConfigureAwait(false);
        var processes = DeserializeResult<InspectProcessReport>(listResult);
        processes.Error.Should().BeNull(processes.Summary);
        processes.Data.Should().NotBeNull();
        var target = processes.Data!.List.Should().ContainSingle().Which;
        target.ProcessId.Should().NotBe(1, "the namespace anchor, not the crashable target, must own PID 1");

        using var targetHttp = new HttpClient
        {
            BaseAddress = new Uri(TargetUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
        var crashDriver = TriggerCrashAsync(targetHttp, ct);

        var guardResult = await client.CallToolAsync(
            "collect_events",
            new Dictionary<string, object?>
            {
                ["kind"] = "crash-guard",
                ["processId"] = target.ProcessId,
                ["durationSeconds"] = 30,
                ["maxRecent"] = 20,
            },
            cancellationToken: ct).ConfigureAwait(false);
        await crashDriver.ConfigureAwait(false);

        var guard = DeserializeResult<CollectEventsEnvelope>(guardResult);
        guard.Error.Should().BeNull(guard.Summary);
        guard.Handle.Should().NotBeNullOrWhiteSpace();
        guard.Data.Should().NotBeNull();
        guard.Data!.CrashGuard.Should().NotBeNull();
        guard.Data.CrashGuard!.ProcessExited.Should().BeTrue();
        guard.Data.CrashGuard.UnhandledExceptionObserved.Should().BeTrue();
        guard.Data.CrashGuard.FinalException.Should().NotBeNull();
        guard.Data.CrashGuard.FinalException!.ExceptionType.Should().Contain("InvalidOperationException");
        guard.Data.CrashGuard.FinalException.ExceptionMessage.Should().Contain("crash fixture");

        var stackResult = await client.CallToolAsync(
            "query_snapshot",
            new Dictionary<string, object?>
            {
                ["handle"] = guard.Handle,
                ["view"] = "stack",
            },
            cancellationToken: ct).ConfigureAwait(false);
        var stack = DeserializeResult<CollectionQueryResult>(stackResult);
        stack.Error.Should().BeNull(stack.Summary);
        stack.Data.Should().NotBeNull();
        stack.Data!.Kind.Should().Be(CollectionHandleKinds.CrashGuardSnapshot);
        stack.Data.View.Should().Be("stack");
        var stackPayload = ((JsonElement)stack.Data.Payload)
            .Deserialize<CrashGuardStackView>(DeserializeOptions);
        stackPayload.Should().NotBeNull();
        stackPayload!.FinalException.Should().NotBeNull();

        using var health = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var healthResponse = await health.GetAsync(HealthUrl, ct).ConfigureAwait(false);
        healthResponse.EnsureSuccessStatusCode();
    }

    private static async Task TriggerCrashAsync(HttpClient client, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await client.GetAsync("/crash?mode=unhandled", cancellationToken).ConfigureAwait(false);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);
        }
        catch (HttpRequestException)
        {
            // The process may close Kestrel before the 202 response reaches the client.
        }
    }

    private static async Task<McpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var endpoint = new Uri(McpUrl);
        var httpClient = new HttpClient { BaseAddress = endpoint };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {BearerToken}",
                },
            },
            httpClient,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(
            transport,
            clientOptions: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static DiagnosticResult<T> DeserializeResult<T>(CallToolResult result)
    {
        var json = result.StructuredContent?.GetRawText()
            ?? result.Content.OfType<TextContentBlock>().First().Text;
        return JsonSerializer.Deserialize<DiagnosticResult<T>>(json, DeserializeOptions)
            ?? throw new JsonException("MCP response did not contain a diagnostic envelope.");
    }
}
