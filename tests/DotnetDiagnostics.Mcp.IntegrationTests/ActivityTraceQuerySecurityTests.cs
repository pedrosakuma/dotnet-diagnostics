using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class ActivityTraceQuerySecurityTests
{
    [Fact]
    public void QueryCollection_TraceView_AppliesConfiguredRedactionPatterns()
    {
        const string traceId = "0af7651916cd43dd8448eb211c80319c";
        var startedAt = new DateTimeOffset(2026, 8, 18, 4, 0, 0, TimeSpan.Zero);
        var capture = new ActivityCapture(
            ProcessId: 42,
            SourceFilters: null,
            StartedAt: startedAt,
            Duration: TimeSpan.FromSeconds(1),
            TotalActivities: 1,
            CompletedActivities: 1,
            Activities:
            [
                new CapturedActivity(
                    SourceName: "tests",
                    OperationName: "redaction",
                    Id: "activity",
                    ParentId: null,
                    TraceId: traceId,
                    SpanId: "1111111111111111",
                    ParentSpanId: null,
                    StartedAt: startedAt,
                    StoppedAt: startedAt.AddMilliseconds(10),
                    Duration: TimeSpan.FromMilliseconds(10),
                    Tags: new Dictionary<string, string>
                    {
                        ["db.system"] = "CUSTOMSECRET-42",
                    }),
            ],
            BySource: Array.Empty<ActivitySourceSummary>(),
            ByOperation: Array.Empty<ActivityOperationSummary>());
        var handles = new MemoryDiagnosticHandleStore();
        var handle = handles.Register(
            42,
            CollectionHandleKinds.Activities,
            capture,
            TimeSpan.FromMinutes(10));
        var options = new SecurityOptions
        {
            RedactionPatterns = { @"CUSTOMSECRET-\d+" },
        };

        var result = DiagnosticTools.QueryCollection(
            handles,
            TestPrincipalAccessors.WithScopes("eventpipe"),
            new SensitiveDataRedactor(options),
            handle.Id,
            view: "trace",
            traceId: traceId);

        var projection = result.Data!.Payload.Should().BeOfType<ActivityTraceProjection>().Subject;
        projection.Spans.Should().ContainSingle();
        projection.Spans[0].Tags["db.system"].Should().Be(SensitiveDataRedactor.RedactedPlaceholder);
    }
}
