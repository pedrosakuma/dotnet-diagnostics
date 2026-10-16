using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliStreamingCaptureViewsTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(6);

    public static TheoryData<string, object> Snapshots => new()
    {
        { "allocation", Result(new AllocationSample(42, Start, Duration, 2, 200,
            [new("Sample", 150, 1, HeapKind.Small), new("Other", 50, 1, HeapKind.Small)], [])) },
        { "off_cpu", Result(new OffCpuSnapshot(42, Start, Duration, 1000, 1,
            [new("Wait", 1000, 1, "S", [new("Module", "Wait")])], 2, "mixed", Notes: ["perf-note"])) },
        { "contention", Result(new ContentionSnapshot(42, Start, Duration, 2, 1, Duration, Duration, Duration, Duration,
            [new(Start, Start + Duration, Duration, 1, null, 0, 0, "Sample.Lock", "Module")], ["cap-note"])) },
        { "threadpool", Result(new ThreadPoolEventSnapshot(42, Start, Duration, [], [],
            [new(Start, "Starvation", null, 5, null, ThreadPoolEvidence.RuntimeObserved)], [], null, 1, 1, ["pool-note"])) },
        { "gc", Result(new GcSummary(42, Start, Duration, 1, Duration, Duration,
            [new(0, 1)], [new(Start, 0, "AllocSmall", "NonConcurrentGC", Duration)])) },
        { "requests", Result(new InFlightRequestSnapshot(42, Start, Duration, 1, 0, 1, 1, 1000, 2000,
            [new("trace", null, "GET", "/<script>", Start, 2000, true)], ["request-note"])) },
        { "kestrel", Result(Kestrel()) },
        { "db", Result(new DbSnapshot(42, Start, Duration, 1,
            [new("hash", "SELECT sample", "SECRET-CONNECTION", [], 1, 10, 10, 10, Start, Start)],
            [], [], ["db-note"])) },
        { "networking", Result(new NetworkingSnapshot(42, Start, Duration,
            1, 1, 0, 0, 0, 0, Duration, Duration, Duration, Duration, Duration, Duration,
            0, 0, 0, Duration, Duration, Duration, 0, 0, 0, Duration, Duration, Duration, 0, 0, 0,
            [], [new("sample", "/request", 1, Duration, Duration, Duration)], [], ["network-note"])) },
        { "gc-activities", Result(new GcActivitiesCapture(42, null, "partial",
            new("unavailable", Start, Start + Duration, null, null, null, null, "GC unavailable"),
            new("unavailable", Start, Start + Duration, null, null, null, null, "Activities unavailable"),
            null, null, null, null, "Overlay unavailable", ["partial-note"])) },
    };

    private static CliCommandResult Result<T>(T data) => new(false, false,
        DiagnosticResult.Ok(data, "Observed sample"), string.Empty);

    private static KestrelSnapshot Kestrel() => new(42, Start, Duration,
        1, 1, 0, 1, 1, 0, 0, 0, 0, 0, Duration, Duration, Duration,
        TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, Duration, Duration, Duration,
        [], [], [new("GET", "/sample", "2", 1, Duration, Duration, Duration)], [], null, ["kestrel-note"]);

    [Fact]
    public void AllocationView_ExposesTheExactCaptureHandleAndResourceLimitCaveats()
    {
        var sample = new AllocationSample(42, Start, Duration, 2, 200, [], []);
        var envelope = DiagnosticResult.OkWithHandle(sample, "Sampled allocations", "allocation-one",
            Start + TimeSpan.FromMinutes(20));
        var result = new CliCommandResult(false, false, envelope, string.Empty);
        var view = CliStreamingCaptureViews.Project(result, Options("allocation"));
        view.Handle.Should().Be("allocation-one");
        view.Notes.Should().Contain(note => note.Contains("not a hard process-RSS budget", StringComparison.Ordinal));
        view.Notes.Should().Contain(note => note.Contains("64 MiB", StringComparison.Ordinal));
    }

    [Fact]
    public void KestrelUnpairedStopsDoNotEstablishZeroLatency()
    {
        var snapshot = Kestrel() with { RequestsStarted = 0, RequestsStopped = 1, ByOperation = [], RequestP95 = TimeSpan.Zero };
        var view = CliStreamingCaptureViews.Project(Result(snapshot), Options("kestrel"));
        view.Metrics.Single(m => m.Label == "Request P95 (ms)").Value.Should().BeNull();
        view.Notes.Should().Contain(n => n.Contains("stop events alone", StringComparison.Ordinal));
        var pairedZero = Kestrel() with { RequestP95 = TimeSpan.Zero };
        CliStreamingCaptureViews.Project(Result(pairedZero), Options("kestrel"))
            .Metrics.Single(m => m.Label == "Request P95 (ms)").Value.Should().Be(0.0);
    }

    [Theory]
    [MemberData(nameof(Snapshots))]
    public void EveryKindProducesStructuredBoundedView(string kind, object envelope)
    {
        var view = CliStreamingCaptureViews.Project((CliCommandResult)envelope, Options(kind));
        view.Kind.Should().Be(kind);
        view.Provenance.Origin.Should().Be("live-stream");
        view.Provenance.Durable.Should().BeFalse();
        view.Provenance.ProcessId.Should().Be(42);
        view.Metrics.Should().NotBeEmpty();
        view.Tables.Should().NotBeEmpty();
        view.Tables.Should().OnlyContain(t => t.Rows.Count <= 1 && t.Rows.All(r => r.Length == t.Columns.Count));
        view.Notes.Should().NotBeEmpty();
        var serialized = JsonSerializer.Serialize(view);
        serialized.Should().NotContain("SECRET-CONNECTION");
        if (kind == "allocation")
            view.Notes.Should().Contain(n => n.Contains("1 retained row(s) omitted", StringComparison.Ordinal));
        if (kind == "gc")
        {
            view.Metrics.Should().Contain(m => m.Label == "Authoritative suspension total (ms)" && m.Value == null);
            view.Notes.Should().Contain(n => n.Contains("not pause", StringComparison.Ordinal));
        }
        if (kind == "networking")
            view.Tables.Single(t => t.Id == "operations").Rows[0][3].Should().BeNull();
        if (kind == "threadpool")
            view.Metrics.Should().Contain(m => m.Label == "Confirmed starvation adjustments" && Equals(m.Value, 1));
    }

    [Theory]
    [InlineData("allocation", """{"topN":0}""")]
    [InlineData("allocation", """{"topN":101}""")]
    [InlineData("allocation", """{"topN":"10"}""")]
    [InlineData("allocation", """{"symbolPath":"/tmp/symbols"}""")]
    [InlineData("off_cpu", """{"symbolPath":""}""")]
    [InlineData("off_cpu", """{"symbolPath":"invalid\npath"}""")]
    [InlineData("gc", """{"sources":[]}""")]
    [InlineData("gc-activities", """{"sources":[""]}""")]
    [InlineData("requests", """{"thresholdMs":-1}""")]
    [InlineData("gc", """{"thresholdMs":1000}""")]
    public void InvalidParametersAreRejected(string kind, string json)
    {
        using var document = JsonDocument.Parse(json);
        var act = () => CliStreamingCaptureOptions.Parse(document.RootElement, kind, 42, 6);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidParametersAreForwardedWithoutEnablingPersistence()
    {
        using var document = JsonDocument.Parse("""{"topN":17,"symbolPath":"/tmp/symbols","acknowledgeRisk":"high"}""");
        var options = CliStreamingCaptureOptions.Parse(document.RootElement, "off_cpu", 42, 6);
        options.Command.Should().Be("collect");
        options.Depth.Should().Be("detail");
        options.Top.Should().Be(17);
        options.SymbolPath.Should().Be("/tmp/symbols");
        options.AcknowledgeRisk.Should().Be("high");
        options.Persist.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"kind":"off_cpu"}""", "capture_safety_rejected")]
    [InlineData("""{"kind":"allocation","topN":101}""", "invalid_capture")]
    [InlineData("""{"kind":"requests","thresholdMs":"1000"}""", "invalid_capture")]
    [InlineData("""{"kind":"gc","durationSeconds":"6"}""", "invalid_capture")]
    public async Task ProtocolRejectsUnsafeOrInvalidCaptureWithoutResolvingCollectors(string fields, string code)
    {
        using var input = new StringReader("{\"type\":\"hello\",\"protocolVersion\":2}\n"
            + "{\"type\":\"capture\",\"requestId\":\"test\",\"processId\":42," + fields[1..] + "\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await CliStreamingProtocol.RunAsync(["stream", "--protocol", "jsonl"], input, output, error,
            () => new HostBuilder().Build(), CancellationToken.None);
        exit.Should().Be(0, error.ToString());
        using var frame = JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
        frame.RootElement.GetProperty("code").GetString().Should().Be(code);
        frame.RootElement.GetProperty("requestId").GetString().Should().Be("test");
    }

    [Fact]
    public void ProjectionReportsTextAndNoteLimits()
    {
        var builder = new CaptureViewBuilder();
        builder.Table("sample", "Sample", ["Value"], new[] { new string('x', 3000) }, 1, value => [value]);
        builder.Notes(Enumerable.Repeat("note", 110));
        var view = builder.Build("gc", "test", Options("gc"), Start, Duration, null);
        view.Tables[0].Rows[0][0].As<string>().Length.Should().Be(2051);
        view.Notes.Should().HaveCount(50);
        view.Notes[^1].Should().Contain("MaxNotes=50").And.Contain("1 text value(s) truncated");
    }

    private static CliOptions Options(string kind) => new()
    {
        Command = "collect", Kind = kind, Pid = 42, DurationSeconds = 6, Top = 1, Depth = "detail",
    };
}
