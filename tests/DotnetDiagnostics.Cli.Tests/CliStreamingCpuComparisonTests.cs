using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli.Tests;

[Collection(nameof(CliStreamingProtocolTests))]
public sealed class CliStreamingCpuComparisonTests
{
    [Fact]
    public async Task Hello_AdvertisesCpuHandleComparisonCapability()
    {
        var store = new MemoryDiagnosticHandleStore();

        var frames = await RunAsync(store, new { type = "hello", protocolVersion = 2 });

        var capability = frames.Single().GetProperty("cpuHandleComparison");
        capability.GetProperty("version").GetInt32().Should().Be(1);
        capability.GetProperty("queryView").GetString().Should().Be("diff");
        capability.GetProperty("baselineHandleField").GetString().Should().Be("baselineHandle");
        capability.GetProperty("retainedHandleLimit").GetInt32().Should().Be(2);
        capability.GetProperty("maxTopMovers").GetInt32().Should().Be(CpuSampleHandleComparison.MaxTopMovers);
    }

    [Fact]
    public async Task QueryDiff_ReturnsObjectiveComparisonWithoutPerformanceVerdict()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = Artifact(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 20L),
            ("Removed", 80L));
        var current = Artifact(
            DateTimeOffset.UnixEpoch.AddSeconds(10),
            TimeSpan.FromSeconds(8),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 50L),
            ("Added", 50L));
        var baselineHandle = Register(store, "cpu-sample", baseline, TimeSpan.FromMinutes(10));
        var currentHandle = Register(store, "cpu-sample", current, TimeSpan.FromMinutes(10));

        var frames = await RunAsync(
            store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "compare",
                handle = currentHandle.Id,
                baselineHandle = baselineHandle.Id,
                view = "diff",
                topN = 10,
            });

        var frame = frames[1];
        frame.GetProperty("type").GetString().Should().Be("query");
        frame.GetProperty("handle").GetString().Should().Be(currentHandle.Id);
        frame.GetProperty("baselineHandle").GetString().Should().Be(baselineHandle.Id);
        var result = frame.GetProperty("result");
        result.GetProperty("summary").GetString().Should().Contain("no performance verdict");
        var data = result.GetProperty("data");
        data.TryGetProperty("verdict", out _).Should().BeFalse();
        data.GetProperty("compatibility").GetProperty("status").GetString().Should().Be("qualified");
        data.GetProperty("baseline").GetProperty("durationSeconds").GetDouble().Should().Be(5);
        data.GetProperty("current").GetProperty("durationSeconds").GetDouble().Should().Be(8);
        var movers = data.GetProperty("topMovers").EnumerateArray().ToArray();
        movers.Single(row => row.GetProperty("displayName").GetString() == "Shared")
            .GetProperty("exclusiveShare").GetProperty("outcome").GetString().Should().Be("up");
        movers.Single(row => row.GetProperty("displayName").GetString() == "Added")
            .GetProperty("exclusiveShare").GetProperty("unavailableReason").GetString()
            .Should().Be("MissingBaselineObservation");
    }

    [Fact]
    public async Task QueryDiff_BackendMismatchIsStructuredAndUnavailable()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = Artifact(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 100L));
        var current = Artifact(
            DateTimeOffset.UnixEpoch.AddSeconds(5),
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.WindowsEtwOnCpu,
            ("Shared", 100L));
        var baselineHandle = Register(store, "cpu-sample", baseline, TimeSpan.FromMinutes(10));
        var currentHandle = Register(store, "cpu-sample", current, TimeSpan.FromMinutes(10));

        var frames = await RunAsync(
            store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "compare",
                handle = currentHandle.Id,
                baselineHandle = baselineHandle.Id,
                view = "diff",
            });

        var data = frames[1].GetProperty("result").GetProperty("data");
        data.GetProperty("compatibility").GetProperty("status").GetString().Should().Be("incompatible");
        data.GetProperty("compatibility").GetProperty("reasons")[0].GetProperty("code").GetString()
            .Should().Be("CpuBackendMismatch");
        var share = data.GetProperty("topMovers")[0].GetProperty("exclusiveShare");
        share.GetProperty("deltaAbs").ValueKind.Should().Be(JsonValueKind.Null);
        share.GetProperty("outcome").GetString().Should().Be("unavailable");
        share.GetProperty("unavailableReason").GetString().Should().Be("CpuBackendMismatch");
    }

    [Fact]
    public async Task QueryDiff_ExpiredBaselineIsDistinguishedFromUnknown()
    {
        var clock = new ManualTimeProvider();
        var store = new MemoryDiagnosticHandleStore(clock: clock);
        var baseline = Artifact(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 100L));
        var current = Artifact(
            DateTimeOffset.UnixEpoch.AddSeconds(5),
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 100L));
        var baselineHandle = Register(store, "cpu-sample", baseline, TimeSpan.FromSeconds(1));
        var currentHandle = Register(store, "cpu-sample", current, TimeSpan.FromMinutes(10));
        clock.Advance(TimeSpan.FromSeconds(2));

        var frames = await RunAsync(
            store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "compare",
                handle = currentHandle.Id,
                baselineHandle = baselineHandle.Id,
                view = "diff",
            });

        frames[1].GetProperty("type").GetString().Should().Be("error");
        frames[1].GetProperty("code").GetString().Should().Be("expired_handle");
        frames[1].GetProperty("message").GetString().Should().Contain("Baseline");
    }

    [Fact]
    public async Task QueryDiff_RequiresCpuKindsAndBaselineHandle()
    {
        var store = new MemoryDiagnosticHandleStore();
        var artifact = Artifact(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 100L));
        var wrongKind = Register(store, "heap-snapshot", artifact, TimeSpan.FromMinutes(10));
        var current = Register(store, "cpu-sample", artifact, TimeSpan.FromMinutes(10));

        var frames = await RunAsync(
            store,
            new { type = "hello", protocolVersion = 2 },
            new { type = "query", requestId = "missing", handle = current.Id, view = "diff" },
            new
            {
                type = "query",
                requestId = "wrong-kind",
                handle = current.Id,
                baselineHandle = wrongKind.Id,
                view = "diff",
            });

        frames[1].GetProperty("code").GetString().Should().Be("invalid_query");
        frames[1].GetProperty("message").GetString().Should().Contain("baselineHandle");
        frames[2].GetProperty("code").GetString().Should().Be("invalid_query");
        frames[2].GetProperty("message").GetString().Should().Contain("cpu-sample");
    }

    [Fact]
    public async Task QuerySingleProfile_RemainsWireCompatibleWithoutBaselineHandle()
    {
        var store = new MemoryDiagnosticHandleStore();
        var artifact = Artifact(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 100L));
        var handle = Register(store, "cpu-sample", artifact, TimeSpan.FromMinutes(10));

        var frames = await RunAsync(
            store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "single",
                handle = handle.Id,
                view = "top-methods",
                topN = 10,
            });

        frames[1].GetProperty("type").GetString().Should().Be("query");
        frames[1].GetProperty("view").GetString().Should().Be("top-methods");
        frames[1].TryGetProperty("baselineHandle", out _).Should().BeFalse();
        frames[1].GetProperty("result").GetProperty("data")
            .GetProperty("methods").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public void CpuHandleWindow_RetainsExactlyLatestPair()
    {
        var store = new MemoryDiagnosticHandleStore(maxEntries: 8);
        var artifact = Artifact(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 100L));
        var first = Register(store, "cpu-sample", artifact, TimeSpan.FromMinutes(10));
        var second = Register(store, "cpu-sample", artifact, TimeSpan.FromMinutes(10));
        var third = Register(store, "cpu-sample", artifact, TimeSpan.FromMinutes(10));
        var window = new CliStreamingCpuHandleWindow();

        window.Track(store, first.Id);
        window.Track(store, second.Id);
        window.Track(store, third.Id);

        store.LookupWithKind(first.Id).Status.Should().Be(DiagnosticHandleLookupStatus.Unknown);
        store.LookupWithKind(second.Id).Status.Should().Be(DiagnosticHandleLookupStatus.Found);
        store.LookupWithKind(third.Id).Status.Should().Be(DiagnosticHandleLookupStatus.Found);
    }

    private static DiagnosticHandle Register(
        IDiagnosticHandleStore store,
        string kind,
        CpuSampleTraceArtifact artifact,
        TimeSpan ttl)
        => store.RegisterWithMetadata(
            artifact.ProcessId,
            kind,
            artifact,
            ttl,
            evictWhenProcessExits: false,
            origin: HandleOrigin.Live,
            producingTool: "collect_sample");

    private static CpuSampleTraceArtifact Artifact(
        DateTimeOffset startedAt,
        TimeSpan duration,
        CpuSampleEvidence evidence,
        params (string Method, long Samples)[] rows)
    {
        var total = rows.Sum(static row => row.Samples);
        var root = new CallTreeNode(
            new SampledFrame("[root]", "<root>"),
            total,
            0,
            rows.Select(static row => new CallTreeNode(
                new SampledFrame("App.dll", row.Method),
                row.Samples,
                row.Samples,
                [])).ToArray());
        return new CpuSampleTraceArtifact(123, startedAt, duration, total, root)
        {
            Evidence = evidence,
        };
    }

    private static async Task<IReadOnlyList<JsonElement>> RunAsync(
        IDiagnosticHandleStore store,
        params object[] requests)
    {
        using var input = new StringReader(
            string.Join('\n', requests.Select(static request => JsonSerializer.Serialize(request))) + "\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await CliStreamingProtocol.RunAsync(
            ["stream", "--protocol", "jsonl"],
            input,
            output,
            errors,
            () => new HostBuilder()
                .ConfigureServices(services => services.AddSingleton(store))
                .Build(),
            CancellationToken.None);
        exit.Should().Be(0, errors.ToString());
        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
