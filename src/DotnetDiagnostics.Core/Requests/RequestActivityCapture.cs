namespace DotnetDiagnostics.Core.Requests;

/// <summary>Explicit reproduction window. Only exact ActivitySource names are accepted.</summary>
public sealed record RequestActivityOptions(int DurationSeconds, IReadOnlyList<string> Sources)
{
    public void Validate()
    {
        if (DurationSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds), "Duration must be between 1 and 300 seconds.");
        if (Sources is null || Sources.Count is < 1 or > 20 ||
            Sources.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 200 ||
                s.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))))
            throw new ArgumentException("Sources must contain 1-20 exact names of 1-200 ASCII letters, digits, '.', '_' or '-'.", nameof(Sources));
    }
}

public sealed record JointRequestRow(string RowId, string? TraceId, string? SpanId,
    string Method, string Path, DateTimeOffset StartedAt, double ElapsedMs);

/// <summary>Minimal completed Activity evidence; no tags or destination/command data.</summary>
public sealed record JointActivityRow(string RowId, string Source, string Operation,
    string? TraceId, string? SpanId, string? ParentSpanId, DateTimeOffset StartedAt,
    DateTimeOffset StoppedAt, double? DurationMs);

public sealed record RequestActivityQuality(string Completion, long? EventsLost,
    string LifetimeStatus, long ParseErrors, long RequestsStarted, long RequestsCompleted,
    long DroppedRequests, long ActivitiesObserved, long DroppedActivities,
    long InvalidIdentities, long DuplicateIdentities, bool IdentityCapacityReached, long TruncatedFields,
    int RetainedIdentities, long AccountedRetainedBytes);

/// <summary>Relationship is an observed structural basis, never causal/exclusive delay attribution.</summary>
public sealed record RelatedActivity(JointActivityRow Activity, string Relationship,
    bool Verified, string? VerificationWithheldReason);

public sealed record RequestActivityQuery<T>(string CaptureId, int ProcessId, DateTimeOffset? ProcessStartedAt,
    DateTimeOffset? ObservationStart, DateTimeOffset ObservationEnd, RequestActivityQuality Quality,
    int RetainedRequests, int RetainedActivities, int TotalRows, int OmittedRows,
    IReadOnlyList<T> Rows, IReadOnlyList<string> Notes);

/// <summary>One immutable, nonpersistent joint artifact. Queries never reopen a target.</summary>
public sealed class RequestActivityCapture
{
    public const int MaxRequests = 100;
    public const int MaxActivities = 200;
    public const int MaxIdentities = 4096;
    public const int MaxQueryRows = 100;
    public const long MaxAccountedRetainedBytes = 4 * 1024 * 1024;
    internal const int MaxText = 256;
    internal const int MaxPath = 1024;
    private readonly Dictionary<string, int> _identities;
    private readonly Dictionary<string, JointActivityRow> _spans;
    private readonly IReadOnlyList<JointActivityRow> _activities;
    public string CaptureId { get; }
    public int ProcessId { get; }
    public DateTimeOffset? ProcessStartedAt { get; }
    public DateTimeOffset RequestedStart { get; }
    public int RequestedDurationSeconds { get; }
    public DateTimeOffset? ObservationStart { get; }
    public DateTimeOffset ObservationEnd { get; }
    public IReadOnlyList<string> Sources { get; }
    public IReadOnlyList<JointRequestRow> Requests { get; }
    public int RetainedActivities => _activities.Count;
    public RequestActivityQuality Quality { get; }
    public IReadOnlyList<string> Notes { get; } =
    [
        "Only requests observed starting and still pending at close are retained; first accepted subset, not oldest guarantee.",
        "Only completed ActivitySource Stop records are retained. Empty results do not establish absence of open or uninstrumented operations.",
        "Relationships are observed identifiers, not causality, critical path, exclusive delay, or complete trace coverage. Do not sum concurrent durations.",
        "One union session avoids internal competing filters; coexistence with external DiagnosticSource sessions is not established.",
        "Paths, operation names and correlation IDs may contain sensitive application data. No tags, query strings, SQL, headers or external telemetry are collected.",
        "Limits: MaxRequests=100, MaxActivities=200, MaxIdentities=4096, MaxQueryRows=100, MaxText=256, MaxPath=1024. See quality counters for omissions."
    ];

    internal RequestActivityCapture(string id, int pid, DateTimeOffset? processStart,
        DateTimeOffset requestedStart, int duration, DateTimeOffset? start, DateTimeOffset end,
        IReadOnlyList<string> sources, IReadOnlyList<JointRequestRow> requests,
        IReadOnlyList<JointActivityRow> activities, Dictionary<string, int> identities, RequestActivityQuality quality)
    {
        CaptureId = id; ProcessId = pid; ProcessStartedAt = processStart;
        RequestedStart = requestedStart; RequestedDurationSeconds = duration;
        ObservationStart = start; ObservationEnd = end; Sources = sources;
        Requests = requests; _activities = activities; _identities = identities; Quality = quality;
        _spans = activities.Where(a => Key(a.TraceId, a.SpanId) is not null)
            .GroupBy(a => Key(a.TraceId, a.SpanId)!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    }

    public RequestActivityQuery<JointRequestRow> QueryRequests(string captureId, int topN = 100)
    {
        ValidateQuery(captureId, topN);
        return Project(Requests, topN);
    }

    public RequestActivityQuery<RelatedActivity> QueryRelated(string captureId, string requestRowId, int topN = 100)
    {
        ValidateQuery(captureId, topN);
        var request = Requests.SingleOrDefault(r => r.RowId == requestRowId)
            ?? throw new ArgumentException("Request row is unavailable in this capture.", nameof(requestRowId));
        var rows = _activities.Where(a => request.TraceId is not null && a.TraceId == request.TraceId)
            .Select(a => Relate(request, a)).ToArray();
        return Project(rows, topN);
    }

    private RelatedActivity Relate(JointRequestRow request, JointActivityRow activity)
    {
        var structural = "same-trace-only";
        var reason = Quality.LifetimeStatus != "known" ? $"Process lifetime {Quality.LifetimeStatus}."
            : ObservationStart is null ? "Session window provenance unavailable."
            : Quality.Completion != "normal" || Quality.EventsLost != 0 ? "Stream incomplete or source loss unavailable/nonzero."
            : Quality.ParseErrors != 0 ? "Event parsing failed."
            : Quality.IdentityCapacityReached ? "MaxIdentities reached; duplicate detection incomplete." : null;
        var requestKey = Key(request.TraceId, request.SpanId);
        var current = activity;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var chainReason = "No retained unambiguous parent chain to the selected request.";
        for (var depth = 0; depth <= MaxActivities; depth++)
        {
            var key = Key(current.TraceId, current.SpanId);
            if (key is null || !visited.Add(key))
            {
                chainReason = "Missing/invalid identity or cyclic chain.";
                break;
            }
            if (!_identities.TryGetValue(key, out var count) || count != 1)
                reason ??= "Duplicate/conflicting span identity in the observed chain.";
            if (ObservationStart is not { } start || current.StartedAt < start ||
                current.StartedAt < request.StartedAt || current.StoppedAt > ObservationEnd ||
                current.StoppedAt < current.StartedAt)
                reason ??= "Span lies outside the observed request/capture window.";
            var parentKey = Key(current.TraceId, current.ParentSpanId);
            if (requestKey is not null && parentKey == requestKey)
            {
                structural = depth == 0 ? "direct-child" : "observed-descendant";
                if (!_identities.TryGetValue(requestKey, out var requestCount) || requestCount != 1)
                    reason ??= "Duplicate/conflicting selected request identity.";
                return new(activity, structural, reason is null, reason);
            }
            if (parentKey is null || !_spans.TryGetValue(parentKey, out var parent)) break;
            current = parent;
        }
        return new(activity, structural, false, reason ?? chainReason);
    }

    private void ValidateQuery(string captureId, int topN)
    {
        if (captureId != CaptureId) throw new ArgumentException("Capture identity does not match this artifact.", nameof(captureId));
        if (topN is < 1 or > MaxQueryRows) throw new ArgumentOutOfRangeException(nameof(topN));
    }

    private RequestActivityQuery<T> Project<T>(IReadOnlyList<T> rows, int topN) =>
        new(CaptureId, ProcessId, ProcessStartedAt, ObservationStart, ObservationEnd, Quality,
            Requests.Count, RetainedActivities, rows.Count, Math.Max(0, rows.Count - topN),
            rows.Take(topN).ToArray(), Notes);

    internal static bool ValidId(string? value, int length) =>
        value is not null && value.Length == length && value.Any(c => c != '0') &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string? Key(string? trace, string? span) =>
        ValidId(trace, 32) && ValidId(span, 16) ? trace + "/" + span : null;
}
