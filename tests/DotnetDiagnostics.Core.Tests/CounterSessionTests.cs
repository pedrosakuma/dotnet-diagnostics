using DotnetDiagnostics.Core.Counters;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class CounterSessionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(CounterSessionOptions.MaxAllowedObservationCapacity + 1)]
    public async Task StartAsync_RejectsObservationCapacityOutsideBound(int capacity)
    {
        var collector = new EventPipeCounterCollector();

        var act = () => collector.StartAsync(
            processId: 1,
            new CounterSessionOptions { ObservationCapacity = capacity });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*Observation capacity*");
    }

    [Fact]
    public async Task StartAsync_RejectsNonPositiveProcessId()
    {
        var collector = new EventPipeCounterCollector();

        var act = () => collector.StartAsync(processId: 0);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*Process ID must be positive*");
    }

    [Fact]
    public async Task StartAsync_RejectsTooManyProvidersBeforeAttaching()
    {
        var collector = new EventPipeCounterCollector();
        var providers = Enumerable.Range(0, CounterSessionOptions.MaxAllowedProviderCount + 1)
            .Select(index => $"Provider.{index}")
            .ToArray();

        var act = () => collector.StartAsync(
            processId: 1,
            new CounterSessionOptions { Providers = providers });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*Provider count*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Provider name longer than the supported 256-character limit: " +
                "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task StartAsync_RejectsInvalidProviderNames(string provider)
    {
        var collector = new EventPipeCounterCollector();

        var act = () => collector.StartAsync(
            processId: 1,
            new CounterSessionOptions { Providers = [provider] });

        await act.Should().ThrowAsync<ArgumentException>()
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
            .Should().Be(CounterSessionStatus.Failed);
    }

    [Fact]
    public void DetermineStatus_WhenTargetExited_ReportsTargetExit()
    {
        CounterSession.DetermineStatus(
            stopRequested: false,
            targetAlive: false,
            processingError: new IOException("stream closed"),
            shutdownError: null)
            .Should().Be(CounterSessionStatus.TargetExited);
    }

    [Fact]
    public async Task Buffer_AssignsIncreasingSequencesToAcceptedObservations()
    {
        var buffer = new CounterObservationBuffer(capacity: 2);
        buffer.TryPublish(DateTimeOffset.UtcNow, CreateCounter(1)).Should().BeTrue();
        buffer.TryPublish(DateTimeOffset.UtcNow, CreateCounter(2)).Should().BeTrue();
        buffer.Complete();

        var observations = new List<CounterObservation>();
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
        var buffer = new CounterObservationBuffer(capacity: 1);
        buffer.TryPublish(DateTimeOffset.UtcNow, CreateCounter(1)).Should().BeTrue();
        buffer.TryPublish(DateTimeOffset.UtcNow, CreateCounter(2)).Should().BeFalse();
        buffer.TryPublish(DateTimeOffset.UtcNow, CreateCounter(3)).Should().BeFalse();

        var observations = new List<CounterObservation>();
        await using var reader = buffer.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        (await reader.MoveNextAsync()).Should().BeTrue();
        observations.Add(reader.Current);

        buffer.TryPublish(DateTimeOffset.UtcNow, CreateCounter(4)).Should().BeTrue();
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
}
