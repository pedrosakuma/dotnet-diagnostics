using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Container;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class NextActionHintReplayabilityTests
{
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
