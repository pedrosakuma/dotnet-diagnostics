using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// <see cref="GcSession"/> reuses <see cref="EventPipeDiagnosticSessionBase"/> for its lifecycle
/// (start/stop/cancel/target-exit/failure classification, sequencing, drop-accounting), which is
/// already exercised at full rigor by <see cref="CounterSessionTests"/> against the same shared
/// base. These tests cover what is specific to the GC session: input validation and the shape of
/// <see cref="GcPauseObservation"/>.
/// </summary>
public sealed class GcSessionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(GcSessionOptions.MaxAllowedObservationCapacity + 1)]
    public void CreateSession_RejectsObservationCapacityOutsideBound(int capacity)
    {
        var collector = new EventPipeGcCollector();

        var act = () => collector.CreateSession(
            processId: 1,
            new GcSessionOptions { ObservationCapacity = capacity });

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Observation capacity*");
    }

    [Fact]
    public void CreateSession_RejectsNonPositiveProcessId()
    {
        var collector = new EventPipeGcCollector();

        var act = () => collector.CreateSession(processId: 0);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Process ID must be positive*");
    }

    [Fact]
    public async Task Session_CannotStartWithoutHandlers_AndCanStopBeforeStart()
    {
        await using var session = new EventPipeGcCollector().CreateSession(processId: 1);

        Action act = () => _ = session.StartAsync();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*at least one event handler*");
        var completion = await session.StopAsync();
        completion.Status.Should().Be(DiagnosticSessionStatus.Stopped);
    }

    [Fact]
    public async Task ComposedSession_ForwardsGcPauseObservationsWithComposedSequence()
    {
        await using var session = new ComposedDiagnosticSession(processId: 1);
        var observations = new List<GcPauseObservation>();
        using var subscription = session.Attach<GcPauseObservation>((observation, _) =>
        {
            observations.Add(observation);
            return ValueTask.CompletedTask;
        });
        session.AddSession(new CompletedGcSession(processId: 1));

        await session.StartAsync();
        var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        observations.Should().ContainSingle();
        observations[0].Sequence.Should().Be(1);
        observations[0].Collection.Reason.Should().Be("induced");
        completion.Status.Should().Be(DiagnosticSessionStatus.Completed);
    }

    [Fact]
    public async Task ComposedSession_ForwardsGcAndCounterObservations_WithAggregatedDrops()
    {
        // #1098 acceptance criterion: composing a GcSession alongside a CounterSession (or any
        // other EventPipeDiagnosticSessionBase subclass) must deliver both observation types,
        // correctly sequenced, and must surface the sum of both children's dropped-observation
        // counts, not just one of them.
        await using var session = new ComposedDiagnosticSession(processId: 1);
        var gcObservations = new List<GcPauseObservation>();
        var counterObservations = new List<CounterObservation>();
        using var gcSubscription = session.Attach<GcPauseObservation>((observation, _) =>
        {
            gcObservations.Add(observation);
            return ValueTask.CompletedTask;
        });
        using var counterSubscription = session.Attach<CounterObservation>((observation, _) =>
        {
            counterObservations.Add(observation);
            return ValueTask.CompletedTask;
        });
        session.AddSession(new CompletedGcSession(processId: 1, droppedObservations: 3));
        session.AddSession(new CompletedCounterSession(processId: 1, droppedObservations: 7));

        await session.StartAsync();
        var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        gcObservations.Should().ContainSingle();
        counterObservations.Should().ContainSingle();
        completion.DroppedObservations.Should().Be(
            10, "a composed session must sum drops reported by every child session");
        completion.Status.Should().Be(DiagnosticSessionStatus.Completed);
    }

    [Fact]
    public void GcPauseObservation_WithSequence_PreservesCollectionAndTimestamp()
    {
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(5);
        var collection = new GcEvent(timestamp, Generation: 0, Reason: "induced", Type: "NonConcurrentGC",
            PauseDuration: TimeSpan.FromMilliseconds(12), ClrInstanceId: 1, CollectionCount: 7);
        var observation = new GcPauseObservation(1, timestamp, collection);

        var resequenced = observation.WithSequence(42);

        resequenced.Sequence.Should().Be(42);
        resequenced.Timestamp.Should().Be(timestamp);
        resequenced.Should().BeOfType<GcPauseObservation>()
            .Which.Collection.Should().BeSameAs(collection);
    }

    private sealed class CompletedCounterSession(int processId, long droppedObservations = 0) : IDiagnosticSession
    {
        private readonly List<Func<DiagnosticSessionEvent, CancellationToken, ValueTask>> _handlers = [];
        private readonly TaskCompletionSource<DiagnosticSessionCompletion> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId { get; } = processId;

        public Task<DiagnosticSessionCompletion> Completion => _completion.Task;

        public IDisposable Attach<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
            where TEvent : DiagnosticSessionEvent
        {
            Func<DiagnosticSessionEvent, CancellationToken, ValueTask> wrapped =
                (sessionEvent, cancellationToken) => sessionEvent is TEvent typedEvent
                    ? handler(typedEvent, cancellationToken)
                    : ValueTask.CompletedTask;
            _handlers.Add(wrapped);
            return new NoopDisposable();
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            var counter = new CounterValue("System.Runtime", "cpu-usage", "CPU Usage", 10, CounterKind.Mean);
            var observation = new CounterObservation(1, DateTimeOffset.UtcNow, counter);
            foreach (var handler in _handlers)
            {
                await handler(observation, cancellationToken).ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow;
            _completion.TrySetResult(new DiagnosticSessionCompletion(
                DiagnosticSessionStatus.Completed, now, now, null, droppedObservations, null));
        }

        public Task<DiagnosticSessionCompletion> StopAsync() => _completion.Task;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class CompletedGcSession(int processId, long droppedObservations = 0) : IDiagnosticSession
    {
        private readonly List<Func<DiagnosticSessionEvent, CancellationToken, ValueTask>> _handlers = [];
        private readonly TaskCompletionSource<DiagnosticSessionCompletion> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId { get; } = processId;

        public Task<DiagnosticSessionCompletion> Completion => _completion.Task;

        public IDisposable Attach<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
            where TEvent : DiagnosticSessionEvent
        {
            Func<DiagnosticSessionEvent, CancellationToken, ValueTask> wrapped =
                (sessionEvent, cancellationToken) => sessionEvent is TEvent typedEvent
                    ? handler(typedEvent, cancellationToken)
                    : ValueTask.CompletedTask;
            _handlers.Add(wrapped);
            return new NoopDisposable();
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            var collection = new GcEvent(DateTimeOffset.UtcNow, Generation: 0, Reason: "induced",
                Type: "NonConcurrentGC", PauseDuration: TimeSpan.FromMilliseconds(3));
            var observation = new GcPauseObservation(1, collection.Timestamp, collection);
            foreach (var handler in _handlers)
            {
                await handler(observation, cancellationToken).ConfigureAwait(false);
            }

            var now = DateTimeOffset.UtcNow;
            _completion.TrySetResult(new DiagnosticSessionCompletion(
                DiagnosticSessionStatus.Completed, now, now, null, droppedObservations, null));
        }

        public Task<DiagnosticSessionCompletion> StopAsync() => _completion.Task;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
