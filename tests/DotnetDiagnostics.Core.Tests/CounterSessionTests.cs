using DotnetDiagnostics.Core.Counters;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class CounterSessionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(CounterSessionOptions.MaxAllowedObservationCapacity + 1)]
    public void CreateSession_RejectsObservationCapacityOutsideBound(int capacity)
    {
        var collector = new EventPipeCounterCollector();

        var act = () => collector.CreateSession(
            processId: 1,
            new CounterSessionOptions { ObservationCapacity = capacity });

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Observation capacity*");
    }

    [Fact]
    public void CreateSession_RejectsNonPositiveProcessId()
    {
        var collector = new EventPipeCounterCollector();

        var act = () => collector.CreateSession(processId: 0);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Process ID must be positive*");
    }

    [Fact]
    public async Task Session_CannotStartWithoutHandlers_AndCanStopBeforeStart()
    {
        await using var session = new EventPipeCounterCollector().CreateSession(processId: 1);

        Action act = () => _ = session.StartAsync();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*at least one event handler*");
        var completion = await session.StopAsync();
        completion.Status.Should().Be(DiagnosticSessionStatus.Stopped);
    }

    [Fact]
    public async Task ComposedSession_DispatchesTypedCaptureResultsThroughOneOrderedPipeline()
    {
        await using var session = new ComposedDiagnosticSession(processId: 1);
        var callbackOrder = new List<string>();
        var sequences = new List<long>();
        var baseSequences = new List<long>();
        using var stringSubscription = session.Attach<DiagnosticSessionCaptureResult<string>>((result, _) =>
        {
            callbackOrder.Add($"string:{result.Result}");
            sequences.Add(result.Sequence);
            return ValueTask.CompletedTask;
        });
        using var baseSubscription = session.Attach<DiagnosticSessionEvent>((sessionEvent, _) =>
        {
            callbackOrder.Add($"base:{sessionEvent.GetType().Name}");
            baseSequences.Add(sessionEvent.Sequence);
            return ValueTask.CompletedTask;
        });
        session.AddCapture(_ => Task.FromResult("first"));
        session.AddCapture(_ => Task.FromResult(42));

        await session.StartAsync();
        var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        callbackOrder.Should().HaveCount(3);
        var stringCallbackIndex = callbackOrder.IndexOf("string:first");
        stringCallbackIndex.Should().BeGreaterThanOrEqualTo(0);
        callbackOrder[stringCallbackIndex + 1]
            .Should().Be("base:DiagnosticSessionCaptureResult`1", "matching handlers run in attachment order");
        baseSequences.Should().Equal(1, 2);
        sequences.Should().ContainSingle();
        baseSequences.Should().Contain(sequences[0]);
        completion.Status.Should().Be(DiagnosticSessionStatus.Completed);
        completion.DroppedObservations.Should().Be(0);
    }

    [Fact]
    public async Task ComposedSession_DispatchesStreamingObservationsBeforeTypedCaptureResult()
    {
        await using var session = new ComposedDiagnosticSession(processId: 1);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<DiagnosticSessionEvent>();
        using var observationSubscription = session.Attach<DiagnosticSessionObservation<int>>((item, _) =>
        {
            events.Add(item);
            if (events.Count == 2) observed.TrySetResult();
            return ValueTask.CompletedTask;
        });
        using var resultSubscription = session.Attach<DiagnosticSessionCaptureResult<string>>((result, _) =>
        {
            events.Add(result);
            return ValueTask.CompletedTask;
        });
        var resultSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.AddStreamingCapture<int, string>((publish, _) =>
        {
            publish(10);
            publish(20);
            return resultSource.Task;
        });

        await session.StartAsync();
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        events.Should().HaveCount(2, "observations are delivered before the finite capture finishes");
        resultSource.SetResult("complete");
        var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        events.Select(item => item.Sequence).Should().Equal(1, 2, 3);
        events[0].Should().BeOfType<DiagnosticSessionObservation<int>>()
            .Which.Observation.Should().Be(10);
        events[0].Timestamp.Should().BeAfter(DateTimeOffset.UnixEpoch);
        events[2].Should().BeOfType<DiagnosticSessionCaptureResult<string>>()
            .Which.Result.Should().Be("complete");
        completion.Status.Should().Be(DiagnosticSessionStatus.Completed);
    }

    [Fact]
    public async Task ComposedSession_EventCaptureResequencesHeterogeneousObservationTypes()
    {
        await using var session = new ComposedDiagnosticSession(processId: 1);
        var received = new List<DiagnosticSessionEvent>();
        using var subscription = session.Attach<DiagnosticSessionEvent>((item, _) =>
        {
            received.Add(item);
            return ValueTask.CompletedTask;
        });
        session.AddEventCapture<string>((publish, _) =>
        {
            publish(CreateObservation(90, 1));
            publish(new DiagnosticSessionObservation<int>(91, DateTimeOffset.UtcNow, 2));
            return Task.FromResult("done");
        });

        await session.StartAsync();
        var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        received.Select(item => item.Sequence).Should().Equal(1, 2, 3);
        received[0].Should().BeOfType<CounterObservation>();
        received[1].Should().BeOfType<DiagnosticSessionObservation<int>>()
            .Which.Observation.Should().Be(2);
        received[2].Should().BeOfType<DiagnosticSessionCaptureResult<string>>()
            .Which.Result.Should().Be("done");
        completion.Status.Should().Be(DiagnosticSessionStatus.Completed);
    }

    [Fact]
    public async Task ComposedSession_StopCancelsSourcesAndDrainsPublishedEvents()
    {
        var session = new ComposedDiagnosticSession(processId: 1);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = session.Attach<DiagnosticSessionCaptureResult<int>>((_, _) =>
        {
            observed.TrySetResult();
            return ValueTask.CompletedTask;
        });
        session.AddCapture(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        });

        await session.StartAsync();
        var stopTask = Task.Run(session.StopAsync);
        var finished = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().Be(stopTask, "stopping a composed session must complete within the bounded shutdown window");
        var completion = await stopTask;

        completion.Status.Should().Be(DiagnosticSessionStatus.Stopped);
        observed.Task.IsCompleted.Should().BeFalse("the capture was canceled before it produced a result");
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ComposedSession_HandlerFailureIsReportedInCompletion()
    {
        await using var session = new ComposedDiagnosticSession(processId: 1);
        using var subscription = session.Attach<DiagnosticSessionCaptureResult<int>>((_, _) =>
            ValueTask.FromException(new InvalidOperationException("Handler failed.")));
        session.AddCapture(_ => Task.FromResult(1));

        await session.StartAsync();
        var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        completion.Status.Should().Be(DiagnosticSessionStatus.Failed);
        completion.Error.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Handler failed.");
    }

    [Fact]
    public async Task ComposedSession_ForwardsChildEventsWithComposedSequence()
    {
        await using var session = new ComposedDiagnosticSession(processId: 1);
        var observations = new List<CounterObservation>();
        using var subscription = session.Attach<CounterObservation>((observation, _) =>
        {
            observations.Add(observation);
            return ValueTask.CompletedTask;
        });
        session.AddSession(new CompletedCounterSession(processId: 1));

        await session.StartAsync();
        var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        observations.Should().ContainSingle();
        observations[0].Sequence.Should().Be(1);
        completion.Status.Should().Be(DiagnosticSessionStatus.Completed);
    }

    [Fact]
    public void CreateSession_RejectsTooManyProvidersBeforeAttaching()
    {
        var collector = new EventPipeCounterCollector();
        var providers = Enumerable.Range(0, CounterSessionOptions.MaxAllowedProviderCount + 1)
            .Select(index => $"Provider.{index}")
            .ToArray();

        var act = () => collector.CreateSession(
            processId: 1,
            new CounterSessionOptions { Providers = providers });

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Provider count*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Provider name longer than the supported 256-character limit: " +
                "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void CreateSession_RejectsInvalidProviderNames(string provider)
    {
        var collector = new EventPipeCounterCollector();

        var act = () => collector.CreateSession(
            processId: 1,
            new CounterSessionOptions { Providers = [provider] });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Provider names*");
    }

    [Fact]
    public void DetermineStatus_WhenSourceFailsWhileTargetLives_ReportsFailure()
    {
        CounterSession.DetermineStatus(
            stopRequested: false,
            targetAlive: true,
            processingError: new InvalidOperationException("source failed"),
            shutdownError: null)
            .Should().Be(DiagnosticSessionStatus.Failed);
    }

    [Fact]
    public void DetermineStatus_WhenTargetExited_ReportsTargetExit()
    {
        CounterSession.DetermineStatus(
            stopRequested: false,
            targetAlive: false,
            processingError: new IOException("stream closed"),
            shutdownError: null)
            .Should().Be(DiagnosticSessionStatus.TargetExited);
    }

    [Fact]
    public async Task Buffer_AssignsIncreasingSequencesToAcceptedObservations()
    {
        var buffer = new DiagnosticSessionEventBuffer(capacity: 2);
        buffer.TryPublish(sequence => CreateObservation(sequence, 1)).Should().BeTrue();
        buffer.TryPublish(sequence => CreateObservation(sequence, 2)).Should().BeTrue();
        buffer.Complete();

        var observations = new List<DiagnosticSessionEvent>();
        await foreach (var observation in buffer.ReadAllAsync(CancellationToken.None))
        {
            observations.Add(observation);
        }

        observations.Select(observation => observation.Sequence).Should().Equal(1, 2);
        buffer.DroppedObservations.Should().Be(0);
    }

    [Fact]
    public async Task Buffer_WhenFull_DropsWithoutGrowingAndAccountsForGap()
    {
        var buffer = new DiagnosticSessionEventBuffer(capacity: 1);
        buffer.TryPublish(sequence => CreateObservation(sequence, 1)).Should().BeTrue();
        buffer.TryPublish(sequence => CreateObservation(sequence, 2)).Should().BeFalse();
        buffer.TryPublish(sequence => CreateObservation(sequence, 3)).Should().BeFalse();

        var observations = new List<DiagnosticSessionEvent>();
        await using var reader = buffer.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        (await reader.MoveNextAsync()).Should().BeTrue();
        observations.Add(reader.Current);

        buffer.TryPublish(sequence => CreateObservation(sequence, 4)).Should().BeTrue();
        buffer.Complete();

        while (await reader.MoveNextAsync())
        {
            observations.Add(reader.Current);
        }

        observations.Select(observation => observation.Sequence).Should().Equal(1, 4);
        buffer.DroppedObservations.Should().Be(2);
    }

    private static CounterValue CreateCounter(double value) =>
        new("System.Runtime", "cpu-usage", "CPU Usage", value, CounterKind.Mean);

    private static CounterObservation CreateObservation(long sequence, double value) =>
        new(sequence, DateTimeOffset.UtcNow, CreateCounter(value));

    private sealed class CompletedCounterSession(int processId) : IDiagnosticSession
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
            return new TestSubscription(() => _handlers.Remove(wrapped));
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var observation = CreateObservation(1, 10);
            foreach (var handler in _handlers)
            {
                await handler(observation, cancellationToken);
            }

            _completion.TrySetResult(new DiagnosticSessionCompletion(
                DiagnosticSessionStatus.Completed,
                now,
                now,
                null,
                0,
                null));
        }

        public Task<DiagnosticSessionCompletion> StopAsync() => Completion;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSubscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
