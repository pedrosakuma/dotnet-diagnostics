using System.Text.Json;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.DistributedTrace;
using DotnetDiagnostics.Core.Security;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ActivityRetentionTests
{
    private const string Trace = "abcdef0123456789abcdef0123456789";
    private const string Noise = "ffffffffffffffffffffffffffffffff";
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TargetedRetention_NoiseBeforeDuringAfter_DoesNotSpendMatchingBudget(int matches)
    {
        var state = new ActivityRetentionState(1, $" {Trace.ToUpperInvariant()} ", 2);
        for (var i = 0; i < 5; i++) state.Observe(Span(Noise, $"before-{i}"));
        for (var i = 0; i < matches; i++)
        {
            state.Observe(Span(i % 2 == 0 ? Trace : Trace.ToUpperInvariant(), $"target-{i}"));
            state.Observe(Span(Noise, $"during-{i}"));
        }
        for (var i = 0; i < 5; i++) state.Observe(Span(Noise, $"after-{i}"));

        state.Activities.Select(a => a.OperationName).Should()
            .Equal(Enumerable.Range(0, Math.Min(matches, 2)).Select(i => $"target-{i}"));
        state.Retention.Should().Be(new ActivityRetention(
            Trace, 2, 10 + matches * 2, matches, Math.Min(matches, 2), Math.Max(0, matches - 2), 10 + matches));
        AssertAccounting(state.Retention);
    }

    [Fact]
    public void ExploratoryRetention_PreservesFirstNAndCountsAllStopEvents()
    {
        var state = new ActivityRetentionState(2, null, 1);
        state.Observe(Span(Noise, "first"));
        state.Observe(Span(Trace, "second"));
        state.Observe(Span(Trace, "third"));
        state.Activities.Select(a => a.OperationName).Should().Equal("first", "second");
        state.Retention.Should().Be(new ActivityRetention(null, 2, 3, 3, 2, 1, 0));
        AssertAccounting(state.Retention);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("xabcdef0123456789abcdef0123456789")]
    [InlineData("abcdef")]
    public void Retention_RejectsInvalidTraceIds(string trace)
    {
        var act = () => new ActivityRetentionState(1, trace, 1);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void Retention_RejectsInvalidBudgets(int exploratory, int matching)
    {
        var act = () => new ActivityRetentionState(exploratory, Trace, matching);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Retention_StaysBoundedAtInsertionUnderLargeNoiseAndMatchingVolume()
    {
        var state = new ActivityRetentionState(1, Trace, 2);
        var noise = Span(Noise, "noise");
        var wanted = Span(Trace, "wanted");
        for (var i = 0; i < 10_000; i++)
        {
            state.Observe(noise);
            state.Observe(wanted);
            state.Activities.Count.Should().BeLessThanOrEqualTo(2);
        }
        state.Retention.Should().Be(new ActivityRetention(Trace, 2, 20_000, 10_000, 2, 9_998, 10_000));
        AssertAccounting(state.Retention);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonRoundTrip_PreservesProvenanceInCoverageAndTraceProjection(bool capped)
    {
        var state = new ActivityRetentionState(1, Trace, 1);
        state.Observe(Span(Noise, "noise"));
        state.Observe(Span(Trace, "wanted"));
        if (capped) state.Observe(Span(Trace, "dropped"));
        var capture = RoundTrip(Capture(state));
        capture.Retention.Should().Be(state.Retention);
        var timeline = DistributedTraceStitcher.Stitch(Trace, [("pod", capture)]);
        var projection = ActivityTraceProjector.Project(capture, Trace, 10, new SensitiveDataRedactor());
        timeline.Coverage.Single().Retention.Should().Be(state.Retention);
        projection.Retention.Should().Be(state.Retention);
        projection.Warnings.Any(w => w.StartsWith("Retention truncation", StringComparison.Ordinal)).Should().Be(capped);
        timeline.Warnings.Any(w => w.Contains("retention truncation", StringComparison.Ordinal)).Should().Be(capped);
    }

    [Fact]
    public void LegacyAndPartialJson_DoNotBecomeVerifiedZeroLoss()
    {
        var capture = Capture(new ActivityRetentionState(1, null, 1)) with { Retention = null };
        var json = JsonSerializer.SerializeToNode(capture)!;
        json.AsObject().Remove("Retention");
        var legacy = json.Deserialize<ActivityCapture>()!;
        legacy.Retention.Should().BeNull();
        JsonSerializer.Deserialize<ActivityRetention>("{}")!.RetentionLimited.Should().BeNull();
        RoundTrip(capture with { Retention = new ActivityRetention(Trace, 1, 0, 0, 0, 0, 0) })
            .Retention!.RetentionLimited.Should().BeFalse();
    }

    [Fact]
    public async Task LegacyCollectorImplementationAndPositionalTokenRemainCompatible()
    {
        IActivityCollector collector = new LegacyCollector();
        var capture = await collector.CollectAsync(1, TimeSpan.FromSeconds(1), null, 1, CancellationToken.None);
        capture.Retention.Should().BeNull();
        var act = () => collector.CollectAsync(1, TimeSpan.FromSeconds(1), null, 1, Trace, 1, CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static void AssertAccounting(ActivityRetention retention)
    {
        retention.ObservedActivities.Should().Be(retention.MatchingActivities + retention.NonMatchingActivities);
        retention.MatchingActivities.Should().Be(retention.RetainedMatchingActivities + retention.DroppedMatchingActivities);
    }

    private static ActivityCapture RoundTrip(ActivityCapture capture)
        => JsonSerializer.Deserialize<ActivityCapture>(JsonSerializer.Serialize(capture))!;

    private static ActivityCapture Capture(ActivityRetentionState state)
        => new(1, null, Start, TimeSpan.FromSeconds(1), state.ObservedActivities, state.ObservedActivities,
            state.Activities, [], [], state.Retention);

    private static CapturedActivity Span(string traceId, string name)
        => new("test", name, name, null, traceId, "1111111111111111", null,
            Start, Start.AddMilliseconds(1), TimeSpan.FromMilliseconds(1), new Dictionary<string, string>());

    private sealed class LegacyCollector : IActivityCollector
    {
        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources = null,
            int maxActivities = 200, CancellationToken cancellationToken = default)
            => Task.FromResult(new ActivityCapture(processId, sources, Start, duration, 0, 0, [], [], []));
    }
}
