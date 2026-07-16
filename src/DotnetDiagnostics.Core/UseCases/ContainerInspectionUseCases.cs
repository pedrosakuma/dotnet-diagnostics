using DotnetDiagnostics.Core.Container;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.ProcessDiscovery;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Host-neutral container/cgroup inspection use cases. Reads kernel-side cgroup v2 signals for a
/// target process, curates the summary text and next-action hints, and remains transport-agnostic
/// so both CLI and MCP front-ends can reuse the same logic.
/// </summary>
public static class ContainerInspectionUseCases
{
    public static async Task<DiagnosticResult<ContainerSignals>> GetContainerSignals(
        IContainerSignalsCollector collector,
        IProcessContextResolver resolver,
        int? processId = null,
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveContextAsync<ContainerSignals>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;

        var signals = await collector.CollectAsync(resolved.ProcessId, cancellationToken).ConfigureAwait(false);

        var hints = BuildContainerHints(signals);
        var summary = SummariseContainerSignals(signals);

        var inlinePayload = signals;
        if (depth == SamplingDepth.Summary && signals.Notes.Count > 0)
        {
            inlinePayload = signals with { Notes = Array.Empty<string>() };
        }

        var ok = DiagnosticResult.Ok(inlinePayload, summary, hints);
        return WithContext(ok, resolved.Context);
    }

    public static string SummariseContainerSignals(ContainerSignals s)
    {
        if (!s.InContainer && s.CgroupVersion != CgroupVersion.V2)
        {
            return s.Notes.Count > 0 ? s.Notes[0] : "No container envelope detected.";
        }

        var parts = new List<string>();
        if (s.Cpu is { } cpu)
        {
            if (cpu.QuotaCores is { } q) parts.Add($"quota={q:F2} cores");
            if (cpu.ThrottlePercent is { } tp) parts.Add($"throttled {tp:F1}% of periods ({cpu.NrThrottled}/{cpu.NrPeriods})");
        }
        if (s.Memory is { } mem)
        {
            if (mem.MaxBytes is { } max) parts.Add($"mem {mem.CurrentBytes / 1_048_576}/{max / 1_048_576} MiB ({(mem.UsageFraction ?? 0) * 100:F0}%)");
            else parts.Add($"mem {mem.CurrentBytes / 1_048_576} MiB (no limit)");
            if (mem.OomKillCount > 0) parts.Add($"OOM kills: {mem.OomKillCount}");
        }
        if (s.Pressure?.CpuSomeAvg10 is { } psiCpu && psiCpu > 0) parts.Add($"PSI cpu.some.avg10={psiCpu:F2}");
        if (s.Pressure?.MemFullAvg10 is { } psiMem && psiMem > 0) parts.Add($"PSI mem.full.avg10={psiMem:F2}");

        var prefix = s.InContainer ? $"Container ({s.CgroupPath ?? "/"}): " : "Host cgroup root: ";
        return parts.Count == 0
            ? prefix + "no actionable signals."
            : prefix + string.Join("; ", parts) + ".";
    }

    public static NextActionHint[] BuildContainerHints(ContainerSignals s)
    {
        if (s.Cpu is { ThrottlePercent: > 5 } cpu)
        {
            return
            [
                new NextActionHint("collect_sample",
                    $"CPU throttling > 5% ({cpu.ThrottlePercent:F1}% of periods). Sample on-CPU stacks to see which code is hitting the quota.",
                    new Dictionary<string, object?>
                    {
                        ["kind"] = "cpu",
                        ["processId"] = s.ProcessId,
                        ["durationSeconds"] = 10,
                    }),
            ];
        }
        if (s.Memory is { UsageFraction: > 0.85 } mem)
        {
            return
            [
                new NextActionHint("inspect_heap",
                    $"Memory at {(mem.UsageFraction ?? 0) * 100:F0}% of limit. Inspect the live heap to identify the dominant retainers before the cgroup OOM-kills.",
                    new Dictionary<string, object?>
                    {
                        ["source"] = "live",
                        ["processId"] = s.ProcessId,
                        ["topTypes"] = 25,
                    }),
            ];
        }
        if (!s.InContainer)
        {
            return
            [
                new NextActionHint("collect_events",
                    "Not in a container envelope — runtime EventCounters remain the cheapest first signal.",
                    new Dictionary<string, object?>
                    {
                        ["kind"] = "counters",
                        ["processId"] = s.ProcessId,
                        ["durationSeconds"] = 5,
                    }),
            ];
        }

        return
        [
            new NextActionHint("collect_events",
                "No kernel-level pressure detected. Move up the stack to runtime counters.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "counters",
                    ["processId"] = s.ProcessId,
                    ["durationSeconds"] = 5,
                }),
        ];
    }
}
