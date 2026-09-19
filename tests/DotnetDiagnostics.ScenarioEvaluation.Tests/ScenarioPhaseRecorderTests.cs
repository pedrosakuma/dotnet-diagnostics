using System.Text.Json;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class ScenarioPhaseRecorderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondCaptureFailure_RetainsFirstPhaseAndDisposal_WithoutChangingException(bool cancel)
    {
        var clock = new ManualClock();
        var recorder = new ScenarioPhaseRecorder(clock);
        await CompletePhase(recorder, clock, ScenarioWorkloadPhase.Culture);
        using var cts = new CancellationTokenSource();
        var sample = await recorder.ObserveAsync(ScenarioWorkloadPhase.Ordinal, ScenarioPhaseStage.Startup,
            () => Task.FromResult(new FakeLifetime(() => { clock.Advance(1); return ValueTask.CompletedTask; })));
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception expected = cancel ? new OperationCanceledException(cts.Token) : new InvalidOperationException("owned failure");
        var capture = CaptureAndDisposeAsync();

        recorder.Snapshot().Entries[^1].Should().Match<ScenarioPhaseEntry>(
            entry => entry.Phase == ScenarioWorkloadPhase.Ordinal &&
                     entry.Stage == ScenarioPhaseStage.Capture && entry.Outcome == ScenarioPhaseOutcome.Started);
        clock.Advance(41);
        if (cancel) cts.Cancel();
        release.SetException(expected);
        var observed = await Record.ExceptionAsync(() => capture);
        observed.Should().BeSameAs(expected);
        if (cancel) ((OperationCanceledException)observed!).CancellationToken.Should().Be(cts.Token);

        var snapshot = recorder.Snapshot();
        snapshot.OmittedEntries.Should().Be(0);
        snapshot.Entries.Should().HaveCount(12);
        snapshot.Entries.Take(6).Should().OnlyContain(entry => entry.Phase == ScenarioWorkloadPhase.Culture);
        snapshot.Entries.Take(6).Select(entry => entry.Outcome).Should().Equal(
            ScenarioPhaseOutcome.Started, ScenarioPhaseOutcome.Completed,
            ScenarioPhaseOutcome.Started, ScenarioPhaseOutcome.Completed,
            ScenarioPhaseOutcome.Started, ScenarioPhaseOutcome.Completed);
        snapshot.Entries[9].Outcome.Should().Be(cancel ? ScenarioPhaseOutcome.Canceled : ScenarioPhaseOutcome.Failed);
        snapshot.Entries[9].SamplerTimings.Should().BeNull("a failed outer call supplies no internal breakdown");
        snapshot.Entries[9].ElapsedSeconds.Should().Be(51);
        snapshot.Entries[^1].ElapsedSeconds.Should().Be(52);
        snapshot.Entries[^1].Stage.Should().Be(ScenarioPhaseStage.Disposal);
        snapshot.Entries[^1].Outcome.Should().Be(ScenarioPhaseOutcome.Completed);

        async Task CaptureAndDisposeAsync()
        {
            await using var lifetime = recorder.ObserveLifetime(ScenarioWorkloadPhase.Ordinal, sample);
            await recorder.ObserveAsync(ScenarioWorkloadPhase.Ordinal, ScenarioPhaseStage.Capture, () => release.Task);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupFailure_DoesNotInventCaptureOrDisposal(bool cancel)
    {
        var recorder = new ScenarioPhaseRecorder(new ManualClock());
        Exception expected = cancel ? new OperationCanceledException() : new IOException("owned startup failure");
        var observed = await Record.ExceptionAsync(() => recorder.ObserveAsync<int>(
            ScenarioWorkloadPhase.Culture, ScenarioPhaseStage.Startup, () => throw expected));
        observed.Should().BeSameAs(expected);
        recorder.Snapshot().Entries.Should().HaveCount(2).And.OnlyContain(entry => entry.Stage == ScenarioPhaseStage.Startup);
        recorder.Snapshot().Entries[^1].Outcome.Should().Be(cancel ? ScenarioPhaseOutcome.Canceled : ScenarioPhaseOutcome.Failed);
    }

    [Fact]
    public async Task DisposalFailure_IsRetainedAndNotReportedAsCompleted()
    {
        var recorder = new ScenarioPhaseRecorder(new ManualClock());
        var expected = new IOException("owned disposal failure");
        var lifetime = recorder.ObserveLifetime(ScenarioWorkloadPhase.Ordinal,
            new FakeLifetime(() => ValueTask.FromException(expected)));
        var observed = await Record.ExceptionAsync(() => lifetime.DisposeAsync().AsTask());
        observed.Should().BeSameAs(expected);
        recorder.Snapshot().Entries.Select(entry => entry.Outcome).Should().Equal(
            ScenarioPhaseOutcome.Started, ScenarioPhaseOutcome.Failed);
    }

    [Fact]
    public async Task CompletedCapture_RetainsOnlyExistingReturnedTimings_NotInventedGateOrProviderTimes()
    {
        var recorder = new ScenarioPhaseRecorder(new ManualClock());
        var timings = new CpuSampleTimings(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(4),
            TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
        await recorder.ObserveAsync(ScenarioWorkloadPhase.Culture, ScenarioPhaseStage.Capture,
            () => Task.FromResult(timings), value => value);
        recorder.Snapshot().Entries[0].SamplerTimings.Should().BeNull();
        recorder.Snapshot().Entries[1].SamplerTimings.Should().Be(new ScenarioReturnedSamplerTimings(9, 4, 2, 15));
    }

    [Fact]
    public async Task CapacityIsEnforcedAtInsertion_AndSnapshotsAreDetached()
    {
        var recorder = new ScenarioPhaseRecorder(new ManualClock());
        for (var index = 0; index < 20; index++)
            await recorder.ObserveAsync(ScenarioWorkloadPhase.Culture, ScenarioPhaseStage.Capture, () => Task.FromResult(1));
        var snapshot = recorder.Snapshot();
        snapshot.Entries.Should().HaveCount(ScenarioPhaseRecorder.MaximumEntries);
        snapshot.Capacity.Should().Be(32);
        snapshot.OmittedEntries.Should().Be(8);
        await recorder.ObserveAsync(ScenarioWorkloadPhase.Ordinal, ScenarioPhaseStage.Capture, () => Task.FromResult(1));
        snapshot.OmittedEntries.Should().Be(8);
        recorder.Snapshot().OmittedEntries.Should().Be(10);
    }

    [Fact]
    public async Task UtcClockJump_DoesNotChangeMonotonicElapsed()
    {
        var clock = new ManualClock();
        var recorder = new ScenarioPhaseRecorder(clock);
        await recorder.ObserveAsync(ScenarioWorkloadPhase.Culture, ScenarioPhaseStage.Startup, () =>
        {
            clock.Advance(3);
            clock.Utc = clock.Utc.AddHours(-1);
            return Task.FromResult(1);
        });
        var entries = recorder.Snapshot().Entries;
        entries[1].Utc.Should().BeBefore(entries[0].Utc);
        entries[1].ElapsedSeconds.Should().Be(3);
    }

    [Fact]
    public async Task FailedArtifact_RoundTripsTimelineWithNullEvidence_AndNoExceptionPayload()
    {
        var clock = new ManualClock();
        var recorder = new ScenarioPhaseRecorder(clock);
        await CompletePhase(recorder, clock, ScenarioWorkloadPhase.Culture);
        var artifact = new ScenarioTrialArtifact(
            ScenarioJson.CurrentTrialArtifactSchemaVersion, "culture-lookup", 1, 1,
            ScenarioTrialArtifactOutcome.Failed, ScenarioFailureKind.Environment, "Budget exceeded.", null, null)
        {
            PhaseTimeline = recorder.Snapshot(),
        };
        var json = JsonSerializer.Serialize(artifact, ScenarioJsonContext.Default.ScenarioTrialArtifact);
        var restored = JsonSerializer.Deserialize(json, ScenarioJsonContext.Default.ScenarioTrialArtifact)!;
        restored.Evidence.Should().BeNull();
        restored.Report.Should().BeNull();
        restored.Outcome.Should().Be(ScenarioTrialArtifactOutcome.Failed);
        restored.PhaseTimeline.Should().BeEquivalentTo(artifact.PhaseTimeline);
        json.Should().NotContain("exception").And.NotContain("sessionStartDuration");
        var legacy = JsonSerializer.Serialize(artifact with { PhaseTimeline = null }, ScenarioJsonContext.Default.ScenarioTrialArtifact);
        legacy.Should().NotContain("phaseTimeline");
        JsonSerializer.Deserialize(legacy, ScenarioJsonContext.Default.ScenarioTrialArtifact)!.PhaseTimeline.Should().BeNull();
    }

    private static async Task CompletePhase(
        ScenarioPhaseRecorder recorder, ManualClock clock, ScenarioWorkloadPhase phase)
    {
        var sample = await recorder.ObserveAsync(phase, ScenarioPhaseStage.Startup, () =>
        {
            clock.Advance(1);
            return Task.FromResult(new FakeLifetime(() => { clock.Advance(1); return ValueTask.CompletedTask; }));
        });
        await using var lifetime = recorder.ObserveLifetime(phase, sample);
        await recorder.ObserveAsync(phase, ScenarioPhaseStage.Capture, () =>
        {
            clock.Advance(8);
            return Task.FromResult(1);
        });
    }

    private sealed class FakeLifetime(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        internal DateTimeOffset Utc { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        internal void Advance(int seconds)
        {
            _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
            Utc = Utc.AddSeconds(seconds);
        }
    }
}
