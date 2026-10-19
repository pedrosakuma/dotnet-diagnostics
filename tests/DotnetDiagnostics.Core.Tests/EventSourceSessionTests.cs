using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.EventSources;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace DotnetDiagnostics.Core.Tests;

public sealed class EventSourceSessionTests
{
    [Theory]
    [InlineData(80, 1, true)]
    [InlineData(80, 0, false)]
    [InlineData(80, 2, false)]
    [InlineData(81, 1, false)]
    public void ClrDecoder_RequiresTheVerifiedExceptionIdentity(int eventId, int version, bool expected)
    {
        EventSourceSession.UsesClrExceptionDecoder(ClrTraceEventParser.ProviderGuid, eventId, version)
            .Should().Be(expected);
        EventSourceSession.UsesClrExceptionDecoder(Guid.Empty, eventId, version).Should().BeFalse();
    }

    [Theory]
    [InlineData(80, 1, true)]
    [InlineData(80, 0, false)]
    [InlineData(80, 2, false)]
    [InlineData(81, 1, false)]
    public void DecoderRoutes_PublishEachIdentityOnceAndKeepUnknownVersionsGeneric(
        int eventId, int version, bool expectedClrDecoder)
    {
        foreach (var provider in new[] { ClrTraceEventParser.ProviderGuid, Guid.Empty })
        {
            var routes = new[] { true, false }
                .Where(clrDecoder => EventSourceSession.ShouldPublishEvent(provider, eventId, version, clrDecoder))
                .ToArray();

            routes.Should().ContainSingle()
                .Which.Should().Be(provider == ClrTraceEventParser.ProviderGuid && expectedClrDecoder);
        }
    }

    [Fact]
    public void Observation_PreservesUnavailableFieldsWithoutInventingPayloadOrTruncationCounts()
    {
        var observation = new EventSourceLiveObservation(
            1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "provider", "event", 80, 2,
            "Error", null, null, null, new Dictionary<string, string>(), false, 0, 0, 0,
            payloadFieldsUnavailable: true);

        var resequenced = observation.WithSequence(2).Should().BeOfType<EventSourceLiveObservation>().Subject;
        resequenced.PayloadFieldsUnavailable.Should().BeTrue();
        resequenced.Payload.Should().BeEmpty();
        resequenced.PayloadTruncated.Should().BeFalse();
        resequenced.UnsupportedPayloadValues.Should().Be(0);
    }

    [Fact]
    public void Factory_RejectsInvalidProviderCountAndDuplicateNames()
    {
        var factory = new EventPipeEventSourceSessionFactory();

        var noProviders = () => factory.CreateSession(1, new EventSourceSessionOptions());
        noProviders.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Provider count*");

        var duplicateProviders = () => factory.CreateSession(1, new EventSourceSessionOptions
        {
            Providers =
            [
                new("System.Runtime", 4, -1),
                new("system.runtime", 5, 0),
            ],
        });
        duplicateProviders.Should().Throw<ArgumentException>().WithMessage("*unique*");
    }

    [Theory]
    [InlineData(6, "event level")]
    public void Factory_RejectsEventLevelOutsideEtwRange(int eventLevel, string message)
    {
        var act = () => new EventPipeEventSourceSessionFactory().CreateSession(1, new EventSourceSessionOptions
        {
            Providers = [new("System.Runtime", eventLevel, -1)],
        });

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage($"*{message}*");
    }

    [Fact]
    public async Task Session_RequiresAHandlerAndCanStopBeforeStarting()
    {
        await using var session = new EventPipeEventSourceSessionFactory().CreateSession(
            1,
            new EventSourceSessionOptions { Providers = [new("System.Runtime", 5, -1)] });

        Action start = () => { _ = session.StartAsync(); };
        start.Should().Throw<InvalidOperationException>().WithMessage("*at least one event handler*");
        var completion = await session.StopAsync();
        completion.Status.Should().Be(DiagnosticSessionStatus.Stopped);
    }

    [Fact]
    public void Observation_CopiesPayloadAndResequencesWithoutLosingSourceMetadata()
    {
        var payload = new Dictionary<string, string> { ["ExceptionMessage"] = "sensitive" };
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var receivedAt = timestamp.AddMilliseconds(4);
        var observation = new EventSourceLiveObservation(
            1, timestamp, receivedAt, "provider", "event", 80, 0, "Error", 42,
            Guid.NewGuid(), Guid.NewGuid(), payload, false, 0, 0, 0);
        payload["ExceptionMessage"] = "changed";

        var resequenced = observation.WithSequence(17).Should().BeOfType<EventSourceLiveObservation>().Subject;

        resequenced.Sequence.Should().Be(17);
        resequenced.Timestamp.Should().Be(timestamp);
        resequenced.ReceivedAt.Should().Be(receivedAt);
        resequenced.Payload["ExceptionMessage"].Should().Be("sensitive");
        resequenced.RetainedSizeBytes.Should().BeGreaterThan(512);
    }

    [Fact]
    public async Task ComposedSession_EnforcesConfiguredRetainedByteBudget()
    {
        await using var session = new ComposedDiagnosticSession(1, eventCapacity: 8, eventRetainedBytesCapacity: 64);
        var delivered = 0;
        using var subscription = session.Attach<DiagnosticSessionEvent>((_, _) =>
        {
            delivered++;
            return ValueTask.CompletedTask;
        });
        session.AddEventCapture<bool>((publish, _) =>
        {
            publish(new EventSourceLiveObservation(
                0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "provider", "event", 1, 0,
                "Informational", null, null, null, new Dictionary<string, string>(), false, 0, 0, 0));
            return Task.FromResult(true);
        });

        await session.StartAsync();
        var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        delivered.Should().Be(0);
        completion.DroppedObservations.Should().Be(2);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(256)]
    public async Task EventBuffer_SlowConsumerPreservesConfiguredCapacityAndExactDropNewestAccounting(int capacity)
    {
        EventSourceSessionOptions.DefaultObservationCapacity.Should().Be(32);
        new EventSourceSessionOptions().ObservationCapacity.Should().Be(32);
        EventSourceSessionOptions.MaxObservationCapacity.Should().Be(256);
        new CounterSessionOptions().ObservationCapacity.Should().Be(256);

        var buffer = new DiagnosticSessionEventBuffer(capacity, EventSourceSessionOptions.MaxPendingRetainedBytes);
        var template = CreateBoundedObservation();
        for (var index = 1; index <= 100_000; index++)
        {
            buffer.TryPublish(sequence => template.WithSequence(sequence)).Should().Be(index <= capacity);
        }
        buffer.DroppedObservations.Should().Be(100_000 - capacity);

        await using var reader = buffer.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.Sequence.Should().Be(1);
        buffer.TryPublish(sequence => template.WithSequence(sequence)).Should().BeTrue();
        buffer.Complete();

        var accepted = new List<long> { 1 };
        while (await reader.MoveNextAsync()) accepted.Add(reader.Current.Sequence);
        accepted.Should().Equal(Enumerable.Range(1, capacity).Select(index => (long)index).Append(100_001));
        buffer.DroppedObservations.Should().Be(100_000 - capacity);
    }

    [Fact(Timeout = 15_000)]
    public async Task ComposedSession_SlowHandlerKeepsThirtyTwoPendingAndCountsRejectedOffers()
    {
        const int capacity = EventSourceSessionOptions.DefaultObservationCapacity;
        await using var session = new ComposedDiagnosticSession(
            1, eventCapacity: capacity, eventRetainedBytesCapacity: EventSourceSessionOptions.MaxPendingRetainedBytes);
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offersFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<long>();
        using var subscription = session.Attach<EventSourceLiveObservation>(async (observation, token) =>
        {
            delivered.Add(observation.Sequence);
            if (delivered.Count == 1)
            {
                handlerEntered.TrySetResult();
                await releaseHandler.Task.WaitAsync(token);
            }
            if (delivered.Count == capacity + 1) pendingDrained.TrySetResult();
        });
        session.AddEventCapture<bool>(async (publish, token) =>
        {
            var observation = CreateBoundedObservation();
            publish(observation);
            await handlerEntered.Task.WaitAsync(token);
            for (var index = 2; index <= 100_000; index++) publish(observation);
            offersFinished.TrySetResult();
            await releaseSource.Task.WaitAsync(token);
            return true;
        });

        try
        {
            await session.StartAsync();
            await offersFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            delivered.Should().Equal(1);
            session.DroppedEvents.Should().Be(100_000 - capacity - 1,
                "one handler is active and exactly 32 additional events can remain pending");
            releaseHandler.TrySetResult();
            await pendingDrained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            delivered.Should().Equal(Enumerable.Range(1, capacity + 1).Select(index => (long)index));
            releaseSource.TrySetResult();
            var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            completion.DroppedObservations.Should().Be(100_000 - capacity - 1);
        }
        finally
        {
            releaseHandler.TrySetResult();
            releaseSource.TrySetResult();
        }
    }

    private static EventSourceLiveObservation CreateBoundedObservation() =>
        new(0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "provider", "event", 1, 0,
            "Informational", null, null, null, new Dictionary<string, string>(), false, 0, 0, 0);
}
