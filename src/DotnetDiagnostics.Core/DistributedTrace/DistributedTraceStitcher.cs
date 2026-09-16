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
/// because the captures originate on different nodes whose clocks may drift. UTC intervals are
/// used for residual estimates; temporal anomalies suppress the slowest-hop candidate.
/// </remarks>
public static class DistributedTraceStitcher
{
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

        if (!ActivityTraceProjector.TryNormalizeTraceId(traceId, out var normalizedTraceId))
        {
            throw new ArgumentException("traceId must be a non-zero 32-hex W3C trace-id.", nameof(traceId));
        }
        var warnings = new List<string>
        {
            "Completed-stop-only, bounded-window evidence cannot establish full trace completeness; spans on unattached Pods are also absent.",
            "Residuals and slowest-hop rankings describe retained intervals only, are not lower bounds, and can be inflated by missing children. No clock offsets are inferred.",
        };
        var timingAnomaly = false;
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

            coverage.Add(new DistributedTracePodCoverage(podName, matched, capture.Activities.Count, capture.Retention));
            if (capture.Retention?.RetentionLimited is null)
            {
                warnings.Add($"Pod '{podName}': retention provenance is unknown (legacy response); zero matching loss is not established.");
            }
            if (!string.Equals(capture.Retention?.AppliedTraceId, normalizedTraceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Pod '{podName}': requested trace filtering was not confirmed by applied-filter metadata; retained spans cannot establish trace absence.");
            }
            if (capture.Retention?.RetentionLimited == true)
            {
                warnings.Add($"Pod '{podName}': retention truncation at effective cap={capture.Retention.EffectiveCap}; dropped {capture.Retention.DroppedMatchingActivities} matching activities. Counts are lower bounds, not residuals or rankings.");
            }
            if (matched == 0)
            {
                warnings.Add(
                    $"Pod '{podName}' is attached but retained none of trace {normalizedTraceId}'s spans " +
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
        working = working.OrderBy(s => s.StartedAt).ThenBy(s => s.PodName, StringComparer.Ordinal)
            .ThenBy(s => s.SourceName, StringComparer.Ordinal).ThenBy(s => s.OperationName, StringComparer.Ordinal)
            .ThenBy(s => s.SpanId, StringComparer.Ordinal).ThenBy(s => s.StoppedAt)
            .ThenBy(s => s.ParentSpanId, StringComparer.Ordinal).ThenBy(s => s.DurationMs).ToList();
        var bySpanId = new Dictionary<string, WorkingSpan>(StringComparer.OrdinalIgnoreCase);
        var missingSpanId = 0;
        foreach (var span in working)
        {
            if (!IsValidSpanId(span.SpanId))
            {
                missingSpanId++;
                continue;
            }

            if (!bySpanId.TryAdd(span.SpanId!, span))
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
            warnings.Add($"{missingSpanId} matched span(s) have no span-id or a malformed span-id and cannot be linked into the parent/child tree; they are treated as roots.");
        }

        // 3) Resolve parents and group direct children (by parent span-id) for self-time.
        var childrenByParent = new Dictionary<string, List<WorkingSpan>>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<WorkingSpan>();
        var orphanParents = 0;
        foreach (var span in working)
        {
            if (IsValidSpanId(span.SpanId) && IsValidSpanId(span.ParentSpanId) &&
                bySpanId.TryGetValue(span.ParentSpanId!, out var parent))
            {
                span.Parent = parent;
            }
        }

        // A duplicate's self-id still denotes an invalid self-edge, even when lookup resolves another row.
        var cycleMembers = working.Where(span => span.Parent is not null &&
            string.Equals(span.SpanId, span.ParentSpanId, StringComparison.OrdinalIgnoreCase)).ToHashSet();
        var complete = new HashSet<WorkingSpan>();
        foreach (var span in working)
        {
            var path = new List<WorkingSpan>();
            var indexes = new Dictionary<WorkingSpan, int>();
            for (var current = span; current is not null && !complete.Contains(current); current = current.Parent)
            {
                if (indexes.TryGetValue(current, out var cycleStart))
                {
                    foreach (var member in path.Skip(cycleStart))
                    {
                        cycleMembers.Add(member);
                    }
                    break;
                }
                indexes.Add(current, path.Count);
                path.Add(current);
            }
            complete.UnionWith(path);
        }
        foreach (var span in cycleMembers)
        {
            span.Parent = null;
        }
        if (cycleMembers.Count > 0)
        {
            warnings.Add($"A parent/child cycle was detected; {cycleMembers.Count} cycle edges were removed before timing attribution.");
            timingAnomaly = true;
        }

        foreach (var span in working)
        {
            var hasParent = span.Parent is not null;
            span.ParentResolved = hasParent;

            if (hasParent)
            {
                if (!childrenByParent.TryGetValue(span.ParentSpanId!, out var siblings))
                {
                    siblings = new List<WorkingSpan>();
                    childrenByParent[span.ParentSpanId!] = siblings;
                }

                siblings.Add(span);

                if (span.StartedAt < span.Parent!.StartedAt ||
                    (span.StoppedAt is { } childStop && span.Parent.StoppedAt is { } parentStop && childStop > parentStop))
                {
                    timingAnomaly = true;
                    warnings.Add(
                        $"Clock skew or temporal anomaly: span '{span.SpanId}' on Pod '{span.PodName}' extends outside its parent interval. Residuals are clipped estimates; slowest-hop ranking is withheld.");
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

        // 4) Self-duration = own duration minus the union of direct-child intervals.
        // Summing raw child durations double-counts when two direct children overlap or nest.
        foreach (var span in working)
        {
            if (span.DurationMs is null || span.DurationMs < 0 || span.StoppedAt is null ||
                span.StoppedAt < span.StartedAt ||
                Math.Abs((span.StoppedAt.Value - span.StartedAt).TotalMilliseconds - span.DurationMs.Value) > 0.001)
            {
                timingAnomaly = true;
                warnings.Add($"Span '{span.SpanId}' has an invalid or incomplete completed interval; its residual is unknown and ranking is withheld.");
                continue;
            }

            double childMs = 0;
            if (span.IsCanonical && !string.IsNullOrEmpty(span.SpanId) && childrenByParent.TryGetValue(span.SpanId, out var kids))
            {
                var union = UtcIntervalUnion.Measure(span.StartedAt, span.StoppedAt.Value,
                    kids.Where(kid => kid.DurationMs is >= 0 && kid.StoppedAt >= kid.StartedAt &&
                        Math.Abs((kid.StoppedAt.Value - kid.StartedAt).TotalMilliseconds - kid.DurationMs.Value) <= 0.001)
                    .Select(kid => (kid.StartedAt, kid.StoppedAt!.Value)));
                childMs = union.CoveredTicks / (double)TimeSpan.TicksPerMillisecond;
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
            if (!timingAnomaly && span.SelfDurationMs is { } self && (slowest is null || self > slowest.SelfDurationMs))
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

    private static bool IsValidSpanId(string? value)
        => value is { Length: 16 } && value.Any(c => c != '0') && value.All(Uri.IsHexDigit);

    private static void AppendSubtree(
        WorkingSpan span,
        int depth,
        IReadOnlyDictionary<string, List<WorkingSpan>> childrenByParent,
        List<WorkingSpan> ordered,
        HashSet<WorkingSpan> visited)
    {
        var stack = new Stack<(WorkingSpan Span, int Depth)>();
        stack.Push((span, depth));
        while (stack.TryPop(out var entry))
        {
            var current = entry.Span;
            if (!visited.Add(current))
            {
                continue;
            }
            current.Depth = entry.Depth;
            ordered.Add(current);
            if (current.IsCanonical && current.SpanId is { } id && childrenByParent.TryGetValue(id, out var children))
            {
                for (var index = children.Count - 1; index >= 0; index--)
                {
                    stack.Push((children[index], entry.Depth + 1));
                }
            }
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
        public WorkingSpan? Parent { get; set; }

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
