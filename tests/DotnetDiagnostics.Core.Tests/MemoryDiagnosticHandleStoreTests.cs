using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using DotnetDiagnostics.Core.Drilldown;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

public class MemoryDiagnosticHandleStoreTests
{
    [Fact]
    public void DiagnosticHandle_PositionalApiRemainsCompatibleWithOptionalProducerMetadata()
    {
        var handle = new DiagnosticHandle(
            "h",
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            1234,
            "cpu-sample");

        var (id, expiresAt, processId, kind) = handle;
        id.Should().Be("h");
        expiresAt.Should().Be(DateTimeOffset.UnixEpoch.AddMinutes(1));
        processId.Should().Be(1234);
        kind.Should().Be("cpu-sample");
        handle.ProducingTool.Should().BeNull();

        var stamped = handle with { ProducingTool = "collect_events" };
        stamped.ProducingTool.Should().Be("collect_events");
        stamped.Id.Should().Be(handle.Id);

        typeof(IDiagnosticHandleStore).GetMethod(
            nameof(IDiagnosticHandleStore.Register),
            [
                typeof(int),
                typeof(string),
                typeof(object),
                typeof(TimeSpan),
                typeof(bool),
                typeof(HandleOrigin?),
            ]).Should().NotBeNull("the original Register binary member must remain available");
    }

    [Fact]
    public void Register_PersistsOptionalProducingTool()
    {
        var store = new MemoryDiagnosticHandleStore();

        var handle = store.RegisterWithMetadata(
            1234,
            "cpu-sample",
            new object(),
            TimeSpan.FromMinutes(1),
            producingTool: "collect_events");

        handle.ProducingTool.Should().Be("collect_events");
        store.TryGetWithKind(handle.Id)!.Value.Handle.ProducingTool.Should().Be("collect_events");
    }

    [Fact]
    public void Register_IssuesUniqueIdsAndStoresArtifact()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = store.Register(123, "cpu-sample", new Payload("a"), TimeSpan.FromMinutes(5));
        var second = store.Register(123, "cpu-sample", new Payload("b"), TimeSpan.FromMinutes(5));

        first.Id.Should().NotBeNullOrWhiteSpace();
        second.Id.Should().NotBe(first.Id);

        store.TryGet<Payload>(first.Id)!.Value.Should().Be("a");
        store.TryGet<Payload>(second.Id)!.Value.Should().Be("b");
    }

    [Fact]
    public void TryGet_ReturnsNullAfterTtlElapses()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new MemoryDiagnosticHandleStore(clock: clock);
        var handle = store.Register(1, "cpu-sample", new Payload("x"), TimeSpan.FromMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(11));

        store.TryGet<Payload>(handle.Id).Should().BeNull("the artifact must be evicted once TTL elapses");
        store.LookupWithKind(handle.Id).Status.Should().Be(DiagnosticHandleLookupStatus.Expired);
    }

    [Fact]
    public void InvalidateForProcess_RemovesEveryHandleForThatPid()
    {
        var store = new MemoryDiagnosticHandleStore();
        var keep = store.Register(99, "cpu-sample", new Payload("keep"), TimeSpan.FromMinutes(5));
        var drop1 = store.Register(42, "cpu-sample", new Payload("a"), TimeSpan.FromMinutes(5));
        var drop2 = store.Register(42, "gc-dump", new Payload("b"), TimeSpan.FromMinutes(5));

        store.InvalidateForProcess(42).Should().Be(2);

        store.TryGet<Payload>(keep.Id).Should().NotBeNull();
        store.TryGet<Payload>(drop1.Id).Should().BeNull();
        store.TryGet<Payload>(drop2.Id).Should().BeNull();
    }

    [Fact]
    public void InvalidateForProcess_PreservesDumpOriginHandlesEvenWhenPidMatches()
    {
        // Regression guard (issue #206 review): handles registered with
        // `evictWhenProcessExits: false` represent offline artifacts (dump files) and must
        // outlive the PID-exit sweep even if a same-PID live handle exists alongside them.
        var store = new MemoryDiagnosticHandleStore();
        var liveHandle = store.Register(42, "cpu-sample", new Payload("live"), TimeSpan.FromMinutes(5));
        var dumpHandle = store.Register(42, "heap-snapshot", new Payload("dump"), TimeSpan.FromMinutes(5), evictWhenProcessExits: false);

        store.InvalidateForProcess(42).Should().Be(1, "only the live-origin entry opts in to PID-exit eviction");

        store.TryGet<Payload>(liveHandle.Id).Should().BeNull();
        store.TryGet<Payload>(dumpHandle.Id).Should().NotBeNull(
            "dump-origin handles must survive the originating PID's exit (#206)");
    }

    [Fact]
    public void TryGetLatestByKind_ReturnsMostRecentlyRegisteredHandleOfThatKind()
    {
        var store = new MemoryDiagnosticHandleStore();
        var older = store.Register(1, "cpu-sample", new Payload("older"), TimeSpan.FromMinutes(5));
        var newer = store.Register(1, "cpu-sample", new Payload("newer"), TimeSpan.FromMinutes(5));

        var latest = store.TryGetLatestByKind("cpu-sample");

        latest.Should().NotBeNull();
        latest!.Id.Should().Be(newer.Id, "the most recently registered handle of the kind must win, not the oldest");
        latest.Id.Should().NotBe(older.Id);
    }

    [Fact]
    public void TryGetLatestByKind_FiltersByKind()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(1, "heap-snapshot", new Payload("heap"), TimeSpan.FromMinutes(5));
        var cpu = store.Register(1, "cpu-sample", new Payload("cpu"), TimeSpan.FromMinutes(5));

        store.TryGetLatestByKind("cpu-sample")!.Id.Should().Be(cpu.Id);
        store.TryGetLatestByKind("no-such-kind").Should().BeNull();
    }

    [Fact]
    public void TryGetLatestByKind_OptionallyFiltersByProcessId()
    {
        var store = new MemoryDiagnosticHandleStore();
        var forOtherProcess = store.Register(1, "cpu-sample", new Payload("pid1"), TimeSpan.FromMinutes(5));
        var forTargetProcess = store.Register(2, "cpu-sample", new Payload("pid2"), TimeSpan.FromMinutes(5));

        store.TryGetLatestByKind("cpu-sample", processId: 2)!.Id.Should().Be(forTargetProcess.Id);
        store.TryGetLatestByKind("cpu-sample", processId: 1)!.Id.Should().Be(forOtherProcess.Id);
        store.TryGetLatestByKind("cpu-sample", processId: 999).Should().BeNull();
    }

    [Fact]
    public void TryGetLatestByKind_ExcludesExpiredEntries()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new MemoryDiagnosticHandleStore(clock: clock);
        var expiring = store.Register(1, "cpu-sample", new Payload("expiring"), TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(2));
        var fresh = store.Register(1, "cpu-sample", new Payload("fresh"), TimeSpan.FromMinutes(5));

        var latest = store.TryGetLatestByKind("cpu-sample");

        latest.Should().NotBeNull();
        latest!.Id.Should().Be(fresh.Id);
        latest.Id.Should().NotBe(expiring.Id);
    }

    [Fact]
    public void Register_EvictsOldestWhenCapacityReached()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new MemoryDiagnosticHandleStore(maxEntries: 2, clock: clock);

        var first = store.Register(1, "cpu-sample", new Payload("1"), TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = store.Register(2, "cpu-sample", new Payload("2"), TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        var third = store.Register(3, "cpu-sample", new Payload("3"), TimeSpan.FromMinutes(5));

        store.TryGet<Payload>(first.Id).Should().BeNull("oldest entry must be evicted to make room");
        store.TryGet<Payload>(second.Id).Should().NotBeNull();
        store.TryGet<Payload>(third.Id).Should().NotBeNull();
        store.LookupWithKind(first.Id).Status.Should().Be(DiagnosticHandleLookupStatus.CapacityEvicted);
        store.LookupWithKind("never-issued").Status.Should().Be(DiagnosticHandleLookupStatus.Unknown);
    }

    [Fact]
    public void Constructor_RejectsUnsafeCapacity()
    {
        var zero = () => new MemoryDiagnosticHandleStore(maxEntries: 0);
        var excessive = () => new MemoryDiagnosticHandleStore(
            maxEntries: DiagnosticHandleStoreOptions.MaxAllowedEntries + 1);

        zero.Should().Throw<ArgumentOutOfRangeException>();
        excessive.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Tombstones_RemainStrictlyBoundedAndForgetOldestMetadata()
    {
        var store = new MemoryDiagnosticHandleStore(maxEntries: 2);
        var handles = Enumerable.Range(0, 20)
            .Select(i => store.Register(i, "cpu-sample", new Payload(i.ToString()), TimeSpan.FromMinutes(5)))
            .ToArray();

        store.TombstoneCount.Should().Be(store.TombstoneCapacity);
        store.LookupWithKind(handles[0].Id).Status.Should().Be(DiagnosticHandleLookupStatus.Unknown);
        store.LookupWithKind(handles[^3].Id).Status.Should().Be(DiagnosticHandleLookupStatus.CapacityEvicted);
    }

    [Fact]
    public async Task Register_ConcurrentCallersNeverExceedCapacityAndDisposeEachVictimOnce()
    {
        const int capacity = 8;
        const int registrationCount = 128;
        var store = new MemoryDiagnosticHandleStore(capacity);
        var artifacts = Enumerable.Range(0, registrationCount)
            .Select(_ => new TrackingDisposable())
            .ToArray();
        var handles = new DiagnosticHandle[registrationCount];
        var maximumObserved = 0;

        await Task.WhenAll(Enumerable.Range(0, registrationCount).Select(index => Task.Run(() =>
        {
            handles[index] = store.Register(
                index,
                "cpu-sample",
                artifacts[index],
                TimeSpan.FromMinutes(5));
            int observed;
            do
            {
                observed = maximumObserved;
                if (store.EntryCount <= observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref maximumObserved, store.EntryCount, observed) != observed);
        })));

        store.EntryCount.Should().Be(capacity);
        maximumObserved.Should().BeLessThanOrEqualTo(capacity);
        handles.Count(handle => store.LookupWithKind(handle.Id).Status == DiagnosticHandleLookupStatus.Found)
            .Should().Be(capacity);
        artifacts.Sum(static artifact => artifact.DisposeCount).Should().Be(registrationCount - capacity);
        artifacts.Should().OnlyContain(static artifact => artifact.DisposeCount == 0 || artifact.DisposeCount == 1);
    }

    [Fact]
    public void CapacityEviction_EmitsWarningAndBoundedMetricsWithoutArtifactTags()
    {
        var kind = $"test-{Guid.NewGuid():N}";
        var logger = new RecordingLogger<MemoryDiagnosticHandleStore>();
        var measurements = new ConcurrentBag<(string Name, string? Reason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == MemoryDiagnosticHandleStore.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            string? measuredKind = null;
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "kind") measuredKind = tag.Value as string;
                if (tag.Key == "reason") reason = tag.Value as string;
            }

            if (measuredKind == kind)
            {
                measurements.Add((instrument.Name, reason));
            }
        });
        listener.Start();

        var store = new MemoryDiagnosticHandleStore(maxEntries: 1, logger: logger);
        store.Register(1, kind, new ThrowingDisposable(), TimeSpan.FromMinutes(5));
        store.Register(2, kind, new Payload("replacement"), TimeSpan.FromMinutes(5));

        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("Capacity-evicted", StringComparison.Ordinal));
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("Failed to dispose", StringComparison.Ordinal));
        measurements.Should().Contain(measurement =>
            measurement.Name == "dotnet_diagnostics_handle_registrations_total");
        measurements.Should().Contain(measurement =>
            measurement.Name == "dotnet_diagnostics_handle_evictions_total" && measurement.Reason == "capacity");
        measurements.Should().Contain(measurement =>
            measurement.Name == "dotnet_diagnostics_handle_disposal_failures_total" && measurement.Reason == "capacity");
    }

    private sealed record Payload(string Value);

    private sealed class TrackingDisposable : IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("expected test failure");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentBag<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualClock(DateTimeOffset start) { _now = start; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
