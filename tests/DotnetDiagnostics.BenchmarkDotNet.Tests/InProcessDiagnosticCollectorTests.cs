using DotnetDiagnostics.BenchmarkDotNet;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public class InProcessDiagnosticCollectorTests
{
    private static readonly string[] ExpectedKinds =
    {
        "counters", "exceptions", "gc", "cpu", "allocation", "datas", "catalog",
        "activities", "logs", "jit", "threadpool", "contention", "db",
        "kestrel", "networking", "requests", "gcdump",
    };

    [Fact]
    public void SupportedKinds_MatchExpectedSet()
        => InProcessDiagnosticCollector.SupportedKinds.Should().BeEquivalentTo(ExpectedKinds);

    [Fact]
    public void SupportedKinds_ExcludesEventSource()
        => InProcessDiagnosticCollector.IsSupported("event_source").Should().BeFalse();

    [Fact]
    public void SupportedKinds_ExcludesStartup()
        => InProcessDiagnosticCollector.IsSupported("startup").Should().BeFalse();

    [Theory]
    [InlineData("gc", true)]
    [InlineData("cpu", true)]
    [InlineData("allocation", true)]
    [InlineData("contention", true)]
    [InlineData("kestrel", true)]
    [InlineData("networking", true)]
    [InlineData("requests", true)]
    [InlineData("gcdump", true)]
    [InlineData("event_source", false)]
    [InlineData("startup", false)]
    [InlineData("nonsense", false)]
    public void IsSupported_ReflectsTheSet(string kind, bool expected)
        => InProcessDiagnosticCollector.IsSupported(kind).Should().Be(expected);

    [Fact]
    public void Unsupported_ProducesErrorCapture()
    {
        var capture = KindCapture.Unsupported("event_source");

        capture.Kind.Should().Be("event_source");
        capture.IsError.Should().BeTrue();
        capture.Headline.Should().Contain("event_source");
        capture.Json.Should().Contain("unsupported");
    }

    [Fact]
    public void BuildCpuSummary_RanksHottestFrameFromFullTree_NotTruncatedHotspots()
    {
        // TopHotspots is inclusive-ranked and truncated to top-N, so a hot leaf (Crunch) can be
        // excluded entirely. The headline must select it from the full caller→callee tree by
        // self-cost, not from the (truncated, inclusive-ordered) hotspot list.
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 400, 0, new[]
        {
            new CallTreeNode(new SampledFrame("host.dll", "Main"), 398, 1, new[]
            {
                new CallTreeNode(new SampledFrame("MyApp.dll", "Dispatch"), 397, 10, new[]
                {
                    new CallTreeNode(new SampledFrame("MyApp.dll", "Crunch"), 300, 300, Array.Empty<CallTreeNode>()),
                    new CallTreeNode(new SampledFrame("MyApp.dll", "Parse"), 87, 80, Array.Empty<CallTreeNode>()),
                }),
            }),
        });
        var sample = new CpuSample(
            ProcessId: 1234,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalSamples: 400,
            // Crunch is deliberately absent — simulating the inclusive top-N truncation.
            TopHotspots: new[]
            {
                new Hotspot(new SampledFrame("host.dll", "Main"), InclusiveSamples: 398, ExclusiveSamples: 1),
                new Hotspot(new SampledFrame("MyApp.dll", "Dispatch"), InclusiveSamples: 397, ExclusiveSamples: 10),
            });

        var summary = InProcessDiagnosticCollector.BuildCpuSummary(sample, root, durationSeconds: 5);

        summary.Should().Contain("400 sample(s)");
        summary.Should().Contain("Crunch");
        summary.Should().NotContain("Main");
        summary.Should().NotContain("Dispatch");
        summary.Should().Contain("75.0% exclusive");
        summary.Should().Contain("300 self");
        summary.Should().Contain("300 inclusive");
    }

    [Fact]
    public void BuildCpuSummary_FallsBackToHotspotsBySelfCost_WhenNoTree()
    {
        // Defensive path: no tree available (root null) → pick the highest self-cost hotspot,
        // skipping the inclusive-ranked call-tree root (Main) that has ~zero self-cost.
        var sample = new CpuSample(
            ProcessId: 1234,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalSamples: 400,
            TopHotspots: new[]
            {
                new Hotspot(new SampledFrame("host.dll", "Main"), InclusiveSamples: 398, ExclusiveSamples: 1),
                new Hotspot(new SampledFrame("MyApp.dll", "Crunch"), InclusiveSamples: 360, ExclusiveSamples: 300),
            });

        var summary = InProcessDiagnosticCollector.BuildCpuSummary(sample, root: null, durationSeconds: 5);

        summary.Should().Contain("Crunch");
        summary.Should().NotContain("Main");
        summary.Should().Contain("75.0% exclusive");
        summary.Should().Contain("300 self");
        summary.Should().Contain("360 inclusive");
    }

    [Fact]
    public void BuildCpuSummary_EmptySample_ExplainsNoAggregation()
    {
        var sample = new CpuSample(
            ProcessId: 1234,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalSamples: 0,
            TopHotspots: Array.Empty<Hotspot>());

        var summary = InProcessDiagnosticCollector.BuildCpuSummary(sample, root: null, durationSeconds: 5);

        summary.Should().Contain("no method aggregation");
    }

    [Fact]
    public void BuildAllocationSummary_ReportsTopTypeByBytes()
    {
        var sample = new AllocationSample(
            ProcessId: 1234,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalEvents: 120,
            TotalBytes: 4_200_000,
            TopByBytes: new[]
            {
                new AllocatedType("System.String", TotalBytes: 1_800_000, EventCount: 64, DominantKind: HeapKind.Small),
                new AllocatedType("System.Byte[]", TotalBytes: 900_000, EventCount: 20, DominantKind: HeapKind.Large),
            },
            TopByCount: Array.Empty<AllocatedType>());

        var summary = InProcessDiagnosticCollector.BuildAllocationSummary(sample, durationSeconds: 5, coLocated: false);

        summary.Should().Contain("120 allocation event(s)");
        summary.Should().Contain("4,200,000 bytes");
        summary.Should().Contain("System.String");
        summary.Should().Contain("1,800,000 bytes");
        summary.Should().Contain("Small heap");
        summary.Should().NotContain("in-process toolchain");
    }

    [Fact]
    public void BuildAllocationSummary_WithCallSites_ReportsTopOrigin()
    {
        var sample = new AllocationSample(
            ProcessId: 1234,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalEvents: 120,
            TotalBytes: 4_200_000,
            TopByBytes: new[]
            {
                new AllocatedType("System.String", TotalBytes: 1_800_000, EventCount: 64, DominantKind: HeapKind.Small),
            },
            TopByCount: Array.Empty<AllocatedType>())
        {
            TopBySite = new[]
            {
                new AllocationSite(
                    new SampledFrame("MyApp", "MyApp.Workload.Churn()"),
                    TotalBytes: 1_700_000,
                    EventCount: 60,
                    DominantKind: HeapKind.Small),
            },
        };

        var summary = InProcessDiagnosticCollector.BuildAllocationSummary(sample, durationSeconds: 5, coLocated: false);

        summary.Should().Contain("Top site: MyApp!MyApp.Workload.Churn()");
        summary.Should().Contain("1,700,000 bytes");
    }

    [Fact]
    public void BuildAllocationSummary_CoLocated_FlagsMeasurementNotIsolated()
    {
        var sample = new AllocationSample(
            ProcessId: 1234,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalEvents: 10,
            TotalBytes: 50_000,
            TopByBytes: new[]
            {
                new AllocatedType("System.String", TotalBytes: 50_000, EventCount: 10, DominantKind: HeapKind.Small),
            },
            TopByCount: Array.Empty<AllocatedType>());

        var summary = InProcessDiagnosticCollector.BuildAllocationSummary(sample, durationSeconds: 5, coLocated: true);

        summary.Should().Contain("System.String");
        summary.Should().Contain("in-process toolchain");
        summary.Should().Contain("NOT isolated from MemoryDiagnoser");
    }

    [Fact]
    public void BuildAllocationSummary_NativeAotUnknownOnly_ExplainsEmptyTypeName()
    {
        var sample = new AllocationSample(
            ProcessId: 1234,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalEvents: 40,
            TotalBytes: 3_000_000,
            TopByBytes: new[]
            {
                new AllocatedType("<unknown>", TotalBytes: 3_000_000, EventCount: 40, DominantKind: HeapKind.Small),
            },
            TopByCount: Array.Empty<AllocatedType>());

        var summary = InProcessDiagnosticCollector.BuildAllocationSummary(sample, durationSeconds: 5, coLocated: false);

        summary.Should().Contain("expected on NativeAOT");
    }

    [Fact]
    public void BuildAllocationSummary_EmptySample_ExplainsNoAggregation()
    {
        var sample = new AllocationSample(
            ProcessId: 1234,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalEvents: 0,
            TotalBytes: 0,
            TopByBytes: Array.Empty<AllocatedType>(),
            TopByCount: Array.Empty<AllocatedType>());

        var summary = InProcessDiagnosticCollector.BuildAllocationSummary(sample, durationSeconds: 5, coLocated: false);

        summary.Should().Contain("no type aggregation");
    }
}
