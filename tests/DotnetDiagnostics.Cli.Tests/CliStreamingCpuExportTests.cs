using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Hosting;
using DotnetDiagnostics.Core.Security;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli.Tests;

[Collection(nameof(CliStreamingProtocolTests))]
public sealed class CliStreamingCpuExportTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "cpu-export-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryDiagnosticHandleStore _store = new();
    private readonly FakeSampler _sampler = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("\"yes\"", "cpu")]
    [InlineData("1", "cpu")]
    [InlineData("null", "cpu")]
    [InlineData("true", "exceptions")]
    public async Task Capture_InvalidTraceFlag_RejectsBeforeCollection(string value, string kind)
    {
        var frames = await RunAsync(
            $$"""{"type":"capture","requestId":"c","kind":"{{kind}}","processId":{{Environment.ProcessId}},"exportTrace":{{value}}}""");
        frames[0].GetProperty("code").GetString().Should().Be("invalid_capture");
        _sampler.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_RegistersHandle_AndSummaryUsesTheSameHost(bool trace)
    {
        var frames = await RunAsync(
            $$"""{"type":"capture","requestId":"c","kind":"cpu","processId":{{Environment.ProcessId}},"exportTrace":{{(trace ? "true" : "false")}}}""",
            capture => Request(capture.GetProperty("exportHandle").GetString()!, "summary"));
        frames[0].GetProperty("result").GetProperty("totalSamples").GetInt64().Should().Be(10);
        _sampler.LastExportTrace.Should().Be(trace);
        var lookup = _store.TryGetWithKind(frames[0].GetProperty("exportHandle").GetString()!);
        lookup.Should().NotBeNull();
        lookup!.Value.Kind.Should().Be("cpu-sample");
        frames[1].GetProperty("type").GetString().Should().Be("export", frames[1].ToString());
        frames[1].GetProperty("result").GetProperty("Findings").GetProperty("TotalSamples").GetInt64().Should().Be(10);
    }

    [Fact]
    public async Task Capture_TraceFlagDefaultsToFalse()
    {
        await RunAsync($$"""{"type":"capture","requestId":"c","kind":"cpu","processId":{{Environment.ProcessId}}}""");
        _sampler.LastExportTrace.Should().BeFalse();
    }

    [Theory]
    [InlineData("call-tree")]
    [InlineData("top-methods")]
    [InlineData("by-module")]
    [InlineData("by-namespace")]
    [InlineData("hot-path")]
    [InlineData("caller-callee")]
    public async Task AllocationQueries_ConfirmEstimatedByteUnits_WithoutRecapture(string view)
    {
        var tickTrace = Artifact(null);
        var byteTrace = tickTrace with
        {
            TotalSamples = 1000,
            Root = new CallTreeNode(new SampledFrame("App.dll", "Main"), 1000, 1000, []),
        };
        var artifact = new AllocationSampleArtifact(
            new(Environment.ProcessId, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), 10, 1000, [], []), tickTrace)
        { EstimatedBytesTrace = byteTrace };
        var handle = _store.Register(Environment.ProcessId, "allocation-sample", artifact, TimeSpan.FromMinutes(1), evictWhenProcessExits: false).Id;
        var frames = await RunAsync(JsonSerializer.Serialize(new
        {
            type = "query", requestId = "a", handle, view, rootMethodFilter = view == "caller-callee" ? "Main" : null,
        }));
        var result = frames[0].GetProperty("result");
        result.GetProperty("weightUnit").GetString().Should().Be("allocation-tick-estimated-bytes");
        result.GetProperty("summary").GetString().Should().Contain("not object counts");
        if (view == "call-tree")
        {
            result.GetProperty("data").GetProperty("totalSamples").GetInt64().Should().Be(1000);
            result.GetProperty("data").GetProperty("root").GetProperty("inclusiveSamples").GetInt64().Should().Be(1000);
        }
        _sampler.Calls.Should().Be(0);
        artifact.TraceArtifact.TotalSamples.Should().Be(10);
    }

    [Theory]
    [InlineData("triage", true)]
    [InlineData("inlining", true)]
    [InlineData("call-tree", false)]
    public async Task AllocationQueries_RejectCpuInterpretationsAndHistoricalTickOnlyArtifacts(string view, bool byteWeighted)
    {
        var trace = Artifact(null);
        var artifact = new AllocationSampleArtifact(
            new(Environment.ProcessId, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), 10, 1000, [], []), trace)
        { EstimatedBytesTrace = byteWeighted ? trace : null };
        var handle = _store.Register(Environment.ProcessId, "allocation-sample", artifact, TimeSpan.FromMinutes(1), evictWhenProcessExits: false).Id;
        var frames = await RunAsync(JsonSerializer.Serialize(new { type = "query", requestId = "a", handle, view }));
        frames[0].GetProperty("code").GetString().Should().Be("unsupported_query_view");
        _sampler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Trace_MaterializesOnlyStoredArtifactPath_AfterAcknowledgement()
    {
        Directory.CreateDirectory(_root);
        var bytes = "private trace bytes"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_root, "cpu.nettrace"), bytes);
        var handle = RegisterCpu("cpu.nettrace");
        var output = Path.Combine(_root, "exported.nettrace");
        var frames = await RunAsync(Request(handle, "trace", output, "critical"));
        frames[0].GetProperty("type").GetString().Should().Be("export", frames[0].ToString());
        File.ReadAllBytes(output).Should().Equal(bytes);
    }

    [Theory]
    [InlineData(null, "critical")]
    [InlineData("relative.nettrace", "critical")]
    [InlineData("/abs/../escape.nettrace", "critical")]
    [InlineData("ABS", null)]
    [InlineData("ABS", "moderate")]
    [InlineData("ABS", "high")]
    [InlineData(@"\\server\share\trace.nettrace", "critical")]
    public async Task Trace_InvalidDestinationOrAcknowledgement_Rejects(string? path, string? acknowledgement)
    {
        var output = path == "ABS" ? Path.Combine(_root, "out.nettrace") : path;
        var frames = await RunAsync(Request(RegisterCpu("cpu.nettrace"), "trace", output, acknowledgement));
        frames[0].GetProperty("code").GetString().Should().Be("export_failed");
        File.Exists(Path.Combine(_root, "out.nettrace")).Should().BeFalse();
    }

    [Fact]
    public async Task Export_UnknownWrongKindOrMissingTrace_FailsWithRequestId()
    {
        var wrong = _store.Register(1, "heap-snapshot", new object(), TimeSpan.FromMinutes(1), evictWhenProcessExits: false);
        foreach (var request in new[]
        {
            Request("missing", "summary"), Request(wrong.Id, "summary"),
            Request(RegisterCpu(null), "trace", Path.Combine(_root, "out.nettrace"), "critical"),
            Request(RegisterCpu(null), "pdf"),
        })
        {
            var frames = await RunAsync(request);
            frames[0].GetProperty("code").GetString().Should().Be("export_failed");
            frames[0].GetProperty("requestId").GetString().Should().Be("e");
        }
    }

    private string RegisterCpu(string? trace)
        => _store.Register(Environment.ProcessId, "cpu-sample", Artifact(trace), TimeSpan.FromMinutes(20), evictWhenProcessExits: false).Id;

    private static CpuSampleTraceArtifact Artifact(string? trace)
        => new(Environment.ProcessId, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), 10,
            new CallTreeNode(new SampledFrame("App.dll", "Main"), 10, 10, []), TracePath: trace);

    private static string Request(string handle, string format, string? output = null, string? acknowledgement = null)
        => JsonSerializer.Serialize(new { type = "export", requestId = "e", handle, format, outputPath = output, acknowledgeRisk = acknowledgement });

    private async Task<List<JsonElement>> RunAsync(string first, Func<JsonElement, string>? next = null)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var lines = new List<Func<string>>
        {
            () => """{"type":"hello","protocolVersion":2}""", () => first,
        };
        if (next is not null) lines.Add(() =>
        {
            using var document = JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
            return next(document.RootElement);
        });
        var exit = await CliStreamingProtocol.RunAsync(["stream", "--protocol", "jsonl"],
            new ScriptedReader(lines), output, error,
            () => new HostBuilder().ConfigureServices(services =>
            {
                services.AddDiagnosticCoreServices(new SecurityOptions());
                services.AddSingleton<IDiagnosticHandleStore>(_store);
                services.AddSingleton<ICpuSampler>(_sampler);
                services.AddSingleton<IArtifactRootProvider>(new FixedArtifactRootProvider(_root));
            }).Build(), CancellationToken.None);
        exit.Should().Be(0, error.ToString());
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToList();
    }

    private sealed class ScriptedReader(IReadOnlyList<Func<string>> lines) : TextReader
    {
        private int _next;
        private string? _currentLine;
        private int _position;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty || (_currentLine is null && _next >= lines.Count))
            {
                return ValueTask.FromResult(0);
            }

            _currentLine ??= lines[_next++]() + "\n";
            var count = Math.Min(buffer.Length, _currentLine.Length - _position);
            _currentLine.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            if (_position == _currentLine.Length)
            {
                _currentLine = null;
                _position = 0;
            }

            return ValueTask.FromResult(count);
        }
    }

    private sealed class FakeSampler : ICpuSampler
    {
        public int Calls { get; private set; }
        public bool LastExportTrace { get; private set; }
        public Task<CpuSampleResult> SampleAsync(int processId, TimeSpan duration, int topN = 25,
            SourceResolutionOptions? sourceResolution = null, MethodInstantiationResolutionOptions? methodInstantiationResolution = null,
            NativeAotSymbolResolutionOptions? nativeAotSymbols = null, bool exportTrace = false,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastExportTrace = exportTrace;
            return Task.FromResult(new CpuSampleResult(
                new CpuSample(processId, DateTimeOffset.UtcNow, duration, 10, []), Artifact(exportTrace ? "cpu.nettrace" : null)));
        }
    }
}
