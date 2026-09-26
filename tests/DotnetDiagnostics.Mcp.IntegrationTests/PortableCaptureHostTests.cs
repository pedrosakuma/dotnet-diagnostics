using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class PortableCaptureHostTests : IAsyncLifetime
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".validation", "portable-host", Guid.NewGuid().ToString("N")));
    private readonly TestClock _clock = new();
    private readonly StdioRootPrincipalAccessor _owner = Local();
    private PortableCaptureTools _tools = null!;
    private SqliteCaptureStore _store = null!;

    public Task InitializeAsync()
    {
        _store = new(new TestRoot(_root));
        _tools = new(_store, new(Worker: null), _clock, new HttpContextAccessor());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _tools.StopAsync(CancellationToken.None);
        _tools.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Export_DeliversActualZipBytesWithRepeatableChunksAndBoundedResponses()
    {
        var capture = await Capture();
        var operation = Guid.NewGuid().ToString("N");
        var start = await Call("export-start", new { operationId = operation, requestedUtc = _clock.Now,
            entries = new[] { new { captureId = capture.CaptureId, label = "client / label" } } });
        var id = Data(start).GetProperty("transferId").GetString()!;
        var ready = await Ready(id);
        var length = ready.GetProperty("archiveBytes").GetInt64();
        var expected = ready.GetProperty("archiveSha256").GetString();
        using var bytes = new MemoryStream();
        for (long offset = 0; offset < length; offset += PortableCaptureTools.ChunkBytes)
        {
            var response = await Call("download-chunk", new { transferId = id, offset, count = PortableCaptureTools.ChunkBytes });
            response.Error.Should().BeNull();
            var data = Data(response);
            var chunk = Convert.FromBase64String(data.GetProperty("base64").GetString()!);
            data.GetProperty("count").GetInt32().Should().Be(chunk.Length);
            Hash(chunk).Should().Be(data.GetProperty("sha256").GetString());
            await bytes.WriteAsync(chunk);
            var replay = await Call("download-chunk", new { transferId = id, offset, count = PortableCaptureTools.ChunkBytes });
            Data(replay).GetRawText().Should().Be(data.GetRawText());
            JsonSerializer.SerializeToUtf8Bytes(response).Length.Should().BeLessThan(64 * 1024);
        }
        bytes.Length.Should().Be(length);
        Hash(bytes.ToArray()).Should().Be(expected);
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read, leaveOpen: true);
        archive.Entries.Should().HaveCount(5);
        archive.GetEntry("bundle.json").Should().NotBeNull();
        var eof = Data(await Call("download-chunk", new { transferId = id, offset = length, count = PortableCaptureTools.ChunkBytes }));
        eof.GetProperty("count").GetInt32().Should().Be(0);
        eof.GetProperty("eof").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task OperationRetryIsStable_ConflictingSelectionRejected_AndCancellationReleasesStoreSlot()
    {
        var capture = await Capture();
        var key = Guid.NewGuid().ToString("N");
        var input = new { operationId = key, requestedUtc = _clock.Now, entries = new[] { new { captureId = capture.CaptureId, label = "same" } } };
        var id = Data(await Call("export-start", input)).GetProperty("transferId").GetString()!;
        await Ready(id);
        Data(await Call("export-start", input)).GetProperty("transferId").GetString().Should().Be(id);
        (await Call("export-start", new { operationId = key, requestedUtc = _clock.Now,
            entries = new[] { new { captureId = capture.CaptureId, label = "different" } } })).Error!.Detail.Should().Be("InvalidInput");
        var cancelled = Data(await Call("transfer-cancel", new { transferId = id }));
        cancelled.GetProperty("state").GetString().Should().Be("Cancelled");
        await Task.Delay(30);
        Directory.GetFiles(Path.Combine(_root, "captures", ".portable"), "bundle.ddcapture", SearchOption.AllDirectories)
            .Should().BeEmpty();
        (await Call("download-chunk", new { transferId = id, offset = 0, count = PortableCaptureTools.ChunkBytes }))
            .Error!.Detail.Should().Be("InvalidInput");
    }

    [Fact]
    public async Task IdleRetriesDoNotExtendLease_ExpiredTransferCannotYieldBytes()
    {
        var capture = await Capture();
        var id = Data(await Start(capture)).GetProperty("transferId").GetString()!;
        await Ready(id);
        await Call("download-chunk", new { transferId = id, offset = 0, count = PortableCaptureTools.ChunkBytes });
        _clock.Now = _clock.Now.AddMinutes(4);
        await Call("download-chunk", new { transferId = id, offset = 0, count = PortableCaptureTools.ChunkBytes });
        _clock.Now = _clock.Now.AddMinutes(2);
        Data(await Call("transfer-status", new { transferId = id })).GetProperty("state").GetString().Should().Be("Expired");
        (await Call("download-chunk", new { transferId = id, offset = 0, count = PortableCaptureTools.ChunkBytes }))
            .Error.Should().NotBeNull();
    }

    [Fact]
    public async Task FirstReadOfEarlierAlignedChunkCountsAsProgressEvenAfterLaterChunkWasRead()
    {
        var capture = await Capture();
        var id = Data(await Start(capture)).GetProperty("transferId").GetString()!;
        var ready = await Ready(id);
        ready.GetProperty("archiveBytes").GetInt64().Should().BeGreaterThan(PortableCaptureTools.ChunkBytes);
        await Call("download-chunk", new { transferId = id, offset = PortableCaptureTools.ChunkBytes, count = PortableCaptureTools.ChunkBytes });
        _clock.Now = _clock.Now.AddMinutes(4);
        await Call("download-chunk", new { transferId = id, offset = 0, count = PortableCaptureTools.ChunkBytes });
        _clock.Now = _clock.Now.AddMinutes(2);
        Data(await Call("transfer-status", new { transferId = id })).GetProperty("state").GetString().Should().Be("Ready");
    }

    [Fact]
    public async Task SensitiveModifierRevocationStopsHeldExportReads()
    {
        var owner = Local("eventsource-any");
        var capture = await Capture("event-source");
        var id = Data(await Start(capture, owner)).GetProperty("transferId").GetString()!;
        await Ready(id, owner);
        var denied = await Call("download-chunk", new { transferId = id, offset = 0, count = PortableCaptureTools.ChunkBytes });
        denied.Error!.Kind.Should().Be("InsufficientScope");
        Data(denied).TryGetProperty("base64", out _).Should().BeFalse();
    }

    [Fact]
    public async Task MissingWorkerDoesNotAcceptUploadOrCreateStore()
    {
        var result = await Call("import-start", new { operationId = Guid.NewGuid().ToString("N"),
            requestedUtc = _clock.Now, archiveBytes = 1, archiveSha256 = Hash([1]) });
        result.Error!.Detail.Should().Be("UnsupportedFormat");
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task StoreRateLimitIncludesFailedCallsAndRejectsWithoutQueueing()
    {
        for (var i = 0; i < 100; i++)
            (await Call("transfer-status", new { transferId = new string('a', 32) })).Error!.Detail.Should().Be("Forbidden");
        var excess = await Call("transfer-status", new { transferId = new string('a', 32) });
        excess.Error!.Detail.Should().Be("Busy");
        Data(excess).GetProperty("retryAfterMilliseconds").GetInt32().Should().Be(1000);
        _clock.Now = _clock.Now.AddSeconds(1);
        (await Call("transfer-status", new { transferId = new string('a', 32) })).Error!.Detail.Should().Be("Forbidden");
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    [InlineData(-1440)]
    public async Task InvalidOperationWindowNeverProducesArchive(int minutes)
    {
        var capture = await Capture();
        var response = await Call("export-start", new { operationId = Guid.NewGuid().ToString("N"),
            requestedUtc = _clock.Now.AddMinutes(minutes), entries = new[] { new { captureId = capture.CaptureId } } });
        if (minutes == -6)
        {
            var id = Data(response).GetProperty("transferId").GetString()!;
            for (var i = 0; i < 50 && Data(await Call("transfer-status", new { transferId = id })).GetProperty("state").GetString() == "Preparing"; i++)
                await Task.Delay(20);
            Data(await Call("transfer-status", new { transferId = id })).GetProperty("state").GetString().Should().Be("Failed");
        }
        else response.Error!.Detail.Should().Be("InvalidInput");
        Directory.GetFiles(_root, "bundle.ddcapture", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Theory]
    [InlineData("DOTNET_DIAGNOSTICS_IMPORT_WORKER")]
    [InlineData("DOTNET_DIAGNOSTICS_SQLITE_LIBRARY")]
    public void PartialWorkerConfigurationFailsBeforeStartup(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = "/operator/explicit/path" }).Build();
        var configure = () => PortableTransferOptions.FromConfiguration(configuration);
        configure.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(-1, 24576)]
    [InlineData(1, 24576)]
    [InlineData(0, 24577)]
    public async Task DownloadRejectsUnalignedAndOversizedRequests(long offset, int count)
    {
        var capture = await Capture();
        var id = Data(await Start(capture)).GetProperty("transferId").GetString()!;
        await Ready(id);
        (await Call("download-chunk", new { transferId = id, offset, count })).Error!.Detail.Should().Be("InvalidInput");
    }

    private async Task<CaptureInfo> Capture(string kind = "counters")
    {
        await using var writer = await _store.CreateAsync(new("transport source"), new(_owner.Current!.OwnershipKey));
        var artifact = writer.AddArtifact(kind, "records");
        writer.TryAppend(artifact, new(Name: "retained actual row")).Should().BeTrue();
        return await writer.CompleteAsync();
    }

    private Task<DiagnosticResult<object>> Start(CaptureInfo capture, IPrincipalAccessor? owner = null) =>
        Call("export-start", new { operationId = Guid.NewGuid().ToString("N"), requestedUtc = _clock.Now,
            entries = new[] { new { captureId = capture.CaptureId } } }, owner);

    private async Task<JsonElement> Ready(string id, IPrincipalAccessor? owner = null)
    {
        for (var i = 0; i < 100; i++)
        {
            var response = await Call("transfer-status", new { transferId = id }, owner);
            if (response.Error?.Detail == "Busy") { await Task.Delay(20); continue; }
            response.Error.Should().BeNull();
            var data = Data(response);
            var state = data.GetProperty("state").GetString();
            state.Should().NotBe("Failed", data.GetRawText());
            if (state == "Ready") return data;
            await Task.Delay(20);
        }
        throw new TimeoutException("Export did not become Ready.");
    }

    private Task<DiagnosticResult<object>> Call(string action, object arguments, IPrincipalAccessor? owner = null)
        => _tools.InvokeAsync(owner ?? _owner, null, action, JsonSerializer.SerializeToElement(arguments), CancellationToken.None);
    private static JsonElement Data(DiagnosticResult<object> result) => JsonSerializer.SerializeToElement(result.Data,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static StdioRootPrincipalAccessor Local(string? modifier = null)
    {
        var values = new Dictionary<string, string?> { ["Stdio:CaptureBytes"] = "true" };
        if (modifier is not null) values["Stdio:CaptureModifiers:0"] = modifier;
        return StdioRootPrincipalAccessor.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }
    private sealed record TestRoot(string Root) : IArtifactRootProvider;
    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
