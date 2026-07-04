using DotnetDiagnostics.Core.Signals;
using DotnetDiagnostics.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests the cross-signal correlation groupings (#528): same-window co-occurrence across different
/// collectors' already-derived signals (<c>correlation.co-occurrence</c>) and the by-thread-identity
/// overlap between the two thread-snapshot groupings (<c>correlation.thread-overlap</c>). Both
/// describe an <i>observed</i> relationship over already-collected data, never a cause.
/// </summary>
public sealed class CorrelationSignalsTests
{
    // ---- correlation.co-occurrence -------------------------------------------------------------

    private static SignalGroup Fake(string signal, double salience, string handle) => new(
        Signal: signal,
        Summary: "fake",
        Salience: salience,
        Buckets: [new SignalBucket("k", 1, null, handle)],
        NextAction: null);

    [Fact]
    public void CoOccurrence_ReturnsEmpty_WhenOnlyOneCollectorHasSignals()
    {
        var context = new CoOccurrenceContext(
        [
            new CorrelationSource("counters", "h-counters", [Fake("counters.trend", 0.8, "h-counters")]),
            new CorrelationSource("gc", "h-gc", []),
            new CorrelationSource("exceptions", "h-exceptions", []),
        ]);

        var signals = CoOccurrenceSignals.Detect(context);

        signals.Should().BeEmpty();
    }

    [Fact]
    public void CoOccurrence_Emits_WhenTwoCollectorsBothHaveSignals()
    {
        var context = new CoOccurrenceContext(
        [
            new CorrelationSource("counters", "h-counters", [Fake("counters.trend", 0.9, "h-counters")]),
            new CorrelationSource("gc", "h-gc", [Fake("gc.gen2-share", 0.6, "h-gc")]),
            new CorrelationSource("exceptions", "h-exceptions", []),
        ]);

        var signals = CoOccurrenceSignals.Detect(context);

        var co = signals.Should().ContainSingle(s => s.Signal == "correlation.co-occurrence").Subject;
        // Salience is the minimum of the two contributors — never rated above the weakest ingredient.
        co.Salience.Should().BeApproximately(0.6, 0.001);
        co.Summary.Should().Contain("counters").And.Contain("gc");
        co.Buckets.Should().HaveCount(2);
        co.Buckets.Should().Contain(b => b.Handle == "h-counters");
        co.Buckets.Should().Contain(b => b.Handle == "h-gc");
    }

    [Fact]
    public void CoOccurrence_ReturnsEmpty_WhenNoCollectorHasSignals()
    {
        var context = new CoOccurrenceContext(
        [
            new CorrelationSource("counters", "h-counters", []),
            new CorrelationSource("gc", "h-gc", []),
        ]);

        var signals = CoOccurrenceSignals.Detect(context);

        signals.Should().BeEmpty();
    }

    // ---- correlation.thread-overlap -------------------------------------------------------------

    private static ManagedThread Thread(int id, bool blocked, string? waitReason) => new(
        ManagedThreadId: id,
        OSThreadId: (uint)(10_000 + id),
        Address: (ulong)id,
        State: blocked ? "Wait" : "Running",
        IsAlive: true,
        IsBackground: false,
        IsFinalizer: false,
        IsGc: false,
        IsThreadpoolWorker: true,
        LockCount: 0,
        CurrentExceptionType: null,
        TopFrameMethod: null,
        Frames: Array.Empty<ManagedStackFrame>())
    {
        IsLikelyBlocked = blocked,
        InferredWaitReason = waitReason,
    };

    private static MonitorLockState Lock(ulong address, string? typeName, int ownerId, int waitingCount) => new(
        ObjectAddress: address,
        ObjectTypeFullName: typeName,
        OwnerManagedThreadId: ownerId,
        OwnerOSThreadId: (uint)(10_000 + ownerId),
        OwnerThreadAddress: (ulong)ownerId,
        RecursionCount: 1,
        WaitingThreadCount: waitingCount,
        IsContended: waitingCount > 0,
        Source: "SyncBlock");

    [Fact]
    public void ThreadOverlap_Emits_WhenContendedLockOwnerIsItselfBlocked()
    {
        var threads = new[]
        {
            Thread(1, blocked: true, "Socket I/O"), // the owner of the contended lock below
            Thread(2, blocked: true, "Monitor.Enter (contended)"),
            Thread(3, blocked: true, "Monitor.Enter (contended)"),
            Thread(4, blocked: true, "Monitor.Enter (contended)"),
        };
        var locks = new[] { Lock(0x1000, "MyApp.Cache", ownerId: 1, waitingCount: 3) };

        var signals = ThreadWaitSignals.Detect(new ThreadWaitSignalContext(threads.Length, threads, locks, "handle-threads"));

        var overlap = signals.Should().Contain(s => s.Signal == "correlation.thread-overlap").Subject;
        overlap.Summary.Should().Contain("1").And.Contain("Socket I/O");
        overlap.Buckets[0].Handle.Should().Be("handle-threads");
        overlap.NextAction!.SuggestedArguments!["view"].Should().Be("lock-graph");
    }

    [Fact]
    public void ThreadOverlap_ReturnsNothing_WhenLockOwnerIsNotBlocked()
    {
        var threads = new[]
        {
            Thread(1, blocked: false, null), // owner is running, not blocked — no overlap
            Thread(2, blocked: true, "Monitor.Enter (contended)"),
            Thread(3, blocked: true, "Monitor.Enter (contended)"),
            Thread(4, blocked: true, "Monitor.Enter (contended)"),
        };
        var locks = new[] { Lock(0x1000, "MyApp.Cache", ownerId: 1, waitingCount: 3) };

        var signals = ThreadWaitSignals.Detect(new ThreadWaitSignalContext(threads.Length, threads, locks, "handle-threads"));

        signals.Should().NotContain(s => s.Signal == "correlation.thread-overlap");
    }

    [Fact]
    public void ThreadOverlap_ReturnsNothing_WhenNoLockHasWaiters()
    {
        var threads = new[] { Thread(1, blocked: true, "Socket I/O") };
        var signals = ThreadWaitSignals.Detect(new ThreadWaitSignalContext(threads.Length, threads, Array.Empty<MonitorLockState>(), "handle-threads"));

        signals.Should().NotContain(s => s.Signal == "correlation.thread-overlap");
    }
}
