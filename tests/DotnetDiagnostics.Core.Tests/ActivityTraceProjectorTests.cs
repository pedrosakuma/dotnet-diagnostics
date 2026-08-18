using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Security;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ActivityTraceProjectorTests
{
    private const string TraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string ParentId = "1111111111111111";
    private static readonly DateTimeOffset T0 = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly SensitiveDataRedactor Redactor = new();

    [Fact]
    public void Project_SequentialChildren_UsesUnionForResidualAndDoesNotInventSiblingEdge()
    {
        var projection = Project(
            Span("parent", ParentId, null, 0, 100),
            Span("first", "2222222222222222", ParentId, 0, 30),
            Span("second", "3333333333333333", ParentId, 40, 40));

        projection.Spans[0].ResidualDurationMs.Should().Be(30);
        projection.CriticalPathDurationMs.Should().Be(70);
        projection.CriticalPathNodeIndexes.Should().Equal(0, 2);
        projection.Spans[1].ParentNodeIndex.Should().Be(0);
        projection.Spans[2].ParentNodeIndex.Should().Be(0);
    }

    [Fact]
    public void Project_NestedChildren_CriticalPathAddsResidualAlongExactParentLinks()
    {
        var projection = Project(
            Span("parent", ParentId, null, 0, 100),
            Span("child", "2222222222222222", ParentId, 10, 80),
            Span("grandchild", "3333333333333333", "2222222222222222", 20, 50));

        projection.Spans.Select(span => span.ResidualDurationMs).Should().Equal(20, 30, 50);
        projection.CriticalPathDurationMs.Should().Be(100);
        projection.CriticalPathNodeIndexes.Should().Equal(0, 1, 2);
        projection.MaxResidualNodeIndex.Should().Be(2);
        projection.MaxResidualDurationMs.Should().Be(50);
    }

    [Fact]
    public void Project_OverlappingChildren_SubtractsTheirUnionOnlyOnce()
    {
        var projection = Project(
            Span("parent", ParentId, null, 0, 100),
            Span("left", "2222222222222222", ParentId, 0, 70),
            Span("right", "3333333333333333", ParentId, 30, 70));

        projection.Spans[0].ResidualDurationMs.Should().Be(0);
        projection.CriticalPathDurationMs.Should().Be(70);
        projection.CriticalPathNodeIndexes.Should().Equal(0, 1);
    }

    [Fact]
    public void Project_ParallelChildren_UsesLongestExactBranchForCriticalPath()
    {
        var projection = Project(
            Span("parent", ParentId, null, 0, 100),
            Span("long", "2222222222222222", ParentId, 10, 80),
            Span("short", "3333333333333333", ParentId, 10, 50));

        projection.Spans[0].ResidualDurationMs.Should().Be(20);
        projection.CriticalPathDurationMs.Should().Be(100);
        projection.CriticalPathNodeIndexes.Should().Equal(0, 2);
        projection.MaxResidualNodeIndex.Should().Be(2);
        projection.MaxResidualDurationMs.Should().Be(80);
    }

    [Fact]
    public void Project_ClassifiesMissingMalformedDuplicateAndOrphanIdsWithoutInventingEdges()
    {
        var projection = Project(
            Span("root", ParentId, null, 0, 100),
            Span("duplicate", ParentId, null, 1, 10),
            Span("missing-id-child", null, ParentId, 2, 5),
            Span("malformed-id", "bad", null, 3, 5),
            Span("orphan", "4444444444444444", "9999999999999999", 4, 5),
            Span("malformed-parent", "5555555555555555", "bad", 5, 5));

        projection.DuplicateSpanIdCount.Should().Be(1);
        projection.MissingSpanIdCount.Should().Be(1);
        projection.MalformedSpanIdCount.Should().Be(1);
        projection.OrphanCount.Should().Be(1);
        projection.MalformedParentSpanIdCount.Should().Be(1);
        projection.RootCount.Should().Be(5);

        projection.Spans.Single(span => span.OperationName == "missing-id-child")
            .ParentStatus.Should().Be(ActivityTraceParentStatus.Resolved);
        projection.Spans.Single(span => span.OperationName == "orphan")
            .ParentNodeIndex.Should().BeNull();
        projection.Spans.Single(span => span.OperationName == "malformed-parent")
            .ParentStatus.Should().Be(ActivityTraceParentStatus.Malformed);
        projection.Warnings.Should().Contain(warning => warning.Contains("without an invented edge", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_IsExplicitlyWindowLimitedTruncatedAndSafeTagBounded()
    {
        var tags = new Dictionary<string, string>
        {
            ["db.system"] = "Bearer abcdefghijklmnop",
            ["http.request.method"] = "GET",
            ["db.statement"] = "SELECT * FROM Customers WHERE Email = 'private@example.test'",
        };
        var retained = Span("root", ParentId, null, 0, 100, tags);
        var child = Span("child", "2222222222222222", ParentId, 10, 50);
        var capture = Capture(totalActivities: 9, retained, child);

        var projection = ActivityTraceProjector.Project(capture, TraceId.ToUpperInvariant(), 1, Redactor);

        projection.TraceId.Should().Be(TraceId);
        projection.CompletedOnly.Should().BeTrue();
        projection.CanClaimComplete.Should().BeFalse();
        projection.TotalActivities.Should().Be(9);
        projection.RetainedActivities.Should().Be(2);
        projection.Spans.Should().ContainSingle();
        projection.Truncated.Should().BeTrue();
        projection.CriticalPathTruncated.Should().BeTrue();
        projection.Spans[0].Tags.Keys.Should().BeEquivalentTo("db.system", "http.request.method");
        projection.Spans[0].Tags["db.system"].Should().Be(SensitiveDataRedactor.RedactedPlaceholder);
        projection.Warnings.Should().Contain(warning => warning.StartsWith("Completed-only semantics", StringComparison.Ordinal));
        projection.Warnings.Should().Contain(warning => warning.StartsWith("Capture-window limitation", StringComparison.Ordinal));
        projection.Warnings.Should().Contain(warning => warning.StartsWith("Retention truncation", StringComparison.Ordinal));
        projection.Warnings.Should().Contain(warning => warning.StartsWith("Wire projection truncated", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("not-a-trace-id")]
    public void Project_RejectsInvalidTraceId(string? traceId)
    {
        var action = () => ActivityTraceProjector.Project(Capture(0), traceId!, 10, Redactor);

        action.Should().Throw<ArgumentException>();
    }

    private static ActivityTraceProjection Project(params CapturedActivity[] spans) =>
        ActivityTraceProjector.Project(Capture(spans.Length, spans), TraceId, 50, Redactor);

    private static ActivityCapture Capture(int totalActivities, params CapturedActivity[] spans) => new(
        ProcessId: 42,
        SourceFilters: null,
        StartedAt: T0,
        Duration: TimeSpan.FromSeconds(10),
        TotalActivities: totalActivities,
        CompletedActivities: totalActivities,
        Activities: spans,
        BySource: Array.Empty<ActivitySourceSummary>(),
        ByOperation: Array.Empty<ActivityOperationSummary>());

    private static CapturedActivity Span(
        string operation,
        string? spanId,
        string? parentSpanId,
        double startMs,
        double durationMs,
        IReadOnlyDictionary<string, string>? tags = null) => new(
            SourceName: "tests",
            OperationName: operation,
            Id: spanId ?? $"missing-{operation}",
            ParentId: parentSpanId,
            TraceId: TraceId,
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            StartedAt: T0.AddMilliseconds(startMs),
            StoppedAt: T0.AddMilliseconds(startMs + durationMs),
            Duration: TimeSpan.FromMilliseconds(durationMs),
            Tags: tags ?? new Dictionary<string, string>());
}
