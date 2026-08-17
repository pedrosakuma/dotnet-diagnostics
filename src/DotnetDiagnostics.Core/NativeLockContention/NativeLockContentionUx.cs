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

            foreach (var frame in stack.Stack)
            {
                if (MatchesAny(frame.Method, NativeSyncFrameMarkers) || MatchesAny(frame.Module, NativeSyncFrameMarkers))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static NextActionHint? BuildOffCpuFollowUpHint(OffCpuSnapshot snapshot, ProcessContext? context, string handleId, int processId, int durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(handleId);

        if (!HasNativeSynchronizationEvidence(snapshot))
        {
            return null;
        }

        if (context?.CanSampleNativeLockContention == true)
        {
            return new NextActionHint(
                "collect_sample",
                "Off-CPU evidence includes native synchronization waits (for example futex/mutex); run the Linux native-lock-contention sampler to attribute mutex-call sites, then corroborate because it counts calls rather than waits.",
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
                "Off-CPU evidence includes native synchronization waits, but native-lock-contention has no Windows backend in this release; inspect the off-CPU stack evidence instead.",
                new Dictionary<string, object?> { ["handle"] = handleId, ["view"] = "topStacks" });
        }

        if (OperatingSystem.IsLinux())
        {
            return new NextActionHint(
                "inspect_process",
                "Off-CPU evidence includes native synchronization waits, but native-lock-contention is unavailable on this Linux host; check capabilities and ensure linux-perf plus CAP_SYS_ADMIN/tracefs write access before retrying.",
                new Dictionary<string, object?> { ["processId"] = processId, ["view"] = "capabilities" });
        }

        return new NextActionHint(
            "query_snapshot",
            "Off-CPU evidence includes native synchronization waits, but native-lock-contention is Linux-only in this release; inspect the off-CPU stack evidence instead.",
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

        if (callerSelection.Top is null)
        {
            return $"Probed {string.Join("/", sample.ProbedFunctions)} in {sample.LibcPath} but captured no native " +
                   $"mutex-call samples in {durationSeconds}s — the workload may not use native pthread mutexes, or samplePeriod " +
                   "is too high. Drive the suspect load during the window or lower samplePeriod.";
        }

        var baseSummary = $"Captured {sample.TotalSampledLockCalls} sampled native mutex-call(s) over {durationSeconds}s " +
                          $"(probed {string.Join("/", sample.ProbedFunctions)} in {sample.LibcPath}, samplePeriod={sample.SamplePeriod}). ";
        const string Caveat = "Counts are calls, not confirmed blocking waits.";
        if (callerSelection.Useful is { } useful)
        {
            var displacement = callerSelection.UsefulWasDisplaced
                ? $" Top sampled frame was {callerSelection.Top.Frame.Method} ({callerSelection.Top.InclusiveSamples} inclusive hits), but that looks unresolved/plumbing."
                : string.Empty;
            return baseSummary +
                   $"First useful caller: {useful.Frame.Method} ({useful.InclusiveSamples} inclusive hits).{displacement} " +
                   Caveat;
        }

        return baseSummary +
               $"Top sampled frame: {callerSelection.Top.Frame.Method} ({callerSelection.Top.InclusiveSamples} inclusive hits), but no clearer application/native caller surfaced inline. " +
               $"{Caveat} Use query_snapshot(handle=\"{handleId}\", view=\"call-tree\") before attributing this to application code.";
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
}

internal sealed record NativeLockCallerSelection(Hotspot? Top, Hotspot? Useful, bool UsefulWasDisplaced);
