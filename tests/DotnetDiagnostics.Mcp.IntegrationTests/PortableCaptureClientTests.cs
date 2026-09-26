using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(nameof(EnvSerial))]
public sealed class PortableCaptureClientTests(ITestOutputHelper output)
{
    [Fact]
    public async Task StatelessHttp_ExplicitlyRejectsTransferInitiationWithoutChangingOrdinaryLifecycle()
    {
        var root = Path.GetFullPath(Path.Combine(".validation", "portable-stateless", Guid.NewGuid().ToString("N")));
        try
        {
            var capture = await Capture(root, stdio: false);
            await using var host = await ClientHost.Start(root, stdio: false, native: false);
            await using var client = await host.AnotherClient(session: false);
            var key = Key();
            var denied = await Call(client, "export-start", new { key.operationId, key.requestedUtc,
                entries = new[] { new { captureId = capture.CaptureId } } }, expectSuccess: false);
            denied.IsError.Should().BeTrue();
            Data(denied).GetProperty("failure").GetProperty("reason").GetString().Should().Be("SessionRequired");
            var described = await client.CallToolAsync("get_bytes", new Dictionary<string, object?>
            {
                ["kind"] = "captures", ["captureAction"] = "describe", ["captureId"] = capture.CaptureId,
            }, cancellationToken: CancellationToken.None);
            described.IsError.Should().NotBeTrue();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealClients_ExportDownloadCancelAndSessionOwnership(bool stdio)
    {
        var root = Path.GetFullPath(Path.Combine(".validation", "portable-client", Guid.NewGuid().ToString("N")));
        try
        {
            var capture = await Capture(root, stdio);
            await using var host = await ClientHost.Start(root, stdio, native: false);
            var key = Key();
            var started = await Call(host.Client, "export-start", new { key.operationId, key.requestedUtc,
                entries = new[] { new { captureId = capture.CaptureId, label = "actual client bytes" } } });
            var id = Data(started).GetProperty("transferId").GetString()!;
            var ready = await Wait(host.Client, id, "Ready");
            var bytes = await Download(host.Client, id, ready);
            bytes.LongLength.Should().Be(ready.GetProperty("archiveBytes").GetInt64());
            Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(ready.GetProperty("archiveSha256").GetString());
            bytes[0].Should().Be((byte)'P');
            bytes[1].Should().Be((byte)'K');
            var repeat = await Call(host.Client, "download-chunk", new { transferId = id, offset = 0, count = 24576 });
            Convert.FromBase64String(Data(repeat).GetProperty("base64").GetString()!).Should().Equal(bytes[..Math.Min(24576, bytes.Length)]);
            if (!stdio)
            {
                await using var other = await host.AnotherClient();
                var denied = await Call(other, "transfer-status", new { transferId = id }, expectSuccess: false);
                denied.IsError.Should().BeTrue("same owner on another MCP session is not the initiating session");
            }
            else
            {
                await using var competing = await ClientHost.Start(root, stdio: true, native: false);
                var competingKey = Key();
                var second = await Call(competing.Client, "export-start", new
                {
                competingKey.operationId, competingKey.requestedUtc,
                entries = new[] { new { captureId = capture.CaptureId } }
                });
                var secondId = Data(second).GetProperty("transferId").GetString()!;
                var failed = await Wait(competing.Client, secondId, "Failed");
                failed.GetProperty("failure").GetProperty("code").GetString().Should().Be("Busy",
                "independent subprocesses must share Core's one-operation-per-owner lease");
            }
            await Call(host.Client, "transfer-cancel", new { transferId = id });
            (await Call(host.Client, "download-chunk", new { transferId = id, offset = 0, count = 24576 },
                expectSuccess: false)).IsError.Should().BeTrue();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [PortableImportTheory]
    [Trait("Category", "PortableImportNative")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealClients_CrossRootDownloadUploadImportAndOfflineQuery(bool stdio)
    {
        Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_PORTABLE_IMPORT_TEST").Should().Be("1",
            "native acceptance requires an explicit coordinated invocation; never silently pass an unexecuted case");
        var root = Path.GetFullPath(Path.Combine(".validation", "portable-roundtrip", Guid.NewGuid().ToString("N")));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        try
        {
            var capture = await Capture(source, stdio);
            byte[] archive;
            await using (var a = await ClientHost.Start(source, stdio, native: false))
            {
                var key = Key();
                var started = await Call(a.Client, "export-start", new { key.operationId, key.requestedUtc,
                    entries = new[] { new { captureId = capture.CaptureId, label = "portable label / data" } } });
                var id = Data(started).GetProperty("transferId").GetString()!;
                archive = await Download(a.Client, id, await Wait(a.Client, id, "Ready"));
                output.WriteLine("transport={0}; actualArchiveBytes={1}; sha256={2}; sourceCapture={3}",
                    stdio ? "stdio" : "http-session", archive.Length, Hash(archive), capture.CaptureId);
            }
            Directory.Delete(source, recursive: true);
            var operation = Key();
            string localCapture;
            string localArtifact;
            await using (var b = await ClientHost.Start(destination, stdio, native: true))
            {
                var start = await Call(b.Client, "import-start", new { operation.operationId, operation.requestedUtc,
                    archiveBytes = archive.Length, archiveSha256 = Hash(archive) });
                var id = Data(start).GetProperty("transferId").GetString()!;
                (await Call(b.Client, "import-commit", new { transferId = id }, expectSuccess: false)).IsError.Should().BeTrue();
                for (var offset = 0; offset < archive.Length; offset += 24576)
                {
                    var chunk = archive.AsSpan(offset, Math.Min(24576, archive.Length - offset)).ToArray();
                    var request = new { transferId = id, offset, base64 = Convert.ToBase64String(chunk), sha256 = Hash(chunk) };
                    if (offset == 0)
                    {
                        (await Call(b.Client, "upload-chunk", new { transferId = id, offset, request.base64,
                            sha256 = new string('0', 64) }, expectSuccess: false)).IsError.Should().BeTrue();
                        foreach (var invalid in new[] { " " + request.base64, new string('A', 32772), "%%%=" })
                            (await Call(b.Client, "upload-chunk", new { transferId = id, offset, base64 = invalid,
                                request.sha256 }, expectSuccess: false)).IsError.Should().BeTrue();
                        var gap = await Call(b.Client, "upload-chunk", new { transferId = id, offset = 24576,
                            request.base64, request.sha256 }, expectSuccess: false);
                        gap.IsError.Should().BeTrue();
                        Data(gap).GetProperty("nextOffset").GetInt64().Should().Be(0);
                    }
                    var accepted = Data(await Call(b.Client, "upload-chunk", request));
                    accepted.GetProperty("nextOffset").GetInt64().Should().Be(offset + chunk.Length);
                    var replayChunk = Data(await Call(b.Client, "upload-chunk", request));
                    replayChunk.GetProperty("replay").GetBoolean().Should().BeTrue();
                    replayChunk.GetProperty("nextOffset").GetInt64().Should().Be(offset + chunk.Length);
                }
                await Call(b.Client, "import-commit", new { transferId = id });
                await Call(b.Client, "import-commit", new { transferId = id });
                await Wait(b.Client, id, "Completed");
                var result = await Result(b.Client, operation.operationId, operation.requestedUtc);
                result.GetProperty("complete").GetBoolean().Should().BeTrue();
                var mapping = result.GetProperty("entries")[0].GetProperty("mapping");
                mapping.GetProperty("label").GetString().Should().Be("portable label / data");
                localCapture = mapping.GetProperty("localCaptureId").GetString()!;
                localArtifact = mapping.GetProperty("artifacts")[0].GetProperty("localArtifactId").GetString()!;
                localCapture.Should().NotBe(capture.CaptureId);
                output.WriteLine("destinationCapture={0}; destinationArtifact={1}; operation={2}; complete=true",
                    localCapture, localArtifact, operation.operationId);
                var replay = await Call(b.Client, "import-start", new { operation.operationId, operation.requestedUtc,
                    archiveBytes = archive.Length, archiveSha256 = Hash(archive) });
                Data(replay).GetProperty("transferId").GetString().Should().Be(id);
            }
            Array.Clear(archive);
            await using var restarted = await ClientHost.Start(destination, stdio, native: false);
            var reconciled = await Result(restarted.Client, operation.operationId, operation.requestedUtc);
            reconciled.GetProperty("entries")[0].GetProperty("mapping").GetProperty("localCaptureId")
                .GetString().Should().Be(localCapture);
            var records = await restarted.Client.CallToolAsync("query_snapshot", new Dictionary<string, object?>
            {
                ["captureId"] = localCapture, ["artifactId"] = localArtifact, ["view"] = "records",
            }, cancellationToken: CancellationToken.None);
            records.IsError.Should().NotBeTrue(JsonSerializer.Serialize(records));
            var rows = records.StructuredContent!.Value.GetProperty("data").GetProperty("records");
            rows.GetArrayLength().Should().Be(2);
            rows[0].GetProperty("record").GetProperty("name").GetString().Should().Be("exact-record");
            output.WriteLine("restartResultStable=true; sourceDeleted=true; retainedRecords={0}", rows.GetArrayLength());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task<CaptureInfo> Capture(string root, bool stdio)
    {
        var store = new SqliteCaptureStore(new TestRoot(root));
        var owner = stdio ? StdioRootPrincipalAccessor.Instance.Current!.OwnershipKey
            : PrincipalOwnershipKey.ForOpaqueEntry("Auth:BearerTokens:0");
        await using var writer = await store.CreateAsync(new("client source"), new(owner));
        var artifact = writer.AddArtifact("counters", "retained normalized");
        for (var i = 0; i < 2; i++)
            writer.TryAppend(artifact, new(Name: "exact-record", ThreadId: 42,
                Fields: [new("value", CaptureFieldKind.SignedInteger, Int64Value: long.MaxValue)])).Should().BeTrue();
        return await writer.CompleteAsync();
    }

    private static (string operationId, DateTimeOffset requestedUtc) Key() => (Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static JsonElement Data(CallToolResult result) => result.StructuredContent!.Value.GetProperty("data");

    private static async Task<CallToolResult> Call(McpClient client, string action, object input, bool expectSuccess = true)
    {
        var arguments = new Dictionary<string, object?> { ["kind"] = "captures", ["captureAction"] = action, ["captureTransfer"] = input };
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var result = await client.CallToolAsync("get_bytes", arguments, cancellationToken: CancellationToken.None);
            if (result.StructuredContent is { } envelope && envelope.TryGetProperty("safetyApproval", out var approval) &&
                approval.TryGetProperty("requiredAcknowledgement", out var acknowledgement))
            {
                arguments["_dotnetDiagnostics"] = new { acknowledgement = acknowledgement.Clone() };
                result = await client.CallToolAsync("get_bytes", arguments, cancellationToken: CancellationToken.None);
            }
            JsonSerializer.SerializeToUtf8Bytes(result).Length.Should().BeLessThanOrEqualTo(128 * 1024);
            if (expectSuccess && result.IsError == true && result.StructuredContent is { } errorEnvelope &&
                errorEnvelope.TryGetProperty("error", out var error) &&
                error.TryGetProperty("detail", out var detail) && detail.GetString() == "Busy")
            {
                await Task.Delay(1000);
                continue;
            }
            if (expectSuccess) result.IsError.Should().NotBeTrue(JsonSerializer.Serialize(result));
            return result;
        }
        throw new TimeoutException("Protocol Busy retry budget exhausted.");
    }

    private static async Task<JsonElement> Wait(McpClient client, string id, string expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var data = Data(await Call(client, "transfer-status", new { transferId = id }));
            var state = data.GetProperty("state").GetString();
            if (state == expected) return data;
            (state is "Failed" or "Cancelled" or "Expired").Should().BeFalse(data.GetRawText());
            await Task.Delay(100, deadline.Token);
        }
    }

    private static async Task<JsonElement> Result(McpClient client, string operationId, DateTimeOffset requestedUtc)
    {
        for (var i = 0; i < 50; i++)
        {
            var result = await Call(client, "import-result", new { operationId, requestedUtc, afterEntry = -1, pageSize = 1 }, false);
            if (result.IsError != true) return Data(result);
            await Task.Delay(100);
        }
        throw new TimeoutException("Import result did not become readable.");
    }

    private static async Task<byte[]> Download(McpClient client, string id, JsonElement descriptor)
    {
        using var destination = new MemoryStream();
        var length = descriptor.GetProperty("archiveBytes").GetInt64();
        for (long offset = 0; offset < length; offset += 24576)
        {
            var chunk = Data(await Call(client, "download-chunk", new { transferId = id, offset, count = 24576 }));
            var bytes = Convert.FromBase64String(chunk.GetProperty("base64").GetString()!);
            Hash(bytes).Should().Be(chunk.GetProperty("sha256").GetString());
            await destination.WriteAsync(bytes);
        }
        return destination.ToArray();
    }

    private sealed record TestRoot(string Root) : IArtifactRootProvider;

    private sealed class PortableImportTheoryAttribute : TheoryAttribute
    {
        public PortableImportTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_PORTABLE_IMPORT_TEST") != "1")
                Skip = "Requires an explicitly scheduled native portable-import validation run.";
        }
    }

    private sealed class ClientHost : IAsyncDisposable
    {
        private const string Token = "portable-client-test-token";
        private WebApplicationFactory<Program>? _factory;
        internal McpClient Client { get; private set; } = null!;

        internal static async Task<ClientHost> Start(string root, bool stdio, bool native)
        {
            var host = new ClientHost();
            if (stdio)
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
                    directory = directory.Parent;
                directory.Should().NotBeNull();
                var dll = Path.Combine(directory!.FullName, "src", "DotnetDiagnostics.Mcp", "bin", "Release", "net10.0", "DotnetDiagnostics.Mcp.dll");
                File.Exists(dll).Should().BeTrue("real subprocess assets are required");
                var environment = new Dictionary<string, string?>
                {
                    ["MCP_ARTIFACT_ROOT"] = root, ["Orchestrator__Enabled"] = "false", ["AzureDiscovery__Enabled"] = "false",
                    ["DOTNET_DIAGNOSTICS_IMPORT_WORKER"] = native ? Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_IMPORT_WORKER") : "",
                    ["DOTNET_DIAGNOSTICS_SQLITE_LIBRARY"] = native ? Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_SQLITE_LIBRARY") : "",
                };
                host.Client = await McpClient.CreateAsync(new StdioClientTransport(new()
                {
                    Command = "dotnet", Arguments = [dll, "--stdio", "--Stdio:CaptureBytes=true"],
                    EnvironmentVariables = environment,
                }), cancellationToken: CancellationToken.None);
            }
            else
            {
                host._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("Auth:BearerTokens:0:Name", "portable-test");
                    builder.UseSetting("Auth:BearerTokens:0:Token", Token);
                    builder.UseSetting("Auth:BearerTokens:0:Scopes:0", "root");
                    builder.UseSetting("Auth:BearerTokens:0:Scopes:1", "module-bytes-read");
                    builder.UseSetting("Orchestrator:Enabled", "false");
                    builder.UseSetting("AzureDiscovery:Enabled", "false");
                    builder.UseSetting("DOTNET_DIAGNOSTICS_IMPORT_WORKER",
                        native ? Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_IMPORT_WORKER")! : "");
                    builder.UseSetting("DOTNET_DIAGNOSTICS_SQLITE_LIBRARY",
                        native ? Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_SQLITE_LIBRARY")! : "");
                    builder.ConfigureServices(services =>
                    {
                        services.AddSingleton<IArtifactRootProvider>(new TestRoot(root));
                        services.AddSingleton(new PortableTransferOptions(native
                            ? new PortableCaptureImportWorker(Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_IMPORT_WORKER")!,
                                Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_SQLITE_LIBRARY")!) : null));
                    });
                });
                host.Client = await host.AnotherClient();
            }
            return host;
        }

        internal async Task<McpClient> AnotherClient(bool session = true)
        {
            var http = _factory!.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            }, http, ownsHttpClient: true), new McpClientOptions { ProtocolVersion = session ? "2025-11-25" : null },
                cancellationToken: CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            if (_factory is not null) await _factory.DisposeAsync();
        }
    }
}
