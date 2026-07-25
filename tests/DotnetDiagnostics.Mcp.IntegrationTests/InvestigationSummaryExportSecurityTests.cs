using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Observability;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class InvestigationSummaryExportSecurityTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    public static TheoryData<string, string> ScopedEvidenceKinds()
        => new()
        {
            { CollectionHandleKinds.Counters, "read-counters" },
            { CollectionHandleKinds.GcEvents, "eventpipe" },
            { SamplerUseCases.ThreadSnapshotKind, "ptrace" },
        };

    [Theory]
    [MemberData(nameof(ScopedEvidenceKinds))]
    public void Export_InvestigationExportAlone_CannotReadUnderlyingHandle(
        string kind,
        string requiredScope)
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(1234, kind, ArtifactFor(kind), TimeSpan.FromMinutes(10));

        var result = Export(
            store,
            TestPrincipalAccessors.WithScopes("investigation-export"),
            handle.Id);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
        result.Error.Message.Should().Contain(requiredScope);
        result.Summary.Should().Contain("export_investigation_summary")
            .And.NotContain("query_snapshot");
    }

    [Theory]
    [MemberData(nameof(ScopedEvidenceKinds))]
    public void Export_WithInvestigationAndUnderlyingScope_MatchesCollectorAccess(
        string kind,
        string requiredScope)
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(1234, kind, ArtifactFor(kind), TimeSpan.FromMinutes(10));

        var result = Export(
            store,
            TestPrincipalAccessors.WithScopes("investigation-export", requiredScope),
            handle.Id);

        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Summary.Evidence.Should().ContainSingle()
            .Which.Kind.Should().Be(kind);
    }

    [Fact]
    public void Export_NativeAllocHandleWithCpuTraceArtifact_IsRejected()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            1234,
            SamplerUseCases.NativeAllocHandleKind,
            CpuArtifact(),
            TimeSpan.FromMinutes(10));

        var result = Export(
            store,
            TestPrincipalAccessors.WithScopes("investigation-export"),
            handle.Id);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("HandleKindMismatch");
        result.Data.Should().BeNull();
    }

    [Fact]
    public void Export_CanonicalKindWithWrongArtifactType_IsRejected()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            1234,
            "cpu-sample",
            CounterArtifact(),
            TimeSpan.FromMinutes(10));

        var result = Export(
            store,
            TestPrincipalAccessors.WithScopes("investigation-export"),
            handle.Id);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("HandleKindMismatch");
    }

    [Fact]
    public void Export_ConflictingDuplicateMetrics_ReturnsActionableError()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(
            1234,
            CollectionHandleKinds.Counters,
            CounterArtifact(queueLength: 1),
            TimeSpan.FromMinutes(10));
        var second = store.Register(
            1234,
            CollectionHandleKinds.Counters,
            CounterArtifact(queueLength: 2),
            TimeSpan.FromMinutes(10));

        var result = DiagnosticToolInvestigationPlanning.ExportInvestigationSummary(
            NewExporter(),
            store,
            new NoopTelemetry(),
            TestPrincipalAccessors.WithScopes("investigation-export", "read-counters"),
            first.Id,
            additionalHandles: [second.Id]);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("EvidenceMetricConflict");
        result.Error.Message.Should().Contain("threadpool-queue-length")
            .And.Contain("export separately");
        result.Hints.Should().ContainSingle()
            .Which.NextTool.Should().Be("export_investigation_summary");
    }

    private static DiagnosticResult<ExportedInvestigationSummary> Export(
        IDiagnosticHandleStore store,
        IPrincipalAccessor principalAccessor,
        string handle)
        => DiagnosticToolInvestigationPlanning.ExportInvestigationSummary(
            NewExporter(),
            store,
            new NoopTelemetry(),
            principalAccessor,
            handle);

    private static InvestigationSummaryExporter NewExporter()
        => new(
            new FixedProvenance(),
            new FixedClock(T0),
            static () => "inv-security-test");

    private static object ArtifactFor(string kind)
        => kind switch
        {
            CollectionHandleKinds.Counters => CounterArtifact(),
            CollectionHandleKinds.GcEvents => new GcSummary(
                1234,
                T0,
                TimeSpan.FromSeconds(5),
                1,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(1),
                [new GenerationStats(0, 1)],
                []),
            SamplerUseCases.ThreadSnapshotKind => new ThreadSnapshotArtifact(
                ThreadSnapshotOrigin.Live,
                1234,
                T0,
                TimeSpan.FromMilliseconds(1),
                ".NET",
                "10.0.0",
                [],
                []),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported test kind."),
        };

    private static CounterSnapshot CounterArtifact(double queueLength = 0)
        => new(
            1234,
            T0,
            TimeSpan.FromSeconds(5),
            [new CounterValue("System.Runtime", "threadpool-queue-length", "Queue", queueLength, CounterKind.Mean)],
            [],
            []);

    private static CpuSampleTraceArtifact CpuArtifact()
        => new(
            1234,
            T0,
            TimeSpan.FromSeconds(1),
            1,
            new CallTreeNode(
                new SampledFrame(string.Empty, "<root>"),
                1,
                0,
                [new CallTreeNode(new SampledFrame("App.dll", "App.Work"), 1, 1, [])]));

    private sealed class NoopTelemetry : IInvestigationTelemetryEmitter
    {
        public void Emit(InvestigationSummary summary, string sourceHandle)
        {
        }
    }

    private sealed class FixedProvenance : IProvenanceCollector
    {
        public InvestigationProvenance Collect(int processId, string? buildAssemblyName = null)
            => new("test-host");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
