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
            { "cpu-sample", "eventpipe" },
            { CollectionHandleKinds.Counters, "read-counters" },
            { CollectionHandleKinds.GcEvents, "eventpipe" },
            { CollectionHandleKinds.GcDatas, "eventpipe" },
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
        if (kind == "cpu-sample")
        {
            result.Data!.Summary.Evidence.Should().BeNull(
                "CPU-only exports retain the legacy v1 JSON shape");
            return;
        }

        var evidence = result.Data!.Summary.Evidence.Should().ContainSingle().Which;
        evidence.Kind.Should().Be(kind);
        evidence.Origin.Should().Be("live");
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
            TestPrincipalAccessors.WithScopes("investigation-export", "eventpipe"),
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
            TestPrincipalAccessors.WithScopes("investigation-export", "eventpipe"),
            handle.Id);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("HandleKindMismatch");
    }

    [Fact]
    public void Export_MissingPrincipal_FailsClosedForSupportedHandle()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            1234,
            CollectionHandleKinds.Counters,
            CounterArtifact(),
            TimeSpan.FromMinutes(10));

        var result = Export(store, NullPrincipalAccessor.Instance, handle.Id);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
        result.Error.Message.Should().Contain("read-counters");
    }

    [Fact]
    public void Export_MixedHandles_RequiresEveryOriginatingScope()
    {
        var store = new MemoryDiagnosticHandleStore();
        var counters = store.Register(
            1234,
            CollectionHandleKinds.Counters,
            CounterArtifact(),
            TimeSpan.FromMinutes(10));
        var gc = store.Register(
            1234,
            CollectionHandleKinds.GcEvents,
            ArtifactFor(CollectionHandleKinds.GcEvents),
            TimeSpan.FromMinutes(10));
        var threads = store.Register(
            1234,
            SamplerUseCases.ThreadSnapshotKind,
            ArtifactFor(SamplerUseCases.ThreadSnapshotKind),
            TimeSpan.FromMinutes(10));

        var denied = DiagnosticToolInvestigationPlanning.ExportInvestigationSummary(
            NewExporter(),
            store,
            new NoopTelemetry(),
            TestPrincipalAccessors.WithScopes(
                "investigation-export",
                "read-counters",
                "eventpipe"),
            counters.Id,
            additionalHandles: [gc.Id, threads.Id]);

        denied.Error.Should().NotBeNull();
        denied.Error!.Kind.Should().Be("Forbidden");
        denied.Error.Message.Should().Contain("ptrace");

        var allowed = DiagnosticToolInvestigationPlanning.ExportInvestigationSummary(
            NewExporter(),
            store,
            new NoopTelemetry(),
            TestPrincipalAccessors.WithScopes(
                "investigation-export",
                "read-counters",
                "eventpipe",
                "ptrace"),
            counters.Id,
            additionalHandles: [gc.Id, threads.Id]);

        allowed.Error.Should().BeNull();
        allowed.Data!.Summary.Evidence.Should().HaveCount(3);
    }

    [Fact]
    public void Export_GuessedHandle_ReturnsExpiredWithoutExportingEvidence()
    {
        var result = Export(
            new MemoryDiagnosticHandleStore(),
            TestPrincipalAccessors.WithScopes(
                "investigation-export",
                "read-counters",
                "eventpipe",
                "ptrace"),
            "guessed-handle");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("HandleExpired");
        result.Data.Should().BeNull();
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
            "cpu-sample" => CpuArtifact(),
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
            CollectionHandleKinds.GcDatas => new GcDatasSnapshot(
                1234,
                T0,
                TimeSpan.FromSeconds(5),
                [new DatasSampleEvent(T0, 1, 100, 1, 0, 0, 1024, 512)],
                [],
                [],
                new DatasParseStats(0, 0, 0)),
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

    private sealed class NullPrincipalAccessor : IPrincipalAccessor
    {
        internal static readonly NullPrincipalAccessor Instance = new();

        public BearerPrincipal? Current => null;
    }
}
