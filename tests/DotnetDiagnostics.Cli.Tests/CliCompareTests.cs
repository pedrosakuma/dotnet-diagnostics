using System.Globalization;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.ThreadPool;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliCompareTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "CliCompareTests", Guid.NewGuid().ToString("N"));

    public CliCompareTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void TrySaveComparableSnapshot_Counters_WritesSnapshotJson()
    {
        var output = Path.Combine(_root, "counter-before.json");
        var snapshot = new CounterSnapshot(
            Environment.ProcessId,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(5),
            new[] { new CounterValue("System.Runtime", "cpu-usage", "CPU Usage", 12.5, CounterKind.Mean, "%") },
            Array.Empty<MeterInstrumentValue>(),
            Array.Empty<string>());

        var saved = CliCommands.TrySaveComparableSnapshot(snapshot, output, out var comparable, out var error);

        saved.Should().BeTrue(error);
        comparable.Should().NotBeNull();
        File.Exists(output).Should().BeTrue();
        using var stream = File.OpenRead(output);
        var roundTrip = JsonSerializer.Deserialize(stream, ComparableSnapshotJsonContext.Default.ComparableSnapshot);
        roundTrip.Should().NotBeNull();
        roundTrip!.Schema.Should().Be(ComparableSnapshot.SchemaV1);
        roundTrip.Kind.Should().Be(CollectionHandleKinds.Counters);
        roundTrip.Label.Should().Be("counter-before");
        roundTrip.Metrics.Should().Contain(m => m.Definition.Name == "counter:System.Runtime/cpu-usage" && m.Value == 12.5);
    }

    [Fact]
    public void TrySaveComparableSnapshot_ThreadPool_WritesScalarOnlySnapshotJson()
    {
        var output = Path.Combine(_root, "threadpool-after.json");
        var timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var snapshot = new ThreadPoolEventSnapshot(
            Environment.ProcessId,
            timestamp,
            TimeSpan.FromSeconds(5),
            [new ThreadPoolCountBucket(timestamp, 2), new ThreadPoolCountBucket(timestamp.AddSeconds(1), 5)],
            Array.Empty<ThreadPoolCountBucket>(),
            [new ThreadPoolHillClimbingSample(timestamp, "Starvation", 2, 5, 10)],
            [new ThreadPoolWorkItemOrigin("MyApp.Queue.Work", 3)],
            new ThreadPoolEffectiveSettings(1, 100, 1, 100),
            TotalEnqueueEvents: 10,
            TotalDequeueEvents: 7,
            Notes: Array.Empty<string>());

        var saved = CliCommands.TrySaveComparableSnapshot(snapshot, output, out var comparable, out var error);

        saved.Should().BeTrue(error);
        comparable.Should().NotBeNull();
        comparable!.Kind.Should().Be(CollectionHandleKinds.ThreadPoolSnapshot);
        comparable.Rows.Should().BeEmpty();
        comparable.Metrics.Should().Contain(m => m.Definition.Name == "starvationAdjustments" && m.Value == 1);
        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public async Task Compare_TwoSnapshots_RendersHeadlineVerdict()
    {
        var before = WriteSnapshot("before", 1);
        var after = WriteSnapshot("after", 3);

        var (exit, stdout, stderr) = await RunAsync("compare", before, after);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("compare: gc-datas Trend before→after verdict=regression");
        stdout.Should().Contain("headline: first→last regression");
        stdout.Should().Contain("heapCountChanges");
    }

    [Fact]
    public async Task Compare_ThreeSnapshots_RendersTrendAndCanSaveFullDiff()
    {
        var before = WriteSnapshot("before", 1);
        var middle = WriteSnapshot("middle", 2);
        var final = WriteSnapshot("final", 3);
        var output = Path.Combine(_root, "matrix.json");

        var (exit, stdout, stderr) = await RunAsync("compare", before, middle, final, "--save", output);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("before→final verdict=regression");
        stdout.Should().Contain("trend MonotonicUp");
        File.Exists(output).Should().BeTrue();
        using var stream = File.OpenRead(output);
        var diff = JsonSerializer.Deserialize(stream, ComparableSnapshotJsonContext.Default.SnapshotJourneyDiff);
        diff.Should().NotBeNull();
        diff!.Labels.Should().Equal("before", "middle", "final");
        diff.Verdict.Should().Be("regression");
    }

    [Fact]
    public async Task Compare_DispersionMode_ParsesAndPlumbsMode()
    {
        var pod0 = WriteMetricSnapshot("pod0", ("outlier", 10), ("monotonic-0", 10), ("monotonic-1", 10), ("monotonic-2", 10), ("monotonic-3", 10), ("monotonic-4", 10));
        var pod1 = WriteMetricSnapshot("pod1", ("outlier", 100), ("monotonic-0", 50), ("monotonic-1", 51), ("monotonic-2", 52), ("monotonic-3", 53), ("monotonic-4", 54));
        var pod2 = WriteMetricSnapshot("pod2", ("outlier", 10), ("monotonic-0", 90), ("monotonic-1", 90), ("monotonic-2", 90), ("monotonic-3", 90), ("monotonic-4", 90));

        var (exit, stdout, stderr) = await RunAsync("compare", "--mode", "dispersion", pod0, pod1, pod2);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("compare: gc-datas Dispersion pod0→pod2 verdict=dispersed");
        stdout.Should().Contain("outlier: cv");
    }

    [Fact]
    public async Task Compare_InvalidMode_ReturnsUsageError()
    {
        var before = WriteSnapshot("before", 1);
        var after = WriteSnapshot("after", 3);

        var (exit, _, stderr) = await RunAsync("compare", "--mode", "fleet", before, after);

        exit.Should().Be(2);
        stderr.Should().Contain("Unknown --mode 'fleet'");
    }

    [Fact]
    public async Task Compare_Json_EmitsSnapshotJourneyDiff()
    {
        var before = WriteSnapshot("before", 1);
        var after = WriteSnapshot("after", 3);

        var (exit, stdout, _) = await RunAsync("compare", before, after, "--json");

        exit.Should().Be(0);
        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("kind").GetString().Should().Be(CollectionHandleKinds.GcDatas);
        doc.RootElement.GetProperty("verdict").GetString().Should().Be("regression");
        doc.RootElement.TryGetProperty("metricSeries", out _).Should().BeTrue();
    }

    [Fact]
    public void TrySaveComparableSnapshot_UnsupportedKind_ReturnsActionableMessage()
    {
        var saved = CliCommands.TrySaveComparableSnapshot(new object(), Path.Combine(_root, "unsupported.json"), out _, out var error);

        saved.Should().BeFalse();
        error.Should().Be("kind 'Object' is not yet comparable (--save supports: gc-datas, counters, gc-events, contention-snapshot, threadpool-snapshot)");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteSnapshot(string label, double heapCountChanges)
        => WriteMetricSnapshot(label, ("heapCountChanges", heapCountChanges));

    private string WriteMetricSnapshot(string label, params (string name, double value)[] metrics)
    {
        var path = Path.Combine(_root, label + ".json");
        var snapshot = new ComparableSnapshot(
            ComparableSnapshot.SchemaV1,
            CollectionHandleKinds.GcDatas,
            label,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            Environment.ProcessId,
            metrics.Select(metric => new MetricValue(
                new MetricDefinition(metric.name, MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Total, MetricNormalization.None, "count"),
                metric.value)).ToArray(),
            Array.Empty<ComparableRow>());
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, snapshot, ComparableSnapshotJsonContext.Default.ComparableSnapshot);
        return path;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
