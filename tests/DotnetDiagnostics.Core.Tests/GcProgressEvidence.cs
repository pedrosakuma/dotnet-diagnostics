using System.Globalization;
using DotnetDiagnostics.Core.Gc;
using Microsoft.Diagnostics.Tracing;

namespace DotnetDiagnostics.Core.Tests;

// Test-side evidence only. Never compares a target wall-clock heartbeat with EventPipe UTC.
internal sealed class GcProgressEvidence
{
    internal const int MaxRequests = 8;
    internal const int MaxSamples = 32_768;
    private readonly object _gate = new();
    private readonly RequestState?[] _requests = new RequestState[MaxRequests];
    private readonly Dictionary<(int Clr, uint Count), CollectionState> _collections = [];
    private string? _failure;
    private EventPipeEventSource? _source;
    internal TaskCompletionSource Ready { get; } = NewCompletion();

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Attach(EventPipeEventSource source)
    {
        _source = source;
        source.Clr.GCStart += e => Start(e.ClrInstanceID, unchecked((uint)e.Count), e.Type.ToString(),
            Qpc(e), new DateTimeOffset(e.TimeStamp.ToUniversalTime()));
        source.Clr.GCStop += e => Stop(e.ClrInstanceID, unchecked((uint)e.Count),
            Qpc(e), new DateTimeOffset(e.TimeStamp.ToUniversalTime()));
        source.Dynamic.All += e =>
        {
            if (e.ProviderName != "DotnetDiagnostics.GcReadiness") return;
            if ((int)e.ID == 1) { Ready.TrySetResult(); return; }
            var request = Convert.ToInt32(e.PayloadByName("request"), CultureInfo.InvariantCulture);
            Marker((int)e.ID, request, Qpc(e), e.ThreadID,
                (int)e.ID == 3 ? Convert.ToInt32(e.PayloadByName("sequence"), CultureInfo.InvariantCulture) :
                (int)e.ID == 6 ? Convert.ToInt32(e.PayloadByName("count"), CultureInfo.InvariantCulture) : 0,
                (int)e.ID == 6 ? Convert.ToInt32(e.PayloadByName("status"), CultureInfo.InvariantCulture) : 0);
        };
    }

    // TraceEvent discourages this API in favor of floating-point relative milliseconds.
    // This fixture deliberately needs exact integer timestamps from one session's clock.
#pragma warning disable CS0618
    private static long Qpc(TraceEvent e) => e.TimeStampQPC;
#pragma warning restore CS0618

    private RequestState? Request(int id)
    {
        if (id is < 0 or >= MaxRequests) { _failure ??= "request-cap-or-invalid-id"; return null; }
        return _requests[id] ??= new();
    }

    internal async Task AcknowledgeArmAsync(int id, CancellationToken token)
    {
        Task armed;
        lock (_gate) armed = Request(id)?.ArmedSignal.Task ?? throw new ArgumentOutOfRangeException(nameof(id));
        await armed.WaitAsync(TimeSpan.FromSeconds(4), token);
        lock (_gate)
        {
            var request = _requests[id]!;
            if (request.Requested != 0) _failure ??= "request-before-arm-acknowledgment";
            request.Acknowledged = true;
        }
    }

    internal Task WaitForCollectionAsync(int id, CancellationToken token)
    {
        lock (_gate) return _requests[id]!.CollectionSignal.Task.WaitAsync(TimeSpan.FromSeconds(4), token);
    }

    internal Task WaitForCompletionAsync(int id, CancellationToken token)
    {
        lock (_gate) return _requests[id]!.CompletedSignal.Task.WaitAsync(TimeSpan.FromSeconds(4), token);
    }

    internal void Marker(int kind, int id, long qpc, int thread, int value = 0, int status = 0)
    {
        lock (_gate)
        {
            var r = Request(id);
            if (r is null) return;
            switch (kind)
            {
                case 2:
                    if (r.Armed != 0) _failure ??= "duplicate-arm";
                    r.Armed = qpc;
                    r.WorkerThread = thread;
                    break;
                case 3:
                    if (value is < 0 or >= MaxSamples) { _failure ??= "progress-cap"; return; }
                    if (r.Progress[value] != 0) _failure ??= "duplicate-progress";
                    r.Progress[value] = qpc;
                    r.Observed++;
                    if (r.ProgressThread != 0 && r.ProgressThread != thread) _failure ??= "multiple-progress-threads";
                    r.ProgressThread = thread;
                    break;
                case 4:
                    if (r.Requested != 0) _failure ??= "duplicate-request";
                    r.Requested = qpc;
                    r.RequestThread = thread;
                    break;
                case 5:
                    if (r.Returned != 0) _failure ??= "duplicate-return";
                    r.Returned = qpc;
                    break;
                case 6:
                    if (r.Completed != 0) _failure ??= "duplicate-completion";
                    r.Completed = qpc;
                    r.Produced = value;
                    r.Status = status;
                    r.CompletedSignal.TrySetResult();
                    break;
                default: _failure ??= "unknown-witness-event"; break;
            }
            if (r.Armed > 0 && r.Progress[0] > 0) r.ArmedSignal.TrySetResult();
            if (kind != 3) SignalCollections();
        }
    }

    private CollectionState? Collection(int clr, uint count)
    {
        if (_collections.TryGetValue((clr, count), out var existing)) return existing;
        if (_collections.Count == 64) { _failure ??= "collection-cap"; return null; }
        var result = new CollectionState(clr, count);
        _collections.Add((clr, count), result);
        return result;
    }

    internal void Start(int clr, uint count, string type, long qpc, DateTimeOffset utc)
    {
        lock (_gate)
        {
            var c = Collection(clr, count);
            if (c is null) return;
            if (c.Start != 0) _failure ??= "duplicate-gc-start";
            c.Start = qpc; c.StartUtc = utc; c.Type = type;
            SignalCollections();
        }
    }

    internal void Stop(int clr, uint count, long qpc, DateTimeOffset utc)
    {
        lock (_gate)
        {
            var c = Collection(clr, count);
            if (c is null) return;
            if (c.Stop != 0) _failure ??= "duplicate-gc-stop";
            c.Stop = qpc; c.StopUtc = utc;
            SignalCollections();
        }
    }

    // Evaluate timestamps, not callback arrival order; provider buffers may interleave.
    private IEnumerable<CollectionState> MatchingCollections(RequestState r) =>
        _collections.Values.Where(c => r.Requested > 0 && r.Returned >= r.Requested &&
            c.Start >= r.Requested && c.Start <= r.Returned && c.Stop > c.Start);

    private void SignalCollections()
    {
        foreach (var r in _requests)
            if (r is not null && MatchingCollections(r).Any()) r.CollectionSignal.TrySetResult();
    }

    internal Result Evaluate(GcSummary summary, int? transportLost = null)
    {
        lock (_gate)
        {
            var failure = _failure;
            if ((transportLost ?? _source?.EventsLost ?? -1) != 0) failure ??= "transport-loss-or-unknown";
            if (summary.Suspension is not { Completion: "normal-stop", IsAuthoritative: true } ||
                summary.DroppedEvents != 0) failure ??= "incomplete-capture";
            var requests = new List<RequestDiagnostic>();
            var collections = new List<CollectionDiagnostic>();
            var positive = 0;
            for (var id = 0; id < MaxRequests; id++)
            {
                var r = _requests[id];
                if (r is null) continue;
                requests.Add(new(id, r.Armed, r.Requested, r.Returned, r.Completed, r.Produced, r.Observed,
                    r.Status, r.Progress.Where(p => p != 0).DefaultIfEmpty().Min(),
                    r.Progress.Max(), r.Acknowledged));
                if (!r.Acknowledged || r.Armed <= 0 || r.Requested <= r.Armed ||
                    r.Returned < r.Requested || r.Completed <= r.Returned ||
                    r.Progress[0] < r.Armed || r.Progress[0] >= r.Requested ||
                    r.WorkerThread != r.ProgressThread || r.ProgressThread == r.RequestThread)
                    failure ??= "unready-or-invalid-request-lifecycle";
                if (r.Status != 0 || r.Produced is <= 0 or > MaxSamples || r.Produced != r.Observed)
                    failure ??= "witness-deadline-cap-or-loss";
                for (var sequence = 0; sequence < Math.Clamp(r.Produced, 0, MaxSamples); sequence++)
                    if (r.Progress[sequence] < r.Armed || r.Progress[sequence] >= r.Completed ||
                        (sequence > 0 && r.Progress[sequence] <= r.Progress[sequence - 1]))
                        failure ??= "missing-or-invalid-progress-sequence";
                var matches = MatchingCollections(r).ToArray();
                if (matches.Length == 0) failure ??= "missing-request-collection";
                foreach (var c in matches)
                {
                    var published = summary.Events.Where(e => e.ClrInstanceId == c.Clr && e.CollectionCount == c.Count).ToArray();
                    if (published.Length != 1 || published[0].Type != c.Type || published[0].Timestamp != c.StartUtc ||
                        published[0].Timestamp + published[0].CollectionElapsedDuration != c.StopUtc ||
                        c.Stop >= r.Completed)
                        failure ??= "missing-or-mismatched-published-collection";
                    var inside = r.Progress.Count(qpc => qpc > c.Start && qpc < c.Stop);
                    collections.Add(new(id, c.Clr, c.Count, c.Type, c.Start, c.Stop, c.StartUtc, c.StopUtc, inside,
                        r.Progress.Where(qpc => qpc > 0 && qpc <= c.Start).DefaultIfEmpty().Max(),
                        r.Progress.Where(qpc => qpc >= c.Stop).DefaultIfEmpty().Min()));
                    if (c.Type == "BackgroundGC" && inside > 0) positive++;
                }

            }
            if (positive == 0) failure ??= "no-observed-background-progress";
            return new(failure is null, failure, positive, requests, collections);
        }
    }

    internal string Describe()
    {
        lock (_gate)
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                failure = _failure, transportLost = _source?.EventsLost,
                requests = _requests.Select((r, id) => r is null ? null : new RequestDiagnostic(id,
                    r.Armed, r.Requested, r.Returned, r.Completed, r.Produced, r.Observed, r.Status,
                    r.Progress.Where(p => p != 0).DefaultIfEmpty().Min(), r.Progress.Max(), r.Acknowledged)),
                collections = _collections.Values.Select(c => new { c.Clr, c.Count, c.Type, c.Start, c.Stop, c.StartUtc, c.StopUtc }),
            });
    }

    internal sealed record Result(bool ProvesProgress, string? Failure, int BackgroundWithProgress,
        IReadOnlyList<RequestDiagnostic> Requests, IReadOnlyList<CollectionDiagnostic> Collections);
    internal sealed record RequestDiagnostic(int Request, long Armed, long Requested, long Returned, long Completed,
        int Produced, int Observed, int Status, long FirstProgress, long LastProgress, bool Acknowledged);
    internal sealed record CollectionDiagnostic(int Request, int Clr, uint Count, string Type, long Start, long Stop,
        DateTimeOffset RawStartUtc, DateTimeOffset RawStopUtc, int Inside, long LastBeforeStart, long FirstAfterStop);

    private sealed class RequestState
    {
        internal readonly long[] Progress = new long[MaxSamples];
        internal readonly TaskCompletionSource ArmedSignal = NewCompletion();
        internal readonly TaskCompletionSource CollectionSignal = NewCompletion();
        internal readonly TaskCompletionSource CompletedSignal = NewCompletion();
        internal long Armed, Requested, Returned, Completed;
        internal int WorkerThread, ProgressThread, RequestThread, Observed, Produced, Status;
        internal bool Acknowledged;
    }

    private sealed class CollectionState(int clr, uint count)
    {
        internal readonly int Clr = clr;
        internal readonly uint Count = count;
        internal long Start, Stop;
        internal DateTimeOffset StartUtc, StopUtc;
        internal string Type = "";
    }
}
