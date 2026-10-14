using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliStreamingExceptionCaptureTests
{
    [Theory]
    [InlineData("exceptions")]
    [InlineData("crash-guard")]
    public async Task Capture_UsesCoreCollectorAndPreservesBoundedEvidence(string kind)
    {
        var collector = new ExceptionCollector();
        var frame = await CaptureAsync(collector, new
        {
            type = "capture", requestId = "ex-1", kind, processId = Environment.ProcessId,
            durationSeconds = 6, maxRecent = 2, topN = 1,
        });
        frame.GetProperty("type").GetString().Should().Be("capture", frame.ToString());
        frame.GetProperty("requestId").GetString().Should().Be("ex-1");
        frame.GetProperty("kind").GetString().Should().Be(kind);
        collector.Calls.Should().Be(1);
        var result = frame.GetProperty("result");
        var data = result.GetProperty("data");
        data.GetProperty("totalExceptions").GetInt32().Should().Be(7);
        data.GetProperty("byType").GetArrayLength().Should().Be(1);
        data.GetProperty("byType")[0].GetProperty("count").GetInt32().Should().Be(5);
        data.GetProperty("recent").GetArrayLength().Should().Be(2);
        data.GetProperty("recent")[0].GetProperty("exceptionMessage").GetString().Should().Be("<script>sample</script>");
        result.GetProperty("notes").ToString().Should().Contain("5 event(s) omitted")
            .And.Contain("1 lower-ranked type(s) omitted").And.Contain("retained events only");
        if (kind == "crash-guard")
        {
            data.GetProperty("recent")[0].GetProperty("managedStack")[0].GetString().Should().Be("Sample.Throw()");
            data.GetProperty("processExited").GetBoolean().Should().BeTrue();
            data.GetProperty("unhandledExceptionObserved").GetBoolean().Should().BeFalse();
            result.GetProperty("notes").ToString().Should().Contain("collector-note");
        }
        else
            result.GetProperty("notes").ToString().Should().Contain("do not include stack frames");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    [InlineData("two")]
    [InlineData(false)]
    public async Task Capture_RejectsInvalidMaxRecentBeforeCollecting(object maxRecent)
    {
        var collector = new ExceptionCollector();
        var frame = await CaptureAsync(collector, new
        {
            type = "capture", requestId = "bad", kind = "exceptions",
            processId = Environment.ProcessId, maxRecent,
        });
        frame.GetProperty("code").GetString().Should().Be("invalid_capture");
        frame.GetProperty("message").GetString().Should().Contain("maxRecent");
        collector.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Capture_CollectorFailureIsNotASuccessShapedResult()
    {
        var frame = await CaptureAsync(new ExceptionCollector { Fail = true }, new
        {
            type = "capture", requestId = "failure", kind = "exceptions",
            processId = Environment.ProcessId, durationSeconds = 6, maxRecent = 2,
        });
        frame.GetProperty("type").GetString().Should().Be("error");
        frame.GetProperty("requestId").GetString().Should().Be("failure");
        frame.GetProperty("code").GetString().Should().Be("capture_failed");
        frame.GetProperty("message").GetString().Should().Contain("test collector failure");
    }

    private static async Task<JsonElement> CaptureAsync(ExceptionCollector collector, object request)
    {
        using var input = new StringReader("{\"type\":\"hello\",\"protocolVersion\":2}\n" + JsonSerializer.Serialize(request) + "\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await CliStreamingProtocol.RunAsync(["stream", "--protocol", "jsonl"],
            input, output, error,
            () => new HostBuilder().ConfigureServices(services => services
                .AddSingleton<IExceptionCollector>(collector)
                .AddSingleton<ICrashGuardCollector>(collector)
                .AddSingleton<IProcessContextResolver>(new Resolver())
                .AddSingleton<IDiagnosticHandleStore>(new MemoryDiagnosticHandleStore())).Build(),
            CancellationToken.None);
        exit.Should().Be(0, error.ToString());
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        using var document = JsonDocument.Parse(lines[1]);
        return document.RootElement.Clone();
    }

    private sealed class Resolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new(requestedProcessId!.Value, RuntimeFlavor.CoreClr, true, true, false), null));
    }

    private sealed class ExceptionCollector : IExceptionCollector, ICrashGuardCollector
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }

        public Task<ExceptionSnapshot> CollectAsync(int processId, TimeSpan duration, int maxRecent = 100, CancellationToken cancellationToken = default)
        {
            AssertArguments(duration, maxRecent);
            return Task.FromResult(new ExceptionSnapshot(processId, DateTimeOffset.UnixEpoch, duration, 7,
                [new("System.InvalidOperationException", 5), new("System.Exception", 2)],
                [new(DateTimeOffset.UnixEpoch, "System.InvalidOperationException", "<script>sample</script>", "0x0", 1),
                 new(DateTimeOffset.UnixEpoch.AddSeconds(1), "System.InvalidOperationException", "<script>sample</script>", "0x0", 1)])
            { RecentCap = maxRecent });
        }

        Task<CrashGuardSnapshot> ICrashGuardCollector.CollectAsync(int processId, TimeSpan duration, int maxRecent, CancellationToken cancellationToken)
        {
            AssertArguments(duration, maxRecent);
            return Task.FromResult(new CrashGuardSnapshot(processId, DateTimeOffset.UnixEpoch, duration, true, 0, false, 7,
                [new("System.InvalidOperationException", 5), new("System.Exception", 2)],
                [new(DateTimeOffset.UnixEpoch, "System.InvalidOperationException", "<script>sample</script>", "0x0", 1, "ExceptionStart", false, ["Sample.Throw()"]),
                 new(DateTimeOffset.UnixEpoch.AddSeconds(1), "System.InvalidOperationException", "<script>sample</script>", "0x0", 1, "ExceptionStart", false, [])],
                null, ["collector-note"]) { RecentCap = maxRecent });
        }

        private void AssertArguments(TimeSpan duration, int maxRecent)
        {
            Calls++;
            duration.Should().Be(TimeSpan.FromSeconds(6));
            maxRecent.Should().Be(2);
            if (Fail) throw new InvalidOperationException("test collector failure");
        }
    }
}
