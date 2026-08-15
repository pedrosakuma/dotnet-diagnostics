namespace DotnetDiagnostics.Core.CpuEfficiency;

/// <summary>
/// Shared metric-name → <see cref="CpuEfficiencySample"/> projection used by both platform
/// backends, so ratio computation (IPC, miss rates) and null-propagation semantics stay identical
/// regardless of which collector produced the raw counters.
/// </summary>
internal static class CpuEfficiencyAggregator
{
    public static CpuEfficiencySample Build(
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        string backend,
        IReadOnlyDictionary<string, long> values,
        IReadOnlyList<string> notes)
    {
        long? Get(string key) => values.TryGetValue(key, out var v) ? v : null;

        var instructions = Get("instructions");
        var cycles = Get("cycles");
        var cacheRefs = Get("cache-references");
        var cacheMisses = Get("cache-misses");
        var branchInstr = Get("branch-instructions");
        var branchMisses = Get("branch-misses");
        var stalledFrontend = Get("stalled-cycles-frontend");
        var stalledBackend = Get("stalled-cycles-backend");
        var dTlbMisses = Get("dTLB-load-misses");
        var iTlbMisses = Get("iTLB-load-misses");
        var pageFaults = Get("page-faults");
        var contextSwitches = Get("context-switches");
        var cpuMigrations = Get("cpu-migrations");

        long? tlbMissesTotal = dTlbMisses is null && iTlbMisses is null
            ? null
            : (dTlbMisses ?? 0) + (iTlbMisses ?? 0);

        return new CpuEfficiencySample(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            Backend: backend,
            Instructions: instructions,
            Cycles: cycles,
            InstructionsPerCycle: Ratio(instructions, cycles),
            CacheReferences: cacheRefs,
            CacheMisses: cacheMisses,
            CacheMissRate: Ratio(cacheMisses, cacheRefs),
            BranchInstructions: branchInstr,
            BranchMisses: branchMisses,
            BranchMissRate: Ratio(branchMisses, branchInstr),
            StalledCyclesFrontend: stalledFrontend,
            StalledCyclesFrontendRate: Ratio(stalledFrontend, cycles),
            StalledCyclesBackend: stalledBackend,
            StalledCyclesBackendRate: Ratio(stalledBackend, cycles),
            DTlbMisses: dTlbMisses,
            ITlbMisses: iTlbMisses,
            TlbMissRate: Ratio(tlbMissesTotal, instructions),
            PageFaults: pageFaults,
            ContextSwitches: contextSwitches,
            CpuMigrations: cpuMigrations,
            Notes: notes.Count > 0 ? notes : null);
    }

    /// <summary>Numerator/denominator as a double ratio, or null when either side is unavailable
    /// or the denominator is zero (avoids a spurious Infinity/NaN in the JSON payload).</summary>
    private static double? Ratio(long? numerator, long? denominator)
    {
        if (numerator is null || denominator is null || denominator == 0)
        {
            return null;
        }

        return (double)numerator.Value / denominator.Value;
    }
}
