using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(nameof(EnvSerial))]
public sealed class HistoricalComparisonHttpTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".validation", "historical-http", Guid.NewGuid().ToString("N")));
    private const string Token = "historical-http-test-not-production";
    private sealed record RootProvider(string Root) : IArtifactRootProvider;
    private static CaptureAccess Owner => new(PrincipalOwnershipKey.ForOpaqueEntry("Auth:BearerTokens:0"));

    private async Task<HistoricalCaptureReference> Capture(double value, CaptureAccess? owner = null)
    {
        var service = new DurableCaptureUseCases(new(new RootProvider(_root)), new MemoryDiagnosticHandleStore(), new());
        var result = await service.CaptureAsync("historical", "counters", owner ?? Owner, _ =>
            Task.FromResult(DiagnosticResult.Ok(new CounterSnapshot(999999, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(7),
                [new("Provider", "value", "value", value, CounterKind.Mean, "bytes")], [], []), "retained")));
        result.Error.Should().BeNull();
        return new(result.Capture!.CaptureId, result.Capture.Artifacts.Single().ArtifactId);
    }

    [Fact]
    public async Task ActualHttpClientComparesExplicitReferencesAcrossServerRestart()
    {
        var a = await Capture(4);
        var b = await Capture(10);
        for (var restart = 0; restart < 2; restart++)
        {
            await using var factory = Factory();
            await using var client = await Connect(factory);
            var response = await client.CallToolAsync("compare_to_baseline",
                new Dictionary<string, object?> { ["captureComparison"] = new { baseline = a, candidate = b } },
                cancellationToken: CancellationToken.None);
            response.IsError.Should().NotBeTrue(JsonSerializer.Serialize(response));
            var data = response.StructuredContent!.Value.GetProperty("data");
            data.GetProperty("schema").GetString().Should().Be(HistoricalComparisonResult.SchemaV1);
            data.GetProperty("metrics")[0].GetProperty("absoluteDelta").GetDecimal().Should().Be(6);
            data.GetProperty("left").GetProperty("reference").GetProperty("captureId").GetString().Should().Be(a.CaptureId);
            JsonSerializer.SerializeToUtf8Bytes(response).Length.Should().BeLessThanOrEqualTo(1024 * 1024);
        }
    }

    [Fact]
    public async Task DeniedSideLeaksNeitherSideIdentityOrMetrics()
    {
        var a = await Capture(1);
        var b = await Capture(2, new("other-owner"));
        await using var factory = Factory();
        await using var client = await Connect(factory);
        var response = await client.CallToolAsync("compare_to_baseline",
            new Dictionary<string, object?> { ["captureComparison"] = new { baseline = a, candidate = b } },
            cancellationToken: CancellationToken.None);
        response.IsError.Should().BeTrue();
        var wire = JsonSerializer.Serialize(response);
        wire.Should().NotContain(a.CaptureId).And.NotContain(b.CaptureId).And.NotContain("other-owner");
    }

    [Fact]
    public async Task ActualStdioClientComparesAfterHostRestartWithoutImportOrLiveTarget()
    {
        var owner = new CaptureAccess(StdioRootPrincipalAccessor.Instance.Current!.OwnershipKey);
        var a = await Capture(3, owner);
        var b = await Capture(8, owner);
        for (var restart = 0; restart < 2; restart++)
        {
            await using var host = await PortableCaptureClientTests.ClientHost.Start(_root, stdio: true, native: false);
            var response = await host.Client.CallToolAsync("compare_to_baseline",
                new Dictionary<string, object?> { ["captureComparison"] = new { baseline = a, candidate = b } },
                cancellationToken: CancellationToken.None);
            response.IsError.Should().NotBeTrue(JsonSerializer.Serialize(response));
            response.StructuredContent!.Value.GetProperty("data").GetProperty("metrics")[0]
                .GetProperty("absoluteDelta").GetDecimal().Should().Be(5);
        }
    }

    [Theory]
    [InlineData("cpu-sample")]
    [InlineData("heap-snapshot")]
    public async Task UnauthorizedSiblingInEitherManifestPreventsBothSidesDisclosure(string siblingKind)
    {
        var a = await Capture(1);
        var store = new SqliteCaptureStore(new RootProvider(_root));
        await using var writer = await store.CreateAsync(new("private source"), Owner);
        var id = writer.AddArtifact("counters", "selected");
        writer.AddArtifact(siblingKind, "private sibling");
        writer.SetSnapshot(id, CaptureArtifactCodec.FormatVersion, CaptureArtifactCodec.Encode("counters",
            new CounterSnapshot(1, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), [], [], []), 1024 * 1024));
        var b = new HistoricalCaptureReference((await writer.CompleteAsync()).CaptureId, id);
        await using var factory = Factory();
        await using var client = await Connect(factory);
        foreach (var pair in new[] { (a, b), (b, a) })
        {
            var response = await client.CallToolAsync("compare_to_baseline",
                new Dictionary<string, object?> { ["captureComparison"] = new { baseline = pair.Item1, candidate = pair.Item2 } },
                cancellationToken: CancellationToken.None);
            response.IsError.Should().BeTrue();
            JsonSerializer.Serialize(response).Should().NotContain(a.CaptureId).And.NotContain(b.CaptureId)
                .And.NotContain("private sibling").And.NotContain("private source");
        }
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public async Task CurrentScopeOrOwnerRevocationSuppressesCompleteResult(int revokeAt, bool changeOwner)
    {
        var a = await Capture(1);
        var b = await Capture(2);
        var store = new SqliteCaptureStore(new RootProvider(_root));
        var tools = new DurableCaptureTools(store, new(store, new MemoryDiagnosticHandleStore(), new()));
        var allowed = TestPrincipalAccessors.WithIdentity("owner", Owner.OwnerId, "investigation-export", "read-counters").Current!;
        var revoked = TestPrincipalAccessors.WithIdentity("owner", changeOwner ? "different-owner" : Owner.OwnerId,
            changeOwner ? ["investigation-export", "read-counters"] : ["investigation-export"]).Current!;
        var accessor = new ChangingPrincipal(allowed, revoked, revokeAt);
        var response = await tools.CompareAsync(accessor,
            JsonSerializer.SerializeToElement(new HistoricalComparisonRequest(a, b), HistoricalComparisonJsonContext.Default.HistoricalComparisonRequest),
            CancellationToken.None);
        response.Error!.Kind.Should().Be("InsufficientScope");
        response.Data.Should().BeNull();
        JsonSerializer.Serialize(response).Should().NotContain(a.CaptureId).And.NotContain(b.CaptureId);
        accessor.Reads.Should().Be(revokeAt);
    }

    [Fact]
    public async Task RevokedAllOwnersAuthorityCannotReuseAlreadyAcquiredForeignLeases()
    {
        var a = await Capture(1, new("foreign-a"));
        var b = await Capture(2, new("foreign-b"));
        var store = new SqliteCaptureStore(new RootProvider(_root));
        var tools = new DurableCaptureTools(store, new(store, new MemoryDiagnosticHandleStore(), new()));
        var root = TestPrincipalAccessors.WithIdentity("owner", Owner.OwnerId, "root").Current!;
        var reader = TestPrincipalAccessors.WithIdentity("owner", Owner.OwnerId, "investigation-export", "read-counters").Current!;
        var response = await tools.CompareAsync(new ChangingPrincipal(root, reader, 6),
            JsonSerializer.SerializeToElement(new HistoricalComparisonRequest(a, b), HistoricalComparisonJsonContext.Default.HistoricalComparisonRequest),
            CancellationToken.None);
        response.Error!.Kind.Should().Be("InsufficientScope");
        response.Data.Should().BeNull();
        JsonSerializer.Serialize(response).Should().NotContain("foreign-a").And.NotContain("foreign-b")
            .And.NotContain(a.CaptureId).And.NotContain(b.CaptureId);
    }

    [Theory]
    [InlineData("mode", "trend")]
    [InlineData("depth", "compact")]
    [InlineData("baselineSummaryJson", "{}")]
    public async Task HistoricalAndLegacyArgumentsCannotMix(string name, string value)
    {
        await using var factory = Factory();
        await using var client = await Connect(factory);
        var reference = new HistoricalCaptureReference(new string('a', 32), new string('b', 32));
        var response = await client.CallToolAsync("compare_to_baseline", new Dictionary<string, object?>
        {
            ["captureComparison"] = new { baseline = reference, candidate = reference }, [name] = value,
        }, cancellationToken: CancellationToken.None);
        response.IsError.Should().BeTrue();
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task ActualHttpRejectsCoreSizedResultThatExceedsCombinedWireEnvelope()
    {
        var store = new SqliteCaptureStore(new RootProvider(_root));
        await using var writer = await store.CreateAsync(new("bounded result"), Owner);
        var artifact = writer.AddArtifact("counters", "retained");
        writer.SetSnapshot(artifact, CaptureArtifactCodec.FormatVersion, CaptureArtifactCodec.Encode("counters",
            new CounterSnapshot(1, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
                Enumerable.Range(0, 500).Select(i => new CounterValue("Provider",
                    i + new string('x', 600), "", 1, CounterKind.Mean, "bytes")).ToArray(), [], []), 1024 * 1024));
        var reference = new HistoricalCaptureReference((await writer.CompleteAsync()).CaptureId, artifact);
        var core = await new DurableCaptureUseCases(store, new MemoryDiagnosticHandleStore(), new())
            .CompareHistoricalAsync(new(reference, reference), Owner, static (_, _, _, _) => ValueTask.CompletedTask);
        core.Metrics.Should().HaveCount(500);
        await using var factory = Factory();
        await using var client = await Connect(factory);
        var response = await client.CallToolAsync("compare_to_baseline", new Dictionary<string, object?>
        {
            ["captureComparison"] = new { baseline = reference, candidate = reference },
        }, cancellationToken: CancellationToken.None);
        response.IsError.Should().BeTrue();
        response.StructuredContent!.Value.GetProperty("error").GetProperty("detail").GetString().Should().Be("CapacityExceeded");
        var wire = JsonSerializer.Serialize(response);
        wire.Should().NotContain(reference.CaptureId);
        System.Text.Encoding.UTF8.GetByteCount(wire).Should().BeLessThan(1024 * 1024);
    }

    [Theory]
    [InlineData("from")]
    [InlineData("host")]
    [InlineData("path")]
    public async Task UnsupportedReferenceFieldsAreRejected(string field)
    {
        await using var factory = Factory();
        await using var client = await Connect(factory);
        var reference = new Dictionary<string, object?> { ["captureId"] = new string('a', 32),
            ["artifactId"] = new string('b', 32), [field] = "unsupported" };
        var response = await client.CallToolAsync("compare_to_baseline",
            new Dictionary<string, object?> { ["captureComparison"] = new { baseline = reference, candidate = reference } },
            cancellationToken: CancellationToken.None);
        response.IsError.Should().BeTrue();
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Auth:BearerTokens:0:Name", "historical");
        builder.UseSetting("Auth:BearerTokens:0:Token", Token);
        builder.UseSetting("Auth:BearerTokens:0:Scopes:0", "investigation-export");
        builder.UseSetting("Auth:BearerTokens:0:Scopes:1", "read-counters");
        builder.UseSetting("Orchestrator:Enabled", "false");
        builder.UseSetting("AzureDiscovery:Enabled", "false");
        builder.ConfigureServices(services => services.AddSingleton<IArtifactRootProvider>(new RootProvider(_root)));
    });

    private static async Task<McpClient> Connect(WebApplicationFactory<Program> factory)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: true), cancellationToken: CancellationToken.None);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private sealed class ChangingPrincipal(BearerPrincipal allowed, BearerPrincipal revoked, int revokeAt) : IPrincipalAccessor
    {
        public int Reads { get; private set; }
        public BearerPrincipal? Current => ++Reads >= revokeAt ? revoked : allowed;
    }
}
