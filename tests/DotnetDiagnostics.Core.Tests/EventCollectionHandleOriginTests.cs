using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class EventCollectionHandleOriginTests
{
    private const int ProcessId = 4242;
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task CollectGcEvents_RegistersNonEvictableLiveHandle()
    {
        var store = new MemoryDiagnosticHandleStore();
        var snapshot = new GcSummary(
            ProcessId,
            StartedAt,
            TimeSpan.FromSeconds(1),
            1,
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(2),
            [new GenerationStats(0, 1)],
            []);

        var result = await EventCollectionUseCases.CollectGcEvents(
            new FixedGcCollector(snapshot),
            new FixedProcessContextResolver(),
            store,
            ProcessId,
            durationSeconds: 1);

        AssertNonEvictableLiveHandle(store, result.Handle);
    }

    [Fact]
    public async Task CollectGcDatas_RegistersNonEvictableLiveHandle()
    {
        var store = new MemoryDiagnosticHandleStore();
        var snapshot = new GcDatasSnapshot(
            ProcessId,
            StartedAt,
            TimeSpan.FromSeconds(1),
            [new DatasSampleEvent(StartedAt, 1, 100, 2, 0, 0, 1024, 512)],
            [],
            [],
            new DatasParseStats(0, 0, 0));

        var result = await EventCollectionUseCases.CollectGcDatas(
            new FixedGcDatasCollector(snapshot),
            new FixedProcessContextResolver(),
            store,
            ProcessId,
            durationSeconds: 1);

        AssertNonEvictableLiveHandle(store, result.Handle);
    }

    private static void AssertNonEvictableLiveHandle(
        MemoryDiagnosticHandleStore store,
        string? handle)
    {
        handle.Should().NotBeNullOrWhiteSpace();
        var lookup = store.TryGetWithKind(handle!);
        lookup.Should().NotBeNull();
        lookup!.Value.Handle.Origin.Should().Be(HandleOrigin.Live);

        store.InvalidateForProcess(ProcessId).Should().Be(0);
        store.TryGetWithKind(handle!).Should().NotBeNull();
    }

    private sealed class FixedGcCollector(GcSummary snapshot) : IGcCollector
    {
        public Task<GcSummary> CollectAsync(
            int processId,
            TimeSpan duration,
            int maxEvents = 200,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class FixedGcDatasCollector(GcDatasSnapshot snapshot) : IGcDatasCollector
    {
        public Task<GcDatasSnapshot> CollectAsync(
            int processId,
            TimeSpan duration,
            int maxEvents = 1000,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class FixedProcessContextResolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(
            int? requestedProcessId,
            CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new ProcessContext(
                    requestedProcessId ?? ProcessId,
                    RuntimeFlavor.CoreClr,
                    CanSampleCpu: true,
                    CanCollectGcDump: true,
                    AutoResolved: false,
                    RuntimeVersion: "10.0.0",
                    BindingSource: "explicit"),
                null));
    }
}
