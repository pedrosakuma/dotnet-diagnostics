using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public enum ScenarioWorkloadPhase { Culture, Ordinal }
public enum ScenarioPhaseStage { Startup, Capture, Disposal }
public enum ScenarioPhaseOutcome { Started, Completed, Canceled, Failed }

public sealed record ScenarioReturnedSamplerTimings(
    double CaptureSeconds,
    double SymbolicationSeconds,
    double AggregationSeconds,
    double TotalSeconds);

public sealed record ScenarioPhaseEntry(
    ScenarioWorkloadPhase Phase,
    ScenarioPhaseStage Stage,
    ScenarioPhaseOutcome Outcome,
    DateTimeOffset Utc,
    double ElapsedSeconds,
    ScenarioReturnedSamplerTimings? SamplerTimings = null);

public sealed record ScenarioPhaseTimeline(
    IReadOnlyList<ScenarioPhaseEntry> Entries,
    long OmittedEntries,
    int Capacity);

/// <summary>Test-only outer lifecycle evidence; failed captures expose no inferred ETW internal timings.</summary>
public sealed class ScenarioPhaseRecorder
{
    internal const int MaximumEntries = 32;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly List<ScenarioPhaseEntry> _entries = [];
    private readonly object _gate = new();
    private long _omitted;

    public ScenarioPhaseRecorder() : this(TimeProvider.System) { }

    internal ScenarioPhaseRecorder(TimeProvider clock)
    {
        _clock = clock;
        _started = clock.GetTimestamp();
    }

    public ScenarioPhaseTimeline Snapshot()
    {
        lock (_gate)
        {
            return new ScenarioPhaseTimeline(_entries.ToArray(), _omitted, MaximumEntries);
        }
    }

    internal async Task<T> ObserveAsync<T>(
        ScenarioWorkloadPhase phase, ScenarioPhaseStage stage, Func<Task<T>> operation,
        Func<T, CpuSampleTimings?>? timings = null)
    {
        Record(phase, stage, ScenarioPhaseOutcome.Started);
        T result;
        try
        {
            result = await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Record(phase, stage, ScenarioPhaseOutcome.Canceled);
            throw;
        }
        catch (Exception)
        {
            Record(phase, stage, ScenarioPhaseOutcome.Failed);
            throw;
        }
        Record(phase, stage, ScenarioPhaseOutcome.Completed, timings?.Invoke(result));
        return result;
    }

    internal ObservedLifetime<T> ObserveLifetime<T>(ScenarioWorkloadPhase phase, T value)
        where T : IAsyncDisposable => new(this, phase, value);

    private void Record(
        ScenarioWorkloadPhase phase, ScenarioPhaseStage stage, ScenarioPhaseOutcome outcome,
        CpuSampleTimings? timings = null)
    {
        lock (_gate)
        {
            if (_entries.Count == MaximumEntries)
            {
                _omitted++;
                return;
            }
            _entries.Add(new ScenarioPhaseEntry(
                phase, stage, outcome, _clock.GetUtcNow(),
                _clock.GetElapsedTime(_started).TotalSeconds,
                timings is null ? null : new ScenarioReturnedSamplerTimings(
                    timings.CaptureDuration.TotalSeconds,
                    timings.SymbolicationDuration.TotalSeconds,
                    timings.AggregationDuration.TotalSeconds,
                    timings.TotalDuration.TotalSeconds)));
        }
    }

    internal sealed class ObservedLifetime<T>(
        ScenarioPhaseRecorder recorder, ScenarioWorkloadPhase phase, T value) : IAsyncDisposable
        where T : IAsyncDisposable
    {
        internal T Value => value;

        public async ValueTask DisposeAsync()
        {
            await recorder.ObserveAsync(phase, ScenarioPhaseStage.Disposal, async () =>
            {
                await value.DisposeAsync().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
    }
}
