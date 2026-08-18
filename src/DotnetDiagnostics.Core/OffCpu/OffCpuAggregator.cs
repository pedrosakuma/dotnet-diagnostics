using DotnetDiagnostics.Core.NativeLockContention;

namespace DotnetDiagnostics.Core.OffCpu;

/// <summary>
/// Shared aggregation pipeline for off-CPU spans coming from any backend
/// (<see cref="PerfSchedOffCpuSampler"/> on Linux, <see cref="EtwOffCpuSampler"/> on Windows).
/// Keeping the per-stack / per-thread rollup, censored-span accounting and Notes wiring in one
/// place ensures the <c>OffCpuSnapshotArtifact</c> shape returned to the LLM (and queried via
/// <c>query_snapshot</c>) is platform-agnostic — only the raw spans and the
/// <c>SymbolSource</c> tag change between backends.
/// </summary>
internal static class OffCpuAggregator
{
    /// <summary>
    /// Aggregates a flat sequence of <see cref="OffCpuSpan"/> records into the lightweight
    /// summary plus the drill-down artifact. Identical aggregation rules across backends so the
    /// LLM never has to special-case Linux vs Windows results.
    /// </summary>
    /// <param name="processId">Target pid.</param>
    /// <param name="startedAt">Wall-clock start of the sampling window.</param>
    /// <param name="duration">Configured (not measured) window length.</param>
    /// <param name="spans">Raw closed off-CPU spans (and optionally censored ones with <see cref="OffCpuSpan.IsCensored"/>=true).</param>
    /// <param name="schedSwitches">Total context switches attributed to the target (capture-density signal).</param>
    /// <param name="topN">Max blocking stacks returned in the summary; the artifact retains all.</param>
    /// <param name="symbolSource">Backend tag — "perf-sched-dwarf" or "etw-cswitch-pdb".</param>
    /// <param name="notes">Best-effort warnings collected by the backend (size caps, late-attribution, etc.).</param>
    public static OffCpuSampleResult Aggregate(
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        IReadOnlyList<OffCpuSpan> spans,
        long schedSwitches,
        int topN,
        string symbolSource,
        IReadOnlyList<string>? notes = null)
    {
        var builder = new OffCpuAggregationBuilder();
        foreach (var span in spans)
        {
            builder.AddSpan(span);
        }

        return builder.Build(processId, startedAt, duration, schedSwitches, topN, symbolSource, notes);
    }

    public static OffCpuAggregationBuilder CreateBuilder() => new();
}

internal sealed class OffCpuAggregationBuilder
{
    // Per-stack syscall breakdown is a label, not a full latency histogram (explicitly out of
    // scope for issue #829) — cap the number of distinct syscalls reported per stack group.
    private const int MaxSyscallsPerStack = 8;

    private readonly Dictionary<string, StackAggregate> _byStack
        = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (string Comm, long Micros, long Switches, Dictionary<string, long> LeafCounts)> _byThread = [];
    private long _totalMicros;
    private long _censoredCount;
    private long _censoredMicros;
    private readonly NativeContentionEvidenceAccumulator _nativeContention = new();

    public void AddSpan(OffCpuSpan span)
    {
        _totalMicros += span.DurationMicros;
        if (span.IsCensored)
        {
            _censoredCount++;
            _censoredMicros += span.DurationMicros;
        }

        var frames = new List<OffCpuFrame>(span.BlockingStack.Count);
        for (var i = span.BlockingStack.Count - 1; i >= 0; i--)
        {
            frames.Add(span.BlockingStack[i]);
        }

        var leaf = frames.Count > 0 ? frames[^1] : new OffCpuFrame(string.Empty, "[no-stack]");
        var key = string.Join('|', frames.Select(f => string.IsNullOrEmpty(f.Module) ? f.Method : $"{f.Module}!{f.Method}"));

        if (!_byStack.TryGetValue(key, out var agg))
        {
            agg = new StackAggregate(frames);
        }

        agg.Micros += span.DurationMicros;
        agg.Count += 1;
        agg.States[span.PrevState] = agg.States.GetValueOrDefault(span.PrevState) + 1;
        if (!string.IsNullOrEmpty(span.Syscall))
        {
            var (prevCount, prevMicros) = agg.Syscalls.TryGetValue(span.Syscall, out var existing) ? existing : (0L, 0L);
            agg.Syscalls[span.Syscall] = (prevCount + 1, prevMicros + span.DurationMicros);
        }
        var nativeContentionClassification = NativeLockContentionUx.ClassifyOffCpuSpan(span);
        agg.NativeContention.Add(span, nativeContentionClassification);
        _nativeContention.Add(span, nativeContentionClassification);
        _byStack[key] = agg;

        if (!_byThread.TryGetValue(span.Tid, out var threadAgg))
        {
            threadAgg = (span.Comm, 0, 0, new Dictionary<string, long>(StringComparer.Ordinal));
        }

        threadAgg.Micros += span.DurationMicros;
        threadAgg.Switches += 1;
        var leafKey = string.IsNullOrEmpty(leaf.Module) ? leaf.Method : $"{leaf.Module}!{leaf.Method}";
        threadAgg.LeafCounts[leafKey] = threadAgg.LeafCounts.GetValueOrDefault(leafKey) + 1;
        _byThread[span.Tid] = threadAgg;
    }

    public OffCpuSampleResult Build(
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        long schedSwitches,
        int topN,
        string symbolSource,
        IReadOnlyList<string>? notes = null)
    {
        var notesList = notes is { Count: > 0 } ? notes : null;
        if (_censoredCount > 0)
        {
            var merged = new List<string>(notesList ?? Array.Empty<string>());
            merged.Add($"{_censoredCount} span(s) ({_censoredMicros} µs) were censored: the thread was still blocked when the capture window ended, so the duration is a lower bound.");
            notesList = merged;
        }

        var hasEvidenceDegradation = NativeLockContentionUx.HasBlockingEvidenceDegradation(notesList);
        var stacks = _byStack
            .Select(kv =>
            {
                var dominant = kv.Value.States.OrderByDescending(s => s.Value).FirstOrDefault().Key ?? "?";
                var leaf = kv.Value.Frames.Count > 0 ? kv.Value.Frames[^1] : new OffCpuFrame(string.Empty, "[no-stack]");
                IReadOnlyList<OffCpuSyscallAttribution>? syscallBreakdown = kv.Value.Syscalls.Count > 0
                    ? kv.Value.Syscalls
                        .Select(s => new OffCpuSyscallAttribution(s.Key, s.Value.Count, s.Value.Micros))
                        .OrderByDescending(s => s.Micros)
                        // Bounded per stack group regardless of how many distinct syscalls were
                        // observed — keeps the enrichment a label, not a full histogram (out of
                        // scope per issue #829).
                        .Take(MaxSyscallsPerStack)
                        .ToList()
                    : null;
                var evidence = NativeLockContentionUx.BuildOffCpuEvidence(
                    kv.Value.NativeContention.ToStatistics(),
                    notesList,
                    hasEvidenceDegradation);
                return new OffCpuStackHotspot(
                    LeafFrame: string.IsNullOrEmpty(leaf.Module) ? leaf.Method : $"{leaf.Module}!{leaf.Method}",
                    OffCpuMicros: kv.Value.Micros,
                    OccurrenceCount: kv.Value.Count,
                    DominantState: dominant,
                    Stack: kv.Value.Frames,
                    SyscallBreakdown: syscallBreakdown,
                    NativeContentionEvidence: evidence);
            })
            .OrderByDescending(s => s.OffCpuMicros)
            .ToList();

        var threads = _byThread
            .Select(kv =>
            {
                var topLeaf = kv.Value.LeafCounts.OrderByDescending(p => p.Value).FirstOrDefault().Key ?? "[no-stack]";
                return new OffCpuThreadView(
                    Tid: kv.Key,
                    ThreadName: kv.Value.Comm,
                    OffCpuMicros: kv.Value.Micros,
                    SwitchCount: kv.Value.Switches,
                    TopBlockingLeaf: topLeaf);
            })
            .OrderByDescending(t => t.OffCpuMicros)
            .ToList();

        var aggregateNativeContentionEvidence = NativeLockContentionUx.BuildOffCpuEvidence(
            _nativeContention.ToStatistics(),
            notesList,
            hasEvidenceDegradation);

        var summary = new OffCpuSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalOffCpuMicros: _totalMicros,
            DistinctThreads: _byThread.Count,
            TopBlockingStacks: stacks.Take(topN).ToList(),
            SchedSwitches: schedSwitches,
            SymbolSource: symbolSource,
            CensoredSpans: _censoredCount,
            CensoredOffCpuMicros: _censoredMicros,
            Notes: notesList,
            NativeContentionEvidence: aggregateNativeContentionEvidence);

        var artifact = new OffCpuSnapshotArtifact(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalOffCpuMicros: _totalMicros,
            SchedSwitches: schedSwitches,
            Stacks: stacks,
            Threads: threads,
            SymbolSource: symbolSource,
            CensoredSpans: _censoredCount,
            CensoredOffCpuMicros: _censoredMicros,
            Notes: notesList,
            NativeContentionEvidence: aggregateNativeContentionEvidence);

        return new OffCpuSampleResult(summary, artifact);
    }

    private sealed class StackAggregate(IReadOnlyList<OffCpuFrame> frames)
    {
        public long Micros;
        public long Count;
        public Dictionary<string, long> States { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<OffCpuFrame> Frames { get; } = frames;
        public Dictionary<string, (long Count, long Micros)> Syscalls { get; } = new(StringComparer.Ordinal);
        public NativeContentionEvidenceAccumulator NativeContention { get; } = new();
    }

    private sealed class NativeContentionEvidenceAccumulator
    {
        private long _nativeSyncSpanCount;
        private long _closedNativeSyncSpanCount;
        private long _censoredNativeSyncSpanCount;
        private long _nativeSyncMicros;
        private long _closedNativeSyncMicros;
        private long _censoredNativeSyncMicros;
        private long _ambiguousNativeSyncFrameSpanCount;
        private long _ambiguousNativeSyncFrameMicros;
        private bool _hasProbableNonFutexNativeSync;

        public void Add(OffCpuSpan span, NativeContentionSpanClassification classification)
        {
            switch (classification)
            {
                case NativeContentionSpanClassification.ConfirmedFutexBlocking:
                    AddNativeSyncSpan(span);
                    break;
                case NativeContentionSpanClassification.ProbableNativeSync:
                    _hasProbableNonFutexNativeSync = true;
                    AddNativeSyncSpan(span);
                    break;
                case NativeContentionSpanClassification.AmbiguousNativeSyncFrame:
                    _ambiguousNativeSyncFrameSpanCount++;
                    _ambiguousNativeSyncFrameMicros += span.DurationMicros;
                    break;
            }
        }

        public NativeContentionEvidenceStatistics ToStatistics()
            => new(
                _nativeSyncSpanCount,
                _closedNativeSyncSpanCount,
                _censoredNativeSyncSpanCount,
                _nativeSyncMicros,
                _closedNativeSyncMicros,
                _censoredNativeSyncMicros,
                _ambiguousNativeSyncFrameSpanCount,
                _ambiguousNativeSyncFrameMicros,
                _hasProbableNonFutexNativeSync);

        private void AddNativeSyncSpan(OffCpuSpan span)
        {
            _nativeSyncSpanCount++;
            _nativeSyncMicros += span.DurationMicros;
            if (span.IsCensored)
            {
                _censoredNativeSyncSpanCount++;
                _censoredNativeSyncMicros += span.DurationMicros;
            }
            else
            {
                _closedNativeSyncSpanCount++;
                _closedNativeSyncMicros += span.DurationMicros;
            }
        }
    }
}
