using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(nameof(EnvSerial))]
public sealed class DurableCaptureHttpTests : IDisposable
{
    private const string Token = "durable-http-test-token-not-production";
    private readonly string _root = Path.GetFullPath(Path.Combine(
        ".validation", "durable-mcp-http", Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task ExistingCollectionAndQueryTools_PersistAcrossServerRestart()
    {
        string captureId;
        string artifactId;
        await using (var factory = Factory())
        await using (var client = await Connect(factory))
        {
            var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
            tools.Should().HaveCount(13);
            tools.Select(tool => tool.Name).Should().NotContain("capture_describe");
            foreach (var name in new[] { "collect_events", "collect_sample", "collect_batch", "collect_thread_snapshot", "inspect_heap" })
            {
                var schema = tools.Single(tool => tool.Name == name).JsonSchema.GetProperty("properties");
                schema.GetProperty("persist").GetProperty("default").GetBoolean().Should().BeFalse();
                schema.TryGetProperty("durableCaptures", out _).Should().BeFalse("the host service is DI-only");
            }

            var ephemeral = await client.CallToolAsync("collect_events",
                new Dictionary<string, object?> { ["kind"] = "counters", ["durationSeconds"] = 1 },
                cancellationToken: CancellationToken.None);
            ephemeral.IsError.Should().NotBeTrue();
            Envelope(ephemeral).TryGetProperty("capture", out _).Should().BeFalse();
            Directory.Exists(Path.Combine(_root, "captures")).Should().BeFalse();

            var persisted = await client.CallToolAsync("collect_events",
                new Dictionary<string, object?> { ["kind"] = "counters", ["durationSeconds"] = 1, ["persist"] = true },
                cancellationToken: CancellationToken.None);
            persisted.IsError.Should().NotBeTrue();
            var capture = Envelope(persisted).GetProperty("capture");
            captureId = capture.GetProperty("captureId").GetString()!;
            artifactId = capture.GetProperty("artifacts").EnumerateArray()
                .Single(artifact => artifact.GetProperty("kind").GetString() == "counters")
                .GetProperty("artifactId").GetString()!;
            Directory.EnumerateFiles(_root, "*.nettrace", SearchOption.AllDirectories).Should().BeEmpty();
        }

        await using var restarted = Factory();
        await using var reader = await Connect(restarted);
        var query = await reader.CallToolAsync("query_snapshot", new Dictionary<string, object?>
        {
            ["captureId"] = captureId, ["artifactId"] = artifactId, ["view"] = "summary",
        }, cancellationToken: CancellationToken.None);
        query.IsError.Should().NotBeTrue();
        Envelope(query).GetProperty("handle").GetString().Should().NotBeNullOrWhiteSpace();
        Envelope(query).GetProperty("capture").GetProperty("captureId").GetString().Should().Be(captureId);
    }

    private WebApplicationFactory<Program> Factory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:BearerTokens:0:Name", "durable-test");
            builder.UseSetting("Auth:BearerTokens:0:Token", Token);
            builder.UseSetting("Auth:BearerTokens:0:Scopes:0", "root");
            builder.UseSetting("Auth:BearerTokens:0:Scopes:1", "module-bytes-read");
            builder.UseSetting("Orchestrator:Enabled", "false");
            builder.UseSetting("AzureDiscovery:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IArtifactRootProvider>(new TestRoot(_root));
                services.AddSingleton<ICounterCollector>(new CounterCollector());
                services.AddSingleton<IProcessContextResolver>(new Resolver());
            });
        });

    private static async Task<McpClient> Connect(WebApplicationFactory<Program> factory)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    private static JsonElement Envelope(CallToolResult result)
    {
        result.StructuredContent.Should().NotBeNull();
        return result.StructuredContent!.Value;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record TestRoot(string RootPath) : IArtifactRootProvider
    {
        public string Root => RootPath;
    }

    private sealed class Resolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(new ProcessContext(
                123, RuntimeFlavor.CoreClr, true, true, false, "10.0.0"), null));
    }

    private sealed class CounterCollector : ICounterCollector
    {
        public Task<CounterSnapshot> CollectAsync(int processId, TimeSpan duration,
            IReadOnlyList<string>? providers = null, IReadOnlyList<string>? meters = null,
            int intervalSeconds = 1, int maxInstrumentTimeSeries = 1000,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CounterSnapshot(processId, DateTimeOffset.UtcNow, duration,
                [new CounterValue("System.Runtime", "cpu-usage", "CPU", 42, CounterKind.Mean)], [], []));
    }
}
