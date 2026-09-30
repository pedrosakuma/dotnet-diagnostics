using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(nameof(EnvSerial))]
public sealed class StdioCaptureAuthorityHttpTests
{
    [Theory]
    [InlineData("root")]
    [InlineData("*")]
    public async Task HttpCallerNamedStdioRoot_DoesNotInheritHostLocalCaptureOptIn(string wildcard)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:BearerTokens:0:Name", "stdio-root");
            builder.UseSetting("Auth:BearerTokens:0:Token", "stdio-name-test-value");
            builder.UseSetting("Auth:BearerTokens:0:Scopes:0", wildcard);
            builder.UseSetting("Stdio:CaptureBytes", "true");
            builder.UseSetting("Stdio:CaptureModifiers:0", "sensitive-heap-read");
            builder.UseSetting("Orchestrator:Enabled", "false");
            builder.UseSetting("AzureDiscovery:Enabled", "false");
        });
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "stdio-name-test-value");
        var transport = new HttpClientTransport(new()
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var result = await client.CallToolAsync("get_bytes", new Dictionary<string, object?>
        {
            ["kind"] = "captures", ["captureAction"] = "list",
        }, cancellationToken: timeout.Token);
        result.IsError.Should().BeTrue();
        result.StructuredContent.Should().BeNull("the scope filter must deny before capture lifecycle dispatch");
        JsonSerializer.Serialize(result).Should().Contain("module-bytes-read");
    }
}
