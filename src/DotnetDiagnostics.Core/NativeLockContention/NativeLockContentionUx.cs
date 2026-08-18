using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;

namespace DotnetDiagnostics.Core.NativeLockContention;

internal static class NativeLockContentionUx
{
    private static readonly string[] NativeSyncFrameMarkers =
    {
        "pthread_mutex",
        "pthread_cond",
        "futex",
        "lll_lock",
        "criticalsection",
        "srwlock",
        "rtlentercriticalsection",
        "waitonaddress",
    };

    private static readonly string[] PlumbingMethodMarkers =
    {
        "pthread_mutex_lock",
        "pthread_mutex_unlock",
        "__pthread_mutex_lock",
        "__pthread_mutex_unlock",
        "__gi___pthread_mutex_lock",
        "__gi___pthread_mutex_unlock",
        "futex",
        "lll_lock",
        "perf_",
        "perf-",
        "start_thread",
        "__clone",
        "clone3",
        "__libc_start",
        "coreclr",
        "clrjit",
        "libhostfxr",
        "libhostpolicy",
        "system.private.corelib",
        "thread::",
        "threadpool",
    };

    private static readonly string[] PlumbingModuleMarkers =
    {
        "coreclr",
        "clrjit",
        "libhostfxr",
        "libhostpolicy",
        "system.private.corelib",
    };

    public static bool HasNativeSynchronizationEvidence(OffCpuSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.NativeContentionEvidence is { } evidence)
        {
            return IsBlockingEvidence(evidence);
        }

        foreach (var stack in snapshot.TopBlockingStacks)
        {
            if (stack.SyscallBreakdown is not null)
            {
                foreach (var syscall in stack.SyscallBreakdown)
                {
                    if (IsNativeSyncSyscall(syscall.Name))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public static NextActionHint? BuildOffCpuFollowUpHint(OffCpuSnapshot snapshot, ProcessContext? context, string handleId, int processId, int durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(handleId);

        var evidence = snapshot.NativeContentionEvidence ?? BuildEvidenceFromStacks(snapshot.TopBlockingStacks, snapshot.Notes);
        if (!IsBlockingEvidence(evidence))
        {
            return null;
        }

        var level = evidence.Level == NativeContentionEvidenceLevels.ConfirmedBlocking ? "confirmed" : "probable";

        if (context?.CanSampleNativeLockContention == true)
        {
            return new NextActionHint(
                "collect_sample",
                $"Off-CPU evidence includes {level} native synchronization blocking ({evidence.ClosedNativeSyncSpanCount} closed / {evidence.CensoredNativeSyncSpanCount} censored span(s)); run native-lock-contention only to attribute mutex-call activity, not to confirm waits.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "native-lock-contention",
                    ["processId"] = processId,
                    ["durationSeconds"] = durationSeconds,
                })
            { Priority = NextActionHintPriority.High };
        }

        if (OperatingSystem.IsWindows())
        {
            return new NextActionHint(
                "query_snapshot",
                $"Off-CPU evidence includes {level} native synchronization blocking, but native-lock-contention has no Windows backend in this release; inspect the off-CPU stack evidence instead.",
                new Dictionary<string, object?> { ["handle"] = handleId, ["view"] = "topStacks" });
        }

        if (OperatingSystem.IsLinux())
        {
            return new NextActionHint(
                "inspect_process",
                $"Off-CPU evidence includes {level} native synchronization blocking, but native-lock-contention is unavailable on this Linux host; check capabilities and ensure linux-perf plus CAP_SYS_ADMIN/tracefs write access before retrying.",
                new Dictionary<string, object?> { ["processId"] = processId, ["view"] = "capabilities" });
        }

        return new NextActionHint(
            "query_snapshot",
            $"Off-CPU evidence includes {level} native synchronization blocking, but native-lock-contention is Linux-only in this release; inspect the off-CPU stack evidence instead.",
            new Dictionary<string, object?> { ["handle"] = handleId, ["view"] = "topStacks" });
    }

    public static NativeLockCallerSelection SelectInlineCaller(IReadOnlyList<Hotspot> hotspots)
    {
        ArgumentNullException.ThrowIfNull(hotspots);
        var top = hotspots.Count > 0 ? hotspots[0] : null;
        var useful = hotspots.FirstOrDefault(IsUsefulCaller);
        return new NativeLockCallerSelection(top, useful, useful is not null && !ReferenceEquals(useful, top));
    }

    public static string BuildSummary(NativeLockContentionSample sample, int durationSeconds, string handleId, NativeLockCallerSelection callerSelection)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentException.ThrowIfNullOrWhiteSpace(handleId);

        var evidence = sample.ContentionEvidence ?? BuildActivityEvidence(sample);
        if (callerSelection.Top is null)
        {
            return $"Probed {string.Join("/", sample.ProbedFunctions)} in {sample.LibcPath} but captured no native " +
                   $"mutex-call samples in {durationSeconds}s — the workload may not use native pthread mutexes, or samplePeriod " +
                   $"is too high. Drive the suspect load during the window or lower samplePeriod. Evidence level: {evidence.Level}.";
        }

        var baseSummary = $"Captured {sample.TotalSampledLockCalls} sampled native mutex-call(s) over {durationSeconds}s " +
                          $"(probed {string.Join("/", sample.ProbedFunctions)} in {sample.LibcPath}, samplePeriod={sample.SamplePeriod}). ";
        var caveat = $"Evidence level: {evidence.Level} — {evidence.Summary}";
        if (callerSelection.Useful is { } useful)
        {
            var displacement = callerSelection.UsefulWasDisplaced
                ? $" Top sampled frame was {callerSelection.Top.Frame.Method} ({callerSelection.Top.InclusiveSamples} inclusive hits), but that looks unresolved/plumbing."
                : string.Empty;
            return baseSummary +
                   $"First useful caller: {useful.Frame.Method} ({useful.InclusiveSamples} inclusive hits).{displacement} " +
                  caveat;
        }

        return baseSummary +
               $"Top sampled frame: {callerSelection.Top.Frame.Method} ({callerSelection.Top.InclusiveSamples} inclusive hits), but no clearer application/native caller surfaced inline. " +
               $"{caveat} Use query_snapshot(handle=\"{handleId}\", view=\"call-tree\") before attributing this to application code.";
    }

    public static NativeLockContentionSample EnsureActivityEvidence(NativeLockContentionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return sample.ContentionEvidence is null
            ? sample with { ContentionEvidence = BuildActivityEvidence(sample) }
            : sample;
    }

    public static OffCpuSnapshot EnsureOffCpuEvidence(OffCpuSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.NativeContentionEvidence is null
            ? snapshot with { NativeContentionEvidence = BuildEvidenceFromStacks(snapshot.TopBlockingStacks, snapshot.Notes) }
            : snapshot;
    }

    public static NativeContentionEvidence BuildActivityEvidence(NativeLockContentionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return new NativeContentionEvidence(
            Level: NativeContentionEvidenceLevels.Activity,
            Summary: "sampled pthread mutex entry points are lock activity only; this sampler does not measure wait duration or prove blocking.",
            SampledLockCallCount: sample.TotalSampledLockCalls,
            EvidenceSources:
            [
               "perf uprobes on pthread_mutex_lock/pthread_mutex_unlock",
            ],
            ConfidenceRationale:
            [
               "Uprobe samples identify mutex-call sites but cannot distinguish uncontended fast-path calls from calls that blocked in the kernel.",
            ],
            UncertaintyNotes: sample.Notes);
    }

    public static IReadOnlyList<NextActionHint> BuildNativeLockHints(
        NativeLockCallerSelection callerSelection,
        ProcessContext? context,
        string handleId,
        int processId,
        int durationSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handleId);
        var hints = new List<NextActionHint>();
        if (ShouldRecommendCallTree(callerSelection))
        {
            hints.Add(new NextActionHint(
               "query_snapshot",
               "Inline native-lock activity is unresolved or displaced by plumbing; walk the call tree to find the first useful caller.",
               new Dictionary<string, object?> { ["handle"] = handleId, ["view"] = "call-tree", ["maxDepth"] = 8, ["maxNodes"] = 200 })
            { Priority = NextActionHintPriority.High });
        }

        if (context?.CanSampleOffCpu == true)
        {
            hints.Add(new NextActionHint(
               "collect_sample",
               "Corroborate with off-CPU sampling; only closed futex/native-sync off-CPU spans can raise this from lock activity to probable or confirmed blocking.",
               new Dictionary<string, object?> { ["kind"] = "off_cpu", ["processId"] = processId, ["durationSeconds"] = durationSeconds }));
        }
        else
        {
            hints.Add(new NextActionHint(
               "inspect_process",
               "Off-CPU sampling is unavailable, so this result remains activity-only; inspect capabilities before attempting blocking confirmation.",
               new Dictionary<string, object?> { ["processId"] = processId, ["view"] = "capabilities" }));
        }

        return hints;
    }

    public static string FormatOffCpuEvidenceClause(NativeContentionEvidence? evidence)
    {
        if (evidence is null || evidence.Level == NativeContentionEvidenceLevels.None)
        {
            return "Native sync blocking evidence: none confirmed/probable in this window.";
        }

        return $"Native sync blocking evidence: {evidence.Level} ({evidence.ClosedNativeSyncSpanCount} closed / " +
               $"{evidence.CensoredNativeSyncSpanCount} censored span(s), {evidence.ClosedNativeSyncOffCpuMicros / 1000.0:F1} ms closed).";
    }

    internal static NativeContentionSpanClassification ClassifyOffCpuSpan(OffCpuSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        if (IsConfirmableFutexSyscall(span.Syscall))
        {
            return span.IsCensored || !IsBlockedWaitState(span.PrevState)
               ? NativeContentionSpanClassification.ProbableNativeSync
               : NativeContentionSpanClassification.ConfirmedFutexBlocking;
        }

        if (IsNativeSyncSyscall(span.Syscall))
        {
            return NativeContentionSpanClassification.ProbableNativeSync;
        }

        return HasNativeSyncFrameMarker(span.BlockingStack)
            ? NativeContentionSpanClassification.AmbiguousNativeSyncFrame
            : NativeContentionSpanClassification.None;
    }

    internal static NativeContentionEvidence BuildOffCpuEvidence(
        NativeContentionEvidenceStatistics statistics,
        IReadOnlyList<string>? notes,
        bool hasEvidenceDegradation)
    {
        var uncertainty = BuildUncertaintyNotes(statistics, notes, hasEvidenceDegradation);
        var sources = BuildSources(statistics);
        if (statistics.NativeSyncSpanCount == 0)
        {
            var noEvidenceSummary = statistics.AmbiguousNativeSyncFrameSpanCount > 0
               ? "off-CPU stacks contained native synchronization-looking frames, but no futex/native-sync syscall attribution correlated on the same target thread."
               : "no futex/native-sync syscall attribution correlated with target off-CPU spans.";
            return new NativeContentionEvidence(
               Level: NativeContentionEvidenceLevels.None,
               Summary: noEvidenceSummary,
               AmbiguousNativeSyncFrameSpanCount: statistics.AmbiguousNativeSyncFrameSpanCount,
               AmbiguousNativeSyncFrameOffCpuMicros: statistics.AmbiguousNativeSyncFrameOffCpuMicros,
               EvidenceSources: sources,
               ConfidenceRationale:
               [
                   "Frame names alone are not treated as native mutex blocking evidence without syscall/wait correlation.",
               ],
               UncertaintyNotes: uncertainty);
        }

        var canConfirmBlocking =
            statistics.ClosedNativeSyncSpanCount > 0 &&
            statistics.CensoredNativeSyncSpanCount == 0 &&
            statistics.AmbiguousNativeSyncFrameSpanCount == 0 &&
            !statistics.HasProbableNonFutexNativeSync &&
            !hasEvidenceDegradation;
        var level = canConfirmBlocking
            ? NativeContentionEvidenceLevels.ConfirmedBlocking
            : NativeContentionEvidenceLevels.ProbableBlocking;
        var summary = level == NativeContentionEvidenceLevels.ConfirmedBlocking
            ? "closed futex/native-sync off-CPU span(s) on target threads prove blocking occurred during the capture window."
            : "native-sync off-CPU evidence is present, but confirmation is limited by censored/open spans, non-futex wait labels, or capture/correlation degradation.";

        var rationale = new List<string>
        {
            $"{statistics.ClosedNativeSyncSpanCount} closed and {statistics.CensoredNativeSyncSpanCount} censored native-sync off-CPU span(s) were attributed on target threads.",
        };
        if (level == NativeContentionEvidenceLevels.ConfirmedBlocking)
        {
            rationale.Add("Confirmation is based only on closed off-CPU futex/native-sync waits; sampled mutex-call activity is not used as proof.");
        }
        else
        {
            rationale.Add("The result is probable rather than confirmed because at least one required closed futex-span or data-quality condition is missing.");
        }

        return new NativeContentionEvidence(
            Level: level,
            Summary: summary,
            NativeSyncSpanCount: statistics.NativeSyncSpanCount,
            ClosedNativeSyncSpanCount: statistics.ClosedNativeSyncSpanCount,
            CensoredNativeSyncSpanCount: statistics.CensoredNativeSyncSpanCount,
            NativeSyncOffCpuMicros: statistics.NativeSyncOffCpuMicros,
            ClosedNativeSyncOffCpuMicros: statistics.ClosedNativeSyncOffCpuMicros,
            CensoredNativeSyncOffCpuMicros: statistics.CensoredNativeSyncOffCpuMicros,
            AmbiguousNativeSyncFrameSpanCount: statistics.AmbiguousNativeSyncFrameSpanCount,
            AmbiguousNativeSyncFrameOffCpuMicros: statistics.AmbiguousNativeSyncFrameOffCpuMicros,
            EvidenceSources: sources,
            ConfidenceRationale: rationale,
            UncertaintyNotes: uncertainty);
    }

    internal static bool HasBlockingEvidenceDegradation(IReadOnlyList<string>? notes)
    {
        if (notes is null) return false;
        foreach (var note in notes)
        {
            if (note.Contains("cap", StringComparison.OrdinalIgnoreCase) ||
               note.Contains("truncat", StringComparison.OrdinalIgnoreCase) ||
               note.Contains("dropped", StringComparison.OrdinalIgnoreCase) ||
               note.Contains("censor", StringComparison.OrdinalIgnoreCase) ||
               note.Contains("budget", StringComparison.OrdinalIgnoreCase) ||
               note.Contains("ignored", StringComparison.OrdinalIgnoreCase) ||
               note.Contains("stopped early", StringComparison.OrdinalIgnoreCase) ||
               note.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
               return true;
            }
        }
        return false;
    }

    public static bool ShouldRecommendCallTree(NativeLockCallerSelection callerSelection)
        => callerSelection.Top is not null && (callerSelection.Useful is null || callerSelection.UsefulWasDisplaced);

    public static bool IsUsefulCaller(Hotspot hotspot)
    {
        ArgumentNullException.ThrowIfNull(hotspot);
        var method = hotspot.Frame.Method ?? string.Empty;
        var module = hotspot.Frame.Module ?? string.Empty;
        if (string.IsNullOrWhiteSpace(method) ||
            string.Equals(method, "[unknown]", StringComparison.OrdinalIgnoreCase) ||
            method.StartsWith("[unknown]", StringComparison.OrdinalIgnoreCase) ||
            method.StartsWith("0x", StringComparison.Ordinal) ||
            method.StartsWith("[0x", StringComparison.Ordinal))
        {
            return false;
        }

        return !MatchesAny(method, PlumbingMethodMarkers) && !MatchesAny(module, PlumbingModuleMarkers);
    }

    private static bool MatchesAny(string? value, IReadOnlyList<string> markers)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var marker in markers)
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNativeSyncSyscall(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("futex", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("umtx", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("psynch_mutexwait", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Sync", StringComparison.OrdinalIgnoreCase);
    }

    private static NativeContentionEvidence BuildEvidenceFromStacks(
        IReadOnlyList<OffCpuStackHotspot> stacks,
        IReadOnlyList<string>? notes)
    {
        var accumulator = new StackEvidenceAccumulator();
        foreach (var stack in stacks)
        {
            if (stack.NativeContentionEvidence is { } evidence && IsBlockingEvidence(evidence))
            {
               accumulator.NativeSyncSpanCount += evidence.NativeSyncSpanCount;
               accumulator.ClosedNativeSyncSpanCount += evidence.ClosedNativeSyncSpanCount;
               accumulator.CensoredNativeSyncSpanCount += evidence.CensoredNativeSyncSpanCount;
               accumulator.NativeSyncOffCpuMicros += evidence.NativeSyncOffCpuMicros;
               accumulator.ClosedNativeSyncOffCpuMicros += evidence.ClosedNativeSyncOffCpuMicros;
               accumulator.CensoredNativeSyncOffCpuMicros += evidence.CensoredNativeSyncOffCpuMicros;
               continue;
            }

            if (stack.SyscallBreakdown?.Any(s => IsNativeSyncSyscall(s.Name)) == true)
            {
               foreach (var syscall in stack.SyscallBreakdown.Where(s => IsNativeSyncSyscall(s.Name)))
               {
                   accumulator.NativeSyncSpanCount += syscall.Count;
                   accumulator.NativeSyncOffCpuMicros += syscall.Micros;
                   accumulator.HasProbableNonFutexNativeSync = true;
               }
            }
            else if (HasNativeSyncFrameMarker(stack.Stack))
            {
               accumulator.AmbiguousNativeSyncFrameSpanCount += stack.OccurrenceCount;
               accumulator.AmbiguousNativeSyncFrameOffCpuMicros += stack.OffCpuMicros;
            }
        }

        return BuildOffCpuEvidence(accumulator.ToStatistics(), notes, HasBlockingEvidenceDegradation(notes));
    }

    private static bool IsBlockingEvidence(NativeContentionEvidence evidence)
        => evidence.Level is NativeContentionEvidenceLevels.ProbableBlocking or NativeContentionEvidenceLevels.ConfirmedBlocking;

    private static bool IsConfirmableFutexSyscall(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Contains("futex", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockedWaitState(string? prevState)
        => !string.IsNullOrWhiteSpace(prevState) &&
           !prevState.StartsWith("R", StringComparison.OrdinalIgnoreCase);

    private static bool HasNativeSyncFrameMarker(IReadOnlyList<OffCpuFrame> frames)
        => frames.Any(frame => MatchesAny(frame.Method, NativeSyncFrameMarkers) || MatchesAny(frame.Module, NativeSyncFrameMarkers));

    private static List<string>? BuildSources(NativeContentionEvidenceStatistics statistics)
    {
        var sources = new List<string>();
        if (statistics.ClosedNativeSyncSpanCount > 0)
        {
            sources.Add("closed off-CPU futex/native-sync spans");
        }
        if (statistics.CensoredNativeSyncSpanCount > 0)
        {
            sources.Add("censored/open off-CPU native-sync spans");
        }
        if (statistics.AmbiguousNativeSyncFrameSpanCount > 0)
        {
            sources.Add("native-sync-looking stack frames without syscall attribution");
        }
        return sources.Count == 0 ? null : sources;
    }

    private static List<string>? BuildUncertaintyNotes(
        NativeContentionEvidenceStatistics statistics,
        IReadOnlyList<string>? notes,
        bool hasEvidenceDegradation)
    {
        var uncertainty = new List<string>();
        if (statistics.CensoredNativeSyncSpanCount > 0)
        {
            uncertainty.Add($"{statistics.CensoredNativeSyncSpanCount} native-sync span(s) were censored/open, so their durations are lower bounds and are not confirmed blocking.");
        }
        if (statistics.AmbiguousNativeSyncFrameSpanCount > 0)
        {
            uncertainty.Add($"{statistics.AmbiguousNativeSyncFrameSpanCount} span(s) had native synchronization-looking frames without same-thread syscall attribution.");
        }
        if (statistics.HasProbableNonFutexNativeSync)
        {
            uncertainty.Add("Some native-sync evidence was not a closed futex wait span with full raw-span correlation, so it is probable rather than confirmed.");
        }
        if (hasEvidenceDegradation && notes is not null)
        {
            uncertainty.AddRange(notes.Where(IsEvidenceDegradationNote).Take(3));
        }
        return uncertainty.Count == 0 ? null : uncertainty;
    }

    private static bool IsEvidenceDegradationNote(string note)
        => note.Contains("cap", StringComparison.OrdinalIgnoreCase) ||
           note.Contains("truncat", StringComparison.OrdinalIgnoreCase) ||
           note.Contains("dropped", StringComparison.OrdinalIgnoreCase) ||
           note.Contains("censor", StringComparison.OrdinalIgnoreCase) ||
           note.Contains("budget", StringComparison.OrdinalIgnoreCase) ||
           note.Contains("ignored", StringComparison.OrdinalIgnoreCase) ||
           note.Contains("stopped early", StringComparison.OrdinalIgnoreCase) ||
           note.Contains("failed", StringComparison.OrdinalIgnoreCase);

    private sealed class StackEvidenceAccumulator
    {
        public long NativeSyncSpanCount;
        public long ClosedNativeSyncSpanCount;
        public long CensoredNativeSyncSpanCount;
        public long NativeSyncOffCpuMicros;
        public long ClosedNativeSyncOffCpuMicros;
        public long CensoredNativeSyncOffCpuMicros;
        public long AmbiguousNativeSyncFrameSpanCount;
        public long AmbiguousNativeSyncFrameOffCpuMicros;
        public bool HasProbableNonFutexNativeSync;

        public NativeContentionEvidenceStatistics ToStatistics()
            => new(
               NativeSyncSpanCount,
               ClosedNativeSyncSpanCount,
               CensoredNativeSyncSpanCount,
               NativeSyncOffCpuMicros,
               ClosedNativeSyncOffCpuMicros,
               CensoredNativeSyncOffCpuMicros,
               AmbiguousNativeSyncFrameSpanCount,
               AmbiguousNativeSyncFrameOffCpuMicros,
               HasProbableNonFutexNativeSync);
    }
}

internal sealed record NativeLockCallerSelection(Hotspot? Top, Hotspot? Useful, bool UsefulWasDisplaced);
