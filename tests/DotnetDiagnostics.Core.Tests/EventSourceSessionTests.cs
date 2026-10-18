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
}
