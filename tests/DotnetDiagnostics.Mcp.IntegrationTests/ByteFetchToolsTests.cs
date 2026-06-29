using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Dump;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(nameof(EnvSerial))]
public sealed class ByteFetchToolsTests : IAsyncLifetime
{
    private LiveSampleProcess? _sample;

    private int SampleProcessId => _sample?.ProcessId ?? throw new InvalidOperationException("Sample not started.");
    private string SampleDll => _sample?.SampleDll ?? throw new InvalidOperationException("Sample DLL not resolved.");

    [Fact]
    public async Task GetModuleBytes_MissingScope_IsRejectedByAuthorizationFilter()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("counters-only", "scope-miss-token", new[] { "read-counters" }));
        await using var client = await ConnectWithTokenAsync(factory, "scope-miss-token");

        var mvid = GetSampleMvid();
        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "module", ["moduleVersionId"] = mvid, ["processId"] = SampleProcessId },
            cancellationToken: CancellationToken.None);

        var (_, envelope) = ParseForbidden(result);
        envelope.GetProperty("kind").GetString().Should().Be("forbidden");
        envelope.GetProperty("required_scopes").EnumerateArray().Select(e => e.GetString())
            .Should().ContainSingle().Which.Should().Be("module-bytes-read");
    }

    [Fact]
    public async Task GetModuleBytes_RootTokenWithoutExplicitModifier_IsRejected()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("root-only", "root-token", new[] { "*" }));
        await using var client = await ConnectWithTokenAsync(factory, "root-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "module", ["moduleVersionId"] = GetSampleMvid(), ["processId"] = SampleProcessId },
            cancellationToken: CancellationToken.None);

        result.IsError.Should().NotBeTrue();
        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("Forbidden");
        envelope.Error.Message.Should().Contain("module-bytes-read");
    }

    [Fact]
    public async Task GetModuleBytes_WithExplicitScope_Succeeds()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("module-reader", "module-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "module-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "module", ["moduleVersionId"] = GetSampleMvid(), ["processId"] = SampleProcessId, ["maxBytes"] = 512 },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().BeNull();
        var data = envelope.Data;
        data.GetProperty("asset").GetString().Should().Be("pe");
        Convert.FromBase64String(data.GetProperty("base64Chunk").GetString()!).Take(2).Should().Equal((byte)'M', (byte)'Z');
    }

    [Fact]
    public async Task GetDumpBytes_RejectsPathTraversal()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("module-reader", "dump-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "dump-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "dump", ["dumpFilePath"] = "../escape.dmp" },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("InvalidArtifactPath");
    }

    [Fact]
    public async Task GetDumpBytes_RoundTripsDumpContent()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("byte-fetcher", "roundtrip-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "roundtrip-token");

        var dumpFilePath = Path.Combine(artifactRoot.Path, "roundtrip.dmp");
        var expected = new byte[2 * 1024 * 1024 + 137];
        RandomNumberGenerator.Fill(expected);
        await File.WriteAllBytesAsync(dumpFilePath, expected, CancellationToken.None);

        var bytes = new List<byte>();
        long offset = 0;
        string? sha256 = null;
        while (true)
        {
            var chunkResult = await client.CallToolAsync(
                "get_bytes",
                new Dictionary<string, object?>
                {
                    ["kind"] = "dump",
                    ["dumpFilePath"] = dumpFilePath,
                    ["offset"] = offset,
                    ["maxBytes"] = 1024 * 1024,
                },
                cancellationToken: CancellationToken.None);

            var chunkEnvelope = DeserializeEnvelope(chunkResult);
            chunkEnvelope.Should().NotBeNull();
            chunkEnvelope!.Error.Should().BeNull();
            var data = chunkEnvelope.Data;
            bytes.AddRange(Convert.FromBase64String(data.GetProperty("base64Chunk").GetString()!));
            sha256 ??= data.GetProperty("sha256").GetString();
            if (!data.TryGetProperty("nextOffset", out var next) || next.ValueKind == JsonValueKind.Null)
            {
                break;
            }

            offset = next.GetInt64();
        }

        var assembled = bytes.ToArray();
        sha256.Should().Be(Convert.ToHexString(SHA256.HashData(assembled)).ToLowerInvariant());
        assembled.Should().Equal(expected);
    }

    [Fact]
    public async Task GetTraceBytes_RejectsPathTraversal()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("module-reader", "trace-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "trace-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "trace", ["traceFilePath"] = "../escape.nettrace" },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("InvalidArtifactPath");
    }

    [Fact]
    public async Task GetTraceBytes_RoundTripsTraceContent()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("byte-fetcher", "trace-roundtrip-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "trace-roundtrip-token");

        var traceFilePath = Path.Combine(artifactRoot.Path, "roundtrip.nettrace");
        var expected = new byte[2 * 1024 * 1024 + 137];
        RandomNumberGenerator.Fill(expected);
        await File.WriteAllBytesAsync(traceFilePath, expected, CancellationToken.None);

        var bytes = new List<byte>();
        long offset = 0;
        string? sha256 = null;
        string? kind = null;
        while (true)
        {
            var chunkResult = await client.CallToolAsync(
                "get_bytes",
                new Dictionary<string, object?>
                {
                    ["kind"] = "trace",
                    ["traceFilePath"] = traceFilePath,
                    ["offset"] = offset,
                    ["maxBytes"] = 1024 * 1024,
                },
                cancellationToken: CancellationToken.None);

            var chunkEnvelope = DeserializeEnvelope(chunkResult);
            chunkEnvelope.Should().NotBeNull();
            chunkEnvelope!.Error.Should().BeNull();
            var data = chunkEnvelope.Data;
            kind ??= data.GetProperty("kind").GetString();
            bytes.AddRange(Convert.FromBase64String(data.GetProperty("base64Chunk").GetString()!));
            sha256 ??= data.GetProperty("sha256").GetString();
            if (!data.TryGetProperty("nextOffset", out var next) || next.ValueKind == JsonValueKind.Null)
            {
                break;
            }

            offset = next.GetInt64();
        }

        kind.Should().Be("trace");
        var assembled = bytes.ToArray();
        sha256.Should().Be(Convert.ToHexString(SHA256.HashData(assembled)).ToLowerInvariant());
        assembled.Should().Equal(expected);
    }

    [Fact]
    public async Task GetTraceBytes_MissingExplicitScope_IsRejected()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("root-only", "trace-root-token", new[] { "*" }));
        await using var client = await ConnectWithTokenAsync(factory, "trace-root-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "trace", ["traceFilePath"] = "x.nettrace" },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("Forbidden");
        envelope.Error.Message.Should().Contain("module-bytes-read");
    }

    [Fact]
    public async Task GetDumpBytes_RejectsArtifactsOver256MiB()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        var dumpPath = Path.Combine(artifactRoot.Path, "too-large.dmp");
        await using (var stream = new FileStream(dumpPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(FileChunkReader.MaxArtifactBytes + 1);
        }

        await using var factory = CreateFactory(("module-reader", "ceiling-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "ceiling-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "dump", ["dumpFilePath"] = dumpPath },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("InvalidArgument");
        envelope.Error.Message.Should().Contain("256 MiB");
    }

    [Fact]
    public async Task GetBytes_List_ReturnsArtifactsNewestFirst()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot.Path, "a.dmp"), new byte[64], CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(artifactRoot.Path, "sub"));
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot.Path, "sub", "b.nettrace"), new byte[16], CancellationToken.None);

        await using var factory = CreateFactory(("module-reader", "list-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "list-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "list" },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope!.Error.Should().BeNull();
        envelope.Data.GetProperty("count").GetInt32().Should().Be(2);
        envelope.Data.GetProperty("totalSizeBytes").GetInt64().Should().Be(80);
    }

    [Fact]
    public async Task GetBytes_Delete_RemovesArtifactWithScope()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        var path = Path.Combine(artifactRoot.Path, "trash.dmp");
        await File.WriteAllBytesAsync(path, new byte[8], CancellationToken.None);

        await using var factory = CreateFactory(("deleter", "del-token", new[] { "module-bytes-read", "delete-artifact" }));
        await using var client = await ConnectWithTokenAsync(factory, "del-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "delete", ["artifactPath"] = "trash.dmp" },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope!.Error.Should().BeNull();
        envelope.Data.GetProperty("deleted").GetProperty("relativePath").GetString().Should().Be("trash.dmp");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task GetBytes_Delete_MissingScope_IsRejected()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        var path = Path.Combine(artifactRoot.Path, "keep.dmp");
        await File.WriteAllBytesAsync(path, new byte[8], CancellationToken.None);

        await using var factory = CreateFactory(("reader", "noscope-token", new[] { "module-bytes-read" }));
        await using var client = await ConnectWithTokenAsync(factory, "noscope-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "delete", ["artifactPath"] = "keep.dmp" },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("Forbidden");
        envelope.Error.Message.Should().Contain("delete-artifact");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task GetBytes_Delete_RejectsPathTraversal()
    {
        using var artifactRoot = CreateArtifactRoot();
        using var env = EnvScope.Set(EnvironmentArtifactRootProvider.EnvironmentVariableName, artifactRoot.Path);
        await using var factory = CreateFactory(("deleter", "trav-token", new[] { "module-bytes-read", "delete-artifact" }));
        await using var client = await ConnectWithTokenAsync(factory, "trav-token");

        var result = await client.CallToolAsync(
            "get_bytes",
            new Dictionary<string, object?> { ["kind"] = "delete", ["artifactPath"] = "../escape.dmp" },
            cancellationToken: CancellationToken.None);

        var envelope = DeserializeEnvelope(result);
        envelope!.Error.Should().NotBeNull();
        envelope.Error!.Kind.Should().Be("InvalidArtifactPath");
    }

    public async Task InitializeAsync()
    {
        _sample = await LiveSampleProcess.StartPublishedAsync(
            "CoreClrSample",
            new LiveSampleOptions { DiagnosticTimeout = TimeSpan.FromSeconds(30) });
        await WaitForModuleVisibilityAsync(_sample.ProcessId, _sample.SampleDll, TimeSpan.FromSeconds(30));
    }

    public async Task DisposeAsync()
    {
        if (_sample is not null)
        {
            await _sample.DisposeAsync();
        }
    }

    private string GetSampleMvid()
    {
        var mvid = new MvidReader().TryRead(SampleDll);
        mvid.Should().NotBeNull();
        return mvid!.Value.ToString("D");
    }

    private static WebApplicationFactory<Program> CreateFactory(params (string Name, string Token, string[] Scopes)[] tokens)
    {
        Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", null);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            for (var i = 0; i < tokens.Length; i++)
            {
                b.UseSetting($"Auth:BearerTokens:{i}:Name", tokens[i].Name);
                b.UseSetting($"Auth:BearerTokens:{i}:Token", tokens[i].Token);
                for (var j = 0; j < tokens[i].Scopes.Length; j++)
                {
                    b.UseSetting($"Auth:BearerTokens:{i}:Scopes:{j}", tokens[i].Scopes[j]);
                }
            }
        });
    }

    private static async Task<McpClient> ConnectWithTokenAsync(WebApplicationFactory<Program> factory, string token)
    {
        var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            },
            httpClient,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    private static (string Summary, JsonElement Envelope) ParseForbidden(CallToolResult result)
    {
        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        var splitIndex = text.IndexOf('\n');
        splitIndex.Should().BeGreaterThan(0);
        var summary = text[..splitIndex];
        var json = text[(splitIndex + 1)..];
        var envelope = JsonDocument.Parse(json).RootElement.GetProperty("error");
        return (summary, envelope);
    }

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static DiagnosticResult<JsonElement>? DeserializeEnvelope(CallToolResult result)
    {
        string json;
        if (result.StructuredContent is { } structured)
        {
            json = structured.GetRawText();
        }
        else
        {
            var textBlock = result.Content.OfType<TextContentBlock>().FirstOrDefault();
            textBlock.Should().NotBeNull();
            json = textBlock!.Text;
        }

        return JsonSerializer.Deserialize<DiagnosticResult<JsonElement>>(json, DeserializeOptions);
    }

    private static async Task WaitForModuleVisibilityAsync(int processId, string sampleDll, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var mvid = new MvidReader().TryRead(sampleDll) ?? throw new InvalidOperationException("Sample DLL MVID not readable.");
        var source = new ClrMdModuleByteSource();
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await source.FetchAsync(processId, mvid, asset: "pe", offset: 0, maxBytes: 2);
                return;
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"CoreClrSample mvid {mvid:D} was not visible in pid {processId} within {timeout}.");
    }

    private static TestDirectory CreateArtifactRoot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-artifacts", nameof(ByteFetchToolsTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TestDirectory(path);
    }

    private sealed class TestDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // best effort
            }
        }
    }
}
