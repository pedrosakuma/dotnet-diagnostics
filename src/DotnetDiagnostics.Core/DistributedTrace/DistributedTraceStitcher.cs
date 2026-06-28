using DotnetDiagnostics.Core.Activities;

namespace DotnetDiagnostics.Core.DistributedTrace;

/// <summary>
/// Pure, side-effect-free engine that stitches per-Pod <see cref="ActivityCapture"/> windows
/// into a single cross-replica <see cref="DistributedTraceTimeline"/> for one W3C trace-id
/// (issue #437 / Phase 13 G3). It is deliberately decoupled from the orchestrator fan-out so
/// the correlation logic can be unit-tested without a Kubernetes topology: the MCP layer
/// collects the captures, this class joins them.
/// </summary>
/// <remarks>
/// Spans are joined by W3C <c>parent/child</c> span links — <em>never</em> by raw wall-clock —
/// because the captures originate on different nodes whose clocks may drift. Wall-clock is only
/// used to order siblings under the same parent.
/// </remarks>
public static class DistributedTraceStitcher
{
    /// <summary>Tolerance for the "child started before its parent" clock-skew heuristic.</summary>
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Stitches the supplied per-Pod captures into one timeline for <paramref name="traceId"/>.
    /// </summary>
    /// <param name="traceId">Target W3C trace-id. Matched case-insensitively; required.</param>
    /// <param name="captures">One entry per attached Pod that was asked for activities. Pods that
    /// observed none of the trace's spans still contribute a zero-match coverage row + a warning.</param>
    public static DistributedTraceTimeline Stitch(
        string traceId,
        IReadOnlyList<(string PodName, ActivityCapture Capture)> captures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        ArgumentNullException.ThrowIfNull(captures);

        var normalizedTraceId = traceId.Trim();
        var warnings = new List<string>();
        var coverage = new List<DistributedTracePodCoverage>(captures.Count);

        // 1) Materialize every span of this trace across all Pods, tracking per-Pod coverage.
        var working = new List<WorkingSpan>();
        foreach (var (podName, capture) in captures)
        {
            var matched = 0;
            foreach (var activity in capture.Activities)
            {
                if (!string.Equals(activity.TraceId, normalizedTraceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matched++;
                working.Add(new WorkingSpan(podName, activity));
            }

            coverage.Add(new DistributedTracePodCoverage(podName, matched, capture.Activities.Count));
            if (matched == 0)
            {
                warnings.Add(
                    $"Pod '{podName}' is attached but observed none of trace {normalizedTraceId}'s spans " +
                    "in the capture window (it may not participate in this trace, or its spans completed outside the window).");
            }
        }

        if (working.Count == 0)
        {
            warnings.Insert(0,
                $"No spans matching trace {normalizedTraceId} were captured across {captures.Count} attached Pod(s). " +
                "Trace correlation targets in-flight / recently-active traces — re-issue the request while the trace is live, " +
                "or widen the capture window.");
            return new DistributedTraceTimeline(
                normalizedTraceId, captures.Count, 0,
                Array.Empty<DistributedTraceSpan>(), null, null, coverage, warnings);
        }

        // 2) Index by SpanId so parent/child links can be resolved across Pod boundaries.
        var bySpanId = new Dictionary<string, WorkingSpan>(StringComparer.OrdinalIgnoreCase);
        var missingSpanId = 0;
        foreach (var span in working)
        {
            if (string.IsNullOrEmpty(span.SpanId))
            {
                missingSpanId++;
                continue;
            }

            if (!bySpanId.TryAdd(span.SpanId, span))
            {
                warnings.Add($"Duplicate span-id '{span.SpanId}' observed across Pods; the first occurrence is kept for linking.");
            }
            else
            {
                span.IsCanonical = true;
            }
        }

        if (missingSpanId > 0)
        {
            warnings.Add($"{missingSpanId} matched span(s) have no span-id and cannot be linked into the parent/child tree; they are treated as roots.");
        }

        // 3) Resolve parents and group direct children (by parent span-id) for self-time.
        var childrenByParent = new Dictionary<string, List<WorkingSpan>>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<WorkingSpan>();
        var orphanParents = 0;
        foreach (var span in working)
        {
            var hasParent = !string.IsNullOrEmpty(span.ParentSpanId) && bySpanId.ContainsKey(span.ParentSpanId);
            span.ParentResolved = hasParent;

            if (hasParent)
            {
                if (!childrenByParent.TryGetValue(span.ParentSpanId!, out var siblings))
                {
                    siblings = new List<WorkingSpan>();
                    childrenByParent[span.ParentSpanId!] = siblings;
                }

                siblings.Add(span);

                if (span.StartedAt + ClockSkewTolerance < bySpanId[span.ParentSpanId!].StartedAt)
                {
                    warnings.Add(
                        $"Clock skew: span '{span.SpanId}' on Pod '{span.PodName}' starts before its parent — " +
                        "spans are ordered causally (parent/child), not by wall-clock.");
                }
            }
            else
            {
                roots.Add(span);
                if (!string.IsNullOrEmpty(span.ParentSpanId))
                {
                    orphanParents++;
                }
            }
        }

        if (orphanParents > 0)
        {
            warnings.Add(
                $"{orphanParents} span(s) reference a parent that was not captured (the parent Pod may not be attached, " +
                "or the parent span completed outside the window); they are surfaced as additional roots.");
        }

        // 4) Self-duration = own duration minus the duration attributed to direct children.
        foreach (var span in working)
        {
            if (span.DurationMs is null)
            {
                continue;
            }

            double childMs = 0;
            if (span.IsCanonical && !string.IsNullOrEmpty(span.SpanId) && childrenByParent.TryGetValue(span.SpanId, out var kids))
            {
                foreach (var kid in kids)
                {
                    childMs += kid.DurationMs ?? 0;
                }
            }

            span.SelfDurationMs = Math.Max(0, span.DurationMs.Value - childMs);
        }

        // 5) Causal ordering: roots by start, then DFS pre-order with siblings by start.
        var ordered = new List<WorkingSpan>(working.Count);
        var visited = new HashSet<WorkingSpan>();
        foreach (var root in roots.OrderBy(s => s.StartedAt))
        {
            AppendSubtree(root, 0, childrenByParent, ordered, visited);
        }

        // Any span not reachable from a root (cycle) is appended deterministically so it is never dropped.
        if (ordered.Count != working.Count)
        {
            warnings.Add("A parent/child cycle was detected; the unreachable spans are appended in start order.");
            foreach (var span in working.OrderBy(s => s.StartedAt))
            {
                if (visited.Add(span))
                {
                    span.Depth = 0;
                    ordered.Add(span);
                }
            }
        }

        // 6) Pick the slowest hop by self-time (the actual culprit, not an ancestor that merely waits).
        WorkingSpan? slowest = null;
        foreach (var span in ordered)
        {
            if (span.SelfDurationMs is { } self && (slowest is null || self > slowest.SelfDurationMs))
            {
                slowest = span;
            }
        }

        // 7) Best-effort wall-clock span of the whole trace.
        double? wallClockMs = null;
        var minStart = ordered.Min(s => s.StartedAt);
        DateTimeOffset? maxStop = null;
        foreach (var span in ordered)
        {
            if (span.StoppedAt is { } stop && (maxStop is null || stop > maxStop))
            {
                maxStop = stop;
            }
        }

        if (maxStop is { } end && end > minStart)
        {
            wallClockMs = (end - minStart).TotalMilliseconds;
        }

        var spans = ordered.Select(s => s.ToSpan()).ToArray();
        return new DistributedTraceTimeline(
            normalizedTraceId,
            captures.Count,
            spans.Length,
            spans,
            slowest?.ToSpan(),
            wallClockMs,
            coverage,
            warnings);
    }

    private static void AppendSubtree(
        WorkingSpan span,
        int depth,
        IReadOnlyDictionary<string, List<WorkingSpan>> childrenByParent,
        List<WorkingSpan> ordered,
        HashSet<WorkingSpan> visited)
    {
        if (!visited.Add(span))
        {
            return;
        }

        span.Depth = depth;
        ordered.Add(span);

        // Only the canonical owner of a span-id consumes its child list; a non-canonical
        // duplicate (same span-id seen on another Pod) is treated as a leaf so children are
        // never attached under — nor self-time double-subtracted from — the wrong occurrence.
        if (!span.IsCanonical || string.IsNullOrEmpty(span.SpanId) || !childrenByParent.TryGetValue(span.SpanId, out var children))
        {
            return;
        }

        foreach (var child in children.OrderBy(s => s.StartedAt))
        {
            AppendSubtree(child, depth + 1, childrenByParent, ordered, visited);
        }
    }

    /// <summary>Mutable per-Pod span used during stitching; projected to the immutable record at the end.</summary>
    private sealed class WorkingSpan(string podName, CapturedActivity activity)
    {
        public string PodName { get; } = podName;
        public string SourceName { get; } = activity.SourceName;
        public string OperationName { get; } = activity.OperationName;
        public string? SpanId { get; } = string.IsNullOrEmpty(activity.SpanId) ? null : activity.SpanId;
        public string? ParentSpanId { get; } = string.IsNullOrEmpty(activity.ParentSpanId) ? null : activity.ParentSpanId;
        public DateTimeOffset StartedAt { get; } = activity.StartedAt;
        public DateTimeOffset? StoppedAt { get; } = activity.StoppedAt;
        public double? DurationMs { get; } = activity.Duration?.TotalMilliseconds;
        public IReadOnlyDictionary<string, string> Tags { get; } = activity.Tags;
        public double? SelfDurationMs { get; set; }
        public int Depth { get; set; }
        public bool ParentResolved { get; set; }
        public bool IsCanonical { get; set; }

        public DistributedTraceSpan ToSpan() => new(
            PodName,
            SourceName,
            OperationName,
            SpanId,
            ParentSpanId,
            StartedAt,
            StoppedAt,
            DurationMs is { } d ? Math.Round(d, 3, MidpointRounding.AwayFromZero) : null,
            SelfDurationMs is { } s ? Math.Round(s, 3, MidpointRounding.AwayFromZero) : null,
            Depth,
            ParentResolved,
            Tags);
    }
}
