using System.Diagnostics;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Mcp.Observability;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class InvestigationTelemetryTests
{
    private static InvestigationSummary BuildSummary(int hotspotCount = 1)
    {
        var hotspots = Enumerable.Range(0, hotspotCount)
            .Select(i => new HotspotSummary(
                new SymbolRef($"Sample{i}.dll", $"Sample.Work{i}"),
                InclusiveSamples: 50 - i,
                ExclusiveSamples: 40 - i,
                InclusivePercent: 50.0 - i,
                ExclusivePercent: 40.0 - i))
            .ToArray();

        return new InvestigationSummary(
            InvestigationSummary.SchemaV1,
            InvestigationId: "inv-test-1",
            CreatedAt: DateTimeOffset.UnixEpoch,
            ProcessId: 4321,
            Provenance: new InvestigationProvenance("test-host")
            {
                Build = new BuildProvenance(
                    AssemblyName: "Sample0",
                    InformationalVersion: "1.2.3+abc",
                    GitSha: "abc123"),
                Container = new ContainerProvenance(
                    Image: "ghcr.io/acme/app:1.0",
                    Namespace: "prod",
                    PodName: "app-7d9",
                    NodeName: "node-1"),
            },
            Findings: new InvestigationFindings(
                TotalSamples: 100,
                StartedAt: DateTimeOffset.UnixEpoch,
                Duration: TimeSpan.FromSeconds(10),
                TopHotspots: hotspots),
            PreviousInvestigationId: "inv-prev",
            TargetsFix: new InvestigationFixTarget(CommitSha: "deadbeef", PullRequestUrl: "https://example/pr/1"));
    }

    private static (List<Activity> Captured, ActivityListener Listener) CaptureSpans()
    {
        var captured = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == InvestigationTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);
        return (captured, listener);
    }

    [Fact]
    public void Emit_WhenEnabled_ProducesSpanWithSummaryTags()
    {
        var (captured, listener) = CaptureSpans();
        using var _ = listener;
        using var telemetry = new InvestigationTelemetry(new InvestigationTelemetryOptions { Enabled = true });

        telemetry.Emit(BuildSummary(), "HANDLE123");

        captured.Should().ContainSingle();
        var span = captured[0];
        span.OperationName.Should().Be(InvestigationTelemetry.ActivityName);
        span.GetTagItem("investigation.id").Should().Be("inv-test-1");
        span.GetTagItem("investigation.source_handle").Should().Be("HANDLE123");
        span.GetTagItem("process.pid").Should().Be(4321);
        span.GetTagItem("investigation.previous_id").Should().Be("inv-prev");
        span.GetTagItem("investigation.total_samples").Should().Be(100L);
        span.GetTagItem("investigation.hotspot_count").Should().Be(1);
        span.GetTagItem("host.name").Should().Be("test-host");
        span.GetTagItem("service.version").Should().Be("1.2.3+abc");
        span.GetTagItem("vcs.revision").Should().Be("abc123");
        span.GetTagItem("k8s.pod.name").Should().Be("app-7d9");
        span.GetTagItem("investigation.fix.commit").Should().Be("deadbeef");
        span.GetTagItem("investigation.hotspot.0.method").Should().Be("Sample.Work0");
    }

    [Fact]
    public void Emit_WhenDisabled_ProducesNoSpan()
    {
        var (captured, listener) = CaptureSpans();
        using var _ = listener;
        using var telemetry = new InvestigationTelemetry(new InvestigationTelemetryOptions { Enabled = false });

        telemetry.Emit(BuildSummary(), "HANDLE123");

        captured.Should().BeEmpty();
    }

    [Fact]
    public void Emit_CapsHotspotAttributesToMaxHotspotAttributes()
    {
        var (captured, listener) = CaptureSpans();
        using var _ = listener;
        using var telemetry = new InvestigationTelemetry(
            new InvestigationTelemetryOptions { Enabled = true, MaxHotspotAttributes = 2 });

        telemetry.Emit(BuildSummary(hotspotCount: 5), "HANDLE123");

        var span = captured.Should().ContainSingle().Subject;
        span.GetTagItem("investigation.hotspot_count").Should().Be(5);
        span.GetTagItem("investigation.hotspot.0.method").Should().NotBeNull();
        span.GetTagItem("investigation.hotspot.1.method").Should().NotBeNull();
        span.GetTagItem("investigation.hotspot.2.method").Should().BeNull();
    }

    [Fact]
    public void Emit_NeverThrows_WhenTracingListenerFaults()
    {
        // A faulty exporter/listener that throws on stop must not perturb the export path.
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == InvestigationTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ => throw new InvalidOperationException("boom"),
        };
        ActivitySource.AddActivityListener(listener);
        using var _ = listener;
        using var telemetry = new InvestigationTelemetry(new InvestigationTelemetryOptions { Enabled = true });

        var act = () => telemetry.Emit(BuildSummary(), "HANDLE123");

        act.Should().NotThrow();
    }
}
