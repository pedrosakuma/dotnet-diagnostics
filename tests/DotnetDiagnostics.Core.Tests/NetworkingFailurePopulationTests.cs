using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.Networking;

namespace DotnetDiagnostics.Core.Tests;

public sealed class NetworkingFailurePopulationTests
{
    [Theory]
    [InlineData("Resolution", false)]
    [InlineData("Resolution", true)]
    [InlineData("Handshake", false)]
    [InlineData("Handshake", true)]
    public void ProductionPairedHandler_AllFailedAndMixedUseStartToStop(string prefix, bool mixed)
    {
        var pending = new NetworkingActivityCorrelator<DateTimeOffset>();
        var durations = new BoundedDurationSampler();
        long started = 0, stopped = 0, failed = 0;
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UnixEpoch;
        Handle("Start", 0);
        Handle("/Failed", 100);
        Handle("Failed", 200);
        Assert.Equal(0, durations.Count);
        Assert.Equal(1, pending.Snapshot().UnfinishedFailed);
        Handle("/Stop", 255);
        Assert.Equal(TimeSpan.FromMilliseconds(255), durations.Max);
        if (mixed)
        {
            id = Guid.NewGuid();
            Handle("/Start", 300);
            Handle("Stop", 310);
        }
        Assert.Equal(mixed ? 2 : 1, durations.Count);
        Assert.Equal(mixed ? 2 : 1, started);
        Assert.Equal(started, stopped);
        Assert.Equal(2, failed);
        var counts = pending.Snapshot();
        Assert.Equal(started, counts.Paired);
        Assert.Equal(1, counts.PairedFailed);
        Assert.Equal(mixed ? 1 : 0, counts.PairedWithoutFailure);
        Assert.Equal(1, counts.RepeatedFailureEvents);
        Assert.False(counts.HasLimitations);
        Assert.Equal(TimeSpan.FromMilliseconds(255), durations.GetPercentile(0.95));

        void Handle(string suffix, int milliseconds)
            => Assert.True(EventPipeNetworkingCollector.HandlePaired(prefix + suffix,
                prefix + "Start", prefix + "/Start", prefix + "Stop", prefix + "/Stop",
                prefix + "Failed", prefix + "/Failed", id, start.AddMilliseconds(milliseconds),
                pending, durations, ref started, ref stopped, ref failed));
    }

    [Theory]
    [InlineData("Resolution")]
    [InlineData("Handshake")]
    public void ProductionPairedHandler_MissingStartOrStopDoesNotUseFailurePayload(string prefix)
    {
        var pending = new NetworkingActivityCorrelator<DateTimeOffset>();
        var durations = new BoundedDurationSampler();
        long started = 0, stopped = 0, failed = 0;
        var id = Guid.NewGuid();
        Handle("Failed");
        Handle("Stop");
        id = Guid.NewGuid();
        Handle("Start");
        Handle("Failed");
        Assert.Equal(0, durations.Count);
        Assert.Equal(1, pending.Snapshot().UnmatchedFailures);
        Assert.Equal(1, pending.Snapshot().UnmatchedStops);
        Assert.Equal(1, pending.Snapshot().UnfinishedFailed);
        Assert.Equal(0, pending.Snapshot().Paired);
        Assert.True(pending.Snapshot().HasLimitations);

        void Handle(string suffix)
            => Assert.True(EventPipeNetworkingCollector.HandlePaired(prefix + suffix,
                prefix + "Start", prefix + "/Start", prefix + "Stop", prefix + "/Stop",
                prefix + "Failed", prefix + "/Failed", id, DateTimeOffset.UnixEpoch,
                pending, durations, ref started, ref stopped, ref failed));
    }
}
