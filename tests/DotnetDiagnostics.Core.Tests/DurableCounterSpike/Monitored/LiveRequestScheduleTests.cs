using System.Net;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

public sealed class LiveRequestScheduleTests
{
    [Theory]
    [InlineData(0, 0, 600)]
    [InlineData(80, 0, 601)]
    [InlineData(0, 80, 599)]
    [InlineData(80, 80, 600)]
    [InlineData(1250, 1250, 600)]
    public async Task BoundaryJitterPreservesAllNominalSlotsAndTheirOutcomes(
        int startJitterMilliseconds, int endJitterMilliseconds, int actualDispatchPopulation)
    {
        var now = TimeSpan.Zero;
        var sends = 0;
        var dispatchedDuringMeasurement = 0;
        var load = new BoundedLiveRequestLoad("http://localhost");
        using var handler = new ControlledHandler(() =>
        {
            var slot = sends++;
            if (now >= TimeSpan.FromSeconds(12) && now < TimeSpan.FromSeconds(42))
                dispatchedDuringMeasurement++;
            now.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(slot * 50));
            now.Should().BeLessThan(TimeSpan.FromSeconds(44));
            var measured = load.Snapshot().Scheduled;
            if (slot == 239)
                measured.Should().Be(0);
            if (slot == 240)
                measured.Should().Be(1);
            if (slot is 839 or 840)
                measured.Should().Be(600);
            return Response(slot is 239 or 839 or 840
                ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        await load.RunAsync(client, TimeSpan.FromSeconds(44), CancellationToken.None,
            elapsed: () => now,
            delay: (value, _) =>
            {
                now += value;
                if (now == TimeSpan.FromMilliseconds(11_950))
                    now += TimeSpan.FromMilliseconds(startJitterMilliseconds);
                else if (now == TimeSpan.FromMilliseconds(41_950))
                    now += TimeSpan.FromMilliseconds(endJitterMilliseconds);
                return Task.CompletedTask;
            });

        var metrics = load.Snapshot();
        sends.Should().Be(880);
        dispatchedDuringMeasurement.Should().Be(actualDispatchPopulation);
        metrics.Scheduled.Should().Be(600);
        metrics.Completed.Should().Be(600);
        metrics.Succeeded.Should().Be(599);
        metrics.Failed.Should().Be(1);
        metrics.RetainedSamples.Should().Be(600);
        metrics.SkippedAtConcurrencyLimit.Should().Be(0);
        metrics.EpisodeScheduled.Should().Be(880);
        metrics.EpisodeCompleted.Should().Be(880);
        metrics.EpisodeFailed.Should().Be(3);
        metrics.EpisodeSkippedAtConcurrencyLimit.Should().Be(0);
        metrics.SchedulingElapsedSeconds.Should().Be(44);
        metrics.EpisodeElapsedSeconds.Should().Be(44);
        BoundedLiveRequestLoad.HasCompleteSchedule(metrics).Should().BeTrue();
    }

    [Fact]
    public async Task EarlyTimerWakesCannotDispatchBeforeASlotOrCreateAnExtraSlot()
    {
        var now = TimeSpan.Zero;
        var sends = 0;
        var delays = 0;
        var load = new BoundedLiveRequestLoad("http://localhost");
        using var handler = new ControlledHandler(() =>
        {
            now.Should().Be(TimeSpan.FromMilliseconds(sends++ * 50));
            return Response(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        await load.RunAsync(client, TimeSpan.FromSeconds(44), CancellationToken.None,
            elapsed: () => now,
            delay: (value, _) =>
            {
                now += ++delays % 2 == 1 ? value / 2 : value;
                return Task.CompletedTask;
            });

        sends.Should().Be(880);
        delays.Should().Be(1760);
        load.Snapshot().Scheduled.Should().Be(600);
        load.Snapshot().Completed.Should().Be(600);
        load.Snapshot().EpisodeCompleted.Should().Be(880);
        BoundedLiveRequestLoad.HasCompleteSchedule(load.Snapshot()).Should().BeTrue();
    }

    [Theory]
    [InlineData(11_950, 239, 0)]
    [InlineData(41_950, 839, 599)]
    [InlineData(42_000, 840, 600)]
    [InlineData(43_950, 879, 600)]
    public async Task StallAtStopDoesNotReplayOrMislabelUnservedSlots(
        int stallAtMilliseconds, int episodeScheduled, int measuredScheduled)
    {
        var now = TimeSpan.Zero;
        var sends = 0;
        var load = new BoundedLiveRequestLoad("http://localhost");
        using var handler = new ControlledHandler(() =>
        {
            now.Should().BeLessThan(TimeSpan.FromSeconds(44));
            sends++;
            return Response(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        await load.RunAsync(client, TimeSpan.FromSeconds(44), CancellationToken.None,
            elapsed: () => now,
            delay: (value, _) =>
            {
                now += value;
                if (now == TimeSpan.FromMilliseconds(stallAtMilliseconds))
                    now = TimeSpan.FromSeconds(45);
                return Task.CompletedTask;
            });

        var metrics = load.Snapshot();
        sends.Should().Be(episodeScheduled);
        metrics.EpisodeScheduled.Should().Be(episodeScheduled);
        metrics.EpisodeCompleted.Should().Be(episodeScheduled);
        metrics.Scheduled.Should().Be(measuredScheduled);
        metrics.Completed.Should().Be(measuredScheduled);
        metrics.SkippedAtConcurrencyLimit.Should().Be(0);
        metrics.EpisodeSkippedAtConcurrencyLimit.Should().Be(0);
        metrics.SchedulingElapsedSeconds.Should().Be(45);
        metrics.EpisodeElapsedSeconds.Should().Be(45);
        BoundedLiveRequestLoad.HasCompleteSchedule(metrics).Should().BeFalse();
    }

    [Fact]
    public async Task TwoInFlightRequestsAreTheOnlyReasonForConcurrencySkips()
    {
        var now = TimeSpan.Zero;
        var sends = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = new BoundedLiveRequestLoad("http://localhost");
        using var handler = new ControlledHandler(async () =>
        {
            sends++;
            await completion.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        var run = load.RunAsync(client, TimeSpan.FromSeconds(44), CancellationToken.None,
            elapsed: () => now,
            delay: (value, _) =>
            {
                now += value;
                return Task.CompletedTask;
            });
        sends.Should().Be(2);
        run.IsCompleted.Should().BeFalse();
        load.Snapshot().EpisodeScheduled.Should().Be(880);
        load.Snapshot().EpisodeSkippedAtConcurrencyLimit.Should().Be(878);
        load.Snapshot().Scheduled.Should().Be(600);
        load.Snapshot().SkippedAtConcurrencyLimit.Should().Be(600);
        completion.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        load.Snapshot().EpisodeCompleted.Should().Be(2);
        load.Snapshot().Completed.Should().Be(0);
        load.Snapshot().RetainedSamples.Should().Be(0);
    }

    [Fact]
    public async Task WarmupAndMeasuredCompletionsKeepTheirSlotsAfterBothBoundariesAndStop()
    {
        var now = TimeSpan.Zero;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = new BoundedLiveRequestLoad("http://localhost");
        using var handler = new ControlledHandler(async () =>
        {
            var dispatchedAt = now;
            if (dispatchedAt == TimeSpan.FromMilliseconds(11_950)
                || dispatchedAt == TimeSpan.FromMilliseconds(41_950))
                await completion.Task;
            return new HttpResponseMessage(dispatchedAt == TimeSpan.FromMilliseconds(11_950)
                ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        var run = load.RunAsync(client, TimeSpan.FromSeconds(44), CancellationToken.None,
            elapsed: () => now,
            delay: (value, _) =>
            {
                now += value;
                return Task.CompletedTask;
            });
        run.IsCompleted.Should().BeFalse();
        load.Snapshot().Scheduled.Should().Be(600);
        load.Snapshot().Completed.Should().Be(599);
        load.Snapshot().EpisodeSkippedAtConcurrencyLimit.Should().Be(40);
        now = TimeSpan.FromSeconds(44.5);
        completion.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        var metrics = load.Snapshot();
        metrics.Scheduled.Should().Be(600);
        metrics.Completed.Should().Be(600);
        metrics.Succeeded.Should().Be(600);
        metrics.Failed.Should().Be(0);
        metrics.RetainedSamples.Should().Be(600);
        metrics.SkippedAtConcurrencyLimit.Should().Be(0);
        metrics.EpisodeScheduled.Should().Be(880);
        metrics.EpisodeCompleted.Should().Be(840);
        metrics.EpisodeFailed.Should().Be(1);
        metrics.SchedulingElapsedSeconds.Should().Be(44);
        metrics.EpisodeElapsedSeconds.Should().Be(44.5);
        BoundedLiveRequestLoad.HasCompleteSchedule(metrics).Should().BeTrue();
    }

    [Fact]
    public async Task TaskRetentionFailsBeforeSendingThe1001stRequest()
    {
        var now = TimeSpan.Zero;
        var sends = 0;
        var load = new BoundedLiveRequestLoad("http://localhost");
        using var handler = new ControlledHandler(() =>
        {
            sends++;
            return Response(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        var run = () => load.RunAsync(client, TimeSpan.FromSeconds(51), CancellationToken.None,
            elapsed: () => now,
            delay: (value, _) =>
            {
                now += value;
                return Task.CompletedTask;
            });
        (await run.Should().ThrowAsync<DurableStorageExperimentException>())
            .Which.Code.Should().Be("LiveRequestTaskLimit");
        sends.Should().Be(1000);
        load.Snapshot().EpisodeScheduled.Should().Be(1000);
        load.Snapshot().EpisodeCompleted.Should().Be(1000);
        BoundedLiveRequestLoad.HasCompleteSchedule(load.Snapshot()).Should().BeFalse();
    }

    [Fact]
    public void SampleRetentionReportsOverflowWithoutTruncatingCompletionCounts()
    {
        var population = new MonitoredRequestPopulation(
            TimeSpan.Zero, TimeSpan.FromSeconds(100), 1000);
        for (var slot = 0; slot < 1001; slot++)
        {
            var scheduledAt = TimeSpan.FromMilliseconds(slot * 50);
            population.RecordScheduled(scheduledAt, skipped: false);
            population.RecordCompleted(scheduledAt, 10, success: true);
        }

        var complete = () => population.RecordEpisodeCompleted(TimeSpan.FromSeconds(100));
        complete.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("LiveRequestSampleLimit");
        population.Snapshot().Scheduled.Should().Be(1001);
        population.Snapshot().Completed.Should().Be(1001);
        population.Snapshot().RetainedSamples.Should().Be(1000);
    }

    private static Task<HttpResponseMessage> Response(HttpStatusCode status)
        => Task.FromResult(new HttpResponseMessage(status));

    private sealed class ControlledHandler(Func<Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsolutePath.Should().Be("/cpu-burn");
            request.RequestUri.Query.Should().Be("?ms=10");
            return send();
        }
    }
}
