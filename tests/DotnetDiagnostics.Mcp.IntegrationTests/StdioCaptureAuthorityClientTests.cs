using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Mcp.Security;
using FluentAssertions;
using ModelContextProtocol.Client;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(nameof(EnvSerial))]
public sealed class StdioCaptureAuthorityClientTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(
        ".validation", "stdio-capture-authority", Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualStdioClient_RequiresLocalOptInForLifecycle(bool enabled)
    {
        var store = new SqliteCaptureStore(new TestRoot(_root));
        await using var writer = await store.CreateAsync(new("stdio authority fixture"),
            new(StdioRootPrincipalAccessor.Instance.Current!.OwnershipKey));
        var artifact = writer.AddArtifact("counters", "retained counters");
        writer.TryAppend(artifact, new(Name: "cpu-usage")).Should().BeTrue();
        var capture = await writer.CompleteAsync();

        var transport = Transport(enabled);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var result = await client.CallToolAsync("get_bytes", new Dictionary<string, object?>
        {
            ["kind"] = "captures", ["captureAction"] = "describe", ["captureId"] = capture.CaptureId,
        }, cancellationToken: timeout.Token);
        if (enabled)
        {
            result.IsError.Should().NotBeTrue("actual result: {0}", JsonSerializer.Serialize(result));
            result.StructuredContent.Should().NotBeNull();
            var envelope = result.StructuredContent!.Value;
            envelope.GetProperty("data").GetProperty("captureId").GetString().Should().Be(capture.CaptureId);
            var records = await client.CallToolAsync("query_snapshot", new Dictionary<string, object?>
            {
                ["captureId"] = capture.CaptureId, ["artifactId"] = artifact, ["view"] = "records",
            }, cancellationToken: timeout.Token);
            records.IsError.Should().NotBeTrue();
            records.StructuredContent!.Value.GetProperty("data").GetProperty("records").GetArrayLength()
                .Should().Be(1);
        }
        else
        {
            result.IsError.Should().BeTrue();
            JsonSerializer.Serialize(result).Should().Contain("module-bytes-read");
            result.StructuredContent.Should().BeNull("the scope filter denies before lifecycle dispatch");
        }
    }

    [Theory]
    [InlineData("heap-snapshot", "sensitive-heap-read", false)]
    [InlineData("heap-snapshot", "sensitive-heap-read", true)]
    [InlineData("event-source", "eventsource-any", false)]
    [InlineData("event-source", "eventsource-any", true)]
    [InlineData("method-params-capture", "sensitive-parameter-read", false)]
    [InlineData("method-params-capture", "sensitive-parameter-read", true)]
    public async Task ActualStdioClient_SensitiveRecordsRequireExplicitConfiguredModifier(
        string kind, string modifier, bool granted)
    {
        var store = new SqliteCaptureStore(new TestRoot(_root));
        await using var writer = await store.CreateAsync(new("sensitive retained fixture"),
            new(StdioRootPrincipalAccessor.Instance.Current!.OwnershipKey));
        var artifact = writer.AddArtifact(kind, "sensitive evidence");
        writer.TryAppend(artifact, new(Name: "sensitive-fixture-record")).Should().BeTrue();
        var capture = await writer.CompleteAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await McpClient.CreateAsync(
            Transport(enabled: true, granted ? modifier : null), cancellationToken: timeout.Token);
        var result = await client.CallToolAsync("query_snapshot", new Dictionary<string, object?>
        {
            ["captureId"] = capture.CaptureId, ["artifactId"] = artifact, ["view"] = "records",
        }, cancellationToken: timeout.Token);
        if (granted)
        {
            result.IsError.Should().NotBeTrue("actual result: {0}", JsonSerializer.Serialize(result));
            var records = result.StructuredContent!.Value.GetProperty("data").GetProperty("records");
            records.GetArrayLength().Should().Be(1);
            records[0].GetProperty("record").GetProperty("name").GetString().Should().Be("sensitive-fixture-record");
        }
        else
        {
            result.IsError.Should().BeTrue();
            JsonSerializer.Serialize(result).Should().NotContain("sensitive-fixture-record");
        }
    }

    [Fact]
    public async Task ActualStdioHost_RejectsOversizedFrameBeforeSdkDispatch()
    {
        using var process = new Process
        {
            StartInfo = new()
            {
                FileName = "dotnet",
                ArgumentList = { ServerDll(), "--stdio" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.Environment["MCP_ARTIFACT_ROOT"] = _root;
        process.StartInfo.Environment["Orchestrator__Enabled"] = "false";
        process.StartInfo.Environment["AzureDiscovery__Enabled"] = "false";
        process.Start().Should().BeTrue();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"framing-test","version":"1"}}}""");
            await process.StandardInput.FlushAsync(timeout.Token);
            var initialized = await process.StandardOutput.ReadLineAsync(timeout.Token);
            using (var message = JsonDocument.Parse(initialized!))
                message.RootElement.GetProperty("id").GetInt32().Should().Be(1);
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await process.StandardInput.WriteAsync(Encoding.UTF8.GetString(
                McpRequestFramingTests.Frame(65537, capture: true)));
            await process.StandardInput.FlushAsync(timeout.Token);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            output.Should().NotContain("\"id\":2", "the oversized request must not reach tools/call");
            (await stderr).Should().Contain("64 KiB");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static string ServerDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the real subprocess test requires the repository build");
        var dll = Path.Combine(directory!.FullName, "src", "DotnetDiagnostics.Mcp", "bin",
            new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
            new DirectoryInfo(AppContext.BaseDirectory).Name, "DotnetDiagnostics.Mcp.dll");
        File.Exists(dll).Should().BeTrue("missing subprocess assets are a test failure, not passing coverage");
        return dll;
    }

    private StdioClientTransport Transport(bool enabled, string? modifier = null)
    {
        var arguments = new List<string> { ServerDll(), "--stdio", "--Stdio:CaptureBytes=" + enabled };
        if (modifier is not null) arguments.Add("--Stdio:CaptureModifiers:0=" + modifier);
        return new(new()
        {
            Command = "dotnet",
            Arguments = arguments,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["MCP_ARTIFACT_ROOT"] = _root,
                ["Orchestrator__Enabled"] = "false",
                ["AzureDiscovery__Enabled"] = "false",
            },
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record TestRoot(string RootPath) : IArtifactRootProvider
    {
        public string Root => RootPath;
    }
}
