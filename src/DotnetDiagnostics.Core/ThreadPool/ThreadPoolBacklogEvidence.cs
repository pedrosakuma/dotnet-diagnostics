using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Counters;

namespace DotnetDiagnostics.Core.ThreadPool;

/// <summary>Availability of bounded ThreadPool backlog evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThreadPoolBacklogAvailability>))]
public enum ThreadPoolBacklogAvailability
{
    [JsonStringEnumMemberName("available")]
    Available,

    [JsonStringEnumMemberName("insufficient_observations")]
    InsufficientObservations,

    [JsonStringEnumMemberName("loss_detected")]
    LossDetected,

    [JsonStringEnumMemberName("unsupported_approximation")]
    UnsupportedApproximation,
}

/// <summary>Why bounded ThreadPool backlog evidence abstained.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThreadPoolBacklogAbstentionReason>))]
public enum ThreadPoolBacklogAbstentionReason
{
    [JsonStringEnumMemberName("eventpipe_loss")]
    EventPipeLoss,

    [JsonStringEnumMemberName("dispatch_loss")]
    DispatchLoss,

    [JsonStringEnumMemberName("processing_failure")]
    ProcessingFailure,

    [JsonStringEnumMemberName("retention_truncated")]
    RetentionTruncated,

    [JsonStringEnumMemberName("irregular_cadence")]
    IrregularCadence,

    [JsonStringEnumMemberName("insufficient_windows")]
    InsufficientWindows,
}

/// <summary>Whether a runtime reason is known from the evaluated capture.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThreadPoolBacklogReasonState>))]
public enum ThreadPoolBacklogReasonState
{
    [JsonStringEnumMemberName("known")]
    Known,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

/// <summary>Caller-declared protocol for bounded ThreadPool backlog evidence.</summary>
public sealed record ThreadPoolBacklogProtocol(
    double QueueLevel,
    int MinimumWindows = ThreadPoolBacklogProtocol.DefaultMinimumWindows,
    int DrainTailWindows = ThreadPoolBacklogProtocol.DefaultDrainTailWindows)
{
    public const int DefaultMinimumWindows = 4;
    public const int DefaultDrainTailWindows = 2;
    public const int MaximumWindows = 64;
    public const double CadenceToleranceRatio = 0.25;

    public void Validate()
    {
        if (!double.IsFinite(QueueLevel) || QueueLevel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(QueueLevel), "Queue level must be finite and non-negative.");
        }

        if (MinimumWindows is < 2 or > MaximumWindows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumWindows),
                $"Minimum windows must be between 2 and {MaximumWindows}.");
        }

        if (DrainTailWindows is < 1 || DrainTailWindows > MinimumWindows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DrainTailWindows),
                "Drain-tail windows must be positive and no greater than the minimum window count.");
        }
    }
}

/// <summary>One non-overlapping bounded counter window.</summary>
public sealed record ThreadPoolBacklogWindow(
    DateTimeOffset Timestamp,
    double DurationSeconds,
    double Value,
    double? CollectorP95 = null);

/// <summary>Optional runtime reason and its capture provenance.</summary>
public sealed record ThreadPoolBacklogReason(
    string? Value,
    string Provenance,
    string? CaptureId,
    DateTimeOffset? ObservedAt);

/// <summary>Validated runtime-reason state for this capture.</summary>
public sealed record ThreadPoolBacklogReasonEvidence(
    ThreadPoolBacklogReasonState State,
    string? Value,
    string Provenance,
    string? CaptureId,
    DateTimeOffset? ObservedAt,
    bool SameCapture);

/// <summary>Descriptive movement of one independently observed counter series.</summary>
public sealed record ThreadPoolBacklogMovement(
    double First,
    double Last,
    double Delta,
    double Median,
    double TheilSenSlopePerSecond);

/// <summary>An optional result field that the collector could not supply.</summary>
public sealed record ThreadPoolBacklogUnavailableField(string Field, string Reason);

/// <summary>Input to the deterministic bounded ThreadPool backlog analyzer.</summary>
public sealed record ThreadPoolBacklogEvidenceRequest(
    string CaptureId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    ThreadPoolBacklogProtocol Protocol,
    IReadOnlyList<ThreadPoolBacklogWindow> QueueWindows,
    IReadOnlyList<ThreadPoolBacklogWindow> WorkerWindows,
    ThreadPoolBacklogReason Reason,
    long? EventPipeEventsLost = 0,
    long DroppedObservations = 0,
    long TruncatedWindows = 0,
    bool ProcessingFailed = false);

/// <summary>
/// Objective bounded ThreadPool backlog evidence. This record never emits a starvation verdict.
/// </summary>
public sealed record ThreadPoolBacklogEvidence(
    string Schema,
    string CaptureId,
    ThreadPoolBacklogProtocol Protocol,
    ThreadPoolBacklogAvailability Availability,
    IReadOnlyList<ThreadPoolBacklogAbstentionReason> AbstentionReasons,
    int QueueWindowCount,
    int WorkerWindowCount,
    int? WindowsAboveLevel,
    double? ProportionAboveLevel,
    double? LongestContiguousDurationAboveLevelSeconds,
    bool? DrainedAtTail,
    ThreadPoolBacklogMovement? QueueMovement,
    ThreadPoolBacklogMovement? WorkerMovement,
    double? CollectorQueueP95Median,
    IReadOnlyList<ThreadPoolBacklogUnavailableField> UnavailableFields,
    ThreadPoolBacklogReasonEvidence Reason)
{
    public const string SchemaV1 = "dotnet-diagnostics/threadpool-backlog-evidence/v1";
}

/// <summary>Deterministic analysis over bounded non-overlapping ThreadPool counter windows.</summary>
public static class ThreadPoolBacklogAnalyzer
{
    public static ThreadPoolBacklogEvidence Evaluate(ThreadPoolBacklogEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CaptureId);
        request.Protocol.Validate();
        if (request.EndedAt < request.StartedAt)
        {
            throw new ArgumentException("Capture end must not precede capture start.", nameof(request));
        }

        var queue = request.QueueWindows.OrderBy(static window => window.Timestamp).ToArray();
        var workers = request.WorkerWindows.OrderBy(static window => window.Timestamp).ToArray();
        var abstention = new List<ThreadPoolBacklogAbstentionReason>(6);
        if (request.EventPipeEventsLost is null or > 0)
        {
            abstention.Add(ThreadPoolBacklogAbstentionReason.EventPipeLoss);
        }
        if (request.DroppedObservations > 0)
        {
            abstention.Add(ThreadPoolBacklogAbstentionReason.DispatchLoss);
        }
        if (request.ProcessingFailed)
        {
            abstention.Add(ThreadPoolBacklogAbstentionReason.ProcessingFailure);
        }
        if (request.TruncatedWindows > 0)
        {
            abstention.Add(ThreadPoolBacklogAbstentionReason.RetentionTruncated);
        }
        if (!HasRegularCadence(queue) || !HasRegularCadence(workers))
        {
            abstention.Add(ThreadPoolBacklogAbstentionReason.IrregularCadence);
        }
        if (queue.Length < request.Protocol.MinimumWindows
            || workers.Length < request.Protocol.MinimumWindows)
        {
            abstention.Add(ThreadPoolBacklogAbstentionReason.InsufficientWindows);
        }

        var availability = abstention.Contains(ThreadPoolBacklogAbstentionReason.EventPipeLoss)
            || abstention.Contains(ThreadPoolBacklogAbstentionReason.DispatchLoss)
            || abstention.Contains(ThreadPoolBacklogAbstentionReason.ProcessingFailure)
                ? ThreadPoolBacklogAvailability.LossDetected
                : abstention.Contains(ThreadPoolBacklogAbstentionReason.RetentionTruncated)
                    || abstention.Contains(ThreadPoolBacklogAbstentionReason.IrregularCadence)
                    ? ThreadPoolBacklogAvailability.UnsupportedApproximation
                    : abstention.Contains(ThreadPoolBacklogAbstentionReason.InsufficientWindows)
                        ? ThreadPoolBacklogAvailability.InsufficientObservations
                        : ThreadPoolBacklogAvailability.Available;

        var reason = ValidateReason(request);
        var unavailableFields = new List<ThreadPoolBacklogUnavailableField>();
        double? collectorP95 = queue.Length > 0 && queue.All(static window => window.CollectorP95.HasValue)
            ? Median(queue.Select(static window => window.CollectorP95!.Value).ToArray())
            : null;
        if (collectorP95 is null)
        {
            unavailableFields.Add(new(
                "collectorQueueP95Median",
                "The collector did not provide a queue p95 for every retained window."));
        }

        if (availability != ThreadPoolBacklogAvailability.Available)
        {
            unavailableFields.Add(new(
                "decisionFields",
                $"Unavailable because availability is {AvailabilityName(availability)}."));
            return new(
                ThreadPoolBacklogEvidence.SchemaV1,
                request.CaptureId,
                request.Protocol,
                availability,
                abstention,
                queue.Length,
                workers.Length,
                null,
                null,
                null,
                null,
                null,
                null,
                collectorP95,
                unavailableFields,
                reason);
        }

        var windowsAbove = queue.Count(window => window.Value >= request.Protocol.QueueLevel);
        var proportionAbove = (double)windowsAbove / queue.Length;
        var longestDuration = LongestContiguousDuration(queue, request.Protocol.QueueLevel);
        var tail = queue.TakeLast(request.Protocol.DrainTailWindows).ToArray();
        var drainedAtTail = windowsAbove > 0
            && tail.All(window => window.Value < request.Protocol.QueueLevel);

        return new(
            ThreadPoolBacklogEvidence.SchemaV1,
            request.CaptureId,
            request.Protocol,
            availability,
            abstention,
            queue.Length,
            workers.Length,
            windowsAbove,
            proportionAbove,
            longestDuration,
            drainedAtTail,
            Movement(queue),
            Movement(workers),
            collectorP95,
            unavailableFields,
            reason);
    }

    private static bool HasRegularCadence(ThreadPoolBacklogWindow[] windows)
    {
        if (windows.Any(static window =>
                !double.IsFinite(window.DurationSeconds)
                || window.DurationSeconds <= 0
                || !double.IsFinite(window.Value)
                || window.Value < 0
                || window.CollectorP95 is double p95 && (!double.IsFinite(p95) || p95 < 0)))
        {
            return false;
        }

        for (var index = 1; index < windows.Length; index++)
        {
            var expected = windows[index - 1].DurationSeconds;
            var actual = (windows[index].Timestamp - windows[index - 1].Timestamp).TotalSeconds;
            var tolerance = Math.Max(0.1, expected * ThreadPoolBacklogProtocol.CadenceToleranceRatio);
            if (!double.IsFinite(actual)
                || actual <= 0
                || Math.Abs(actual - expected) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static ThreadPoolBacklogReasonEvidence ValidateReason(
        ThreadPoolBacklogEvidenceRequest request)
    {
        var runtimeObserved = string.Equals(
            request.Reason.Provenance,
            ThreadPoolEvidence.RuntimeObserved,
            StringComparison.Ordinal);
        var sameCapture = runtimeObserved
            && !string.IsNullOrWhiteSpace(request.Reason.Value)
            && string.Equals(request.Reason.CaptureId, request.CaptureId, StringComparison.Ordinal)
            && request.Reason.ObservedAt is { } observedAt
            && observedAt >= request.StartedAt
            && observedAt <= request.EndedAt;
        return new(
            sameCapture ? ThreadPoolBacklogReasonState.Known : ThreadPoolBacklogReasonState.Unknown,
            sameCapture ? request.Reason.Value : null,
            request.Reason.Provenance,
            request.Reason.CaptureId,
            request.Reason.ObservedAt,
            sameCapture);
    }

    private static ThreadPoolBacklogMovement Movement(
        ThreadPoolBacklogWindow[] windows)
    {
        var firstTimestamp = windows[0].Timestamp;
        var slopes = new List<double>(windows.Length * (windows.Length - 1) / 2);
        for (var left = 0; left < windows.Length - 1; left++)
        {
            var leftX = (windows[left].Timestamp - firstTimestamp).TotalSeconds;
            for (var right = left + 1; right < windows.Length; right++)
            {
                var rightX = (windows[right].Timestamp - firstTimestamp).TotalSeconds;
                var deltaX = rightX - leftX;
                if (deltaX > 0)
                {
                    slopes.Add((windows[right].Value - windows[left].Value) / deltaX);
                }
            }
        }

        var first = windows[0].Value;
        var last = windows[^1].Value;
        return new(
            first,
            last,
            last - first,
            Median(windows.Select(static window => window.Value).ToArray()),
            Median(slopes.ToArray()));
    }

    private static double LongestContiguousDuration(
        ThreadPoolBacklogWindow[] windows,
        double queueLevel)
    {
        var longest = 0d;
        var current = 0d;
        for (var index = 0; index < windows.Length; index++)
        {
            if (windows[index].Value < queueLevel)
            {
                current = 0;
                continue;
            }

            var contiguous = index > 0
                && windows[index - 1].Value >= queueLevel
                && Math.Abs(
                    (windows[index].Timestamp - windows[index - 1].Timestamp).TotalSeconds
                    - windows[index - 1].DurationSeconds)
                    <= Math.Max(
                        0.1,
                        windows[index - 1].DurationSeconds
                        * ThreadPoolBacklogProtocol.CadenceToleranceRatio);
            current = contiguous
                ? current + windows[index].DurationSeconds
                : windows[index].DurationSeconds;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }

    private static string AvailabilityName(ThreadPoolBacklogAvailability availability)
        => availability switch
        {
            ThreadPoolBacklogAvailability.Available => "available",
            ThreadPoolBacklogAvailability.InsufficientObservations => "insufficient_observations",
            ThreadPoolBacklogAvailability.LossDetected => "loss_detected",
            ThreadPoolBacklogAvailability.UnsupportedApproximation => "unsupported_approximation",
            _ => throw new ArgumentOutOfRangeException(nameof(availability)),
        };
}

/// <summary>
/// Bounded capture-local accumulator shared by finite counter collection and the CLI JSONL stream.
/// </summary>
public sealed class ThreadPoolBacklogAccumulator
{
    private readonly object _gate = new();
    private readonly string _captureId;
    private readonly DateTimeOffset _startedAt;
    private readonly ThreadPoolBacklogProtocol _protocol;
    private readonly List<ThreadPoolBacklogWindow> _queue = [];
    private readonly List<ThreadPoolBacklogWindow> _workers = [];
    private long _truncatedWindows;

    public ThreadPoolBacklogAccumulator(
        string captureId,
        DateTimeOffset startedAt,
        ThreadPoolBacklogProtocol protocol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        ArgumentNullException.ThrowIfNull(protocol);
        protocol.Validate();
        _captureId = captureId;
        _startedAt = startedAt;
        _protocol = protocol;
    }

    public void Observe(DateTimeOffset timestamp, CounterValue counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        if (!string.Equals(counter.Provider, "System.Runtime", StringComparison.Ordinal)
            || counter.Name is not ("threadpool-queue-length" or "threadpool-thread-count"))
        {
            return;
        }

        var window = new ThreadPoolBacklogWindow(
            timestamp,
            counter.IntervalSec ?? double.NaN,
            counter.Value);
        lock (_gate)
        {
            var target = counter.Name == "threadpool-queue-length" ? _queue : _workers;
            if (target.Count >= ThreadPoolBacklogProtocol.MaximumWindows)
            {
                _truncatedWindows++;
                return;
            }

            target.Add(window);
        }
    }

    public ThreadPoolBacklogEvidence Complete(
        DateTimeOffset endedAt,
        long? eventPipeEventsLost,
        long droppedObservations,
        bool processingFailed = false,
        ThreadPoolBacklogReason? reason = null)
    {
        ThreadPoolBacklogWindow[] queue;
        ThreadPoolBacklogWindow[] workers;
        long truncated;
        lock (_gate)
        {
            queue = _queue.ToArray();
            workers = _workers.ToArray();
            truncated = _truncatedWindows;
        }

        return ThreadPoolBacklogAnalyzer.Evaluate(new(
            _captureId,
            _startedAt,
            endedAt,
            _protocol,
            queue,
            workers,
            reason ?? new ThreadPoolBacklogReason(
                null,
                ThreadPoolEvidence.Missing,
                null,
                null),
            eventPipeEventsLost,
            droppedObservations,
            truncated,
            processingFailed));
    }
}
