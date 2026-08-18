namespace DotnetDiagnostics.Core.Capabilities;

/// <summary>
/// Typed classification of why a perf-backed collector (CPU, off-CPU, native-alloc,
/// native-lock-contention) failed to collect. All Linux perf-backed samplers currently surface
/// failures as a generic <see cref="InvalidOperationException"/>/<see cref="UnauthorizedAccessException"/>
/// with a free-text message built from <c>perf</c>'s stdout/stderr. This classifier is a pure,
/// non-privileged function over that text (plus the resolver's binary-not-found signal) so the
/// distinct failure modes called out in issue #851 — missing perf, an unusable kernel-mismatched
/// wrapper, a missing tracepoint, permission denial, and an unsupported call-graph mode — can be
/// told apart deterministically and covered by unit tests without spawning perf or requiring root.
/// See <c>docs/perf-compat-matrix.md</c> for the environments each failure mode is expected on.
/// </summary>
public enum PerfFailureKind
{
    /// <summary>No failure text recognized; treat as an opaque/unclassified perf error.</summary>
    Unknown = 0,

    /// <summary>No usable <c>perf</c> binary was found at all (configured path and every
    /// <c>linux-tools-*</c> fallback candidate failed the version probe).</summary>
    MissingPerf,

    /// <summary>A <c>perf</c> binary exists at the configured path but is the Debian/Ubuntu/WSL
    /// kernel-matching wrapper with no backing binary for the running kernel (prints
    /// "WARNING: perf not found for kernel ..." and exits non-zero).</summary>
    UnusableWrapper,

    /// <summary>The requested tracepoint/probe/event is not available on this kernel (e.g.
    /// <c>sched:sched_switch</c> or a dynamically created <c>probe_libc:*</c> uprobe).</summary>
    MissingTracepoint,

    /// <summary>The kernel denied the requested trace/attach due to insufficient privilege
    /// (missing <c>CAP_PERFMON</c>/<c>CAP_SYS_ADMIN</c>, or <c>perf_event_paranoid</c> too
    /// restrictive for the requested scope).</summary>
    PermissionDenied,

    /// <summary>The requested call-graph/unwind mode (e.g. <c>--call-graph dwarf</c>) is not
    /// supported by this perf build/kernel combination.</summary>
    UnsupportedCallGraph,
}

/// <summary>
/// Pure classifier for perf failure text. See <see cref="PerfFailureKind"/> for the full set of
/// recognized failure modes.
/// </summary>
public static class PerfFailureClassifier
{
    /// <summary>
    /// Classifies a perf failure from the combined stdout/stderr text produced by a failed
    /// <c>perf</c> invocation (record, probe, stat, or script). Returns
    /// <see cref="PerfFailureKind.Unknown"/> when no recognized pattern matches — callers should
    /// preserve the raw text in that case rather than dropping it.
    /// </summary>
    public static PerfFailureKind Classify(string? perfOutput)
    {
        if (string.IsNullOrWhiteSpace(perfOutput))
        {
            return PerfFailureKind.Unknown;
        }

        // Order matters: check the most specific / unambiguous signals first so a message that
        // happens to mention multiple keywords is not misclassified.
        if (perfOutput.Contains("WARNING: perf not found for kernel", StringComparison.OrdinalIgnoreCase)
            || perfOutput.Contains("You may need to install", StringComparison.OrdinalIgnoreCase))
        {
            return PerfFailureKind.UnusableWrapper;
        }

        if (perfOutput.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || perfOutput.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
            || perfOutput.Contains("perf_event_paranoid", StringComparison.OrdinalIgnoreCase))
        {
            return PerfFailureKind.PermissionDenied;
        }

        var fileNotFoundEvent = perfOutput.Contains("Error: File", StringComparison.OrdinalIgnoreCase)
            && perfOutput.Contains("not found", StringComparison.OrdinalIgnoreCase);
        if (perfOutput.Contains("Invalid or unsupported event", StringComparison.OrdinalIgnoreCase)
            || fileNotFoundEvent
            || perfOutput.Contains("event not found", StringComparison.OrdinalIgnoreCase)
            || perfOutput.Contains("is not a valid event", StringComparison.OrdinalIgnoreCase)
            || perfOutput.Contains("no such tracepoint", StringComparison.OrdinalIgnoreCase))
        {
            return PerfFailureKind.MissingTracepoint;
        }

        if (perfOutput.Contains("callchain", StringComparison.OrdinalIgnoreCase)
            && (perfOutput.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                || perfOutput.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                || perfOutput.Contains("failed to set", StringComparison.OrdinalIgnoreCase)))
        {
            return PerfFailureKind.UnsupportedCallGraph;
        }

        if (perfOutput.Contains("dwarf", StringComparison.OrdinalIgnoreCase)
            && perfOutput.Contains("not supported", StringComparison.OrdinalIgnoreCase))
        {
            return PerfFailureKind.UnsupportedCallGraph;
        }

        if (perfOutput.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || perfOutput.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
        {
            return PerfFailureKind.MissingPerf;
        }

        return PerfFailureKind.Unknown;
    }

    /// <summary>
    /// Convenience overload for the common "perf binary could not be resolved at all" case
    /// (<see cref="Capabilities.PerfHostProbe"/>/<c>PerfBinaryResolver.Resolve</c> returned
    /// <c>null</c>), which has no perf-produced text to classify from.
    /// </summary>
    public static PerfFailureKind ClassifyMissingBinary() => PerfFailureKind.MissingPerf;
}
