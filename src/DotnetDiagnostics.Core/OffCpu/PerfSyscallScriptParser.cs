using System.Globalization;
using System.Text.RegularExpressions;

namespace DotnetDiagnostics.Core.OffCpu;

/// <summary>
/// Parses the textual <c>perf script</c> rendering of <c>raw_syscalls:sys_enter</c> /
/// <c>raw_syscalls:sys_exit</c> events into a flat sequence of (tid, timestamp, syscall id,
/// enter/exit) records.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PerfSchedOffCpuSampler"/> records syscall tracepoints in a separate, target-scoped
/// <c>perf record -p &lt;pid&gt;</c> companion file without callgraphs. This keeps sched_switch DWARF
/// callchains for wait-stack quality without co-recording global raw_syscalls with those callchains.
/// This parser then reads the stackless companion file and only needs
/// <c>tid</c>/<c>timestamp</c>/<c>id</c> per syscall event, not a stack.
/// </para>
/// <para>
/// The generic <c>raw_syscalls:sys_enter</c>/<c>sys_exit</c> tracepoints only carry the raw
/// numeric syscall id (<see cref="SyscallTable"/> turns that into a name) — there is no
/// per-syscall tracepoint enabled, so parsing is limited to lines containing the
/// <c>raw_syscalls:sys_enter:</c> / <c>raw_syscalls:sys_exit:</c> markers; every other line
/// (sched_switch events, stray blank lines) is silently skipped rather than treated as an error,
/// so a perf version that renders the tracepoint payload slightly differently degrades to "no
/// syscall attribution" instead of throwing.
/// </para>
/// </remarks>
internal static class PerfSyscallScriptParser
{
    /// <summary>
    /// Resource-boundedness cap (per <c>docs/resource-boundedness.md</c>) on the number of parsed
    /// syscall enter/exit events materialized into memory, enforced <b>at the point of
    /// insertion</b> — once reached, further matching lines are still read (draining the process's
    /// stdout pipe so <c>perf script</c> is never blocked on a full pipe buffer) but are no longer
    /// added to the returned list. Set well above <see cref="SyscallIntervalIndex.MaxIntervals"/>
    /// (which caps at 500,000 *paired* intervals, i.e. up to ~1,000,000 raw enter+exit events in
    /// the worst case) so this cap is a true backstop against a pathologically syscall-heavy
    /// target, not the common limiting factor.
    /// </summary>
    internal const int MaxParsedEvents = 1_000_000;

    private const string EnterMarker = " raw_syscalls:sys_enter:";
    private const string ExitMarker = " raw_syscalls:sys_exit:";

    // Kernel tracepoint print fmt for raw_syscalls/sys_enter is "NR %ld (...)" and for
    // sys_exit is "NR %ld = %ld" — tolerate "id=<n>" / "id: <n>" renderings some perf builds use.
    private static readonly Regex SyscallIdRegex = new(@"(?:NR\s+|id[:=]\s*)(-?\d+)", RegexOptions.Compiled);

    internal readonly record struct SyscallEvent(int Tid, double TimestampSeconds, long SyscallId, bool IsEnter);

    /// <summary>Parse outcome: the (possibly capped) event list plus whether <see cref="MaxParsedEvents"/> was hit.</summary>
    internal readonly record struct ParseResult(IReadOnlyList<SyscallEvent> Events, bool HitCap, long DroppedCount);

    public static async Task<ParseResult> ParseAsync(
        TextReader reader,
        HashSet<int> targetTids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(targetTids);

        var events = new List<SyscallEvent>();
        long dropped = 0;
        while (true)
        {
            var raw = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (raw is null) break;

            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            bool isEnter;
            string marker;
            if (line.Contains(EnterMarker, StringComparison.Ordinal))
            {
                isEnter = true;
                marker = EnterMarker;
            }
            else if (line.Contains(ExitMarker, StringComparison.Ordinal))
            {
                isEnter = false;
                marker = ExitMarker;
            }
            else
            {
                // Not a syscall line we understand — a sched_switch event, or (defensively) a
                // stray indented frame line if the caller forgot -G. Either way, skip it.
                continue;
            }

            if (!TryParseHeader(line, marker, out var ts, out var tid, out var payload))
            {
                continue;
            }
            if (!targetTids.Contains(tid))
            {
                continue;
            }
            if (!TryParseSyscallId(payload, out var id))
            {
                continue;
            }

            // Cap at insertion: keep draining the pipe (perf script must not block on a full
            // stdout buffer) but stop growing the in-memory list once the budget is reached.
            if (events.Count >= MaxParsedEvents)
            {
                dropped++;
                continue;
            }

            events.Add(new SyscallEvent(tid, ts, id, isEnter));
        }

        return new ParseResult(events, dropped > 0, dropped);
    }

    /// <summary>
    /// Extracts the timestamp and TID from the common <c>perf script</c> event header:
    /// <c>&lt;comm&gt;  &lt;tid&gt; [&lt;cpu&gt;]  &lt;timestamp&gt;: &lt;marker&gt;</c>. Unlike
    /// <c>sched:sched_switch</c> (whose payload carries <c>prev_pid=</c>/<c>next_pid=</c>
    /// explicitly), raw_syscalls events only identify the acting thread via this header, so
    /// (unlike <see cref="PerfSchedScriptParser"/>) we must parse it here.
    /// </summary>
    private static bool TryParseHeader(string line, string marker, out double ts, out int tid, out string payload)
    {
        ts = 0;
        tid = 0;
        payload = string.Empty;

        var markerIdx = line.IndexOf(marker, StringComparison.Ordinal);
        if (markerIdx < 0) return false;

        var prefix = line[..markerIdx];
        payload = line[(markerIdx + marker.Length)..].Trim();

        // prefix ends with the timestamp's own trailing colon, e.g. "target  1000 [001]  1.000000:"
        var colonIdx = prefix.LastIndexOf(':');
        if (colonIdx < 0) return false;
        var beforeColon = prefix[..colonIdx];
        var lastSpace = beforeColon.LastIndexOf(' ');
        if (lastSpace < 0) return false;
        var tsToken = beforeColon[(lastSpace + 1)..];
        if (!double.TryParse(tsToken, NumberStyles.Float, CultureInfo.InvariantCulture, out ts))
        {
            return false;
        }

        // "target  1000 [001]" — strip the "[cpu]" suffix, then take the trailing whitespace
        // token as the TID. Some perf builds render "pid/tid [cpu]"; when a slash is present we
        // want the TID (second component), matching the /proc/<pid>/task/<tid> identity we filter by.
        var commTidCpu = beforeColon[..lastSpace].TrimEnd();
        var bracketIdx = commTidCpu.IndexOf('[');
        var beforeBracket = (bracketIdx >= 0 ? commTidCpu[..bracketIdx] : commTidCpu).TrimEnd();
        var lastSpace2 = beforeBracket.LastIndexOf(' ');
        var tidToken = lastSpace2 >= 0 ? beforeBracket[(lastSpace2 + 1)..] : beforeBracket;
        var slashIdx = tidToken.IndexOf('/');
        if (slashIdx >= 0)
        {
            tidToken = tidToken[(slashIdx + 1)..];
        }

        return int.TryParse(tidToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out tid);
    }

    private static bool TryParseSyscallId(string payload, out long id)
    {
        id = 0;
        var m = SyscallIdRegex.Match(payload);
        if (!m.Success) return false;
        return long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }
}
