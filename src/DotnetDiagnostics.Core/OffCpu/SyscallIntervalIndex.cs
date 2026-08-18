namespace DotnetDiagnostics.Core.OffCpu;

/// <summary>
/// Builds a per-TID index of "syscall in flight" time intervals from a flat sequence of
/// <see cref="PerfSyscallScriptParser.SyscallEvent"/> records, so that
/// <see cref="PerfSchedOffCpuSampler"/> can label each off-CPU span with the syscall that was
/// active when the thread went off-CPU (issue #829).
/// </summary>
/// <remarks>
/// <para>
/// A syscall is "in flight" for a TID from its <c>sys_enter</c> timestamp until the next
/// <c>sys_exit</c> for the same TID (syscalls do not nest per-thread, so pairing consecutive
/// enter/exit events per TID is sufficient — we do not need to match on syscall id). An enter
/// with no matching exit before the capture window ended stays open through
/// <c>captureEndTs</c> — this deliberately mirrors the off-CPU sampler's own
/// <c>IsCensored</c> span handling: a thread parked in a syscall for the entire window should
/// still get a label rather than silently losing attribution.
/// </para>
/// <para>
/// <b>Resource-boundedness (per <c>docs/resource-boundedness.md</c>):</b> the interval count is
/// capped at <see cref="MaxIntervals"/> AT THE POINT OF INSERTION (checked before each interval
/// is added, not accumulate-then-truncate) so a pathologically high syscall-rate target cannot
/// blow up memory here on top of the separate hard caps on the underlying sched and syscall
/// <c>perf.data</c> files. Once the cap is hit, further syscalls for that capture are simply left
/// unlabeled (<see cref="Lookup"/> returns <c>null</c>) — degrading attribution completeness, not
/// correctness — and <see cref="HitCap"/> lets the caller surface a <c>notes[]</c> entry.
/// </para>
/// </remarks>
internal sealed class SyscallIntervalIndex
{
    // 500k intervals covers a very high syscall-rate target across a multi-second window while
    // keeping the index a small multiple of the sched_switch span count in the worst realistic case.
    internal const int MaxIntervals = 500_000;

    private readonly Dictionary<int, List<(double Start, double End, long SyscallId)>> _byTid = new();
    private long _dropped;

    public long TotalIntervals { get; private set; }
    public bool HitCap => _dropped > 0;
    public long DroppedCount => _dropped;

    /// <summary>
    /// Consumes a (not necessarily time-sorted) sequence of syscall events for a single TID and
    /// records the enter/exit-paired intervals, honoring <see cref="MaxIntervals"/>.
    /// </summary>
    /// <param name="tid">Kernel thread id the events belong to.</param>
    /// <param name="events">The TID's syscall enter/exit events, in any order.</param>
    /// <param name="captureEndTs">
    /// End timestamp used to close a trailing unmatched <c>sys_enter</c> (no observed
    /// <c>sys_exit</c> before the syscall trace ended). Callers that cannot cheaply establish a
    /// tight, reliably-later-than-every-lookup capture end (see <see cref="PerfSchedOffCpuSampler"/>'s
    /// caller for why using the max observed *syscall* timestamp is unsafe) should pass
    /// <see cref="double.PositiveInfinity"/> here — the resulting open interval never produces a
    /// false correlation because every real lookup timestamp is, by construction, strictly less
    /// than "the rest of time".
    /// </param>
    public void AddThreadEvents(int tid, IEnumerable<PerfSyscallScriptParser.SyscallEvent> events, double captureEndTs)
    {
        var sorted = events.OrderBy(e => e.TimestampSeconds).ToList();
        if (sorted.Count == 0) return;

        List<(double, double, long)>? list = null;
        double? pendingEnterTs = null;
        long pendingId = 0;

        foreach (var ev in sorted)
        {
            if (ev.IsEnter)
            {
                // A second enter without an intervening exit (should not happen per-thread, but
                // tolerate malformed/truncated traces): the earlier enter had no observed exit
                // before this next enter, so close it exactly at the new enter's timestamp.
                if (pendingEnterTs.HasValue)
                {
                    TryAdd(ref list, pendingEnterTs.Value, ev.TimestampSeconds, pendingId);
                }
                pendingEnterTs = ev.TimestampSeconds;
                pendingId = ev.SyscallId;
            }
            else if (pendingEnterTs.HasValue)
            {
                TryAdd(ref list, pendingEnterTs.Value, ev.TimestampSeconds, pendingId);
                pendingEnterTs = null;
            }
            // A lone sys_exit with no pending enter (missed the enter, e.g. capture started
            // mid-syscall) carries no reliable start time — skip it rather than guess.
        }

        if (pendingEnterTs.HasValue)
        {
            // Still inside the syscall when the capture window ended — open-ended interval,
            // mirroring the sched_switch sampler's own IsCensored span handling.
            TryAdd(ref list, pendingEnterTs.Value, captureEndTs, pendingId);
        }

        if (list is { Count: > 0 })
        {
            _byTid[tid] = list;
        }
    }

    private void TryAdd(ref List<(double, double, long)>? list, double start, double end, long id)
    {
        if (TotalIntervals >= MaxIntervals)
        {
            _dropped++;
            return;
        }
        list ??= [];
        list.Add((start, end, id));
        TotalIntervals++;
    }

    /// <summary>Returns the syscall id in flight for <paramref name="tid"/> at <paramref name="timestampSeconds"/>, or <c>null</c> if none / unknown / capped.</summary>
    public long? Lookup(int tid, double timestampSeconds)
    {
        if (!_byTid.TryGetValue(tid, out var intervals)) return null;

        var lo = 0;
        var hi = intervals.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var (start, end, id) = intervals[mid];
            if (timestampSeconds < start) hi = mid - 1;
            else if (timestampSeconds > end) lo = mid + 1;
            else return id;
        }
        return null;
    }

    /// <summary>Builds the index from a flat event stream, grouping by TID internally.</summary>
    public static SyscallIntervalIndex Build(IReadOnlyList<PerfSyscallScriptParser.SyscallEvent> events, double captureEndTs)
    {
        var index = new SyscallIntervalIndex();
        foreach (var group in events.GroupBy(e => e.Tid))
        {
            index.AddThreadEvents(group.Key, group, captureEndTs);
        }
        return index;
    }
}
