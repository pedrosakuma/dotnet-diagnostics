namespace DotnetDiagnostics.Core.Activities;

/// <summary>Single-writer bounded stop-event retention, shared by the callback and deterministic tests.</summary>
internal sealed class ActivityRetentionState
{
    private readonly List<CapturedActivity> _activities;
    private readonly string? _traceId;
    private readonly int _cap;
    private int _matching;
    private int _nonMatching;
    private int _dropped;

    internal ActivityRetentionState(int maxActivities, string? traceId, int maxMatchedActivities)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxActivities, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMatchedActivities, 1);
        if (traceId is not null)
        {
            if (!ActivityTraceProjector.TryNormalizeTraceId(traceId, out var normalized))
            {
                throw new ArgumentException("traceId must be a non-zero 32-hex W3C trace-id.", nameof(traceId));
            }

            _traceId = normalized;
        }

        _cap = _traceId is null ? maxActivities : maxMatchedActivities;
        _activities = new List<CapturedActivity>(Math.Min(_cap, 256));
    }

    internal IReadOnlyList<CapturedActivity> Activities => _activities;
    internal int ObservedActivities => _matching + _nonMatching;
    internal ActivityRetention Retention => new(
        _traceId, _cap, ObservedActivities, _matching, _activities.Count, _dropped, _nonMatching);

    internal void Observe(CapturedActivity activity)
    {
        if (_traceId is not null && !string.Equals(_traceId, activity.TraceId, StringComparison.OrdinalIgnoreCase))
        {
            _nonMatching++;
            return;
        }

        _matching++;
        if (_activities.Count < _cap)
        {
            _activities.Add(activity);
        }
        else
        {
            _dropped++;
        }
    }
}
