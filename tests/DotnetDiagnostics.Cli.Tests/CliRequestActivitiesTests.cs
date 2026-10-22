using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Requests;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliRequestActivitiesTests
{
    private const string Trace = "1234567890abcdef1234567890abcdef";
    private const string Span = "1111111111111111";

    [Fact]
    public async Task StandaloneRequestsQueryKeepsItsExistingDispatcherInsteadOfJointSlot()
    {
        var handles = new MemoryDiagnosticHandleStore();
        var handle = handles.Register(42, "in-flight-requests", new object(), TimeSpan.FromMinutes(10));
        await using var client = new Client(new FakeCollector(false), handles);
        await client.HelloAsync();
        await client.SendAsync(new { type = "query", requestId = "standalone", handle = handle.Id, view = "requests" });
        var frame = await client.ReadAsync();
        // JSONL has no standalone requests query view today; preserve its ordinary dispatcher error.
        frame.GetProperty("code").GetString().Should().Be("unsupported_handle_kind");
        frame.GetProperty("message").GetString().Should().Contain("in-flight-requests");
    }

    [Fact]
    public async Task CapabilityAndCaptureQueryRoundtripPreserveExactRowAndReplaceOneSlot()
    {
        var fake = new FakeCollector(false);
        await using var client = new Client(fake);
        var hello = await client.HelloAsync();
        hello.GetProperty("protocolVersion").GetInt32().Should().Be(2);
        hello.GetProperty("requestActivities").GetProperty("version").GetInt32().Should().Be(1);
        await client.SendAsync(Capture("first"));
        (await client.ReadAsync()).GetProperty("type").GetString().Should().Be("capture-started");
        var frame = await client.ReadAsync();
        var result = frame.GetProperty("result");
        var handle = result.GetProperty("handle").GetString();
        var captureId = result.GetProperty("captureId").GetString();
        var rowId = result.GetProperty("requests").GetProperty("rows")[0].GetProperty("rowId").GetString();
        await client.SendAsync(new { type = "query", requestId = "related", handle, captureId, view = "related-activities", requestRowId = rowId, topN = 1 });
        var query = (await client.ReadAsync()).GetProperty("result");
        query.GetProperty("retainedActivities").GetInt32().Should().Be(200);
        query.GetProperty("totalRows").GetInt32().Should().Be(200);
        query.GetProperty("omittedRows").GetInt32().Should().Be(199);
        query.GetProperty("rows")[0].GetProperty("verified").GetBoolean().Should().BeTrue();
        await client.SendAsync(new { type = "query", requestId = "wrong-window", handle, captureId = "wrong", view = "requests" });
        (await client.ReadAsync()).GetProperty("code").GetString().Should().Be("invalid_query");
        await client.SendAsync(new { type = "query", requestId = "wrong-row", handle, captureId, view = "related-activities", requestRowId = "not-retained" });
        (await client.ReadAsync()).GetProperty("code").GetString().Should().Be("invalid_query");
        await client.SendAsync(new { type = "query", requestId = "wrong-target", handle, captureId, view = "requests", processId = 43 });
        (await client.ReadAsync()).GetProperty("code").GetString().Should().Be("invalid_query");
        await client.SendAsync(Capture("second"));
        await client.ReadAsync();
        var replacement = await client.ReadAsync();
        replacement.GetProperty("result").GetProperty("handle").GetString().Should().NotBe(handle);
        await client.SendAsync(new { type = "query", requestId = "stale", handle, captureId, view = "requests" });
        (await client.ReadAsync()).GetProperty("code").GetString().Should().Be("handle_unavailable");
        fake.Calls.Should().Be(2);
    }

    [Fact]
    public async Task CancelRemainsResponsiveAndDrainsOwnerWithExplicitPartialQuality()
    {
        var fake = new FakeCollector(true);
        await using var client = new Client(fake);
        await client.HelloAsync();
        await client.SendAsync(Capture("running"));
        await client.ReadAsync();
        await client.SendAsync(new { type = "cancel", requestId = "wrong" });
        (await client.ReadAsync()).GetProperty("code").GetString().Should().Be("capture_not_found");
        await client.SendAsync(new { type = "cancel", requestId = "running" });
        var capture = await client.ReadAsync();
        capture.GetProperty("result").GetProperty("requests").GetProperty("quality").GetProperty("completion").GetString().Should().Be("cancelled");
        fake.Drained.Should().BeTrue();
    }

    [Fact]
    public async Task DisconnectCancelsAndDrainsOutstandingCapture()
    {
        var fake = new FakeCollector(true);
        var client = new Client(fake);
        await client.HelloAsync();
        await client.SendAsync(Capture("disconnect"));
        await client.ReadAsync();
        await client.DisposeAsync();
        fake.Drained.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"sources":["*"]}""")]
    [InlineData("""{"sources":["A/Stop"]}""")]
    [InlineData("""{"sources":[]}""")]
    [InlineData("""{"sources":["Sample"],"durationSeconds":301}""")]
    [InlineData("""{"sources":["Sample"],"persist":true}""")]
    [InlineData("""{"sources":["Sample"],"includeTags":true}""")]
    [InlineData("""{"sources":["Sample"],"durationSeconds":"10"}""")]
    public async Task InvalidScopeIsRejectedBeforeCollectorInvocation(string fields)
    {
        var fake = new FakeCollector(false);
        await using var client = new Client(fake);
        await client.HelloAsync();
        await client.SendRawAsync("{\"type\":\"capture\",\"requestId\":\"invalid\",\"kind\":\"request-activities\",\"processId\":42," + fields[1..]);
        var error = await client.ReadAsync();
        error.GetProperty("code").GetString().Should().Be("invalid_capture");
        fake.Calls.Should().Be(0);
    }

    private static object Capture(string id) => new
    {
        type = "capture",
        requestId = id,
        kind = "request-activities",
        processId = 42,
        durationSeconds = 6,
        sources = new[] { "Sample" },
    };

    private sealed class FakeCollector(bool waitForCancel) : IRequestActivityCollector
    {
        public int Calls { get; private set; }
        public bool Drained { get; private set; }
        public async Task<RequestActivityCapture> CollectAsync(int processId, RequestActivityOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            var completion = "normal";
            if (waitForCancel)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { completion = "cancelled"; }
            }
            var start = DateTimeOffset.UtcNow;
            var state = new RequestActivityState();
            state.ObserveRequest(new(Trace + "/" + Span, true, new(Trace, Span, "/sample", "GET", start)));
            for (var i = 1; i <= 200; i++)
            {
                var span = i.ToString("x16", CultureInfo.InvariantCulture);
                state.ObserveActivity(new CapturedActivity("Sample", "Child", span, Span, Trace, span, Span,
                    start.AddMilliseconds(1), start.AddMilliseconds(2), TimeSpan.FromMilliseconds(1), new Dictionary<string, string>()));
            }
            Drained = true;
            return state.Close(Guid.NewGuid().ToString("N"), processId, start.AddHours(-1), start, options,
                start, start.AddSeconds(6), completion, 0, "known");
        }
    }

    private sealed class Client : IAsyncDisposable
    {
        private readonly Input _input = new();
        private readonly Output _output = new();
        private readonly StringWriter _errors = new(CultureInfo.InvariantCulture);
        private readonly Task<int> _run;
        public Client(IRequestActivityCollector collector, IDiagnosticHandleStore? handles = null)
        {
            _run = CliStreamingProtocol.RunAsync(["stream", "--protocol", "jsonl"], _input, _output, _errors,
                () => new HostBuilder().ConfigureServices(s =>
                {
                    s.AddSingleton(collector);
                    if (handles is not null) s.AddSingleton(handles);
                }).Build(), CancellationToken.None);
        }
        public async Task<JsonElement> HelloAsync()
        {
            await SendAsync(new { type = "hello", protocolVersion = 2 });
            return await ReadAsync();
        }
        public Task SendAsync(object frame) => SendRawAsync(JsonSerializer.Serialize(frame));
        public Task SendRawAsync(string frame) => _input.Lines.Writer.WriteAsync(frame + "\n").AsTask();
        public async Task<JsonElement> ReadAsync() => await _output.Frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public async ValueTask DisposeAsync()
        {
            _input.Lines.Writer.TryComplete();
            (await _run.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(0, _errors.ToString());
            _input.Dispose(); _output.Dispose(); _errors.Dispose();
        }
    }

    private sealed class Input : TextReader
    {
        public Channel<string> Lines { get; } = Channel.CreateBounded<string>(16);
        private string _line = "";
        private int _position;
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == _line.Length)
            {
                if (!await Lines.Reader.WaitToReadAsync(cancellationToken)) return 0;
                _line = await Lines.Reader.ReadAsync(cancellationToken);
                _position = 0;
            }
            var count = Math.Min(buffer.Length, _line.Length - _position);
            _line.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private sealed class Output : TextWriter
    {
        public Channel<JsonElement> Frames { get; } = Channel.CreateBounded<JsonElement>(32);
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            using var document = JsonDocument.Parse(buffer.ToString());
            return Frames.Writer.WriteAsync(document.RootElement.Clone(), cancellationToken).AsTask();
        }
    }
}
