using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Core.Requests;

/// <summary>Single-writer state, including bounded tombstones for identities whose rows were dropped.</summary>
internal sealed class RequestActivityState
{
    private readonly SensitiveDataRedactor _redactor = new();
    private readonly Dictionary<string, JointRequestRow> _pending = new(StringComparer.Ordinal);
    private readonly List<JointActivityRow> _activities = [];
    private readonly Dictionary<string, int> _identities = new(StringComparer.Ordinal);
    private long _sequence;
    private long _started, _completed, _droppedRequests, _observed, _droppedActivities;
    private long _invalid, _duplicates, _truncated;
    private bool _identityCapacityReached;
    internal long ParseErrors { get; set; }

    internal void ObserveRequest(EventPipeInFlightRequestCollector.RequestEvent e)
    {
        var request = e.PendingRequest;
        if (!e.IsStart)
        {
            if (_pending.Remove(e.Key)) _completed++;
            return;
        }
        _started++;
        var key = RequestActivityCapture.Key(request!.TraceId, request.SpanId);
        if (key is null)
        {
            _invalid++;
            _droppedRequests++;
            return;
        }
        ObserveIdentity(key);
        if (_pending.ContainsKey(key)) return;
        if (_pending.Count == RequestActivityCapture.MaxRequests)
        {
            _droppedRequests++;
            return;
        }
        var path = request.Path;
        var delimiter = path.IndexOfAny(['?', '#']);
        if (delimiter >= 0) path = path[..delimiter];
        _pending.Add(key, new(NextRow(), request.TraceId, request.SpanId,
            Text(request.Method, RequestActivityCapture.MaxText),
            Text(path, RequestActivityCapture.MaxPath), request.StartedAt, 0));
    }

    internal void ObserveActivity(CapturedActivity activity)
    {
        _observed++;
        var key = RequestActivityCapture.Key(activity.TraceId, activity.SpanId);
        if (key is null) _invalid++;
        else ObserveIdentity(key);
        var validParent = RequestActivityCapture.ValidId(activity.ParentSpanId, 16);
        if (activity.ParentSpanId is not null && !validParent) _invalid++;
        if (_activities.Count == RequestActivityCapture.MaxActivities)
        {
            _droppedActivities++;
            return;
        }
        _activities.Add(new(NextRow(), Text(activity.SourceName, RequestActivityCapture.MaxText),
            Text(activity.OperationName, RequestActivityCapture.MaxText),
            RequestActivityCapture.ValidId(activity.TraceId, 32) ? activity.TraceId : null,
            RequestActivityCapture.ValidId(activity.SpanId, 16) ? activity.SpanId : null,
            validParent ? activity.ParentSpanId : null, activity.StartedAt,
            activity.StoppedAt ?? activity.StartedAt, activity.Duration?.TotalMilliseconds));
    }

    internal RequestActivityCapture Close(string id, int pid, DateTimeOffset? lifetime,
        DateTimeOffset requestedStart, RequestActivityOptions options, DateTimeOffset? start,
        DateTimeOffset end, string completion, long? eventsLost, string lifetimeStatus)
    {
        var requests = _pending.Values.Select(r => r with
        {
            ElapsedMs = Math.Max(0, (end - r.StartedAt).TotalMilliseconds),
        }).ToArray();
        return new(id, pid, lifetime, requestedStart, options.DurationSeconds, start, end,
            options.Sources.ToArray(), requests, _activities.ToArray(), new(_identities, StringComparer.Ordinal),
            new(completion, eventsLost, lifetimeStatus, ParseErrors, _started, _completed,
                _droppedRequests, _observed, _droppedActivities, _invalid, _duplicates,
                _identityCapacityReached, _truncated, _identities.Count, AccountedBytes()));
    }

    // Conservative implementation accounting, not a CLR heap/RSS measurement. Insertion cardinality
    // and string caps imply <4 MiB even at all maxima, including both indices and close-time copies.
    private long AccountedBytes() =>
        128 * 1024L + _identities.Count * (256L + 4 * 49) + _activities.Count * 256L +
        _pending.Values.Sum(r => 512L + 4L * (r.RowId.Length + (r.TraceId?.Length ?? 0) +
            (r.SpanId?.Length ?? 0) + r.Method.Length + r.Path.Length)) +
        _activities.Sum(a => 512L + 4L * (a.RowId.Length + a.Source.Length + a.Operation.Length +
            (a.TraceId?.Length ?? 0) + (a.SpanId?.Length ?? 0) + (a.ParentSpanId?.Length ?? 0)));

    private string NextRow() => (++_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void ObserveIdentity(string key)
    {
        if (_identities.TryGetValue(key, out _))
        {
            _identities[key] = 2;
            _duplicates++;
        }
        else if (_identities.Count < RequestActivityCapture.MaxIdentities) _identities.Add(key, 1);
        else _identityCapacityReached = true;
    }

    private string Text(string text, int cap)
    {
        if (text.Length > cap)
        {
            _truncated++;
            return "<omitted:overlength>";
        }
        var redacted = _redactor.Redact(new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()))!;
        if (redacted.Length <= cap) return redacted;
        _truncated++;
        return "<omitted:overlength>";
    }
}
