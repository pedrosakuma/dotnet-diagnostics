using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class StructuredToolErrorProtocolTests
{
    [Fact]
    public async Task InspectHeap_PermissionDenied_SetsProtocolIsErrorAndPreservesEnvelope()
    {
        await using var factory = new PermissionDeniedFactory();
        await using var client = await ConnectAsync(factory);

        var arguments = new Dictionary<string, object?>
        {
            ["source"] = "live",
            ["processId"] = Environment.ProcessId,
        };
        var preview = await client.CallToolAsync(
            "inspect_heap",
            arguments,
            cancellationToken: CancellationToken.None);
        var acknowledgement = preview.StructuredContent!.Value
            .GetProperty("safetyApproval")
            .GetProperty("requiredAcknowledgement");
        arguments["_dotnetDiagnostics"] = new Dictionary<string, object?>
            {
                ["acknowledgement"] = JsonSerializer.Deserialize<JsonElement>(acknowledgement.GetRawText()),
            };

        var result = await client.CallToolAsync(
            "inspect_heap",
            arguments,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.StructuredContent.Should().NotBeNull();

        var envelope = JsonSerializer.Deserialize<DiagnosticResult<JsonElement>>(
            result.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("PermissionDenied");
        envelope.Error.Message.Should().Contain("PTRACE_ATTACH");
        envelope.Summary.Should().Contain("ptrace");

        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().Contain("\"kind\":\"PermissionDenied\"");
        text.Should().Contain("inspect_heap could not attach");
    }

    private static async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", PermissionDeniedFactory.Token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {PermissionDeniedFactory.Token}",
                },
            },
            httpClient,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    private sealed class PermissionDeniedFactory : WebApplicationFactory<Program>
    {
        public const string Token = "structured-error-test-token";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Auth:BearerTokens:0:Name", "structured-error-test");
            builder.UseSetting("Auth:BearerTokens:0:Token", Token);
            builder.UseSetting("Auth:BearerTokens:0:Scopes:0", "root");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDumpInspector>();
                services.AddSingleton<IDumpInspector, PermissionDeniedDumpInspector>();
            });
            base.ConfigureWebHost(builder);
        }
    }

    private sealed class PermissionDeniedDumpInspector : IDumpInspector
    {
        private static ClrDiagnosticsException Error()
            => new("Could not PTRACE_ATTACH to any thread of the process.");

        public Task<HeapSnapshotArtifact> InspectAsync(
            string dumpFilePath,
            DumpInspectionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw Error();

        public Task<HeapSnapshotArtifact> InspectLiveAsync(
            int processId,
            DumpInspectionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw Error();

        public Task<HeapObjectInspection> InspectObjectAsync(
            HeapSnapshotArtifact snapshot,
            ulong address,
            CancellationToken cancellationToken = default)
            => throw Error();

        public Task<HeapGcRootInspection> InspectGcRootAsync(
            HeapSnapshotArtifact snapshot,
            ulong address,
            CancellationToken cancellationToken = default)
            => throw Error();

        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(
            HeapSnapshotArtifact snapshot,
            ulong address,
            CancellationToken cancellationToken = default)
            => throw Error();
    }
}
