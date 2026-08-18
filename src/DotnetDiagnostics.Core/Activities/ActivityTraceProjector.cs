using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Core.Activities;

/// <summary>
/// Projects one W3C trace from a retained <see cref="ActivityCapture"/> into a deterministic
/// parent/child forest. The projection is completed-span-only and cannot prove trace completeness
/// outside the bounded capture window.
/// </summary>
public static class ActivityTraceProjector
{
    private const int TraceIdLength = 32;
    private const int SpanIdLength = 16;
    private const int MaxSafeTagValueLength = 128;

    private static readonly string[] SafeTagKeys =
    [
        "activity.type",
        "db.system",
        "http.method",
        "http.request.method",
        "http.response.status_code",
        "http.status_code",
        "messaging.system",
        "network.protocol.name",
        "network.protocol.version",
        "otel.status_code",
        "rpc.system",
    ];

    /// <summary>
    /// Returns a bounded wire projection. <paramref name="topN"/> caps returned span rows; all
    /// retained matching spans still participate in hierarchy and timing calculations.
    /// </summary>
    public static ActivityTraceProjection Project(
        ActivityCapture capture,
        string traceId,
        int topN,
        SensitiveDataRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(redactor);
        if (topN < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(topN), "topN must be >= 1.");
        }

        if (!TryNormalizeTraceId(traceId, out var normalizedTraceId))
        {
            throw new ArgumentException(
                "traceId must be a non-zero 32-hex W3C trace-id.",
                nameof(traceId));
        }

        var warnings = new List<string>
        {
            "Completed-only semantics: Activity EventPipe capture retains stop events, so in-flight spans that did not stop during the window are absent.",
            "Capture-window limitation: spans that completed before collection started or after it ended are absent; this projection cannot claim the full trace is complete.",
        };

        var matchedActivities = 0;
        var incompleteIntervals = 0;
        var working = new List<WorkingSpan>();
        for (var ordinal = 0; ordinal < capture.Activities.Count; ordinal++)
        {
            var activity = capture.Activities[ordinal];
            if (!string.Equals(activity.TraceId, normalizedTraceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matchedActivities++;
            if (activity.StoppedAt is not { } stoppedAt ||
                activity.Duration is not { } duration ||
                duration < TimeSpan.Zero ||
                stoppedAt < activity.StartedAt)
            {
                incompleteIntervals++;
                continue;
            }

            working.Add(new WorkingSpan(activity, ordinal));
        }

        working.Sort(WorkingSpanComparer.Instance);

        var bySpanId = new Dictionary<string, WorkingSpan>(StringComparer.OrdinalIgnoreCase);
        var missingSpanIds = 0;
        var malformedSpanIds = 0;
        var duplicateSpanIds = 0;
        foreach (var span in working)
        {
            if (string.IsNullOrWhiteSpace(span.SpanId))
            {
                span.SpanIdStatus = ActivityTraceSpanIdStatus.Missing;
                missingSpanIds++;
                continue;
            }

            if (!IsValidSpanId(span.SpanId))
            {
                span.SpanIdStatus = ActivityTraceSpanIdStatus.Malformed;
                malformedSpanIds++;
                continue;
            }

            if (!bySpanId.TryAdd(span.SpanId, span))
            {
                span.SpanIdStatus = ActivityTraceSpanIdStatus.Duplicate;
                duplicateSpanIds++;
                continue;
            }

            span.SpanIdStatus = ActivityTraceSpanIdStatus.Valid;
            span.IsCanonical = true;
        }

        var orphanParents = 0;
        var malformedParentIds = 0;
        foreach (var span in working)
        {
            if (string.IsNullOrWhiteSpace(span.ParentSpanId))
            {
                span.ParentStatus = ActivityTraceParentStatus.Root;
                continue;
            }

            if (!IsValidSpanId(span.ParentSpanId))
            {
                span.ParentStatus = ActivityTraceParentStatus.Malformed;
                malformedParentIds++;
                continue;
            }

            if (!bySpanId.TryGetValue(span.ParentSpanId, out var parent))
            {
                span.ParentStatus = ActivityTraceParentStatus.Orphan;
                orphanParents++;
                continue;
            }

            span.Parent = parent;
            span.ParentStatus = ActivityTraceParentStatus.Resolved;
        }

        var cycleSpans = BreakCycles(working);
        var roots = new List<WorkingSpan>();
        foreach (var span in working)
        {
            if (span.Parent is null)
            {
                roots.Add(span);
            }
            else
            {
                span.Parent.Children.Add(span);
            }
        }

        roots.Sort(WorkingSpanComparer.Instance);
        foreach (var span in working)
        {
            span.Children.Sort(WorkingSpanComparer.Instance);
        }

        var ordered = Flatten(roots, working.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index].NodeIndex = index;
        }

        var clippedChildIntervals = 0;
        var disjointChildIntervals = 0;
        foreach (var span in ordered)
        {
            ComputeResidual(span, ref clippedChildIntervals, ref disjointChildIntervals);
        }

        ComputeCriticalPaths(ordered);
        var criticalRoot = SelectLargest(
            roots,
            static span => span.CriticalPathTicks);
        var criticalPath = BuildCriticalPath(criticalRoot);
        foreach (var span in criticalPath)
        {
            span.OnCriticalPath = true;
        }

        var maxResidual = SelectLargest(
            ordered,
            static span => span.ResidualTicks);

        var retainedActivities = capture.Activities.Count;
        if (capture.TotalActivities > retainedActivities)
        {
            warnings.Add(
                $"Retention truncation: the collector observed {capture.TotalActivities} activities but retained only {retainedActivities}; trace membership, roots, timing, and critical-path results are incomplete lower-bound evidence.");
        }

        if (matchedActivities == 0)
        {
            warnings.Add(
                $"No retained completed activity matched trace {normalizedTraceId}; retry while the trace is active and ensure the relevant ActivitySource filters are enabled.");
        }

        if (incompleteIntervals > 0)
        {
            warnings.Add(
                $"{incompleteIntervals} matching activity row(s) lacked a valid completed interval and were excluded from the completed-span tree.");
        }

        if (missingSpanIds > 0)
        {
            warnings.Add(
                $"{missingSpanIds} completed span(s) have no span-id. They may attach to a captured parent but cannot own child links.");
        }

        if (malformedSpanIds > 0)
        {
            warnings.Add(
                $"{malformedSpanIds} completed span(s) have a malformed W3C span-id. They may attach to a captured parent but cannot own child links.");
        }

        if (duplicateSpanIds > 0)
        {
            warnings.Add(
                $"{duplicateSpanIds} duplicate span-id occurrence(s) were retained as distinct rows; only the first deterministic occurrence owns child links.");
        }

        if (malformedParentIds > 0)
        {
            warnings.Add(
                $"{malformedParentIds} completed span(s) have a malformed parent-span-id and are surfaced as separate roots without an invented edge.");
        }

        if (orphanParents > 0)
        {
            warnings.Add(
                $"{orphanParents} completed span(s) reference a valid parent-span-id that is absent from the retained window and are surfaced as orphan roots.");
        }

        if (cycleSpans > 0)
        {
            warnings.Add(
                $"{cycleSpans} completed span(s) participate in parent cycles; their cyclic parent edges were omitted and they are surfaced as roots.");
        }

        if (clippedChildIntervals > 0)
        {
            warnings.Add(
                $"{clippedChildIntervals} direct-child interval(s) extended outside the parent interval and were clipped before residual wall-time calculation.");
        }

        if (disjointChildIntervals > 0)
        {
            warnings.Add(
                $"{disjointChildIntervals} direct-child interval(s) did not overlap the parent interval and were not subtracted from residual wall time.");
        }

        var returnedCount = Math.Min(topN, ordered.Count);
        var truncated = returnedCount < ordered.Count;
        if (truncated)
        {
            warnings.Add(
                $"Wire projection truncated: returned {returnedCount} of {ordered.Count} completed matching spans because topN={topN}; timing metrics use all retained matching spans.");
        }

        var visibleCriticalPath = criticalPath
            .Where(span => span.NodeIndex < returnedCount)
            .Select(span => span.NodeIndex)
            .ToArray();
        var criticalPathTruncated = visibleCriticalPath.Length != criticalPath.Count;
        if (criticalPathTruncated)
        {
            warnings.Add(
                "The critical path extends beyond the returned span rows; increase topN to expose the remaining path nodes.");
        }

        var projectedSpans = ordered
            .Take(returnedCount)
            .Select(span => span.ToProjection(normalizedTraceId, redactor))
            .ToArray();

        var wallClockTicks = ComputeWallClockTicks(ordered);
        return new ActivityTraceProjection(
            TraceId: normalizedTraceId,
            CompletedOnly: true,
            CanClaimComplete: false,
            CaptureWindowStartedAt: capture.StartedAt,
            CaptureWindowEndedAt: capture.StartedAt + capture.Duration,
            TotalActivities: capture.TotalActivities,
            RetainedActivities: retainedActivities,
            MatchedActivities: matchedActivities,
            CompletedSpanCount: ordered.Count,
            ReturnedSpans: projectedSpans.Length,
            Truncated: truncated,
            RootCount: roots.Count,
            OrphanCount: orphanParents,
            DuplicateSpanIdCount: duplicateSpanIds,
            MissingSpanIdCount: missingSpanIds,
            MalformedSpanIdCount: malformedSpanIds,
            MalformedParentSpanIdCount: malformedParentIds,
            CycleSpanCount: cycleSpans,
            WallClockDurationMs: wallClockTicks is { } ticks ? ToMilliseconds(ticks) : null,
            MaxResidualNodeIndex: maxResidual?.NodeIndex,
            MaxResidualDurationMs: maxResidual is null ? null : ToMilliseconds(maxResidual.ResidualTicks),
            CriticalPathDurationMs: criticalRoot is null ? null : ToMilliseconds(criticalRoot.CriticalPathTicks),
            CriticalPathNodeIndexes: visibleCriticalPath,
            CriticalPathTruncated: criticalPathTruncated,
            Spans: projectedSpans,
            Warnings: warnings);
    }

    /// <summary>Validates and lower-cases a non-zero W3C trace-id.</summary>
    public static bool TryNormalizeTraceId(string? traceId, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return false;
        }

        var candidate = traceId.Trim();
        if (!IsValidHexId(candidate, TraceIdLength))
        {
            return false;
        }

        normalized = candidate.ToLowerInvariant();
        return true;
    }

    private static bool IsValidSpanId(string spanId) => IsValidHexId(spanId, SpanIdLength);

    private static bool IsValidHexId(string value, int expectedLength)
    {
        if (value.Length != expectedLength)
        {
            return false;
        }

        var anyNonZero = false;
        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }

            anyNonZero |= ch != '0';
        }

        return anyNonZero;
    }

    private static int BreakCycles(IReadOnlyList<WorkingSpan> spans)
    {
        var complete = new HashSet<WorkingSpan>();
        var cycleMembers = new HashSet<WorkingSpan>();

        foreach (var start in spans)
        {
            if (complete.Contains(start))
            {
                continue;
            }

            var path = new List<WorkingSpan>();
            var pathIndexes = new Dictionary<WorkingSpan, int>();
            WorkingSpan? current = start;
            while (current is not null && !complete.Contains(current))
            {
                if (pathIndexes.TryGetValue(current, out var cycleStart))
                {
                    for (var index = cycleStart; index < path.Count; index++)
                    {
                        cycleMembers.Add(path[index]);
                    }

                    break;
                }

                pathIndexes[current] = path.Count;
                path.Add(current);
                current = current.Parent;
            }

            foreach (var span in path)
            {
                complete.Add(span);
            }
        }

        foreach (var span in cycleMembers)
        {
            span.Parent = null;
            span.ParentStatus = ActivityTraceParentStatus.Cycle;
        }

        return cycleMembers.Count;
    }

    private static List<WorkingSpan> Flatten(IReadOnlyList<WorkingSpan> roots, int capacity)
    {
        var ordered = new List<WorkingSpan>(capacity);
        var stack = new Stack<(WorkingSpan Span, int Depth)>();
        for (var index = roots.Count - 1; index >= 0; index--)
        {
            stack.Push((roots[index], 0));
        }

        while (stack.Count > 0)
        {
            var (span, depth) = stack.Pop();
            span.Depth = depth;
            ordered.Add(span);

            for (var index = span.Children.Count - 1; index >= 0; index--)
            {
                stack.Push((span.Children[index], depth + 1));
            }
        }

        return ordered;
    }

    private static void ComputeResidual(
        WorkingSpan parent,
        ref int clippedChildIntervals,
        ref int disjointChildIntervals)
    {
        var parentStart = parent.StartedAt.UtcTicks;
        var parentStop = parent.StoppedAt.UtcTicks;
        var intervals = new List<(long Start, long Stop)>(parent.Children.Count);
        foreach (var child in parent.Children)
        {
            var childStart = child.StartedAt.UtcTicks;
            var childStop = child.StoppedAt.UtcTicks;
            var clippedStart = Math.Max(parentStart, childStart);
            var clippedStop = Math.Min(parentStop, childStop);
            if (clippedStop <= clippedStart)
            {
                disjointChildIntervals++;
                continue;
            }

            if (clippedStart != childStart || clippedStop != childStop)
            {
                clippedChildIntervals++;
            }

            intervals.Add((clippedStart, clippedStop));
        }

        intervals.Sort(static (left, right) =>
        {
            var byStart = left.Start.CompareTo(right.Start);
            return byStart != 0 ? byStart : left.Stop.CompareTo(right.Stop);
        });

        long coveredTicks = 0;
        if (intervals.Count > 0)
        {
            var mergedStart = intervals[0].Start;
            var mergedStop = intervals[0].Stop;
            for (var index = 1; index < intervals.Count; index++)
            {
                var interval = intervals[index];
                if (interval.Start <= mergedStop)
                {
                    mergedStop = Math.Max(mergedStop, interval.Stop);
                    continue;
                }

                coveredTicks += mergedStop - mergedStart;
                mergedStart = interval.Start;
                mergedStop = interval.Stop;
            }

            coveredTicks += mergedStop - mergedStart;
        }

        parent.ResidualTicks = Math.Max(0, parentStop - parentStart - coveredTicks);
    }

    private static void ComputeCriticalPaths(IReadOnlyList<WorkingSpan> ordered)
    {
        for (var index = ordered.Count - 1; index >= 0; index--)
        {
            var span = ordered[index];
            span.CriticalChild = SelectLargest(
                span.Children,
                static child => child.CriticalPathTicks);
            span.CriticalPathTicks =
                span.ResidualTicks + (span.CriticalChild?.CriticalPathTicks ?? 0);
        }
    }

    private static WorkingSpan? SelectLargest(
        IReadOnlyList<WorkingSpan> spans,
        Func<WorkingSpan, long> selector)
    {
        WorkingSpan? selected = null;
        long selectedValue = 0;
        foreach (var span in spans)
        {
            var value = selector(span);
            if (selected is null ||
                value > selectedValue ||
                (value == selectedValue && WorkingSpanComparer.Instance.Compare(span, selected) < 0))
            {
                selected = span;
                selectedValue = value;
            }
        }

        return selected;
    }

    private static List<WorkingSpan> BuildCriticalPath(WorkingSpan? root)
    {
        var path = new List<WorkingSpan>();
        for (var current = root; current is not null; current = current.CriticalChild)
        {
            path.Add(current);
        }

        return path;
    }

    private static long? ComputeWallClockTicks(IReadOnlyList<WorkingSpan> spans)
    {
        if (spans.Count == 0)
        {
            return null;
        }

        var first = spans[0].StartedAt.UtcTicks;
        var last = spans[0].StoppedAt.UtcTicks;
        for (var index = 1; index < spans.Count; index++)
        {
            first = Math.Min(first, spans[index].StartedAt.UtcTicks);
            last = Math.Max(last, spans[index].StoppedAt.UtcTicks);
        }

        return Math.Max(0, last - first);
    }

    private static double ToMilliseconds(long ticks) =>
        Math.Round(TimeSpan.FromTicks(ticks).TotalMilliseconds, 3, MidpointRounding.AwayFromZero);

    private sealed class WorkingSpan(CapturedActivity activity, int ordinal)
    {
        public CapturedActivity Activity { get; } = activity;
        public int Ordinal { get; } = ordinal;
        public string? SpanId => Activity.SpanId;
        public string? ParentSpanId => Activity.ParentSpanId;
        public DateTimeOffset StartedAt => Activity.StartedAt;
        public DateTimeOffset StoppedAt => Activity.StoppedAt!.Value;
        public List<WorkingSpan> Children { get; } = [];
        public WorkingSpan? Parent { get; set; }
        public WorkingSpan? CriticalChild { get; set; }
        public string SpanIdStatus { get; set; } = ActivityTraceSpanIdStatus.Valid;
        public string ParentStatus { get; set; } = ActivityTraceParentStatus.Root;
        public bool IsCanonical { get; set; }
        public bool OnCriticalPath { get; set; }
        public int NodeIndex { get; set; }
        public int Depth { get; set; }
        public long ResidualTicks { get; set; }
        public long CriticalPathTicks { get; set; }

        public ActivityTraceSpan ToProjection(string traceId, SensitiveDataRedactor redactor) => new(
            NodeIndex,
            Parent?.NodeIndex,
            Depth,
            SpanIdStatus,
            ParentStatus,
            IsCanonical,
            OnCriticalPath,
            Activity.SourceName,
            Activity.OperationName,
            Activity.Id,
            traceId,
            Activity.SpanId,
            Activity.ParentSpanId,
            Activity.StartedAt,
            StoppedAt,
            ToMilliseconds(StoppedAt.UtcTicks - Activity.StartedAt.UtcTicks),
            ToMilliseconds(ResidualTicks),
            ProjectSafeTags(Activity.Tags, redactor));
    }

    private sealed class WorkingSpanComparer : IComparer<WorkingSpan>
    {
        public static WorkingSpanComparer Instance { get; } = new();

        public int Compare(WorkingSpan? left, WorkingSpan? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var result = left.StartedAt.CompareTo(right.StartedAt);
            if (result != 0) return result;
            result = left.StoppedAt.CompareTo(right.StoppedAt);
            if (result != 0) return result;
            result = string.Compare(left.Activity.SourceName, right.Activity.SourceName, StringComparison.Ordinal);
            if (result != 0) return result;
            result = string.Compare(left.Activity.OperationName, right.Activity.OperationName, StringComparison.Ordinal);
            if (result != 0) return result;
            result = string.Compare(left.SpanId, right.SpanId, StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
            result = string.Compare(left.ParentSpanId, right.ParentSpanId, StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
            result = string.Compare(left.Activity.Id, right.Activity.Id, StringComparison.Ordinal);
            return result != 0 ? result : left.Ordinal.CompareTo(right.Ordinal);
        }
    }

    private static Dictionary<string, string> ProjectSafeTags(
        IReadOnlyDictionary<string, string> tags,
        SensitiveDataRedactor redactor)
    {
        var projected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in SafeTagKeys)
        {
            if (!tags.TryGetValue(key, out var value))
            {
                continue;
            }

            var safeValue = redactor.Redact(value) ?? string.Empty;
            if (safeValue.Length > MaxSafeTagValueLength)
            {
                safeValue = string.Concat(safeValue.AsSpan(0, MaxSafeTagValueLength - 3), "...");
            }

            projected[key] = safeValue;
        }

        return projected;
    }
}

/// <summary>Bounded completed-span view for one retained W3C trace.</summary>
public sealed record ActivityTraceProjection(
    string TraceId,
    bool CompletedOnly,
    bool CanClaimComplete,
    DateTimeOffset CaptureWindowStartedAt,
    DateTimeOffset CaptureWindowEndedAt,
    int TotalActivities,
    int RetainedActivities,
    int MatchedActivities,
    int CompletedSpanCount,
    int ReturnedSpans,
    bool Truncated,
    int RootCount,
    int OrphanCount,
    int DuplicateSpanIdCount,
    int MissingSpanIdCount,
    int MalformedSpanIdCount,
    int MalformedParentSpanIdCount,
    int CycleSpanCount,
    double? WallClockDurationMs,
    int? MaxResidualNodeIndex,
    double? MaxResidualDurationMs,
    double? CriticalPathDurationMs,
    IReadOnlyList<int> CriticalPathNodeIndexes,
    bool CriticalPathTruncated,
    IReadOnlyList<ActivityTraceSpan> Spans,
    IReadOnlyList<string> Warnings);

/// <summary>One completed span in deterministic parent-before-child order.</summary>
public sealed record ActivityTraceSpan(
    int NodeIndex,
    int? ParentNodeIndex,
    int Depth,
    string SpanIdStatus,
    string ParentStatus,
    bool IsCanonical,
    bool OnCriticalPath,
    string SourceName,
    string OperationName,
    string ActivityId,
    string TraceId,
    string? SpanId,
    string? ParentSpanId,
    DateTimeOffset StartedAt,
    DateTimeOffset StoppedAt,
    double DurationMs,
    double ResidualDurationMs,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>Stable span-id classifications emitted by <see cref="ActivityTraceSpan"/>.</summary>
public static class ActivityTraceSpanIdStatus
{
    public const string Valid = "valid";
    public const string Missing = "missing";
    public const string Malformed = "malformed";
    public const string Duplicate = "duplicate";
}

/// <summary>Stable parent-link classifications emitted by <see cref="ActivityTraceSpan"/>.</summary>
public static class ActivityTraceParentStatus
{
    public const string Root = "root";
    public const string Resolved = "resolved";
    public const string Orphan = "orphan";
    public const string Malformed = "malformed";
    public const string Cycle = "cycle";
}
