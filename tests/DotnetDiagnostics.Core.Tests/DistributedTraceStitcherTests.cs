using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.DistributedTrace;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Unit tests for the pure cross-replica trace stitcher (Phase 13 / G3, issue #437). No Kubernetes
/// topology required — the orchestrator fan-out is mocked away by feeding the engine pre-built
/// per-Pod <see cref="ActivityCapture"/> windows directly.
/// </summary>
public sealed class DistributedTraceStitcherTests
{
    private const string Trace = "0af7651916cd43dd8448eb211c80319c";
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Stitch_TwoPods_LinksParentChildAndFlagsSlowestHopBySelfTime()
    {
        // frontend span (200ms total) wraps a backend span (180ms total). The frontend mostly
        // *waits* on the backend, so the slowest HOP must be the backend, not the wrapping parent.
        var frontend = Capture(
            Activity("frontend", "GET /checkout", spanId: "1111111111111111", parentSpanId: null,
                start: T0, durationMs: 200));
        var backend = Capture(
            Activity("backend", "POST /charge", spanId: "2222222222222222", parentSpanId: "1111111111111111",
                start: T0.AddMilliseconds(10), durationMs: 180));

        var timeline = DistributedTraceStitcher.Stitch(Trace, new[]
        {
            ("frontend-abc", frontend),
            ("backend-xyz", backend),
        });

        timeline.PodCount.Should().Be(2);
        timeline.SpanCount.Should().Be(2);
        // Causal order: parent before child.
        timeline.Spans[0].OperationName.Should().Be("GET /checkout");
        timeline.Spans[0].Depth.Should().Be(0);
        timeline.Spans[1].OperationName.Should().Be("POST /charge");
        timeline.Spans[1].Depth.Should().Be(1);
        timeline.Spans[1].ParentResolved.Should().BeTrue();

        // Parent self-time = 200 - 180 = 20ms; backend self-time = 180ms (no children).
        timeline.Spans[0].SelfDurationMs.Should().BeApproximately(20, 0.01);
        timeline.Spans[1].SelfDurationMs.Should().BeApproximately(180, 0.01);

        timeline.SlowestHop.Should().NotBeNull();
        timeline.SlowestHop!.PodName.Should().Be("backend-xyz");
        timeline.SlowestHop.OperationName.Should().Be("POST /charge");
    }

    [Fact]
    public void Stitch_IgnoresSpansOfOtherTraces()
    {
        var capture = Capture(
            Activity("api", "wanted", spanId: "aaaaaaaaaaaaaaaa", parentSpanId: null, start: T0, durationMs: 5, traceId: Trace),
            Activity("api", "other-trace", spanId: "bbbbbbbbbbbbbbbb", parentSpanId: null, start: T0, durationMs: 5, traceId: "ffffffffffffffffffffffffffffffff"));

        var timeline = DistributedTraceStitcher.Stitch(Trace, new[] { ("api-1", capture) });

        timeline.SpanCount.Should().Be(1);
        timeline.Spans[0].OperationName.Should().Be("wanted");
        timeline.Coverage.Single().MatchedSpans.Should().Be(1);
        timeline.Coverage.Single().TotalCapturedActivities.Should().Be(2);
    }

    [Fact]
    public void Stitch_MatchesTraceIdCaseInsensitively()
    {
        var capture = Capture(
            Activity("api", "op", spanId: "aaaaaaaaaaaaaaaa", parentSpanId: null, start: T0, durationMs: 5,
                traceId: Trace.ToUpperInvariant()));

        var timeline = DistributedTraceStitcher.Stitch(Trace, new[] { ("api-1", capture) });

        timeline.SpanCount.Should().Be(1);
    }

    [Fact]
    public void Stitch_OrphanParent_IsTreatedAsRootWithWarning()
    {
        // The child references a parent span that was never captured (parent Pod not attached).
        var capture = Capture(
            Activity("backend", "POST /charge", spanId: "2222222222222222", parentSpanId: "9999999999999999",
                start: T0, durationMs: 50));

        var timeline = DistributedTraceStitcher.Stitch(Trace, new[] { ("backend-1", capture) });

        timeline.SpanCount.Should().Be(1);
        timeline.Spans[0].Depth.Should().Be(0);
        timeline.Spans[0].ParentResolved.Should().BeFalse();
        timeline.Warnings.Should().Contain(w => w.Contains("reference a parent that was not captured", StringComparison.Ordinal));
    }

    [Fact]
    public void Stitch_AttachedPodWithNoMatchingSpans_ProducesCoverageRowAndWarning()
    {
        var hit = Capture(Activity("api", "op", spanId: "aaaaaaaaaaaaaaaa", parentSpanId: null, start: T0, durationMs: 5));
        var miss = Capture(Activity("api", "unrelated", spanId: "cccccccccccccccc", parentSpanId: null, start: T0, durationMs: 5,
            traceId: "ffffffffffffffffffffffffffffffff"));

        var timeline = DistributedTraceStitcher.Stitch(Trace, new[]
        {
            ("pod-hit", hit),
            ("pod-miss", miss),
        });

        timeline.Coverage.Should().ContainSingle(c => c.PodName == "pod-miss" && c.MatchedSpans == 0);
        timeline.Warnings.Should().Contain(w => w.Contains("pod-miss", StringComparison.Ordinal) && w.Contains("observed none", StringComparison.Ordinal));
    }

    [Fact]
    public void Stitch_NoMatchesAnywhere_ReturnsEmptyTimelineWithGuidanceWarning()
    {
        var capture = Capture(Activity("api", "x", spanId: "aaaaaaaaaaaaaaaa", parentSpanId: null, start: T0, durationMs: 5,
            traceId: "ffffffffffffffffffffffffffffffff"));

        var timeline = DistributedTraceStitcher.Stitch(Trace, new[] { ("api-1", capture) });

        timeline.SpanCount.Should().Be(0);
        timeline.Spans.Should().BeEmpty();
        timeline.SlowestHop.Should().BeNull();
        timeline.Warnings[0].Should().Contain("No spans matching trace");
    }

    [Fact]
    public void Stitch_ChildStartsBeforeParent_OrdersCausallyAndWarnsClockSkew()
    {
        // Backend node's clock is 30ms behind the frontend: the child appears to start before its parent.
        var frontend = Capture(
            Activity("frontend", "parent", spanId: "1111111111111111", parentSpanId: null,
                start: T0.AddMilliseconds(50), durationMs: 100));
        var backend = Capture(
            Activity("backend", "child", spanId: "2222222222222222", parentSpanId: "1111111111111111",
                start: T0.AddMilliseconds(20), durationMs: 40));

        var timeline = DistributedTraceStitcher.Stitch(Trace, new[]
        {
            ("frontend-1", frontend),
            ("backend-1", backend),
        });

        // Causal ordering still puts the parent first despite the earlier wall-clock child.
        timeline.Spans[0].OperationName.Should().Be("parent");
        timeline.Spans[1].OperationName.Should().Be("child");
        timeline.Warnings.Should().Contain(w => w.Contains("Clock skew", StringComparison.Ordinal));
    }

    [Fact]
    public void Stitch_DuplicateSpanId_AttachesChildrenOnlyToCanonicalAndWarns()
    {
        // Same parent span-id observed on two Pods (pathological); the child must attach to the
        // canonical (first) parent only, and self-time must not be double-subtracted.
        var podA = Capture(
            Activity("api", "parent-A", spanId: "1111111111111111", parentSpanId: null, start: T0, durationMs: 100));
        var podB = Capture(
            Activity("api", "parent-B-dup", spanId: "1111111111111111", parentSpanId: null, start: T0.AddMilliseconds(1), durationMs: 100),
            Activity("api", "child", spanId: "2222222222222222", parentSpanId: "1111111111111111", start: T0.AddMilliseconds(5), durationMs: 40));

        var timeline = DistributedTraceStitcher.Stitch(Trace, new[]
        {
            ("pod-a", podA),
            ("pod-b", podB),
        });

        timeline.SpanCount.Should().Be(3);
        timeline.Warnings.Should().Contain(w => w.Contains("Duplicate span-id", StringComparison.Ordinal));

        var child = timeline.Spans.Single(s => s.OperationName == "child");
        child.Depth.Should().Be(1);
        // The non-canonical duplicate keeps its full self-time (no child subtraction).
        var dup = timeline.Spans.Single(s => s.OperationName == "parent-B-dup");
        dup.SelfDurationMs.Should().BeApproximately(100, 0.01);
        // Canonical parent owns the child: self-time = 100 - 40 = 60.
        var canonical = timeline.Spans.Single(s => s.OperationName == "parent-A");
        canonical.SelfDurationMs.Should().BeApproximately(60, 0.01);
    }

    [Fact]
    public void Stitch_NullTraceId_Throws()
    {
        var act = () => DistributedTraceStitcher.Stitch("  ", Array.Empty<(string, ActivityCapture)>());
        act.Should().Throw<ArgumentException>();
    }

    // ---- helpers ----------------------------------------------------------------

    private static CapturedActivity Activity(
        string source,
        string operation,
        string? spanId,
        string? parentSpanId,
        DateTimeOffset start,
        double durationMs,
        string? traceId = null)
        => new(
            SourceName: source,
            OperationName: operation,
            Id: spanId ?? Guid.NewGuid().ToString("N"),
            ParentId: parentSpanId,
            TraceId: traceId ?? Trace,
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            StartedAt: start,
            StoppedAt: start.AddMilliseconds(durationMs),
            Duration: TimeSpan.FromMilliseconds(durationMs),
            Tags: new Dictionary<string, string>());

    private static ActivityCapture Capture(params CapturedActivity[] activities)
        => new(
            ProcessId: 1,
            SourceFilters: null,
            StartedAt: T0,
            Duration: TimeSpan.FromSeconds(10),
            TotalActivities: activities.Length,
            CompletedActivities: activities.Length,
            Activities: activities,
            BySource: Array.Empty<ActivitySourceSummary>(),
            ByOperation: Array.Empty<ActivityOperationSummary>());
}
