using System.ComponentModel;
using System.Globalization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Container;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.Preflight;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.Triage;
using DotnetDiagnostics.Core.UseCases;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Mcp.Tools;

internal static class DiagnosticToolProcessInspection
{
    private static readonly string[] HostingCounterProviders = ["Microsoft.AspNetCore.Hosting"];

    public static DiagnosticResult<IReadOnlyList<DotnetProcess>> ListDotnetProcesses(IProcessDiscovery discovery)
        => ProcessInspectionUseCases.ListProcesses(discovery);

    public static Task<DiagnosticResult<DotnetProcess>> GetProcessInfo(
        IProcessDiscovery discovery,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        CancellationToken cancellationToken = default)
        => ProcessInspectionUseCases.GetProcessInfoAsync(discovery, resolver, processId, cancellationToken);

    public static Task<DiagnosticResult<DiagnosticCapabilities>> GetDiagnosticCapabilities(
        ICapabilityDetector detector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        CancellationToken cancellationToken = default)
        => ProcessInspectionUseCases.GetCapabilitiesAsync(detector, resolver, processId, cancellationToken);

    public static DiagnosticResult<PreflightReport> PerformPreflight(
        IPreflightInspector inspector,
        int? processId = null)
        => ProcessInspectionUseCases.Preflight(inspector, processId);

    public static async Task<DiagnosticResult<ContainerSignals>> GetContainerSignals(
        IContainerSignalsCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops the verbose Notes (caveats about cgroup v1, missing PSI, etc.) and keeps only the actionable signals. 'detail' / 'raw' include all Notes.")]
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

    public static async Task<DiagnosticResult<MemoryTrend>> GetMemoryTrend(
        IMemoryTrendCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target process. When provided, any OS process is accepted (no .NET IPC required). Optional — omit to auto-select the lone reachable .NET process.")] int? processId = null,
        [Description("Duration of the observation window in seconds. Must be >= 2. Defaults to 10.")] int durationSeconds = 10,
        [Description("Interval between consecutive samples in seconds. Must be >= 1. Defaults to 2.")] int sampleEverySeconds = 2,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 2) return InvalidArg<MemoryTrend>(nameof(durationSeconds), "must be >= 2");
        if (sampleEverySeconds < 1) return InvalidArg<MemoryTrend>(nameof(sampleEverySeconds), "must be >= 1");

        int pid;
        ProcessContext? context = null;

        if (processId is > 0)
        {
            pid = processId.Value;
        }
        else if (processId is < 0)
        {
            return InvalidArg<MemoryTrend>(nameof(processId), "must be a positive process id");
        }
        else
        {
            var resolved = await ResolveContextAsync<MemoryTrend>(resolver, null, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;
            pid = resolved.ProcessId;
            context = resolved.Context;
        }

        var trend = await collector.CollectAsync(pid, durationSeconds, sampleEverySeconds, cancellationToken).ConfigureAwait(false);

        const double bytesPerMiB = 1_048_576.0;
        var rssMiB = trend.Deltas.RssBytesPerSec / bytesPerMiB;
        var summary = trend.Samples.Count < 2
            ? $"Process {pid}: could not collect enough samples — check Notes for details."
            : $"Process {pid} memory over {durationSeconds}s ({trend.Samples.Count} samples): " +
              $"verdict={trend.Verdict}, " +
              $"rss={trend.Samples[^1].RssBytes / bytesPerMiB:F1} MiB, " +
              $"Δrss={rssMiB:+0.00;-0.00;0.00} MiB/s.";

        var hints = trend.Verdict == "growing"
            ? new[]
            {
                new NextActionHint("inspect_heap",
                    $"RSS growing at {rssMiB:F2} MiB/s — inspect the live heap to identify dominant retainers.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["topTypes"] = 25 }),
                new NextActionHint("inspect_process",
                    "Cross-check memory against cgroup limits before concluding it is a leak.",
                    new Dictionary<string, object?> { ["processId"] = pid }),
            }
            : new[]
            {
                new NextActionHint("collect_events",
                    "Memory looks stable — check runtime counters for CPU/GC pressure.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 5 }),
            };

        var ok = DiagnosticResult.Ok(trend, summary, hints);
        return WithContext(ok, context);
    }

    public static async Task<DiagnosticResult<RuntimeConfigView>> GetRuntimeConfig(
        IRuntimeConfigInspector inspector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        if (processId is < 0)
        {
            return InvalidArg<RuntimeConfigView>(nameof(processId), "must be a positive process id");
        }

        var resolved = await ResolveContextAsync<RuntimeConfigView>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return resolved.Failure;
        }

        var runtimeConfig = await inspector.InspectAsync(resolved.ProcessId, cancellationToken).ConfigureAwait(false);
        var summary = SummariseRuntimeConfig(runtimeConfig);
        var hints = BuildRuntimeConfigHints(runtimeConfig);
        return WithContext(DiagnosticResult.Ok(runtimeConfig, summary, hints), resolved.Context);
    }

    public static async Task<DiagnosticResult<TriageResult>> PerformTriage(
        ICounterCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the counter collection window in seconds. Must be >= 1. Defaults to 5.")] int durationSeconds = 5,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1)
        {
            return InvalidArg<TriageResult>(nameof(durationSeconds), "must be >= 1");
        }

        var resolved = await ResolveContextAsync<TriageResult>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return resolved.Failure;
        }

        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(durationSeconds),
            providers: null,
            meters: ["Microsoft.AspNetCore.Hosting"],
            intervalSeconds: 1,
            maxInstrumentTimeSeries: 100,
            cancellationToken).ConfigureAwait(false);

        var requestDuration = HeadlineCounters.FindRequestDuration(snapshot.Meters);
        var requestDurationP95 = requestDuration?.Histogram?.P95;
        var triage = TriageClassifier.Classify(snapshot, requestDurationP95);
        var hints = BuildTriageHints(triage, pid);

        var secondaryText = triage.SecondaryVerdicts?.Count > 0
            ? $" (also: {string.Join(", ", triage.SecondaryVerdicts)})"
            : string.Empty;

        var indicatorsText = triage.TopIndicators?.Count > 0
            ? $" | top: {string.Join(", ", triage.TopIndicators.Take(3).Select(i => $"{i.Name}={i.Value}{i.Unit ?? ""}({i.Level})"))}"
            : string.Empty;

        var summary = $"Triage: {triage.Verdict} ({triage.Severity}){secondaryText}{indicatorsText}";

        var ok = DiagnosticResult.Ok(triage, summary, [.. hints]);
        return WithContext(ok, resolved.Context);
    }

    public static async Task<DiagnosticResult<ProcessResources>> GetProcessResources(
        IProcessResourcesCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target process. When provided, any OS process is accepted (no .NET IPC required). Optional — omit to auto-select the lone reachable .NET process.")] int? processId = null,
        [Description("Observation window length in seconds. 0 returns a single snapshot (default); values >= 2 enable trend mode.")] int durationSeconds = 0,
        [Description("Interval between consecutive samples in seconds when trend mode is enabled. Must be >= 1. Defaults to 2.")] int sampleEverySeconds = 2,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 0 || durationSeconds == 1)
        {
            return InvalidArg<ProcessResources>(nameof(durationSeconds), "must be 0 or >= 2");
        }

        if (sampleEverySeconds < 1)
        {
            return InvalidArg<ProcessResources>(nameof(sampleEverySeconds), "must be >= 1");
        }

        int pid;
        ProcessContext? context = null;

        if (processId is > 0)
        {
            pid = processId.Value;
        }
        else if (processId is < 0)
        {
            return InvalidArg<ProcessResources>(nameof(processId), "must be a positive process id");
        }
        else
        {
            var resolved = await ResolveContextAsync<ProcessResources>(resolver, null, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;
            pid = resolved.ProcessId;
            context = resolved.Context;
        }

        var resources = await collector.CollectAsync(pid, durationSeconds, sampleEverySeconds, cancellationToken).ConfigureAwait(false);
        var summary = SummariseProcessResources(resources, durationSeconds);
        var hints = BuildProcessResourceHints(resources, durationSeconds);
        return WithContext(DiagnosticResult.Ok(resources, summary, hints), context);
    }

    public static async Task<DiagnosticResult<RequestsNowSnapshot>> GetRequestsNow(
        IRequestsNowCollector collector,
        IProcessContextResolver resolver,
        int? processId = null,
        CancellationToken cancellationToken = default)
    {
        const int windowSeconds = 2;
        const int topFrames = 8;

        if (processId is < 0)
        {
            return InvalidArg<RequestsNowSnapshot>(nameof(processId), "must be a positive process id");
        }

        var resolved = await ResolveContextAsync<RequestsNowSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return resolved.Failure;
        }

        var snapshot = await collector
            .CollectAsync(resolved.ProcessId, TimeSpan.FromSeconds(windowSeconds), topFrames, cancellationToken)
            .ConfigureAwait(false);
        var summary = SummariseRequestsNow(snapshot, windowSeconds);
        var hints = BuildRequestsNowHints(snapshot);
        return WithContext(DiagnosticResult.Ok(snapshot, summary, hints), resolved.Context);
    }

    private static string SummariseContainerSignals(ContainerSignals s)
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

    private static NextActionHint[] BuildContainerHints(ContainerSignals s)
    {
        if (s.Cpu is { ThrottlePercent: > 5 } cpu)
        {
            return
            [
                new NextActionHint("collect_sample",
                    $"CPU throttling > 5% ({cpu.ThrottlePercent:F1}% of periods). Sample on-CPU stacks to see which code is hitting the quota.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 10 }),
            ];
        }
        if (s.Memory is { UsageFraction: > 0.85 } mem)
        {
            return
            [
                new NextActionHint("inspect_heap",
                    $"Memory at {(mem.UsageFraction ?? 0) * 100:F0}% of limit. Inspect the live heap to identify the dominant retainers before the cgroup OOM-kills.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["topTypes"] = 25 }),
            ];
        }
        if (!s.InContainer)
        {
            return
            [
                new NextActionHint("collect_events",
                    "Not in a container envelope — runtime EventCounters remain the cheapest first signal.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 5 }),
            ];
        }
        return
        [
            new NextActionHint("collect_events",
                "No kernel-level pressure detected. Move up the stack to runtime counters.",
                new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 5 }),
        ];
    }

    private static string SummariseRuntimeConfig(RuntimeConfigView runtimeConfig)
    {
        var parts = new List<string>();
        if (runtimeConfig.Gc is { } gc)
        {
            parts.Add($"GC server={gc.IsServerGc}, concurrent={gc.IsConcurrent?.ToString() ?? "unknown"}, background={gc.IsBackground?.ToString() ?? "unknown"}, heaps={gc.HeapCount}");
        }

        if (runtimeConfig.ThreadPool is { } threadPool)
        {
            parts.Add($"ThreadPool worker={FormatNullableInt(threadPool.MinWorkerThreads)}/{FormatNullableInt(threadPool.MaxWorkerThreads)}, iocp={FormatNullableInt(threadPool.MinIocpThreads)}/{FormatNullableInt(threadPool.MaxIocpThreads)}");
        }

        if (runtimeConfig.TieredCompilation is { } tiered)
        {
            parts.Add($"tiered={tiered.Enabled?.ToString() ?? "unknown"}, quickjit={tiered.QuickJitEnabled?.ToString() ?? "unknown"}, pgo={tiered.DynamicPgoEnabled?.ToString() ?? "unknown"}");
        }

        parts.Add($"env={runtimeConfig.EnvVars.Count}");
        parts.Add($"appContextSwitches={runtimeConfig.AppContextSwitches.Count}");
        return $"Process {runtimeConfig.ProcessId} runtime-config: {string.Join("; ", parts)}.";
    }

    private static string FormatNullableInt(int? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "?";

    private static NextActionHint[] BuildRuntimeConfigHints(RuntimeConfigView runtimeConfig)
    {
        if (runtimeConfig.ThreadPool is { HillClimbingEnabled: false })
        {
            return
            [
                new NextActionHint(
                    "collect_events",
                    "ThreadPool hill-climbing is not active; capture runtime counters before investigating starvation symptoms further.",
                    new Dictionary<string, object?> { ["kind"] = "threadpool", ["processId"] = runtimeConfig.ProcessId, ["durationSeconds"] = 6 }),
            ];
        }

        return
        [
            new NextActionHint(
                "collect_events",
                "Use runtime counters as the next cheap signal after confirming the startup configuration.",
                new Dictionary<string, object?> { ["kind"] = "counters", ["processId"] = runtimeConfig.ProcessId, ["durationSeconds"] = 5 }),
        ];
    }

    private static List<NextActionHint> BuildTriageHints(TriageResult triage, int pid)
    {
        var hints = new List<NextActionHint>();

        switch (triage.Verdict)
        {
            case TriageClassifier.CpuBound:
                hints.Add(new NextActionHint("collect_sample", $"cpu-usage={triage.Evidence.CpuUsage:F1}% — investigate the hot path.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10, ["topN"] = 25 }));
                break;

            case TriageClassifier.GcPressure:
                hints.Add(new NextActionHint("collect_events", $"time-in-gc={triage.Evidence.TimeInGc:F1}% — GC pressure detected.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "gc", ["durationSeconds"] = 10 }));
                hints.Add(new NextActionHint("inspect_heap", "GC pressure — inspect heap for allocation patterns.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["source"] = "live" }));
                break;

            case TriageClassifier.MemoryPressure:
                if ((triage.Evidence.AllocRate ?? 0) >= 50_000_000)
                {
                    hints.Add(new NextActionHint("collect_sample", $"alloc-rate={triage.Evidence.AllocRate / 1_000_000:F0} MB/s — profile the allocation hotspot.",
                        new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "allocation", ["durationSeconds"] = 10 }));
                }

                hints.Add(new NextActionHint("inspect_heap", $"Memory pressure (gen-2 GC={triage.Evidence.Gen2GcCount:F0}) — inspect the live heap for the dominant types and their retention paths.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["source"] = "live" }));
                hints.Add(new NextActionHint("inspect_process", "Confirm whether the working set is trending up over time.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["view"] = "memory_trend" }));
                break;

            case TriageClassifier.ThreadPoolStarvation:
                hints.Add(new NextActionHint("collect_events", $"threadpool-queue-length={triage.Evidence.ThreadPoolQueueLength:F0} — possible ThreadPool starvation.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "threadpool", ["durationSeconds"] = 10 }));
                break;

            case TriageClassifier.LockContention:
                hints.Add(new NextActionHint("collect_events", $"monitor-lock-contention-count={triage.Evidence.MonitorLockContentionCount:F0} — lock contention detected.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "contention", ["durationSeconds"] = 10 }));
                break;

            case TriageClassifier.IoBound:
                hints.Add(new NextActionHint("collect_thread_snapshot", $"cpu-usage={triage.Evidence.CpuUsage:F1}% but queue={triage.Evidence.ThreadPoolQueueLength:F0} — I/O bound likely, inspect blocking stacks.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
                hints.Add(new NextActionHint("collect_events", "Low CPU + queue buildup — trace activities to see what's waiting.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "activities", ["durationSeconds"] = 10 }));
                break;

            default:
                hints.Add(new NextActionHint("collect_events", "System looks healthy — confirm with GC events if response times are high.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "gc", ["durationSeconds"] = 10 }));
                break;
        }

        if (triage.SecondaryVerdicts is not null)
        {
            foreach (var secondary in triage.SecondaryVerdicts)
            {
                hints.Add(new NextActionHint("collect_events", $"Also detected: {secondary} — follow up after primary issue.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = GetKindForVerdict(secondary), ["durationSeconds"] = 10 }));
            }
        }

        return hints;
    }

    private static string GetKindForVerdict(string verdict) => verdict switch
    {
        TriageClassifier.CpuBound => "counters",
        TriageClassifier.GcPressure => "gc",
        TriageClassifier.MemoryPressure => "gc",
        TriageClassifier.ThreadPoolStarvation => "threadpool",
        TriageClassifier.LockContention => "contention",
        TriageClassifier.IoBound => "activities",
        _ => "counters"
    };

    private static string SummariseProcessResources(ProcessResources resources, int durationSeconds)
    {
        var parts = new List<string>();
        if (resources.FdCount is { } fdCount)
        {
            parts.Add($"fd={fdCount}");
        }

        if (resources.HandleCount is { } handleCount)
        {
            parts.Add($"handles={handleCount}");
        }

        if (resources.Sockets is { } sockets)
        {
            parts.Add($"tcp est={sockets.Established}, listen={sockets.Listen}, close_wait={sockets.CloseWait}, time_wait={sockets.TimeWait}");
        }

        if (resources.Limits?.NoFileSoft is { } noFileSoft)
        {
            var limitText = resources.Limits.NoFileUsageFraction is { } usage
                ? $"nofile={resources.FdCount ?? 0}/{noFileSoft} ({usage * 100:F0}%)"
                : $"nofile={noFileSoft}";
            parts.Add(limitText);
        }

        if (resources.ManagedVsNative is { } memory)
        {
            var rss = FormatBytes(memory.RssBytes);
            var gcHeap = FormatBytes(memory.GcHeapBytes);
            parts.Add($"rss={rss}, gc_heap={gcHeap}");
        }

        if (parts.Count == 0)
        {
            return resources.Notes.Count > 0
                ? resources.Notes[0]
                : $"Process {resources.ProcessId}: no resource data collected.";
        }

        var prefix = durationSeconds >= 2 && resources.Trend is { Samples.Count: > 0 }
            ? $"Process {resources.ProcessId} resources over {durationSeconds}s ({resources.Trend.Samples.Count} samples): "
            : $"Process {resources.ProcessId} resource snapshot: ";
        return prefix + string.Join("; ", parts) + ".";
    }

    private static string SummariseRequestsNow(RequestsNowSnapshot snapshot, int windowSeconds)
    {
        var completenessNote = snapshot.Notes.Count > 0
            ? " " + string.Join(" ", snapshot.Notes)
            : string.Empty;

        if (snapshot.Requests.Count == 0)
        {
            return snapshot.Notes.Count == 0
                ? $"Process {snapshot.ProcessId}: no in-flight ASP.NET Core requests observed during the last {windowSeconds}s."
                : $"Process {snapshot.ProcessId}: no in-flight ASP.NET Core request rows were captured during the last {windowSeconds}s.{completenessNote}";
        }

        var preview = string.Join(", ", snapshot.Requests.Take(3).Select(request =>
            $"{request.Method} {request.Endpoint} ({request.StartedAtMs:F0} ms, tid {request.ThreadId})"));
        return $"Process {snapshot.ProcessId}: {snapshot.Requests.Count} in-flight ASP.NET Core request(s) observed during the last {windowSeconds}s: {preview}{(snapshot.Requests.Count > 3 ? ", …" : string.Empty)}.{completenessNote}";
    }

    private static NextActionHint[] BuildRequestsNowHints(RequestsNowSnapshot snapshot)
    {
        if (snapshot.Requests.Count == 0)
        {
            return
            [
                new NextActionHint(
                    "collect_events",
                    snapshot.Notes.Count == 0
                        ? "No hanging requests were active in this 2s window — re-run during the incident or cross-check Hosting counters."
                        : "The requests-now snapshot queue overflowed, so zero captured rows does not prove that no requests were active — re-run during the incident or cross-check Hosting counters.",
                    new Dictionary<string, object?>
                    {
                        ["kind"] = "counters",
                        ["processId"] = snapshot.ProcessId,
                        ["durationSeconds"] = 5,
                        ["providers"] = HostingCounterProviders,
                    }),
            ];
        }

        var oldest = snapshot.Requests.MaxBy(request => request.StartedAtMs)!;
        return
        [
            new NextActionHint(
                "collect_thread_snapshot",
                $"Oldest in-flight request is {oldest.Method} {oldest.Endpoint} ({oldest.StartedAtMs:F0} ms). Capture a full thread snapshot if you need every thread and lock, not just the matched request thread.",
                new Dictionary<string, object?>
                {
                    ["processId"] = snapshot.ProcessId,
                    ["maxFramesPerThread"] = 64,
                }),
            new NextActionHint(
                "collect_events",
                "Correlate the hang with Hosting counters (current-requests / failed-requests) over a longer window.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "counters",
                    ["processId"] = snapshot.ProcessId,
                    ["durationSeconds"] = 5,
                    ["providers"] = HostingCounterProviders,
                }),
        ];
    }

    private static NextActionHint[] BuildProcessResourceHints(ProcessResources resources, int durationSeconds)
    {
        var hints = new List<NextActionHint>();
        var closeWaitGrowing = resources.Trend is { Samples.Count: > 1 } trend &&
                               GetSocketMetric(trend.Samples[^1].Sockets, static sockets => sockets.CloseWait) >
                               GetSocketMetric(trend.Samples[0].Sockets, static sockets => sockets.CloseWait);
        var fdFlat = resources.Trend is { Samples.Count: > 1 } trendSamples &&
                     Math.Abs((trendSamples.Samples[^1].FdCount ?? 0) - (trendSamples.Samples[0].FdCount ?? 0)) <= 5;

        if (resources.Sockets?.CloseWait is > 100 && (durationSeconds == 0 || closeWaitGrowing))
        {
            hints.Add(new NextActionHint(
                "collect_events",
                "Likely undisposed HttpResponseMessage / HttpClient — collect System.Net.Http EventSource events to confirm.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "event_source",
                    ["processId"] = resources.ProcessId,
                    ["providerName"] = "System.Net.Http",
                    ["durationSeconds"] = 10,
                }));
        }

        if (resources.Limits?.NoFileUsageFraction is > 0.85)
        {
            hints.Add(new NextActionHint(
                "collect_process_dump",
                "Approaching the FD ceiling — consider collect_process_dump before the crash to capture state.",
                new Dictionary<string, object?>
                {
                    ["processId"] = resources.ProcessId,
                    ["dumpType"] = "Mini",
                }));
        }

        if (resources.Sockets?.TimeWait is > 100 && fdFlat)
        {
            hints.Add(new NextActionHint(
                "collect_events",
                "Connection churn pattern; check connection pooling config and confirm with System.Net.Http EventSource activity.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "event_source",
                    ["processId"] = resources.ProcessId,
                    ["providerName"] = "System.Net.Http",
                    ["durationSeconds"] = 10,
                }));
        }

        if (resources.ManagedVsNative?.RssDominated == true)
        {
            hints.Add(new NextActionHint(
                "inspect_heap",
                "RSS is much larger than the managed GC heap. Capture a heap snapshot to check pinned LOH/POH and managed roots; if flat, pivot to native allocation or fragmentation investigation.",
                new Dictionary<string, object?>
                {
                    ["source"] = "live",
                    ["processId"] = resources.ProcessId,
                }));
        }

        if (hints.Count == 0)
        {
            hints.Add(new NextActionHint(
                "collect_events",
                "Resource usage looks unremarkable — move up the stack to runtime counters.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "counters",
                    ["processId"] = resources.ProcessId,
                    ["durationSeconds"] = 5,
                }));
        }

        return hints.ToArray();
    }

    private static int GetSocketMetric(SocketBreakdown? sockets, Func<SocketBreakdown, int> selector)
        => sockets is null ? 0 : selector(sockets);

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null)
        {
            return "?";
        }

        return bytes.Value >= 1024 * 1024
            ? $"{bytes.Value / (1024.0 * 1024.0):F1} MiB"
            : $"{bytes.Value:N0} B";
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
