using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Container;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Investigation;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class NextActionHintReplayabilityTests
{
    [Theory]
    [InlineData("collect_events")]
    [InlineData("collect_sample")]
    public void CollectorHintSchema_RequiresExplicitKind(string tool)
    {
        var hint = new NextActionHint(
            tool,
            "test",
            new Dictionary<string, object?> { ["processId"] = 424242 });

        Action validate = hint.ShouldMatchCanonicalSchema;

        validate.Should().Throw<Xunit.Sdk.XunitException>();
    }

    [Fact]
    public async Task InvestigationPlannerStepsAndEmittedHints_MatchCanonicalSchema()
    {
        const int processId = 424242;
        var baseline = new BaselineHandle(
            "inv-baseline",
            DateTimeOffset.UtcNow,
            new Dictionary<string, double> { ["cpu_pct"] = 10 });
        var requests = new[]
        {
            new InvestigationRequest(processId, Symptom: "high latency"),
            new InvestigationRequest(processId, Baseline: baseline),
            new InvestigationRequest(processId, Hypothesis: "lock contention"),
            new InvestigationRequest(processId, Hypothesis: "hot CPU"),
            new InvestigationRequest(processId, Hypothesis: "memory leak"),
            new InvestigationRequest(processId, Hypothesis: "threadpool starvation"),
            new InvestigationRequest(processId, Hypothesis: "exception storm"),
            new InvestigationRequest(processId, Hypothesis: "cold startup"),
            new InvestigationRequest(processId, Hypothesis: "unknown failure mode"),
        };
        var planner = new InvestigationPlanner();
        var resolver = new FixedProcessContextResolver(processId);

        foreach (var request in requests)
        {
            var plan = planner.Plan(request);
            var plannedHints = plan.AllSteps
                .Select(step => new NextActionHint(step.ToolName, step.Rationale, step.ToolParams))
                .Concat(plan.Terminals
                    .Where(terminal => terminal.ToolName is not null)
                    .Select(terminal => new NextActionHint(
                        terminal.ToolName!,
                        terminal.Description,
                        terminal.ToolParams)))
                .Concat(plan.Playbook ?? [])
                .Append(plan.NextAction!);

            foreach (var hint in plannedHints)
            {
                hint.ShouldMatchCanonicalSchema();
            }

            var result = await DiagnosticToolInvestigationPlanning.StartInvestigation(
                planner,
                resolver,
                processId,
                request.Symptom,
                request.Hypothesis,
                request.Baseline);

            result.Error.Should().BeNull();
            foreach (var hint in result.Hints)
            {
                hint.ShouldMatchCanonicalSchema();
            }
        }
    }

    [Fact]
    public void AttachFailureDynamicCollectorHint_OmitsUnreconstructableArguments()
    {
        var result = AttachGuard.ClassifyAttachFailure<object>(
            "collect_sample",
            424242,
            new ArtifactPathException("nativeAotMapFile", "test rejection"));

        var hint = result.Hints.Should().ContainSingle().Which;
        hint.NextTool.Should().Be("collect_sample");
        hint.SuggestedArguments.Should().BeNull();
        hint.ShouldMatchCanonicalSchema();
    }

    [Theory]
    [InlineData("growing", "inspect_heap", "source", "live")]
    [InlineData("stable", "collect_events", "kind", "counters")]
    public async Task MemoryTrendHints_MatchCanonicalSchema(
        string verdict,
        string expectedTool,
        string discriminator,
        string expectedValue)
    {
        var trend = CreateMemoryTrend(verdict);

        var result = await DiagnosticToolProcessInspection.GetMemoryTrend(
            new StubMemoryTrendCollector(trend),
            new FixedProcessContextResolver(trend.ProcessId),
            processId: trend.ProcessId,
            durationSeconds: 2,
            sampleEverySeconds: 1);

        result.Error.Should().BeNull();
        foreach (var hint in result.Hints)
        {
            hint.ShouldMatchCanonicalSchema();
        }

        var selected = result.Hints.Should().ContainSingle(hint => hint.NextTool == expectedTool).Which;
        selected.SuggestedArguments![discriminator].Should().Be(expectedValue);
    }

    [Fact]
    public async Task ContainerPressureHints_MatchCanonicalSchema()
    {
        var signals = new[]
        {
            CreateContainerSignals(cpu: new ContainerCpuSignals(1, 10, 2, 1, 20, 1), memory: null, inContainer: true),
            CreateContainerSignals(
                cpu: null,
                memory: new ContainerMemorySignals(90, 100, null, 0.9, 0, 0),
                inContainer: true),
            CreateContainerSignals(cpu: null, memory: null, inContainer: false),
            CreateContainerSignals(cpu: null, memory: null, inContainer: true),
        };

        foreach (var signal in signals)
        {
            var result = await DiagnosticToolProcessInspection.GetContainerSignals(
                new StubContainerSignalsCollector(signal),
                new FixedProcessContextResolver(signal.ProcessId),
                processId: signal.ProcessId);

            result.Error.Should().BeNull();
            var hint = result.Hints.Should().ContainSingle().Which;
            hint.ShouldMatchCanonicalSchema();
            if (signal.InContainer && signal.Cpu is null && signal.Memory is null)
            {
                hint.SuggestedArguments!["kind"].Should().Be("counters");
            }

            var coreHint = ContainerInspectionUseCases.BuildContainerHints(signal)
                .Should().ContainSingle().Which;
            coreHint.ShouldMatchCanonicalSchema();
        }
    }

    [Theory]
    [InlineData(ProcessDumpType.Mini)]
    [InlineData(ProcessDumpType.WithHeap)]
    public async Task WrittenDumpHint_MatchesCanonicalSchema(ProcessDumpType dumpType)
    {
        const int processId = 424242;
        var dump = new DumpResult(
            processId,
            dumpType,
            Path.Combine(Environment.CurrentDirectory, "sample.dmp"),
            1024,
            DateTimeOffset.UtcNow);

        var result = await ProcessDumpUseCases.CollectProcessDump(
            new StubProcessDumper(dump),
            new FixedProcessContextResolver(processId),
            logger: null,
            principalName: "test",
            processId,
            dumpType,
            confirm: true);

        result.Error.Should().BeNull();
        var hint = result.Hints.Should().ContainSingle().Which;
        hint.NextTool.Should().Be("inspect_heap");
        hint.SuggestedArguments!["source"].Should().Be("dump");
        hint.ShouldMatchCanonicalSchema();
    }

    [Theory]
    [InlineData(HeapSnapshotOrigin.Dump, "dump")]
    [InlineData(HeapSnapshotOrigin.Live, "live")]
    [InlineData(HeapSnapshotOrigin.GcDump, null)]
    public void HeapProjectionRecapture_PreservesOriginOrOmitsClrMdOnlyArguments(
        HeapSnapshotOrigin origin,
        string? expectedSource)
    {
        var snapshot = CreateHeapSnapshot(origin);

        var outcome = HeapSnapshotQueryDispatcher.Dispatch(
            snapshot,
            handle: "HEAPHANDLE",
            view: "retention-paths",
            topN: 10,
            rankBy: null,
            typeFullName: null);

        var hint = outcome.Result!.Hints.Should().ContainSingle().Which;
        AssertHeapRecaptureArguments(hint, expectedSource);
    }

    [Theory]
    [InlineData(HeapSnapshotOrigin.Dump, "dump")]
    [InlineData(HeapSnapshotOrigin.Live, "live")]
    [InlineData(HeapSnapshotOrigin.GcDump, null)]
    public async Task ServerHeapRecapture_PreservesOriginOrOmitsClrMdOnlyArguments(
        HeapSnapshotOrigin origin,
        string? expectedSource)
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(
            424242,
            HeapInspectionUseCases.HeapSnapshotKind,
            CreateHeapSnapshot(origin),
            TimeSpan.FromMinutes(10));
        var result = await DiagnosticToolHeapDump.QueryHeapSnapshot(
            store,
            new ThrowingDumpInspector(),
            new SensitiveDataRedactor(null),
            new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            handle.Id,
            view: "duplicate-strings");

        var hint = result.Hints.Should().ContainSingle().Which;
        AssertHeapRecaptureArguments(hint, expectedSource);
    }

    private static MemoryTrend CreateMemoryTrend(string verdict)
    {
        var started = DateTimeOffset.UtcNow;
        return new MemoryTrend(
            ProcessId: 424242,
            WindowStart: started,
            WindowEnd: started.AddSeconds(1),
            Samples:
            [
                new MemoryTrendSample(started, 100, null, null, null, 0, 0),
                new MemoryTrendSample(started.AddSeconds(1), 200, null, null, null, 0, 0),
            ],
            Deltas: new MemoryTrendDeltas(2 * 1024 * 1024, null, null),
            Verdict: verdict,
            Notes: []);
    }

    private static ContainerSignals CreateContainerSignals(
        ContainerCpuSignals? cpu,
        ContainerMemorySignals? memory,
        bool inContainer)
        => new(
            ProcessId: 424242,
            CollectedAt: DateTimeOffset.UtcNow,
            InContainer: inContainer,
            CgroupVersion: inContainer ? CgroupVersion.V2 : CgroupVersion.None,
            CgroupPath: null,
            Cpu: cpu,
            Memory: memory,
            Pressure: null,
            Pids: null,
            OomScore: null,
            Notes: []);

    private static HeapSnapshotArtifact CreateHeapSnapshot(HeapSnapshotOrigin origin)
    {
        var snapshot = new HeapSnapshotArtifact(
            Origin: origin,
            ProcessId: 424242,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(50),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "X64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
            TopTypesByBytes: [],
            TopTypesByInstances: []);

        return origin == HeapSnapshotOrigin.Dump
            ? snapshot with { DumpFilePath = Path.Combine(Environment.CurrentDirectory, "sample.dmp") }
            : snapshot;
    }

    private static void AssertHeapRecaptureArguments(NextActionHint hint, string? expectedSource)
    {
        hint.ShouldMatchCanonicalSchema();
        if (expectedSource is null)
        {
            hint.SuggestedArguments.Should().BeNull();
        }
        else
        {
            hint.SuggestedArguments.Should().NotBeNull();
            hint.SuggestedArguments!["source"].Should().Be(expectedSource);
        }
    }

    private sealed class StubMemoryTrendCollector(MemoryTrend result) : IMemoryTrendCollector
    {
        public Task<MemoryTrend> CollectAsync(
            int processId,
            int durationSeconds,
            int sampleEverySeconds,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class StubContainerSignalsCollector(ContainerSignals result) : IContainerSignalsCollector
    {
        public Task<ContainerSignals> CollectAsync(int processId, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class StubProcessDumper(DumpResult result) : IProcessDumper
    {
        public Task<DumpResult> WriteDumpAsync(
            int processId,
            ProcessDumpType dumpType,
            string? outputDirectory = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class ThrowingDumpInspector : IDumpInspector
    {
        public Task<HeapSnapshotArtifact> InspectAsync(
            string dumpFilePath,
            DumpInspectionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapSnapshotArtifact> InspectLiveAsync(
            int processId,
            DumpInspectionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectInspection> InspectObjectAsync(
            HeapSnapshotArtifact snapshot,
            ulong address,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapGcRootInspection> InspectGcRootAsync(
            HeapSnapshotArtifact snapshot,
            ulong address,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(
            HeapSnapshotArtifact snapshot,
            ulong address,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FixedProcessContextResolver(int processId) : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(
            int? requestedProcessId,
            CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new ProcessContext(
                    processId,
                    RuntimeFlavor.CoreClr,
                    CanSampleCpu: true,
                    CanCollectGcDump: true,
                    AutoResolved: requestedProcessId is null),
                Error: null));
    }
}
