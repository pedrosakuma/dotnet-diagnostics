using DotnetDiagnostics.Core.CaptureRecording;
using F = DotnetDiagnostics.Core.CaptureRecording.CaptureObservationField;

namespace DotnetDiagnostics.Core.Gc;

/// <summary>Single-parser-thread bounded state; no event clones, TTL, or inferred endpoints.</summary>
internal sealed class GcCaptureState
{
    internal const int MaxPendingCollections = 256;
    internal const int MaxSuspensionStates = 128;
    internal const int MaxRuntimeIdentities = 16;
    internal const int MaxRetainedIntervals = 100_000;
    private readonly int _cap;
    private readonly Dictionary<(int Clr, uint Count), CollectionStart> _collections = [];
    private readonly Dictionary<(int Clr, int Thread), SuspensionState> _suspensions = [];
    private readonly HashSet<int> _runtimes = [];
    private readonly HashSet<int> _ignoredReasons = [];
    private readonly Dictionary<string, long> _limitations = new(StringComparer.Ordinal);
    private readonly List<GcSuspensionInterval> _intervals = [];
    private long _observed;
    private long _dropped;
    private long _totalTicks;
    private long _maxTicks;
    private DateTimeOffset? _lastPauseEnd;
    private bool _unreliable;
    private bool _sawBoundary;
    private readonly ICaptureObservationSink? _sink;

    internal GcCaptureState(int cap, ICaptureObservationSink? sink = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cap, MaxRetainedIntervals);
        _cap = cap;
        _sink = sink;
        Collections = new GcEventAggregation(cap, sink);
    }

    internal GcEventAggregation Collections { get; }

    private void Count(string category, bool unreliable = false)
    {
        _limitations.TryGetValue(category, out var count);
        _limitations[category] = count == long.MaxValue ? count : count + 1;
        _unreliable |= unreliable;
        _sink?.TryAppend(new("gc.correlation", null, null, category,
            [F.Bool("affectsSuspensionReliability", unreliable), F.String("timing", "not-associated-with-a-reliable-interval")]));
    }

    private bool Identity(int clr, int version)
    {
        if (version < 1)
        {
            Count("missing-clr-identity", true);
            return false;
        }
        if (!_runtimes.Contains(clr) && _runtimes.Count >= MaxRuntimeIdentities)
        {
            Count("runtime-identity-cap-16", true);
            return false;
        }
        _runtimes.Add(clr);
        return true;
    }

    internal void CollectionBegin(int clr, uint count, int version, DateTimeOffset at, int generation, string reason, string type)
    {
        if (!Identity(clr, version)) return;
        var key = (clr, count);
        if (_collections.Remove(key)) Count("conflicting-collection-begin");
        if (_collections.Count >= MaxPendingCollections)
        {
            Count("pending-collection-cap-256");
            return;
        }
        _collections.Add(key, new(at, generation, reason, type));
    }

    internal void CollectionEnd(int clr, uint count, int version, DateTimeOffset at)
    {
        if (!Identity(clr, version)) return;
        if (!_collections.Remove((clr, count), out var start))
        {
            Count("orphan-collection-end");
            return;
        }
        if (at < start.At)
        {
            Count("regressing-collection-time");
            return;
        }
        Collections.Add(new(start.At, start.Generation, start.Reason, start.Type, at - start.At, clr, count));
    }

    internal void SuspendBegin(int clr, int thread, int version, DateTimeOffset at, int reason, uint count)
    {
        if (!Identity(clr, version)) return;
        _sawBoundary = true;
        var key = (clr, thread);
        if (_suspensions.Remove(key, out var previous))
            Count(previous.Closed ? "missing-restart-stop" : "conflicting-suspend-begin", !previous.Closed && previous.IsGc);
        if (thread <= 0) { Count("missing-thread-identity", true); return; }
        if (_suspensions.Count >= MaxSuspensionStates) { Count("suspension-state-cap-128", true); return; }
        if (reason is not (1 or 6))
        {
            Count("ignored-non-gc-suspension");
            if (_ignoredReasons.Count < 16 || _ignoredReasons.Contains(reason)) _ignoredReasons.Add(reason);
            else Count("ignored-reason-cap-16");
        }
        _suspensions.Add(key, new(at, reason, count));
    }

    // phase: 0=suspend stop, 1=restart start, 2=restart stop.
    internal void Boundary(int clr, int thread, int version, DateTimeOffset at, int phase)
    {
        if (!Identity(clr, version)) return;
        _sawBoundary = true;
        var key = (clr, thread);
        if (!_suspensions.TryGetValue(key, out var state))
        {
            Count("orphan-suspension-boundary", true);
            return;
        }
        if (at < state.Last || phase != state.NextPhase)
        {
            _suspensions.Remove(key);
            Count("retry-or-invalid-phase", state.IsGc);
            return;
        }
        state.Last = at;
        state.NextPhase++;
        if (phase == 0) state.SuspendedAt = at;
        if (phase == 1)
        {
            state.Closed = true;
            if (state.IsGc)
            {
                var start = state.SuspendedAt;
                if (_lastPauseEnd is { } end && start < end) Count("overlapping-runtime-suspension", true);
                var ticks = (at - start).Ticks;
                _totalTicks = ticks > long.MaxValue - _totalTicks ? long.MaxValue : _totalTicks + ticks;
                _maxTicks = Math.Max(_maxTicks, ticks);
                _lastPauseEnd = at;
                _observed = _observed == long.MaxValue ? _observed : _observed + 1;
                _sink?.TryAppend(new("gc.suspension", at, thread, null,
                [
                    RuntimeObservationProjection.Time("startedAt", start),
                    F.Int64("durationTicks", ticks), F.Int64("acquisitionTicks", (start - state.Begin).Ticks),
                    F.Int64("clrInstanceId", clr), F.Int64("reason", state.Reason),
                    F.Int64("gcCountAtSuspend", state.Count),
                    F.String("correlation", "ordered-runtime-thread-boundaries; gcCountAtSuspend is not a collection join"),
                    F.String("quality", "local pair only; capture-wide loss and correlation limits apply"),
                ]));
                if (_intervals.Count < _cap)
                {
                    state.RetainedIndex = _intervals.Count;
                    _intervals.Add(new(start, at, clr, thread, state.Reason, state.Count, start - state.Begin));
                }
                else _dropped = _dropped == long.MaxValue ? _dropped : _dropped + 1;
            }
            state.RestartAt = at;
        }
        if (phase == 2)
        {
            if (state.IsGc)
                _sink?.TryAppend(new("gc.restart", at, thread, null,
                [
                    F.Int64("clrInstanceId", clr), F.Int64("gcCountAtSuspend", state.Count),
                    RuntimeObservationProjection.Time("restartStartedAt", state.RestartAt),
                    F.Int64("restartTicks", (at - state.RestartAt).Ticks),
                    F.String("correlation", "ordered-runtime-thread-boundaries"),
                ]));
            if (state.RetainedIndex is { } index)
                _intervals[index] = _intervals[index] with { RestartDuration = at - state.RestartAt };
            _suspensions.Remove(key);
        }
    }

    internal GcSuspensionEvidence Finish(DateTimeOffset start, DateTimeOffset end, DateTimeOffset? processStart,
        long eventsLost = 0, string completion = "normal-stop")
    {
        foreach (var state in _suspensions.Values.Where(s => s.IsGc))
            Count(state.Closed ? "missing-restart-stop" : "right-censored-suspension", !state.Closed);
        foreach (var _ in _collections) Count("right-censored-collection");
        if (eventsLost > 0) { _limitations["transport-events-lost"] = eventsLost; _unreliable = true; }
        if (Collections.ObservedCollections > int.MaxValue) Count("legacy-collection-count-saturated");
        if (completion != "normal-stop") Count("incomplete-processing", true);
        if (_runtimes.Count > 1) Count("multiple-clr-instances", true);
        if (end <= start) Count("invalid-observation-window", true);
        if (_intervals.Any(p => p.StartedAt < start || p.StoppedAt > end)) Count("boundary-outside-window", true);
        var status = _unreliable ? "unreliable" : !_sawBoundary ? "unavailable-no-boundaries" : "no-detected-loss";
        var authoritative = status == "no-detected-loss";
        return new(status, start, end, processStart, completion,
            authoritative ? TimeSpan.FromTicks(_totalTicks) : null,
            authoritative ? TimeSpan.FromTicks(_maxTicks) : null,
            _observed, _dropped, _intervals.ToArray(), new Dictionary<string, long>(_limitations))
        {
            IgnoredReasons = _ignoredReasons.Order().ToArray(),
            ObservedCollectionPairs = Collections.ObservedCollections,
        };
    }

    private sealed record CollectionStart(DateTimeOffset At, int Generation, string Reason, string Type);
    private sealed class SuspensionState(DateTimeOffset begin, int reason, uint count)
    {
        internal DateTimeOffset Begin { get; } = begin;
        internal DateTimeOffset Last { get; set; } = begin;
        internal int Reason { get; } = reason;
        internal uint Count { get; } = count;
        internal bool IsGc => Reason is 1 or 6;
        internal DateTimeOffset SuspendedAt { get; set; }
        internal DateTimeOffset RestartAt { get; set; }
        internal int NextPhase { get; set; }
        internal bool Closed { get; set; }
        internal int? RetainedIndex { get; set; }
    }
}
